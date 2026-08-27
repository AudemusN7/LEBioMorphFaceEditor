using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;
using static MorphFaceEditor.Tests.SpeciesMaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the salarian material regression set.
public static class SalarianMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Salarian complexion colour uses addition alpha", SalarianComplexionColourUsesAlpha),
        new("Salarian complexion normal follows the selected region", SalarianComplexionNormalFollowsSelectedRegion),
        new("LE3 Salarian normals consume stored RGB instead of BC5 reconstruction", Le3SalarianNormalsConsumeRgb),
        new("Salarian skin scattering uses inverse surface-tint green", SalarianSkinScatteringUsesTintGreen),
        new("Salarian tattoo texture channels only select coverage", SalarianTattooChannelsOnlySelectCoverage),
        new("Salarian eye packed channels preserve their compiled roles", SalarianEyePackedChannelsPreserveRoles),
        new("Salarian eye Chrome and Visor cubes remain independent", SalarianEyeCubesRemainIndependent),
        new("LE1 Salarian specular map participates in direct lighting", SalarianSpecularMapParticipates)
    ];

    private static void SalarianComplexionColourUsesAlpha()
    {
        var blueZeroAlpha = CreateSalarianMaterial(
            "salarian", addition: [128, 128, 255, 0], additionBlend: 1);
        var blackZeroAlpha = CreateSalarianMaterial(
            "salarian", addition: [128, 128, 0, 0], additionBlend: 1);
        AssertMaterialsRenderEqually(
            blueZeroAlpha,
            blackZeroAlpha,
            "SAL_HED_Addn blue incorrectly gated complexion colour; the compiled shader samples R/G/A.");

        var opaque = CreateSalarianMaterial(
            "salarian", addition: [128, 128, 0, 255], additionBlend: 1);
        AssertMaterialsRenderDifferently(
            blackZeroAlpha,
            opaque,
            "SAL_HED_Addn alpha did not gate complexion colour.");
    }

    private static void SalarianComplexionNormalFollowsSelectedRegion()
    {
        var selectedFlat = CreateSalarianMaterial(
            "salarian", addition: [128, 128, 0, 0]);
        var selectedTilted = CreateSalarianMaterial(
            "salarian", addition: [255, 128, 0, 0]);
        AssertMaterialsRenderDifferently(
            selectedFlat,
            selectedTilted,
            "SAL_HED_Addn RG did not perturb normals inside the selected complexion region.");

        var unselectedFlat = CreateSalarianMaterial(
            "salarian", addition: [128, 128, 0, 0], complexionMask: Vector4.Zero);
        var unselectedTilted = CreateSalarianMaterial(
            "salarian", addition: [255, 128, 0, 0], complexionMask: Vector4.Zero);
        AssertMaterialsRenderEqually(
            unselectedFlat,
            unselectedTilted,
            "SAL_HED_Addn RG perturbed normals outside the selected complexion region.");
    }

    private static void Le3SalarianNormalsConsumeRgb()
    {
        var le2 = CreateSalarianMaterial(
            "salarian", normal: [224, 128, 160, 255], isLe3: false);
        var le3 = CreateSalarianMaterial(
            "salarian", normal: [224, 128, 160, 255], isLe3: true);
        var (renderer, camera) = CreateTriangleRenderer(le2);
        using (renderer)
        {
            var reconstructed = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [le3.Key] = le3 });
            var storedRgb = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            TestAssert.True(reconstructed.Zip(storedRgb)
                    .Count(pair => Math.Abs(pair.First - pair.Second) > 3) > 100,
                "The LE3 SAL RGB normal path collapsed to the LE2 BC5 reconstruction.");
        }
    }

    private static void SalarianSkinScatteringUsesTintGreen()
    {
        var disabled = CreateSalarianMaterial(
            "salarian", tint: [0, 0, 255, 255], scattering: Vector4.Zero);
        var enabled = CreateSalarianMaterial(
            "salarian", tint: [0, 0, 255, 255], scattering: new Vector4(8, 8, 8, 1));
        AssertMaterialsRenderDifferently(
            disabled,
            enabled,
            "SkinLightScattering remained inert where inverse SAL_HED_Tint.G selected the surface.");

        var excludedDisabled = CreateSalarianMaterial(
            "salarian", tint: [0, 255, 255, 255], scattering: Vector4.Zero);
        var excludedEnabled = CreateSalarianMaterial(
            "salarian", tint: [0, 255, 255, 255], scattering: new Vector4(8, 8, 8, 1));
        AssertMaterialsRenderEqually(
            excludedDisabled,
            excludedEnabled,
            "SkinLightScattering escaped the compiled inverse SAL_HED_Tint.G selector.");
    }

    private static void SalarianTattooChannelsOnlySelectCoverage()
    {
        var redPattern = CreateSalarianMaterial(
            "salarian", tattooPattern: [255, 0, 0, 255], tattooSelector: new Vector4(1, 0, 0, 0));
        var greenPattern = CreateSalarianMaterial(
            "salarian", tattooPattern: [0, 255, 0, 255], tattooSelector: new Vector4(0, 1, 0, 0));
        AssertMaterialsRenderEqually(
            redPattern,
            greenPattern,
            "SAL_HED_Tatt RGB leaked into visible colour instead of selecting tattoo coverage.");
    }

    private static void SalarianEyePackedChannelsPreserveRoles()
    {
        var redZero = CreateSalarianEyeMaterial("salarian-eyes", [0, 255, 255, 0]);
        var redFull = CreateSalarianEyeMaterial("salarian-eyes", [255, 255, 255, 0]);
        AssertMaterialsRenderEqually(
            redZero,
            redFull,
            "SAL_HED_EYE_Spec red affected the eye while emissive strength was zero.");

        var greenZero = CreateSalarianEyeMaterial("salarian-eyes", [0, 0, 255, 0]);
        var greenFull = CreateSalarianEyeMaterial("salarian-eyes", [0, 255, 255, 0]);
        AssertMaterialsRenderDifferently(
            greenZero,
            greenFull,
            "SAL_HED_EYE_Spec green did not drive the direct-light exponent.");

        var pupil = CreateSalarianEyeMaterial("salarian-eyes", [0, 255, 0, 255]);
        AssertMaterialsRenderDifferently(
            greenFull,
            pupil,
            "SAL_HED_EYE_Spec blue/alpha did not switch between iris and pupil colour.");

        var emissiveOff = CreateSalarianEyeMaterial("salarian-eyes", [0, 255, 255, 0], emissive: 1);
        var emissiveOn = CreateSalarianEyeMaterial("salarian-eyes", [255, 255, 255, 0], emissive: 1);
        AssertMaterialsRenderDifferently(
            emissiveOff,
            emissiveOn,
            "SAL_HED_EYE_Spec red did not gate eye emissive output.");
    }

    private static void SalarianEyeCubesRemainIndependent()
    {
        var unreflected = CreateSalarianEyeMaterial(
            "salarian-eyes", [0, 255, 255, 0], chromeValue: 0, visorValue: 0);
        var chrome = CreateSalarianEyeMaterial(
            "salarian-eyes", [0, 255, 255, 0], chromeValue: 255, visorValue: 0);
        var visor = CreateSalarianEyeMaterial(
            "salarian-eyes", [0, 255, 255, 0], chromeValue: 0, visorValue: 255);

        AssertMaterialsRenderDifferently(
            unreflected,
            chrome,
            "The fixed Cube_Chrome binding did not participate in the Salarian eye base pass.");
        AssertMaterialsRenderDifferently(
            unreflected,
            visor,
            "The fixed Cube_Visor binding did not participate in the Salarian eye base pass.");
        AssertMaterialsRenderDifferently(
            chrome,
            visor,
            "Cube_Chrome and Cube_Visor were collapsed into one Salarian eye contribution.");
    }

    private static void SalarianSpecularMapParticipates()
    {
        var black = CreateSalarianMaterial("salarian", specularMap: [0, 0, 0, 255]);
        var white = CreateSalarianMaterial("salarian", specularMap: [255, 255, 255, 255]);
        AssertMaterialsRenderDifferently(
            black,
            white,
            "The LE1-only SAL_HED_SpecMap did not affect direct lighting.");
    }
}
