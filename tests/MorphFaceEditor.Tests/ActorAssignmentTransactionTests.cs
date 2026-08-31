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
        new("LE1 placed morph assignment leaves the shared spawn type untouched", LeavesSharedLe1TypeUntouched),
        new("actor material assignment converts BMO arrays and protects non-target MICs", ConvertsAndProtectsMaterials),
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

            var target = CreateMic(package, "HMM_HED_PRO_Face_Mat", "HMM_HED_PRO_Face_Master");
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

            var target = CreateMic(package, "HMM_HED_PRO_Face_Mat", "HMM_HED_PRO_Face_Master");
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
            package.Save();
            var faceId = face.UIndex;
            var actorId = actor.UIndex;
            var targetId = target.UIndex;

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
        });
    }

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
        var root = package.CreatePackageImport($"MFEFixture_{name}");
        var parent = package.CreateImport("Material", parentName, root);
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
