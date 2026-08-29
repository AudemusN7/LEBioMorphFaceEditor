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
        new("PROShort01 opacity uses only red at the 0.15 clip", MaskedHairOpacityUsesRedClip),
        new("PROShort01 diffuse tangent and specular maps are live", MaskedHairMapsDriveCustomLighting),
        new("PROShort01 ignores the unbound normal texture", MaskedHairNormalAssetIsNotSampled),
        new("PROShort01 is a single opaque-masked draw", MaskedHairUsesSingleMaskedPass),
        new("PROShort01 LE1 and LE2 shader paths remain equivalent", MaskedHairLe1AndLe2RemainEquivalent),
        new("installed PROShort01 masters resolve all four fixed samplers", InstalledMaskedHairMastersResolve),
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

    private static void MaskedHairMapsDriveCustomLighting()
    {
        var baseline = CreateMaskedHairMaterial(
            "short01", [255, 0, 0, 0], [200, 30, 10, 255],
            [255, 128, 0, 255], [0, 0, 0, 255]);
        var changedDiffuse = CreateMaskedHairMaterial(
            "short01", [255, 0, 0, 0], [10, 30, 200, 255],
            [255, 128, 0, 255], [0, 0, 0, 255]);
        var changedTangent = CreateMaskedHairMaterial(
            "short01", [255, 0, 0, 0], [200, 30, 10, 255],
            [128, 128, 0, 255], [0, 0, 0, 255]);
        var changedSpecular = CreateMaskedHairMaterial(
            "short01", [255, 0, 0, 0], [200, 30, 10, 255],
            [255, 128, 0, 255], [255, 255, 255, 255]);

        AssertMaterialsRenderDifferently(baseline, changedDiffuse,
            "The fixed PROShort01 diffuse sampler did not affect output.");
        AssertMaterialsRenderDifferently(baseline, changedTangent,
            "The fixed PROShort01 tangent sampler did not rotate the anisotropic response.");
        AssertMaterialsRenderDifferently(baseline, changedSpecular,
            "The fixed PROShort01 specular sampler did not affect its exponent-500 lobe.");
    }

    private static void MaskedHairNormalAssetIsNotSampled()
    {
        var baseline = CreateMaskedHairMaterial(
            "short01", [255, 0, 0, 0], [180, 100, 40, 255],
            [255, 128, 0, 255], [255, 255, 255, 255]);
        var textures = new Dictionary<string, HeadPreviewTexture>(baseline.Textures, StringComparer.OrdinalIgnoreCase)
        {
            ["__PROShort01_Normal"] = CreateTexture(
                "__PROShort01_Normal", [255, 0, 0, 255], TextureRole.Normal)
        };
        var withNormal = baseline with { Textures = textures };
        AssertMaterialsRenderEqually(baseline, withNormal,
            "The renderer sampled the _Norm asset absent from both compiled uniform tables.");
    }

    private static void MaskedHairUsesSingleMaskedPass()
    {
        var material = CreateMaskedHairMaterial(
            "short01", [255, 0, 0, 0], [180, 100, 40, 255],
            [255, 128, 0, 255], [255, 255, 255, 255]);
        var (renderer, camera) = CreateTriangleRenderer(material);
        using (renderer)
        {
            TestAssert.Equal(1, renderer.Render(camera, new HeadPreviewOptions()).DrawCalls);
        }
    }

    private static void MaskedHairLe1AndLe2RemainEquivalent()
    {
        var le1 = CreateMaskedHairMaterial(
            "short01", [255, 0, 0, 0], [180, 100, 40, 255],
            [255, 128, 0, 255], [255, 255, 255, 255]);
        var le2 = CreateMaskedHairMaterial(
            "short01", [255, 0, 0, 0], [180, 100, 40, 255],
            [255, 128, 0, 255], [255, 255, 255, 255], isLe2: true);
        AssertMaterialsRenderEqually(le1, le2,
            "A renderer-side game branch diverged despite the independently matching LE1/LE2 instruction streams.");
    }

    private static void InstalledMaskedHairMastersResolve()
    {
        var cases = new[]
        {
            (Path: Path.Combine(LegendaryExplorerCoreRuntime.DefaultLe1CookedPath ?? string.Empty,
                    "BIOG_HMM_HIR_PRO_R.pcc"),
                Material: "HMM_HIR_PROShort01_MAT_1a"),
            (Path: Path.Combine(LegendaryExplorerCoreRuntime.DefaultLe2CookedPath ?? string.Empty,
                    "BIOG_HMM_HIR_PRO_R.pcc"),
                Material: "HMM_HIR_PROShort01_MAT_1b")
        };
        if (cases.Any(value => !File.Exists(value.Path)))
        {
            return;
        }

        LegendaryExplorerCoreRuntime.Initialize();
        string[] expectedTextures =
        [
            "__PROShort01_Opacity",
            "__PROShort01_Diffuse",
            "__PROShort01_Tangent",
            "__PROShort01_Specular"
        ];
        foreach (var item in cases)
        {
            using var package = MEPackageHandler.OpenMEPackage(item.Path, forceLoadFromDisk: true);
            var source = package.Exports.Single(export =>
                export.ObjectNameString.Equals(item.Material, StringComparison.OrdinalIgnoreCase));
            using var cache = new PackageCache();
            var reader = new MorphFaceMaterialReader(cache, new GamePackageReferenceResolver(cache));
            var result = reader.Read(source, [], [source], applyFaceOverrides: false);
            var material = result.Materials.Find(MorphFacePackageReader.ToIdentity(source)!);
            TestAssert.True(material is not null,
                $"{package.Game} did not resolve {item.Material}.");
            TestAssert.Equal(HeadMaterialFamily.MaskedHair, material!.Family);
            TestAssert.Equal(HeadMaterialBlendMode.Masked, material.BlendMode);
            TestAssert.True(!material.TwoSided,
                $"{package.Game} incorrectly made PROShort01 two-sided.");
            foreach (var texture in expectedTextures)
            {
                TestAssert.True(material.Textures.ContainsKey(texture),
                    $"{package.Game} lost fixed sampler {texture}.");
            }
            TestAssert.True(!material.Textures.ContainsKey("__PROShort01_Normal"),
                $"{package.Game} incorrectly bound the compiled-out _Norm texture.");
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
