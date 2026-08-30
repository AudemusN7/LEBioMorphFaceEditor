using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

public static class VorchaMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Vorcha masters use dedicated LE2 families and shared LE3 Turian eyes", MastersUseRecoveredFamilies),
        new("Vorcha region mask and tattoo selector remain live", RegionAndTattooSelectorsRemainLive),
        new("Vorcha eye glow remains alpha masked", EyeGlowRemainsAlphaMasked),
        new("Vorcha material labels hide editor shorthand", MaterialLabelsAreUserFacing)
    ];

    private static void MastersUseRecoveredFamilies()
    {
        TestAssert.Equal(HeadMaterialFamily.VorchaSkin,
            HumanMaterialProfiles.ClassifyMaster("ALN_HED_PRO_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.VorchaEyes,
            HumanMaterialProfiles.ClassifyMaster("ALN_EYE_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.TurianEyes,
            HumanMaterialProfiles.ClassifyMaster("TUR_HED_EYE_MASTER_MAT"));
    }

    private static void RegionAndTattooSelectorsRemainLive()
    {
        var skin = CreateVorchaSkin("vorcha", [255, 0, 0, 255], [0, 0, 0, 255]);
        var muzzle = CreateVorchaSkin("vorcha", [0, 255, 0, 255], [0, 0, 0, 255]);
        AssertMaterialsRenderDifferently(skin, muzzle,
            "ALN_HED_Tint did not select distinct skin and muzzle colour branches.");

        var tattooOff = CreateVorchaSkin("vorcha", [255, 0, 0, 255], [0, 0, 0, 255]);
        var tattooOn = CreateVorchaSkin("vorcha", [255, 0, 0, 255], [255, 0, 0, 255]);
        AssertMaterialsRenderDifferently(tattooOff, tattooOn,
            "ALN_HED_Tatt and Tattoo_Chooser did not activate Tattoo_Color.");
    }

    private static void EyeGlowRemainsAlphaMasked()
    {
        var noAlpha = CreateVorchaEye("vorcha-eye", 0);
        var alpha = CreateVorchaEye("vorcha-eye", 255);
        AssertMaterialsRenderDifferently(noAlpha, alpha,
            "ALN eye diffuse alpha did not gate EYE_Glow emissive output.");
    }

    private static void MaterialLabelsAreUserFacing()
    {
        var catalog = new VorchaFeatureMetadataCatalog();
        var diffuse = catalog.DescribeMaterial(HumanMaterialProfiles.Describe(
            "TUR_HED_Diff", MaterialParameterKind.Texture, HeadMaterialFamily.VorchaSkin));
        var power = catalog.DescribeMaterial(HumanMaterialProfiles.Describe(
            "ALN_HED_Spwr_Skin_Scalar", MaterialParameterKind.Scalar, HeadMaterialFamily.VorchaSkin));
        var teeth = catalog.DescribeMaterial(HumanMaterialProfiles.Describe(
            "ALN_HED_Diff_Tint_Teeth", MaterialParameterKind.Vector, HeadMaterialFamily.VorchaSkin));
        var le2EyeDiffuse = catalog.DescribeMaterial(HumanMaterialProfiles.Describe(
            "ALN_HED_Diff", MaterialParameterKind.Texture, HeadMaterialFamily.VorchaEyes));
        var le3Reflection = catalog.DescribeMaterial(HumanMaterialProfiles.Describe(
            "CubeMap_Intensity", MaterialParameterKind.Scalar, HeadMaterialFamily.TurianEyes));

        TestAssert.Equal("Diffuse Texture", diffuse.Label);
        TestAssert.Equal("Skin Specular Power", power.Label);
        TestAssert.Equal("Teeth / Bone Colour", teeth.Label);
        TestAssert.Equal(VorchaFeatureMetadataCatalog.Eyes, le2EyeDiffuse.Group);
        TestAssert.Equal("Eye Diffuse Texture", le2EyeDiffuse.Label);
        TestAssert.Equal(VorchaFeatureMetadataCatalog.Eyes, le3Reflection.Group);
        TestAssert.Equal("Eye Reflection Strength", le3Reflection.Label);
        TestAssert.True(!diffuse.Label.StartsWith("TUR", StringComparison.OrdinalIgnoreCase) &&
                        !power.Label.Contains("Spwr", StringComparison.OrdinalIgnoreCase),
            "Vorcha user-facing labels leaked package/editor shorthand.");
    }

    private static HeadPreviewMaterial CreateVorchaSkin(string key, byte[] tint, byte[] tattoo)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase)
        {
            ["TUR_HED_Diff"] = CreateTexture("TUR_HED_Diff", [180, 160, 140, 255], TextureRole.Diffuse),
            ["ALN_HED_Norm"] = CreateTexture("ALN_HED_Norm", [128, 128, 255, 255], TextureRole.Normal),
            ["ALN_HED_Tint"] = CreateTexture("ALN_HED_Tint", tint, TextureRole.Mask),
            ["ALN_HED_Tatt"] = CreateTexture("ALN_HED_Tatt", tattoo, TextureRole.Mask)
        };
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.VorchaSkin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>
            {
                ["ALN_HED_Spwr_Skin_Scalar"] = 0.2f,
                ["ALN_HED_Spwr_Muzzle_Scalar"] = 0.5f
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new(0.8f, 0.15f, 0.05f, 1),
                ["ALN_HED_Diff_Tint_Muzzle"] = new(0.05f, 0.7f, 0.12f, 1),
                ["ALN_HED_Diff_Tint_Muzzle2"] = new(0.05f, 0.1f, 0.8f, 1),
                ["ALN_HED_Diff_Tint_Teeth"] = Vector4.One,
                ["Tattoo_Chooser"] = Vector4.UnitX,
                ["Tattoo_Color"] = new(0.8f, 0.02f, 0.7f, 1),
                ["ALN_HED_Spec_Colour"] = Vector4.Zero,
                ["Tmissive"] = Vector4.Zero,
                ["SkinLightScattering"] = Vector4.Zero
            }, textures);
    }

    private static HeadPreviewMaterial CreateVorchaEye(string key, byte alpha)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase)
        {
            ["ALN_HED_Diff"] = CreateTexture("ALN_HED_Diff", [80, 32, 16, alpha], TextureRole.Diffuse),
            ["Eye_Norm"] = CreateTexture("Eye_Norm", [128, 128, 255, 255], TextureRole.Normal)
        };
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.VorchaEyes, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>
            {
                ["EYE_Spec"] = 0,
                ["EYE_Spec_Power"] = 4,
                ["EYE_Glow_Intensity"] = 4
            },
            new Dictionary<string, Vector4>
            {
                ["EYE_Tint_Iris"] = Vector4.One,
                ["EYE_Glow"] = new(1, 0.2f, 0.01f, 1)
            }, textures);
    }
}
