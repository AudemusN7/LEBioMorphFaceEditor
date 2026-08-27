using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;
using static MorphFaceEditor.Tests.SpeciesMaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the turian material regression set.
public static class TurianMaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Turian base specular follows diffuse alpha rather than tint alpha", TurianBaseSpecularUsesDiffuseAlpha),
        new("Turian complexion specular replaces base specular locally", TurianComplexionSpecularReplacesBase),
        new("Turian tattoos use raw diffuse blue rather than composed bone tint", TurianTattooUsesRawDiffuseBlue),
        new("Turian Mask scalar clips the tint-green teeth region", TurianMaskClipsTeethRegion),
        new("Turian transmission follows the compiled diffuse response", TurianTransmissionFollowsDiffuse),
        new("Turian eye specular channels preserve their compiled roles", TurianEyeSpecularChannelsPreserveRoles),
        new("LE3 Turian head normals consume stored RGB instead of BC5 reconstruction", Le3TurianNormalsConsumeRgb),
        new("LE3 Turian eye cubes and direct specular remain independent", Le3TurianEyePathsRemainIndependent)
    ];

    private static void TurianBaseSpecularUsesDiffuseAlpha()
    {
        var tintAlphaZero = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.One);
        var tintAlphaOne = CreateTurianMaterial(
            "turian", [255, 0, 0, 255], Vector4.One);
        AssertMaterialsRenderEqually(
            tintAlphaZero,
            tintAlphaOne,
            "Tint alpha incorrectly changed the Turian base specular lobe.");

        var diffuseAlphaZero = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.One,
            diffuse: [128, 128, 128, 0]);
        AssertMaterialsRenderDifferently(
            diffuseAlphaZero,
            tintAlphaZero,
            "Diffuse alpha did not gate TUR_HED_Spec_Colour.");
    }

    private static void TurianComplexionSpecularReplacesBase()
    {
        var blackBase = CreateTurianMaterial(
            "turian", [255, 0, 0, 255], Vector4.Zero,
            addition: [128, 128, 0, 255],
            additionSpecular: new Vector4(0.9f, 0.1f, 0.05f, 1));
        var whiteBase = CreateTurianMaterial(
            "turian", [255, 0, 0, 255], Vector4.One,
            addition: [128, 128, 0, 255],
            additionSpecular: new Vector4(0.9f, 0.1f, 0.05f, 1));
        AssertMaterialsRenderEqually(
            blackBase,
            whiteBase,
            "The fully covered Turian complexion retained the global skin specular colour.");
    }

    private static void TurianTattooUsesRawDiffuseBlue()
    {
        var whiteBone = CreateTurianMaterial(
            "turian", [0, 0, 0, 0], Vector4.Zero,
            boneTint: Vector4.One,
            tattooColour: new Vector4(0.9f, 0.2f, 0.6f, 1),
            fullTattoo: true);
        var yellowBone = CreateTurianMaterial(
            "turian", [0, 0, 0, 0], Vector4.Zero,
            boneTint: new Vector4(1, 1, 0, 1),
            tattooColour: new Vector4(0.9f, 0.2f, 0.6f, 1),
            fullTattoo: true);
        AssertMaterialsRenderEqually(
            whiteBone,
            yellowBone,
            "Bone Plate Tint leaked into a fully covered Turian tattoo.");

        var noDiffuseBlue = CreateTurianMaterial(
            "turian", [0, 0, 0, 0], Vector4.Zero,
            diffuse: [128, 128, 0, 255],
            tattooColour: new Vector4(0.9f, 0.2f, 0.6f, 1),
            fullTattoo: true);
        AssertMaterialsRenderDifferently(
            noDiffuseBlue,
            whiteBone,
            "Raw Turian diffuse blue did not drive tattoo intensity.");
    }

    private static void TurianMaskClipsTeethRegion()
    {
        var visibleTeeth = CreateTurianMaterial(
            "turian", [0, 255, 0, 0], Vector4.Zero, maskScalar: 1);
        var hiddenTeeth = CreateTurianMaterial(
            "turian", [0, 255, 0, 0], Vector4.Zero, maskScalar: 0);
        AssertMaterialsRenderDifferently(
            visibleTeeth,
            hiddenTeeth,
            "Mask did not clip the Tint.G teeth region.");

        var unselectedA = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.Zero, maskScalar: 1);
        var unselectedB = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.Zero, maskScalar: 0);
        AssertMaterialsRenderEqually(
            unselectedA,
            unselectedB,
            "Mask affected pixels outside the Tint.G teeth region.");
    }

    private static void TurianTransmissionFollowsDiffuse()
    {
        var disabled = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.Zero, transmission: 0);
        var enabled = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.Zero, transmission: 1);
        AssertMaterialsRenderDifferently(
            disabled,
            enabled,
            "TUR_HED_TMis_Multiplier remained disconnected from the compiled diffuse-green response.");
    }

    private static void TurianEyeSpecularChannelsPreserveRoles()
    {
        var iris = CreateTurianEyeMaterial("turian-eyes", [255, 0, 0, 255]);
        var sclera = CreateTurianEyeMaterial("turian-eyes", [0, 255, 0, 255]);
        var lens = CreateTurianEyeMaterial("turian-eyes", [0, 0, 255, 255]);
        AssertMaterialsRenderDifferently(
            iris,
            sclera,
            "TUR_Eye_Spec red and green did not retain separate iris/sclera lobes.");
        AssertMaterialsRenderDifferently(
            sclera,
            lens,
            "TUR_Eye_Spec blue did not retain the separate lens lobe.");
    }

    private static void Le3TurianNormalsConsumeRgb()
    {
        var le2 = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.Zero,
            normal: [224, 128, 160, 255], isLe3: false);
        var le3 = CreateTurianMaterial(
            "turian", [255, 0, 0, 0], Vector4.Zero,
            normal: [224, 128, 160, 255], isLe3: true);
        AssertMaterialsRenderDifferently(le2, le3,
            "The LE3 TUR RGB normal path collapsed to the LE2 BC5 reconstruction.");
    }

    private static void Le3TurianEyePathsRemainIndependent()
    {
        var unreflected = CreateLe3TurianEyeMaterial(
            "turian-eye", chromeValue: 0, parameterCubeValue: 0, cubeIntensity: 0, specular: 0);
        var chrome = CreateLe3TurianEyeMaterial(
            "turian-eye", chromeValue: 255, parameterCubeValue: 0, cubeIntensity: 0, specular: 0);
        var parameterCube = CreateLe3TurianEyeMaterial(
            "turian-eye", chromeValue: 0, parameterCubeValue: 255, cubeIntensity: 3, specular: 0);
        var directSpecular = CreateLe3TurianEyeMaterial(
            "turian-eye", chromeValue: 0, parameterCubeValue: 0, cubeIntensity: 0, specular: 2);

        AssertMaterialsRenderDifferently(unreflected, chrome,
            "The fixed LE3 TUR Chrome cube did not participate in the eye response.");
        AssertMaterialsRenderDifferently(unreflected, parameterCube,
            "The LE3 TUR parameter CubeMap or CubeMap_Intensity did not participate in the eye response.");
        AssertMaterialsRenderDifferently(unreflected, directSpecular,
            "Eye_Specular did not drive the LE3 TUR reflection-vector Phong response.");
    }
}
