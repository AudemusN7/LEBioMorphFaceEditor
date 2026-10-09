using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

internal static class NativeAttachmentPreviewTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("native hat tint and reflection respect game shader differences", HatTintAndCube),
        new("native cap decal clips alpha and LE2 cap uses fixed diffuse", CapChannels),
        new("native moto emissive uses specular alpha and Anderson uses blue mask", Le3Channels),
        new("native visor LE1 Fresnel opacity differs from LE2 constant opacity", VisorOpacity)
    ];

    private static void HatTintAndCube()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        foreach (var game in new[] { 1, 2, 3 })
        {
            var material = Material(HumanMaterialPreviewModel.Hat, game,
                ("HMN_HAT_Diff", [64, 200, 120, 180]), ("HMN_HAT_Norm", [128, 128, 255, 255]));
            material = material with
            {
                Scalars = new Dictionary<string, float> { ["CubeMap_Intensity_Scalar"] = 0.5f },
                Vectors = new Dictionary<string, Vector4>
                {
                    ["HMN_HAT_Tint1_Vector"] = new(0.3f, 0.1f, 0.05f, 1),
                    ["HMN_HAT_Tint2_Vector"] = new(0.05f, 0.15f, 0.3f, 1)
                }
            };
            var baseline = Render(renderer, material, HeadPreviewRenderMode.Unlit);
            var reflected = Render(renderer, material with { FixedCubeTexture = Cube() }, HeadPreviewRenderMode.Unlit);
            TestAssert.True(game == 3 ? baseline.SequenceEqual(reflected) : !baseline.SequenceEqual(reflected),
                $"LE{game} hat cube behavior disagrees with the native shader.");
            var tinted = Render(renderer, material with { Vectors = new Dictionary<string, Vector4>(material.Vectors)
            { ["HMN_HAT_Tint1_Vector"] = new(0.03f, 0.6f, 0.02f, 1) } }, HeadPreviewRenderMode.Unlit);
            TestAssert.True(!baseline.SequenceEqual(tinted), $"LE{game} ignored the inherited hat tint.");
        }
    }

    private static void CapChannels()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        foreach (var game in new[] { 1, 2, 3 })
        {
            var material = Material(HumanMaterialPreviewModel.CapDecal, game,
                ("Cap_Diff", [100, 40, 150, 255]), ("Cap_Norm", [128, 128, 255, 255])) with
            { BlendMode = HeadMaterialBlendMode.Masked };
            var baseline = Render(renderer, material, HeadPreviewRenderMode.Unlit);
            var clipped = Render(renderer, Replace(material, "Cap_Diff", [100, 40, 150, 40]), HeadPreviewRenderMode.Unlit);
            TestAssert.True(!baseline.SequenceEqual(clipped), $"LE{game} cap decal did not clip alpha.");
            var otherChannels = Render(renderer, Replace(material, "Cap_Diff", [100, 255, 0, 255]), HeadPreviewRenderMode.Unlit);
            TestAssert.True(baseline.SequenceEqual(otherChannels), $"LE{game} cap decal treated specular channels as diffuse colour.");
        }
        var cap = Material(HumanMaterialPreviewModel.CapLe2, 2,
            ("__CapCrewDiffuse", [160, 60, 20, 255]), ("HMN_HAT_Diff", [10, 90, 200, 255]),
            ("HMN_HAT_Norm", [128, 128, 255, 255]));
        var fixedColour = Render(renderer, cap, HeadPreviewRenderMode.Unlit);
        var changedSpec = Render(renderer, Replace(cap, "HMN_HAT_Diff", [240, 220, 10, 255]), HeadPreviewRenderMode.Unlit);
        TestAssert.True(fixedColour.SequenceEqual(changedSpec), "LE2 cap used packed specular data as diffuse.");
        TestAssert.True(!fixedColour.SequenceEqual(Render(renderer, Replace(cap, "__CapCrewDiffuse", [30, 160, 210, 255]), HeadPreviewRenderMode.Unlit)),
            "LE2 cap ignored its fixed CrewCerberus colour texture.");
    }

    private static void Le3Channels()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var moto = Material(HumanMaterialPreviewModel.MotoHat, 3,
            ("Diffuse_Map", [0, 0, 0, 255]), ("Normal_Map", [128, 128, 255, 255]),
            ("Specular_Map", [0, 0, 0, 128])) with
        {
            Scalars = new Dictionary<string, float> { ["Emissive_Blowout"] = 1 },
            Vectors = new Dictionary<string, Vector4> { ["Emissive_color"] = new(0.3f, 0.1f, 0.05f, 1) }
        };
        var baseline = Render(renderer, moto, HeadPreviewRenderMode.Unlit);
        TestAssert.True(baseline.SequenceEqual(Render(renderer, Replace(moto, "Specular_Map", [0, 255, 0, 128]), HeadPreviewRenderMode.Unlit)),
            "Moto emissive incorrectly used green instead of alpha.");
        TestAssert.True(!baseline.SequenceEqual(Render(renderer, Replace(moto, "Specular_Map", [0, 0, 0, 0]), HeadPreviewRenderMode.Unlit)),
            "Moto ignored its emissive alpha mask.");
        var anderson = Material(HumanMaterialPreviewModel.AndersonHat, 3,
            ("Diffuse_Map", [30, 30, 30, 255]), ("Normal_Map", [128, 128, 255, 255]),
            ("Specular_Map", [128, 0, 0, 255])) with
        {
            Scalars = new Dictionary<string, float> { ["Phong_Spwr"] = 5 },
            Vectors = new Dictionary<string, Vector4> { ["Phong_Spec_Color"] = new(0.25f, 0.2f, 0.1f, 1) }
        };
        TestAssert.True(!Render(renderer, anderson, HeadPreviewRenderMode.Shaded).SequenceEqual(
            Render(renderer, Replace(anderson, "Specular_Map", [128, 0, 255, 255]), HeadPreviewRenderMode.Shaded)),
            "Anderson ignored its blue Phong mask.");
    }

    private static void VisorOpacity()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var visor = Material(HumanMaterialPreviewModel.Visor, 1, ("HMM_VSR_Norm", [128, 128, 255, 255])) with
        {
            BlendMode = HeadMaterialBlendMode.Translucent, FixedCubeTexture = Cube(),
            Scalars = new Dictionary<string, float> { ["VSR_Opacity"] = 0.8f, ["VSR_CubeMap_Intensity"] = 1 },
            Vectors = new Dictionary<string, Vector4> { ["VSR_Colour_01"] = Vector4.Zero, ["VSR_Colour_02"] = new(0.5f) }
        };
        var le1 = Render(renderer, visor, HeadPreviewRenderMode.Shaded);
        var le2 = Render(renderer, visor with { IsLe2 = true }, HeadPreviewRenderMode.Shaded);
        TestAssert.True(!le1.SequenceEqual(le2), "LE1 Fresnel transparency collapsed to the LE2 constant.");
        var center = (48 * 96 + 48) * 4;
        TestAssert.True(le2[center] > le1[center], "LE1 visor should be more transparent when viewed head-on.");
    }

    private static HeadPreviewMaterial Material(HumanMaterialPreviewModel model, int game,
        params (string Name, byte[] Pixels)[] textures) => new(
        "hat", "hat", HeadMaterialFamily.Accessory, HeadMaterialBlendMode.Opaque, false,
        new Dictionary<string, float>(), new Dictionary<string, Vector4>(),
        textures.ToDictionary(item => item.Name, item => new HeadPreviewTexture(
            item.Name + Convert.ToHexString(item.Pixels), item.Name, 1, 1, item.Pixels,
            item.Name.Contains("Norm", StringComparison.OrdinalIgnoreCase) ? TextureRole.Normal : TextureRole.Other,
            item.Name is "Diffuse_Map" or "__CapCrewDiffuse" ? TextureColorSpace.Srgb : TextureColorSpace.Linear,
            TextureAlphaPolicy.Ignore, true), StringComparer.OrdinalIgnoreCase))
        { PreviewModel = model, IsLe2 = game == 2, IsLe3 = game == 3 };

    private static HeadPreviewMaterial Replace(HeadPreviewMaterial material, string name, byte[] pixels) => material with
    { Textures = new Dictionary<string, HeadPreviewTexture>(material.Textures)
        { [name] = material.Textures[name] with { Key = name + Convert.ToHexString(pixels), Rgba8 = pixels } } };

    private static HeadPreviewCubeTexture Cube() => new("hat-cube", 1,
        Enumerable.Range(0, 6).Select(_ => new byte[] { 100, 120, 140, 255 }).ToArray(), TextureColorSpace.Srgb);

    private static byte[] Render(HeadPreviewRenderer renderer, HeadPreviewMaterial material, HeadPreviewRenderMode mode)
    {
        static HeadPreviewVertex Vertex(float x, float y) => new(new Vector3(x, y, 0), Vector3.UnitZ,
            new Vector4(1, 0, 0, 1), new Vector2(0.5f), 0, 0, 0, 0, new Vector4(1, 0, 0, 0));
        var mesh = new HeadPreviewMesh("hat", [Vertex(-0.8f, -0.8f), Vertex(0.8f, -0.8f), Vertex(0, 0.8f)],
            [0u, 1u, 2u], [new HeadPreviewSection(0, 3, 0, material)], IsAttachment: true);
        var scene = new HeadPreviewScene("hat", [mesh], new HeadPreviewBounds(new Vector3(-1), new Vector3(1)));
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera(); camera.Fit(scene.Bounds);
        return renderer.Render(camera, new HeadPreviewOptions { RenderMode = mode, AmbientOcclusionEnabled = false,
            BackgroundColor = Vector3.Zero }).BgraPixels.ToArray();
    }
}
