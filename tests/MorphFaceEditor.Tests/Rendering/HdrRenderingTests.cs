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
        new("compact emissive bloom forms a smooth halo without displaced copies", CompactBloomHasSmoothSpatialFalloff),
        new("postprocess remains stable across enable toggles and small resizes", PostProcessSurvivesToggleAndResize),
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
        var hdr = ReadFloatTarget(renderer, "Resolved");
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
            PostProcessSettings: receiverOn)).BgraPixels;
        var sceneHdr = ReadFloatTarget(renderer, "Resolved");
        var withoutReceiver = renderer.Render(camera, new HeadPreviewOptions(
            BackgroundColor: Vector3.Zero,
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
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
            PostProcessSettings: receiverOn));
        var bloom = ReadFloatTarget(renderer, "Bloom");
        TestAssert.True(bloom.Where((_, index) => index % 4 != 3).All(value => value < 0.001f),
            "The bright selected background was extracted as a bloom source.");
    }

    private static void CompactBloomHasSmoothSpatialFalloff()
    {
        const int size = 512;
        const float sourceDepth = 0.4f;
        using var renderer = new HeadPreviewRenderer(size, size);
        var bounds = new HeadPreviewBounds(new Vector3(-1, -1, -0.05f), new Vector3(1, 1, sourceDepth));
        var camera = CreateCamera(bounds);
        var worldPerPixel = 2 * (camera.Position.Z - sourceDepth) *
            MathF.Tan(camera.VerticalFieldOfViewDegrees * MathF.PI / 360) / size;
        // A small eye highlight must bloom smoothly with the production Studio
        // settings, even when its source covers only four render pixels.
        var halfWidth = worldPerPixel * 2;
        var emitter = HumanMaterialTestFixtures.CreateEyeMaterial(
            "compact-emitter", [24, 24, 24, 255], [0, 0, 0, 255], Vector4.Zero, 0) with
        {
            Scalars = new Dictionary<string, float> { ["Emis_Scalar"] = 24 },
            Vectors = new Dictionary<string, Vector4> { ["Emis_Color"] = Vector4.One },
            SupportedScalars = new HashSet<string>(["Emis_Scalar"], StringComparer.OrdinalIgnoreCase),
            SupportedVectors = new HashSet<string>(["Emis_Color"], StringComparer.OrdinalIgnoreCase),
            SupportedTextures = new HashSet<string>(["EYE_Lens_Norm"], StringComparer.OrdinalIgnoreCase)
        };
        var source = CreateQuad("compact-emitter", -halfWidth, -halfWidth, halfWidth, halfWidth, sourceDepth, emitter);
        renderer.SetScene(new HeadPreviewScene("Compact eye bloom",
        [
            CreateQuad("dim-receiver", -0.82f, -0.82f, 0.82f, 0.82f, 0,
                CreateSkinMaterial("dim-receiver", new Vector4(0.12f, 0.12f, 0.12f, 1), [255, 255, 255, 255])),
            source
        ], bounds));
        var options = new HeadPreviewOptions(BackgroundColor: Vector3.Zero,
            AmbientOcclusionEnabled: false, ShadowsEnabled: false);
        renderer.Render(camera, options, animationTimeSeconds: 0);
        var halo = ReadFloatTexture(renderer, "Bloom", SharpDX.DXGI.Format.R16G16B16A16_Float);
        var center = (halo.Width - 1) * 0.5f;
        var peak = Sample(halo, center, center);
        TestAssert.True(peak > 0.01f, "The compact emissive fixture did not produce a measurable bloom halo.");

        var maximumRise = 0f;
        var diagonal = MathF.Sqrt(0.5f);
        foreach (var direction in new[] { Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY,
                     new Vector2(diagonal, diagonal), new Vector2(-diagonal, diagonal),
                     new Vector2(diagonal, -diagonal), new Vector2(-diagonal, -diagonal) })
        {
            var previous = peak;
            for (var radius = 1; radius <= 16; radius++)
            {
                var current = Sample(halo, center + direction.X * radius, center + direction.Y * radius);
                maximumRise = Math.Max(maximumRise, current - previous);
                previous = current;
            }
        }
        // Equal distances should have similar brightness in every direction.
        var axis = Sample(halo, center + 4, center);
        var diagonalValue = Sample(halo, center + 4 * diagonal, center + 4 * diagonal);
        var isotropyDifference = Math.Abs(axis - diagonalValue) / peak;
        var maximumShiftDifference = 0d;
        var energy = Enumerable.Range(0, halo.Width * halo.Height).Sum(pixel => (double)halo.Values[pixel * 4]);
        foreach (var renderPixelShift in new[] { 0.5f, 1f })
        {
            renderer.UpdateDeformation(new HeadPreviewDeformationUpdate(source.Name,
                source.Vertices.Select(vertex => vertex with
                {
                    Position = vertex.Position + new Vector3(worldPerPixel * renderPixelShift, 0, 0)
                }).ToArray(), []));
            renderer.Render(camera, options, animationTimeSeconds: 0);
            var shifted = ReadFloatTexture(renderer, "Bloom", SharpDX.DXGI.Format.R16G16B16A16_Float);
            // Remove the expected translation before measuring changes in the
            // halo itself, as a slowly moving bright eye must not sprout copies.
            var difference = 0d;
            for (var y = 0; y < halo.Height; y++)
            for (var x = 0; x < halo.Width; x++)
                difference += Math.Abs(Sample(shifted, x + renderPixelShift * halo.Width / size, y) - halo.Values[(y * halo.Width + x) * 4]);
            maximumShiftDifference = Math.Max(maximumShiftDifference, difference / energy);
        }

        TestAssert.True(maximumRise / peak < 0.025f,
            $"A compact bright eye produced secondary bloom lobes instead of a smooth outward falloff " +
            $"(largest outward rise {maximumRise / peak:P1}; axis/diagonal difference {isotropyDifference:P1}; subpixel shift difference {maximumShiftDifference:P1}).");
        TestAssert.True(isotropyDifference < 0.08f,
            $"A compact bright eye produced a directional bloom pattern ({isotropyDifference:P1} of the central halo).");
        TestAssert.True(maximumShiftDifference < 0.12,
            $"Moving the bright eye by half or one render pixel changed its compensated bloom halo by {maximumShiftDifference:P1}.");

        // A normalized blur may spread a highlight, but must not brighten or
        // darken a constant HDR field, including where sampling clamps at edges.
        var extent = worldPerPixel * size;
        renderer.SetScene(new HeadPreviewScene("Constant emissive field",
            [CreateQuad("flat-emitter", -extent, -extent, extent, extent, sourceDepth, emitter)], bounds));
        renderer.Render(camera, options with { RenderMode = HeadPreviewRenderMode.Unlit }, animationTimeSeconds: 0);
        var flatHdr = ReadFloatTarget(renderer, "Resolved");
        var flatBloom = ReadFloatTarget(renderer, "Bloom");
        var studio = HeadPreviewLightingPresets.Get(HeadPreviewLightingPreset.Studio);
        TestAssert.True(flatHdr.All(float.IsFinite) && flatBloom.All(float.IsFinite),
            "A constant emissive field produced a non-finite HDR or bloom value.");
        TestAssert.True(flatHdr.Take(3).Max() > studio.BloomThreshold + 2,
            "The constant field did not fully pass the bloom extraction threshold.");
        var maximumRelativeError = Enumerable.Range(0, flatBloom.Length).Where(index => index % 4 != 3)
            .Max(index => Math.Abs(flatBloom[index] - flatHdr[index % 4] * studio.Exposure) /
                          Math.Max(flatHdr[index % 4] * studio.Exposure, 0.001f));
        TestAssert.True(maximumRelativeError < 0.003f,
            $"Bloom changed a constant HDR field by {maximumRelativeError:P2}, beyond half-float rounding tolerance.");

        static float Sample(FloatTarget target, float x, float y)
        {
            x = Math.Clamp(x, 0, target.Width - 1);
            y = Math.Clamp(y, 0, target.Height - 1);
            var left = (int)x;
            var top = (int)y;
            var right = Math.Min(left + 1, target.Width - 1);
            var bottom = Math.Min(top + 1, target.Height - 1);
            var upper = float.Lerp(target.Values[(top * target.Width + left) * 4], target.Values[(top * target.Width + right) * 4], x - left);
            var lower = float.Lerp(target.Values[(bottom * target.Width + left) * 4], target.Values[(bottom * target.Width + right) * 4], x - left);
            return float.Lerp(upper, lower, y - top);
        }
    }

    private static void PostProcessSurvivesToggleAndResize()
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
            PostProcessSettings: settings);

        var first = renderer.Render(camera, options);
        var gradingTexture = GetTexture(renderer, "GradingLut");
        var resolvedTexture = GetTexture(renderer, "Resolved");
        var bloomTexture = GetTexture(renderer, "Bloom");
        var bloomTarget = GetSurfaceResource<RenderTargetView>(renderer, "Bloom", "Target");
        var bloomShaderView = GetSurfaceResource<ShaderResourceView>(renderer, "Bloom", "ShaderView");
        renderer.Resize(32, 24);
        renderer.Resize(32, 24);
        var repeated = renderer.Render(camera, options);
        TestAssert.True(first.BgraPixels.SequenceEqual(repeated.BgraPixels),
            "Repeated unchanged GameInspired frames or same-size resizes produced different display pixels.");
        TestAssert.True(ReferenceEquals(gradingTexture, GetTexture(renderer, "GradingLut")) &&
                        ReferenceEquals(resolvedTexture, GetTexture(renderer, "Resolved")) &&
                        ReferenceEquals(bloomTexture, GetTexture(renderer, "Bloom")) &&
                        ReferenceEquals(bloomTarget, GetSurfaceResource<RenderTargetView>(renderer, "Bloom", "Target")) &&
                        ReferenceEquals(bloomShaderView, GetSurfaceResource<ShaderResourceView>(renderer, "Bloom", "ShaderView")),
            "Unchanged frames or same-size resizes recreated the grading LUT, viewport images or bloom views.");

        renderer.Render(camera, options with { PostProcessingEnabled = false });
        var afterToggle = renderer.Render(camera, options);
        TestAssert.True(first.BgraPixels.SequenceEqual(afterToggle.BgraPixels),
            "Disabling and re-enabling postprocessing changed the next identical frame.");
        TestAssert.True(ReferenceEquals(gradingTexture, GetTexture(renderer, "GradingLut")),
            "Toggling postprocessing recreated the cached grading LUT.");

        renderer.Resize(7, 5);
        TestAssert.True(resolvedTexture.IsDisposed && bloomTexture.IsDisposed &&
                        bloomTarget.IsDisposed && bloomShaderView.IsDisposed,
            "Resizing retained an old viewport image or one of its render/sample views.");
        var resized = renderer.Render(camera, options);
        var resizedBloom = GetTexture(renderer, "Bloom");
        TestAssert.True(resized.Width == 7 && resized.Height == 5 && resized.BgraPixels.Length == 7 * 5 * 4,
            $"Small odd-size resize returned an invalid frame ({resized.Width}x{resized.Height}, {resized.BgraPixels.Length} bytes).");
        TestAssert.True(resizedBloom.Description.Width == 2 && resizedBloom.Description.Height == 2,
            $"Odd-size bloom target was not rounded up to quarter resolution ({resizedBloom.Description.Width}x{resizedBloom.Description.Height}).");
        TestAssert.True(!gradingTexture.IsDisposed && ReferenceEquals(gradingTexture, GetTexture(renderer, "GradingLut")),
            "Resizing recreated the viewport-independent grading LUT.");
    }

    private static HeadPreviewPostProcessSettings CreateBloomSettings(float screenBlendThreshold) => new(
        Vector3.Zero, Vector3.One, Vector3.One, Vector3.One, Vector3.Zero, 0f,
        screenBlendThreshold, 32f, Vector3.One);

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
        var rawHdr = ReadFloatTarget(renderer, "Resolved");
        var processedFrame = renderer.Render(camera, new HeadPreviewOptions(
            RenderMode: HeadPreviewRenderMode.Unlit,
            BackgroundColor: new Vector3(0.001f, 0.18f, 0.5f),
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessingEnabled: true));
        var processedHdr = ReadFloatTarget(renderer, "Resolved");

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
        var settings = HeadPreviewPostProcessProfiles.Get(HeadPreviewLightingPreset.Studio);
        var prepare = typeof(HeadPreviewRenderer).GetMethod(
            "PreparePostProcess", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Renderer post-process preparation method was not found.");
        var constants = prepare.Invoke(renderer, [rig, camera, null, settings, true])
            ?? throw new InvalidOperationException("Renderer post-process preparation returned no constants.");
        var method = typeof(HeadPreviewRenderer).GetMethod(
            "ApplyPostProcess", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Renderer post-process method was not found.");
        method.Invoke(renderer, [constants, false, settings]);
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
