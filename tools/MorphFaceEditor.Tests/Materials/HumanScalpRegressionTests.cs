using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.HumanMaterialTestFixtures;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns focused HMM/HMF scalp and face-to-scalp rendering regressions.
public static class HumanScalpRegressionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE1 and LE2 scalp hair brightness varies continuously below the old thresholds", ScalpHairBrightnessIsContinuous),
        new("LE2 face and scalp match native transmission across diffuse blue values", Le2TransmissionMatchesFaceAndScalp)
    ];

    private static void ScalpHairBrightnessIsContinuous()
    {
        foreach (var isLe2 in new[] { false, true })
        {
            var hair01 = CreateScalpHairMaterial("scalp", new Vector4(0.1f, 0.1f, 0.1f, 1), 128, isLe2);
            var hair02 = CreateScalpHairMaterial("scalp", new Vector4(0.2f, 0.2f, 0.2f, 1), 128, isLe2);
            var hair03 = CreateScalpHairMaterial("scalp", new Vector4(0.3f, 0.3f, 0.3f, 1), 128, isLe2);
            var (renderer, camera) = CreateTriangleRenderer(hair01);
            using (renderer)
            {
                var first = RenderCenterRgb(renderer, camera, HeadPreviewRenderMode.Unlit)[2];
                renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [hair02.Key] = hair02 });
                var second = RenderCenterRgb(renderer, camera, HeadPreviewRenderMode.Unlit)[2];
                renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [hair03.Key] = hair03 });
                var third = RenderCenterRgb(renderer, camera, HeadPreviewRenderMode.Unlit)[2];

                TestAssert.True(first < second && second < third,
                    $"{GameName(isLe2)} scalp hair did not brighten continuously within the old gain band " +
                    $"(0.1={first}, 0.2={second}, 0.3={third}).");
            }
        }
    }

    private static void Le2TransmissionMatchesFaceAndScalp()
    {
        var transmissionColour = new Vector4(0.9f, 0.8f, 0.7f, 1);
        foreach (var gender in new[] { "HMM", "HMF" })
        foreach (var blue in new byte[] { 56, 240 })
        {
            var diffuse = new byte[] { 96, 72, blue, 255 };
            var face = CreateLe2TransmissionFace(gender, blue, transmissionColour);
            var scalp = CreateScalpHairMaterial(
                $"le2-scalp-{gender}-{blue}", Vector4.One, 96, isLe2: true,
                diffuseRgb: [diffuse[0], diffuse[1], diffuse[2]]);
            var scalpTextures = new Dictionary<string, HeadPreviewTexture>(scalp.Textures);
            var scalpMask = scalpTextures["HED_Scalp_Spec"];
            scalpTextures["HED_Scalp_Spec"] = scalpMask with
            {
                Key = $"le2-scalp-{gender}-{blue}:empty-mask",
                Rgba8 = [0, 0, 0, 255]
            };
            scalp = scalp with
            {
                Textures = scalpTextures,
                Scalars = new Dictionary<string, float>(scalp.Scalars)
                {
                    ["HED_Scalp_Mask_Scalar"] = 0,
                    ["HED_TMis_Scalar"] = 1,
                    ["HED_Scalp_PhongSpec_Scalar"] = 0
                },
                Vectors = new Dictionary<string, Vector4>(scalp.Vectors)
                {
                    ["HED_TClr_Vector"] = transmissionColour,
                    ["SkinLightScattering"] = Vector4.Zero
                }
            };

            var faceRgb = RenderCenterRgb(face, HeadPreviewRenderMode.Shaded);
            var scalpRgb = RenderCenterRgb(scalp, HeadPreviewRenderMode.Shaded);
            const int tolerance = 1;
            for (var channel = 0; channel < 3; channel++)
            {
                TestAssert.True(Math.Abs(faceRgb[channel] - scalpRgb[channel]) <= tolerance,
                    $"LE2 {gender} scalp transmission diverged from the face at Diff.B={blue} " +
                    $"in RGB channel {channel} (face={faceRgb[channel]}, scalp={scalpRgb[channel]}).");
            }
        }
    }

    private static HeadPreviewMaterial CreateLe2TransmissionFace(
        string gender,
        byte diffuseBlue,
        Vector4 transmissionColour)
    {
        byte[] diffuseRgba = [96, 72, diffuseBlue, 255];
        var key = $"le2-{gender}-transmission-{diffuseBlue}";
        if (gender == "HMM")
        {
            return CreateLayeredSkinMaterial(
                key, diffuseRgba, [0, 0, 0, 0], null,
                transmissionScalar: 1,
                scatterColour: Vector4.Zero,
                transmissionColour: transmissionColour) with { IsLe2 = true };
        }

        var face = CreateFemaleSkinMaterial(key, browStrength: 0);
        var textures = new Dictionary<string, HeadPreviewTexture>(face.Textures);
        var diffuse = textures["HED_Diff"];
        textures["HED_Diff"] = diffuse with
        {
            Key = $"{key}:diff:{diffuseBlue}",
            Rgba8 = diffuseRgba
        };
        var makeup = textures["HED_Makeup_Mask"];
        textures["HED_Makeup_Mask"] = makeup with
        {
            Key = $"{key}:neutral-makeup",
            Rgba8 = [0, 0, 0, 0]
        };
        var scalars = new Dictionary<string, float>(face.Scalars)
        {
            ["HED_TMis_Scalar"] = 1
        };
        var vectors = new Dictionary<string, Vector4>(face.Vectors)
        {
            ["HED_TClr_Vector"] = transmissionColour,
            ["SkinLightScattering"] = Vector4.Zero,
            ["HED_Spec_Add_Vector"] = Vector4.Zero
        };
        return face with
        {
            IsLe2 = true,
            Textures = textures,
            Scalars = scalars,
            Vectors = vectors
        };
    }

    private static HeadPreviewMaterial CreateScalpHairMaterial(
        string key,
        Vector4 hairColour,
        byte diffuseValue,
        bool isLe2,
        byte[]? diffuseRgb = null)
    {
        var material = CreateScalpMaterial(
            key,
            scalpMask: 1,
            mask: 1,
            hairColour: hairColour,
            specularColour: Vector4.Zero);
        var textures = new Dictionary<string, HeadPreviewTexture>(material.Textures);
        var diffuse = textures["HED_Scalp_Diff"];
        var rgb = diffuseRgb ?? [diffuseValue, diffuseValue, diffuseValue];
        textures["HED_Scalp_Diff"] = diffuse with
        {
            Key = $"{key}:diff:{Convert.ToHexString(rgb)}",
            Rgba8 = [rgb[0], rgb[1], rgb[2], 255]
        };
        var scalars = new Dictionary<string, float>(material.Scalars)
        {
            ["HED_Scalp_PhongSpec_Scalar"] = 0,
            ["HED_TMis_Scalar"] = 0
        };
        var vectors = new Dictionary<string, Vector4>(material.Vectors)
        {
            ["HED_TClr_Vector"] = Vector4.One,
            ["SkinLightScattering"] = Vector4.Zero
        };
        return material with
        {
            IsLe2 = isLe2,
            Scalars = scalars,
            Vectors = vectors,
            Textures = textures
        };
    }

    private static byte[] RenderCenterRgb(HeadPreviewMaterial material, HeadPreviewRenderMode mode)
    {
        var (renderer, camera) = CreateTriangleRenderer(material);
        using (renderer)
        {
            return RenderCenterRgb(renderer, camera, mode);
        }
    }

    private static byte[] RenderCenterRgb(
        HeadPreviewRenderer renderer,
        HeadOrbitCamera camera,
        HeadPreviewRenderMode mode)
    {
        var pixels = renderer.Render(camera, new HeadPreviewOptions(
            RenderMode: mode,
            AmbientOcclusionEnabled: false)).BgraPixels;
        const int width = 96;
        const int center = width / 2;
        var offset = (center * width + center) * 4;
        return [pixels[offset], pixels[offset + 1], pixels[offset + 2]];
    }

    private static string GameName(bool isLe2) => isLe2 ? "LE2" : "LE1";
}
