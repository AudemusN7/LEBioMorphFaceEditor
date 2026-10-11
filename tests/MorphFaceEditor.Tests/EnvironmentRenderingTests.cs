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

/// <summary>GPU checks for the SH environment's route into resolved diffuse lighting.</summary>
public static class EnvironmentRenderingTests
{
    private const float SqrtFourPi = 3.5449077f;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("SH environment drives world-space diffuse and matches CPU evaluation", EnvironmentMatchesCpuEvaluation),
        new("lighting-rig SH environment reaches the diffuse target", LightingRigEnvironmentReachesDiffuseTarget),
        new("attachment diffuse uses SH while its reflection stays separate", AttachmentDiffuseUsesEnvironment),
        new("SH environment does not affect unlit pixels, background, or shadow draws", EnvironmentIsBypassedForUnlitAndBackground)
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
        var constantScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var constantAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");
        var variedFrame = renderer.Render(camera, options with { Environment = oriented });
        var variedScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var variedAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");

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

    private static void LightingRigEnvironmentReachesDiffuseTarget()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var material = CreateSkinMaterial("sh-rig", Vector4.One);
        var camera = new HeadOrbitCamera();
        var reference = ConstantEnvironment(Vector3.One);
        var rigEnvironment = HeadPreviewEnvironment.FromLightingRig(
            HeadPreviewLightingPresets.Get(HeadPreviewLightingPreset.Studio));
        var options = new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: false,
            PostProcessingEnabled: false,
            Environment: reference);

        var hemispheres = new Dictionary<int, float[]>();
        foreach (var (label, normal) in new[]
                 {
                     ("sky", Vector3.UnitY),
                     ("ground", -Vector3.UnitY)
                 })
        {
            var scene = CreateScene(CreateQuad($"sh-rig-{label}-quad", material, normal));
            renderer.SetScene(scene);
            camera.Fit(scene.Bounds);
            renderer.Render(camera, options);
            var referenceAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");
            renderer.Render(camera, options with { Environment = rigEnvironment });
            var rigAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");
            var offset = PixelIndex(96, 48, 48) * 4;
            var expected = rigEnvironment.Evaluate(normal);
            var sample = new float[3];
            for (var channel = 0; channel < 3; channel++)
            {
                var value = rigAmbient[offset + channel];
                var expectedValue = referenceAmbient[offset + channel] * Component(expected, channel);
                TestAssert.True(Math.Abs(value - expectedValue) < 0.002f,
                    $"The generated lighting-rig environment did not reach {label} diffuse channel {channel}: got {value:F3}, expected {expectedValue:F3}.");
                sample[channel] = value;
            }
            hemispheres[label == "sky" ? 1 : 0] = sample;
        }

        TestAssert.True(Enumerable.Range(0, 3).All(channel => hemispheres[1][channel] > hemispheres[0][channel]),
            "The generated studio environment did not preserve the brighter sky direction in the GPU diffuse target.");
    }

    private static void AttachmentDiffuseUsesEnvironment()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var normal = Vector3.Normalize(new Vector3(0.31f, 0.58f, 0.75f));
        var material = CreateHatMaterial("sh-attachment") with
        {
            Scalars = new Dictionary<string, float> { ["CubeMap_Intensity_Scalar"] = 1f },
            FixedCubeTexture = CreateSolidCube("sh-attachment-cube", [210, 150, 90, 255])
        };
        var scene = CreateScene(CreateQuad("sh-attachment-quad", material, normal, isAttachment: true));
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);
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
        var constantScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var constantAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");
        renderer.Render(camera, options with { Environment = oriented });
        var orientedScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var orientedAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");
        renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
        {
            [material.Key] = material with { FixedCubeTexture = null }
        });
        renderer.Render(camera, options with { Environment = oriented });
        var noCubeScene = ReadFloatTarget(renderer, "_resolvedTexture");
        var noCubeAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");

        var offset = PixelIndex(96, 48, 48) * 4;
        var expected = oriented.Evaluate(normal);
        for (var channel = 0; channel < 3; channel++)
        {
            var expectedValue = constantAmbient[offset + channel] * Component(expected, channel);
            TestAssert.True(Math.Abs(orientedAmbient[offset + channel] - expectedValue) < 0.002f,
                $"Attachment diffuse channel {channel} did not follow the SH environment: got {orientedAmbient[offset + channel]:F3}, expected {expectedValue:F3}.");

            var reflectedAndDirect = constantScene[offset + channel] - constantAmbient[offset + channel];
            var changedReflectedAndDirect = orientedScene[offset + channel] - orientedAmbient[offset + channel];
            TestAssert.True(Math.Abs(changedReflectedAndDirect - reflectedAndDirect) < 0.003f,
                $"Environment changes leaked into the attachment reflection/direct term in channel {channel}.");
            TestAssert.True(Math.Abs(noCubeAmbient[offset + channel] - orientedAmbient[offset + channel]) < 0.002f,
                $"Removing the attachment cube changed diffuse ambient channel {channel}.");
        }
        TestAssert.True(Enumerable.Range(0, 3).Any(channel =>
                orientedScene[offset + channel] - noCubeScene[offset + channel] > 0.01f),
            "The attachment cube did not produce a visible reflected contribution outside the SH diffuse target.");
    }

    private static void EnvironmentIsBypassedForUnlitAndBackground()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var material = CreateSkinMaterial("sh-unlit", Vector4.One);
        var scene = CreateScene(CreateQuad("sh-unlit-quad", material, Vector3.UnitZ));
        renderer.SetScene(scene);
        var camera = CreateCamera(scene.Bounds);
        var bright = ConstantEnvironment(new Vector3(2f, 1.5f, 1f));
        var coloured = new HeadPreviewEnvironment(
            new Vector3(SqrtFourPi * 0.2f, SqrtFourPi * 0.7f, SqrtFourPi * 1.1f),
            new Vector3(0.7f, -0.4f, 0.2f),
            new Vector3(-0.2f, 0.6f, 0.3f),
            new Vector3(0.4f, 0.1f, -0.5f),
            Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);
        var options = new HeadPreviewOptions(
            RenderMode: HeadPreviewRenderMode.Unlit,
            BackgroundColor: new Vector3(0.12f, 0.24f, 0.36f),
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: true,
            PostProcessingEnabled: false,
            Environment: bright);

        var first = renderer.Render(camera, options);
        var firstAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");
        var second = renderer.Render(camera, options with { Environment = coloured });
        var secondAmbient = ReadFloatTarget(renderer, "_resolvedAmbientTexture");

        TestAssert.True(first.BgraPixels.SequenceEqual(second.BgraPixels),
            "Changing the environment changed the Unlit image.");
        TestAssert.True(first.ShadowDrawCalls == 0 && second.ShadowDrawCalls == 0,
            "Unlit rendering unexpectedly performed key-shadow draws while an SH environment was active.");

        var faceOffset = PixelIndex(96, 48, 48) * 4;
        var backgroundOffset = PixelIndex(96, 2, 2) * 4;
        for (var channel = 0; channel < 3; channel++)
        {
            TestAssert.True(Math.Abs(firstAmbient[faceOffset + channel]) < 0.001f &&
                            Math.Abs(secondAmbient[faceOffset + channel]) < 0.001f,
                $"Unlit rendering wrote environment light into ambient target channel {channel}.");
            TestAssert.True(first.BgraPixels[backgroundOffset + channel] == second.BgraPixels[backgroundOffset + channel],
                $"Changing the environment changed background byte channel {channel}.");
        }
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

    private static HeadPreviewMaterial CreateHatMaterial(string key)
    {
        var diffuse = new HeadPreviewTexture(
            $"{key}-diff", "HMN_HAT_Diff", 1, 1, [255, 255, 255, 255],
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, true);
        var normal = new HeadPreviewTexture(
            $"{key}-norm", "HMN_HAT_Norm", 1, 1, [128, 128, 255, 255],
            TextureRole.Normal, TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, true);
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Accessory, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            new Dictionary<string, HeadPreviewTexture>
            {
                ["HMN_HAT_Diff"] = diffuse,
                ["HMN_HAT_Norm"] = normal
            })
        {
            PreviewModel = HumanMaterialPreviewModel.Hat
        };
    }

    private static HeadPreviewCubeTexture CreateSolidCube(string key, byte[] rgba) => new(
        key,
        1,
        Enumerable.Range(0, 6).Select(_ => rgba.ToArray()).ToArray(),
        TextureColorSpace.Srgb);

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

    private static float Component(Vector3 value, int channel) => channel switch
    {
        0 => value.X,
        1 => value.Y,
        2 => value.Z,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };
}
