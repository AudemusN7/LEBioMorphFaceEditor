using System.Numerics;

namespace MorphFaceEditor.Rendering;

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
        float toneCurveStrength = 0.6f,
        Vector3? shadowTint = null,
        Vector3? highlightTint = null)
    {
        ValidateFinite(shadows, nameof(shadows));
        ValidateFinite(highlights, nameof(highlights));
        ValidatePositive(highlights, nameof(highlights));
        ValidateFinite(midtones, nameof(midtones));
        ValidatePositive(midtones, nameof(midtones));
        ValidateFinite(colorScale, nameof(colorScale));
        ValidateFinite(overlay, nameof(overlay));
        ValidateFinite(bloomTint, nameof(bloomTint));
        var resolvedShadowTint = shadowTint ?? Vector3.One;
        var resolvedHighlightTint = highlightTint ?? Vector3.One;
        ValidateFinite(resolvedShadowTint, nameof(shadowTint));
        ValidatePositive(resolvedShadowTint, nameof(shadowTint));
        ValidateFinite(resolvedHighlightTint, nameof(highlightTint));
        ValidatePositive(resolvedHighlightTint, nameof(highlightTint));

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
        ShadowTint = resolvedShadowTint;
        HighlightTint = resolvedHighlightTint;
    }

    public Vector3 Shadows { get; }
    public Vector3 Highlights { get; }
    public Vector3 Midtones { get; }
    public Vector3 ColorScale { get; }
    public Vector3 Overlay { get; }
    public float Desaturation { get; }
    public float BloomScreenBlendThreshold { get; }
    /// <summary>Gaussian standard deviation in viewport pixels at a height of 1080.</summary>
    public float BloomRadius1080 { get; }
    public Vector3 BloomTint { get; }
    /// <summary>Blend toward the game response from the accepted neutral tone curve.</summary>
    public float ToneCurveStrength { get; }
    /// <summary>Luminance-preserving colour gains blended into the dark end of the LUT.</summary>
    public Vector3 ShadowTint { get; }
    /// <summary>Colour gains for lit midtones and highlights, fading out at white.</summary>
    public Vector3 HighlightTint { get; }

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
            throw new ArgumentOutOfRangeException(parameterName, "Colour gains must be greater than zero in every channel.");
        }
    }
}

/// <summary>
/// Produces authored mood grades and a 16-cube atlas for the preview.
/// The profile values are authored approximations, not averages of game levels.
/// </summary>
public static class HeadPreviewPostProcessProfiles
{
    private static readonly HeadPreviewPostProcessSettings Neutral = new();

    private static readonly HeadPreviewPostProcessSettings HighContrast = new(
        shadows: new Vector3(0.002f, 0.002f, 0.002f),
        highlights: new Vector3(1.025f, 1.022f, 1.018f),
        midtones: new Vector3(0.975f, 0.978f, 0.983f),
        colorScale: new Vector3(1.01f, 1f, 0.985f),
        overlay: Vector3.Zero,
        desaturation: 0.055f,
        bloomScreenBlendThreshold: 10f,
        bloomRadius1080: 20f,
        bloomTint: Vector3.One,
        shadowTint: new Vector3(0.86f, 0.96f, 1.13f),
        highlightTint: new Vector3(1.065f, 1.025f, 0.93f));

    private static readonly HeadPreviewPostProcessSettings WarmInterior = new(
        shadows: Vector3.Zero,
        highlights: Vector3.One,
        midtones: Vector3.One,
        colorScale: new Vector3(1.06f, 1.01f, 0.88f),
        overlay: Vector3.Zero,
        desaturation: 0f,
        bloomScreenBlendThreshold: 10f,
        bloomRadius1080: 30f,
        bloomTint: new Vector3(1.08f, 1.00f, 0.88f),
        shadowTint: new Vector3(1.08f, 0.94f, 0.83f),
        highlightTint: new Vector3(1.17f, 1.04f, 0.78f));

    private static readonly HeadPreviewPostProcessSettings CoolNight = new(
        shadows: Vector3.Zero,
        highlights: Vector3.One,
        midtones: Vector3.One,
        colorScale: new Vector3(0.88f, 0.96f, 1.12f),
        overlay: Vector3.Zero,
        desaturation: 0f,
        bloomScreenBlendThreshold: 10f,
        bloomRadius1080: 22f,
        bloomTint: new Vector3(0.88f, 0.98f, 1.12f),
        shadowTint: new Vector3(0.75f, 0.94f, 1.30f),
        highlightTint: new Vector3(0.85f, 1.02f, 1.16f));

    private static readonly HeadPreviewPostProcessSettings Cinematic = new(
        shadows: new Vector3(0.006f, 0.004f, 0.003f),
        highlights: new Vector3(1.025f, 1.025f, 1.020f),
        midtones: new Vector3(0.980f, 0.985f, 0.990f),
        colorScale: Vector3.One,
        overlay: Vector3.Zero,
        desaturation: 0.025f,
        bloomScreenBlendThreshold: 10f,
        bloomRadius1080: 24f,
        bloomTint: Vector3.One,
        shadowTint: new Vector3(0.78f, 0.93f, 1.18f),
        highlightTint: new Vector3(1.12f, 1.01f, 0.88f));

    /// <summary>
    /// Gets the colour grade associated with the chosen lighting mood.
    /// Studio lighting remains the neutral reference.
    /// </summary>
    public static HeadPreviewPostProcessSettings Get(HeadPreviewLightingPreset preset) => preset switch
    {
        HeadPreviewLightingPreset.Studio => Neutral,
        HeadPreviewLightingPreset.HighContrast => HighContrast,
        HeadPreviewLightingPreset.WarmInterior => WarmInterior,
        HeadPreviewLightingPreset.CoolNight => CoolNight,
        HeadPreviewLightingPreset.Cinematic => Cinematic,
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
                        && settings.Desaturation == 0f
                        && settings.ShadowTint == Vector3.One
                        && settings.HighlightTint == Vector3.One;

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
                    // Author separate shadow/highlight colours inside the cached LUT.
                    // Normalizing the gains preserves luminance rather than using tint
                    // as another exposure control. Black stays black; white fades neutral.
                    var shadowWeight = 1f - SmoothStep(0.02f, 0.35f, luminance);
                    var highlightWeight = SmoothStep(0.30f, 0.80f, luminance)
                                          * (1f - SmoothStep(0.85f, 1f, luminance));
                    var tint = Vector3.Lerp(Vector3.One, settings.ShadowTint, shadowWeight)
                               * Vector3.Lerp(Vector3.One, settings.HighlightTint, highlightWeight);
                    var tinted = corrected * tint;
                    var tintedLuminance = Vector3.Dot(tinted, new Vector3(0.299f, 0.587f, 0.114f));
                    corrected = tinted * (luminance / Math.Max(tintedLuminance, epsilon));
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

    private static float SmoothStep(float low, float high, float value)
    {
        var t = Math.Clamp((value - low) / (high - low), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float GradeChannel(float input, float shadow, float highlight, float midtone, float epsilon)
    {
        var shifted = Math.Clamp(input - shadow, 0f, 1f);
        var normalized = Math.Max(shifted / highlight, epsilon);
        return MathF.Pow(normalized, midtone);
    }
}
