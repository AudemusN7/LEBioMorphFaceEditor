using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
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
        new("scalp hair colour tints only its packed mask", ScalpHairColourTintsPackedMask),
        new("hair diffuse uses its packed green channel", HairDiffuseUsesPackedGreenChannel),
        new("DXT1 hair uses decoded alpha instead of luminance", Dxt1HairUsesDecodedAlpha),
        new("opaque hair strand cores occlude rear cards", HairDepthPrepassOccludesRearCards),
        new("partial hair shells accumulate behind translucent fronts", PartialHairShellsAccumulate),
        new("PROShort01 opacity uses only red at the 0.15 clip", MaskedHairOpacityUsesRedClip)
    ];

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
        // Native scalp composition retains a small hair floor with a white
        // packed map even at zero mask scalar. Black RGB isolates bare skin.
        var bareTextures = new Dictionary<string, HeadPreviewTexture>(darkHair.Textures)
        {
            ["HED_Scalp_Spec"] = CreateTexture("HED_Scalp_Spec", [0, 0, 0, 255], TextureRole.Specular)
        };
        darkHair = darkHair with { Textures = bareTextures };
        redHair = redHair with { Textures = bareTextures };
        var (renderer, camera) = CreateTriangleRenderer(darkHair);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [redHair.Key] = redHair });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                first.SequenceEqual(second),
                "HED_Hair_Colour_Vector affected scalp output outside the packed hair mask.");

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

    private static void MaskedHairOpacityUsesRedClip()
    {
        var below = CreateMaskedHairMaterial(
            "short01", [38, 255, 255, 255], [180, 100, 40, 255],
            [255, 128, 255, 255], [0, 0, 0, 255]);
        var above = CreateMaskedHairMaterial(
            "short01", [39, 0, 0, 0], [180, 100, 40, 255],
            [255, 128, 255, 255], [0, 0, 0, 255]);
        var (renderer, camera) = CreateTriangleRenderer(below);
        using (renderer)
        {
            var clipped = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [above.Key] = above });
            var visible = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.Equal(0, CountNonBackgroundPixels(clipped));
            TestAssert.True(CountNonBackgroundPixels(visible) > 0,
                "Opacity red immediately above 0.15 did not retain the hair section.");
        }
    }


}
