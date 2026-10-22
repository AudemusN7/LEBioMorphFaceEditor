using MorphFaceEditor.Core.Domain;
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
        new("Vorcha eye glow remains alpha masked", EyeGlowRemainsAlphaMasked)
    ];

    private static void EyeGlowRemainsAlphaMasked()
    {
        var noAlpha = CreateVorchaEye("vorcha-eye", 0);
        var alpha = CreateVorchaEye("vorcha-eye", 255);
        AssertMaterialsRenderDifferently(noAlpha, alpha,
            "ALN eye diffuse alpha did not gate EYE_Glow emissive output.");
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
