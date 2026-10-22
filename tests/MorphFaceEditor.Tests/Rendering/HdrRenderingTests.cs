using System.Numerics;
using System.Reflection;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;
using SharpDX.Direct3D11;

using static MorphFaceEditor.Tests.RenderTestReadback;

namespace MorphFaceEditor.Tests;

// HDR/GPU display-boundary checks plus pure grading-LUT contract checks.
public static class HdrRenderingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("HDR scene values survive above one and the brighter exposure stays brighter", HdrValuesSurviveAndExposureOrderingIsStable),
        new("game-inspired HDR bloom respects the receiver and excludes selected background", GameInspiredBloomRespectsBackgroundAndReceiver),
        new("game-inspired postprocess remains stable across mode switches and small resizes", GameInspiredPostProcessSurvivesModeSwitchAndResize),
        new("translucent colour blends in linear light", TranslucencyBlendsInLinearLight),
        new("unlit bypass returns raw sRGB while enabled postprocessing preserves HDR", UnlitPostProcessingCanBeBypassed)
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
            ShadowsEnabled: false,
            PostProcessingEnabled: false));

        var offset = PixelIndex(frame.Width, frame.Width / 2, frame.Height / 2) * 4;
        var red = frame.BgraPixels[offset + 2];
        var blue = frame.BgraPixels[offset];
        TestAssert.True(Math.Abs(red - 188) <= 3 && Math.Abs(blue - 188) <= 3,
            $"A half-alpha red hair texel over blue should encode the linear 50/50 mix near (188,0,188), got ({red},{frame.BgraPixels[offset + 1]},{blue}).");
    }

    private static void GameInspiredBloomRespectsBackgroundAndReceiver()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var camera = new HeadOrbitCamera();
        var brightMaterial = CreateSkinMaterial("game-bloom-skin", new Vector4(5f, 4.5f, 4f, 1), [255, 255, 255, 255]);
        var receiverMaterial = CreateSkinMaterial("game-bloom-receiver", new Vector4(0.12f, 0.12f, 0.12f, 1), [255, 255, 255, 255]);
        var scene = CreateScene(
        [
            CreateQuad("game-bloom-receiver", -0.82f, -0.82f, 0.82f, 0.82f, 0, receiverMaterial),
            CreateQuad("game-bloom-patch", -0.32f, -0.32f, 0.32f, 0.32f, 0.4f, brightMaterial)
        ]);
        renderer.SetScene(scene);
        camera.Fit(scene.Bounds);

        var receiverOn = CreateBloomSettings(screenBlendThreshold: 10f);
        var receiverOff = CreateBloomSettings(screenBlendThreshold: 0f);
        var blooming = renderer.Render(camera, new HeadPreviewOptions(
            BackgroundColor: Vector3.Zero,
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessMode: HeadPreviewPostProcessMode.GameInspired,
            PostProcessSettings: receiverOn)).BgraPixels;
        var sceneHdr = ReadFloatTarget(renderer, "_resolvedTexture");
        var withoutReceiver = renderer.Render(camera, new HeadPreviewOptions(
            BackgroundColor: Vector3.Zero,
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessMode: HeadPreviewPostProcessMode.GameInspired,
            PostProcessSettings: receiverOff)).BgraPixels;

        var haloPixels = 0;
        for (var pixel = 0; pixel < sceneHdr.Length / 4; pixel++)
        {
            var offset = pixel * 4;
            var peak = Math.Max(sceneHdr[offset], Math.Max(sceneHdr[offset + 1], sceneHdr[offset + 2]));
            var isDimReceiverSurface = peak > 0.001f && peak < 2f;
            if (isDimReceiverSurface && Enumerable.Range(0, 3).Any(channel =>
                    blooming[offset + channel] > withoutReceiver[offset + channel] + 1))
            {
                haloPixels++;
            }
        }
        TestAssert.True(haloPixels > 2,
            $"HDR highlight did not create a halo on the dim surface outside its source patch, or a zero screen-blend receiver failed to disable it ({haloPixels} halo pixels).");

        var emptyScene = new HeadPreviewScene(
            "bright selected background",
            [],
            new HeadPreviewBounds(new Vector3(-1, -1, -0.05f), new Vector3(1, 1, 0.4f)));
        renderer.SetScene(emptyScene);
        camera.Fit(emptyScene.Bounds);
        renderer.Render(camera, new HeadPreviewOptions(
            BackgroundColor: Vector3.One,
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessMode: HeadPreviewPostProcessMode.GameInspired,
            PostProcessSettings: receiverOn));
        var bloom = ReadFloatTarget(renderer, "_bloomTexture");
        TestAssert.True(bloom.Where((_, index) => index % 4 != 3).All(value => value < 0.001f),
            "The bright selected background was extracted as a bloom source.");
    }

    private static void GameInspiredPostProcessSurvivesModeSwitchAndResize()
    {
        using var renderer = new HeadPreviewRenderer(32, 24);
        var material = CreateSkinMaterial("resize-post-skin", new Vector4(0.3f, 0.25f, 0.2f, 1), [255, 255, 255, 255]);
        var scene = CreateScene([CreateQuad("resize-post-face", -0.8f, -0.8f, 0.8f, 0.8f, 0, material)]);
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);
        var settings = HeadPreviewPostProcessProfiles.Get(HeadPreviewLightingPreset.WarmInterior);
        var options = new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessMode: HeadPreviewPostProcessMode.GameInspired,
            PostProcessSettings: settings);

        var first = renderer.Render(camera, options);
        var gradingTexture = GetPrivateField(renderer, "_gradingTexture");
        var bloomTexture = GetPrivateField(renderer, "_bloomTexture");
        var repeated = renderer.Render(camera, options);
        TestAssert.True(first.BgraPixels.SequenceEqual(repeated.BgraPixels),
            "Repeated unchanged GameInspired frames produced different display pixels.");
        TestAssert.True(ReferenceEquals(gradingTexture, GetPrivateField(renderer, "_gradingTexture")) &&
                        ReferenceEquals(bloomTexture, GetPrivateField(renderer, "_bloomTexture")),
            "Unchanged frames recreated the grading LUT or quarter-resolution bloom target.");

        renderer.Render(camera, options with { PostProcessMode = HeadPreviewPostProcessMode.Neutral });
        var afterModeSwitch = renderer.Render(camera, options);
        TestAssert.True(first.BgraPixels.SequenceEqual(afterModeSwitch.BgraPixels),
            "Switching through Neutral changed the next identical GameInspired frame.");
        TestAssert.True(ReferenceEquals(gradingTexture, GetPrivateField(renderer, "_gradingTexture")),
            "Switching modes recreated the cached grading LUT.");

        renderer.Resize(7, 5);
        var resized = renderer.Render(camera, options);
        var resizedBloom = (Texture2D)GetPrivateField(renderer, "_bloomTexture");
        TestAssert.True(resized.Width == 7 && resized.Height == 5 && resized.BgraPixels.Length == 7 * 5 * 4,
            $"Small odd-size resize returned an invalid frame ({resized.Width}x{resized.Height}, {resized.BgraPixels.Length} bytes).");
        TestAssert.True(resizedBloom.Description.Width == 2 && resizedBloom.Description.Height == 2,
            $"Odd-size bloom target was not rounded up to quarter resolution ({resizedBloom.Description.Width}x{resizedBloom.Description.Height}).");
        TestAssert.True(ReferenceEquals(gradingTexture, GetPrivateField(renderer, "_gradingTexture")),
            "Resizing recreated the viewport-independent grading LUT.");
    }

    private static HeadPreviewPostProcessSettings CreateBloomSettings(float screenBlendThreshold) => new(
        Vector3.Zero, Vector3.One, Vector3.One, Vector3.One, Vector3.Zero, 0f,
        screenBlendThreshold, 32f, Vector3.One);

    private static object GetPrivateField(object instance, string fieldName) =>
        typeof(HeadPreviewRenderer).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException($"Renderer field '{fieldName}' was not found.");

    private static void UnlitPostProcessingCanBeBypassed()
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
            ShadowsEnabled: false,
            PostProcessingEnabled: false));
        var rawHdr = ReadFloatTarget(renderer, "_resolvedTexture");
        var processedFrame = renderer.Render(camera, new HeadPreviewOptions(
            RenderMode: HeadPreviewRenderMode.Unlit,
            BackgroundColor: new Vector3(0.001f, 0.18f, 0.5f),
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessingEnabled: true));
        var processedHdr = ReadFloatTarget(renderer, "_resolvedTexture");

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

        var processedFaceChanged = Enumerable.Range(0, 3).Any(channel =>
            Math.Abs(processedFrame.BgraPixels[faceOffset + channel] - frame.BgraPixels[faceOffset + channel]) > 2);
        TestAssert.True(processedFaceChanged,
            "Enabling postprocessing did not change the unlit face reference.");
        TestAssert.True(rawHdr.Zip(processedHdr).All(pair => Math.Abs(pair.First - pair.Second) < 0.002f),
            "Enabling postprocessing changed the resolved HDR scene instead of only changing its display transform.");
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

    private static int PixelIndex(int width, int x, int y) => y * width + x;

    private static void ApplyDisplayExposure(
        HeadPreviewRenderer renderer,
        HeadPreviewLightingRig rig,
        HeadOrbitCamera camera)
    {
        var method = typeof(HeadPreviewRenderer).GetMethod(
            "ApplyPostProcess", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Renderer post-process method was not found.");
        method.Invoke(renderer, [rig, camera, false, HeadPreviewRenderMode.Shaded, null, HeadPreviewPostProcessMode.Neutral, null, true, null]);
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
