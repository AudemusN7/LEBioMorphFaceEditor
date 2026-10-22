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
        new("Salarian complexion normal follows the selected region", SalarianComplexionNormalFollowsSelectedRegion),
        new("Salarian skin scattering uses inverse surface-tint green", SalarianSkinScatteringUsesTintGreen)
    ];

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


}
