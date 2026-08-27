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
        new("Asari tattoo selector channels cannot paint raw colours", AsariTattooSelectorsOnlyControlCoverage),
        new("LE3 Asari makeup colours and strength participate when region weights are enabled", Le3AsariMakeupControlsParticipate),
        new("Asari complexion mask applies its normal perturbation", AsariComplexionMaskAppliesNormal),
        new("Asari complexion colour uses addition alpha", AsariComplexionColourUsesAlpha),
        new("Asari fixed skin-noise texture participates in composition", AsariSkinNoiseParticipates)
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

    private static void AsariTattooSelectorsOnlyControlCoverage()
    {
        var redPattern = CreateAsariMaterial(
            "asari", [255, 0, 0, 255], new Vector4(1, 0, 0, 0), 1);
        var greenPattern = CreateAsariMaterial(
            "asari", [0, 255, 0, 255], new Vector4(0, 1, 0, 0), 1);
        AssertMaterialsRenderEqually(
            redPattern,
            greenPattern,
            "ASA_HED_Tatt RGB leaked into output colour instead of acting only as pattern coverage.");
    }

    private static void Le3AsariMakeupControlsParticipate()
    {
        var redLips = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0,
            makeup: [255, 0, 0, 0], makeupStrength: 1,
            makeupLips: new Vector4(1, 0, 0, 1),
            makeupBlender: new Vector4(1, 0, 0, 0), isLe3: true);
        var greenLips = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0,
            makeup: [255, 0, 0, 0], makeupStrength: 1,
            makeupLips: new Vector4(0, 1, 0, 1),
            makeupBlender: new Vector4(1, 0, 0, 0), isLe3: true);
        AssertMaterialsRenderDifferently(
            redLips,
            greenLips,
            "The LE3 Asari lip-makeup colour remained inert with its red region weight enabled.");

        var redEyes = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0,
            makeup: [0, 255, 0, 0], makeupStrength: 1,
            makeupEyes: new Vector4(1, 0, 0, 1),
            makeupBlender: new Vector4(0, 1, 0, 0), isLe3: true);
        var blueEyes = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0,
            makeup: [0, 255, 0, 0], makeupStrength: 1,
            makeupEyes: new Vector4(0, 0, 1, 1),
            makeupBlender: new Vector4(0, 1, 0, 0), isLe3: true);
        AssertMaterialsRenderDifferently(
            redEyes,
            blueEyes,
            "The LE3 Asari eye-makeup colour remained inert with its green region weight enabled.");

        var disabled = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0,
            makeup: [255, 0, 0, 0], makeupStrength: 0,
            makeupLips: new Vector4(1, 0, 0, 1),
            makeupBlender: new Vector4(1, 0, 0, 0), isLe3: true);
        AssertMaterialsRenderDifferently(
            disabled,
            redLips,
            "Makeup Strength remained inert after the corresponding LE3 Asari region weight was enabled.");
    }

    private static void AsariComplexionMaskAppliesNormal()
    {
        var flat = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0, [128, 128, 255, 255], 1);
        var tilted = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0, [255, 128, 255, 255], 1);
        AssertMaterialsRenderDifferently(
            flat,
            tilted,
            "The mask-selected ASA_HED_Addn normal channels did not affect lighting.");
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

    private static void AsariSkinNoiseParticipates()
    {
        var white = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0, skinNoise: [255, 255, 255, 255]);
        var blue = CreateAsariMaterial(
            "asari", [0, 0, 0, 255], Vector4.Zero, 0, skinNoise: [0, 0, 255, 255]);
        AssertMaterialsRenderDifferently(
            white,
            blue,
            "The fixed SkinNoise_Diffuse texture was not bound to the Asari composition.");
    }
}
