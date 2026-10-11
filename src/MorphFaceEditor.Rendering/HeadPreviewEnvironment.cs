using System.Numerics;

namespace MorphFaceEditor.Rendering;

/// <summary>
/// A second-order real spherical-harmonic representation of diffuse environment lighting.
/// Coefficients represent radiance projections; <see cref="Evaluate(Vector3)"/> applies
/// the normalized Lambert cosine convolution before evaluating the result.
/// </summary>
/// <remarks>
/// The orthonormal basis is ordered [Y00, z, y, x, xz, zy, (3y²-1), xy, (x²-z²)],
/// with y as the vertical axis:
/// [0.282095, 0.488603*z, 0.488603*y, 0.488603*x, 1.092548*x*z,
///  1.092548*z*y, 0.315392*(3*y*y-1), 1.092548*x*y,
///  0.546274*(x*x-z*z)]. The diffuse convolution multipliers by band are 1, 2/3,
/// and 1/4. With this convention, constant radiance evaluates to that same RGB value.
/// </remarks>
public sealed class HeadPreviewEnvironment
{
    private const float Y00 = 0.2820948f;
    private const float Y1 = 0.48860252f;
    private const float Y2XY = 1.0925485f;
    private const float Y20 = 0.31539157f;
    private const float Y22 = 0.54627424f;
    private const int IntegrationSampleCount = 512;
    private const float HorizonBandHeight = 0.28f;
    private const float GoldenAngle = 2.3999631f;

    public HeadPreviewEnvironment(
        Vector3 coefficient0,
        Vector3 coefficient1,
        Vector3 coefficient2,
        Vector3 coefficient3,
        Vector3 coefficient4,
        Vector3 coefficient5,
        Vector3 coefficient6,
        Vector3 coefficient7,
        Vector3 coefficient8)
    {
        Coefficient0 = ValidateCoefficient(coefficient0, nameof(coefficient0));
        Coefficient1 = ValidateCoefficient(coefficient1, nameof(coefficient1));
        Coefficient2 = ValidateCoefficient(coefficient2, nameof(coefficient2));
        Coefficient3 = ValidateCoefficient(coefficient3, nameof(coefficient3));
        Coefficient4 = ValidateCoefficient(coefficient4, nameof(coefficient4));
        Coefficient5 = ValidateCoefficient(coefficient5, nameof(coefficient5));
        Coefficient6 = ValidateCoefficient(coefficient6, nameof(coefficient6));
        Coefficient7 = ValidateCoefficient(coefficient7, nameof(coefficient7));
        Coefficient8 = ValidateCoefficient(coefficient8, nameof(coefficient8));
    }

    public Vector3 Coefficient0 { get; }
    public Vector3 Coefficient1 { get; }
    public Vector3 Coefficient2 { get; }
    public Vector3 Coefficient3 { get; }
    public Vector3 Coefficient4 { get; }
    public Vector3 Coefficient5 { get; }
    public Vector3 Coefficient6 { get; }
    public Vector3 Coefficient7 { get; }
    public Vector3 Coefficient8 { get; }

