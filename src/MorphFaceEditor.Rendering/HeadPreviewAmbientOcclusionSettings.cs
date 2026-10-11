namespace MorphFaceEditor.Rendering;

/// <summary>Preview AO radii use the same world units as the head mesh, independently of zoom and viewport size.</summary>
public sealed record HeadPreviewAmbientOcclusionSettings(
    float ContactRadiusWorld = 2.0f,
    float CavityRadiusWorld = 3.2f,
    float ContactStrength = 2.5f,
    float CavityStrength = 3.5f,
    float AngularBias = 0.33f,
    float FilterDepthSigmaWorld = 0.16f,
    float FilterNormalPower = 0.0f,
    float PowerExponent = 5.25f,
    bool FilteringEnabled = true)
{
    public static HeadPreviewAmbientOcclusionSettings Default { get; } = new();
}
