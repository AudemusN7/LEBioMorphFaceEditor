using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

/// <summary>Shared material session, render and comparison helpers.</summary>
public static class MaterialTestFixtures
{
    internal static void AssertMaterialsRenderEqually(
        HeadPreviewMaterial firstMaterial,
        HeadPreviewMaterial secondMaterial,
        string message)
    {
        var (renderer, camera) = CreateTriangleRenderer(firstMaterial);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
                { [secondMaterial.Key] = secondMaterial });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(first.SequenceEqual(second), message);
        }
    }

    internal static void AssertMaterialsRenderDifferently(
        HeadPreviewMaterial firstMaterial,
        HeadPreviewMaterial secondMaterial,
        string message)
    {
        var (renderer, camera) = CreateTriangleRenderer(firstMaterial);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
                { [secondMaterial.Key] = secondMaterial });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(!first.SequenceEqual(second), message);
        }
    }

    internal static MaterialEditingSession CreateSession()
    {
        var identity = TestFixtures.CreateIdentity("FaceMaterial", "MaterialInstanceConstant");
        var textureIdentity = TestFixtures.CreateIdentity("FaceDiffuse", "Texture2D");
        var texture = new DecodedTextureAsset(
            textureIdentity, 1, 1, [255, 255, 255, 255], "PF_DXT1", TextureRole.Diffuse,
            TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "original");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "HMM_HED_PRONPC_MASTER_FACE_MAT",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>
            {
                ["HED_Norm_Blend"] = 0.5f,
                ["HED_Addn_Blend_Scalar"] = 0f
            },
            new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.One },
            new Dictionary<string, MaterialTextureBinding> { ["HED_Diff"] = new("HED_Diff", texture) });
        var overrides = new MorphFaceMaterialOverrides(
            null,
            [
                new ScalarMaterialOverride("HED_Norm_Blend", 0.5f),
                new ScalarMaterialOverride("HED_Addn_Blend_Scalar", 0f)
            ],
            [new VectorMaterialOverride("SkinTone", Vector4.One)],
            [new TextureMaterialOverride("HED_Diff", textureIdentity)]);
        return new MaterialEditingSession(overrides, new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material }));
    }

    internal static HeadPreviewMaterial CreatePreviewMaterial(string key, byte[] rgba)
    {
        var texture = new HeadPreviewTexture(
            Convert.ToHexString(rgba), "Diffuse", 1, 1, rgba, TextureRole.Diffuse,
            TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false);
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Unknown, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            new Dictionary<string, HeadPreviewTexture> { ["Diffuse"] = texture });
    }

    internal static HeadPreviewTexture CreateTexture(
        string parameterName,
        byte[] rgba,
        TextureRole role,
        bool meaningfulAlpha = false) => new(
            $"{parameterName}:{Convert.ToHexString(rgba)}", parameterName, 1, 1, rgba, role,
            role == TextureRole.Diffuse ? TextureColorSpace.Srgb : TextureColorSpace.Linear,
            meaningfulAlpha ? TextureAlphaPolicy.Mask : TextureAlphaPolicy.Ignore,
            meaningfulAlpha);

    internal static (HeadPreviewRenderer Renderer, HeadOrbitCamera Camera) CreateTriangleRenderer(HeadPreviewMaterial material)
    {
        var vertices = new[]
        {
            CreateVertex(new Vector3(-0.8f, -0.7f, 0)),
            CreateVertex(new Vector3(0.8f, -0.7f, 0)),
            CreateVertex(new Vector3(0, 0.8f, 0))
        };
        var mesh = new HeadPreviewMesh("Triangle", vertices, [0u, 1u, 2u], [new HeadPreviewSection(0, 3, 0, material)]);
        var scene = new HeadPreviewScene("Material test", [mesh], new HeadPreviewBounds(new Vector3(-1), new Vector3(1)));
        var renderer = new HeadPreviewRenderer(96, 96);
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);
        return (renderer, camera);
    }

    internal static int CountNonBackgroundPixels(byte[] pixels)
    {
        var count = 0;
        for (var offset = 4; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != pixels[0]
                || pixels[offset + 1] != pixels[1]
                || pixels[offset + 2] != pixels[2]
                || pixels[offset + 3] != pixels[3])
            {
                count++;
            }
        }
        return count;
    }

    internal static HeadPreviewVertex CreateVertex(Vector3 position) => new(
        position, Vector3.UnitZ, new Vector4(1, 0, 0, 1), Vector2.Zero,
        0, 0, 0, 0, new Vector4(1, 0, 0, 0));
}
