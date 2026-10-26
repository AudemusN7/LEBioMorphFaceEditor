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
        new("Krogan specular power preserves the compiled tint-alpha product", KroganSpecularPowerPreservesTintProduct)
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


}
