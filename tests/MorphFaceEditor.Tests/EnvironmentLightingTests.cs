using System.Numerics;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

public static class EnvironmentLightingTests
{
    private const float SqrtFourPi = 3.5449077f;
    private const float Y1 = 0.48860252f;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("SH environment preserves constant radiance", ConstantRadianceIsPreserved),
        new("SH diffuse evaluation matches cosine integral for tilted normals", TiltedNormalMatchesCosineIntegral),
        new("lighting rig environment preserves ambient mean and sky-ground direction", RigEnvironmentPreservesMeanAndDirection),
        new("room bounce preserves complementary direction and excludes direct lights", RoomBouncePreservesDirectionAndSeparation),
        new("SH environment evaluations remain finite and non-negative", EvaluationsRemainFiniteAndNonNegative)
    ];

    private static void ConstantRadianceIsPreserved()
    {
        var radiance = new Vector3(0.32f, 0.61f, 0.87f);
        var environment = CreateRadianceEnvironment(radiance * SqrtFourPi);

        foreach (var normal in new[]
                 {
                     Vector3.UnitY,
                     -Vector3.UnitY,
                     Vector3.UnitX,
                     Vector3.Normalize(new Vector3(0.31f, -0.72f, 0.62f))
                 })
        {
            TestAssert.Near(radiance, environment.Evaluate(normal), 2e-6f);
        }
    }

    private static void TiltedNormalMatchesCosineIntegral()
    {
        const float baseRadiance = 2f;
        const float lobeStrength = 0.5f;
        var lobeDirection = Vector3.Normalize(new Vector3(-0.33f, 0.74f, 0.58f));
        var normal = Vector3.Normalize(new Vector3(0.46f, 0.81f, -0.36f));
        var environment = CreateRadianceEnvironment(
            new Vector3(baseRadiance * SqrtFourPi),
            new Vector3(lobeStrength * Y1 * (4f * MathF.PI / 3f) * lobeDirection.Z),
            new Vector3(lobeStrength * Y1 * (4f * MathF.PI / 3f) * lobeDirection.Y),
            new Vector3(lobeStrength * Y1 * (4f * MathF.PI / 3f) * lobeDirection.X));

        var expected = IntegrateLambertDiffuse(baseRadiance, lobeStrength, lobeDirection, normal, 8192);
        TestAssert.Near(expected, environment.Evaluate(normal), 2e-4f);
    }

    private static void RigEnvironmentPreservesMeanAndDirection()
    {
        var lighting = HeadPreviewLightingPresets.Get(HeadPreviewLightingPreset.Studio) with
        {
            AmbientColor = new Vector3(0.6f, 0.7f, 0.8f),
            AmbientIntensity = 0.4f,
            GroundColor = new Vector3(0.015f, 0.025f, 0.05f),
            HorizonColor = new Vector3(0.16f, 0.22f, 0.31f)
        };
        var environment = HeadPreviewEnvironment.FromLightingRig(lighting);
        var expectedMean = lighting.AmbientColor * lighting.AmbientIntensity;

        TestAssert.Near(expectedMean, environment.Coefficient0 / SqrtFourPi, 2e-5f);
        var sky = environment.Evaluate(Vector3.UnitY);
        var ground = environment.Evaluate(-Vector3.UnitY);
        TestAssert.True(sky.X > ground.X && sky.Y > ground.Y && sky.Z > ground.Z,
            "The sky and ground radiance should retain their vertical direction after SH projection.");
    }

    private static void EvaluationsRemainFiniteAndNonNegative()
    {
        foreach (var preset in Enum.GetValues<HeadPreviewLightingPreset>())
        {
            var environment = HeadPreviewLightingPresets.GetEnvironment(preset);
            const int sampleCount = 1024;
            for (var index = 0; index < sampleCount; index++)
            {
                var normal = FibonacciDirection(index, sampleCount);
                var value = environment.Evaluate(normal);
                TestAssert.True(float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z),
                    "An environment evaluation became non-finite.");
                TestAssert.True(value.X >= 0 && value.Y >= 0 && value.Z >= 0,
                    "An environment evaluation returned negative irradiance.");
            }
        }
    }

    private static void RoomBouncePreservesDirectionAndSeparation()
    {
        var rig = HeadPreviewLightingPresets.Get(HeadPreviewLightingPreset.Studio) with
        {
            AmbientColor = Vector3.One,
            AmbientIntensity = 0.2f,
            GroundColor = Vector3.One,
            HorizonColor = Vector3.One,
            EnvironmentLobes = Array.AsReadOnly<HeadPreviewEnvironmentLobe>([
                new(Vector3.UnitX, new Vector3(4, 0, 0)),
                new(-Vector3.UnitX, new Vector3(0, 0, 4))])
        };
        var environment = HeadPreviewEnvironment.FromLightingRig(rig);
        TestAssert.Near(new Vector3(0.2f), environment.Coefficient0 / SqrtFourPi, 2e-5f);
        var right = environment.Evaluate(Vector3.UnitX);
        var left = environment.Evaluate(-Vector3.UnitX);
        TestAssert.True(right.X > right.Z * 1.5f && left.Z > left.X * 1.5f,
            "Complementary room bounce became a uniform colour wash after projection.");

        var alteredDirect = HeadPreviewEnvironment.FromLightingRig(rig with
        {
            Key = new(-Vector3.UnitX, Vector3.One, 25),
            Fill = new(Vector3.UnitY, Vector3.One, 0),
            Rim = new(Vector3.UnitZ, Vector3.One, 50)
        });
        foreach (var normal in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            TestAssert.Near(environment.Evaluate(normal), alteredDirect.Evaluate(normal), 1e-6f);
    }

    private static Vector3 IntegrateLambertDiffuse(
        float baseRadiance,
        float lobeStrength,
        Vector3 lobeDirection,
        Vector3 normal,
        int sampleCount)
    {
        var sum = 0f;
        for (var index = 0; index < sampleCount; index++)
        {
            var direction = FibonacciDirection(index, sampleCount);
            var radiance = baseRadiance + lobeStrength * Vector3.Dot(lobeDirection, direction);
            sum += radiance * MathF.Max(0, Vector3.Dot(normal, direction));
        }
        return new Vector3(sum * (4f / sampleCount)); // dω/π = 4/N for the unit sphere.
    }

    private static Vector3 FibonacciDirection(int index, int count)
    {
        const float goldenAngle = 2.39996323f;
        var y = 1f - 2f * (index + 0.5f) / count;
        var radial = MathF.Sqrt(MathF.Max(0, 1f - y * y));
        var azimuth = goldenAngle * index;
        return new Vector3(radial * MathF.Cos(azimuth), y, radial * MathF.Sin(azimuth));
    }

    private static HeadPreviewEnvironment CreateRadianceEnvironment(
        Vector3 coefficient0,
        Vector3 coefficient1 = default,
        Vector3 coefficient2 = default,
        Vector3 coefficient3 = default) =>
        new(
            coefficient0,
            coefficient1,
            coefficient2,
            coefficient3,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero);
}
