using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

internal static class NativeHairPreviewTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("Human_Hair_MASTER white colours retain native stack lighting", HumanHairWhiteColours)
    ];

    private static void HumanHairWhiteColours()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var mesh = CreateQuad("James white colour regression");
        var camera = new HeadOrbitCamera();
        camera.Fit(CreateScene(mesh).Bounds);
        var material = CreateHumanHairMaterial();
        HeadPreviewMaterial Colours(Vector4 colour) => material with
        {
            Vectors = WithVector(WithVector(WithVector(material.Vectors,
                "Hair_Color", colour), "Aniso_Color_1", colour), "Aniso_Color_2", colour)
        };
        var black = Render(renderer, camera, mesh, Colours(new Vector4(0, 0, 0, 1)));
        var white = Render(renderer, camera, mesh, Colours(Vector4.One));
        var center = (48 * 96 + 48) * 4;
        var blackEnergy = black[center] + black[center + 1] + black[center + 2];
        var whiteEnergy = white[center] + white[center + 1] + white[center + 2];
        TestAssert.True(whiteEnergy > blackEnergy + 30,
            "White native hair colours lost their stack response; SelectionColor must not be bound to Hair_Color.");
    }

    private static HeadPreviewMaterial CreateHumanHairMaterial()
    {
        var stack = SolidTexture(
            "Hair_Stack", TextureRole.Diffuse, TextureColorSpace.Linear,
            TextureAlphaPolicy.Translucency, [184, 122, 54, 166], meaningfulAlpha: true);
        return new HeadPreviewMaterial(
            "James Human Hair",
            "James Human Hair",
            HeadMaterialFamily.Hair,
            HeadMaterialBlendMode.Translucent,
            TwoSided: true,
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["Aniso_Highlight_1"] = 15,
                ["Aniso_Highlight_2"] = 1500
            },
            new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["Hair_Color"] = new Vector4(0.04324168f, 0.02237726f, 0.01053152f, 1),
                ["Aniso_Color_1"] = new Vector4(0.12431984f, 0.01809168f, 0.01344398f, 1),
                ["Aniso_Color_2"] = new Vector4(0.79456598f, 0.45715508f, 0.44239265f, 1)
            },
            new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase)
            {
                ["Hair_Stack"] = stack
            })
        {
            IsLe3 = true,
            PreviewModel = HumanMaterialPreviewModel.HumanHair,
            SupportedScalars = new HashSet<string>(["Aniso_Highlight_1", "Aniso_Highlight_2"], StringComparer.OrdinalIgnoreCase),
            SupportedVectors = new HashSet<string>(["Hair_Color", "Aniso_Color_1", "Aniso_Color_2"], StringComparer.OrdinalIgnoreCase),
            SupportedTextures = new HashSet<string>(["Hair_Stack"], StringComparer.OrdinalIgnoreCase)
        };
    }

    private static HeadPreviewScene CreateScene(HeadPreviewMesh mesh) => new(
        "Native hair material test",
        [mesh],
        new HeadPreviewBounds(new Vector3(-0.9f, -0.9f, -0.05f), new Vector3(0.9f, 0.9f, 0.05f)));

    private static HeadPreviewMesh CreateQuad(string name)
    {
        // The tangent sits near, but not exactly on, the key-light lobe crest.
        // Both the 15/1500 authored powers and their edited values therefore
        // produce distinguishable output instead of all saturating at one.
        var tangent = Vector3.Normalize(new Vector3(0.66f, 0.75f, 0));
        var vertices = new[]
        {
            Vertex(new Vector3(-0.82f, -0.82f, 0), new Vector2(0, 0), tangent),
            Vertex(new Vector3( 0.82f, -0.82f, 0), new Vector2(1, 0), tangent),
            Vertex(new Vector3( 0.82f,  0.82f, 0), new Vector2(1, 1), tangent),
            Vertex(new Vector3(-0.82f,  0.82f, 0), new Vector2(0, 1), tangent)
        };
        var material = CreateHumanHairMaterial();
        return new HeadPreviewMesh(
            name,
            vertices,
            // Clockwise front faces match the renderer's D3D rasterizer and +Z normals.
            [0u, 2u, 1u, 0u, 3u, 2u],
            [new HeadPreviewSection(0, 6, 0, material)]);
    }

    private static HeadPreviewVertex Vertex(Vector3 position, Vector2 uv, Vector3 tangent) => new(
        position,
        Vector3.UnitZ,
        new Vector4(tangent, 1),
        uv,
        0,
        0,
        0,
        0,
        new Vector4(1, 0, 0, 0));

    private static byte[] Render(
        HeadPreviewRenderer renderer,
        HeadOrbitCamera camera,
        HeadPreviewMesh mesh,
        HeadPreviewMaterial material)
    {
        var section = mesh.Sections[0] with { Material = material };
        var updatedMesh = mesh with { Sections = [section] };
        var scene = CreateScene(updatedMesh);
        renderer.SetScene(scene);
        return renderer.Render(camera, new HeadPreviewOptions(
            LightingPreset: HeadPreviewLightingPreset.Studio,
            AmbientOcclusionEnabled: false)).BgraPixels.ToArray();
    }

    private static HeadPreviewTexture SolidTexture(
        string name,
        TextureRole role,
        TextureColorSpace colorSpace,
        TextureAlphaPolicy alphaPolicy,
        byte[] rgba,
        bool meaningfulAlpha = false) => new(
            $"{name}:{Convert.ToHexString(rgba)}",
            name,
            1,
            1,
            rgba,
            role,
            colorSpace,
            alphaPolicy,
            meaningfulAlpha);

    private static IReadOnlyDictionary<string, Vector4> WithVector(
        IReadOnlyDictionary<string, Vector4> source,
        string name,
        Vector4 value)
    {
        var copy = new Dictionary<string, Vector4>(source, StringComparer.OrdinalIgnoreCase)
        {
            [name] = value
        };
        return copy;
    }


}