    /// <summary>
    /// Builds an ambient-only environment from the preset's sky, horizon and ground colours.
    /// The distribution is scaled so its mean radiance equals AmbientColor * AmbientIntensity.
    /// Key, fill and rim lights are excluded because they are evaluated separately.
    /// </summary>
    public static HeadPreviewEnvironment FromLightingRig(HeadPreviewLightingRig lighting)
    {
        ArgumentNullException.ThrowIfNull(lighting);
        ValidateEnvironmentColor(lighting.AmbientColor, nameof(lighting.AmbientColor));
        ValidateEnvironmentColor(lighting.GroundColor, nameof(lighting.GroundColor));
        ValidateEnvironmentColor(lighting.HorizonColor, nameof(lighting.HorizonColor));
        if (!float.IsFinite(lighting.AmbientIntensity) || lighting.AmbientIntensity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lighting), "Ambient intensity must be finite and non-negative.");
        }

        var radiance = new Vector3[IntegrationSampleCount];
        var radianceMean = Vector3.Zero;
        for (var index = 0; index < IntegrationSampleCount; index++)
        {
            var direction = FibonacciDirection(index, IntegrationSampleCount);
            radiance[index] = EvaluateEnvironmentRadiance(lighting, direction.Y);
            radianceMean += radiance[index];
        }

        radianceMean /= IntegrationSampleCount;
        var targetMean = lighting.AmbientColor * lighting.AmbientIntensity;
        var uniformFallback = new Vector3(
            radianceMean.X <= 1e-8f ? targetMean.X : 0,
            radianceMean.Y <= 1e-8f ? targetMean.Y : 0,
            radianceMean.Z <= 1e-8f ? targetMean.Z : 0);
        if (uniformFallback != Vector3.Zero)
        {
            for (var index = 0; index < IntegrationSampleCount; index++)
            {
                radiance[index] += uniformFallback;
            }
            radianceMean += uniformFallback;
        }

        var scale = new Vector3(
            NormalizeChannel(radianceMean.X, targetMean.X),
            NormalizeChannel(radianceMean.Y, targetMean.Y),
            NormalizeChannel(radianceMean.Z, targetMean.Z));

        Span<Vector3> coefficients = stackalloc Vector3[9];
        coefficients.Clear();
        Span<float> basis = stackalloc float[9];
        var solidAnglePerSample = 4f * MathF.PI / IntegrationSampleCount;
        for (var index = 0; index < IntegrationSampleCount; index++)
        {
            var direction = FibonacciDirection(index, IntegrationSampleCount);
            EvaluateBasis(direction, basis);
            var scaledRadiance = radiance[index] * scale * solidAnglePerSample;
            for (var coefficient = 0; coefficient < coefficients.Length; coefficient++)
            {
                coefficients[coefficient] += scaledRadiance * basis[coefficient];
            }
        }

        return new HeadPreviewEnvironment(
            coefficients[0], coefficients[1], coefficients[2],
            coefficients[3], coefficients[4], coefficients[5],
            coefficients[6], coefficients[7], coefficients[8]);
    }

    /// <summary>Evaluates normalized Lambert diffuse irradiance for a world-space normal.</summary>
    public Vector3 Evaluate(Vector3 normal)
    {
        if (!IsFinite(normal) || normal.LengthSquared() < 1e-12f)
        {
            throw new ArgumentException("Normal must be finite and non-zero.", nameof(normal));
        }

        Span<float> basis = stackalloc float[9];
        EvaluateBasis(Vector3.Normalize(normal), basis);
        var result = Coefficient0 * basis[0]
            + (Coefficient1 * basis[1] + Coefficient2 * basis[2] + Coefficient3 * basis[3]) * (2f / 3f)
            + (Coefficient4 * basis[4] + Coefficient5 * basis[5] + Coefficient6 * basis[6]
                + Coefficient7 * basis[7] + Coefficient8 * basis[8]) * 0.25f;
        return Vector3.Max(result, Vector3.Zero);
    }

    private static Vector3 EvaluateEnvironmentRadiance(HeadPreviewLightingRig lighting, float vertical)
    {
        var blend = SmoothStep(0, HorizonBandHeight, MathF.Abs(vertical));
        var distantColor = vertical >= 0 ? lighting.AmbientColor : lighting.GroundColor;
        return Vector3.Lerp(lighting.HorizonColor, distantColor, blend);
    }

    private static Vector3 FibonacciDirection(int index, int count)
    {
        var y = 1f - 2f * (index + 0.5f) / count;
        var radial = MathF.Sqrt(MathF.Max(0, 1f - y * y));
        var azimuth = GoldenAngle * index;
        return new Vector3(radial * MathF.Cos(azimuth), y, radial * MathF.Sin(azimuth));
    }

    private static void EvaluateBasis(Vector3 direction, Span<float> basis)
    {
        basis[0] = Y00;
        basis[1] = Y1 * direction.Z;
        basis[2] = Y1 * direction.Y;
        basis[3] = Y1 * direction.X;
        basis[4] = Y2XY * direction.X * direction.Z;
        basis[5] = Y2XY * direction.Z * direction.Y;
        basis[6] = Y20 * (3f * direction.Y * direction.Y - 1f);
        basis[7] = Y2XY * direction.X * direction.Y;
        basis[8] = Y22 * (direction.X * direction.X - direction.Z * direction.Z);
    }

    private static float NormalizeChannel(float mean, float target) =>
        target <= 0 ? 0 : mean > 1e-8f ? target / mean : target;

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        var t = Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
        return t * t * (3f - 2f * t);
    }

    private static Vector3 ValidateCoefficient(Vector3 value, string parameterName)
    {
        if (!IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "SH coefficients must be finite.");
        }
        return value;
    }

    private static void ValidateEnvironmentColor(Vector3 value, string parameterName)
    {
        if (!IsFinite(value) || value.X < 0 || value.Y < 0 || value.Z < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Environment colours must be finite and non-negative.");
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
