using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3DDevice = SharpDX.Direct3D11.Device;
using D3DMapFlags = SharpDX.Direct3D11.MapFlags;

namespace MorphFaceEditor.Tests;

// GPU-only checks for the linear HDR/display boundary and ambient MRT handoff.
public static class HdrRenderingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("HDR scene values survive above one and the brighter exposure stays brighter", HdrValuesSurviveAndExposureOrderingIsStable),
        new("HDR bloom thresholds above one create an off-surface halo", HdrBloomUsesValuesAboveOne),
        new("translucent colour blends in linear light", TranslucencyBlendsInLinearLight),
        new("unlit texture and selected background preserve sRGB reference bytes", UnlitAndBackgroundPreserveSrgbReferences),
        new("resolved ambient diffuse stays separate from key shadows", ResolvedAmbientDiffuseIsSeparateFromKeyShadows)
    ];

    private static void HdrValuesSurviveAndExposureOrderingIsStable()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var material = CreateSkinMaterial("hdr-skin", new Vector4(1.8f, 1.45f, 1.2f, 1), [255, 255, 255, 255]);
        var scene = CreateScene([CreateQuad("hdr-face", -0.9f, -0.9f, 0.9f, 0.9f, 0, material)]);
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);

        renderer.Render(camera, new HeadPreviewOptions(
            LightingPreset: HeadPreviewLightingPreset.Studio,
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false));
        var hdr = ReadFloatTarget(renderer, "_resolvedTexture");
        var center = PixelIndex(96, 96 / 2, 96 / 2);
        var centerHdr = hdr.AsSpan(center * 4, 3);
        var rig = HeadPreviewLightingPresets.Get(HeadPreviewLightingPreset.Studio);
        ApplyDisplayExposure(renderer, rig with { Exposure = 0.45f }, camera);
        var lowExposure = ReadDisplayPixels(renderer);
        ApplyDisplayExposure(renderer, rig with { Exposure = 1.8f }, camera);
        var highExposure = ReadDisplayPixels(renderer);
        var lowLuma = Luma(lowExposure, center * 4);
        var highLuma = Luma(highExposure, center * 4);

        TestAssert.True(centerHdr.ToArray().Max() > 1.1f,
            $"Overbright lit material values were clamped before the linear HDR resolve ({string.Join(",", centerHdr.ToArray().Select(value => value.ToString("F2")))}).");
        TestAssert.True(highLuma > lowLuma + 12,
            $"Higher display exposure did not brighten the same HDR scene ({highLuma:F1} vs {lowLuma:F1}).");
    }

    private static void TranslucencyBlendsInLinearLight()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var rgba = new byte[] { 0, 255, 0, 128 };
        var diffuse = new HeadPreviewTexture(
            "linear-blend-hair-diff", "HAIR_Diff", 1, 1, rgba,
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Translucency, true);
        var hair = new HeadPreviewMaterial(
            "linear-blend-hair", "linear-blend-hair", HeadMaterialFamily.Hair,
            HeadMaterialBlendMode.Translucent, true,
            new Dictionary<string, float>(),
            // LE1/2's native base diffuse carries a 0.25 gain for this colour
            // band, so a red tint of four produces unit linear red for the mix.
            new Dictionary<string, Vector4> { ["HED_Hair_Colour_Vector"] = new Vector4(4, 0, 0, 1) },
            new Dictionary<string, HeadPreviewTexture> { ["HAIR_Diff"] = diffuse });
        var scene = CreateScene([CreateQuad("hair-card", -0.75f, -0.75f, 0.75f, 0.75f, 0, hair)]);
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);
        var frame = renderer.Render(camera, new HeadPreviewOptions(
            RenderMode: HeadPreviewRenderMode.Unlit,
            BackgroundColor: new Vector3(0, 0, 1),
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false));

        var offset = PixelIndex(frame.Width, frame.Width / 2, frame.Height / 2) * 4;
        var red = frame.BgraPixels[offset + 2];
        var blue = frame.BgraPixels[offset];
        TestAssert.True(Math.Abs(red - 188) <= 3 && Math.Abs(blue - 188) <= 3,
            $"A half-alpha red hair texel over blue should encode the linear 50/50 mix near (188,0,188), got ({red},{frame.BgraPixels[offset + 1]},{blue}).");
    }

    private static void HdrBloomUsesValuesAboveOne()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var material = CreateSkinMaterial("hdr-bloom-skin", new Vector4(4, 3.5f, 3, 1), [255, 255, 255, 255]);
        var scene = CreateScene([CreateQuad("bright-patch", -0.35f, -0.35f, 0.35f, 0.35f, 0, material)]);
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);
        renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false));
        var sceneHdr = ReadFloatTarget(renderer, "_resolvedTexture");
        var hdrMaximum = sceneHdr.Where((_, index) => index % 4 != 3).Max();
        var rig = HeadPreviewLightingPresets.Get(HeadPreviewLightingPreset.Studio) with
        {
            Exposure = 0.55f,
            BloomThreshold = 1.2f,
            BloomIntensity = 0
        };

        ApplyDisplayExposure(renderer, rig, camera);
        var bloomOff = ReadDisplayPixels(renderer);
        ApplyDisplayExposure(renderer, rig with { BloomIntensity = 2.5f }, camera);
        var bloomOn = ReadDisplayPixels(renderer);

        var offSurfaceBloomPixels = 0;
        for (var pixel = 0; pixel < sceneHdr.Length / 4; pixel++)
        {
            var offset = pixel * 4;
            var isOutsideBrightPatch = sceneHdr[offset] < 0.1f &&
                                       sceneHdr[offset + 1] < 0.1f &&
                                       sceneHdr[offset + 2] < 0.1f;
            if (isOutsideBrightPatch && Enumerable.Range(0, 3).Any(channel =>
                    bloomOn[offset + channel] > bloomOff[offset + channel] + 2))
            {
                offSurfaceBloomPixels++;
            }
        }

        TestAssert.True(hdrMaximum > 1.2f,
            $"The bright source did not retain values above the deliberately raised bloom threshold ({hdrMaximum:F2}).");
        TestAssert.True(offSurfaceBloomPixels > 2,
            $"An HDR source above threshold produced no off-surface bloom halo ({offSurfaceBloomPixels} background pixels changed).");
    }

    private static void UnlitAndBackgroundPreserveSrgbReferences()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var material = CreateSkinMaterial("srgb-reference-skin", Vector4.One, [32, 128, 220, 255]);
        var scene = CreateScene([CreateQuad("reference-face", -0.4f, -0.4f, 0.4f, 0.4f, 0, material)]);
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);
        var frame = renderer.Render(camera, new HeadPreviewOptions(
            RenderMode: HeadPreviewRenderMode.Unlit,
            BackgroundColor: new Vector3(0.001f, 0.18f, 0.5f),
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false));

        var faceOffset = PixelIndex(frame.Width, frame.Width / 2, frame.Height / 2) * 4;
        TestAssert.True(Math.Abs(frame.BgraPixels[faceOffset] - 220) <= 3 &&
                        Math.Abs(frame.BgraPixels[faceOffset + 1] - 128) <= 3 &&
                        Math.Abs(frame.BgraPixels[faceOffset + 2] - 32) <= 3,
            $"Unlit sRGB diffuse bytes changed after sampling and display encoding ({frame.BgraPixels[faceOffset + 2]},{frame.BgraPixels[faceOffset + 1]},{frame.BgraPixels[faceOffset]}).");

        var backgroundOffset = PixelIndex(frame.Width, 2, 2) * 4;
        var expected = new byte[] { 128, 46, 0 };
        for (var channel = 0; channel < expected.Length; channel++)
        {
            TestAssert.True(Math.Abs(frame.BgraPixels[backgroundOffset + channel] - expected[channel]) <= 2,
                $"Background sRGB channel {channel} changed from {expected[channel]} to {frame.BgraPixels[backgroundOffset + channel]}.");
        }
    }

    private static void ResolvedAmbientDiffuseIsSeparateFromKeyShadows()
    {
        using var renderer = new HeadPreviewRenderer(128, 128);
        var material = CreateSkinMaterial("ambient-separation-skin", new Vector4(0.7f, 0.6f, 0.5f, 1), [255, 255, 255, 255]);
        var meshes = new[]
        {
            CreateQuad("ambient-receiver", -1, -1, 1, 1, 0, material),
            CreateQuad("ambient-caster", -0.65f, 0.05f, -0.15f, 0.55f, 0.35f, material)
        };
        var scene = CreateScene(meshes);
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);

        renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: false, ShadowsEnabled: false));
        var unshadowedScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var unshadowedAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");
        renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: false, ShadowsEnabled: true));
        var shadowedScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var shadowedAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");

        var highSpecular = material with
        {
            Vectors = new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.7f, 0.6f, 0.5f, 1),
                ["HED_Spec_Add_Vector"] = new Vector4(30, 30, 30, 1)
            }
        };
        renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [highSpecular.Key] = highSpecular });
        renderer.Render(camera, new HeadPreviewOptions(AmbientOcclusionEnabled: false, ShadowsEnabled: false));
        var highSpecScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var highSpecAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");

        var litAmbientPixels = 0;
        var directOnlyChanges = 0;
        var specularOnlyChanges = 0;
        for (var pixel = 0; pixel < unshadowedScene.Length / 4; pixel++)
        {
            var offset = pixel * 4;
            var hasAmbient = unshadowedAmbient[offset] > 0.01f ||
                             unshadowedAmbient[offset + 1] > 0.01f ||
                             unshadowedAmbient[offset + 2] > 0.01f;
            if (hasAmbient)
            {
                litAmbientPixels++;
            }

            var directChanged = Enumerable.Range(0, 3).Any(channel =>
                unshadowedScene[offset + channel] - shadowedScene[offset + channel] > 0.08f);
            var ambientStable = Enumerable.Range(0, 3).All(channel =>
                Math.Abs(unshadowedAmbient[offset + channel] - shadowedAmbient[offset + channel]) < 0.01f);
            if (directChanged && ambientStable && hasAmbient)
            {
                directOnlyChanges++;
            }

            var specularChanged = Enumerable.Range(0, 3).Any(channel =>
                highSpecScene[offset + channel] - unshadowedScene[offset + channel] > 0.01f);
            var specularAmbientStable = Enumerable.Range(0, 3).All(channel =>
                Math.Abs(highSpecAmbient[offset + channel] - unshadowedAmbient[offset + channel]) < 0.01f);
            if (specularChanged && specularAmbientStable && hasAmbient)
            {
                specularOnlyChanges++;
            }
        }

        TestAssert.True(litAmbientPixels > 100,
            $"The ambient-diffuse target did not retain albedo × environment lighting ({litAmbientPixels} lit pixels).");
        TestAssert.True(directOnlyChanges > 20,
            $"Key-shadowed direct light was missing or leaked into the resolved ambient target ({directOnlyChanges} separable pixels).");
        TestAssert.True(specularOnlyChanges > 0,
            $"Changing direct specular response either had no visible effect or changed the ambient-diffuse target ({specularOnlyChanges} separable pixels).");
    }

    private static HeadPreviewMaterial CreateSkinMaterial(string key, Vector4 skinTone, byte[] diffuseRgba)
    {
        var diffuse = new HeadPreviewTexture(
            $"{key}-diff", "HED_Diff", 1, 1, diffuseRgba,
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false);
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4> { ["SkinTone"] = skinTone },
            new Dictionary<string, HeadPreviewTexture> { ["HED_Diff"] = diffuse });
    }

    private static HeadPreviewScene CreateScene(IReadOnlyList<HeadPreviewMesh> meshes) => new(
        "HDR rendering synthetic scene",
        meshes,
        new HeadPreviewBounds(new Vector3(-1, -1, -0.05f), new Vector3(1, 1, 0.4f)));

    private static HeadPreviewMesh CreateQuad(
        string name,
        float minX,
        float minY,
        float maxX,
        float maxY,
        float z,
        HeadPreviewMaterial material)
    {
        var vertices = new[]
        {
            Vertex(new Vector3(minX, minY, z), new Vector2(0, 1)),
            Vertex(new Vector3(maxX, minY, z), new Vector2(1, 1)),
            Vertex(new Vector3(maxX, maxY, z), new Vector2(1, 0)),
            Vertex(new Vector3(minX, maxY, z), new Vector2(0, 0))
        };
        return new HeadPreviewMesh(name, vertices, [0u, 1u, 2u, 0u, 2u, 3u],
            [new HeadPreviewSection(0, 6, 0, material)]);
    }

    private static HeadPreviewVertex Vertex(Vector3 position, Vector2 uv) => new(
        position, Vector3.UnitZ, new Vector4(1, 0, 0, 1), uv,
        0, 0, 0, 0, new Vector4(1, 0, 0, 0));

    private static HeadOrbitCamera CreateCamera(HeadPreviewBounds bounds)
    {
        var camera = new HeadOrbitCamera();
        camera.Fit(bounds);
        return camera;
    }

    private static float[] ReadFloatTarget(HeadPreviewRenderer renderer, string fieldName)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var rendererType = typeof(HeadPreviewRenderer);
        var source = (Texture2D?)rendererType.GetField(fieldName, privateInstance)?.GetValue(renderer)
            ?? throw new InvalidOperationException($"Renderer target '{fieldName}' was not found.");
        var device = (D3DDevice?)rendererType.GetField("_device", privateInstance)?.GetValue(renderer)
            ?? throw new InvalidOperationException("Renderer D3D device was not found.");
        var context = (DeviceContext?)rendererType.GetField("_context", privateInstance)?.GetValue(renderer)
            ?? throw new InvalidOperationException("Renderer D3D context was not found.");
        if (source.Description.Format != Format.R16G16B16A16_Float || source.Description.SampleDescription.Count != 1)
        {
            throw new InvalidOperationException($"Renderer target '{fieldName}' is not a resolved single-sample RGBA16F texture.");
        }

        var description = source.Description;
        using var staging = new Texture2D(device, new Texture2DDescription
        {
            Width = description.Width,
            Height = description.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None
        });
        context.CopyResource(source, staging);
        var mapped = context.MapSubresource(staging, 0, MapMode.Read, D3DMapFlags.None);
        try
        {
            var pixels = new float[description.Width * description.Height * 4];
            for (var y = 0; y < description.Height; y++)
            {
                var row = mapped.DataPointer + y * mapped.RowPitch;
                for (var x = 0; x < description.Width; x++)
                {
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var address = row + x * 8 + channel * 2;
                        var halfBits = unchecked((ushort)Marshal.ReadInt16(address));
                        pixels[(y * description.Width + x) * 4 + channel] =
                            (float)BitConverter.UInt16BitsToHalf(halfBits);
                    }
                }
            }
            return pixels;
        }
        finally
        {
            context.UnmapSubresource(staging, 0);
        }
    }

    private static int PixelIndex(int width, int x, int y) => y * width + x;

    private static void ApplyDisplayExposure(
        HeadPreviewRenderer renderer,
        HeadPreviewLightingRig rig,
        HeadOrbitCamera camera)
    {
        var method = typeof(HeadPreviewRenderer).GetMethod(
            "ApplyPostProcess", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Renderer post-process method was not found.");
        method.Invoke(renderer, [rig, camera, false, HeadPreviewRenderMode.Shaded, null]);
    }

    private static byte[] ReadDisplayPixels(HeadPreviewRenderer renderer)
    {
        var method = typeof(HeadPreviewRenderer).GetMethod(
            "ReadTexture", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Renderer display readback method was not found.");
        return (byte[]?)method.Invoke(renderer, [null])
            ?? throw new InvalidOperationException("Renderer display readback returned no pixels.");
    }

    private static double Luma(byte[] bgra, int offset) =>
        bgra[offset] * 0.0722 + bgra[offset + 1] * 0.7152 + bgra[offset + 2] * 0.2126;
}
