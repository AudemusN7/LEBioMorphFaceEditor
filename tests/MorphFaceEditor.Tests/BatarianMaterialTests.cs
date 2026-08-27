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
        new("Batarian complexion colour uses masked addition alpha", BatarianComplexionUsesMaskedAlpha),
        new("Batarian LE1 and LE2 diffuse ordering remains distinct", BatarianGamePathsRemainDistinct),
        new("LE3 Batarian normals consume stored RGB instead of BC5 reconstruction", Le3BatarianNormalsConsumeRgb)
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

    private static void BatarianGamePathsRemainDistinct()
    {
        var le1 = CreateBatarianMaterial("batarian", [255, 0, 0, 255]) with { IsLe2 = false };
        var le2 = le1 with { IsLe2 = true };
        AssertMaterialsRenderDifferently(le1, le2,
            "Batarian LE1 pre-tinted diffuse and LE2 post-composition diffuse paths collapsed together.");
    }

    private static void Le3BatarianNormalsConsumeRgb()
    {
        var le2 = CreateBatarianMaterial(
            "batarian", [255, 0, 0, 255], normal: [224, 128, 160, 255]);
        var le3 = CreateBatarianMaterial(
            "batarian", [255, 0, 0, 255], normal: [224, 128, 160, 255], isLe3: true);
        le2 = le2 with { IsLe2 = true };
        AssertMaterialsRenderDifferently(le2, le3,
            "The LE3 BAT stored-RGB normal path collapsed to LE2 BC5 reconstruction.");
    }
}
