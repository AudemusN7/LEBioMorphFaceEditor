using System.Numerics;

namespace MorphFaceEditor.Rendering;

/// <summary>
/// Selects whether the preview uses the plain reference view or a restrained,
/// game-informed presentation grade.
/// </summary>
public enum HeadPreviewPostProcessMode
{
    Neutral,
    GameInspired
}

/// <summary>
/// Settings for the preview's spatially uniform grading and bloom receiver.
/// These are preview approximations; they are not copied from a particular level.
/// </summary>
public sealed record HeadPreviewPostProcessSettings
{
    public HeadPreviewPostProcessSettings()
        : this(
            Vector3.Zero,
            Vector3.One,
            Vector3.One,
            Vector3.One,
            Vector3.Zero,
            0f,
            10f,
            24f,
            Vector3.One)
    {
    }

    public HeadPreviewPostProcessSettings(
        Vector3 shadows,
        Vector3 highlights,
        Vector3 midtones,
        Vector3 colorScale,
        Vector3 overlay,
        float desaturation,
        float bloomScreenBlendThreshold,
        float bloomRadius1080,
        Vector3 bloomTint,
        float toneCurveStrength = 0.6f)
    {
        ValidateFinite(shadows, nameof(shadows));
        ValidateFinite(highlights, nameof(highlights));
        ValidatePositive(highlights, nameof(highlights));
        ValidateFinite(midtones, nameof(midtones));
        ValidatePositive(midtones, nameof(midtones));
        ValidateFinite(colorScale, nameof(colorScale));
        ValidateFinite(overlay, nameof(overlay));
        ValidateFinite(bloomTint, nameof(bloomTint));

        if (!float.IsFinite(toneCurveStrength) || toneCurveStrength is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(toneCurveStrength), "Tone curve strength must be finite and between 0 and 1.");
        }

        if (!float.IsFinite(desaturation) || desaturation is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(desaturation), "Desaturation must be finite and between 0 and 1.");
        }
        if (!float.IsFinite(bloomScreenBlendThreshold) || bloomScreenBlendThreshold < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(bloomScreenBlendThreshold), "Bloom screen-blend threshold must be finite and non-negative.");
        }
        if (!float.IsFinite(bloomRadius1080) || bloomRadius1080 is < 0.5f or > 128f)
        {
            throw new ArgumentOutOfRangeException(nameof(bloomRadius1080), "Bloom radius must be finite and between 0.5 and 128 pixels at 1080p.");
        }
        if (bloomTint.X < 0f || bloomTint.Y < 0f || bloomTint.Z < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(bloomTint), "Bloom tint must be non-negative.");
        }

        Shadows = shadows;
        Highlights = highlights;
        Midtones = midtones;
        ColorScale = colorScale;
        Overlay = overlay;
        Desaturation = desaturation;
        BloomScreenBlendThreshold = bloomScreenBlendThreshold;
        BloomRadius1080 = bloomRadius1080;
        BloomTint = bloomTint;
        ToneCurveStrength = toneCurveStrength;
    }

    public Vector3 Shadows { get; }
    public Vector3 Highlights { get; }
    public Vector3 Midtones { get; }
    public Vector3 ColorScale { get; }
    public Vector3 Overlay { get; }
    public float Desaturation { get; }
    public float BloomScreenBlendThreshold { get; }
    public float BloomRadius1080 { get; }
    public Vector3 BloomTint { get; }
    /// <summary>Blend toward the game response from the accepted neutral tone curve.</summary>
    public float ToneCurveStrength { get; }

    private static void ValidateFinite(Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Colour values must be finite.");
        }
    }

    private static void ValidatePositive(Vector3 value, string parameterName)
    {
        if (value.X <= 0f || value.Y <= 0f || value.Z <= 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Highlights and midtones must be greater than zero in every channel.");
        }
    }
}

/// <summary>
/// Produces restrained generic grades and a 16-cube atlas for the preview.
/// The profile values are authored approximations, not averages of game levels.
/// </summary>
public static class HeadPreviewPostProcessProfiles
{
    private static readonly HeadPreviewPostProcessSettings Neutral = new();

    private static readonly HeadPreviewPostProcessSettings HighContrast = new(
        shadows: new Vector3(0.004f, 0.004f, 0.005f),
        highlights: new Vector3(1.012f, 1.010f, 1.006f),
        midtones: new Vector3(0.985f, 0.987f, 0.990f),
        colorScale: Vector3.One,
        overlay: Vector3.Zero,
        desaturation: 0.005f,
        bloomScreenBlendThreshold: 10f,
        bloomRadius1080: 24f,
        bloomTint: Vector3.One);

