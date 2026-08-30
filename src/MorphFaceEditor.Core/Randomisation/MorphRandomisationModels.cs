using System.Numerics;

namespace MorphFaceEditor.Core.Randomisation;

public enum MorphRandomisationPoolKey
{
    HumanMaleLe12,
    HumanFemaleLe12,
    HumanMaleLe3,
    HumanFemaleLe3,
    Asari,
    Salarian,
    Turian,
    Krogan,
    Batarian,
    Vorcha
}

public sealed record MorphRandomisationDonor(
    string Id,
    string SourceProfileKey,
    IReadOnlySet<string> AvailableFeatures,
    IReadOnlyDictionary<string, float> NonZeroValues)
{
    public IReadOnlyDictionary<string, float> MaterialScalars { get; init; } =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, Vector4> MaterialVectors { get; init; } =
        new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MaterialTextureFamilies { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    public bool HasMaterialEvidence =>
        MaterialScalars.Count > 0 || MaterialVectors.Count > 0 || MaterialTextureFamilies.Count > 0;
}

public enum MaterialVectorRandomisationKind
{
    PerceptualColour,
    Selector
}

public sealed record MaterialScalarStatistics(
    string Name,
    float Minimum,
    float Maximum,
    float P10,
    float P90);

public sealed record MaterialSelectorState(Vector4 Value, int Count);

public sealed record MaterialVectorStatistics(
    string Name,
    MaterialVectorRandomisationKind Kind,
    Vector4 Minimum,
    Vector4 Maximum,
    IReadOnlyList<MaterialSelectorState> SelectorStates);

public sealed record MaterialRandomisationProfile(
    string ProfileKey,
    IReadOnlyDictionary<string, MaterialScalarStatistics> Scalars,
    IReadOnlyDictionary<string, MaterialVectorStatistics> Vectors,
    IReadOnlySet<string> TextureFamilies);

public sealed record MorphRandomisationCorpus(
    int FormatVersion,
    IReadOnlyDictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>> Pools)
{
    public const int CurrentFormatVersion = 2;

    public IReadOnlyDictionary<string, MaterialRandomisationProfile> MaterialProfiles { get; init; } =
        new Dictionary<string, MaterialRandomisationProfile>(StringComparer.OrdinalIgnoreCase);
}

public sealed record MorphRandomisationFeatureBounds(
    string Name,
    float Minimum,
    float Maximum);

public sealed record MorphRandomisationProposal(
    string DonorId,
    int RandomSeed,
    IReadOnlyDictionary<string, float> Values);

public sealed record MaterialRandomisationScalarBounds(
    string Name,
    float Minimum,
    float Maximum);

public sealed record MaterialRandomisationProposal(
    string DonorId,
    int RandomSeed,
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, Vector4> Vectors,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TextureFamilies);

/// <summary>Maps editor profile identities onto the deliberately shared donor populations.</summary>
public static class MorphRandomisationPoolRouter
{
    public static MorphRandomisationPoolKey Resolve(string profileKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        return profileKey.ToLowerInvariant() switch
        {
            "le1-human-male" or "le2-human-male" => MorphRandomisationPoolKey.HumanMaleLe12,
            "le1-human-female" or "le2-human-female" => MorphRandomisationPoolKey.HumanFemaleLe12,
            "le3-human-male" => MorphRandomisationPoolKey.HumanMaleLe3,
            "le3-human-female" => MorphRandomisationPoolKey.HumanFemaleLe3,
            var value when value.EndsWith("-asari", StringComparison.Ordinal) => MorphRandomisationPoolKey.Asari,
            var value when value.EndsWith("-salarian", StringComparison.Ordinal) => MorphRandomisationPoolKey.Salarian,
            var value when value.EndsWith("-turian", StringComparison.Ordinal) => MorphRandomisationPoolKey.Turian,
            var value when value.EndsWith("-krogan", StringComparison.Ordinal) => MorphRandomisationPoolKey.Krogan,
            var value when value.EndsWith("-batarian", StringComparison.Ordinal) => MorphRandomisationPoolKey.Batarian,
            var value when value.EndsWith("-vorcha", StringComparison.Ordinal) => MorphRandomisationPoolKey.Vorcha,
            _ => throw new NotSupportedException($"Profile '{profileKey}' has no morph-randomisation pool.")
        };
    }
}
