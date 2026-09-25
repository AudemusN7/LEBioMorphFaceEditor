using System.Globalization;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Randomisation;

/// <summary>Builds Player face and scar texture choices from verified game assets.</summary>
public static class PlayerFaceScarTexturePolicy
{
    private const string HmmFacePrefix = "HMM_HED_PROCustom_";
    private const string HmfFacePrefix = "HMF_HED_PROCustom_";
    private const ulong FaceSelectionStream = 0xFACE5E7A11UL;
    private const ulong ScarSelectionStream = 0x5CA45E1EC7UL;

    /// <summary>
    /// Combines reviewed NPC Diff/Norm donor pairs with Player Custom face pairs found
    /// in the base-game texture registry. Candidate enumeration and selection are
    /// deduplicated by the exact paired texture paths, so donor frequency cannot weight
    /// this roll and Diff/Norm cannot become mismatched.
    /// </summary>
    public static IReadOnlyList<PlayerFaceTextureSet> CreateFacePool(
        string profileKey,
        IEnumerable<PlayerFaceTextureSet> npcFaceSets,
        IEnumerable<TextureCatalogCandidate> installedCandidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        ArgumentNullException.ThrowIfNull(npcFaceSets);
        ArgumentNullException.ThrowIfNull(installedCandidates);

        if (!TryResolveProfile(profileKey, out var game, out var isHumanMale)) return [];

        var playerSets = ResolvePlayerFaceSets(game, isHumanMale, installedCandidates);
        return npcFaceSets
            .Where(IsCompletePair)
            .Concat(playerSets)
            .GroupBy(FacePairKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(value => value.PlayerDiffuseAsset is not null)
                .ThenBy(value => value.DiffusePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.NormalPath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(value => value.DiffusePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.NormalPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Selects uniformly from distinct complete NPC and Player face pairs.</summary>
    public static PlayerFaceTextureSet? SelectFaceSet(
        string profileKey,
        IEnumerable<PlayerFaceTextureSet> npcFaceSets,
        IEnumerable<TextureCatalogCandidate> installedCandidates,
        int randomSeed)
    {
        var pool = CreateFacePool(profileKey, npcFaceSets, installedCandidates);
        if (pool.Count == 0) return null;
        var random = new MorphRandomiser.StableRandom(
            unchecked((ulong)(uint)randomSeed) ^ FaceSelectionStream);
        return pool[random.NextIndex(pool.Count)];
    }

    /// <summary>
    /// Selects a base-game Player scar texture or the explicit no-scar result. HMF
    /// supports this packed scar slot in LE1 only. A selected scar sets both blend
    /// strengths to 1; no-scar sets both to 0.
    /// </summary>
    public static PlayerScarTextureSelection? SelectScar(
        string profileKey,
        IEnumerable<TextureCatalogCandidate> installedCandidates,
        int randomSeed,
        bool scarParameterSupported = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        ArgumentNullException.ThrowIfNull(installedCandidates);
        if (!scarParameterSupported || !TryResolveProfile(profileKey, out var game, out var isHumanMale))
            return null;
        if (!isHumanMale && game != TextureCatalogGame.LE1) return null;

        var candidates = ResolvePlayerScars(game, isHumanMale, installedCandidates)
            .Cast<PlayerScarTextureSelection?>()
            .Prepend(PlayerScarTextureSelection.NoScar)
            .ToArray();
        var random = new MorphRandomiser.StableRandom(
            unchecked((ulong)(uint)randomSeed) ^ ScarSelectionStream);
        return candidates[random.NextIndex(candidates.Length)];
    }

    private static IReadOnlyList<PlayerFaceTextureSet> ResolvePlayerFaceSets(
        TextureCatalogGame game,
        bool isHumanMale,
        IEnumerable<TextureCatalogCandidate> installedCandidates)
    {
        var prefix = isHumanMale ? HmmFacePrefix : HmfFacePrefix;
        var bySuffix = installedCandidates
            .Where(candidate => candidate.Game == game)
            .SelectMany(candidate => BaseGameOccurrences(candidate)
                .Select(occurrence => new
                {
                    Candidate = candidate,
                    Occurrence = occurrence,
                    // Keep the registry's package-local export path on resolution
                    // identities. A package-qualified PCC path is only for persisted
                    // cross-package references and is not an entry selector here.
                    Path = candidate.InstancedPath
                }))
            .Where(value => value.Candidate.ObjectName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(value =>
            {
                var name = value.Candidate.ObjectName;
                var component = name.EndsWith("_Diff", StringComparison.OrdinalIgnoreCase)
                    ? "Diff"
                    : name.EndsWith("_Norm", StringComparison.OrdinalIgnoreCase) ? "Norm" : null;
                var suffix = component is null ? null : name[prefix.Length..^(component.Length + 1)];
                var asset = new AssetIdentity(
                    value.Occurrence.PackagePath,
                    value.Path,
                    value.Occurrence.ExportUIndex,
                    "Texture2D");
                return new FaceTextureComponent(component, suffix, value.Path, asset,
                    value.Occurrence.MountPriority);
            })
            .Where(value => value.Component is not null && !string.IsNullOrWhiteSpace(value.Suffix))
            .GroupBy(value => (Component: value.Component!, Suffix: value.Suffix!), FaceComponentKeyComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(value => value.MountPriority)
                    .ThenBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
                    .First(),
                FaceComponentKeyComparer.Instance);

        var diffuseSuffixes = bySuffix.Keys
            .Where(key => key.Component.Equals("Diff", StringComparison.OrdinalIgnoreCase))
            .Select(key => key.Suffix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return diffuseSuffixes
            .Select(suffix =>
            {
                // The HMF Frek diffuse deliberately uses the common Military normal.
                var normalSuffix = !isHumanMale && suffix.Equals("Frek", StringComparison.OrdinalIgnoreCase)
                    ? "Military"
                    : suffix;
                return bySuffix.TryGetValue(("Diff", suffix), out var diffuse) &&
                       bySuffix.TryGetValue(("Norm", normalSuffix), out var normal)
                    ? new PlayerFaceTextureSet(diffuse.Path, normal.Path, diffuse.Asset, normal.Asset)
                    : null;
            })
            .Where(value => value is not null)
            .Cast<PlayerFaceTextureSet>()
            .ToArray();
    }

    private static IReadOnlyList<PlayerScarTextureSelection> ResolvePlayerScars(
        TextureCatalogGame game,
        bool isHumanMale,
        IEnumerable<TextureCatalogCandidate> installedCandidates)
    {
        var prefix = isHumanMale ? HmmFacePrefix : HmfFacePrefix;
        return installedCandidates
            .Where(candidate => candidate.Game == game &&
                                candidate.ObjectName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                IsScarObject(candidate.ObjectName[prefix.Length..]))
            .SelectMany(candidate => BaseGameOccurrences(candidate)
                .Select(occurrence => new
                {
                    Candidate = candidate,
                    Occurrence = occurrence,
                    Path = candidate.InstancedPath
                }))
            .GroupBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(value => value.Occurrence.MountPriority)
                .ThenBy(value => value.Occurrence.PackagePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
            .Select(value => new PlayerScarTextureSelection(
                new AssetIdentity(
                    value.Occurrence.PackagePath,
                    value.Path,
                    value.Occurrence.ExportUIndex,
                    "Texture2D"),
                1,
                isHumanMale ? 1 : 0))
            .ToArray();
    }

    private static bool IsScarObject(string suffix)
    {
        const string marker = "Scr";
        if (!suffix.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(suffix[marker.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static IEnumerable<TextureCatalogOccurrence> BaseGameOccurrences(
        TextureCatalogCandidate candidate) =>
        candidate.Occurrences
            .DefaultIfEmpty(candidate.EffectiveOccurrence)
            .Where(occurrence => occurrence.Origin == TextureCatalogOrigin.BaseGame);

    private static bool IsCompletePair(PlayerFaceTextureSet value) =>
        !string.IsNullOrWhiteSpace(value.DiffusePath) &&
        !string.IsNullOrWhiteSpace(value.NormalPath);

    private static string FacePairKey(PlayerFaceTextureSet value) =>
        $"{value.DiffusePath}\n{value.NormalPath}";

    private static bool TryResolveProfile(
        string profileKey,
        out TextureCatalogGame game,
        out bool isHumanMale)
    {
        var parts = profileKey.Split('-', StringSplitOptions.RemoveEmptyEntries);
        game = parts.Length > 0
            ? parts[0].ToUpperInvariant() switch
            {
                "LE1" => TextureCatalogGame.LE1,
                "LE2" => TextureCatalogGame.LE2,
                "LE3" => TextureCatalogGame.LE3,
                _ => (TextureCatalogGame)(-1)
            }
            : (TextureCatalogGame)(-1);
        isHumanMale = parts.Length == 3 && parts[1].Equals("human", StringComparison.OrdinalIgnoreCase) &&
                      parts[2].Equals("male", StringComparison.OrdinalIgnoreCase);
        var isHumanFemale = parts.Length == 3 &&
                            parts[1].Equals("human", StringComparison.OrdinalIgnoreCase) &&
                            parts[2].Equals("female", StringComparison.OrdinalIgnoreCase);
        return game is TextureCatalogGame.LE1 or TextureCatalogGame.LE2 or TextureCatalogGame.LE3 &&
               (isHumanMale || isHumanFemale);
    }

    private sealed record FaceTextureComponent(
        string? Component,
        string? Suffix,
        string Path,
        AssetIdentity Asset,
        int MountPriority);

    private sealed class FaceComponentKeyComparer : IEqualityComparer<(string Component, string Suffix)>
    {
        public static FaceComponentKeyComparer Instance { get; } = new();

        public bool Equals((string Component, string Suffix) x, (string Component, string Suffix) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Component, y.Component) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Suffix, y.Suffix);

        public int GetHashCode((string Component, string Suffix) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Component),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Suffix));
    }
}

/// <summary>A matched face diffuse and normal, including exact Player asset identities when applicable.</summary>
public sealed record PlayerFaceTextureSet(
    string DiffusePath,
    string NormalPath,
    AssetIdentity? PlayerDiffuseAsset = null,
    AssetIdentity? PlayerNormalAsset = null);

/// <summary>
/// A scar choice. A null texture denotes no scar. The scalar values are explicit so
/// callers can apply them only when those parameters exist on the selected material.
/// </summary>
public sealed record PlayerScarTextureSelection(
    AssetIdentity? TextureAsset,
    float CustomScarScalar,
    float ScarDiffuseScalar)
{
    public bool HasScar => TextureAsset is not null;

    public static PlayerScarTextureSelection NoScar { get; } = new(null, 0, 0);
}
