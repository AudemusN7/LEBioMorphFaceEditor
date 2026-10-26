using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;
using static MorphFaceEditor.Tests.SpeciesMaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the turian material regression set.
public static class TurianMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Turian base specular follows diffuse alpha rather than tint alpha", TurianBaseSpecularUsesDiffuseAlpha),
        new("Turian Mask scalar clips the tint-green teeth region", TurianMaskClipsTeethRegion)
    ];

    private static void TurianBaseSpecularUsesDiffuseAlpha()
    {
        var tintAlphaZero = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.One);
        var tintAlphaOne = CreateTurianMaterial(
            "turian", [255, 0, 0, 255], Vector4.One);
        AssertMaterialsRenderEqually(
            tintAlphaZero,
            tintAlphaOne,
            "Tint alpha incorrectly changed the Turian base specular lobe.");

        var diffuseAlphaZero = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.One,
            diffuse: [128, 128, 128, 0]);
        AssertMaterialsRenderDifferently(
            diffuseAlphaZero,
            tintAlphaZero,
            "Diffuse alpha did not gate TUR_HED_Spec_Colour.");
    }

    private static void TurianMaskClipsTeethRegion()
    {
        var visibleTeeth = CreateTurianMaterial(
            "turian", [0, 255, 0, 0], Vector4.Zero, maskScalar: 1);
        var hiddenTeeth = CreateTurianMaterial(
            "turian", [0, 255, 0, 0], Vector4.Zero, maskScalar: 0);
        AssertMaterialsRenderDifferently(
            visibleTeeth,
            hiddenTeeth,
            "Mask did not clip the Tint.G teeth region.");

        var unselectedA = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.Zero, maskScalar: 1);
        var unselectedB = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.Zero, maskScalar: 0);
        AssertMaterialsRenderEqually(
            unselectedA,
            unselectedB,
            "Mask affected pixels outside the Tint.G teeth region.");
    }


}
