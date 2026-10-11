using System.Numerics;

namespace MorphFaceEditor.Rendering;

public enum HeadPreviewLightingPreset
{
    Studio,
    HighContrast,
    WarmInterior,
    CoolNight,
    Cinematic
}

public readonly record struct HeadPreviewDirectionalLight
{
    public HeadPreviewDirectionalLight(Vector3 direction, Vector3 color, float intensity)
    {
        if (!IsFinite(direction) || direction.LengthSquared() < 1e-6f)
        {
            throw new ArgumentException("Light direction must be finite and non-zero.", nameof(direction));
        }
        if (!IsFinite(color) || color.X < 0 || color.Y < 0 || color.Z < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(color), "Light colour must be finite and non-negative.");
        }
        if (!float.IsFinite(intensity) || intensity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), "Light intensity must be finite and non-negative.");
        }

        Direction = Vector3.Normalize(direction);
        Color = color;
        Intensity = intensity;
    }

    public Vector3 Direction { get; }
    public Vector3 Color { get; }
    public float Intensity { get; }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

public sealed record HeadPreviewLightingRig(
    HeadPreviewDirectionalLight Key,
    HeadPreviewDirectionalLight Fill,
    HeadPreviewDirectionalLight Rim,
    Vector3 AmbientColor,
    float AmbientIntensity,
    float Exposure,
    Vector3 GroundColor,
    Vector3 HorizonColor,
    float KeyAngularRadius,
    float AmbientFresnel,
    float BloomThreshold,
    float BloomIntensity)
{
    // Broad room/window bounce in the camera-relative light rig, independent
    // of the three direct sources. Explicit environment overrides are world-space.
    public IReadOnlyList<HeadPreviewEnvironmentLobe> EnvironmentLobes { get; init; } = [];
    public float ShadowSoftnessWorld { get; init; } = 0.085f;
}

/// <summary>A broad radiance lobe used only when projecting diffuse room lighting.</summary>
public readonly record struct HeadPreviewEnvironmentLobe
{
    public HeadPreviewEnvironmentLobe(Vector3 direction, Vector3 radiance, float power = 2f)
    {
        // Reuse the finite, non-negative colour and normalized direction contract.
        var light = new HeadPreviewDirectionalLight(direction, radiance, 1f);
        if (!float.IsFinite(power) || power is < 1f or > 8f)
            throw new ArgumentOutOfRangeException(nameof(power), "Diffuse lobes must be broad (power 1 to 8).");
        Direction = light.Direction;
        Radiance = light.Color;
        Power = power;
    }

    public Vector3 Direction { get; }
    public Vector3 Radiance { get; }
    public float Power { get; }

    internal Vector3 Evaluate(Vector3 direction) =>
        Radiance * MathF.Pow(MathF.Max(0, Vector3.Dot(Direction, direction)), Power);
}

public static class HeadPreviewLightingPresets
{
    private static readonly HeadPreviewLightingRig Studio = new(
        new(new Vector3(-0.48f, 0.60f, 0.64f), new Vector3(1.00f, 0.97f, 0.93f), 1.12f),
        new(new Vector3(0.65f, 0.12f, 0.75f), new Vector3(0.88f, 0.94f, 1.00f), 0.38f),
        new(new Vector3(0.42f, 0.52f, -0.75f), new Vector3(0.94f, 0.97f, 1.00f), 0.68f),
        new Vector3(0.96f, 0.98f, 1.00f),
        0.20f,
        1.00f,
        new Vector3(0.20f, 0.18f, 0.16f),
        new Vector3(0.42f, 0.46f, 0.52f),
        0.055f,
        0.09f,
        1.10f,
        0.045f)
    {
        ShadowSoftnessWorld = 0.14f,
        EnvironmentLobes = Array.AsReadOnly<HeadPreviewEnvironmentLobe>([
            new(new Vector3(-0.75f, 0.55f, 0.36f), new Vector3(0.85f, 0.82f, 0.78f)),
            new(new Vector3(0.85f, 0.25f, 0.45f), new Vector3(0.46f, 0.52f, 0.62f)),
            new(Vector3.UnitY, new Vector3(0.30f, 0.32f, 0.36f), 1f)])
    };

    private static readonly HeadPreviewLightingRig HighContrast = new(
        new(new Vector3(-0.74f, 0.40f, 0.54f), new Vector3(1.00f, 0.90f, 0.80f), 1.06f),
        new(new Vector3(0.58f, 0.04f, 0.81f), new Vector3(0.58f, 0.70f, 0.94f), 0.14f),
        new(new Vector3(0.42f, 0.34f, -0.84f), new Vector3(0.62f, 0.80f, 1.00f), 0.76f),
        new Vector3(0.58f, 0.66f, 0.80f),
        0.065f,
        1.02f,
        new Vector3(0.16f, 0.10f, 0.08f),
        new Vector3(0.28f, 0.38f, 0.58f),
        0.035f,
        0.055f,
        1.15f,
        0.055f)
    {
        ShadowSoftnessWorld = 0.09f,
        EnvironmentLobes = Array.AsReadOnly<HeadPreviewEnvironmentLobe>([
            new(new Vector3(-0.70f, 0.55f, 0.45f), new Vector3(0.32f, 0.27f, 0.22f), 2f),
            new(new Vector3(0.62f, 0.24f, 0.75f), new Vector3(0.16f, 0.24f, 0.38f), 1f)])
    };

