namespace MorphFaceEditor.Rendering;

/// <summary>Preview AO radii use the same world units as the head mesh, independently of zoom and viewport size.</summary>
public static class HeadPreviewAmbientOcclusionSettings
{
    public const float ContactRadiusWorld = 2.0f;
    public const float CavityRadiusWorld = 3.2f;
    public const float ContactStrength = 2.5f;
    public const float CavityStrength = 3.5f;
    public const float AngularBias = 0.33f;
    public const float FilterDepthSigmaWorld = 0.16f;
    public const float PowerExponent = 5.25f;
}
