using System.Numerics;
using System.Reflection;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

internal static class NativeHairPreviewTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("Human_Hair_MASTER BMO controls change rendered pixels", HumanHairControlsAffectPixels),
        new("Human_Hair_MASTER white colours retain native stack lighting", HumanHairWhiteColours),
        new("Human_Hair_MASTER uses Hair_Stack instead of HAIR_Diff", HumanHairUsesHairStack),
        new("Human_Hair_MASTER Hair_Stack alpha controls layered hair depth", HumanHairStackAlphaControlsLayering),
        new("CC scalp Buzzcut responds while native-inert Hair Mask Alpha stays inert", CcScalpControlResponse)
    ];

    private static void HumanHairControlsAffectPixels()
    {
        using var renderer = new HeadPreviewRenderer(128, 128);
        var mesh = CreateQuad("James hair card");
        var scene = CreateScene(mesh);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var baselineMaterial = CreateHumanHairMaterial();
        AssertHumanHairConstantLayout(baselineMaterial);
        var baseline = Render(renderer, camera, mesh, baselineMaterial);
        var edits = new (string Name, Func<HeadPreviewMaterial, HeadPreviewMaterial> Apply)[]
        {
            ("Hair_Color", material => material with
            {
                Vectors = WithVector(material.Vectors, "Hair_Color", new Vector4(0.24f, 0.12f, 0.06f, 1))
            }),
            ("Aniso_Color_1", material => material with
            {
                Vectors = WithVector(material.Vectors, "Aniso_Color_1", new Vector4(0.8f, 0.08f, 0.04f, 1))
            }),
            ("Aniso_Color_2", material => material with
            {
                Vectors = WithVector(material.Vectors, "Aniso_Color_2", new Vector4(0.04f, 0.12f, 0.9f, 1))
            }),
            ("Aniso_Highlight_1", material => material with
            {
                Scalars = WithScalar(material.Scalars, "Aniso_Highlight_1", 40)
            }),
            ("Aniso_Highlight_2", material => material with
            {
                Scalars = WithScalar(material.Scalars, "Aniso_Highlight_2", 300)
            })
        };

        foreach (var (name, apply) in edits)
        {
            var edited = Render(renderer, camera, mesh, apply(baselineMaterial));
            AssertPixelsDiffer(baseline, edited, $"Changing {name} did not affect the rendered James hair card.");
        }
    }

    private static void HumanHairUsesHairStack()
    {
        using var renderer = new HeadPreviewRenderer(96, 96);
        var mesh = CreateQuad("James hair stack contract");
        var scene = CreateScene(mesh);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var withStack = CreateHumanHairMaterial();
        var withoutStack = withStack with
        {
            Textures = WithoutTexture(withStack.Textures, "Hair_Stack")
        };
        var baseline = Render(renderer, camera, mesh, withoutStack);
        var stackFrame = Render(renderer, camera, mesh, withStack);
        AssertPixelsDiffer(baseline, stackFrame, "Hair_Stack did not drive the Human_Hair_MASTER preview.");

        // Human_Hair_MASTER has no diffuse expression. An unrelated HAIR_Diff
        // texture must not become a fallback diffuse binding for this model.
        var withSpuriousDiffuse = withStack with
        {
            Textures = WithTexture(withStack.Textures, "HAIR_Diff", SolidTexture(
                "HAIR_Diff", TextureRole.Diffuse, TextureColorSpace.Srgb,
                TextureAlphaPolicy.Translucency, [255, 0, 255, 255], meaningfulAlpha: true)),
            SupportedTextures = new HashSet<string>(["Hair_Stack", "HAIR_Diff"], StringComparer.OrdinalIgnoreCase)
        };
        var diffuseFrame = Render(renderer, camera, mesh, withSpuriousDiffuse);
        TestAssert.True(stackFrame.SequenceEqual(diffuseFrame),
            "A HAIR_Diff texture altered Human_Hair_MASTER even though the model is driven by Hair_Stack.");
    }

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

    private static void HumanHairStackAlphaControlsLayering()
    {
        var bounds = new HeadPreviewBounds(new Vector3(-1), new Vector3(1));
        var rear = WithHairColor(CreateHumanHairMaterial(), new Vector4(0.92f, 0.018f, 0.012f, 1));
        var frontBase = WithHairColor(CreateHumanHairMaterial(), new Vector4(0.012f, 0.88f, 0.02f, 1));
        var rearCard = CreateHairCard("rear Human Hair card", -0.2f, rear);

        byte[] Render(HeadPreviewMesh mesh)
        {
            using var renderer = new HeadPreviewRenderer(96, 96);
            renderer.SetScene(new HeadPreviewScene("Human Hair alpha depth", [mesh], bounds));
            var camera = new HeadOrbitCamera();
            camera.Fit(bounds);
            return renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
        }

        HeadPreviewMesh Layered(HeadPreviewMaterial front) => new(
            "Layered Human Hair cards",
            [.. rearCard.Vertices, .. CreateHairCard("front", 0.2f, front).Vertices],
            [0u, 2u, 1u, 3u, 5u, 4u],
            [
                new HeadPreviewSection(0, 3, 0, rear),
                new HeadPreviewSection(3, 3, 1, front)
            ]);

        var backOnly = Render(rearCard);
        var partialFront = WithHairStackAlpha(frontBase, 128);
        var partialLayered = Render(Layered(partialFront));
        var partialFrontOnly = Render(CreateHairCard("partial front only", 0.2f, partialFront));
        TestAssert.True(!partialLayered.SequenceEqual(partialFrontOnly),
            "A partially transparent Human Hair front card occluded its rear card instead of accumulating with it.");

        var transparentFront = WithHairStackAlpha(frontBase, 0);
        var transparentLayered = Render(Layered(transparentFront));
        TestAssert.True(transparentLayered.SequenceEqual(backOnly),
            "A zero-alpha Hair_Stack front card changed the visible rear Human Hair card.");

        var opaqueFront = WithHairStackAlpha(frontBase, 255);
        var opaqueLayered = Render(Layered(opaqueFront));
        var opaqueFrontOnly = Render(CreateHairCard("opaque front only", 0.2f, opaqueFront));
        TestAssert.True(opaqueLayered.SequenceEqual(opaqueFrontOnly),
            "An opaque Human Hair front card failed to occlude its rear card in the alpha depth prepass.");
    }

    private static void CcScalpControlResponse()
    {
        using var renderer = new HeadPreviewRenderer(128, 128);
        var mesh = CreateQuad("CC scalp material card");
        var scene = CreateScene(mesh);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);

        var baselineMaterial = CreateCcScalpMaterial();
        var baseline = Render(renderer, camera, mesh, baselineMaterial);
        var buzzcutEdited = baselineMaterial with
        {
            Scalars = WithScalar(baselineMaterial.Scalars, "HED_Scalp_Buzzcut_Alpha_Scalar", 1)
        };
        AssertPixelsDiffer(baseline,
            Render(renderer, camera, mesh, buzzcutEdited),
            "Changing the native CC scalp Buzzcut Alpha value did not affect the preview.");

        foreach (var (name, value) in new[]
                 { ("HED_Spec_Aniso_Exp_Scalar", 0.5f), ("HAIR_Shine_Desaturate_Scalar", 1f) })
            AssertPixelsDiffer(baseline, Render(renderer, camera, mesh, baselineMaterial with
                { Scalars = WithScalar(baselineMaterial.Scalars, name, value) }),
                $"Changing the native CC scalp {name} did not affect the preview.");

        var hairMaskEdited = baselineMaterial with
        {
            Scalars = WithScalar(baselineMaterial.Scalars, "HAIR_Mask_Alpha_Scalar", 1)
        };
        var hairMaskFrame = Render(renderer, camera, mesh, hairMaskEdited);
        TestAssert.True(baseline.SequenceEqual(hairMaskFrame),
            "HAIR_Mask_Alpha changed CC scalp output even though the captured native pixel passes do not read it.");
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

    private static HeadPreviewMaterial WithHairColor(HeadPreviewMaterial material, Vector4 color) => material with
    {
        Key = material.Key + ":" + color,
        Vectors = WithVector(material.Vectors, "Hair_Color", color)
    };

    private static HeadPreviewMaterial WithHairStackAlpha(HeadPreviewMaterial material, byte alpha)
    {
        var original = material.Textures["Hair_Stack"];
        var rgba = (byte[])original.Rgba8.Clone();
        rgba[3] = alpha;
        return material with
        {
            Textures = WithTexture(material.Textures, "Hair_Stack",
                original with { Key = "Hair_Stack:" + Convert.ToHexString(rgba), Rgba8 = rgba })
        };
    }

    private static HeadPreviewMaterial CreateCcScalpMaterial()
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Scalp_Diff"] = SolidTexture("HED_Scalp_Diff", TextureRole.Diffuse,
                TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, [186, 122, 88, 255]),
            ["HED_Scalp_Norm"] = SolidTexture("HED_Scalp_Norm", TextureRole.Normal,
                TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, [128, 128, 250, 255]),
            ["HED_Scalp_Spec"] = SolidTexture("HED_Scalp_Spec", TextureRole.Specular,
                TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, [56, 128, 204, 172]),
            ["HED_Tang"] = SolidTexture("HED_Tang", TextureRole.Tangent,
                // A planar fibre direction keeps both native specular lobes visible to the test light.
                TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, [255, 128, 128, 255]),
            ["HED_Scalp_SpecShift"] = SolidTexture("HED_Scalp_SpecShift", TextureRole.Other,
                TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, [190, 150, 170, 255]),
            ["HED_Scalp_SpecShift2"] = SolidTexture("HED_Scalp_SpecShift2", TextureRole.Other,
                TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, [202, 202, 202, 255]),
            ["HED_Teeth_Diff"] = SolidTexture("HED_Teeth_Diff", TextureRole.Other,
                TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, [10, 28, 12, 255])
        };
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Scalp_Mask_Scalar"] = 0.8f,
            ["HED_Scalp_Mask_OverlayKill_Scalar"] = 0.65f,
            ["HED_Scalp_Buzzcut_Alpha_Scalar"] = 0,
            ["HAIR_Mask_Alpha_Scalar"] = 0,
            ["HAIR_Shine_Desaturate_Scalar"] = 0,
            ["HED_Teeth_Scalar"] = 0.75f,
            ["HED_Spec_Aniso_Exp_Scalar"] = 3,
            ["HED_Scalp_PhongSpec_Scalar"] = 4,
            ["HED_SPwr_Scalar"] = 8,
            ["HED_TMis_Scalar"] = 0.25f
        };
        var vectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
        {
            ["SkinTone"] = new Vector4(0.64f, 0.34f, 0.22f, 1),
            ["HED_Hair_Colour_Vector"] = new Vector4(0.18f, 0.065f, 0.025f, 1),
            ["HED_Spec_Add_Vector"] = new Vector4(0.45f, 0.35f, 0.28f, 1),
            ["HED_TClr_Vector"] = new Vector4(0.26f, 0.05f, 0.05f, 1),
            ["SkinLightScattering"] = new Vector4(0.45f, 0.05f, 0.03f, 1)
        };
        return new HeadPreviewMaterial(
            "CC scalp",
            "CC scalp",
            HeadMaterialFamily.Scalp,
            HeadMaterialBlendMode.Masked,
            TwoSided: true,
            scalars,
            vectors,
            textures)
        {
            IsLe3 = true,
            PreviewModel = HumanMaterialPreviewModel.CcScalp,
            SupportedScalars = new HashSet<string>(scalars.Keys, StringComparer.OrdinalIgnoreCase),
            SupportedVectors = new HashSet<string>(vectors.Keys, StringComparer.OrdinalIgnoreCase),
            SupportedTextures = new HashSet<string>(textures.Keys, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void AssertHumanHairConstantLayout(HeadPreviewMaterial material)
    {
        var assembly = typeof(HeadPreviewRenderer).Assembly;
        var bindingsType = assembly.GetType("MorphFaceEditor.Rendering.HeadPreviewMaterialBindings", throwOnError: true)!;
        var constantsType = assembly.GetType("MorphFaceEditor.Rendering.HeadPreviewMaterialConstants", throwOnError: true)!;
        var bindings = bindingsType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [material])!;
        var constants = constantsType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [material, HeadPreviewRenderMode.Shaded, bindings])!;

        Vector4 Field(string name)
        {
            var value = (SharpDX.Vector4)constantsType.GetField(name, BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(constants)!;
            return new Vector4(value.X, value.Y, value.Z, value.W);
        }

        TestAssert.Equal(material.Vectors["Hair_Color"], Field("BaseColor"));
        TestAssert.Equal(material.Vectors["Aniso_Color_1"], Field("SecondaryColor"));
        TestAssert.Equal(material.Vectors["Aniso_Color_2"], Field("TertiaryColor"));
        var highlightFields = Field("ScalpParameters2");
        TestAssert.Near(material.Scalars["Aniso_Highlight_1"], highlightFields.Y, 0);
        TestAssert.Near(material.Scalars["Aniso_Highlight_2"], highlightFields.Z, 0);

        var textureFlags0 = Field("TextureFlags0");
        var textureFlags1 = Field("TextureFlags1");
        TestAssert.Near(0, textureFlags0.X, 0);
        TestAssert.Near(1, textureFlags1.X, 0);
        TestAssert.Near((float)HumanMaterialPreviewModel.HumanHair, Field("TextureFlags2").Z, 0);
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

    private static HeadPreviewMesh CreateHairCard(string name, float z, HeadPreviewMaterial material)
    {
        var tangent = Vector3.Normalize(new Vector3(0.66f, 0.75f, 0));
        var vertices = new[]
        {
            Vertex(new Vector3(-0.8f, -0.7f, z), new Vector2(0, 0), tangent),
            Vertex(new Vector3(0.8f, -0.7f, z), new Vector2(1, 0), tangent),
            Vertex(new Vector3(0, 0.8f, z), new Vector2(0.5f, 1), tangent)
        };
        return new HeadPreviewMesh(name, vertices, [0u, 2u, 1u],
            [new HeadPreviewSection(0, 3, 0, material)]);
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

    private static void AssertPixelsDiffer(byte[] baseline, byte[] changed, string message)
    {
        var changedPixels = 0;
        var totalDelta = 0;
        for (var index = 0; index < baseline.Length; index += 4)
        {
            var pixelDelta = Math.Abs(baseline[index] - changed[index])
                + Math.Abs(baseline[index + 1] - changed[index + 1])
                + Math.Abs(baseline[index + 2] - changed[index + 2]);
            if (pixelDelta > 2)
            {
                changedPixels++;
                totalDelta += pixelDelta;
            }
        }

        TestAssert.True(changedPixels > 8 && totalDelta > 80,
            $"{message} Changed pixels: {changedPixels}; RGB delta: {totalDelta}.");
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

    private static IReadOnlyDictionary<string, float> WithScalar(
        IReadOnlyDictionary<string, float> source,
        string name,
        float value)
    {
        var copy = new Dictionary<string, float>(source, StringComparer.OrdinalIgnoreCase)
        {
            [name] = value
        };
        return copy;
    }

    private static IReadOnlyDictionary<string, HeadPreviewTexture> WithTexture(
        IReadOnlyDictionary<string, HeadPreviewTexture> source,
        string name,
        HeadPreviewTexture value)
    {
        var copy = new Dictionary<string, HeadPreviewTexture>(source, StringComparer.OrdinalIgnoreCase)
        {
            [name] = value
        };
        return copy;
    }

    private static IReadOnlyDictionary<string, HeadPreviewTexture> WithoutTexture(
        IReadOnlyDictionary<string, HeadPreviewTexture> source,
        string name)
    {
        var copy = new Dictionary<string, HeadPreviewTexture>(source, StringComparer.OrdinalIgnoreCase);
        copy.Remove(name);
        return copy;
    }
}