    private static readonly HeadPreviewLightingRig WarmInterior = new(
        new(new Vector3(-0.48f, 0.43f, 0.76f), new Vector3(1.00f, 0.62f, 0.34f), 1.06f),
        new(new Vector3(0.64f, 0.06f, 0.77f), new Vector3(1.00f, 0.55f, 0.31f), 0.22f),
        new(new Vector3(0.12f, 0.42f, -0.90f), new Vector3(1.00f, 0.76f, 0.50f), 0.62f),
        new Vector3(1.00f, 0.55f, 0.30f),
        0.11f,
        1.00f,
        new Vector3(0.25f, 0.09f, 0.035f),
        new Vector3(0.72f, 0.30f, 0.12f),
        0.07f,
        0.08f,
        1.00f,
        0.065f)
    {
        ShadowSoftnessWorld = 0.16f,
        EnvironmentLobes = Array.AsReadOnly<HeadPreviewEnvironmentLobe>([
            new(new Vector3(-0.65f, 0.50f, 0.57f), new Vector3(1.10f, 0.45f, 0.17f)),
            new(new Vector3(0.82f, 0.20f, 0.53f), new Vector3(0.10f, 0.065f, 0.035f)),
            new(new Vector3(0, -0.70f, 0.71f), new Vector3(0.36f, 0.12f, 0.035f), 1f)])
    };

    private static readonly HeadPreviewLightingRig CoolNight = new(
        new(new Vector3(-0.36f, 0.30f, 0.88f), new Vector3(0.34f, 0.52f, 1.00f), 0.42f),
        new(new Vector3(0.68f, 0.04f, 0.73f), new Vector3(0.28f, 0.40f, 0.75f), 0.14f),
        new(new Vector3(0.23f, 0.36f, -0.90f), new Vector3(0.24f, 0.58f, 1.00f), 0.48f),
        new Vector3(0.18f, 0.32f, 0.70f),
        0.055f,
        0.78f,
        new Vector3(0.025f, 0.035f, 0.09f),
        new Vector3(0.10f, 0.25f, 0.55f),
        0.045f,
        0.12f,
        0.85f,
        0.05f)
    {
        ShadowSoftnessWorld = 0.16f,
        EnvironmentLobes = Array.AsReadOnly<HeadPreviewEnvironmentLobe>([
            new(new Vector3(-0.30f, 0.85f, 0.40f), new Vector3(0.18f, 0.34f, 0.82f), 1f),
            new(new Vector3(0.65f, 0.22f, 0.73f), new Vector3(0.12f, 0.22f, 0.45f), 1f)])
    };

    // Cyan key with a red-orange edge, supported by complementary room bounce.
    private static readonly HeadPreviewLightingRig Cinematic = new(
        new(new Vector3(0.54f, 0.36f, 0.76f), new Vector3(0.12f, 0.78f, 1.00f), 1.18f),
        new(new Vector3(-0.76f, 0.12f, 0.40f), new Vector3(1.00f, 0.18f, 0.045f), 0.30f),
        new(new Vector3(-0.66f, 0.30f, -0.69f), new Vector3(1.00f, 0.095f, 0.025f), 1.40f),
        new Vector3(0.44f, 0.62f, 0.80f),
        0.065f,
        1.00f,
        new Vector3(0.025f, 0.025f, 0.045f),
        new Vector3(0.08f, 0.12f, 0.18f),
        0.045f,
        0.09f,
        1.05f,
        0.065f)
    {
        ShadowSoftnessWorld = 0.06f,
        EnvironmentLobes = Array.AsReadOnly<HeadPreviewEnvironmentLobe>([
            new(new Vector3(0.74f, 0.32f, 0.59f), new Vector3(0.08f, 0.72f, 1.00f), 3f),
            new(new Vector3(-0.86f, 0.22f, -0.46f), new Vector3(1.00f, 0.085f, 0.02f), 3f)])
    };

    // Projection is performed once per preset, never per frame or per material.
    private static readonly HeadPreviewEnvironment StudioEnvironment = HeadPreviewEnvironment.FromLightingRig(Studio);
    private static readonly HeadPreviewEnvironment HighContrastEnvironment = HeadPreviewEnvironment.FromLightingRig(HighContrast);
    private static readonly HeadPreviewEnvironment WarmInteriorEnvironment = HeadPreviewEnvironment.FromLightingRig(WarmInterior);
    private static readonly HeadPreviewEnvironment CoolNightEnvironment = HeadPreviewEnvironment.FromLightingRig(CoolNight);
    private static readonly HeadPreviewEnvironment CinematicEnvironment = HeadPreviewEnvironment.FromLightingRig(Cinematic);

    public static HeadPreviewEnvironment GetEnvironment(HeadPreviewLightingPreset preset) => preset switch
    {
        HeadPreviewLightingPreset.Studio => StudioEnvironment,
        HeadPreviewLightingPreset.HighContrast => HighContrastEnvironment,
        HeadPreviewLightingPreset.WarmInterior => WarmInteriorEnvironment,
        HeadPreviewLightingPreset.CoolNight => CoolNightEnvironment,
        HeadPreviewLightingPreset.Cinematic => CinematicEnvironment,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preview lighting preset.")
    };

    public static HeadPreviewLightingRig Get(HeadPreviewLightingPreset preset) => preset switch
    {
        HeadPreviewLightingPreset.Studio => Studio,
        HeadPreviewLightingPreset.HighContrast => HighContrast,
        HeadPreviewLightingPreset.WarmInterior => WarmInterior,
        HeadPreviewLightingPreset.CoolNight => CoolNight,
        HeadPreviewLightingPreset.Cinematic => Cinematic,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preview lighting preset.")
    };
}
