using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;
using static MorphFaceEditor.Tests.HumanMaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the human skin material regression set.
public static class HumanSkinMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("packed face mask channels cannot paint the face", PackedFaceMaskDoesNotAffectAlbedo),
        new("packed face mask red cannot drive specular", PackedFaceMaskDoesNotDriveSpecular),
        new("face mask gates primary Addn colour", FaceMaskGatesPrimaryAdditionColour),
        new("Addn blue selects the secondary blonde colour", AdditionBlueSelectsBlondeColour),
        new("freckle blue replaces the underlying diffuse", FreckleBlueReplacesDiffuse),
        new("Human Female makeup mask controls brow tint", FemaleMakeupMaskControlsBrowTint),
        new("Human Female blush uses makeup blue-alpha coverage", FemaleBlushUsesMakeupCoverage),
        new("Human Female addition power cannot alter unmasked face specular", FemaleAdditionPowerIsMaskBound),
        new("face transmission scalar does not disable skin tone", FaceTransmissionDoesNotDisableSkinTone),
        new("LE3 human face transmission uses diffuse blue rather than alpha", Le3FaceTransmissionUsesDiffuseBlue),
        new("skin scattering uses inverse diffuse alpha under directional lights", SkinScatteringUsesInverseDiffuseAlpha)
    ];

    private static void PackedFaceMaskDoesNotAffectAlbedo()
    {
        var lowGreen = CreateSkinMaterial("skin", [255, 0, 255, 255]);
        var highGreen = CreateSkinMaterial("skin", [255, 255, 255, 255]);
        var (renderer, camera) = CreateTriangleRenderer(lowGreen);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [highGreen.Key] = highGreen });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(first.SequenceEqual(second), "A packed HED_Mask colour channel leaked into face albedo.");
        }
    }

    private static void PackedFaceMaskDoesNotDriveSpecular()
    {
        var lowRed = CreateSkinMaterial("skin", [0, 0, 255, 255]);
        var highRed = CreateSkinMaterial("skin", [255, 0, 255, 255]);
        var (renderer, camera) = CreateTriangleRenderer(lowRed);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [highRed.Key] = highRed });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(first.SequenceEqual(second), "HED_Mask red leaked into face specular.");
        }
    }

    private static void FaceMaskGatesPrimaryAdditionColour()
    {
        var hiddenBlack = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 255], [0, 0, 0, 0], [128, 128, 255, 255],
            addnColour: Vector4.Zero);
        var hiddenRed = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 255], [0, 0, 0, 0], [128, 128, 255, 255],
            addnColour: new Vector4(1, 0, 0, 1));
        var activeRed = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 255], [0, 0, 0, 255], [128, 128, 255, 255],
            addnColour: new Vector4(1, 0, 0, 1));
        var (renderer, camera) = CreateTriangleRenderer(hiddenBlack);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [hiddenRed.Key] = hiddenRed });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "HED_Addn colour leaked through while the packed face selector was zero.");

            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [activeRed.Key] = activeRed });
            var third = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                !second.SequenceEqual(third),
                "HED_Addn colour remained hidden when the packed face selector was active.");
        }
    }

    private static void AdditionBlueSelectsBlondeColour()
    {
        var red = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 255], [0, 0, 0, 255], [128, 128, 0, 0],
            blonde: new Vector4(1, 0, 0, 1));
        var green = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 255], [0, 0, 0, 255], [128, 128, 0, 0],
            blonde: new Vector4(0, 1, 0, 1));
        var (renderer, camera) = CreateTriangleRenderer(red);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [green.Key] = green });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                !first.SequenceEqual(second),
                "The packed Addn blue channel did not select the secondary blonde vector.");
        }
    }

    private static void FreckleBlueReplacesDiffuse()
    {
        var darkDiffuse = CreateLayeredSkinMaterial(
            "skin", [48, 32, 24, 255], [0, 0, 0, 0], null,
            freckle: [255, 255, 0, 255],
            freckleBlue: new Vector4(0.2f, 0.03f, 0.01f, 1),
            freckleBlueScalar: 1);
        var lightDiffuse = CreateLayeredSkinMaterial(
            "skin", [192, 160, 128, 255], [0, 0, 0, 0], null,
            freckle: [255, 255, 0, 255],
            freckleBlue: new Vector4(0.2f, 0.03f, 0.01f, 1),
            freckleBlueScalar: 1);
        var (renderer, camera) = CreateTriangleRenderer(darkDiffuse);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [lightDiffuse.Key] = lightDiffuse });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "The blue Frek channel mixed with diffuse instead of replacing it with its colour vector.");
        }
    }

    private static void FemaleMakeupMaskControlsBrowTint()
    {
        var inactive = CreateFemaleSkinMaterial("skin", 0);
        var active = CreateFemaleSkinMaterial("skin", 1);
        var (renderer, camera) = CreateTriangleRenderer(inactive);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [active.Key] = active });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(!first.SequenceEqual(second), "HED_Makeup_Mask red did not expose the HMF brow tint branch.");
        }
    }

    private static void FemaleBlushUsesMakeupCoverage()
    {
        var disabled = CreateFemaleSkinMaterial("skin", 0, blushStrength: 0);
        var enabled = CreateFemaleSkinMaterial(
            "skin", 0, blushStrength: 1, blushColour: new Vector4(1, 0.05f, 0.03f, 1));
        var (renderer, camera) = CreateTriangleRenderer(disabled);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [enabled.Key] = enabled });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(!first.SequenceEqual(second),
                "HED_Blush_Scalar and HED_Blush_Vector did not affect the HMF makeup region.");
        }
    }

    private static void FemaleAdditionPowerIsMaskBound()
    {
        var low = CreateFemaleSkinMaterial("skin", 0, additionPower: 0, specularColour: Vector4.One);
        var high = CreateFemaleSkinMaterial("skin", 0, additionPower: 24, specularColour: Vector4.One);
        var (renderer, camera) = CreateTriangleRenderer(low);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [high.Key] = high });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "HED_Addn_SPwr_Add_Scalar replaced the base HMF exponent where no addition mask was present.");
        }
    }

    private static void FaceTransmissionDoesNotDisableSkinTone()
    {
        var disabled = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 255], [0, 0, 0, 0], null, transmissionScalar: 0);
        var enabled = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 255], [0, 0, 0, 0], null, transmissionScalar: 1);
        var (renderer, camera) = CreateTriangleRenderer(disabled);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [enabled.Key] = enabled });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "HED_TMis_Scalar incorrectly changed the base SkinTone composition.");
        }
    }

    private static void Le3FaceTransmissionUsesDiffuseBlue()
    {
        var transparentAlpha = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 0], [0, 0, 0, 0], null,
            transmissionScalar: 1,
            transmissionColour: new Vector4(0.8f, 0.1f, 0.05f, 1),
            isLe3: true);
        var opaqueAlpha = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 255], [0, 0, 0, 0], null,
            transmissionScalar: 1,
            transmissionColour: new Vector4(0.8f, 0.1f, 0.05f, 1),
            isLe3: true);
        var (renderer, camera) = CreateTriangleRenderer(transparentAlpha);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [opaqueAlpha.Key] = opaqueAlpha });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(first.SequenceEqual(second),
                "LE3 face alpha leaked into transmission instead of using the compiled diffuse-blue selector.");
        }
    }

    private static void SkinScatteringUsesInverseDiffuseAlpha()
    {
        var disabled = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 0], [0, 0, 0, 0], null,
            scatterColour: Vector4.Zero);
        var enabled = CreateLayeredSkinMaterial(
            "skin", [96, 72, 56, 0], [0, 0, 0, 0], null,
            scatterColour: new Vector4(1, 0.35f, 0.2f, 1));
        var (renderer, camera) = CreateTriangleRenderer(disabled);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [enabled.Key] = enabled });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                !first.SequenceEqual(second),
                "SkinLightScattering was still suppressed by zero diffuse alpha instead of using its inverse.");
        }
    }
}
