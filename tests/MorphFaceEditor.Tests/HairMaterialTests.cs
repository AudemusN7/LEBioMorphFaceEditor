using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;
using static MorphFaceEditor.Tests.HumanMaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the hair material regression set.
public static class HairMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("scalp controls cannot change whole-section coverage", ScalpControlsDoNotChangeCoverage),
        new("scalp hair colour tints only its packed mask", ScalpHairColourTintsPackedMask),
        new("scalp specular vector excludes its packed hair mask", ScalpSpecularVectorExcludesPackedHairMask),
        new("hair diffuse uses its packed green channel", HairDiffuseUsesPackedGreenChannel),
        new("DXT1 hair uses decoded alpha instead of luminance", Dxt1HairUsesDecodedAlpha),
        new("compiled hair highlight scalars do not affect rendering", HairHighlightScalarsAreCompiledOut),
        new("compiled hair auxiliary maps do not affect rendering", HairAuxiliaryMapsAreCompiledOut),
        new("LE3 Human Female additional hair binds its packed diffuse map", Le3FemaleAdditionalHairBindsPackedDiffuse),
        new("opaque hair strand cores occlude rear cards", HairDepthPrepassOccludesRearCards),
        new("partial hair shells accumulate behind translucent fronts", PartialHairShellsAccumulate),
        new("hair executes UE3 lit-translucency pass structure", HairUsesLitTranslucencyPasses),
        new("lashes use texture red as unlit opacity", LashesUseRedAsUnlitOpacity)
    ];

    private static void ScalpControlsDoNotChangeCoverage()
    {
        var low = CreateScalpMaterial("scalp", 0, 0);
        var high = CreateScalpMaterial("scalp", 1, 1);
        var (renderer, camera) = CreateTriangleRenderer(low);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [high.Key] = high });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.Equal(CountNonBackgroundPixels(first), CountNonBackgroundPixels(second));
            TestAssert.True(CountNonBackgroundPixels(first) > 0, "The scalp section was fully clipped.");
        }
    }

    private static void ScalpHairColourTintsPackedMask()
    {
        var darkHair = CreateScalpMaterial(
            "scalp", 0, 1, new Vector4(0.01f, 0.01f, 0.01f, 1));
        var redHair = CreateScalpMaterial(
            "scalp", 0, 1, new Vector4(1, 0, 0, 1));
        var maskedDarkHair = CreateScalpMaterial(
            "scalp", 1, 1, new Vector4(0.01f, 0.01f, 0.01f, 1));
        var maskedRedHair = CreateScalpMaterial(
            "scalp", 1, 1, new Vector4(1, 0, 0, 1));
        var (renderer, camera) = CreateTriangleRenderer(darkHair);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [redHair.Key] = redHair });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "HED_Hair_Colour_Vector affected scalp output while HED_Scalp_Mask_Scalar was zero.");

            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
            {
                [maskedDarkHair.Key] = maskedDarkHair
            });
            var third = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
            {
                [maskedRedHair.Key] = maskedRedHair
            });
            var fourth = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                !third.SequenceEqual(fourth),
                "HED_Hair_Colour_Vector did not tint scalp diffuse inside the packed scalp mask.");
        }
    }

    private static void ScalpSpecularVectorExcludesPackedHairMask()
    {
        var bareDark = CreateScalpMaterial(
            "scalp", 0, 1, specularColour: Vector4.Zero);
        var bareBright = CreateScalpMaterial(
            "scalp", 0, 1, specularColour: Vector4.One);
        var hairDark = CreateScalpMaterial(
            "scalp", 1, 1, specularColour: Vector4.Zero);
        var hairBright = CreateScalpMaterial(
            "scalp", 1, 1, specularColour: Vector4.One);
        var (renderer, camera) = CreateTriangleRenderer(bareDark);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [bareBright.Key] = bareBright });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            TestAssert.True(
                !first.SequenceEqual(second),
                "HED_Spec_Add_Vector did not affect the non-hair scalp region.");

            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [hairDark.Key] = hairDark });
            var third = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [hairBright.Key] = hairBright });
            var fourth = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                third.SequenceEqual(fourth),
                "HED_Spec_Add_Vector leaked into the packed scalp-hair region.");
        }
    }

    private static void HairDiffuseUsesPackedGreenChannel()
    {
        var redPacked = CreateHairMaterial("hair", [255, 128, 0, 255]);
        var bluePacked = CreateHairMaterial("hair", [0, 128, 255, 255]);
        var (renderer, camera) = CreateTriangleRenderer(redPacked);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [bluePacked.Key] = bluePacked });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "HAIR_Diff red/blue channels leaked into hair diffuse colour; LE1 uses the packed green channel.");
        }
    }

    private static void Dxt1HairUsesDecodedAlpha()
    {
        var redPacked = CreateHairMaterial("hair", [255, 128, 0, 255], meaningfulAlpha: false);
        var bluePacked = CreateHairMaterial("hair", [0, 128, 255, 255], meaningfulAlpha: false);
        var (renderer, camera) = CreateTriangleRenderer(redPacked);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [bluePacked.Key] = bluePacked });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "Hair translucency used RGB luminance even though decoded DXT1 alpha was opaque.");
        }
    }

    private static void HairHighlightScalarsAreCompiledOut()
    {
        var low = CreateHairMaterial("hair", [0, 128, 0, 255], highlight1: 1, highlight2: 1);
        var high = CreateHairMaterial("hair", [0, 128, 0, 255], highlight1: 500, highlight2: 500);
        var (renderer, camera) = CreateTriangleRenderer(low);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [high.Key] = high });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "Hair highlight scalar parameters affected rendering despite being absent from the FXC uniform table.");
        }
    }

    private static void HairAuxiliaryMapsAreCompiledOut()
    {
        var left = AddHairAuxiliaryMaps(
            CreateHairMaterial("hair", [0, 128, 0, 255]),
            [255, 128, 128, 255]);
        var right = AddHairAuxiliaryMaps(
            CreateHairMaterial("hair", [0, 128, 0, 255]),
            [0, 128, 128, 255]);
        var (renderer, camera) = CreateTriangleRenderer(left);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [right.Key] = right });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "HAIR_Norm/Mask/Tang/SpecShift affected rendering despite being absent from the FXC texture table.");
        }
    }

    private static void Le3FemaleAdditionalHairBindsPackedDiffuse()
    {
        var dark = CreateHairMaterial(
            "hair", [0, 0, 0, 255], textureName: "HAIR_ADDN_Diff", isLe3: true);
        var lit = CreateHairMaterial(
            "hair", [0, 255, 0, 255], textureName: "HAIR_ADDN_Diff", isLe3: true);
        var (renderer, camera) = CreateTriangleRenderer(dark);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [lit.Key] = lit });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(!first.SequenceEqual(second),
                "HAIR_ADDN_Diff green did not supply the LE3 HMF additional-hair base colour.");
        }
    }

    private static void HairDepthPrepassOccludesRearCards()
    {
        var back = CreateHairMaterial(
            "back", [0, 255, 0, 255], hairColour: new Vector4(1, 0.002f, 0.002f, 1));
        var front = CreateHairMaterial(
            "front", [0, 255, 0, 255], hairColour: new Vector4(0.002f, 1, 0.002f, 1));
        var backVertices = new[]
        {
            CreateVertex(new Vector3(-0.8f, -0.7f, -0.2f)),
            CreateVertex(new Vector3(0.8f, -0.7f, -0.2f)),
            CreateVertex(new Vector3(0, 0.8f, -0.2f))
        };
        var frontVertices = new[]
        {
            CreateVertex(new Vector3(-0.8f, -0.7f, 0.2f)),
            CreateVertex(new Vector3(0.8f, -0.7f, 0.2f)),
            CreateVertex(new Vector3(0, 0.8f, 0.2f))
        };
        var bounds = new HeadPreviewBounds(new Vector3(-1), new Vector3(1));
        var layeredMesh = new HeadPreviewMesh(
            "Layered hair",
            [.. backVertices, .. frontVertices],
            [0u, 1u, 2u, 3u, 4u, 5u],
            [new HeadPreviewSection(0, 3, 0, back), new HeadPreviewSection(3, 3, 1, front)]);
        var frontMesh = new HeadPreviewMesh(
            "Front hair",
            frontVertices,
            [0u, 1u, 2u],
            [new HeadPreviewSection(0, 3, 0, front)]);

        static byte[] Render(HeadPreviewMesh mesh, HeadPreviewBounds bounds)
        {
            using var renderer = new HeadPreviewRenderer(96, 96);
            var scene = new HeadPreviewScene("Hair depth", [mesh], bounds);
            renderer.SetScene(scene);
            var camera = new HeadOrbitCamera();
            camera.Fit(bounds);
            return renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
        }

        var layered = Render(layeredMesh, bounds);
        var frontOnly = Render(frontMesh, bounds);
        TestAssert.True(
            layered.SequenceEqual(frontOnly),
            "A rear translucent hair card remained visible through the nearest strand core.");
    }

    private static void PartialHairShellsAccumulate()
    {
        var back = CreateHairMaterial(
            "back", [0, 255, 0, 128], hairColour: new Vector4(1, 0.002f, 0.002f, 1));
        var front = CreateHairMaterial(
            "front", [0, 255, 0, 128], hairColour: new Vector4(0.002f, 1, 0.002f, 1));
        var backVertices = new[]
        {
            CreateVertex(new Vector3(-0.8f, -0.7f, -0.2f)),
            CreateVertex(new Vector3(0.8f, -0.7f, -0.2f)),
            CreateVertex(new Vector3(0, 0.8f, -0.2f))
        };
        var frontVertices = new[]
        {
            CreateVertex(new Vector3(-0.8f, -0.7f, 0.2f)),
            CreateVertex(new Vector3(0.8f, -0.7f, 0.2f)),
            CreateVertex(new Vector3(0, 0.8f, 0.2f))
        };
        var bounds = new HeadPreviewBounds(new Vector3(-1), new Vector3(1));
        var layeredMesh = new HeadPreviewMesh(
            "Layered partial hair",
            [.. backVertices, .. frontVertices],
            [0u, 1u, 2u, 3u, 4u, 5u],
            [new HeadPreviewSection(0, 3, 0, back), new HeadPreviewSection(3, 3, 1, front)]);
        var frontMesh = new HeadPreviewMesh(
            "Front partial hair",
            frontVertices,
            [0u, 1u, 2u],
            [new HeadPreviewSection(0, 3, 0, front)]);

        static byte[] Render(HeadPreviewMesh mesh, HeadPreviewBounds bounds)
        {
            using var renderer = new HeadPreviewRenderer(96, 96);
            renderer.SetScene(new HeadPreviewScene("Partial hair depth", [mesh], bounds));
            var camera = new HeadOrbitCamera();
            camera.Fit(bounds);
            return renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
        }

        var layered = Render(layeredMesh, bounds);
        var frontOnly = Render(frontMesh, bounds);
        TestAssert.True(
            !layered.SequenceEqual(frontOnly),
            "The alpha depth prepass incorrectly rejected every rear shell behind a partially transparent front card.");
    }

    private static void HairUsesLitTranslucencyPasses()
    {
        var hair = CreateHairMaterial("hair", [0, 255, 0, 128]);
        var (renderer, camera) = CreateTriangleRenderer(hair);
        using (renderer)
        {
            var frame = renderer.Render(camera, new HeadPreviewOptions());
            TestAssert.Equal(4, frame.DrawCalls);
        }
    }

    private static void LashesUseRedAsUnlitOpacity()
    {
        var green = CreateLashMaterial(
            "lashes", [128, 255, 0, 255], Vector4.One, 4);
        var blue = CreateLashMaterial(
            "lashes", [128, 0, 255, 255], new Vector4(1, 0, 0, 1), 0);
        var (renderer, camera) = CreateTriangleRenderer(green);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [blue.Key] = blue });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "Lash green/blue, colour vector, or optimized-out specular changed the unlit result.");
        }
    }
}
