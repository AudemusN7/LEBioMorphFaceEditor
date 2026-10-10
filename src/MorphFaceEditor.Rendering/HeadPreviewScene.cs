using System.Numerics;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Rendering;

public enum HeadPreviewRenderMode
{
    Diagnostic,
    Shaded,
    Unlit
}

public readonly record struct HeadPreviewBounds(Vector3 Minimum, Vector3 Maximum)
{
    public Vector3 Center => (Minimum + Maximum) * 0.5f;

    public float Radius => Math.Max(Vector3.Distance(Minimum, Maximum) * 0.5f, 0.01f);
}

public readonly record struct HeadPreviewVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Tangent,
    Vector2 TextureCoordinate,
    uint BoneIndex0,
    uint BoneIndex1,
    uint BoneIndex2,
    uint BoneIndex3,
    Vector4 BoneWeights);

public sealed record HeadPreviewTexture(
    string Key,
    string ParameterName,
    int Width,
    int Height,
    byte[] Rgba8,
    TextureRole Role,
    TextureColorSpace ColorSpace,
    TextureAlphaPolicy AlphaPolicy,
    bool HasMeaningfulAlpha,
    IReadOnlyList<DecodedTextureMip>? Mips = null);

public sealed record HeadPreviewCubeTexture(
    string Key,
    int Size,
    IReadOnlyList<byte[]> Rgba8Faces,
    TextureColorSpace ColorSpace,
    IReadOnlyList<DecodedTextureCubeMip>? Mips = null);

public sealed record HeadPreviewMaterial(
    string Key,
    string Name,
    HeadMaterialFamily Family,
    HeadMaterialBlendMode BlendMode,
    bool TwoSided,
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, Vector4> Vectors,
    IReadOnlyDictionary<string, HeadPreviewTexture> Textures)
{
    public IReadOnlySet<string> SupportedScalars { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> SupportedVectors { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> SupportedTextures { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool IsLe2 { get; init; }
    public bool IsLe3 { get; init; }
    public HumanMaterialPreviewModel PreviewModel { get; init; }
    public bool HasCompiledParameterSupport { get; init; }
    public HeadPreviewCubeTexture? FixedCubeTexture { get; init; }
    public HeadPreviewCubeTexture? SecondaryFixedCubeTexture { get; init; }

    public bool Supports(string name, MaterialParameterKind kind) => kind switch
    {
        MaterialParameterKind.Scalar => !HasCompiledParameterSupport && SupportedScalars.Count == 0 ? Scalars.ContainsKey(name) : SupportedScalars.Contains(name),
        MaterialParameterKind.Vector => !HasCompiledParameterSupport && SupportedVectors.Count == 0 ? Vectors.ContainsKey(name) : SupportedVectors.Contains(name),
        MaterialParameterKind.Texture => !HasCompiledParameterSupport && SupportedTextures.Count == 0 ? Textures.ContainsKey(name) : SupportedTextures.Contains(name),
        _ => false
    };

    public HeadPreviewMaterial(string name, HeadMaterialFamily family)
        : this(
            name,
            name,
            family,
            HumanMaterialProfiles.BlendMode(family),
            HumanMaterialProfiles.IsTwoSided(family),
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase))
    {
    }
}

public sealed record HeadPreviewSection(
    int BaseIndex,
    int IndexCount,
    int MaterialSlot,
    HeadPreviewMaterial Material);

public sealed record HeadPreviewMesh(
    string Name,
    IReadOnlyList<HeadPreviewVertex> Vertices,
    IReadOnlyList<uint> Indices,
    IReadOnlyList<HeadPreviewSection> Sections,
    bool IsAttachment = false,
    bool ApplySkinning = false);

public sealed record HeadPreviewScene(
    string Name,
    IReadOnlyList<HeadPreviewMesh> Meshes,
    HeadPreviewBounds Bounds,
    IReadOnlyList<Matrix4x4>? SkinningPalette = null);

public sealed record HeadPreviewDeformationUpdate(
    string MeshName,
    IReadOnlyList<HeadPreviewVertex> Vertices,
    IReadOnlyList<Matrix4x4> SkinningPalette)
{
    public IReadOnlyList<HeadPreviewMeshVertexUpdate> AttachmentUpdates { get; init; } = [];
}

public sealed record HeadPreviewMeshVertexUpdate(
    string MeshName,
    IReadOnlyList<HeadPreviewVertex> Vertices);

public sealed record HeadPreviewMaterialUpdate(
    IReadOnlyDictionary<string, HeadPreviewMaterial> Materials);

public sealed record HeadPreviewOptions(
    HeadPreviewRenderMode RenderMode = HeadPreviewRenderMode.Shaded,
    bool Wireframe = false,
    bool ShowAttachment = true,
    HeadPreviewLightingPreset LightingPreset = HeadPreviewLightingPreset.Studio,
    Vector3? BackgroundColor = null,
    bool AmbientOcclusionEnabled = true,
    bool ShadowsEnabled = true,
    // ShadowDepthBias uses normalized shadow-map depth units.
    float ShadowDepthBias = 0.0003f,
    // ShadowNormalBias is a fraction of the fitted shadow radius.
    float ShadowNormalBias = 0.002f,
    HeadPreviewPostProcessMode PostProcessMode = HeadPreviewPostProcessMode.GameInspired,
    HeadPreviewPostProcessSettings? PostProcessSettings = null,
    bool PostProcessingEnabled = true);

public sealed record HeadPreviewFrame(
    int Width,
    int Height,
    byte[] BgraPixels,
    TimeSpan RenderTime,
    int DrawCalls,
    string DeviceKind)
{
    /// <summary>Depth-only caster draws performed before the camera-view passes.</summary>
    public int ShadowDrawCalls { get; init; }
}
