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
        new("LE1 and LE2 scalp hair HDR intensity remains visible on dark diffuse", ScalpHairHdrIntensityRemainsVisible),
        new("LE1 and LE2 scalp hair glints respond to HED_Tang", ScalpHairGlintsRespondToTangents),
        new("LE2 face and skin-only scalp match when transmission is disabled", Le2SkinOnlyScalpMatchesFace),
        new("LE2 face and scalp match native transmission across diffuse blue values", Le2TransmissionMatchesFaceAndScalp),
        new("LE2 female lip spec intensity uses makeup green with zero alpha", Le2FemaleLipSpecUsesMakeupGreen),
        new("LE2 female lip spec power uses makeup alpha independently of tint", Le2FemaleLipSpecPowerUsesMakeupAlpha)
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

    private static void ScalpHairHdrIntensityRemainsVisible()
    {
        foreach (var isLe2 in new[] { false, true })
        {
            var hdr1 = CreateScalpHairMaterial("scalp", Vector4.One, 16, isLe2);
            var hdr2 = CreateScalpHairMaterial("scalp", new Vector4(2, 2, 2, 1), 16, isLe2);
            var (renderer, camera) = CreateTriangleRenderer(hdr1);
            using (renderer)
            {
                var one = RenderCenterRgb(renderer, camera, HeadPreviewRenderMode.Unlit)[2];
                renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [hdr2.Key] = hdr2 });
                var two = RenderCenterRgb(renderer, camera, HeadPreviewRenderMode.Unlit)[2];

                TestAssert.True(one < two && two < 240,
                    $"{GameName(isLe2)} scalp hair HDR brightness was clipped or ignored " +
                    $"(HairColour 1={one}, 2={two}).");
            }
        }
    }

    private static void ScalpHairGlintsRespondToTangents()
    {
        foreach (var isLe2 in new[] { false, true })
        {
            var flat = CreateScalpHairMaterial("scalp", new Vector4(0.2f, 0.2f, 0.2f, 1), 128, isLe2);
            var tilted = CreateScalpHairMaterial("scalp", new Vector4(0.2f, 0.2f, 0.2f, 1), 128, isLe2);
            flat = WithScalpTangent(flat, [128, 128, 255, 255]);
            tilted = WithScalpTangent(tilted, [245, 128, 179, 255]);
            flat = flat with
            {
                Scalars = new Dictionary<string, float>(flat.Scalars)
                {
                    ["HED_Scalp_PhongSpec_Scalar"] = 1
                }
            };
            tilted = tilted with
            {
                Scalars = new Dictionary<string, float>(tilted.Scalars)
                {
                    ["HED_Scalp_PhongSpec_Scalar"] = 1
                }
            };

            var (renderer, camera) = CreateTriangleRenderer(flat);
            using (renderer)
            {
                var flatRgb = RenderCenterRgb(renderer, camera, HeadPreviewRenderMode.Shaded);
                renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [tilted.Key] = tilted });
                var tiltedRgb = RenderCenterRgb(renderer, camera, HeadPreviewRenderMode.Shaded);

                TestAssert.True(!flatRgb.SequenceEqual(tiltedRgb),
                    $"{GameName(isLe2)} scalp hair glint ignored the HED_Tang direction " +
                    $"(flat={string.Join(",", flatRgb)}, tilted={string.Join(",", tiltedRgb)}).");
            }
        }
    }

    private static void Le2SkinOnlyScalpMatchesFace()
    {
        byte[] rgba = [96, 72, 56, 255];
        var face = CreateLayeredSkinMaterial(
            "le2-face", rgba, [0, 0, 0, 0], null,
            transmissionScalar: 0,
            scatterColour: Vector4.Zero,
            transmissionColour: Vector4.One) with { IsLe2 = true };
        var scalp = CreateScalpHairMaterial(
            "le2-scalp", Vector4.One, 96, isLe2: true, diffuseRgb: [96, 72, 56]);
        var scalpTextures = new Dictionary<string, HeadPreviewTexture>(scalp.Textures);
        var scalpMask = scalpTextures["HED_Scalp_Spec"];
        scalpTextures["HED_Scalp_Spec"] = scalpMask with
        {
            Key = "le2-scalp:empty-mask",
            Rgba8 = [0, 0, 0, 255]
        };
        scalp = scalp with
        {
            Textures = scalpTextures,
            Scalars = new Dictionary<string, float>(scalp.Scalars)
            {
                ["HED_Scalp_Mask_Scalar"] = 0,
                ["HED_TMis_Scalar"] = 0
            },
            Vectors = new Dictionary<string, Vector4>(scalp.Vectors)
            {
                ["HED_TClr_Vector"] = Vector4.One,
                ["SkinLightScattering"] = Vector4.Zero
            }
        };

        var faceRgb = RenderCenterRgb(face, HeadPreviewRenderMode.Shaded);
        var scalpRgb = RenderCenterRgb(scalp, HeadPreviewRenderMode.Shaded);
        const int tolerance = 1;
        for (var channel = 0; channel < 3; channel++)
        {
            TestAssert.True(Math.Abs(faceRgb[channel] - scalpRgb[channel]) <= tolerance,
                $"LE2 skin-only scalp leaked light while HED_TMis was zero in RGB channel {channel} " +
                $"(face={faceRgb[channel]}, scalp={scalpRgb[channel]}).");
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

    private static void Le2FemaleLipSpecUsesMakeupGreen()
    {
        var noLipSpec = CreateLe2FemaleLipSpecMaterial("le2-lip-green-0", [0, 0, 0, 0], 0);
        var lipSpec = CreateLe2FemaleLipSpecMaterial("le2-lip-green-255", [0, 255, 0, 0], 0);
        var noLipSpecPixels = RenderPixels(noLipSpec, HeadPreviewRenderMode.Shaded);
        var lipSpecPixels = RenderPixels(lipSpec, HeadPreviewRenderMode.Shaded);

        TestAssert.True(!noLipSpecPixels.SequenceEqual(lipSpecPixels),
            "LE2 HMF lip spec intensity ignored HED_Makeup_Mask.G when alpha and tint scalar were zero.");
    }

    private static void Le2FemaleLipSpecPowerUsesMakeupAlpha()
    {
        var noAlphaBase = CreateLe2FemaleLipSpecMaterial("le2-lip-no-alpha-power-0", [0, 255, 0, 0], 0);
        var noAlphaAdded = CreateLe2FemaleLipSpecMaterial("le2-lip-no-alpha-power-30", [0, 255, 0, 0], 30);
        var alphaBase = CreateLe2FemaleLipSpecMaterial("le2-lip-alpha-power-0", [0, 255, 0, 255], 0);
        var alphaAdded = CreateLe2FemaleLipSpecMaterial("le2-lip-alpha-power-30", [0, 255, 0, 255], 30);
        var noAlphaBasePixels = RenderPixels(noAlphaBase, HeadPreviewRenderMode.Shaded);
        var noAlphaAddedPixels = RenderPixels(noAlphaAdded, HeadPreviewRenderMode.Shaded);
        var alphaBasePixels = RenderPixels(alphaBase, HeadPreviewRenderMode.Shaded);
        var alphaAddedPixels = RenderPixels(alphaAdded, HeadPreviewRenderMode.Shaded);

        TestAssert.True(noAlphaBasePixels.SequenceEqual(noAlphaAddedPixels),
            "LE2 HMF lip spec exponent changed without HED_Makeup_Mask.A coverage.");
        TestAssert.True(!alphaBasePixels.SequenceEqual(alphaAddedPixels),
            "LE2 HMF lip spec exponent ignored HED_Makeup_Mask.A with LipTintScalar held at zero.");
    }

    private static HeadPreviewMaterial CreateLe2FemaleLipSpecMaterial(
        string key,
        byte[] makeupRgba,
        float lipSpecPower)
    {
        var material = CreateFemaleSkinMaterial(key, browStrength: 0, makeupRgba: makeupRgba);
        var textures = new Dictionary<string, HeadPreviewTexture>(material.Textures);
        var diffuse = textures["HED_Diff"];
        textures["HED_Diff"] = diffuse with
        {
            Key = $"{key}:black-diffuse",
            Rgba8 = [0, 0, 0, 255]
        };
        var vectors = new Dictionary<string, Vector4>(material.Vectors)
        {
            ["HED_Spec_Add_Vector"] = Vector4.Zero,
            ["HED_Lips_Tint_Vector"] = Vector4.One,
            ["HED_TClr_Vector"] = Vector4.Zero,
            ["SkinLightScattering"] = Vector4.Zero
        };
        var scalars = new Dictionary<string, float>(material.Scalars)
        {
            ["HED_Lips_Tint_Scalar"] = 0,
            ["HED_Addn_Spec_Lips_Scalar"] = 1,
            ["HED_SPwr_Scalar"] = 5,
            ["HED_Addn_SPwr_Lips_Scalar"] = lipSpecPower,
            ["HED_TMis_Scalar"] = 0
        };
        return material with
        {
            IsLe2 = true,
            Textures = textures,
            Vectors = vectors,
            Scalars = scalars
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

    private static HeadPreviewMaterial WithScalpTangent(HeadPreviewMaterial material, byte[] rgba)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(material.Textures)
        {
            ["HED_Tang"] = CreateTexture("HED_Tang", rgba, TextureRole.Tangent),
            ["HED_Scalp_SpecShift2"] = CreateTexture(
                "HED_Scalp_SpecShift2", [128, 255, 128, 255], TextureRole.Specular)
        };
        return material with { Textures = textures };
    }

    private static byte[] RenderCenterRgb(HeadPreviewMaterial material, HeadPreviewRenderMode mode)
    {
        var (renderer, camera) = CreateTriangleRenderer(material);
        using (renderer)
        {
            return RenderCenterRgb(renderer, camera, mode);
        }
    }

    private static byte[] RenderPixels(HeadPreviewMaterial material, HeadPreviewRenderMode mode)
    {
        var (renderer, camera) = CreateTriangleRenderer(material);
        using (renderer)
        {
            return renderer.Render(camera, new HeadPreviewOptions(
                RenderMode: mode,
                AmbientOcclusionEnabled: false)).BgraPixels.ToArray();
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
