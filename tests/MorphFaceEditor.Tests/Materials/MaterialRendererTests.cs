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
        new("renderer uploads authored mip chains with anisotropic MSAA rendering", RendererUploadsAuthoredMipChain),
        new("renderer samples a shared texture as both colour and linear data", RendererSamplesSharedTextureByColorSpace)
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
        renderer.SetScene(scene);
        var republished = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
        TestAssert.True(first.SequenceEqual(republished), "Republishing the same scene changed its material output.");
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
        TestAssert.Equal(4L, renderer.GpuTextureBytes);

        var original = HumanMaterialTestFixtures.CreateFemaleSkinMaterial(
            red.Key, browStrength: 0, makeupRgba: [0, 255, 255, 0], eyeStrength: 1);
        var vectorEdit = original with
        {
            Vectors = new Dictionary<string, Vector4>(original.Vectors)
            {
                ["HED_EyeShadow_Tint_Vector"] = new(0.8f, 0.1f, 0.05f, 1)
            }
        };
        var scalarEdit = vectorEdit with
        {
            Scalars = new Dictionary<string, float>(vectorEdit.Scalars)
            {
                ["HED_EyeShadow_Tint_Scalar"] = 0
            }
        };
        var originalPixels = UpdateAndCompare(original);
        var vectorPixels = UpdateAndCompare(vectorEdit);
        TestAssert.True(!originalPixels.SequenceEqual(vectorPixels),
            "The same-key tint vector replacement did not change the visible material.");
        var scalarPixels = UpdateAndCompare(scalarEdit);
        TestAssert.True(!vectorPixels.SequenceEqual(scalarPixels),
            "The same-key tint scalar replacement did not change the visible material.");
        TestAssert.True(originalPixels.SequenceEqual(UpdateAndCompare(original)),
            "Reverting material constants did not restore their original output.");
        renderer.ClearScene();
        renderer.SetScene(SceneWithMaterial(original));
        AssertMatchesFresh(original);
        TestAssert.Equal(1, renderer.DeviceCreationCount);

        HeadPreviewScene SceneWithMaterial(HeadPreviewMaterial material) => scene with
        {
            Meshes = [mesh with { Sections = [new HeadPreviewSection(0, 3, 0, material)] }]
        };

        byte[] UpdateAndCompare(HeadPreviewMaterial material)
        {
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [material.Key] = material });
            return AssertMatchesFresh(material);
        }

        byte[] AssertMatchesFresh(HeadPreviewMaterial material)
        {
            using var fresh = new HeadPreviewRenderer(96, 96);
            fresh.SetScene(SceneWithMaterial(material));
            byte[] unlitPixels = [];
            foreach (var mode in new[] { HeadPreviewRenderMode.Shaded, HeadPreviewRenderMode.Unlit,
                         HeadPreviewRenderMode.Diagnostic, HeadPreviewRenderMode.Shaded })
            {
                var options = new HeadPreviewOptions(mode, AmbientOcclusionEnabled: false,
                    ShadowsEnabled: false, PostProcessingEnabled: false);
                var actual = renderer.Render(camera, options, animationTimeSeconds: 0).BgraPixels;
                var expected = fresh.Render(camera, options, animationTimeSeconds: 0).BgraPixels;
                TestAssert.True(expected.SequenceEqual(actual),
                    $"Updated material constants differ from an independently loaded scene in {mode} mode.");
                if (mode == HeadPreviewRenderMode.Unlit) unlitPixels = actual;
            }
            return unlitPixels;
        }
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

    private static void RendererSamplesSharedTextureByColorSpace()
    {
        var material = SpeciesMaterialTestFixtures.CreateAsariMaterial(
            "shared-texture-material", [0, 0, 0, 255], Vector4.Zero, 0,
            addition: [255, 128, 255, 255], additionMaskScalar: 0);
        var vectors = new Dictionary<string, Vector4>(material.Vectors)
        {
            ["ASA_HED_Addn_Mask_Vector"] = new(1, 0, 0, 1)
        };
        var diffuse = material.Textures["ASA_HED_Diff"] with { Key = "shared-source" };
        var mask = material.Textures["ASA_HED_Mask"] with
        {
            Key = diffuse.Key,
            Rgba8 = diffuse.Rgba8
        };
        var textures = new Dictionary<string, HeadPreviewTexture>(material.Textures)
        {
            [diffuse.ParameterName] = diffuse,
            [mask.ParameterName] = mask
        };
        material = material with { Textures = textures, Vectors = vectors };
        var (renderer, camera) = CreateTriangleRenderer(material);
        using (renderer)
        {
            var sharedPixels = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            var uploads = renderer.TextureUploadCount;
            var bytes = renderer.GpuTextureBytes;
            TestAssert.Equal(textures.Count - 1, uploads);
            TestAssert.Equal((textures.Count - 1) * 4L, bytes);
            renderer.Render(camera, new HeadPreviewOptions());
            TestAssert.Equal(uploads, renderer.TextureUploadCount);

            // Identical pixels under distinct source identities cannot collide in the cache.
            textures = new Dictionary<string, HeadPreviewTexture>(textures)
            {
                [mask.ParameterName] = mask with { Key = "independent-mask-source" }
            };
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
            {
                [material.Key] = material with { Textures = textures }
            });
            var referencePixels = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(sharedPixels.SequenceEqual(referencePixels),
                "Shared colour/mask source used the first binding's colour space for both roles.");
            TestAssert.Equal(uploads + 1, renderer.TextureUploadCount);
            TestAssert.Equal(bytes + 4, renderer.GpuTextureBytes);
            renderer.ClearScene();
            TestAssert.Equal(0L, renderer.GpuTextureBytes);
        }
    }
}