    private static readonly HeadPreviewPostProcessSettings WarmInterior = new(
        shadows: Vector3.Zero,
        highlights: Vector3.One,
        midtones: Vector3.One,
        colorScale: new Vector3(1.012f, 1.000f, 0.984f),
        overlay: new Vector3(0.0015f, 0.0005f, 0f),
        desaturation: 0f,
        bloomScreenBlendThreshold: 10f,
        bloomRadius1080: 24f,
        bloomTint: new Vector3(1.015f, 1.000f, 0.975f));

    private static readonly HeadPreviewPostProcessSettings CoolNight = new(
        shadows: Vector3.Zero,
        highlights: Vector3.One,
        midtones: Vector3.One,
        colorScale: new Vector3(0.988f, 1.000f, 1.014f),
        overlay: new Vector3(0f, 0.0003f, 0.0012f),
        desaturation: 0f,
        bloomScreenBlendThreshold: 10f,
        bloomRadius1080: 24f,
        bloomTint: new Vector3(0.980f, 1.000f, 1.020f));

    /// <summary>
    /// Gets a subtle generic grade associated with the chosen lighting mood.
    /// Studio lighting remains the neutral reference.
    /// </summary>
    public static HeadPreviewPostProcessSettings Get(HeadPreviewLightingPreset preset) => preset switch
    {
        HeadPreviewLightingPreset.Studio => Neutral,
        HeadPreviewLightingPreset.HighContrast => HighContrast,
        HeadPreviewLightingPreset.WarmInterior => WarmInterior,
        HeadPreviewLightingPreset.CoolNight => CoolNight,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preview lighting preset.")
    };

    /// <summary>
    /// Builds a 256x16 RGBA atlas containing a 16x16x16 RGB grading cube.
    /// Red varies across each 16-pixel tile, green vertically, and blue by tile.
    /// The returned cube stays linear; gamma encoding belongs to the final output pass.
    /// </summary>
    public static float[] BuildGradingLut(HeadPreviewPostProcessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        const int atlasWidth = 256;
        const int atlasHeight = 16;
        const int cubeSize = 16;
        const float epsilon = 1e-8f;
        var pixels = new float[atlasWidth * atlasHeight * 4];

        // Preserve the comparison path exactly, including black and white cube corners.
        var isNeutral = settings.Shadows == Vector3.Zero
                        && settings.Highlights == Vector3.One
                        && settings.Midtones == Vector3.One
                        && settings.ColorScale == Vector3.One
                        && settings.Overlay == Vector3.Zero
                        && settings.Desaturation == 0f;

        for (var y = 0; y < atlasHeight; y++)
        {
            var green = y / (float)(cubeSize - 1);
            for (var x = 0; x < atlasWidth; x++)
            {
                var red = (x % cubeSize) / (float)(cubeSize - 1);
                var blue = (x / cubeSize) / (float)(cubeSize - 1);
                var color = new Vector3(red, green, blue);

                if (!isNeutral)
                {
                    var corrected = new Vector3(
                        GradeChannel(color.X, settings.Shadows.X, settings.Highlights.X, settings.Midtones.X, epsilon),
                        GradeChannel(color.Y, settings.Shadows.Y, settings.Highlights.Y, settings.Midtones.Y, epsilon),
                        GradeChannel(color.Z, settings.Shadows.Z, settings.Highlights.Z, settings.Midtones.Z, epsilon));

                    // UE3's common shader uses host-provided luminance weights. Those
                    // exact host values remain unverified, so the preview explicitly
                    // uses the legacy luma coefficients from the final blend shader.
                    var luminance = corrected.X * 0.299f + corrected.Y * 0.587f + corrected.Z * 0.114f;
                    var gray = new Vector3(luminance);
                    corrected = Vector3.Lerp(corrected, gray, settings.Desaturation);
                    color = corrected * settings.ColorScale + settings.Overlay;
                    color = Vector3.Clamp(color, Vector3.Zero, Vector3.One);
                }

                var offset = (y * atlasWidth + x) * 4;
                pixels[offset] = color.X;
                pixels[offset + 1] = color.Y;
                pixels[offset + 2] = color.Z;
                pixels[offset + 3] = 1f;
            }
        }

        return pixels;
    }

    private static float GradeChannel(float input, float shadow, float highlight, float midtone, float epsilon)
    {
        var shifted = Math.Clamp(input - shadow, 0f, 1f);
        var normalized = Math.Max(shifted / highlight, epsilon);
        return MathF.Pow(normalized, midtone);
    }
}
