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
        new("packed face mask channels remain isolated from base colour", PackedMasksStayOutOfBaseColour),
        new("face mask gates primary Addn colour", FaceMaskGatesPrimaryAdditionColour),
        new("Human Female eye makeup excludes lips and retains eye coverage", FemaleEyeMakeupExcludesLips),
        new("human male and female base albedo uses raw SkinTone across games", HumanBaseAlbedoUsesRawSkinTone),
        new("female lip tint retains its native diffuse green gain", FemaleLipTintRetainsDiffuseGreenGain),
        new("LE2 human skin transmission is blue-selected and can reduce direct light", Le2SkinTransmissionUsesDiffuseBlue)
    ];

    private static void PackedMasksStayOutOfBaseColour()
    {
        var channels = new (byte[] First, byte[] Second, string Role)[]
        {
            ([255, 0, 255, 255], [255, 255, 255, 255], "green/albedo"),
            ([0, 0, 255, 255], [255, 0, 255, 255], "red/specular")
        };
        foreach (var (first, second, role) in channels)
            AssertMaterialsRenderEqually(CreateSkinMaterial("skin", first), CreateSkinMaterial("skin", second),
                $"Packed HED_Mask {role} leaked into the rendered face.");
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

    private static void FemaleEyeMakeupExcludesLips()
    {
        foreach (var blue in new byte[] { 0, 255 })
        {
            // Green marks both regions; blue distinguishes eyes from lips.
            byte[] mask = [0, 255, blue, (byte)(255 - blue)];
            var disabled = CreateFemaleSkinMaterial("skin", 0, makeupRgba: mask, eyeStrength: 0);
            var enabled = CreateFemaleSkinMaterial("skin", 0, makeupRgba: mask, eyeStrength: 1);
            var (renderer, camera) = CreateTriangleRenderer(disabled);
            using (renderer)
            {
                var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
                renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [enabled.Key] = enabled });
                var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
                TestAssert.True(first.SequenceEqual(second) == (blue == 0),
                    blue == 0
                        ? "Eye makeup tinted the lip region excluded by the blue channel."
                        : "Eye makeup lost the green-blue eye region.");
            }
        }
    }

    private static void HumanBaseAlbedoUsesRawSkinTone()
    {
        var games = new (string Name, bool IsLe2, bool IsLe3)[]
        {
            ("LE1", false, false),
            ("LE2", true, false),
            ("LE3", false, true)
        };
        foreach (var gender in new[] { "HMM", "HMF" })
        foreach (var game in games)
        foreach (var tone in new[] { 0.5f, 0.01f })
        {
            var key = $"{game.Name}_{gender}_{tone}";
            var material = gender == "HMF"
                ? CreateFemaleSkinMaterial(key, 0)
                : CreateLayeredSkinMaterial(key, [96, 72, 56, 255], [0, 0, 0, 0], null);
            var vectors = new Dictionary<string, Vector4>(material.Vectors)
            {
                ["SkinTone"] = new Vector4(tone, tone, tone, 1)
            };
            var textures = new Dictionary<string, HeadPreviewTexture>(material.Textures);
            if (gender == "HMF")
            {
                // Keep the female material identity while suppressing makeup layers.
                var makeup = textures["HED_Makeup_Mask"];
                textures["HED_Makeup_Mask"] = makeup with { Rgba8 = [0, 0, 0, 0] };
            }
            material = material with
            {
                IsLe2 = game.IsLe2,
                IsLe3 = game.IsLe3,
                Vectors = vectors,
                Textures = textures
            };

            var pixel = RenderCenterPixel(material, HeadPreviewRenderMode.Unlit);
            AssertExpectedUnlitDiffuse(pixel, [96, 72, 56], tone,
                $"{game.Name} {gender} SkinTone={tone}");
        }
    }

    private static void FemaleLipTintRetainsDiffuseGreenGain()
    {
        var material = CreateFemaleSkinMaterial("female-lip-gain", 0);
        var scalars = new Dictionary<string, float>(material.Scalars)
        {
            ["HED_Lips_Tint_Scalar"] = 1
        };
        var vectors = new Dictionary<string, Vector4>(material.Vectors)
        {
            ["HED_Lips_Tint_Vector"] = new Vector4(0.8f, 0.4f, 0.2f, 1)
        };
        var textures = new Dictionary<string, HeadPreviewTexture>(material.Textures);
        var makeup = textures["HED_Makeup_Mask"];
        textures["HED_Makeup_Mask"] = makeup with { Rgba8 = [0, 0, 0, 255] };
        material = material with { Scalars = scalars, Vectors = vectors, Textures = textures };

        var pixel = RenderCenterPixel(material, HeadPreviewRenderMode.Unlit);
        var diffuseGreen = DecodeSrgb(72 / 255f);
        var expectedRed = EncodePreviewLinear(diffuseGreen * 3.5f * 0.8f);
        var expectedGreen = EncodePreviewLinear(diffuseGreen * 3.5f * 0.4f);
        var expectedBlue = EncodePreviewLinear(diffuseGreen * 3.5f * 0.2f);
        AssertBgraNear(pixel, expectedBlue, expectedGreen, expectedRed, "female lip tint");
    }

    private static void Le2SkinTransmissionUsesDiffuseBlue()
    {
        foreach (var gender in new[] { "HMM", "HMF" })
        {
            var untransmitted = CreateLe2Skin(gender, 110, 0, transmissionScalar: 0);
            var blueSelected = CreateLe2Skin(gender, 110, 0, transmissionScalar: 1);
            var alphaChanged = CreateLe2Skin(gender, 110, 255, transmissionScalar: 1);
            var noBlueUntransmitted = CreateLe2Skin(gender, 0, 0, transmissionScalar: 0);
            var noBlue = CreateLe2Skin(gender, 0, 0, transmissionScalar: 1);

            var baseline = RenderPixels(untransmitted, HeadPreviewRenderMode.Shaded);
            var transmitted = RenderPixels(blueSelected, HeadPreviewRenderMode.Shaded);
            var alphaOnlyChanged = RenderPixels(alphaChanged, HeadPreviewRenderMode.Shaded);
            var noBlueTransmission = RenderPixels(noBlue, HeadPreviewRenderMode.Shaded);
            var noBlueBaseline = RenderPixels(noBlueUntransmitted, HeadPreviewRenderMode.Shaded);

            TestAssert.True(noBlueBaseline.SequenceEqual(noBlueTransmission),
                $"LE2 {gender} transmission activated without diffuse blue.");
            TestAssert.True(transmitted.SequenceEqual(alphaOnlyChanged),
                $"LE2 {gender} transmission used diffuse alpha instead of diffuse blue.");

            var baselineCenter = CenterPixel(baseline);
            var transmittedCenter = CenterPixel(transmitted);
            TestAssert.True(transmittedCenter[1] < baselineCenter[1],
                $"LE2 {gender} transmission only added light; its green channel was not reduced " +
                $"(baseline {baselineCenter[1]}, transmitted {transmittedCenter[1]}).");
        }
    }

    private static HeadPreviewMaterial CreateLe2Skin(
        string gender,
        byte diffuseBlue,
        byte diffuseAlpha,
        float transmissionScalar)
    {
        var material = gender == "HMF"
            ? CreateFemaleSkinMaterial($"le2-{gender}-{diffuseBlue}-{diffuseAlpha}-{transmissionScalar}", 0)
            : CreateLayeredSkinMaterial(
                $"le2-{gender}-{diffuseBlue}-{diffuseAlpha}-{transmissionScalar}",
                [96, 72, diffuseBlue, diffuseAlpha], [0, 0, 0, 0], null,
                transmissionScalar: transmissionScalar,
                transmissionColour: Vector4.One);
        var textures = new Dictionary<string, HeadPreviewTexture>(material.Textures);
        var diffuse = textures["HED_Diff"];
        textures["HED_Diff"] = diffuse with
        {
            Rgba8 = [96, 72, diffuseBlue, diffuseAlpha],
            Key = $"{diffuse.Key}:{diffuseBlue}:{diffuseAlpha}"
        };
        var scalars = new Dictionary<string, float>(material.Scalars)
        {
            ["HED_TMis_Scalar"] = transmissionScalar
        };
        var vectors = new Dictionary<string, Vector4>(material.Vectors)
        {
            ["HED_TClr_Vector"] = Vector4.One,
            ["SkinLightScattering"] = Vector4.Zero
        };
        if (gender == "HMF")
        {
            var makeup = textures["HED_Makeup_Mask"];
            textures["HED_Makeup_Mask"] = makeup with { Rgba8 = [0, 0, 0, 0] };
        }
        return material with
        {
            IsLe2 = true,
            Vectors = vectors,
            Scalars = scalars,
            Textures = textures
        };
    }

    private static byte[] RenderPixels(HeadPreviewMaterial material, HeadPreviewRenderMode mode)
    {
        var (renderer, camera) = CreateTriangleRenderer(material);
        using (renderer)
        {
            return renderer.Render(camera, new HeadPreviewOptions(
                RenderMode: mode,
                AmbientOcclusionEnabled: false,
                // Raw albedo assertions must not depend on preset exposure or grading.
                PostProcessingEnabled: mode != HeadPreviewRenderMode.Unlit)).BgraPixels.ToArray();
        }
    }

    private static byte[] RenderCenterPixel(HeadPreviewMaterial material, HeadPreviewRenderMode mode) =>
        CenterPixel(RenderPixels(material, mode));

    private static byte[] CenterPixel(byte[] pixels)
    {
        const int width = 96;
        const int x = width / 2;
        const int y = width / 2;
        var offset = (y * width + x) * 4;
        return pixels.AsSpan(offset, 4).ToArray();
    }

    private static void AssertExpectedUnlitDiffuse(byte[] pixel, byte[] diffuseRgb, float skinTone, string label)
    {
        var red = EncodePreviewLinear(DecodeSrgb(diffuseRgb[0] / 255f) * skinTone);
        var green = EncodePreviewLinear(DecodeSrgb(diffuseRgb[1] / 255f) * skinTone);
        var blue = EncodePreviewLinear(DecodeSrgb(diffuseRgb[2] / 255f) * skinTone);
        AssertBgraNear(pixel, blue, green, red, label);
    }

    private static float DecodeSrgb(float value) => value <= 0.04045f
        ? value / 12.92f
        : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private static float EncodePreviewLinear(float value)
    {
        value = Math.Clamp(value, 0, 1);
        return (value <= 0.0031308f ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f) * 255;
    }

    private static void AssertBgraNear(byte[] pixel, float blue, float green, float red, string label)
    {
        const float tolerance = 3;
        TestAssert.True(MathF.Abs(blue - pixel[0]) <= tolerance,
            $"{label} blue channel was {pixel[0]}, expected {blue:F1}.");
        TestAssert.True(MathF.Abs(green - pixel[1]) <= tolerance,
            $"{label} green channel was {pixel[1]}, expected {green:F1}.");
        TestAssert.True(MathF.Abs(red - pixel[2]) <= tolerance,
            $"{label} red channel was {pixel[2]}, expected {red:F1}.");
    }
}
