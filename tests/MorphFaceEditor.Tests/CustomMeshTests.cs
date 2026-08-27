using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class CustomMeshTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("custom mesh profile is inferred from its materials", ProfileIsInferredFromMaterials),
        new("material-only sessions accept an empty morph payload", EmptyMorphPayloadUsesBaseGeometry)
    ];

    private static void ProfileIsInferredFromMaterials()
    {
        var materialIdentity = new AssetIdentity(
            "fixture.pcc",
            "BIOA_PRC2_HMM_Ahern.Materials.Ahern_Custom_Head",
            7,
            "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(materialIdentity),
            materialIdentity,
            "HMM_HED_PRO_MASTER_FACE_MAT",
            HeadMaterialFamily.Skin,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            new Dictionary<string, MaterialTextureBinding>());
        var overrides = new MorphFaceMaterialOverrides(
            TestFixtures.CreateIdentity("Ahern.MaterialOverride", "BioMaterialOverride"),
            [new ScalarMaterialOverride("HED_Norm_Blend", 0.5f)],
            [],
            []);

        var resolution = MorphFaceProfileRegistry.CreateDefault().Resolve(
            MorphFaceGame.LE1,
            "Ahern.CustomFace",
            "BIOA_PRC2_HMM_Ahern.Ahern_Custom_Head_MDL",
            overrides,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [material.Key] = material
            }));

        TestAssert.Equal("le1-human-male", resolution?.Profile.Key);
        TestAssert.True(resolution?.UsesCustomMesh == true,
            "The unknown base mesh was treated as a canonical PROMorph mesh.");
    }

    private static void EmptyMorphPayloadUsesBaseGeometry()
    {
        var mesh = TestFixtures.CreateMesh();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Ahern.CustomFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [],
            [],
            MorphFaceMaterialOverrides.Empty,
            [],
            []);

        var session = new MorphFaceEditingSession(
            document,
            mesh,
            [],
            profileName: "LE1 Human Male",
            geometryEditBlockReason: "Custom base mesh; geometry editing is unavailable.");

        TestAssert.True(!session.CanEdit, "A custom mesh unexpectedly enabled geometry editing.");
        TestAssert.Equal(mesh.Positions[0], session.Evaluation.Geometry.Positions[0]);
        var draft = session.CreateDraft(null, [], new MorphFaceMaterialOverrides(
            null,
            [new ScalarMaterialOverride("HED_Norm_Blend", 0.75f)],
            [],
            []));
        TestAssert.Equal(0, draft.MorphFeatures.Count);
        TestAssert.Equal(0, draft.FinalSkeleton.Count);
        TestAssert.Equal(0, draft.BakedLods.Count);
        TestAssert.Near(0.75f, draft.MaterialOverrides.Scalars.Single().Value, 0);
    }
}
