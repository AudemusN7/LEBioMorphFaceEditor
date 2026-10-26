using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;
using static MorphFaceEditor.Tests.SpeciesMaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the batarian material regression set.
public static class BatarianMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Batarian gradient selector channels preserve their compiled roles", BatarianGradientSelectorsPreserveRoles),
        new("Batarian complexion colour uses masked addition alpha", BatarianComplexionUsesMaskedAlpha)
    ];

    private static void BatarianGradientSelectorsPreserveRoles()
    {
        var neck = CreateBatarianMaterial("batarian", [0, 255, 0, 255]);
        var face = CreateBatarianMaterial("batarian", [255, 0, 0, 255]);
        var top = CreateBatarianMaterial("batarian", [0, 0, 255, 255]);
        AssertMaterialsRenderDifferently(neck, face,
            "BAT_HED_Tint green and red did not remain separate neck/face selectors.");
        AssertMaterialsRenderDifferently(face, top,
            "BAT_HED_Tint red and blue did not remain separate face/top-head selectors.");
    }

    private static void BatarianComplexionUsesMaskedAlpha()
    {
        var inactive = CreateBatarianMaterial(
            "batarian", [0, 0, 0, 255], addition: [128, 128, 0, 0]);
        var active = CreateBatarianMaterial(
            "batarian", [0, 0, 0, 255], addition: [128, 128, 0, 255]);
        AssertMaterialsRenderDifferently(inactive, active,
            "BAT_HED_Addn alpha did not gate complexion colour in the selected mask region.");

        var unselectedPink = CreateBatarianMaterial(
            "batarian", [0, 0, 0, 255], addition: [128, 128, 0, 255],
            complexionMask: new Vector4(0, 1, 0, 0),
            complexionColour: new Vector4(1, 0, 1, 1));
        var unselectedGreen = CreateBatarianMaterial(
            "batarian", [0, 0, 0, 255], addition: [128, 128, 0, 255],
            complexionMask: new Vector4(0, 1, 0, 0),
            complexionColour: new Vector4(0, 1, 0, 1));
        AssertMaterialsRenderEqually(unselectedPink, unselectedGreen,
            "BAT_HED_Addn_Colour leaked through an unselected BAT_HED_Mask channel.");
    }


}
