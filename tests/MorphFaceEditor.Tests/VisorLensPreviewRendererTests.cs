using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

public static class VisorLensPreviewRendererTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("HMM visor lens additively overlays with zero source alpha", AdditiveLensOverlaysWithZeroAlpha),
        new("LE1 visor lens uses its fixed reflection cube", Le1LensUsesFixedCube),
        new("visor lens animation time is deterministic and advances UV phases", LensAnimationTimeIsDeterministic)
    ];

    private static void AdditiveLensOverlaysWithZeroAlpha()
    {
        var material = CreateLens("lens-le2", le2: true);
        var (renderer, camera) = CreateTriangleRenderer(material);
        using (renderer)
        {
            var frame = renderer.Render(camera, PreviewOptions());
            var center = Pixel(frame.BgraPixels, frame.Width, frame.Height / 2, frame.Width / 2);
            var background = Pixel(frame.BgraPixels, frame.Width, 1, 1);

            TestAssert.True(center.B > background.B + 20 && center.G > background.G + 20 && center.R > background.R + 20,
                "The lens did not add its sampled color over the background.");
            TestAssert.Equal(background.A, center.A);
        }
    }

    private static void Le1LensUsesFixedCube()
    {
        var dark = CreateLens("lens-le1", le2: false, cubeColor: 0);
        var bright = dark with
        {
            FixedCubeTexture = CreateCube("lens-cube-bright", 255)
        };
        var (renderer, camera) = CreateTriangleRenderer(dark);
        using (renderer)
        {
            camera.Orbit(1.0f, 0);
            var darkFrame = renderer.Render(camera, PreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [bright.Key] = bright });
            var brightFrame = renderer.Render(camera, PreviewOptions()).BgraPixels;
            var darkPixel = Pixel(darkFrame, 96, 48, 48);
            var brightPixel = Pixel(brightFrame, 96, 48, 48);
            TestAssert.True(brightPixel.R > darkPixel.R + 8 || brightPixel.G > darkPixel.G + 8 || brightPixel.B > darkPixel.B + 8,
                "Changing LE1's fixed cube did not affect the visor lens.");
        }
    }

    private static void LensAnimationTimeIsDeterministic()
    {
        var material = CreateLens("lens-animated", le2: true);
        var textures = new Dictionary<string, HeadPreviewTexture>(material.Textures)
        {
            ["__LensScanline"] = PatternTexture("__LensScanline", [0, 255, 20, 220]),
            ["__LensEmission"] = PatternTexture("__LensEmission", [48, 180, 96, 240]),
            ["__LensDistortion"] = PatternTexture("__LensDistortion", [0, 255, 32, 224]),
            ["__LensFractal"] = PatternTexture("__LensFractal", [220, 16, 180, 40])
        };
        material = material with { Textures = textures };
        var (renderer, camera) = CreateTriangleRenderer(material);
        using (renderer)
        {
            var first = renderer.Render(camera, PreviewOptions(), animationTimeSeconds: 0.25f).BgraPixels;
            var repeat = renderer.Render(camera, PreviewOptions(), animationTimeSeconds: 0.25f).BgraPixels;
            var later = renderer.Render(camera, PreviewOptions(), animationTimeSeconds: 1.25f).BgraPixels;

            TestAssert.True(first.SequenceEqual(repeat), "The same explicit time produced different preview pixels.");
            TestAssert.True(!first.SequenceEqual(later), "Advancing explicit time did not animate the sampled lens maps.");
        }
    }

    private static HeadPreviewMaterial CreateLens(string key, bool le2, byte? cubeColor = null)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>
        {
            ["__LensScanline"] = Texture("__LensScanline", 0),
            ["__LensEmission"] = Texture("__LensEmission", 26),
            ["__LensDistortion"] = Texture("__LensDistortion", 128),
            ["__LensFractal"] = Texture("__LensFractal", 26)
        };
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Accessory, HeadMaterialBlendMode.Additive, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            textures)
        {
            PreviewModel = HumanMaterialPreviewModel.VisorLens,
            IsLe2 = le2,
            FixedCubeTexture = cubeColor is { } color ? CreateCube(key + "-cube", color) : null
        };
    }

    private static HeadPreviewTexture Texture(string name, byte gray) => new(
        $"{name}:{gray}", name, 1, 1, [gray, gray, gray, 255], TextureRole.Other,
        TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, false);

    private static HeadPreviewTexture PatternTexture(string name, byte[] rows)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            var offset = (y * 4 + x) * 4;
            pixels[offset] = rows[(y + x) % rows.Length];
            pixels[offset + 1] = rows[(y + x + 1) % rows.Length];
            pixels[offset + 2] = rows[(y + x + 2) % rows.Length];
            pixels[offset + 3] = 255;
        }
        return new HeadPreviewTexture(
            $"{name}:pattern", name, 4, 4, pixels, TextureRole.Other,
            TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, false);
    }

    private static HeadPreviewCubeTexture CreateCube(string key, byte gray)
    {
        var face = new[] { gray, gray, gray, (byte)255 };
        return new HeadPreviewCubeTexture(key, 1, Enumerable.Range(0, 6).Select(_ => face.ToArray()).ToArray(), TextureColorSpace.Linear);
    }

    private static HeadPreviewOptions PreviewOptions() => new(
        HeadPreviewRenderMode.Unlit,
        BackgroundColor: new Vector3(0.02f),
        AmbientOcclusionEnabled: false);

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] pixels, int width, int y, int x)
    {
        var offset = checked((y * width + x) * 4);
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }
}
