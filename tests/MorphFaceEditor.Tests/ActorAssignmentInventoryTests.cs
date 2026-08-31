using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

/// <summary>Synthetic package coverage for the read-only first-stage actor assignment inventory.</summary>
public static class ActorAssignmentInventoryTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("actor inventory filters supported class families and defaults", FiltersSupportedClassFamilies),
        new("actor inventory preserves authored identity precedence and search aliases", PreservesIdentityAndSearch),
        new("LE1 actor inventory separates local appearance owners from spawn templates", SeparatesLe1Owners),
        new("actor inventory scopes morph and material profile compatibility independently", ScopesOperationCompatibility),
        new("actor inventory discovers inherited components and safe MICs deterministically", DiscoversMaterialsDeterministically),
        new("actor inventory honors approved shared alien material schemas", HonorsSharedAlienMaterialSchemas)
    ];

    private static void FiltersSupportedClassFamilies()
    {
        foreach (var (game, profile, expectedClasses) in new[]
                 {
                     (MEGame.LE1, "le1-human-male", new[] { "BioPawn", "BioPawnChallengeScaledType" }),
                     (MEGame.LE2, "le2-human-male", new[] { "BioPawn", "SFXSkeletalMeshActor", "SFXSkeletalMeshActorMAT" }),
                     (MEGame.LE3, "le3-human-male", new[] { "BioPawn", "SFXStuntActor", "SFXSkeletalMeshActor", "SFXSkeletalMeshActorMAT" })
                 })
        {
            WithPackage(game, (path, package) =>
            {
                var face = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
                foreach (var className in expectedClasses)
                {
                    package.CreateExport($"Candidate_{className}", className, indexed: false);
                }
                package.CreateExport("SetMorphHead0", "SFXSeqAct_SetMorphHead", indexed: false);
                package.CreateExport("Default__BioPawn", "BioPawn", indexed: false);

                // Exercise an actual object-database subclass where the game exposes one.
                var subclass = GlobalUnrealObjectInfo.GetNonAbstractDerivedClassesOf("BioPawn", game)
                    .FirstOrDefault(info => !expectedClasses.Contains(info.ClassName, StringComparer.OrdinalIgnoreCase));
                if (subclass is not null)
                {
                    package.CreateExport("DerivedPawn", subclass.ClassName, indexed: false);
                }
                package.Save();

                var inventory = new ActorAssignmentInventoryService((_, _) => null)
                    .Read(path, face.UIndex.ToString(), profile);
                foreach (var className in expectedClasses)
                {
                    TestAssert.True(inventory.Candidates.Any(candidate =>
                            candidate.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase)),
                        $"{game} did not include supported class {className}.");
                }
                if (subclass is not null)
                {
                    TestAssert.True(inventory.Candidates.Any(candidate => candidate.ObjectName == "DerivedPawn"),
                        $"{game} did not include BioPawn subclass {subclass.ClassName}.");
                }
                TestAssert.True(inventory.Candidates.All(candidate => candidate.ObjectName != "SetMorphHead0"),
                    $"{game} included a Kismet morph override.");
                TestAssert.True(inventory.Candidates.All(candidate => candidate.ObjectName != "Default__BioPawn"),
                    $"{game} included a class-default object.");
            });
        }
    }

    private static void PreservesIdentityAndSearch()
    {
        WithPackage(MEGame.LE2, (path, package) =>
        {
            var face = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var actorType = package.CreateExport("GarrusActorType", "BioPawnType", indexed: false);
            actorType.WriteProperty(new StringRefProperty(4242, "ActorGameNameStrRef"));
            actorType.WriteProperty(new NameProperty("TypeAlias", "UniqueTag"));
            var archetype = package.CreateExport("Default__BioPawn", "BioPawn", indexed: false);
            archetype.WriteProperty(new NameProperty("InheritedGarrus", "Tag"));
            var actor = package.CreateExport("BioPawn_12", "BioPawn", indexed: false);
            actor.Archetype = archetype;
            actor.WriteProperty(new NameProperty("NormandyGarrus", "m_nmUniqueTag"));
            actor.WriteProperty(new ObjectProperty(actorType, "ActorType"));
            var inheritedOnly = package.CreateExport("BioPawn_13", "BioPawn", indexed: false);
            inheritedOnly.Archetype = archetype;
            inheritedOnly.WriteProperty(new ObjectProperty(actorType, "ActorType"));
            package.Save();

            var inventory = new ActorAssignmentInventoryService((_, id) => id == 4242 ? "Garrus Vakarian" : null)
                .Read(path, face.UIndex.ToString(), "le2-turian");
            var candidate = inventory.Candidates.Single(value => value.ObjectName == actor.ObjectNameString);
            var inheritedCandidate = inventory.Candidates.Single(value => value.ObjectName == inheritedOnly.ObjectNameString);

            TestAssert.Equal("NormandyGarrus", candidate.DisplayName);
            TestAssert.Equal("InheritedGarrus (inherited)", inheritedCandidate.DisplayName);
            TestAssert.True(candidate.IdentityEvidence.Any(value =>
                    value.Kind == ActorIdentityEvidenceKind.InheritedTag && value.Value == "InheritedGarrus"),
                "Inherited actor tag was not retained as separate evidence.");
            TestAssert.True(candidate.IdentityEvidence.Any(value =>
                    value.Kind == ActorIdentityEvidenceKind.GameName && value.Value == "Garrus Vakarian"),
                $"Actor type game-name metadata was not resolved. Evidence: {string.Join("; ", candidate.IdentityEvidence.Select(value => $"{value.Kind}={value.Value}"))}");
            foreach (var term in new[]
                     {
                         "NormandyGarrus", "InheritedGarrus", "TypeAlias", "Garrus Vakarian",
                         "GarrusActorType", actorType.UIndex.ToString(), actorType.InstancedFullPath,
                         archetype.InstancedFullPath, actor.ClassName, actor.UIndex.ToString(), actor.InstancedFullPath
                     })
            {
                TestAssert.True(candidate.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase),
                    $"Search evidence omitted '{term}'.");
            }
        });
    }

    private static void SeparatesLe1Owners()
    {
        WithPackage(MEGame.LE1, (path, package) =>
        {
            var selected = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var oldFace = package.CreateExport("OldFace", "BioMorphFace", indexed: false);
            var head = CreateHeadComponent(package, "HMM_HED_PROBase_MDL", "HeadMesh0");
            oldFace.WriteProperty(new ObjectProperty(
                head.GetProperty<ObjectProperty>("SkeletalMesh")!.ResolveToEntry(package), "m_oBaseHead"));
            var type = package.CreateExport("HenchType", "BioPawnChallengeScaledType", indexed: false);
            type.WriteProperty(new ObjectProperty(oldFace, "m_oMorphFace"));
            var appearance = package.CreateExport("PawnAppearance", "BioInterface_Appearance_Pawn", indexed: false);
            appearance.WriteProperty(new ObjectProperty(oldFace, "m_oMorphFace"));
            var behavior = package.CreateExport("PawnBehavior", "BioPawnBehavior", indexed: false);
            behavior.WriteProperty(new ObjectProperty(appearance, "m_oAppearanceType"));
            behavior.WriteProperty(new ObjectProperty(type, "m_oActorType"));
            var pawn = package.CreateExport("PlacedPawn", "BioPawn", indexed: false);
            pawn.WriteProperty(new ObjectProperty(behavior, "m_oBehavior"));
            pawn.WriteProperty(new ObjectProperty(head, "m_oHeadMesh"));

            var sharedOnlyBehavior = package.CreateExport("SharedOnlyBehavior", "BioPawnBehavior", indexed: false);
            sharedOnlyBehavior.WriteProperty(new ObjectProperty(type, "m_oActorType"));
            var sharedOnlyPawn = package.CreateExport("SharedOnlyPawn", "BioPawn", indexed: false);
            sharedOnlyPawn.WriteProperty(new ObjectProperty(sharedOnlyBehavior, "oBioComponent"));
            sharedOnlyPawn.WriteProperty(new ObjectProperty(head, "m_oHeadMesh"));
            var factory = package.CreateExport("HenchFactory", "ActorFactory", indexed: false);
            factory.WriteProperty(new ObjectProperty(type, "SpawnTemplate"));
            package.Save();

            var inventory = new ActorAssignmentInventoryService((_, _) => null)
                .Read(path, selected.UIndex.ToString(), "le1-human-male");
            var placed = inventory.Candidates.Single(value => value.ObjectName == "PlacedPawn");
            var sharedOnly = inventory.Candidates.Single(value => value.ObjectName == "SharedOnlyPawn");
            var spawn = inventory.Candidates.Single(value => value.ObjectName == "HenchType");

            TestAssert.Equal(ActorAssignmentTargetKind.PlacedActor, placed.TargetKind);
            TestAssert.Equal(appearance.UIndex, placed.MorphTarget!.OwnerUIndex);
            TestAssert.Equal("m_oMorphFace", placed.MorphTarget.PropertyName);
            TestAssert.Equal(oldFace.InstancedFullPath, placed.MorphTarget.CurrentMorphPath);
            TestAssert.True(placed.CanAssignMorph, "Compatible placed LE1 pawn was not morph-eligible.");
            TestAssert.True(!sharedOnly.CanAssignMorph && sharedOnly.MorphTarget is null,
                "LE1 pawn silently fell back from its missing local appearance to the shared type.");

            TestAssert.Equal(ActorAssignmentTargetKind.SpawnTemplate, spawn.TargetKind);
            TestAssert.Equal(type.UIndex, spawn.MorphTarget!.OwnerUIndex);
            TestAssert.True(spawn.CanAssignMorph &&
                            spawn.HeadMeshPath!.Contains("HMM_HED_PROBase_MDL", StringComparison.Ordinal),
                "Type-only spawn template did not use its current morph base head as compatibility evidence.");
            TestAssert.True(spawn.ReferencedBy.Any(reference => reference.ObjectNameOrPathContains("HenchFactory")) &&
                            spawn.ReferencedBy.Any(reference => reference.ObjectNameOrPathContains("PawnBehavior")),
                $"Spawn-template fan-out evidence omitted local factory/behavior referrers. Found: {string.Join("; ", spawn.ReferencedBy.Select(value => $"{value.InstancedPath}:{value.PropertyPath}"))}");
            TestAssert.True(spawn.Warnings.Any(value => value.Contains("every actor", StringComparison.OrdinalIgnoreCase)),
                "Spawn-template fan-out warning was not emitted.");
        });
    }

    private static void ScopesOperationCompatibility()
    {
        WithPackage(MEGame.LE2, (path, package) =>
        {
            var selected = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var incompatibleHead = CreateHeadComponent(package, "HMF_HED_PROBase_MDL", "HeadMesh0");
            var hmmMic = CreateMic(package, "HMM_HED_PRO_Face_Mat", "HMM_HED_PRO_Face_Master");
            incompatibleHead.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(hmmMic)], "Materials"));
            var actor = package.CreateExport("MixedEvidenceActor", "BioPawn", indexed: false);
            actor.WriteProperty(new ObjectProperty(incompatibleHead, "HeadMesh"));
            package.Save();

            var candidate = new ActorAssignmentInventoryService((_, _) => null)
                .Read(path, selected.UIndex.ToString(), "le2-human-male")
                .Candidates.Single(value => value.ObjectName == "MixedEvidenceActor");

            TestAssert.True(!candidate.CanAssignMorph &&
                            candidate.MorphIneligibilityReason!.Contains("le2-human-female", StringComparison.Ordinal),
                "Recognized incompatible head mesh did not block morph assignment.");
            TestAssert.True(candidate.CanAssignMaterials && candidate.SafeMaterialCount == 1,
                "A compatible local MIC was suppressed by independently incompatible head geometry.");
        });

        WithPackage(MEGame.LE2, (path, package) =>
        {
            var selected = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var head = CreateHeadComponent(package, "HMM_HED_PROBase_MDL", "HeadMesh0");
            var customEye = CreateMic(package, "Custom_Eye_Instance", "Custom_Eye_Master");
            head.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(customEye)], "Materials"));
            var actor = package.CreateExport("CustomMaterialActor", "BioPawn", indexed: false);
            actor.WriteProperty(new ObjectProperty(head, "HeadMesh"));
            package.Save();

            var candidate = new ActorAssignmentInventoryService((_, _) => null)
                .Read(path, selected.UIndex.ToString(), "le2-human-male")
                .Candidates.Single(value => value.ObjectName == "CustomMaterialActor");
            TestAssert.True(candidate.CanAssignMorph,
                "A custom/unknown material incorrectly blocked compatible morph assignment.");
            TestAssert.True(!candidate.CanAssignMaterials && candidate.SkippedMaterials.Count == 1,
                "Custom eye material was guessed into a writable profile.");
        });
    }

    private static void DiscoversMaterialsDeterministically()
    {
        WithPackage(MEGame.LE3, (path, package) =>
        {
            var selected = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var validFace = CreateMic(package, "HMM_HED_PRO_Face_Mat", "HMM_HED_PRO_Face_Master");
            var body = CreateMic(package, "HMM_ARM_Casual_Mat", "HMM_ARM_Casual_Master");
            var customEye = CreateMic(package, "Custom_Eye_Instance", "Custom_Eye_Master");
            var incompatible = CreateMic(package, "ASA_HED_PRO_Face_Mat", "ASA_HED_PRO_Face_Master");
            var importRoot = package.CreatePackageImport("MFEFixture_Imported");
            var imported = package.CreateImport("MaterialInstanceConstant", "HMM_HED_Imported_Mat", importRoot);

            var head = CreateHeadComponent(package, "HMM_HED_PROBase_MDL", "HeadMesh0");
            head.WriteProperty(new ArrayProperty<ObjectProperty>(
                [
                    new ObjectProperty(validFace),
                    new ObjectProperty(body),
                    new ObjectProperty(customEye),
                    new ObjectProperty(incompatible),
                    new ObjectProperty(imported)
                ], "Materials"));
            var archetype = package.CreateExport("Default__BioPawn", "BioPawn", indexed: false);
            archetype.WriteProperty(new ObjectProperty(head, "HeadMesh"));
            var actor = package.CreateExport("InheritedHeadPawn", "BioPawn", indexed: false);
            actor.Archetype = archetype;
            package.Save();

            var candidate = new ActorAssignmentInventoryService((_, _) => null)
                .Read(path, selected.UIndex.ToString(), "le3-human-male")
                .Candidates.Single(value => value.ObjectName == "InheritedHeadPawn");

            TestAssert.True(candidate.Components.Single().Inherited,
                "Actor archetype component was not marked inherited.");
            TestAssert.Equal(1, candidate.MaterialTargets.Count);
            TestAssert.Equal(validFace.UIndex, candidate.MaterialTargets[0].UIndex);
            TestAssert.Equal(HeadMaterialFamily.Skin, candidate.MaterialTargets[0].Family);
            TestAssert.True(candidate.MaterialTargets[0].ParentChain.Count == 2 &&
                            candidate.MaterialTargets[0].ParentChain[^1].InstancedPath.Contains(
                                "HMM_HED_PRO_Face_Master", StringComparison.Ordinal),
                "Complete MIC parent-chain evidence did not retain the final import path.");
            TestAssert.Equal(4, candidate.SkippedMaterials.Count);
            TestAssert.True(candidate.SkippedMaterials.Any(value => value.UIndex == body.UIndex &&
                                                                    value.Family == HeadMaterialFamily.Accessory),
                "Body MIC was not excluded individually.");
            TestAssert.True(candidate.SkippedMaterials.Any(value => value.UIndex == customEye.UIndex &&
                                                                    value.Reason.Contains("not safe to infer", StringComparison.OrdinalIgnoreCase)),
                "Custom eye MIC did not retain its scoped skip reason.");
            TestAssert.True(candidate.SkippedMaterials.Any(value => value.UIndex == incompatible.UIndex &&
                                                                    value.Reason.Contains("incompatible", StringComparison.OrdinalIgnoreCase)),
                "Profile-conflicting MIC was not excluded individually.");
            TestAssert.True(candidate.SkippedMaterials.Any(value => value.UIndex == imported.UIndex &&
                                                                    value.Reason.Contains("Imported", StringComparison.OrdinalIgnoreCase)),
                "Imported MIC was not retained as a read-only skipped slot.");
            TestAssert.True(candidate.CanAssignMaterials && candidate.SafeMaterialCount == 1,
                "Valid face MIC was suppressed by unsafe sibling slots.");
        });
    }

    private static void HonorsSharedAlienMaterialSchemas()
    {
        WithPackage(MEGame.LE3, (path, package) =>
        {
            var selected = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var tufHead = CreateHeadComponent(package, "TUF_HED_PROBase_MDL", "TufHeadMesh0");
            var tufMic = CreateMic(package, "TUF_HED_PRO_Face_Mat", "TUR_HED_PRO_Face_Master");
            tufHead.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(tufMic)], "Materials"));
            var tufActor = package.CreateExport("FemaleTurianActor", "BioPawn", indexed: false);
            tufActor.WriteProperty(new ObjectProperty(tufHead, "HeadMesh"));

            var vorchaHead = CreateHeadComponent(package, "ALN_HED_PROBase_MDL", "VorchaHeadMesh0");
            var vorchaEye = CreateMic(package, "ALN_EYE_Instance", "TUR_HED_EYE_Master");
            vorchaHead.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(vorchaEye)], "Materials"));
            var vorchaActor = package.CreateExport("VorchaActor", "BioPawn", indexed: false);
            vorchaActor.WriteProperty(new ObjectProperty(vorchaHead, "HeadMesh"));
            package.Save();

            var service = new ActorAssignmentInventoryService((_, _) => null);
            var tuf = service.Read(path, selected.UIndex.ToString(), "le3-female-turian")
                .Candidates.Single(value => value.ObjectName == "FemaleTurianActor");
            var vorcha = service.Read(path, selected.UIndex.ToString(), "le3-vorcha")
                .Candidates.Single(value => value.ObjectName == "VorchaActor");

            TestAssert.True(tuf.CanAssignMaterials && tuf.MaterialTargets.Single().UIndex == tufMic.UIndex,
                "Female Turian local MIC backed by the approved Turian schema was rejected.");
            TestAssert.True(vorcha.CanAssignMaterials && vorcha.MaterialTargets.Single().UIndex == vorchaEye.UIndex,
                "LE3 Vorcha eye MIC backed by the approved Turian eye schema was rejected.");
        });
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
        var root = package.CreatePackageImport($"MFEFixture_{name}");
        var parent = package.CreateImport("Material", parentName, root);
        var mic = package.CreateExport(name, "MaterialInstanceConstant", indexed: false);
        mic.WriteProperty(new ObjectProperty(parent, "Parent"));
        return mic;
    }

    private static void WithPackage(MEGame game, Action<string, IMEPackage> action)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var path = Path.Combine(Path.GetTempPath(), $"MFE-ActorInventory-{game}-{Guid.NewGuid():N}.pcc");
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

    private static bool ObjectNameOrPathContains(this ActorAssignmentReference reference, string value) =>
        reference.InstancedPath.Contains(value, StringComparison.OrdinalIgnoreCase);
}
