using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

/// <summary>Mutation, reopen-verification, and negative-change coverage for actor assignment.</summary>
public static class ActorAssignmentTransactionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("actor morph assignment writes and verifies the exact owner", AssignsExactMorphOwner),
        new("actor assignment rejects an outside edit and cleans its candidate", OutsideEditPreserved),
        new("LE1 placed morph assignment leaves the shared spawn type untouched", LeavesSharedLe1TypeUntouched),
        new("actor material assignment converts BMO arrays and protects non-target MICs", ConvertsAndProtectsMaterials),
        new("actor material assignment surgically updates body MIC skin vectors across all games", UpdatesBodySkinVectors),
        new("actor attachments preserve shared Body MICs with and without face overrides", SharedBodyAttachmentMaterial),
        new("actor material assignment adds only skin vectors present on the source", AddsOnlyPresentBodySkinVectors),
        new("actor material assignment preserves target categories missing from the BMO", PreservesMissingMaterialCategories)
    ];

    private static void SharedBodyAttachmentMaterial()
    {
        foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        foreach (var hasOverrides in new[] { false, true })
        WithPackage(game, (path, package) =>
        {
            var (face, _, actor, _, body) = CreateBodyAssignmentFixture(package,
                SourceVector("SkinTone", 0.1f, 0.2f, 0.3f, 0.4f), SourceVector("HeadOnly", 1, 1, 1, 1));
            if (!hasOverrides) face.WriteProperty(new ObjectProperty(0, "m_oMaterialOverrides"));
            var shared = CreateMic(package, "SharedBodyMaterial", "BodyParent");
            shared.WriteProperty(new ArrayProperty<StructProperty>([MicVector("GearTint", 0.9f)], "VectorParameterValues"));
            shared.WriteProperty(new BoolProperty(true, "bTwoSided"));
            body.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(shared)], "Materials"));
            var hair = CreateHeadComponent(package, "HMM_HIR_Custom_MDL", "HairSMC");
            var hairParent = CreateMic(package, "HairMaterial", "HMN_HED_PRO_MASTER_HAIR_MAT");
            var hairMesh = package.GetUExport(hair.GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
            var binary = SkeletalMesh.Create();
            binary.Materials = [hairParent.UIndex];
            hairMesh.WriteBinary(binary);
            face.WriteProperty(new ObjectProperty(hairMesh, "m_oHairMesh"));
            hair.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(shared)], "Materials"));
            actor.WriteProperty(new ObjectProperty(hair, game == MEGame.LE3 ? "HairMesh" : "m_oHairMesh"));
            var before = shared.GetProperties();
            before.RemoveNamedProperty("VectorParameterValues");
            package.Save();
            var profile = game.ToString().ToLowerInvariant() + "-human-male";
            var service = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null));
            service.AssignMaterials(path, face.UIndex.ToString(), profile, actor.UIndex);
            int activeMaterial;
            int exportCount;
            using (var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
            {
                var after = reopened.GetUExport(shared.UIndex).GetProperties();
                var vectors = after.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues")!;
                TestAssert.Equal(hasOverrides ? 2 : 1, vectors.Count);
                TestAssert.True(vectors.All(value => value.GetProp<NameProperty>("ParameterName")!.Value.Name != "HeadOnly"),
                    "Full head parameters were applied to a shared Body MIC.");
                after.RemoveNamedProperty("VectorParameterValues");
                TestAssert.True(before.Equivalent(after), "Attachment assignment changed the Body MIC's parent or properties.");
                activeMaterial = reopened.GetUExport(hair.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials")!.Single().Value;
                TestAssert.True(activeMaterial != shared.UIndex, "The hair attachment retained the shared Body MIC.");
                if (hasOverrides)
                    TestAssert.Equal(hairParent.UIndex, reopened.GetUExport(activeMaterial).GetProperty<ObjectProperty>("Parent")!.Value);
                else TestAssert.Equal(hairParent.UIndex, activeMaterial);
                exportCount = reopened.ExportCount;
            }
            service.AssignMaterials(path, face.UIndex.ToString(), profile, actor.UIndex);
            using var repeated = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(exportCount, repeated.ExportCount);
            TestAssert.Equal(activeMaterial, repeated.GetUExport(hair.UIndex)
                .GetProperty<ArrayProperty<ObjectProperty>>("Materials")!.Single().Value);
        });
    }

    private static readonly (MEGame Game, string ActorClass)[] PlacedActorCases =
        [
            (MEGame.LE1, "BioPawn"), (MEGame.LE2, "BioPawn"),
            (MEGame.LE2, "SFXSkeletalMeshActor"), (MEGame.LE2, "SFXSkeletalMeshActorMAT"),
            (MEGame.LE3, "BioPawn"), (MEGame.LE3, "SFXStuntActor"),
            (MEGame.LE3, "SFXSkeletalMeshActor"), (MEGame.LE3, "SFXSkeletalMeshActorMAT")
        ];

    private static void AssignsExactMorphOwner()
    {
        foreach (var (game, actorClass) in PlacedActorCases)
        WithPackage(game, (path, package) =>
        {
            var selected = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var empty = package.CreateExport("BaldFace", "BioMorphFace", indexed: false);
            var old = package.CreateExport("OldFace", "BioMorphFace", indexed: false);
            var head = CreateHeadComponent(package, "HMM_HED_PROBase_MDL", "TargetHead");
            var actor = package.CreateExport("TargetActor", actorClass, indexed: false);
            actor.WriteProperty(new ObjectProperty(head, game == MEGame.LE1 ? "m_oHeadMesh" : "HeadMesh"));
            var owner = actor;
            var morphProperty = "MorphHead";
            if (game == MEGame.LE1)
            {
                owner = package.CreateExport("LocalAppearance", "BioInterface_Appearance_Pawn", indexed: false);
                var behavior = package.CreateExport("Behavior", "BioPawnBehavior", indexed: false);
                behavior.WriteProperty(new ObjectProperty(owner, "m_oAppearanceType"));
                actor.WriteProperty(new ObjectProperty(behavior, "m_oBehavior"));
                morphProperty = "m_oMorphFace";
            }
            if (game == MEGame.LE3)
            {
                var archetype = package.CreateExport("Default__AssignmentActor", actorClass, indexed: false);
                archetype.WriteProperty(new ObjectProperty(old, morphProperty));
                actor.Archetype = archetype;
            }
            else owner.WriteProperty(new ObjectProperty(old, morphProperty));
            var hair = CreateHeadComponent(package, "OldHairMesh", "ExistingHairComponent");
            actor.WriteProperty(new ObjectProperty(hair, actorClass == "BioPawn" ? "m_oHairMesh" : "HairMesh"));
            if (actorClass == "BioPawn")
                actor.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(hair), new ObjectProperty(hair)], "m_aoAccessories"));
            var mic = CreateMic(package, "ActorHairMic", "OldHairMaster");
            mic.WriteProperty(new FloatProperty(0.7f, "AuthoredValue"));
            var secondMic = CreateMic(package, "ActorHairSecondMic", "OldSecondHairMaster");
            mic.Parent = actorClass == "BioPawn" ? actor : hair;
            secondMic.Parent = actor;
            head.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(secondMic)], "Materials"));
            hair.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(mic), new ObjectProperty(secondMic)], "Materials"));
            ExportEntry? hairArchetype = null;
            if (game == MEGame.LE3 && actorClass == "BioPawn")
            {
                hairArchetype = package.CreateExport("HairComponentArchetype", "SkeletalMeshComponent", indexed: false);
                hairArchetype.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(mic), new ObjectProperty(secondMic)], "Materials"));
                hair.Archetype = hairArchetype;
                hair.RemoveProperty("Materials");
            }
            var newMaterial = CreateMic(package, "AuthoredHairMic", "HMN_HED_PRO_MASTER_HAIR_MAT");
            var secondMaterial = CreateMic(package, "AuthoredSecondHairMic", "HMN_HED_PRO_MASTER_HAIR_MAT");
            var defaultMaterial = CreateMic(package, "AuthoredDefaultHairMic", "HMN_HED_PRO_MASTER_HAIR_MAT");
            var newHair = package.CreateExport("AuthoredHairMesh", "SkeletalMesh", indexed: false);
            var binary = SkeletalMesh.Create();
            binary.Materials = [newMaterial.UIndex, secondMaterial.UIndex, defaultMaterial.UIndex];
            newHair.WriteBinary(binary);
            selected.WriteProperty(new ObjectProperty(newHair, "m_oHairMesh"));
            var oldAccessory = package.CreateExport("OldHat", "SkeletalMesh", indexed: false);
            var oldHatMaterial = CreateMic(package, "OldHatMaterial", "OldHatMaster");
            var oldHatBinary = SkeletalMesh.Create();
            oldHatBinary.Materials = [oldHatMaterial.UIndex];
            oldAccessory.WriteBinary(oldHatBinary);
            old.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(oldAccessory)], "m_oOtherMeshes"));
            var newAccessory = package.CreateExport("AuthoredHat", "SkeletalMesh", indexed: false);
            var newHatMaterial = CreateMic(package, "AuthoredHatMaterial", "HMN_HAT_MASTER_MAT");
            var hatBinary = SkeletalMesh.Create();
            hatBinary.Materials = [newHatMaterial.UIndex];
            newAccessory.WriteBinary(hatBinary);
            selected.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(newAccessory)], "m_oOtherMeshes"));
            var gear = package.CreateExport("ExistingGearComponent", "SkeletalMeshComponent", indexed: false);
            var gearMic = CreateMic(package, "ActorGearMic", "UnusedGearMaster");
            if (game == MEGame.LE3) gearMic.Parent = gear;
            gearMic.WriteProperty(new ObjectProperty(oldHatMaterial, "Parent"));
            gear.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(gearMic)], "Materials"));
            actor.WriteProperty(new ObjectProperty(gear, actorClass == "BioPawn" ? "m_oHeadGearMesh" : "HeadGearMesh"));
            var gearBefore = gear.Data.ToArray();
            var gearMicBefore = gearMic.Data.ToArray();
            var sourceBefore = selected.Data.ToArray();
            var headBefore = head.Data.ToArray();
            var nonMorphData = package.Exports.Where(export => export.UIndex != owner.UIndex)
                .ToDictionary(export => export.UIndex, export => export.Data.ToArray());
            var exportCount = package.ExportCount;
            package.Save();
            var actorId = actor.UIndex;
            var selectedId = selected.UIndex;
            var oldId = old.UIndex;

            var result = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMorph(path, selectedId.ToString(), game.ToString().ToLowerInvariant() + "-human-male", actorId);

            using (var morphOnly = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
                foreach (var (id, data) in nonMorphData)
                    TestAssert.True(morphOnly.GetUExport(id).Data.SequenceEqual(data),
                        "Morph assignment changed data outside its exact morph owner.");
            var materials = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMaterials(path, selectedId.ToString(), game.ToString().ToLowerInvariant() + "-human-male", actorId);
            TestAssert.Equal(0, materials.ChangedMaterials.Count);
            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(oldId, result.PreviousMorphUIndex);
            TestAssert.Equal(selectedId, result.NewMorphUIndex);
            TestAssert.Equal(selectedId,
                reopened.GetUExport(owner.UIndex).GetProperty<ObjectProperty>(morphProperty)!.Value);
            TestAssert.True(newHair.UIndex ==
                reopened.GetUExport(hair.UIndex).GetProperty<ObjectProperty>("SkeletalMesh")!.Value,
                $"{game} {actorClass}: the existing hair component retained its old mesh.");
            TestAssert.Equal(newMaterial.UIndex,
                reopened.GetUExport(mic.UIndex).GetProperty<ObjectProperty>("Parent")!.Value);
            TestAssert.Equal(secondMaterial.UIndex,
                reopened.GetUExport(secondMic.UIndex).GetProperty<ObjectProperty>("Parent")!.Value);
            TestAssert.Equal(2, (reopened.GetUExport(hair.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials")
                ?? reopened.GetUExport(hairArchetype!.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials"))!.Count);
            TestAssert.Equal(3, materials.ChangedAttachments.Single(value => value.Role == ActorComponentRole.Hair).MaterialSlots.Count);
            TestAssert.Equal(0.7f, reopened.GetUExport(mic.UIndex).GetProperty<FloatProperty>("AuthoredValue")!.Value);
            if (game == MEGame.LE3)
            {
                TestAssert.Equal(newAccessory.UIndex,
                    reopened.GetUExport(gear.UIndex).GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
                TestAssert.Equal(newHatMaterial.UIndex,
                    reopened.GetUExport(gearMic.UIndex).GetProperty<ObjectProperty>("Parent")!.Value);
            }
            else TestAssert.True(reopened.GetUExport(gear.UIndex).Data.SequenceEqual(gearBefore) &&
                                 reopened.GetUExport(gearMic.UIndex).Data.SequenceEqual(gearMicBefore),
                "Unproven LE1/2 headgear mapping was changed.");
            TestAssert.Equal(exportCount, reopened.ExportCount);
            TestAssert.True(reopened.GetUExport(selectedId).Data.SequenceEqual(sourceBefore) &&
                            reopened.GetUExport(head.UIndex).Data.SequenceEqual(headBefore),
                "Assignment changed the source face or the existing head component.");
            new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMaterials(path, empty.UIndex.ToString(), game.ToString().ToLowerInvariant() + "-human-male", actorId);
            using var cleared = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(0, cleared.GetUExport(hair.UIndex).GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
            var clearedMaterials = cleared.GetUExport(hair.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials")
                ?? cleared.GetUExport(hairArchetype!.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials")!;
            TestAssert.True(clearedMaterials.Select(value => value.Value).SequenceEqual(new[] { mic.UIndex, secondMic.UIndex }),
                "Clearing hair destroyed the component's material slot links.");
            foreach (var id in new[] { mic.UIndex, secondMic.UIndex, gearMic.UIndex })
            {
                var expected = reopened.GetUExport(id).GetProperties();
                if (id == mic.UIndex && hairArchetype is null || game == MEGame.LE3 && id == gearMic.UIndex)
                {
                    TestAssert.Equal(0, cleared.GetUExport(id).GetProperty<ObjectProperty>("Parent")!.Value);
                    expected.AddOrReplaceProp(new ObjectProperty(0, "Parent"));
                    foreach (var category in new[] { "TextureParameterValues", "VectorParameterValues", "ScalarParameterValues" })
                        expected.AddOrReplaceProp(new ArrayProperty<StructProperty>(category));
                }
                TestAssert.True(cleared.GetUExport(id).GetProperties().Equivalent(expected),
                    "Clearing changed material data beyond the attachment-owned Parent and parameter arrays.");
            }
            if (hairArchetype is not null)
                TestAssert.True(cleared.GetUExport(hairArchetype.UIndex).Data.SequenceEqual(hairArchetype.Data),
                    "Clearing inherited material references changed the archetype.");
            if (game == MEGame.LE3)
            {
                TestAssert.Equal(0, cleared.GetUExport(gear.UIndex).GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
                TestAssert.True(cleared.GetUExport(gear.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials")!
                    .Single().Value == gearMic.UIndex, "Clearing an accessory destroyed its material slot link.");
            }
            if (actorClass != "BioPawn")
            {
                // A disconnected component-owned child still has proven ownership.
                cleared.GetUExport(mic.UIndex).WriteProperty(new ObjectProperty(newMaterial, "Parent"));
                cleared.Save();
                new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                    .AssignMaterials(path, empty.UIndex.ToString(), game.ToString().ToLowerInvariant() + "-human-male", actorId);
                using var clearedAgain = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
                TestAssert.Equal(0, clearedAgain.GetUExport(mic.UIndex).GetProperty<ObjectProperty>("Parent")!.Value);
                cleared.GetUExport(mic.UIndex).WriteProperty(new ObjectProperty(0, "Parent"));
            }
            TestAssert.Equal(exportCount, cleared.ExportCount);
            TestAssert.Equal(selectedId, cleared.GetUExport(owner.UIndex).GetProperty<ObjectProperty>(morphProperty)!.Value);
            if (game == MEGame.LE3)
            {
                cleared.GetUExport(selectedId).WriteProperty(new ObjectProperty(0, "m_oHairMesh"));
                cleared.Save();
                var reapply = new ActorAssignmentInventoryService((_, _) => null)
                    .Read(path, selectedId.ToString(), "le3-human-male").Candidates.Single(value => value.UIndex == actorId);
                TestAssert.Equal(gear.UIndex, reapply.AttachmentTargets.Single(value =>
                    value.SourceMeshUIndex == newAccessory.UIndex).ComponentUIndex);
            }

            // Other can hold hair too. Follow an exact existing component match,
            // and leave a different equipment mesh alone despite its material slots.
            cleared.GetUExport(owner.UIndex).WriteProperty(new ObjectProperty(oldId, morphProperty));
            cleared.GetUExport(hair.UIndex).WriteProperty(new ObjectProperty(oldAccessory, "SkeletalMesh"));
            cleared.GetUExport(gear.UIndex).WriteProperty(new ObjectProperty(newAccessory, "SkeletalMesh"));
            cleared.GetUExport(selectedId).WriteProperty(new ObjectProperty(0, "m_oHairMesh"));
            cleared.GetUExport(selectedId).WriteProperty(new ArrayProperty<ObjectProperty>(
                [new ObjectProperty(newHair)], "m_oOtherMeshes"));
            var equipmentBefore = cleared.GetUExport(gear.UIndex).Data.ToArray();
            var equipmentMicBefore = cleared.GetUExport(gearMic.UIndex).Data.ToArray();
            cleared.Save();
            new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMaterials(path, selectedId.ToString(), game.ToString().ToLowerInvariant() + "-human-male", actorId);
            using var otherHair = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(newHair.UIndex, otherHair.GetUExport(hair.UIndex).GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
            TestAssert.True(otherHair.GetUExport(gear.UIndex).Data.SequenceEqual(equipmentBefore) &&
                            otherHair.GetUExport(gearMic.UIndex).Data.SequenceEqual(equipmentMicBefore),
                "Unrelated equipment was changed by the morph Other slot.");
            otherHair.GetUExport(selectedId).WriteProperty(new ObjectProperty(newAccessory, "m_oHairMesh"));
            otherHair.Save();
            var conflict = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMaterials(path, selectedId.ToString(), game.ToString().ToLowerInvariant() + "-human-male", actorId);
            TestAssert.Equal("loc_ActorAssignment_SecondHairSkipped", conflict.UserWarnings.Single().LocalizationKey);
            using var preferred = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(newAccessory.UIndex,
                preferred.GetUExport(hair.UIndex).GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
            var meshOnly = preferred.GetUExport(hair.UIndex).GetProperties();
            meshOnly.RemoveNamedProperty("Materials");
            preferred.GetUExport(hair.UIndex).Archetype = null;
            preferred.GetUExport(hair.UIndex).WriteProperties(meshOnly);
            preferred.Save();
            var meshOnlyService = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null));
            var meshOnlyCandidate = meshOnlyService.ReadInventory(path, selectedId.ToString(),
                game.ToString().ToLowerInvariant() + "-human-male").Candidates.Single(value => value.UIndex == actorId);
            TestAssert.True(meshOnlyCandidate.CanAssignMaterials && meshOnlyCandidate.SafeMaterialCount == 0,
                "An attachment with no component MIC was rejected.");
            meshOnlyService.AssignMaterials(path, selectedId.ToString(), game.ToString().ToLowerInvariant() + "-human-male", actorId);
            using var defaults = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.True(defaults.GetUExport(hair.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials") is null,
                "A component using mesh defaults acquired unnecessary material instances.");
            TestAssert.Equal(exportCount, defaults.ExportCount);
            if (actorClass == "BioPawn")
            {
                var savedActor = defaults.GetUExport(actorId);
                savedActor.WriteProperty(new ObjectProperty(0, "m_oHairMesh"));
                defaults.GetUExport(owner.UIndex).WriteProperty(new ObjectProperty(oldId, morphProperty));
                defaults.GetUExport(oldId).WriteProperty(new ObjectProperty(
                    hair.GetProperty<ObjectProperty>("SkeletalMesh")!.Value, "m_oHairMesh"));
                defaults.GetUExport(hair.UIndex).WriteProperty(new ObjectProperty(
                    hair.GetProperty<ObjectProperty>("SkeletalMesh")!.Value, "SkeletalMesh"));
                defaults.GetUExport(gear.UIndex).WriteProperty(new ObjectProperty(oldAccessory.UIndex, "SkeletalMesh"));
                defaults.GetUExport(selectedId).WriteProperty(new ObjectProperty(newHair.UIndex, "m_oHairMesh"));
                defaults.GetUExport(selectedId).WriteProperty(new ArrayProperty<ObjectProperty>(
                    [new ObjectProperty(newAccessory.UIndex)], "m_oOtherMeshes"));
                var arrayOwner = game == MEGame.LE3 ? (ExportEntry)savedActor.Archetype! : savedActor;
                if (game == MEGame.LE3) savedActor.RemoveProperty("m_aoAccessories");
                arrayOwner.WriteProperty(new ArrayProperty<ObjectProperty>(
                    [new ObjectProperty(gear.UIndex), new ObjectProperty(hair.UIndex), new ObjectProperty(gear.UIndex),
                        new ObjectProperty(head.UIndex)], "m_aoAccessories"));
                var arrayBefore = arrayOwner.Data.ToArray();
                defaults.Save();
                var arrayResult = meshOnlyService.AssignMaterials(path, selectedId.ToString(),
                    game.ToString().ToLowerInvariant() + "-human-male", actorId);
                TestAssert.Equal(2, arrayResult.ChangedAttachments.Count);
                using var arraySaved = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
                TestAssert.Equal(newHair.UIndex, arraySaved.GetUExport(hair.UIndex).GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
                TestAssert.Equal(newAccessory.UIndex, arraySaved.GetUExport(gear.UIndex).GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
                TestAssert.True(arraySaved.GetUExport(arrayOwner.UIndex).Data.SequenceEqual(arrayBefore),
                    "Attachment assignment rewrote its owning array or changed its order.");
                TestAssert.True(arraySaved.GetUExport(head.UIndex).Data.SequenceEqual(headBefore),
                    "An aliased head component in the accessory array was overwritten.");
                if (game == MEGame.LE3)
                {
                    var duplicate = arraySaved.CreateExport("OtherGearComponent", "SkeletalMeshComponent", indexed: false);
                    duplicate.WriteProperty(new ObjectProperty(newAccessory.UIndex, "SkeletalMesh"));
                    arraySaved.GetUExport(actorId).WriteProperty(new ObjectProperty(duplicate, "m_oHeadGearMesh"));
                    arraySaved.Save();
                    var ambiguous = meshOnlyService.ReadInventory(path, selectedId.ToString(), "le3-human-male")
                        .Candidates.Single(value => value.UIndex == actorId);
                    TestAssert.True(ambiguous.AttachmentTargets.All(value => value.Role != ActorComponentRole.Accessory),
                        "A material-family fallback overrode ambiguous exact component matches.");
                }

                // Once cleared, an array component has no role evidence. Reuse only
                // one empty component for one authored attachment, never array order.
                arraySaved.GetUExport(actorId).WriteProperty(new ObjectProperty(0, "m_oHeadGearMesh"));
                foreach (var componentId in new[] { hair.UIndex, gear.UIndex })
                {
                    var component = arraySaved.GetUExport(componentId);
                    component.WriteProperty(new ObjectProperty(0, "SkeletalMesh"));
                    component.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(0)], "Materials"));
                }
                arraySaved.GetUExport(selectedId).WriteProperty(new ArrayProperty<ObjectProperty>("m_oOtherMeshes"));
                arraySaved.Save();
                var twoEmpty = meshOnlyService.ReadInventory(path, selectedId.ToString(),
                    game.ToString().ToLowerInvariant() + "-human-male").Candidates.Single(value => value.UIndex == actorId);
                TestAssert.Equal(0, twoEmpty.AttachmentTargets.Count);
                var savedArrayOwner = arraySaved.GetUExport(arrayOwner.UIndex);
                savedArrayOwner.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(hair.UIndex)], "m_aoAccessories"));
                arraySaved.Save();
                var soleEmpty = meshOnlyService.ReadInventory(path, selectedId.ToString(),
                    game.ToString().ToLowerInvariant() + "-human-male").Candidates.Single(value => value.UIndex == actorId);
                TestAssert.Equal(hair.UIndex, soleEmpty.AttachmentTargets.Single().ComponentUIndex);
                arraySaved.GetUExport(selectedId).WriteProperty(new ArrayProperty<ObjectProperty>(
                    [new ObjectProperty(newAccessory.UIndex)], "m_oOtherMeshes"));
                arraySaved.Save();
                var twoSources = meshOnlyService.ReadInventory(path, selectedId.ToString(),
                    game.ToString().ToLowerInvariant() + "-human-male").Candidates.Single(value => value.UIndex == actorId);
                TestAssert.Equal(0, twoSources.AttachmentTargets.Count);
            }
        });
    }

    private static void OutsideEditPreserved()
    {
        WithPackage(MEGame.LE3, (path, package) =>
        {
            var selected = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var old = package.CreateExport("OldFace", "BioMorphFace", indexed: false);
            var actor = CreateActorWithHead(package, "SFXStuntActor", "TargetActor", "HMM_HED_PROBase_MDL");
            actor.WriteProperty(new ObjectProperty(old, "MorphHead"));
            package.Save();
            string? candidate = null;
            byte[]? outsideEdit = null;
            FileSaveTransaction.BeforeCommitForTesting = (temporaryPath, destination) =>
            {
                candidate = temporaryPath;
                using (var external = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true))
                {
                    external.GetUExport(actor.UIndex).WriteProperty(new IntProperty(73, "ExternalEdit"));
                    external.Save();
                }
                outsideEdit = File.ReadAllBytes(destination);
            };
            try
            {
                var rejected = false;
                try
                {
                    new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                        .AssignMorph(path, selected.UIndex.ToString(), "le3-human-male", actor.UIndex);
                }
                catch (IOException) { rejected = true; }
                TestAssert.True(rejected && outsideEdit is not null, "Actor assignment did not reject the outside edit at publication.");
                TestAssert.True(outsideEdit!.SequenceEqual(File.ReadAllBytes(path)), "Actor assignment replaced the outside edit.");
                TestAssert.True(candidate is not null && !File.Exists(candidate), "Rejected actor assignment left its candidate behind.");
            }
            finally { FileSaveTransaction.BeforeCommitForTesting = null; }
        });
    }

    private static void LeavesSharedLe1TypeUntouched()
    {
        WithPackage(MEGame.LE1, (path, package) =>
        {
            var selected = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var old = package.CreateExport("OldFace", "BioMorphFace", indexed: false);
            var head = CreateHeadComponent(package, "HMM_HED_PROBase_MDL", "HeadComponent");
            old.WriteProperty(new ObjectProperty(
                head.GetProperty<ObjectProperty>("SkeletalMesh")!.ResolveToEntry(package), "m_oBaseHead"));
            var type = package.CreateExport("SharedType", "BioPawnChallengeScaledType", indexed: false);
            type.WriteProperty(new ObjectProperty(old, "m_oMorphFace"));
            var appearance = package.CreateExport("LocalAppearance", "BioInterface_Appearance_Pawn", indexed: false);
            appearance.WriteProperty(new ObjectProperty(old, "m_oMorphFace"));
            var behavior = package.CreateExport("Behavior", "BioPawnBehavior", indexed: false);
            behavior.WriteProperty(new ObjectProperty(appearance, "m_oAppearanceType"));
            behavior.WriteProperty(new ObjectProperty(type, "m_oActorType"));
            var actor = package.CreateExport("PlacedPawn", "BioPawn", indexed: false);
            actor.WriteProperty(new ObjectProperty(behavior, "m_oBehavior"));
            actor.WriteProperty(new ObjectProperty(head, "m_oHeadMesh"));
            package.Save();
            var selectedId = selected.UIndex;
            var oldId = old.UIndex;
            var typeId = type.UIndex;
            var appearanceId = appearance.UIndex;
            var actorId = actor.UIndex;

            new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMorph(path, selectedId.ToString(), "le1-human-male", actorId);

            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(selectedId,
                reopened.GetUExport(appearanceId).GetProperty<ObjectProperty>("m_oMorphFace")!.Value);
            TestAssert.Equal(oldId,
                reopened.GetUExport(typeId).GetProperty<ObjectProperty>("m_oMorphFace")!.Value);
            var actorData = reopened.GetUExport(actorId).Data.ToArray();
            var appearanceData = reopened.GetUExport(appearanceId).Data.ToArray();
            new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMorph(path, selectedId.ToString(), "le1-human-male", typeId);
            using var spawned = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(selectedId, spawned.GetUExport(typeId).GetProperty<ObjectProperty>("m_oMorphFace")!.Value);
            TestAssert.True(spawned.GetUExport(actorId).Data.SequenceEqual(actorData) &&
                            spawned.GetUExport(appearanceId).Data.SequenceEqual(appearanceData),
                "Explicit spawn-template assignment changed a placed actor or its local appearance.");
            TestAssert.Equal(reopened.ExportCount, spawned.ExportCount);
        });
    }

    private static void ConvertsAndProtectsMaterials()
    {
        foreach (var (game, actorClass) in PlacedActorCases)
        WithPackage(game, (path, package) =>
        {
            var texture = package.CreateExport("SourceTexture", "Texture2D", indexed: false);
            var face = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var bmo = package.CreateExport("SelectedFaceMaterials", "BioMaterialOverride", face, indexed: false);
            bmo.WriteProperty(new ArrayProperty<StructProperty>(
            [
                new StructProperty("TextureParameter", false,
                    new NameProperty("HED_Scalp_Diff", "nName"),
                    new ObjectProperty(texture, "m_pTexture"))
            ], "m_aTextureOverrides"));
            bmo.WriteProperty(new ArrayProperty<StructProperty>(
            [
                new StructProperty("ColorParameter", false,
                    new NameProperty("SkinTint", "nName"),
                    Color(0.1f, 0.2f, 0.3f, 0.4f, "cValue"))
            ], "m_aColorOverrides"));
            // A present empty category must replace the destination category with an empty array.
            bmo.WriteProperty(new ArrayProperty<StructProperty>("m_aScalarOverrides"));
            face.WriteProperty(new ObjectProperty(bmo, "m_oMaterialOverrides"));

            var target = CreateMic(package, "HMM_HED_PRO_Face_Mat", "HMM_HED_PRONPC_MASTER_FACE_MAT");
            target.WriteProperty(new ArrayProperty<StructProperty>(
            [
                new StructProperty("ScalarParameterValue", false,
                    GuidProperty(), new NameProperty("OldScalar", "ParameterName"),
                    new FloatProperty(2f, "ParameterValue"))
            ], "ScalarParameterValues"));
            var body = CreateMic(package, "HMM_ARM_Casual_Mat", "HMM_ARM_Casual_Master");
            body.WriteProperty(new ArrayProperty<StructProperty>(
            [
                new StructProperty("ScalarParameterValue", false,
                    GuidProperty(), new NameProperty("Protected", "ParameterName"),
                    new FloatProperty(7f, "ParameterValue"))
            ], "ScalarParameterValues"));
            var head = CreateHeadComponent(package, "HMM_HED_PROBase_MDL", "HeadComponent");
            head.WriteProperty(new ArrayProperty<ObjectProperty>(
                [new ObjectProperty(target), new ObjectProperty(body)], "Materials"));
            var actor = package.CreateExport("TargetPawn", actorClass, indexed: false);
            actor.WriteProperty(new ObjectProperty(head, "HeadMesh"));
            var hair = CreateHeadComponent(package, "HMM_HIR_Custom_MDL", "HairSMC");
            var hairParent = CreateMic(package, "SourceHairMaterial", "HMN_HED_PRO_MASTER_HAIR_MAT");
            var hairMesh = package.GetUExport(hair.GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
            var binary = SkeletalMesh.Create();
            var secondHairParent = package.CreateExport("SecondHairParent", "MaterialInstanceConstant", indexed: false);
            secondHairParent.WriteProperty(new ObjectProperty(hairParent, "Parent"));
            binary.Materials = [hairParent.UIndex, hairParent.UIndex, secondHairParent.UIndex];
            hairMesh.WriteBinary(binary);
            face.WriteProperty(new ObjectProperty(hairMesh, "m_oHairMesh"));
            actor.WriteProperty(new ObjectProperty(hair, actorClass == "BioPawn" ? "m_oHairMesh" : "HairMesh"));
            var connected = package.CreateExport("ConnectedHair", "MaterialInstanceConstant", hair, indexed: false);
            connected.WriteProperty(new ObjectProperty(body, "Parent"));
            hair.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(connected), new ObjectProperty(0)], "Materials"));
            var retained = package.CreateExport("RetainedHair", "BioMaterialInstanceConstant", hair, indexed: false);
            retained.WriteProperty(new ObjectProperty(0, "Parent"));
            var secondRetained = package.CreateExport("SecondRetainedHair", "MaterialInstanceConstant", hair, indexed: false);
            secondRetained.WriteProperty(new ObjectProperty(body, "Parent"));
            var retainedHead = package.CreateExport("RetainedHead", "MaterialInstanceConstant", head, indexed: false);
            retainedHead.WriteProperty(new ObjectProperty(0, "Parent"));
            var refreshed = new List<int> { target.UIndex, connected.UIndex, retained.UIndex, secondRetained.UIndex, retainedHead.UIndex };
            if (game == MEGame.LE3 || actorClass == "BioPawn")
            {
                var gear = CreateHeadComponent(package, "HMM_HAT_Cap_MDL", "GearSMC");
                var gearParent = CreateMic(package, "SourceHatMaterial", "HMN_HAT_MASTER_MAT");
                var gearMesh = package.GetUExport(gear.GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
                var gearBinary = SkeletalMesh.Create();
                gearBinary.Materials = [gearParent.UIndex];
                gearMesh.WriteBinary(gearBinary);
                face.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(gearMesh)], "m_oOtherMeshes"));
                if (game == MEGame.LE3) actor.WriteProperty(new ObjectProperty(gear, actorClass == "BioPawn" ? "m_oHeadGearMesh" : "HeadGearMesh"));
                else actor.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(gear)], "m_aoAccessories"));
                var hat = package.CreateExport("ActorHat", "MaterialInstanceConstant", gear, indexed: false);
                hat.WriteProperty(new ObjectProperty(gearParent, "Parent"));
                gear.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(hat)], "Materials"));
                refreshed.Add(hat.UIndex);
            }
            if (actorClass == "BioPawn")
            {
                var bodyComponent = CreateHeadComponent(package, "HMM_ARM_Test_MDL", "BodySMC");
                bodyComponent.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(body)], "Materials"));
                actor.WriteProperty(new ObjectProperty(bodyComponent, "Mesh"));
                var free = package.CreateExport("UnassignedSMC", "SkeletalMeshComponent", indexed: false);
                free.WriteProperty(new ObjectProperty(0, "SkeletalMesh"));
                var freeMic = package.CreateExport("ActorOwnedFreeMic", "BioMaterialInstanceConstant", actor, indexed: false);
                freeMic.WriteProperty(new ObjectProperty(0, "Parent"));
                free.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(freeMic)], "Materials"));
                var arrayName = game == MEGame.LE1 ? "m_aoMeshes" : "m_aoAccessories";
                actor.WriteProperty(new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(head), new ObjectProperty(hair),
                    new ObjectProperty(bodyComponent), new ObjectProperty(free) }
                    .Concat(actor.GetProperty<ArrayProperty<ObjectProperty>>(arrayName)?.AsEnumerable() ?? []), arrayName));
                refreshed.Add(freeMic.UIndex);
            }
            foreach (var id in refreshed)
                package.GetUExport(id).WriteProperty(new ArrayProperty<StructProperty>([
                    new StructProperty("TextureParameterValue", false, GuidProperty(),
                        new NameProperty("StaleScalp", "ParameterName"), new ObjectProperty(0, "ParameterValue"))], "TextureParameterValues"));
            var emptyFace = package.CreateExport("ClearedAttachmentsFace", "BioMorphFace", indexed: false);
            emptyFace.WriteProperty(new ObjectProperty(bmo, "m_oMaterialOverrides"));
            var replacementHair = package.CreateExport("ReplacementHair", "SkeletalMesh", indexed: false);
            binary.Materials = [secondHairParent.UIndex, hairParent.UIndex, hairParent.UIndex];
            replacementHair.WriteBinary(binary);
            package.Save();
            var faceId = face.UIndex;
            var actorId = actor.UIndex;
            var targetId = target.UIndex;
            var bodyId = body.UIndex;
            var textureId = texture.UIndex;
            var bodyBefore = body.Data.ToArray();

            var result = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMaterials(path, faceId.ToString(), game.ToString().ToLowerInvariant() + "-human-male", actorId);

            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            var hairSlots = reopened.GetUExport(hair.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials")!;
            TestAssert.Equal(3, hairSlots.Count);
            TestAssert.Equal(connected.UIndex, hairSlots[0].Value);
            foreach (var slot in hairSlots.Skip(1))
            {
                TestAssert.True(!refreshed.Contains(slot.Value), "Assignment guessed a disconnected child's old slot.");
                TestAssert.Equal(hair.UIndex, reopened.GetUExport(slot.Value).idxLink);
                refreshed.Add(slot.Value);
            }
            TestAssert.Equal(hairParent.UIndex, reopened.GetUExport(hairSlots[1].Value).GetProperty<ObjectProperty>("Parent")!.Value);
            TestAssert.Equal(secondHairParent.UIndex, reopened.GetUExport(hairSlots[2].Value).GetProperty<ObjectProperty>("Parent")!.Value);
            var changed = reopened.GetUExport(targetId);
            var textureValue = changed.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")!.Single();
            var vectorValue = changed.GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues")!.Single();
            var scalars = changed.GetProperty<ArrayProperty<StructProperty>>("ScalarParameterValues")!;
            TestAssert.Equal(textureId, textureValue.GetProp<ObjectProperty>("ParameterValue")!.Value);
            TestAssert.Equal("HED_Scalp_Diff", textureValue.GetProp<NameProperty>("ParameterName")!.Value.Name);
            var expressionGuid = textureValue.GetProp<StructProperty>("ExpressionGUID");
            TestAssert.True(expressionGuid is not null &&
                            new[] { "A", "B", "C", "D" }.Any(name =>
                                expressionGuid.GetProp<IntProperty>(name)!.Value != 0),
                "Converted texture parameter has no generated ExpressionGUID.");
            TestAssert.Equal(0.3f,
                vectorValue.GetProp<StructProperty>("ParameterValue")!.GetProp<FloatProperty>("B")!.Value);
            TestAssert.Equal(0, scalars.Count);
            TestAssert.True(reopened.GetUExport(bodyId).Data.SequenceEqual(bodyBefore),
                "A skipped body MIC changed.");
            TestAssert.True(refreshed.ToHashSet().SetEquals(result.ChangedMaterials.Select(value => value.UIndex)),
                $"{game} {actorClass}: expected MICs {string.Join(',', refreshed)}, got {string.Join(',', result.ChangedMaterials.Select(value => value.UIndex))}; " +
                $"skipped: {string.Join(';', result.SkippedMaterials.Select(value => $"{value.UIndex}: {value.Reason}"))}");
            foreach (var id in refreshed)
            {
                var mic = reopened.GetUExport(id);
                var values = mic.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")!;
                TestAssert.Equal(textureId, values.Single().GetProp<ObjectProperty>("ParameterValue")!.Value);
                TestAssert.Equal("HED_Scalp_Diff", values.Single().GetProp<NameProperty>("ParameterName")!.Value.Name);
                TestAssert.Equal(0.3f, mic.GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues")!.Single()
                    .GetProp<StructProperty>("ParameterValue")!.GetProp<FloatProperty>("B")!.Value);
                TestAssert.Equal(0, mic.GetProperty<ArrayProperty<StructProperty>>("ScalarParameterValues")!.Count);
            }
            TestAssert.Equal(0, reopened.GetUExport(retained.UIndex).GetProperty<ObjectProperty>("Parent")!.Value);
            TestAssert.Equal(0, reopened.GetUExport(secondRetained.UIndex).GetProperty<ObjectProperty>("Parent")!.Value);
            TestAssert.Equal(hairParent.UIndex, reopened.GetUExport(connected.UIndex).GetProperty<ObjectProperty>("Parent")!.Value);
            var assignedCount = reopened.ExportCount;
            new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMaterials(path, faceId.ToString(), game.ToString().ToLowerInvariant() + "-human-male", actorId);
            using var assignedAgain = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(assignedCount, assignedAgain.ExportCount);
            var service = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null));
            var profile = game.ToString().ToLowerInvariant() + "-human-male";
            service.AssignMaterials(path, emptyFace.UIndex.ToString(), profile, actorId);
            using (var cleared = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
            {
                TestAssert.Equal(0, cleared.GetUExport(hair.UIndex).GetProperty<ObjectProperty>("SkeletalMesh")!.Value);
                TestAssert.True(cleared.GetUExport(hair.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials")!
                    .Select(value => value.Value).SequenceEqual(hairSlots.Select(value => value.Value)), "Clearing lost the reusable slot chain.");
                foreach (var id in hairSlots.Select(value => value.Value).Concat(new[] { retained.UIndex, secondRetained.UIndex }))
                {
                    var mic = cleared.GetUExport(id);
                    TestAssert.Equal(0, mic.GetProperty<ObjectProperty>("Parent")!.Value);
                    foreach (var category in new[] { "TextureParameterValues", "VectorParameterValues", "ScalarParameterValues" })
                        TestAssert.Equal(0, mic.GetProperty<ArrayProperty<StructProperty>>(category)!.Count);
                }
                cleared.GetUExport(faceId).WriteProperty(new ObjectProperty(replacementHair, "m_oHairMesh"));
                cleared.Save();
            }
            service.AssignMaterials(path, faceId.ToString(), profile, actorId);
            using var reapplied = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(assignedCount, reapplied.ExportCount);
            TestAssert.True(reapplied.GetUExport(hair.UIndex).GetProperty<ArrayProperty<ObjectProperty>>("Materials")!
                .Select(value => value.Value).SequenceEqual(hairSlots.Select(value => value.Value)), "Reapplication replaced the existing MICs.");
            for (var slot = 0; slot < hairSlots.Count; slot++)
            {
                var mic = reapplied.GetUExport(hairSlots[slot].Value);
                TestAssert.Equal(binary.Materials[slot], mic.GetProperty<ObjectProperty>("Parent")!.Value);
                TestAssert.Equal(textureId, mic.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")!
                    .Single().GetProp<ObjectProperty>("ParameterValue")!.Value);
            }
            TestAssert.True(reapplied.GetUExport(bodyId).Data.SequenceEqual(bodyBefore), "The clear/reapply cycle changed body material data.");
            TestAssert.True(result.SkippedMaterials.Any(value => value.UIndex == bodyId),
                "The result omitted the individually skipped body MIC.");
        });
    }

    private static void PreservesMissingMaterialCategories()
    {
        WithPackage(MEGame.LE2, (path, package) =>
        {
            var face = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var bmo = package.CreateExport("SelectedFaceMaterials", "BioMaterialOverride", face, indexed: false);
            bmo.WriteProperty(new ArrayProperty<StructProperty>(
            [
                new StructProperty("ScalarParameter", false,
                    new NameProperty("Roughness", "nName"), new FloatProperty(0.25f, "sValue"))
            ], "m_aScalarOverrides"));
            face.WriteProperty(new ObjectProperty(bmo, "m_oMaterialOverrides"));

            var target = CreateMic(package, "HMM_HED_PRO_Face_Mat", "HMM_HED_PRONPC_MASTER_FACE_MAT");
            target.WriteProperty(new ArrayProperty<StructProperty>(
            [
                new StructProperty("TextureParameterValue", false,
                    GuidProperty(), new NameProperty("KeepMe", "ParameterName"),
                    new ObjectProperty(0, "ParameterValue"))
            ], "TextureParameterValues"));
            var head = CreateHeadComponent(package, "HMM_HED_PROBase_MDL", "HeadComponent");
            head.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(target)], "Materials"));
            var actor = package.CreateExport("TargetPawn", "BioPawn", indexed: false);
            actor.WriteProperty(new ObjectProperty(head, "HeadMesh"));
            var bodyMic = CreateMic(package, "BodyMaterial", "UnrecognizedParent");
            bodyMic.WriteProperty(new ArrayProperty<StructProperty>(
                [MicVector("SkinTone", 0.9f)], "VectorParameterValues"));
            var body = CreateHeadComponent(package, "Body_MDL", "BodyComponent");
            body.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(bodyMic)], "Materials"));
            actor.WriteProperty(new ObjectProperty(body, "Mesh"));
            package.Save();
            var faceId = face.UIndex;
            var actorId = actor.UIndex;
            var targetId = target.UIndex;
            var bodyId = bodyMic.UIndex;
            var bodyBefore = bodyMic.Data.ToArray();

            var result = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMaterials(path, faceId.ToString(), "le2-human-male", actorId);

            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            var changed = reopened.GetUExport(targetId);
            TestAssert.Equal("KeepMe", changed
                .GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")!.Single()
                .GetProp<NameProperty>("ParameterName")!.Value.Name);
            TestAssert.Equal(0.25f, changed
                .GetProperty<ArrayProperty<StructProperty>>("ScalarParameterValues")!.Single()
                .GetProp<FloatProperty>("ParameterValue")!.Value);
            TestAssert.True(!result.ReplacedTextures && result.ReplacedScalars,
                "Result did not preserve the missing/present category distinction.");
            TestAssert.True(reopened.GetUExport(bodyId).Data.SequenceEqual(bodyBefore),
                "A source with no skin vectors changed a body MIC.");
            TestAssert.Equal(1, result.ChangedMaterials.Count);
        });
    }

    private static void UpdatesBodySkinVectors()
    {
        foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            WithPackage(game, (path, package) =>
            {
                var (face, bmo, actor, headMic, body) = CreateBodyAssignmentFixture(package,
                    SourceVector("SkinTone", 0.1f, 0.2f, 0.3f, 0.4f),
                    SourceVector("SkinLightScattering", 0.5f, 0.6f, 0.7f, 0.8f),
                    SourceVector("HeadOnly", 1, 1, 1, 1));
                bmo.WriteProperty(new ArrayProperty<StructProperty>("m_aTextureOverrides"));
                bmo.WriteProperty(new ArrayProperty<StructProperty>("m_aScalarOverrides"));

                // Unknown parents must still work. Neither their other parameters nor GUIDs may change.
                var existing = CreateMic(package, "CustomBodyMaterial", "UnrecognizedParent");
                var skin = MicVector("sKiNtOnE", 0.9f);
                skin.GetProp<StructProperty>("ExpressionGUID")!.GetProp<IntProperty>("A")!.Value = 123;
                skin.Properties.Add(new IntProperty(42, "AuthoredExtraField"));
                existing.WriteProperty(new ArrayProperty<StructProperty>(
                    [MicVector("GearTint", 0.25f), skin, MicVector("SkinLightScattering", 0.9f)], "VectorParameterValues"));
                existing.WriteProperty(new ArrayProperty<StructProperty>(
                [new StructProperty("ScalarParameterValue", false, GuidProperty(),
                    new NameProperty("Roughness", "ParameterName"), new FloatProperty(0.75f, "ParameterValue"))
                ], "ScalarParameterValues"));
                existing.WriteProperty(new ArrayProperty<StructProperty>(
                [new StructProperty("TextureParameterValue", false, GuidProperty(),
                    new NameProperty("BodyDiffuse", "ParameterName"), new ObjectProperty(0, "ParameterValue"))
                ], "TextureParameterValues"));
                existing.WriteProperty(new BoolProperty(true, "bTwoSided"));
                var empty = CreateMic(package, "SecondBodyMaterial", "AnotherUnknownParent");
                var unrelated = CreateMic(package, "OtherActorsBodyMaterial", "UnrecognizedParent");
                var imported = package.CreateImport("MaterialInstanceConstant", "ImportedBodyMaterial",
                    package.CreatePackageImport("MFEFixture_BodyImport"));
                body.WriteProperty(new ArrayProperty<ObjectProperty>(
                    [new ObjectProperty(existing), new ObjectProperty(empty), new ObjectProperty(imported),
                     new ObjectProperty(headMic)], "Materials"));
                package.Save();
                var before = existing.Data.ToArray();
                var emptyBefore = empty.Data.ToArray();
                var unrelatedBefore = unrelated.Data.ToArray();
                var profile = game.ToString().ToLowerInvariant() + "-human-male";
                var service = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null));
                var candidate = service.ReadInventory(path, face.UIndex.ToString(), profile)
                    .Candidates.Single(value => value.UIndex == actor.UIndex);
                TestAssert.Equal(2, candidate.Components.Count);
                TestAssert.True(candidate.Components.Single(value => value.Role == ActorComponentRole.Body).Inherited ==
                                (game == MEGame.LE3), "Body component inheritance was lost.");

                var result = service.AssignMaterials(path, face.UIndex.ToString(), profile, actor.UIndex);
                // Reassignment must update the additions rather than append duplicate parameters.
                service.AssignMaterials(path, face.UIndex.ToString(), profile, actor.UIndex);
                using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
                TestAssert.Equal(3, result.ChangedMaterials.Select(value => value.UIndex).Distinct().Count());
                TestAssert.True(result.SkippedMaterials.Any(value => value.UIndex == imported.UIndex),
                    "An imported body MIC was not retained as a read-only skipped slot.");
                TestAssert.Equal(3, reopened.GetUExport(headMic.UIndex)
                    .GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues")!.Count);
                var changed = reopened.GetUExport(existing.UIndex);
                var vectors = changed.GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues")!;
                TestAssert.Equal(3, vectors.Count);
                TestAssert.Equal(0.3f, vectors[1].GetProp<StructProperty>("ParameterValue")!.GetProp<FloatProperty>("B")!.Value);
                TestAssert.Equal(0.8f, vectors[2].GetProp<StructProperty>("ParameterValue")!.GetProp<FloatProperty>("A")!.Value);
                // Undo only the two colour values in memory: the entire original MIC must be byte-identical.
                vectors[1].Properties.AddOrReplaceProp(Color(0.9f, 0.9f, 0.9f, 0.9f, "ParameterValue"));
                vectors[2].Properties.AddOrReplaceProp(Color(0.9f, 0.9f, 0.9f, 0.9f, "ParameterValue"));
                changed.WriteProperty(vectors);
                TestAssert.True(changed.Data.SequenceEqual(before), "Other authored body MIC data changed.");
                var newMic = reopened.GetUExport(empty.UIndex);
                var newVectors = newMic.GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues")!;
                TestAssert.Equal(2, newVectors.Count);
                TestAssert.Equal("SkinTone", newVectors[0].GetProp<NameProperty>("ParameterName")!.Value.Name);
                TestAssert.Equal("SkinLightScattering", newVectors[1].GetProp<NameProperty>("ParameterName")!.Value.Name);
                TestAssert.Equal(0.4f, newVectors[0].GetProp<StructProperty>("ParameterValue")!.GetProp<FloatProperty>("A")!.Value);
                TestAssert.Equal(0.7f, newVectors[1].GetProp<StructProperty>("ParameterValue")!.GetProp<FloatProperty>("B")!.Value);
                var newProperties = newMic.GetProperties();
                newProperties.RemoveNamedProperty("VectorParameterValues");
                newMic.WriteProperties(newProperties);
                TestAssert.True(newMic.Data.SequenceEqual(emptyBefore), "Adding skin vectors changed other body MIC data.");
                TestAssert.True(reopened.GetUExport(unrelated.UIndex).Data.SequenceEqual(unrelatedBefore),
                    "An unrelated actor's MIC changed.");
            });
        }
    }

    private static void AddsOnlyPresentBodySkinVectors()
    {
        WithPackage(MEGame.LE2, (path, package) =>
        {
            var (face, _, actor, _, body) = CreateBodyAssignmentFixture(package,
                SourceVector("SkinTone", 0.1f, 0.2f, 0.3f, 0.4f));
            var bodyMic = CreateMic(package, "BodyMaterial", "UnknownParent");
            bodyMic.WriteProperty(new ArrayProperty<StructProperty>(
                [MicVector("SkinLightScattering", 0.9f)], "VectorParameterValues"));
            body.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(bodyMic)], "Materials"));
            package.Save();
            new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMaterials(path, face.UIndex.ToString(), "le2-human-male", actor.UIndex);
            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            var vectors = reopened.GetUExport(bodyMic.UIndex).GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues")!;
            TestAssert.Equal(2, vectors.Count);
            TestAssert.Equal(0.9f, vectors[0].GetProp<StructProperty>("ParameterValue")!.GetProp<FloatProperty>("B")!.Value);
            TestAssert.Equal(0.3f, vectors[1].GetProp<StructProperty>("ParameterValue")!.GetProp<FloatProperty>("B")!.Value);
        });
    }

    private static (ExportEntry Face, ExportEntry Override, ExportEntry Actor, ExportEntry HeadMic, ExportEntry Body)
        CreateBodyAssignmentFixture(IMEPackage package, params StructProperty[] colors)
    {
        var face = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
        var bmo = package.CreateExport("SelectedFaceMaterials", "BioMaterialOverride", face, indexed: false);
        bmo.WriteProperty(new ArrayProperty<StructProperty>(colors, "m_aColorOverrides"));
        face.WriteProperty(new ObjectProperty(bmo, "m_oMaterialOverrides"));
        var headMic = CreateMic(package, "HMM_HED_PRO_Face_Mat", "HMM_HED_PRONPC_MASTER_FACE_MAT");
        var head = CreateHeadComponent(package, "HMM_HED_PROBase_MDL", "HeadComponent");
        head.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(headMic)], "Materials"));
        var body = CreateHeadComponent(package, "Body_MDL", "BodyComponent");
        var actor = package.CreateExport("TargetActor", package.Game == MEGame.LE3 ? "SFXStuntActor" : "BioPawn", indexed: false);
        actor.WriteProperty(new ObjectProperty(head, package.Game == MEGame.LE1 ? "m_oHeadMesh" : "HeadMesh"));
        // Cover inherited BodyMesh in LE3 and the pawn Mesh property in LE1/2.
        if (package.Game == MEGame.LE3)
        {
            var archetype = package.CreateExport("Default__SFXStuntActor", "SFXStuntActor", indexed: false);
            archetype.WriteProperty(new ObjectProperty(body, "BodyMesh"));
            actor.Archetype = archetype;
        }
        else actor.WriteProperty(new ObjectProperty(body, "Mesh"));
        return (face, bmo, actor, headMic, body);
    }

    private static StructProperty SourceVector(string name, float r, float g, float b, float a) => new(
        "ColorParameter", false, new NameProperty(name, "nName"), Color(r, g, b, a, "cValue"));

    private static StructProperty MicVector(string name, float value) => new(
        "VectorParameterValue", false, GuidProperty(),
        Color(value, value, value, value, "ParameterValue"), new NameProperty(name, "ParameterName"));

    private static ExportEntry CreateActorWithHead(
        IMEPackage package, string actorClass, string actorName, string meshName)
    {
        var head = CreateHeadComponent(package, meshName, $"{actorName}_Head");
        var actor = package.CreateExport(actorName, actorClass, indexed: false);
        actor.WriteProperty(new ObjectProperty(head, "HeadMesh"));
        return actor;
    }

    private static ExportEntry CreateHeadComponent(IMEPackage package, string meshName, string componentName)
    {
        var mesh = package.CreateExport(meshName, "SkeletalMesh", indexed: false);
        var component = package.CreateExport(componentName, "SkeletalMeshComponent", indexed: false);
        component.WriteProperty(new ObjectProperty(mesh, "SkeletalMesh"));
        return component;
    }

    private static ExportEntry CreateMic(IMEPackage package, string name, string parentName)
    {
        var root = package.CreatePackageExport($"MFEFixture_{name}");
        var parent = package.CreateExport(parentName, "Material", root, indexed: false);
        var mic = package.CreateExport(name, "MaterialInstanceConstant", indexed: false);
        mic.WriteProperty(new ObjectProperty(parent, "Parent"));
        return mic;
    }

    private static StructProperty Color(float r, float g, float b, float a, string name) => new(
        "LinearColor", false,
        new FloatProperty(r, "R"), new FloatProperty(g, "G"),
        new FloatProperty(b, "B"), new FloatProperty(a, "A"))
    { Name = name, IsImmutable = true };

    private static StructProperty GuidProperty() => new(
        "Guid", false,
        new IntProperty(0, "A"), new IntProperty(0, "B"),
        new IntProperty(0, "C"), new IntProperty(0, "D"))
    { Name = "ExpressionGUID", IsImmutable = true };

    private static void WithPackage(MEGame game, Action<string, IMEPackage> action)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var path = Path.Combine(Path.GetTempPath(), $"MFE-ActorAssignment-{game}-{Guid.NewGuid():N}.pcc");
        MEPackageHandler.CreateAndSavePackage(path, game);
        try
        {
            using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            action(path, package);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
