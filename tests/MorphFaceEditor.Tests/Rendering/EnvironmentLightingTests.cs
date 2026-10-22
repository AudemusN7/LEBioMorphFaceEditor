using System.Numerics;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

public static class EnvironmentLightingTests
{
    private const float SqrtFourPi = 3.5449077f;
    private const float Y1 = 0.48860252f;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("SH diffuse evaluation matches cosine integral for tilted normals", TiltedNormalMatchesCosineIntegral)
    ];

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
