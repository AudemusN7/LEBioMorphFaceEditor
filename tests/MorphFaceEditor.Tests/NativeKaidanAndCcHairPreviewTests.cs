using System.Numerics;
using System.Reflection;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

internal static class NativeKaidanAndCcHairPreviewTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("Kaiden Spec red selects power and green selects intensity", KaidenSpecChannels),
        new("CC Hair native midtone response stays scoped to CC Hair", CcHairMidtoneScoping)
    ];

    private static void KaidenSpecChannels()
    {
        using var renderer = new HeadPreviewRenderer(128, 128);
        var mesh = CreateSurface("Kaiden scalp material");
        var camera = new HeadOrbitCamera();
        camera.Fit(CreateScene(mesh).Bounds);

        var material = CreateKaidenMaterial([64, 160, 24, 255]);
        AssertKaidenBindingsAndConstants(material);
        var baseline = Render(renderer, camera, mesh, material);

        var strongerPower = Render(renderer, camera, mesh,
            WithSpec(material, [224, 160, 24, 255]));
        AssertFramesDiffer(baseline, strongerPower,
            "Spec.r did not change the Kaiden HED_Spwr-controlled exponent.");

        var lowerIntensity = Render(renderer, camera, mesh,
            WithSpec(material, [64, 40, 24, 255]));
        AssertFramesDiffer(baseline, lowerIntensity,
            "Spec.g did not change the Kaiden HED_Spec_Scalar-controlled intensity.");

        var blueOnly = Render(renderer, camera, mesh,
            WithSpec(material, [64, 160, 255, 255]));
        TestAssert.True(baseline.SequenceEqual(blueOnly),
            "Kaiden Spec.b changed preview output even though the captured FXC uses Spec.r and Spec.g for this lobe.");

        var changedSkinTone = material with
        {
            Vectors = WithVector(material.Vectors, "SkinTone", new Vector4(0.02f, 0.9f, 0.7f, 1))
        };
        var skinToneFrame = Render(renderer, camera, mesh, changedSkinTone);
        TestAssert.True(baseline.SequenceEqual(skinToneFrame),
            "A generic SkinTone value changed Kaiden output despite its native HED_Face_Diff sampler.");
    }

    private static void CcHairMidtoneScoping()
    {
        using var renderer = new HeadPreviewRenderer(128, 128);
        var mesh = CreateSurface("CC hair material");
        var camera = new HeadOrbitCamera();
        camera.Fit(CreateScene(mesh).Bounds);

        var ccHair = CreateCcHairMaterial();
        var ordinaryHair = ccHair with { PreviewModel = HumanMaterialPreviewModel.Default };
        var ordinaryBefore = Render(renderer, camera, mesh, ordinaryHair);
        var ccFrame = Render(renderer, camera, mesh, ccHair);
        var ordinaryAfter = Render(renderer, camera, mesh, ordinaryHair);

        AssertFramesDiffer(ordinaryBefore, ccFrame,
            "CC Hair at a midtone did not use its recovered native exponent/lobe response.");
        TestAssert.True(ordinaryBefore.SequenceEqual(ordinaryAfter),
            "Rendering CC Hair changed the ordinary Hair response for the same material data.");
    }

    private static void AssertKaidenBindingsAndConstants(HeadPreviewMaterial material)
    {
        var assembly = typeof(HeadPreviewRenderer).Assembly;
        var bindingsType = assembly.GetType("MorphFaceEditor.Rendering.HeadPreviewMaterialBindings", throwOnError: true)!;
        var constantsType = assembly.GetType("MorphFaceEditor.Rendering.HeadPreviewMaterialConstants", throwOnError: true)!;
        var bindings = bindingsType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [material])!;
        var constants = constantsType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [material, HeadPreviewRenderMode.Shaded, bindings])!;

        string? BoundTextureName(string slot)
        {
            var texture = bindingsType.GetProperty(slot, BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(bindings);
            return texture?.GetType().GetProperty("ParameterName")?.GetValue(texture) as string;
        }

        Vector4 Constant(string name)
        {
            var value = (SharpDX.Vector4)constantsType.GetField(name, BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(constants)!;
            return new Vector4(value.X, value.Y, value.Z, value.W);
        }

        TestAssert.Equal("HED_Face_Diff", BoundTextureName("Diffuse"));
        TestAssert.Equal("Nm", BoundTextureName("Normal"));
        TestAssert.Equal("Spec", BoundTextureName("Mask"));
        TestAssert.Equal(Vector4.One, Constant("BaseColor"));
        var general = Constant("GeneralParameters");
        TestAssert.Near(material.Scalars["HED_Spwr"], general.Y, 0);
        TestAssert.Near(material.Scalars["HED_Spec_Scalar"], general.Z, 0);
        TestAssert.Near((float)HumanMaterialPreviewModel.Kaiden, Constant("TextureFlags2").Z, 0);
    }

    private static HeadPreviewMaterial CreateKaidenMaterial(byte[] spec)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Face_Diff"] = Texture("HED_Face_Diff", TextureRole.Diffuse,
                TextureColorSpace.Srgb, [190, 145, 118, 255]),
            ["Nm"] = Texture("Nm", TextureRole.Normal,
                TextureColorSpace.Linear, [198, 128, 236, 255]),
            ["Spec"] = Texture("Spec", TextureRole.Specular,
                TextureColorSpace.Linear, spec)
        };
        return new HeadPreviewMaterial(
            "Kaiden scalp MIC",
            "Kaiden scalp MIC",
            HeadMaterialFamily.Skin,
            HeadMaterialBlendMode.Opaque,
            TwoSided: true,
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["HED_Spec_Scalar"] = 0.8f,
                ["HED_TMis_Scalar"] = 0.25f,
                ["HED_Spwr"] = 15
            },
            new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["HED_TClr_Vector"] = new Vector4(0.632043f, 0.027755f, 0.017936f, 1),
                ["SkinLightScattering"] = new Vector4(0.45f, 0.05f, 0.03f, 1),
                ["SkinTone"] = new Vector4(0.5f, 0.3f, 0.2f, 1)
            },
            textures)
        {
            IsLe3 = true,
            PreviewModel = HumanMaterialPreviewModel.Kaiden,
            SupportedScalars = new HashSet<string>(["HED_Spec_Scalar", "HED_TMis_Scalar", "HED_Spwr"], StringComparer.OrdinalIgnoreCase),
            SupportedVectors = new HashSet<string>(["HED_TClr_Vector", "SkinLightScattering", "SkinTone"], StringComparer.OrdinalIgnoreCase),
            SupportedTextures = new HashSet<string>(textures.Keys, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static HeadPreviewMaterial CreateCcHairMaterial()
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase)
        {
            ["HAIR_Diff"] = Texture("HAIR_Diff", TextureRole.Diffuse,
                TextureColorSpace.Linear, [32, 184, 12, 255])
        };
        return new HeadPreviewMaterial(
            "CC hair",
            "CC hair",
            HeadMaterialFamily.Hair,
            HeadMaterialBlendMode.Translucent,
            TwoSided: true,
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["HED_Hair_Colour_Vector"] = new Vector4(0.2f, 0.08f, 0.03f, 1)
            },
            textures)
        {
            IsLe3 = true,
            PreviewModel = HumanMaterialPreviewModel.CcHair,
            SupportedVectors = new HashSet<string>(["HED_Hair_Colour_Vector"], StringComparer.OrdinalIgnoreCase),
            SupportedTextures = new HashSet<string>(["HAIR_Diff"], StringComparer.OrdinalIgnoreCase)
        };
    }

    private static HeadPreviewMaterial WithSpec(HeadPreviewMaterial material, byte[] pixels) => material with
    {
        Textures = WithTexture(material.Textures,
            Texture("Spec", TextureRole.Specular, TextureColorSpace.Linear, pixels))
    };

    private static HeadPreviewScene CreateScene(HeadPreviewMesh mesh) => new(
        "Native material response test",
        [mesh],
        new HeadPreviewBounds(new Vector3(-0.9f, -0.9f, -0.05f), new Vector3(0.9f, 0.9f, 0.05f)));

    private static HeadPreviewMesh CreateSurface(string name)
    {
        var tangent = Vector3.Normalize(new Vector3(0.66f, 0.75f, 0));
        static HeadPreviewVertex Vertex(Vector3 position, Vector2 uv, Vector3 tangent) => new(
            position,
            Vector3.UnitZ,
            new Vector4(tangent, 1),
            uv,
            0, 0, 0, 0,
            new Vector4(1, 0, 0, 0));

        return new HeadPreviewMesh(
            name,
            [
                Vertex(new Vector3(-0.82f, -0.82f, 0), new Vector2(0, 0), tangent),
                Vertex(new Vector3( 0.82f, -0.82f, 0), new Vector2(1, 0), tangent),
                Vertex(new Vector3( 0.82f,  0.82f, 0), new Vector2(1, 1), tangent),
                Vertex(new Vector3(-0.82f,  0.82f, 0), new Vector2(0, 1), tangent)
            ],
            [0u, 2u, 1u, 0u, 3u, 2u],
            [new HeadPreviewSection(0, 6, 0, CreateKaidenMaterial([64, 160, 24, 255]))]);
    }

    private static byte[] Render(
        HeadPreviewRenderer renderer,
        HeadOrbitCamera camera,
        HeadPreviewMesh mesh,
        HeadPreviewMaterial material)
    {
        var updatedMesh = mesh with
        {
            Sections = [mesh.Sections[0] with { Material = material }]
        };
        renderer.SetScene(CreateScene(updatedMesh));
        return renderer.Render(camera, new HeadPreviewOptions(
            LightingPreset: HeadPreviewLightingPreset.Studio,
            AmbientOcclusionEnabled: false)).BgraPixels.ToArray();
    }

    private static HeadPreviewTexture Texture(
        string name,
        TextureRole role,
        TextureColorSpace colorSpace,
        byte[] rgba) => new(
        $"{name}:{Convert.ToHexString(rgba)}",
        name,
        1,
        1,
        rgba,
        role,
        colorSpace,
        TextureAlphaPolicy.Ignore,
        false);

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

    private static IReadOnlyDictionary<string, HeadPreviewTexture> WithTexture(
        IReadOnlyDictionary<string, HeadPreviewTexture> source,
        HeadPreviewTexture texture)
    {
        var copy = new Dictionary<string, HeadPreviewTexture>(source, StringComparer.OrdinalIgnoreCase)
        {
            [texture.ParameterName] = texture
        };
        return copy;
    }

    private static void AssertFramesDiffer(byte[] baseline, byte[] changed, string message)
    {
        var changedPixels = 0;
        var rgbDelta = 0;
        for (var index = 0; index < baseline.Length; index += 4)
        {
            var delta = Math.Abs(baseline[index] - changed[index])
                + Math.Abs(baseline[index + 1] - changed[index + 1])
                + Math.Abs(baseline[index + 2] - changed[index + 2]);
            if (delta > 2)
            {
                changedPixels++;
                rgbDelta += delta;
            }
        }

        TestAssert.True(changedPixels > 8 && rgbDelta > 80,
            $"{message} Changed pixels: {changedPixels}; RGB delta: {rgbDelta}.");
    }
}
