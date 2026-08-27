using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the material renderer regression set.
public static class MaterialRendererTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("renderer updates a material texture without recreating its device", RendererUpdatesMaterialTexture),
        new("renderer uploads authored mip chains with anisotropic MSAA rendering", RendererUploadsAuthoredMipChain)
    ];

    private static void RendererUpdatesMaterialTexture()
    {
        var red = CreatePreviewMaterial("material", [255, 0, 0, 255]);
        var green = CreatePreviewMaterial("material", [0, 255, 0, 255]);
        var vertices = new[]
        {
            CreateVertex(new Vector3(-0.8f, -0.7f, 0)),
            CreateVertex(new Vector3(0.8f, -0.7f, 0)),
            CreateVertex(new Vector3(0, 0.8f, 0))
        };
        var mesh = new HeadPreviewMesh("Triangle", vertices, [0u, 1u, 2u], [new HeadPreviewSection(0, 3, 0, red)]);
        var scene = new HeadPreviewScene("Material test", [mesh], new HeadPreviewBounds(new Vector3(-1), new Vector3(1)));
        using var renderer = new HeadPreviewRenderer(96, 96);
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);
        var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
        TestAssert.Equal(1, renderer.TextureUploadCount);
        renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
        {
            [red.Key] = red with { Scalars = new Dictionary<string, float> { ["Test"] = 1 } }
        });
        renderer.Render(camera, new HeadPreviewOptions());
        TestAssert.Equal(1, renderer.TextureUploadCount);
        renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [green.Key] = green });
        var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
        TestAssert.True(!first.SequenceEqual(second), "Live texture update did not change the preview pixels.");
        TestAssert.Equal(1, renderer.DeviceCreationCount);
        TestAssert.Equal(2, renderer.TextureUploadCount);
    }

    private static void RendererUploadsAuthoredMipChain()
    {
        var top = Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray();
        var texture = new HeadPreviewTexture(
            "mipped", "Diffuse", 4, 4, top, TextureRole.Diffuse,
            TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false,
            [
                new DecodedTextureMip(4, 4, top),
                new DecodedTextureMip(2, 2, Enumerable.Repeat((byte)192, 2 * 2 * 4).ToArray()),
                new DecodedTextureMip(1, 1, [128, 128, 128, 255])
            ]);
        var material = new HeadPreviewMaterial(
            "mipped-material", "mipped-material", HeadMaterialFamily.Unknown,
            HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            new Dictionary<string, HeadPreviewTexture> { ["Diffuse"] = texture });
        var (renderer, camera) = CreateTriangleRenderer(material);
        using (renderer)
        {
            renderer.Render(camera, new HeadPreviewOptions());
            TestAssert.Equal(84L, renderer.GpuTextureBytes);
            TestAssert.Equal(8, renderer.MaterialAnisotropy);
            TestAssert.True(renderer.MultisampleCount >= 2, "No multisampled render target was selected.");
        }
    }
}
