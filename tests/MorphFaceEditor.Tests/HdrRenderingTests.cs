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

// HDR/GPU display-boundary checks plus pure grading-LUT contract checks.
public static class HdrRenderingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("HDR scene values survive above one and the brighter exposure stays brighter", HdrValuesSurviveAndExposureOrderingIsStable),
        new("HDR bloom thresholds above one create an off-surface halo", HdrBloomUsesValuesAboveOne),
        new("game-inspired grading changes presentation without changing the HDR scene", GameInspiredGradeChangesPresentationOnly),
        new("game-inspired response uses the shared LE2-style transfer for every game", GameInspiredTransfersUseSharedPowerGamma),
        new("game-inspired HDR bloom respects the receiver and excludes selected background", GameInspiredBloomRespectsBackgroundAndReceiver),
        new("game-inspired postprocess remains stable across mode switches and small resizes", GameInspiredPostProcessSurvivesModeSwitchAndResize),
        new("default game tone curves move LE1/LE2/LE3 contrast toward neutral", GameToneCurvesMoveTowardNeutral),
        new("grading LUT preserves cube corners and source clamp ordering", GradingLutPreservesCubeCornersAndClampOrder),
        new("mood grading distinguishes warm cool and cinematic colours without tinting black", MoodGradingPreservesBoundariesAndColourIntent),
        new("translucent colour blends in linear light", TranslucencyBlendsInLinearLight),
        new("unlit bypass returns raw sRGB while enabled postprocessing preserves HDR", UnlitPostProcessingCanBeBypassed),
        new("enabled unlit postoutput is independent of source-game material flags", UnlitPostOutputIgnoresSourceGameFlags),
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
            ShadowsEnabled: false,
            PostProcessingEnabled: false));

        var offset = PixelIndex(frame.Width, frame.Width / 2, frame.Height / 2) * 4;
        var red = frame.BgraPixels[offset + 2];
        var blue = frame.BgraPixels[offset];
        TestAssert.True(Math.Abs(red - 188) <= 3 && Math.Abs(blue - 188) <= 3,
            $"A half-alpha red hair texel over blue should encode the linear 50/50 mix near (188,0,188), got ({red},{frame.BgraPixels[offset + 1]},{blue}).");
    }

    private static void GameInspiredGradeChangesPresentationOnly()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var material = CreateSkinMaterial("grade-boundary-skin", new Vector4(0.16f, 0.16f, 0.16f, 1), [255, 255, 255, 255]);
        var scene = CreateScene([CreateQuad("grade-face", -0.8f, -0.8f, 0.8f, 0.8f, 0, material)]);
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);
        var settings = new HeadPreviewPostProcessSettings(
            shadows: Vector3.Zero,
            highlights: Vector3.One,
            midtones: Vector3.One,
            colorScale: new Vector3(1.2f, 0.8f, 0.55f),
            overlay: Vector3.Zero,
            desaturation: 0f,
            bloomScreenBlendThreshold: 10f,
            bloomRadius1080: 24f,
            bloomTint: Vector3.Zero);

        var gameFrame = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessMode: HeadPreviewPostProcessMode.GameInspired,
            PostProcessSettings: settings));
        var gameScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var gameDisplay = gameFrame.BgraPixels;

        var neutralFrame = renderer.Render(camera, new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessMode: HeadPreviewPostProcessMode.Neutral,
            PostProcessSettings: settings));
        var neutralScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var offset = PixelIndex(gameFrame.Width, gameFrame.Width / 2, gameFrame.Height / 2) * 4;

        TestAssert.True(gameScene.Zip(neutralScene).All(pair => Math.Abs(pair.First - pair.Second) < 0.002f),
            "Changing the post-process mode modified the resolved linear HDR scene instead of only changing presentation.");
        TestAssert.True(gameDisplay[offset + 2] > neutralFrame.BgraPixels[offset + 2] + 2 &&
                        gameDisplay[offset] + 2 < neutralFrame.BgraPixels[offset],
            $"The directional grade did not produce its expected red-to-blue shift (game RGB {gameDisplay[offset + 2]},{gameDisplay[offset + 1]},{gameDisplay[offset]} vs neutral {neutralFrame.BgraPixels[offset + 2]},{neutralFrame.BgraPixels[offset + 1]},{neutralFrame.BgraPixels[offset]}).");
    }

    private static void GameInspiredTransfersUseSharedPowerGamma()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var camera = new HeadOrbitCamera();
        var settings = CreateZeroBloomSettings(toneCurveStrength: 1f);
        var rig = HeadPreviewLightingPresets.Get(HeadPreviewLightingPreset.Studio);

        // All source-game material flags now share the accepted LE2-style native response.
        AssertTransfer("LE1 material", isLe2: false, isLe3: false, game: 2);
        AssertTransfer("LE2 material", isLe2: true, isLe3: false, game: 2);
        AssertTransfer("LE3 material", isLe2: false, isLe3: true, game: 2);

        void AssertTransfer(string label, bool isLe2, bool isLe3, int game)
        {
            var material = CreateSkinMaterial(
                $"gamma-{label}", new Vector4(0.28f, 0.28f, 0.28f, 1), [255, 255, 255, 255]) with
            {
                IsLe2 = isLe2,
                IsLe3 = isLe3
            };
            var scene = CreateScene([CreateQuad($"gamma-{label}-face", -0.85f, -0.85f, 0.85f, 0.85f, 0, material)]);
            renderer.SetScene(scene);
            camera.Fit(scene.Bounds);
            var frame = renderer.Render(camera, new HeadPreviewOptions(
                AmbientOcclusionEnabled: false,
                ShadowsEnabled: false,
                PostProcessMode: HeadPreviewPostProcessMode.GameInspired,
                PostProcessSettings: settings));
            var hdr = ReadFloatTarget(renderer, "_resolvedTexture");
            var pixel = PixelIndex(frame.Width, frame.Width / 2, frame.Height / 2);
            var source = hdr.AsSpan(pixel * 4, 3);

            for (var channel = 0; channel < 3; channel++)
            {
                var exposed = Math.Max(source[channel] * rig.Exposure, 0f);
                if (game <= 2)
                    exposed = 1f - MathF.Pow(2f, -1.7f * exposed);
                if (game >= 2)
                {
                    var x = Math.Max(exposed - 0.004f, 0f);
                    var mapped = (x * (6.2f * x + 0.5f)) / (x * (6.2f * x + 1.7f) + 0.06f);
                    exposed = mapped * mapped;
                }

                var expectedByte = (int)MathF.Round(MathF.Pow(Math.Clamp(exposed, 0f, 1f), 1f / 2.2f) * 255f);
                var displayChannel = channel switch { 0 => 2, 1 => 1, _ => 0 };
                var actualByte = frame.BgraPixels[pixel * 4 + displayChannel];
                TestAssert.True(Math.Abs(actualByte - expectedByte) <= 3,
                    $"{label} channel {channel} did not match its legacy/filmic transfer followed by one 1/2.2 power ({actualByte} vs {expectedByte}; HDR {source[channel]:F4}, exposure {rig.Exposure:F3}).");
            }
        }
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

    private static void GameToneCurvesMoveTowardNeutral()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var games = new[]
        {
            (Name: "LE1", IsLe2: false, IsLe3: false),
            (Name: "LE2", IsLe2: true, IsLe3: false),
            (Name: "LE3", IsLe2: false, IsLe3: true)
        };
        var greyLevels = new[] { 0.07f, 0.28f, 1.15f };
        var camera = new HeadOrbitCamera();
        var rig = HeadPreviewLightingPresets.Get(HeadPreviewLightingPreset.Studio);

        foreach (var game in games)
        {
            var defaultTotalDistance = 0d;
            var fullTotalDistance = 0d;
            foreach (var grey in greyLevels)
            {
                var material = CreateSkinMaterial(
                    $"tone-{game.Name}-{grey:F2}", new Vector4(grey, grey, grey, 1), [255, 255, 255, 255]) with
                {
                    IsLe2 = game.IsLe2,
                    IsLe3 = game.IsLe3
                };
                var scene = CreateScene([CreateQuad($"tone-face-{game.Name}-{grey:F2}", -0.85f, -0.85f, 0.85f, 0.85f, 0, material)]);
                renderer.SetScene(scene);
                camera.Fit(scene.Bounds);

                var neutral = renderer.Render(camera, new HeadPreviewOptions(
                    AmbientOcclusionEnabled: false,
                    ShadowsEnabled: false,
                    PostProcessMode: HeadPreviewPostProcessMode.Neutral));
                var neutralHdr = ReadFloatTarget(renderer, "_resolvedTexture");
                ApplyDisplayExposure(renderer, rig with { BloomIntensity = 0f }, camera);
                var neutralNoBloom = ReadDisplayPixels(renderer);

                var identityGrade = renderer.Render(camera, new HeadPreviewOptions(
                    AmbientOcclusionEnabled: false,
                    ShadowsEnabled: false,
                    PostProcessMode: HeadPreviewPostProcessMode.GameInspired,
                    PostProcessSettings: CreateZeroBloomSettings(toneCurveStrength: 0f)));
                var identityHdr = ReadFloatTarget(renderer, "_resolvedTexture");
                var maximumDisplayDifference = 0;
                var maximumDifferenceOffset = 0;
                for (var offset = 0; offset < neutralNoBloom.Length; offset++)
                {
                    var difference = Math.Abs(neutralNoBloom[offset] - identityGrade.BgraPixels[offset]);
                    if (difference > maximumDisplayDifference)
                    {
                        maximumDisplayDifference = difference;
                        maximumDifferenceOffset = offset;
                    }
                }
                var centerOffset = PixelIndex(neutral.Width, neutral.Width / 2, neutral.Height / 2) * 4;
                TestAssert.True(maximumDisplayDifference <= 2,
                    $"{game.Name} tone-curve strength zero did not match Neutral for grey level {grey:F2}: max diff {maximumDisplayDifference} at pixel ({maximumDifferenceOffset / 4 % neutral.Width},{maximumDifferenceOffset / 4 / neutral.Width}), channel {maximumDifferenceOffset % 4} expected {neutralNoBloom[maximumDifferenceOffset]}, actual {identityGrade.BgraPixels[maximumDifferenceOffset]}; center BGRA expected {neutralNoBloom[centerOffset]},{neutralNoBloom[centerOffset + 1]},{neutralNoBloom[centerOffset + 2]},{neutralNoBloom[centerOffset + 3]}, actual {identityGrade.BgraPixels[centerOffset]},{identityGrade.BgraPixels[centerOffset + 1]},{identityGrade.BgraPixels[centerOffset + 2]},{identityGrade.BgraPixels[centerOffset + 3]}, HDR center {neutralHdr[centerOffset]:F4},{neutralHdr[centerOffset + 1]:F4},{neutralHdr[centerOffset + 2]:F4}.");
                TestAssert.True(neutralHdr.Zip(identityHdr).All(pair => Math.Abs(pair.First - pair.Second) < 0.002f),
                    $"{game.Name} tone-curve strength zero changed the resolved HDR scene for grey level {grey:F2}.");

                var defaultCurve = renderer.Render(camera, new HeadPreviewOptions(
                    AmbientOcclusionEnabled: false,
                    ShadowsEnabled: false,
                    PostProcessMode: HeadPreviewPostProcessMode.GameInspired,
                    PostProcessSettings: CreateZeroBloomSettings()));
                var fullCurve = renderer.Render(camera, new HeadPreviewOptions(
                    AmbientOcclusionEnabled: false,
                    ShadowsEnabled: false,
                    PostProcessMode: HeadPreviewPostProcessMode.GameInspired,
                    PostProcessSettings: CreateZeroBloomSettings(toneCurveStrength: 1f)));

                var pixelOffset = PixelIndex(neutral.Width, neutral.Width / 2, neutral.Height / 2) * 4;
                var neutralLuma = Luma(neutralNoBloom, pixelOffset);
                var defaultDistance = Math.Abs(Luma(defaultCurve.BgraPixels, pixelOffset) - neutralLuma);
                var fullDistance = Math.Abs(Luma(fullCurve.BgraPixels, pixelOffset) - neutralLuma);
                defaultTotalDistance += defaultDistance;
                fullTotalDistance += fullDistance;
                TestAssert.True(defaultDistance <= fullDistance + 1,
                    $"{game.Name} default tone strength moved grey level {grey:F2} farther from Neutral than full native strength ({defaultDistance:F1} vs {fullDistance:F1} luma levels; exposure {rig.Exposure:F2}).");
            }

            TestAssert.True(defaultTotalDistance + 2 < fullTotalDistance,
                $"{game.Name} default tone strength did not reduce combined dark/mid/bright contrast from Neutral ({defaultTotalDistance} vs {fullTotalDistance} luma levels).");
        }
    }

    private static void GradingLutPreservesCubeCornersAndClampOrder()
    {
        const int width = 256;
        int Offset(int red, int green, int blue) => ((green * width) + blue * 16 + red) * 4;

        var neutral = HeadPreviewPostProcessProfiles.BuildGradingLut(new HeadPreviewPostProcessSettings());
        for (var r = 0; r < 16; r++)
        for (var g = 0; g < 16; g++)
        for (var b = 0; b < 16; b++)
        {
            var offset = Offset(r, g, b);
            TestAssert.True(neutral[offset] == r / 15f && neutral[offset + 1] == g / 15f
                && neutral[offset + 2] == b / 15f && neutral[offset + 3] == 1f,
                "The neutral reference LUT no longer preserves the entire RGB cube exactly.");
        }
        var red = Offset(15, 0, 0);
        var white = Offset(15, 15, 15);

        var clampedSettings = new HeadPreviewPostProcessSettings(
            new Vector3(-0.25f), new Vector3(2f), Vector3.One, Vector3.One, Vector3.Zero,
            0f, 10f, 24f, Vector3.Zero);
        var clamped = HeadPreviewPostProcessProfiles.BuildGradingLut(clampedSettings);
        TestAssert.True(Math.Abs(clamped[white] - 0.5f) < 0.0001f &&
                        Math.Abs(clamped[white + 1] - 0.5f) < 0.0001f &&
                        Math.Abs(clamped[white + 2] - 0.5f) < 0.0001f,
            $"H/M/S grading did not clamp the shadow-shifted input before dividing by highlights (white became {clamped[white]:F3},{clamped[white + 1]:F3},{clamped[white + 2]:F3}; expected 0.5 each).");

        var desaturatedSettings = new HeadPreviewPostProcessSettings(
            Vector3.Zero, Vector3.One, Vector3.One, Vector3.One, Vector3.Zero,
            1f, 10f, 24f, Vector3.Zero);
        var desaturated = HeadPreviewPostProcessProfiles.BuildGradingLut(desaturatedSettings);
        TestAssert.True(Math.Abs(desaturated[red] - desaturated[red + 1]) < 0.0001f &&
                        Math.Abs(desaturated[red + 1] - desaturated[red + 2]) < 0.0001f,
            $"Full desaturation did not make the red cube corner neutral ({desaturated[red]:F3},{desaturated[red + 1]:F3},{desaturated[red + 2]:F3}).");
    }

    private static void MoodGradingPreservesBoundariesAndColourIntent()
    {
        static Vector3 Sample(float[] lut, int r, int g, int b)
        {
            var offset = (g * 256 + b * 16 + r) * 4;
            return new Vector3(lut[offset], lut[offset + 1], lut[offset + 2]);
        }
        static float Luma(Vector3 rgb) => Vector3.Dot(rgb, new Vector3(0.299f, 0.587f, 0.114f));
        var tintOnly = new HeadPreviewPostProcessSettings(Vector3.Zero, Vector3.One, Vector3.One,
            Vector3.One, Vector3.Zero, 0f, 10f, 24f, Vector3.One,
            shadowTint: new Vector3(0.75f, 0.95f, 1.3f), highlightTint: new Vector3(1.1f, 1f, 0.8f));
        var tintLut = HeadPreviewPostProcessProfiles.BuildGradingLut(tintOnly);
        TestAssert.Near(Vector3.Zero, Sample(tintLut, 0, 0, 0), 1e-7f);
        TestAssert.Near(Vector3.One, Sample(tintLut, 15, 15, 15), 1e-6f);
        foreach (var (r, g, b) in new[] { (2, 2, 2), (10, 10, 10), (7, 3, 2) })
        {
            var source = new Vector3(r, g, b) / 15f;
            TestAssert.True(Math.Abs(Luma(Sample(tintLut, r, g, b)) - Luma(source)) < 1e-6f,
                "Split toning changed luminance instead of colour.");
        }
        var bright = Sample(tintLut, 12, 12, 12);
        var nearWhite = Sample(tintLut, 14, 14, 14);
        TestAssert.True(nearWhite.X - nearWhite.Z < bright.X - bright.Z,
            "Highlight tint did not fade as the input approached white.");
        var warm = HeadPreviewPostProcessProfiles.BuildGradingLut(HeadPreviewPostProcessProfiles.Get(HeadPreviewLightingPreset.WarmInterior));
        var cool = HeadPreviewPostProcessProfiles.BuildGradingLut(HeadPreviewPostProcessProfiles.Get(HeadPreviewLightingPreset.CoolNight));
        var cinematic = HeadPreviewPostProcessProfiles.BuildGradingLut(HeadPreviewPostProcessProfiles.Get(HeadPreviewLightingPreset.Cinematic));
        var warmMid = Sample(warm, 8, 8, 8);
        var coolDark = Sample(cool, 3, 3, 3);
        var cinemaDark = Sample(cinematic, 3, 3, 3);
        var cinemaBright = Sample(cinematic, 11, 11, 11);
        TestAssert.True(warmMid.X - warmMid.Z > 0.10f && coolDark.Z - coolDark.X > 0.05f,
            "Warm and cool grades lost their visible colour separation.");
        TestAssert.True(cinemaDark.Z - cinemaDark.X > 0.02f && cinemaBright.X - cinemaBright.Z > 0.05f,
            "Cinematic grading lost its cool shadows and warm highlights.");
        foreach (var preset in Enum.GetValues<HeadPreviewLightingPreset>())
        {
            var settings = HeadPreviewPostProcessProfiles.Get(preset);
            TestAssert.True(settings.ToneCurveStrength == 0.6f, "Preset grading changed the shared accepted tone response.");
            var lut = HeadPreviewPostProcessProfiles.BuildGradingLut(settings);
            TestAssert.True(lut.All(value => float.IsFinite(value) && value is >= 0f and <= 1f),
                $"{preset} grading produced values outside the finite display cube.");
            TestAssert.Near(Vector3.Zero, Sample(lut, 0, 0, 0), 1e-7f);
        }
    }

    private static HeadPreviewPostProcessSettings CreateZeroBloomSettings(float toneCurveStrength = 0.6f) => new(
        Vector3.Zero, Vector3.One, Vector3.One, Vector3.One, Vector3.Zero, 0f, 10f, 24f, Vector3.Zero,
        toneCurveStrength);

    private static HeadPreviewPostProcessSettings CreateBloomSettings(float screenBlendThreshold) => new(
        Vector3.Zero, Vector3.One, Vector3.One, Vector3.One, Vector3.Zero, 0f,
        screenBlendThreshold, 32f, Vector3.One);

    private static object GetPrivateField(object instance, string fieldName) =>
        typeof(HeadPreviewRenderer).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException($"Renderer field '{fieldName}' was not found.");

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

    private static void UnlitPostOutputIgnoresSourceGameFlags()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var diffuse = new HeadPreviewTexture(
            "unlit-game-flag-diffuse", "HAIR_Diff", 1, 1, [64, 144, 216, 255],
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false);
        var material = new HeadPreviewMaterial(
            "unlit-game-flag-hair", "unlit-game-flag-hair", HeadMaterialFamily.Hair,
            HeadMaterialBlendMode.Opaque, true,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            new Dictionary<string, HeadPreviewTexture> { ["HAIR_Diff"] = diffuse });
        var camera = new HeadOrbitCamera();
        var options = new HeadPreviewOptions(
            RenderMode: HeadPreviewRenderMode.Unlit,
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessingEnabled: true,
            PostProcessMode: HeadPreviewPostProcessMode.GameInspired);

        byte[]? baseline = null;
        foreach (var (label, isLe2, isLe3) in new[]
                 {
                     ("unqualified", false, false),
                     ("LE2", true, false),
                     ("LE3", false, true),
                     ("both", true, true)
                 })
        {
            var flaggedMaterial = material with { IsLe2 = isLe2, IsLe3 = isLe3 };
            var scene = CreateScene([CreateQuad($"unlit-game-flags-{label}", -0.8f, -0.8f, 0.8f, 0.8f, 0, flaggedMaterial)]);
            renderer.SetScene(scene);
            camera.Fit(scene.Bounds);
            var frame = renderer.Render(camera, options);
            if (baseline is null)
            {
                baseline = frame.BgraPixels;
                continue;
            }

            TestAssert.True(baseline.SequenceEqual(frame.BgraPixels),
                $"Enabled Unlit output changed when the same opaque hair material carried {label} source-game flags.");
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
