using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
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
        new("actor material assignment adds only skin vectors present on the source", AddsOnlyPresentBodySkinVectors),
        new("actor material assignment preserves target categories missing from the BMO", PreservesMissingMaterialCategories)
    ];

    private static void AssignsExactMorphOwner()
    {
        WithPackage(MEGame.LE3, (path, package) =>
        {
            var selected = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var old = package.CreateExport("OldFace", "BioMorphFace", indexed: false);
            var actor = CreateActorWithHead(package, "SFXStuntActor", "TargetActor", "HMM_HED_PROBase_MDL");
            actor.WriteProperty(new ObjectProperty(old, "MorphHead"));
            package.Save();
            var actorId = actor.UIndex;
            var selectedId = selected.UIndex;
            var oldId = old.UIndex;

            var result = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMorph(path, selectedId.ToString(), "le3-human-male", actorId);

            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            TestAssert.Equal(oldId, result.PreviousMorphUIndex);
            TestAssert.Equal(selectedId, result.NewMorphUIndex);
            TestAssert.Equal(selectedId,
                reopened.GetUExport(actorId).GetProperty<ObjectProperty>("MorphHead")!.Value);
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
        });
    }

    private static void ConvertsAndProtectsMaterials()
    {
        WithPackage(MEGame.LE2, (path, package) =>
        {
            var texture = package.CreateExport("SourceTexture", "Texture2D", indexed: false);
            var face = package.CreateExport("SelectedFace", "BioMorphFace", indexed: false);
            var bmo = package.CreateExport("SelectedFaceMaterials", "BioMaterialOverride", face, indexed: false);
            bmo.WriteProperty(new ArrayProperty<StructProperty>(
            [
                new StructProperty("TextureParameter", false,
                    new NameProperty("Diffuse", "nName"),
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
            var actor = package.CreateExport("TargetPawn", "BioPawn", indexed: false);
            actor.WriteProperty(new ObjectProperty(head, "HeadMesh"));
            package.Save();
            var faceId = face.UIndex;
            var actorId = actor.UIndex;
            var targetId = target.UIndex;
            var bodyId = body.UIndex;
            var textureId = texture.UIndex;
            var bodyBefore = body.Data.ToArray();

            var result = new ActorAssignmentService(new ActorAssignmentInventoryService((_, _) => null))
                .AssignMaterials(path, faceId.ToString(), "le2-human-male", actorId);

            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            var changed = reopened.GetUExport(targetId);
            var textureValue = changed.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")!.Single();
            var vectorValue = changed.GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues")!.Single();
            var scalars = changed.GetProperty<ArrayProperty<StructProperty>>("ScalarParameterValues")!;
            TestAssert.Equal(textureId, textureValue.GetProp<ObjectProperty>("ParameterValue")!.Value);
            TestAssert.Equal("Diffuse", textureValue.GetProp<NameProperty>("ParameterName")!.Value.Name);
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
            TestAssert.Equal(1, result.ChangedMaterials.Count);
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
