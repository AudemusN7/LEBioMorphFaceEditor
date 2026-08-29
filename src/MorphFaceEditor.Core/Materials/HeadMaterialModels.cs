using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Materials;

// Detached material vocabulary shared by the package adapter, editor and renderer.
public enum HeadMaterialFamily
{
    Skin,
    AsariSkin,
    SalarianSkin,
    SalarianEyes,
    TurianSkin,
    TurianEyes,
    BatarianSkin,
    KroganSkin,
    KroganEyes,
    Scalp,
    Eyes,
    Teeth,
    Lashes,
    Hair,
    MaskedHair,
    Accessory,
    Unknown
}

public enum HeadMaterialBlendMode
{
    Opaque,
    Masked,
    Translucent
}

public enum MaterialParameterKind
{
    Scalar,
    Vector,
    Texture
}

public enum TextureRole
{
    Diffuse,
    Normal,
    Mask,
    Specular,
    Detail,
    Tangent,
    Other
}

public enum TextureColorSpace
{
    Srgb,
    Linear
}

public enum TextureAlphaPolicy
{
    Ignore,
    Mask,
    Translucency
}

public sealed record ScalarMaterialOverride(string Name, float Value);

public sealed record VectorMaterialOverride(string Name, Vector4 Value);

public sealed record TextureMaterialOverride(string Name, AssetIdentity? TextureReference);

/// <summary>A detached, decoded mip level in top-to-bottom authored order.</summary>
public sealed record DecodedTextureMip(int Width, int Height, byte[] Rgba8);

/// <summary>One authored cube-map mip containing faces in +X, -X, +Y, -Y, +Z, -Z order.</summary>
public sealed record DecodedTextureCubeMip(int Size, IReadOnlyList<byte[]> Rgba8Faces);

public sealed record MorphFaceMaterialOverrides(
    AssetIdentity? Source,
    IReadOnlyList<ScalarMaterialOverride> Scalars,
    IReadOnlyList<VectorMaterialOverride> Vectors,
    IReadOnlyList<TextureMaterialOverride> Textures)
{
    public static MorphFaceMaterialOverrides Empty { get; } = new(null, [], [], []);
}

public sealed record DecodedTextureAsset(
    AssetIdentity Source,
    int Width,
    int Height,
    byte[] Rgba8,
    string SourceFormat,
    TextureRole Role,
    TextureColorSpace ColorSpace,
    TextureAlphaPolicy AlphaPolicy,
    bool HasMeaningfulAlpha,
    string CacheKey,
    int SourceWidth = 0,
    int SourceHeight = 0,
    IReadOnlyList<DecodedTextureMip>? Mips = null);

public sealed record DecodedTextureCubeAsset(
    AssetIdentity Source,
    int Size,
    IReadOnlyList<byte[]> Rgba8Faces,
    TextureColorSpace ColorSpace,
    string CacheKey,
    IReadOnlyList<DecodedTextureCubeMip>? Mips = null);

public sealed record MaterialTextureBinding(string ParameterName, DecodedTextureAsset Texture);

public sealed record ResolvedHeadMaterial(
    string Key,
    AssetIdentity Source,
    string MasterMaterialName,
    HeadMaterialFamily Family,
    HeadMaterialBlendMode BlendMode,
    bool TwoSided,
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, Vector4> Vectors,
    IReadOnlyDictionary<string, MaterialTextureBinding> Textures)
{
    public IReadOnlyDictionary<string, float> DefaultScalars { get; init; } =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, Vector4> DefaultVectors { get; init; } =
        new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, MaterialTextureBinding> DefaultTextures { get; init; } =
        new Dictionary<string, MaterialTextureBinding>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> SupportedScalars { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> SupportedVectors { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> SupportedTextures { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public DecodedTextureCubeAsset? FixedCubeTexture { get; init; }
    public DecodedTextureCubeAsset? SecondaryFixedCubeTexture { get; init; }

    public bool Supports(string name, MaterialParameterKind kind) => kind switch
    {
        MaterialParameterKind.Scalar => SupportedScalars.Count == 0 ? Scalars.ContainsKey(name) : SupportedScalars.Contains(name),
        MaterialParameterKind.Vector => SupportedVectors.Count == 0 ? Vectors.ContainsKey(name) : SupportedVectors.Contains(name),
        MaterialParameterKind.Texture => SupportedTextures.Count == 0 ? Textures.ContainsKey(name) : SupportedTextures.Contains(name),
        _ => false
    };
}

public sealed record ResolvedHeadMaterialSet(
    IReadOnlyDictionary<string, ResolvedHeadMaterial> Materials)
{
    public static ResolvedHeadMaterialSet Empty { get; } = new(
        new Dictionary<string, ResolvedHeadMaterial>(StringComparer.OrdinalIgnoreCase));

    public ResolvedHeadMaterial? Find(AssetIdentity? identity) =>
        identity is not null && Materials.TryGetValue(MaterialIdentityKey.Create(identity), out var material)
            ? material
            : null;
}

public static class MaterialIdentityKey
{
    public static string Create(AssetIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return $"{Path.GetFullPath(identity.PackagePath)}|{identity.InstancedPath}";
    }
}

public sealed record MaterialParameterDefinition(
    string Name,
    string Label,
    string Group,
    MaterialParameterKind Kind,
    HeadMaterialFamily Family,
    float Minimum = 0,
    float Maximum = 1,
    float Step = 0.01f,
    TextureRole TextureRole = TextureRole.Other,
    TextureColorSpace ColorSpace = TextureColorSpace.Linear,
    TextureAlphaPolicy AlphaPolicy = TextureAlphaPolicy.Ignore,
    string Description = "");
