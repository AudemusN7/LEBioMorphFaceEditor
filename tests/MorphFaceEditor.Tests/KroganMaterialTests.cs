using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;
using static MorphFaceEditor.Tests.SpeciesMaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the krogan material regression set.
public static class KroganMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Krogan tint selectors preserve compiled channel roles", KroganTintSelectorsPreserveRoles),
        new("Krogan complexion colour uses masked addition alpha", KroganComplexionUsesMaskedAlpha),
        new("Krogan Wrex scalar selects the character specular branch", KroganWrexSpecularSelectsCharacterBranch),
        new("Krogan specular power preserves the compiled tint-alpha product", KroganSpecularPowerPreservesTintProduct),
        new("Krogan eye specular and fixed cube bindings remain independent", KroganEyeBindingsRemainIndependent),
        new("Krogan LE1 and LE2 eye paths follow the loaded package", KroganEyePathsFollowLoadedPackage),
        new("LE3 Krogan head and eye normals consume stored RGB", Le3KroganNormalsConsumeRgb)
    ];

    private static void KroganTintSelectorsPreserveRoles()
    {
        var face = CreateKroganMaterial("krogan", tint: [0, 0, 0, 0], gradientSelectors: [0, 255, 0, 255]);
        var shell = CreateKroganMaterial("krogan", tint: [0, 0, 0, 0], gradientSelectors: [255, 0, 0, 255]);
        var lips = CreateKroganMaterial("krogan", tint: [0, 0, 0, 0], gradientSelectors: [0, 0, 255, 255]);
        AssertMaterialsRenderDifferently(face, shell,
            "KRO_HED_Tnt2 green and red did not remain separate face/head-plate selectors.");
        AssertMaterialsRenderDifferently(shell, lips,
            "KRO_HED_Tnt2 red and blue did not remain separate head-plate/lip selectors.");

        var skin = CreateKroganMaterial("krogan", tint: [0, 0, 0, 0], gradientSelectors: [0, 0, 0, 255]);
        var plates = CreateKroganMaterial("krogan", tint: [255, 0, 0, 0], gradientSelectors: [0, 0, 0, 255]);
        AssertMaterialsRenderDifferently(skin, plates,
            "KRO_HED_Tint red did not apply the compiled SkinTone/Helmet_Tint product.");
    }

    private static void KroganComplexionUsesMaskedAlpha()
    {
        var inactive = CreateKroganMaterial(
            "krogan", [0, 0, 0, 0], [0, 0, 0, 255], addition: [128, 128, 0, 0]);
        var active = CreateKroganMaterial(
            "krogan", [0, 0, 0, 0], [0, 0, 0, 255], addition: [128, 128, 0, 255]);
        AssertMaterialsRenderDifferently(inactive, active,
            "KRO_HED_Addn alpha did not gate the complexion colour in the selected mask region.");

        var unselectedPink = CreateKroganMaterial(
            "krogan", [0, 0, 0, 0], [0, 0, 0, 255], addition: [128, 128, 0, 255],
            complexionMask: new Vector4(0, 1, 0, 0),
            complexionColour: new Vector4(1, 0, 1, 1));
        var unselectedGreen = CreateKroganMaterial(
            "krogan", [0, 0, 0, 0], [0, 0, 0, 255], addition: [128, 128, 0, 255],
            complexionMask: new Vector4(0, 1, 0, 0),
            complexionColour: new Vector4(0, 1, 0, 1));
        AssertMaterialsRenderEqually(unselectedPink, unselectedGreen,
            "KRO_HED_Addn_Colour leaked through an unselected KRO_HED_Mask channel.");
    }

    private static void KroganWrexSpecularSelectsCharacterBranch()
    {
        var ordinary = CreateKroganMaterial(
            "krogan", [255, 255, 0, 255], [0, 0, 0, 255], wrexSpecular: 0);
        var wrex = CreateKroganMaterial(
            "krogan", [255, 255, 0, 255], [0, 0, 0, 255], wrexSpecular: 1);
        AssertMaterialsRenderDifferently(ordinary, wrex,
            "Wrex_Spec_Scalar did not select the compiled Diff.A*1.4*Tint.RGB character branch.");
    }

    private static void KroganSpecularPowerPreservesTintProduct()
    {
        var halfAlpha = CreateKroganMaterial(
            "krogan", [0, 255, 0, 128], [0, 0, 0, 255],
            complexionMask: new Vector4(0, 1, 0, 0),
            specularPower: 0.2f,
            skinSpecular: Vector4.One);
        var fullAlpha = CreateKroganMaterial(
            "krogan", [0, 255, 0, 255], [0, 0, 0, 255],
            complexionMask: new Vector4(0, 1, 0, 0),
            specularPower: 0.2f * (128f / 255f),
            skinSpecular: Vector4.One);
        AssertMaterialsRenderEqually(halfAlpha, fullAlpha,
            "Krogan specular power was not the compiled Tint.A * SPwr * 100 product.");

        var disabled = fullAlpha with
        {
            Scalars = new Dictionary<string, float>(fullAlpha.Scalars, StringComparer.OrdinalIgnoreCase)
            {
                ["KRO_HED_Spec_Scalar"] = 0
            }
        };
        AssertMaterialsRenderDifferently(disabled, fullAlpha,
            "KRO_HED_Spec_Scalar did not multiply the ordinary skin-specular branch.");
    }

    private static void KroganEyeBindingsRemainIndependent()
    {
        var iris = CreateKroganEyeMaterial("krogan-eye", [255, 8, 0, 255], cubeValue: 0);
        var ordinary = CreateKroganEyeMaterial("krogan-eye", [0, 8, 0, 255], cubeValue: 0);
        var lens = CreateKroganEyeMaterial("krogan-eye", [0, 0, 255, 255], cubeValue: 0);
        AssertMaterialsRenderDifferently(iris, ordinary,
            "KRO_Eye_Spec red exponent and green strength were collapsed into one generic specular value.");
        AssertMaterialsRenderDifferently(ordinary, lens,
            "KRO_Eye_Spec blue did not remain the independent 500-power lens lobe.");

        var reflected = CreateKroganEyeMaterial("krogan-eye", [0, 0, 0, 255], cubeValue: 255);
        var unreflected = CreateKroganEyeMaterial("krogan-eye", [0, 0, 0, 255], cubeValue: 0);
        AssertMaterialsRenderDifferently(unreflected, reflected,
            "The fixed Metal_Cube binding did not participate in the Krogan eye base pass.");
    }

    private static void KroganEyePathsFollowLoadedPackage()
    {
        var le1 = CreateKroganEyeMaterial("krogan-eye", [0, 255, 0, 255], cubeValue: 0) with { IsLe2 = false };
        var le2 = le1 with { IsLe2 = true };
        TestAssert.True(!le1.IsLe2, "The LE1 Krogan eye material lost its package selector.");
        TestAssert.True(le2.IsLe2, "The LE2 Krogan eye material lost its package selector.");
        TestAssert.Equal(HeadMaterialFamily.KroganEyes, le1.Family);
        TestAssert.Equal(HeadMaterialFamily.KroganEyes, le2.Family);
    }

    private static void Le3KroganNormalsConsumeRgb()
    {
        var le2Head = CreateKroganMaterial(
            "krogan", [0, 255, 0, 255], [0, 0, 0, 255],
            normal: [224, 128, 160, 255], isLe3: false);
        var le3Head = CreateKroganMaterial(
            "krogan", [0, 255, 0, 255], [0, 0, 0, 255],
            normal: [224, 128, 160, 255], isLe3: true);
        AssertMaterialsRenderDifferently(le2Head, le3Head,
            "The LE3 KRO head RGB normal path collapsed to LE2 BC5 reconstruction.");

        var le2Eye = CreateKroganEyeMaterial(
            "krogan-eye", [255, 255, 255, 255], cubeValue: 255,
            irisNormal: [224, 128, 160, 255], lensNormal: [192, 128, 160, 255], isLe3: false);
        var le3Eye = CreateKroganEyeMaterial(
            "krogan-eye", [255, 255, 255, 255], cubeValue: 255,
            irisNormal: [224, 128, 160, 255], lensNormal: [192, 128, 160, 255], isLe3: true);
        AssertMaterialsRenderDifferently(le2Eye, le3Eye,
            "The LE3 KRO iris/lens RGB normal paths collapsed to LE2 BC5 reconstruction.");
    }
}
