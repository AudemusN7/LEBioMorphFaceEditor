using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;
using static MorphFaceEditor.Tests.SpeciesMaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the asari material regression set.
public static class AsariMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Asari tattoo blend zero preserves the composed skin", AsariTattooBlendZeroPreservesSkin),
        new("Asari complexion colour uses addition alpha", AsariComplexionColourUsesAlpha)
    ];

    private static void AsariTattooBlendZeroPreservesSkin()
    {
        var redPattern = CreateAsariMaterial(
            "asari", [255, 0, 0, 255], new Vector4(1, 0, 0, 0), 0);
        var greenPattern = CreateAsariMaterial(
            "asari", [0, 255, 0, 255], new Vector4(0, 1, 0, 0), 0);
        AssertMaterialsRenderEqually(
            redPattern,
            greenPattern,
            "Tatt_Blender=0 allowed a tattoo selector texture to alter the composed skin.");
    }

    private static void AsariComplexionColourUsesAlpha()
    {
        var blueZeroAlpha = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0,
            [128, 128, 255, 0], 1, complexionStrength: 1);
        var blackZeroAlpha = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0,
            [128, 128, 0, 0], 1, complexionStrength: 1);
        AssertMaterialsRenderEqually(
            blueZeroAlpha,
            blackZeroAlpha,
            "ASA_HED_Addn blue incorrectly gated complexion colour; its alpha channel is the compiled mask.");

        var opaque = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0,
            [128, 128, 0, 255], 1, complexionStrength: 1);
        AssertMaterialsRenderDifferently(
            blackZeroAlpha,
            opaque,
            "ASA_HED_Addn alpha did not gate complexion colour.");
    }


}
