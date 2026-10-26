using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;

using static MorphFaceEditor.Tests.RenderTestReadback;

namespace MorphFaceEditor.Tests;

/// <summary>GPU checks for the SH environment's route into resolved diffuse lighting.</summary>
public static class EnvironmentRenderingTests
{
    private const float SqrtFourPi = 3.5449077f;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("SH environment drives world-space diffuse and matches CPU evaluation", EnvironmentMatchesCpuEvaluation),
        new("preset room bounce follows the camera while explicit environments stay world-space", PresetEnvironmentFollowsCamera)
    ];

    private static void EnvironmentMatchesCpuEvaluation()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var material = CreateSkinMaterial("sh-cpu-match", new Vector4(0.72f, 0.63f, 0.51f, 1));
        var tiltedNormal = Vector3.Normalize(new Vector3(0.31f, 0.58f, 0.75f));
        var scene = CreateScene(CreateQuad("sh-cpu-match-quad", material, tiltedNormal));
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);

        // A constant-radiance reference gives us the material's resolved ambient
        // albedo directly. Every other term is small, distinct and in the documented
        // [Y00,z,y,x,xz,zy,3y^2-1,xy,x^2-z^2] order.
        var constant = ConstantEnvironment(Vector3.One);
        var oriented = new HeadPreviewEnvironment(
            new Vector3(SqrtFourPi),
            new Vector3(0.11f, -0.025f, 0.035f),
            new Vector3(-0.03f, 0.09f, 0.015f),
            new Vector3(0.07f, 0.02f, -0.08f),
            new Vector3(0.035f, -0.045f, 0.055f),
            new Vector3(-0.02f, 0.03f, 0.045f),
            new Vector3(0.05f, 0.015f, -0.025f),
            new Vector3(-0.04f, 0.055f, 0.02f),
            new Vector3(0.025f, -0.035f, 0.04f));
        var options = new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessingEnabled: false,
            Environment: constant);

        renderer.Render(camera, options);
        var constantScene = ReadFloatTarget(renderer, "Resolved");
        var constantAmbient = ReadFloatTarget(renderer, "ResolvedAmbient");
        var variedFrame = renderer.Render(camera, options with { Environment = oriented });
        var variedScene = ReadFloatTarget(renderer, "Resolved");
        var variedAmbient = ReadFloatTarget(renderer, "ResolvedAmbient");

        // Flat normal and centre pixel avoid interpolation and silhouette edges.
        var offset = PixelIndex(96, 48, 48) * 4;
        var expectedEnvironment = oriented.Evaluate(tiltedNormal);
        for (var channel = 0; channel < 3; channel++)
        {
            var expected = constantAmbient[offset + channel] * Component(expectedEnvironment, channel);
            TestAssert.True(Math.Abs(variedAmbient[offset + channel] - expected) < 0.002f,
                $"SH ambient channel {channel} did not match the CPU-evaluated world-space basis: got {variedAmbient[offset + channel]:F3}, expected {expected:F3}.");

            var sceneDelta = variedScene[offset + channel] - constantScene[offset + channel];
            var ambientDelta = variedAmbient[offset + channel] - constantAmbient[offset + channel];
            TestAssert.True(Math.Abs(sceneDelta - ambientDelta) < 0.003f,
                $"Environment channel {channel} changed the direct-light contribution as well as diffuse ambient (scene delta {sceneDelta:F3}, ambient delta {ambientDelta:F3}).");
        }

        TestAssert.True(variedFrame.ShadowDrawCalls == 0,
            "Disabling key shadows should keep the synthetic SH comparison free of shadow-map draws.");
    }

    private static void PresetEnvironmentFollowsCamera()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var normal = Vector3.Normalize(new Vector3(0.31f, 0.58f, 0.75f));
        var scene = CreateScene(CreateQuad("sh-camera-quad", CreateSkinMaterial("sh-camera", Vector4.One), normal));
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);
        var environment = HeadPreviewLightingPresets.GetEnvironment(HeadPreviewLightingPreset.Studio);
        var options = new HeadPreviewOptions(AmbientOcclusionEnabled: false, ShadowsEnabled: false,
            PostProcessingEnabled: false, Environment: ConstantEnvironment(Vector3.One));
        var offset = PixelIndex(96, 48, 48) * 4;
        var orientationDifference = 0f;
        foreach (var yaw in new[] { 0f, 0.6f })
        {
            camera.Orbit(yaw, yaw * 0.2f);
            renderer.Render(camera, options);
            var albedo = ReadFloatTarget(renderer, "ResolvedAmbient");
            renderer.Render(camera, options with { Environment = null });
            var presetAmbient = ReadFloatTarget(renderer, "ResolvedAmbient");
            renderer.Render(camera, options with { Environment = environment });
            var worldAmbient = ReadFloatTarget(renderer, "ResolvedAmbient");
            var rigNormal = new Vector3(
                Vector3.Dot(normal, camera.TransformCameraDirectionToWorld(Vector3.UnitX)),
                Vector3.Dot(normal, camera.TransformCameraDirectionToWorld(Vector3.UnitY)),
                Vector3.Dot(normal, camera.TransformCameraDirectionToWorld(Vector3.UnitZ)));
            var rigExpected = environment.Evaluate(rigNormal);
            var worldExpected = environment.Evaluate(normal);
            for (var channel = 0; channel < 3; channel++)
            {
                TestAssert.True(Math.Abs(presetAmbient[offset + channel] - albedo[offset + channel] * Component(rigExpected, channel)) < 0.002f,
                    $"Preset room bounce did not match the camera-relative basis in channel {channel} at yaw {yaw}.");
                TestAssert.True(Math.Abs(worldAmbient[offset + channel] - albedo[offset + channel] * Component(worldExpected, channel)) < 0.002f,
                    $"Explicit environment rotated with the camera in channel {channel} at yaw {yaw}.");
                if (yaw != 0) orientationDifference += Math.Abs(presetAmbient[offset + channel] - worldAmbient[offset + channel]);
            }
        }
        TestAssert.True(orientationDifference > 0.01f, "The camera test did not exercise distinguishable environment orientations.");
    }

    private static HeadPreviewEnvironment ConstantEnvironment(Vector3 radiance) => new(
        radiance * SqrtFourPi,
        Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero,
        Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);

    private static HeadPreviewMaterial CreateSkinMaterial(string key, Vector4 tint)
    {
        var diffuse = new HeadPreviewTexture(
            $"{key}-diff", "HED_Diff", 1, 1, [255, 255, 255, 255],
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false);
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4> { ["SkinTone"] = tint },
            new Dictionary<string, HeadPreviewTexture> { ["HED_Diff"] = diffuse });
    }

    private static HeadPreviewScene CreateScene(HeadPreviewMesh mesh) => new(
        "SH environment integration scene",
        [mesh],
        new HeadPreviewBounds(new Vector3(-1, -1, -0.1f), new Vector3(1, 1, 0.1f)));

    private static HeadPreviewMesh CreateQuad(
        string name,
        HeadPreviewMaterial material,
        Vector3 normal,
        bool isAttachment = false)
    {
        var referenceAxis = Math.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var tangent = Vector3.Normalize(Vector3.Cross(referenceAxis, normal));
        var handedness = Vector3.Dot(Vector3.Cross(normal, tangent), Vector3.UnitY) < 0 ? -1f : 1f;
        var tangent4 = new Vector4(tangent, handedness);
        var vertices = new[]
        {
            Vertex(new Vector3(-0.8f, -0.8f, 0), normal, tangent4, new Vector2(0, 1)),
            Vertex(new Vector3( 0.8f, -0.8f, 0), normal, tangent4, new Vector2(1, 1)),
            Vertex(new Vector3( 0.8f,  0.8f, 0), normal, tangent4, new Vector2(1, 0)),
            Vertex(new Vector3(-0.8f,  0.8f, 0), normal, tangent4, new Vector2(0, 0))
        };
        return new HeadPreviewMesh(name, vertices, [0u, 1u, 2u, 0u, 2u, 3u],
            [new HeadPreviewSection(0, 6, 0, material)], IsAttachment: isAttachment);
    }

    private static HeadPreviewVertex Vertex(Vector3 position, Vector3 normal, Vector4 tangent, Vector2 uv) => new(
        position, normal, tangent, uv,
        0, 0, 0, 0, new Vector4(1, 0, 0, 0));

    private static HeadOrbitCamera CreateCamera(HeadPreviewBounds bounds)
    {
        var camera = new HeadOrbitCamera();
        camera.Fit(bounds);
        return camera;
    }

    private static int PixelIndex(int width, int x, int y) => y * width + x;

    private static float Component(Vector3 value, int channel) => channel switch
    {
        0 => value.X,
        1 => value.Y,
        2 => value.Z,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };
}
