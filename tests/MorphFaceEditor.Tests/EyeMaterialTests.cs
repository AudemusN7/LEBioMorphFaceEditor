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
        new("eye FX replaces the composed eye colour", EyeFxReplacesComposedColour),
        new("LE2 eye mask keeps iris and sclera colours isolated", Le2EyeMaskIsolatesColourRegions),
        new("iris colour multiplier scales the authored iris colour", IrisColourMultiplierScalesAuthoredColour),
        new("LE2 eye emissive changes output without leaking into LE1", Le2EyeEmissiveIsCapabilityBound),
        new("LE3 eye transmission colour participates in wrapped lighting", Le3EyeTransmissionColourParticipates),
        new("human eye fixed reflection cubes remain independent", HumanEyeCubesRemainIndependent)
    ];

    private static void EyeFxReplacesComposedColour()
    {
        var dark = CreateEyeMaterial(
            "eye", [32, 48, 64, 255], [255, 0, 0, 255], new Vector4(0.15f, 0.4f, 0.7f, 1), 1);
        var light = CreateEyeMaterial(
            "eye", [220, 180, 140, 255], [0, 255, 0, 255], new Vector4(0.15f, 0.4f, 0.7f, 1), 1);
        var inactive = CreateEyeMaterial(
            "eye", [220, 180, 140, 255], [0, 255, 0, 255], new Vector4(0.15f, 0.4f, 0.7f, 1), 0);
        var (renderer, camera) = CreateTriangleRenderer(dark);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [light.Key] = light });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(first.SequenceEqual(second), "Full-strength eye FX retained the underlying eye colour.");

            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [inactive.Key] = inactive });
            var third = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(!second.SequenceEqual(third), "Disabling eye FX did not restore the composed eye colour.");
        }
    }

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

    private static void IrisColourMultiplierScalesAuthoredColour()
    {
        static HeadPreviewMaterial WithMultiplier(HeadPreviewMaterial material, float multiplier)
        {
            var scalars = material.Scalars.ToDictionary(value => value.Key, value => value.Value);
            scalars["Iris_Colour_Multiplier"] = multiplier;
            return material with
            {
                Scalars = scalars,
                SupportedScalars = scalars.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            };
        }

        var material = CreateLe2EyeMaterial(
            "eye", [255, 255, 0, 255],
            new Vector4(0.08f, 0.16f, 0.24f, 1), Vector4.One);
        var zero = WithMultiplier(material, 0);
        var one = WithMultiplier(material, 1);
        var two = WithMultiplier(material, 2);

        AssertMaterialsRenderDifferently(zero, one,
            "Iris_Colour_Multiplier did not scale the authored iris colour.");
        AssertMaterialsRenderDifferently(one, two,
            "Iris_Colour_Multiplier saturated before its authored range could affect the iris.");
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

    private static void Le3EyeTransmissionColourParticipates()
    {
        static HeadPreviewMaterial WithTransmission(HeadPreviewMaterial material, Vector4 transmission)
        {
            var vectors = material.Vectors.ToDictionary(value => value.Key, value => value.Value);
            vectors["EyeLightScattering"] = new Vector4(0.45f, 0.25f, 0.15f, 1);
            vectors["Tmission_Color"] = transmission;
            return material with
            {
                Vectors = vectors,
                IsLe3 = true,
                SupportedVectors = vectors.Keys
                    .Append("SelectionColor")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                SupportedTextures = new HashSet<string>(
                    ["EYE_Diff", "EYE_Iris_Norm", "EYE_Lens_Norm", "EYE_Mask"],
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        var material = CreateEyeMaterial(
            "eye", [52, 68, 84, 255], [255, 0, 0, 255], Vector4.Zero, 0);
        var disabled = WithTransmission(material, Vector4.Zero);
        var enabled = WithTransmission(material, new Vector4(0.8f, 0.05f, 0.01f, 1));
        var (renderer, camera) = CreateTriangleRenderer(disabled);
        using (renderer)
        {
            var first = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [enabled.Key] = enabled });
            var second = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(!first.SequenceEqual(second),
                "Tmission_Color remained inert in the LE3 eye wrapped-light branch.");
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
