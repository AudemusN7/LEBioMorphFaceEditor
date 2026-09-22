using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the material schema regression set.
public static class MaterialSchemaTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE1 human material schema contains male and female masters", SchemaContainsRecoveredFamilies),
        new("human material controls hide runtime alignment textures and place teeth in Mouth", HumanMaterialMetadataTaxonomy)
    ];

    private static void HumanMaterialMetadataTaxonomy()
    {
        var profile = new HumanMaleFeatureMetadataCatalog();
        foreach (var scarParameter in new[]
                 {
                     "HED_Scar_Colour_Vector", "Light_Scar_Color", "Light_Scar_Colour",
                     "LightScarColor"
                 })
        {
            TestAssert.Equal("Scar Emissive Color", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
                scarParameter, MaterialParameterKind.Vector)).Label);
        }

        // These are the parameter names used by the LE2/LE3 HMM and HMF Player
        // face masters, rather than names inferred from the visible row labels.
        foreach (var alignmentTexture in new[] { "HED_Face_Alignment_Emis", "HED_Face_Alignment_Norm" })
        {
            TestAssert.True(!profile.IsMaterialVisible(alignmentTexture, MaterialParameterKind.Texture),
                $"{alignmentTexture} remained visible in the material UI.");
        }
        foreach (var morphTexture in new[] { "HED_Norm", "HED_Norm_02" })
        {
            TestAssert.True(profile.IsMaterialVisible(morphTexture, MaterialParameterKind.Texture),
                $"{morphTexture} was incorrectly hidden with the runtime Alignment controls.");
        }

        foreach (var scalpTexture in new[]
                 {
                     "HED_Scalp_Diff", "HED_Scalp_Spec", "HED_Scalp_Norm", "HED_Tang",
                     "HED_Scalp_SpecShift", "HED_Scalp_SpecShift2"
                 })
        {
            TestAssert.Equal("head", profile.GetMaterialCategory(
                scalpTexture, MaterialParameterKind.Texture));
        }
        TestAssert.Equal("mouth", profile.GetMaterialCategory("HED_Teeth_Diff", MaterialParameterKind.Texture));

        var profiles = MorphFaceProfileRegistry.CreateDefault().Profiles;
        foreach (var key in new[]
                 {
                     "le2-human-male", "le2-human-female", "le3-human-male", "le3-human-female"
                 })
        {
            AssertMaterialControlProjection(profiles.Single(value => value.Key == key).UiProfile, key);
        }
        AssertMaterialControlProjection(new DetachedMeshFeatureMetadataCatalog(), "le3-mesh", scoped: true);
    }

    private static void AssertMaterialControlProjection(
        IHeadEditorUiProfile profile,
        string route,
        bool scoped = false)
    {
        var identity = TestFixtures.CreateIdentity($"{route}-face-material", "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "HMM_HED_PROCustom_MASTER_FACE_MAT",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4> { ["Light_Scar_Color"] = Vector4.One },
            new Dictionary<string, MaterialTextureBinding>())
        {
            SupportedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HED_Face_Alignment_Emis", "HED_Face_Alignment_Norm",
                "HED_Norm", "HED_Norm_02", "HED_Teeth_Diff", "HED_Scalp_Diff"
            },
            ParameterScopeKey = scoped ? "human" : null,
            ParameterScopeLabel = scoped ? "Human" : null
        };
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [material.Key] = material
            }));
        using var reader = new MorphFacePackageReader();
        using var editor = new MaterialEditorViewModel(
            session,
            new WpfHdrColorDialogService(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "fixture.pcc", [], _ => { }, profile);
        var textureNames = editor.Textures.Select(value => editor.SourceParameterName(value.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        TestAssert.True(!textureNames.Contains("HED_Face_Alignment_Emis") &&
                        !textureNames.Contains("HED_Face_Alignment_Norm"),
            $"{route} still projected runtime Alignment texture rows.");
        TestAssert.True(textureNames.Contains("HED_Norm") && textureNames.Contains("HED_Norm_02"),
            $"{route} hid the editable morph face-normal controls.");
        TestAssert.Equal("mouth", editor.Textures.Single(value =>
            editor.SourceParameterName(value.Name) == "HED_Teeth_Diff").CategoryKey);
        TestAssert.Equal("head", editor.Textures.Single(value =>
            editor.SourceParameterName(value.Name) == "HED_Scalp_Diff").CategoryKey);
        TestAssert.True(editor.Vectors.Any(value => value.Label.EndsWith(
                "Scar Emissive Color", StringComparison.Ordinal)),
            $"{route} lost the editable scar emissive control.");
    }

    private static void SchemaContainsRecoveredFamilies()
    {
        foreach (var family in new[]
                 {
                     HeadMaterialFamily.Skin,
                     HeadMaterialFamily.Scalp,
                     HeadMaterialFamily.Eyes,
                     HeadMaterialFamily.Lashes,
                     HeadMaterialFamily.Hair,
                     HeadMaterialFamily.AsariSkin,
                     HeadMaterialFamily.SalarianSkin,
                     HeadMaterialFamily.SalarianEyes,
                     HeadMaterialFamily.TurianSkin,
                     HeadMaterialFamily.TurianEyes,
                     HeadMaterialFamily.BatarianSkin,
                     HeadMaterialFamily.KroganSkin,
                     HeadMaterialFamily.KroganEyes
                 })
        {
            TestAssert.True(HumanMaterialProfiles.Definitions.Any(value => value.Family == family), $"No schema entries exist for {family}.");
        }
        TestAssert.Equal(HeadMaterialFamily.Eyes, HumanMaterialProfiles.ClassifyMaster("HMM_EYE_MASTER_OVRD_MAT"));
        TestAssert.Equal(HeadMaterialFamily.Eyes, HumanMaterialProfiles.ClassifyMaster("HMF_EYE_MASTER_OVRD_MAT"));
        TestAssert.Equal(HeadMaterialFamily.Skin, HumanMaterialProfiles.ClassifyMaster("HMF_HED_PRO_MASTER_FACE_MAT"));
        TestAssert.Equal(HeadMaterialFamily.Hair, HumanMaterialProfiles.ClassifyMaster("HMN_HED_PRO_MASTER_ADDN_HAIR_MAT"));
        TestAssert.Equal(HeadMaterialFamily.MaskedHair, HumanMaterialProfiles.ClassifyMaster("HMM_HIR_PROShort01_MAT_1a"));
        TestAssert.Equal(HeadMaterialFamily.SalarianSkin, HumanMaterialProfiles.ClassifyMaster("SAL_HED_PRO_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.SalarianEyes, HumanMaterialProfiles.ClassifyMaster("SAL_HED_EYE_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.BatarianSkin, HumanMaterialProfiles.ClassifyMaster("BAT_HED_PRO_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.KroganSkin, HumanMaterialProfiles.ClassifyMaster("KRO_HED_PRO_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.KroganEyes, HumanMaterialProfiles.ClassifyMaster("KRO_HED_EYE_MASTER_MAT"));
        TestAssert.Equal(TextureRole.Other, HumanMaterialProfiles.Describe(
            "HED_Makeup_Mask", MaterialParameterKind.Texture).TextureRole);
        foreach (var customPlayerParameter in new[]
                 {
                     ("HED_Brow", MaterialParameterKind.Texture),
                     ("HED_Scar", MaterialParameterKind.Texture),
                     ("HED_Custom_Scar_Scalar", MaterialParameterKind.Scalar),
                     ("HED_Scar_Diffuse_Scalar", MaterialParameterKind.Scalar),
                     ("HED_Scar_Vector", MaterialParameterKind.Vector)
                 })
        {
            var definition = HumanMaterialProfiles.Describe(
                customPlayerParameter.Item1,
                customPlayerParameter.Item2);
            TestAssert.Equal(HeadMaterialFamily.Skin, definition.Family);
            TestAssert.True(!definition.Description!.Contains("not in the recovered", StringComparison.OrdinalIgnoreCase),
                $"{customPlayerParameter.Item1} still fell through to the unknown-parameter tooltip.");
        }
        var additionalHair = HumanMaterialProfiles.Describe("HAIR_ADDN_Diff", MaterialParameterKind.Texture);
        TestAssert.Equal(TextureRole.Diffuse, additionalHair.TextureRole);
        TestAssert.Equal(TextureColorSpace.Srgb, additionalHair.ColorSpace);
        TestAssert.Equal(HeadMaterialBlendMode.Translucent, HumanMaterialProfiles.BlendMode(HeadMaterialFamily.Hair));
        TestAssert.Equal(HeadMaterialBlendMode.Masked, HumanMaterialProfiles.BlendMode(HeadMaterialFamily.MaskedHair));
        TestAssert.True(!HumanMaterialProfiles.IsTwoSided(HeadMaterialFamily.MaskedHair),
            "PROShort01 was incorrectly classified as two-sided translucent-card hair.");
    }
}

