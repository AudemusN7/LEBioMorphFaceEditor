using System.Numerics;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

public sealed record PreparedMaterialRandomisation(
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, Vector4> Vectors,
    IReadOnlyDictionary<string, DecodedTextureAsset?> Textures,
    int AppliedTextureFamilies,
    IReadOnlySet<string> FailedTextureFamilySignatures)
{
    public static PreparedMaterialRandomisation Empty { get; } = new(
        new Dictionary<string, float>(), new Dictionary<string, Vector4>(),
        new Dictionary<string, DecodedTextureAsset?>(), 0, new HashSet<string>());

    public static PreparedMaterialRandomisation? Merge(params PreparedMaterialRandomisation?[] parts)
    {
        if (parts.All(part => part is null)) return null;
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        // Callers list the most specific result first; later profiles only fill unclaimed controls.
        foreach (var part in parts.OfType<PreparedMaterialRandomisation>())
        {
            foreach (var (name, value) in part.Scalars) scalars.TryAdd(name, value);
            foreach (var (name, value) in part.Vectors) vectors.TryAdd(name, value);
            foreach (var (name, value) in part.Textures) textures.TryAdd(name, value);
            failures.UnionWith(part.FailedTextureFamilySignatures);
            applied += part.AppliedTextureFamilies;
        }
        return new(scalars, vectors, textures, applied, failures);
    }
}
