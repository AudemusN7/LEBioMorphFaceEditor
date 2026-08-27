using System.Numerics;

namespace MorphFaceEditor.Rendering;

public enum HeadPreviewLightingPreset
{
    Studio,
    HighContrast,
    WarmInterior,
    CoolNight
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
    float AmbientOcclusionStrength,
    float BloomThreshold,
    float BloomIntensity);

public static class HeadPreviewLightingPresets
{
    private static readonly HeadPreviewLightingRig Studio = new(
        new(new Vector3(-0.42f, 0.55f, 0.72f), new Vector3(1.00f, 0.96f, 0.91f), 0.88f),
        new(new Vector3(0.62f, 0.16f, 0.77f), new Vector3(0.78f, 0.88f, 1.00f), 0.34f),
        new(new Vector3(0.18f, 0.36f, -0.92f), new Vector3(0.80f, 0.90f, 1.00f), 0.58f),
        new Vector3(0.88f, 0.93f, 1.00f),
        0.18f,
        1.04f,
        new Vector3(0.31f, 0.25f, 0.22f),
        new Vector3(0.55f, 0.67f, 0.82f),
        0.055f,
        0.09f,
        0.58f,
        0.78f,
        0.075f);

    private static readonly HeadPreviewLightingRig HighContrast = new(
        new(new Vector3(-0.72f, 0.34f, 0.60f), new Vector3(1.00f, 0.86f, 0.74f), 1.00f),
        new(new Vector3(0.58f, -0.04f, 0.81f), new Vector3(0.52f, 0.65f, 0.90f), 0.08f),
        new(new Vector3(0.42f, 0.34f, -0.84f), new Vector3(0.58f, 0.76f, 1.00f), 0.72f),
        new Vector3(0.55f, 0.62f, 0.76f),
        0.045f,
        1.02f,
        new Vector3(0.16f, 0.10f, 0.08f),
        new Vector3(0.28f, 0.38f, 0.58f),
        0.035f,
        0.055f,
        0.68f,
        0.72f,
        0.085f);

    private static readonly HeadPreviewLightingRig WarmInterior = new(
        new(new Vector3(-0.48f, 0.46f, 0.75f), new Vector3(1.00f, 0.62f, 0.34f), 0.92f),
        new(new Vector3(0.64f, 0.06f, 0.77f), new Vector3(1.00f, 0.48f, 0.25f), 0.24f),
        new(new Vector3(0.12f, 0.42f, -0.90f), new Vector3(1.00f, 0.76f, 0.50f), 0.42f),
        new Vector3(1.00f, 0.55f, 0.30f),
        0.095f,
        1.02f,
        new Vector3(0.25f, 0.09f, 0.035f),
        new Vector3(0.72f, 0.30f, 0.12f),
        0.07f,
        0.08f,
        0.60f,
        0.76f,
        0.07f);

    private static readonly HeadPreviewLightingRig CoolNight = new(
        new(new Vector3(-0.48f, 0.38f, 0.79f), new Vector3(0.40f, 0.58f, 1.00f), 0.42f),
        new(new Vector3(0.68f, -0.04f, 0.73f), new Vector3(0.22f, 0.38f, 0.78f), 0.10f),
        new(new Vector3(0.20f, 0.32f, -0.93f), new Vector3(0.34f, 0.72f, 1.00f), 0.48f),
        new Vector3(0.18f, 0.30f, 0.68f),
        0.045f,
        0.82f,
        new Vector3(0.025f, 0.035f, 0.09f),
        new Vector3(0.10f, 0.25f, 0.55f),
        0.045f,
        0.12f,
        0.70f,
        0.68f,
        0.065f);

    public static HeadPreviewLightingRig Get(HeadPreviewLightingPreset preset) => preset switch
    {
        HeadPreviewLightingPreset.Studio => Studio,
        HeadPreviewLightingPreset.HighContrast => HighContrast,
        HeadPreviewLightingPreset.WarmInterior => WarmInterior,
        HeadPreviewLightingPreset.CoolNight => CoolNight,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preview lighting preset.")
    };
}
