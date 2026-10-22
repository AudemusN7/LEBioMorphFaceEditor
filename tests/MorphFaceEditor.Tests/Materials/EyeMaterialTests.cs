using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;
using static MorphFaceEditor.Tests.HumanMaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the eye material regression set.
public static class EyeMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE2 eye mask keeps iris and sclera colours isolated", Le2EyeMaskIsolatesColourRegions),
        new("LE2 eye emissive changes output without leaking into LE1", Le2EyeEmissiveIsCapabilityBound),
        new("human eye fixed reflection cubes remain independent", HumanEyeCubesRemainIndependent)
    ];

    private static void Le2EyeMaskIsolatesColourRegions()
    {
        var red = new Vector4(0.8f, 0.05f, 0.02f, 1);
        var blue = new Vector4(0.02f, 0.08f, 0.8f, 1);
        var white = Vector4.One;
        var green = new Vector4(0.05f, 0.8f, 0.08f, 1);

        // Red selects iris; its inverse suppresses sclera. Green is unrelated
        // to colour selection and belongs to the normal-composition path.
        var irisRed = CreateLe2EyeMaterial("eye", [255, 255, 0, 255], red, white);
        var irisBlue = CreateLe2EyeMaterial("eye", [255, 255, 0, 255], blue, white);
        var irisGreenSclera = CreateLe2EyeMaterial("eye", [255, 255, 0, 255], red, green);
        var (renderer, camera) = CreateTriangleRenderer(irisRed);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [irisBlue.Key] = irisBlue });
            var changedIris = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            TestAssert.True(!first.SequenceEqual(changedIris),
                "EYE_Mask.r did not select the LE2 iris colour.");

            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
                { [irisGreenSclera.Key] = irisGreenSclera });
            var changedSuppressedSclera = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(first.SequenceEqual(changedSuppressedSclera),
                "Sclera colour leaked into an iris-only LE2 eye region.");
        }

        // Red=0 suppresses iris and inverse red selects sclera.
        var scleraRedIris = CreateLe2EyeMaterial("eye", [0, 0, 0, 255], red, white);
        var scleraBlueIris = CreateLe2EyeMaterial("eye", [0, 0, 0, 255], blue, white);
        var scleraGreen = CreateLe2EyeMaterial("eye", [0, 0, 0, 255], red, green);
        var scleraDifferentNormalMask = CreateLe2EyeMaterial("eye", [0, 255, 0, 255], red, white);
        (renderer, camera) = CreateTriangleRenderer(scleraRedIris);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [scleraBlueIris.Key] = scleraBlueIris });
            var changedSuppressedIris = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            TestAssert.True(first.SequenceEqual(changedSuppressedIris),
                "Iris colour leaked into a sclera-only LE2 eye region.");

            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [scleraGreen.Key] = scleraGreen });
            var changedSclera = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(!first.SequenceEqual(changedSclera),
                "Inverse EYE_Mask.r did not select the LE2 sclera colour.");

            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
                { [scleraDifferentNormalMask.Key] = scleraDifferentNormalMask });
            var changedGreen = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(first.SequenceEqual(changedGreen),
                "EYE_Mask.g incorrectly changed LE2 iris/sclera colour selection.");
        }
    }

    private static void Le2EyeEmissiveIsCapabilityBound()
    {
        static HeadPreviewMaterial WithEmissive(HeadPreviewMaterial material, float strength, bool le2)
        {
            var scalars = material.Scalars.ToDictionary(value => value.Key, value => value.Value);
            scalars["Emis_Scalar"] = strength;
            var vectors = material.Vectors.ToDictionary(value => value.Key, value => value.Value);
            vectors["Emis_Color"] = new Vector4(0.8f, 0.12f, 0.04f, 1);
            return material with
            {
                Scalars = scalars,
                Vectors = vectors,
                SupportedScalars = le2
                    ? new HashSet<string>(["Emis_Scalar"], StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SupportedVectors = le2
                    ? new HashSet<string>(["Emis_Color"], StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                // Compiled EYE_Lens_Norm support is the renderer's LE2 eye
                // permutation discriminator; a custom material may inherit its
                // default texture rather than override it.
                SupportedTextures = le2
                    ? new HashSet<string>(["EYE_Lens_Norm"], StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(["EYE_Diff", "EYE_Mask"], StringComparer.OrdinalIgnoreCase)
            };
        }

        var baseline = CreateEyeMaterial(
            "eye", [24, 24, 24, 255], [0, 0, 0, 255], Vector4.Zero, 0);
        var le2Off = WithEmissive(baseline, 0, true);
        var le2On = WithEmissive(baseline, 3, true);
        var le1WithPreservedOverride = WithEmissive(baseline, 3, false);
        var (renderer, camera) = CreateTriangleRenderer(le2Off);
        using (renderer)
        {
            var off = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [le2On.Key] = le2On });
            var on = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            TestAssert.True(!off.SequenceEqual(on), "LE2 Emis_Color/Emis_Scalar did not alter eye output.");

            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial>
                { [le1WithPreservedOverride.Key] = le1WithPreservedOverride });
            var le1 = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(
                !on.SequenceEqual(le1),
                "A preserved LE2-only emissive override leaked into the LE1 eye permutation.");
        }
    }

    private static void HumanEyeCubesRemainIndependent()
    {
        var unreflected = CreateEyeMaterial(
            "eye", [24, 24, 24, 255], [255, 255, 0, 255], Vector4.Zero, 0,
            primaryCubeValue: 0, secondaryCubeValue: 0);
        var primary = CreateEyeMaterial(
            "eye", [24, 24, 24, 255], [255, 255, 0, 255], Vector4.Zero, 0,
            primaryCubeValue: 255, secondaryCubeValue: 0);
        var secondary = CreateEyeMaterial(
            "eye", [24, 24, 24, 255], [255, 255, 0, 255], Vector4.Zero, 0,
            primaryCubeValue: 0, secondaryCubeValue: 255);

        AssertMaterialsRenderDifferently(unreflected, primary,
            "The fixed human-eye Metal/Chrome cube did not participate in the eye response.");
        AssertMaterialsRenderDifferently(unreflected, secondary,
            "The fixed human-eye EyeReflection cube did not participate in the eye response.");
        AssertMaterialsRenderDifferently(primary, secondary,
            "The two fixed human-eye reflection cubes were collapsed into one contribution.");
    }
}
