namespace MorphFaceEditor.Core.Materials;

/// <summary>Legendary Edition game identity kept independent of package-library types.</summary>
public enum TextureCatalogGame
{
    LE1,
    LE2,
    LE3
}

/// <summary>Human-readable provenance inferred from an installed package location.</summary>
public enum TextureCatalogOrigin
{
    BaseGame,
    OfficialDlc,
    Mod
}

/// <summary>
/// The verified source export for a path occurrence. This remains external to the
/// working PCC until the later save-time materialisation stage.
/// </summary>
public sealed record TextureCatalogOccurrence(
    string PackagePath,
    int ExportUIndex,
    int MountPriority,
    TextureCatalogOrigin Origin,
    int Width,
    int Height,
    string PixelFormat,
    string TextureGroup,
    bool HasExternalMips,
    string? TextureFileCacheName)
{
    public string PackageName => Path.GetFileName(PackagePath);
    public string OriginLabel => Origin switch
    {
        TextureCatalogOrigin.BaseGame => "Base game",
        TextureCatalogOrigin.OfficialDlc => "Official DLC",
        TextureCatalogOrigin.Mod => "Mod",
        _ => Origin.ToString()
    };
}

/// <summary>
/// A single UE3-facing Texture2D path with all installed verified occurrences.
/// The effective occurrence is chosen by mount precedence; duplicate paths are
/// never exposed as misleading independent picker entries.
/// </summary>
public sealed record TextureCatalogCandidate(
    TextureCatalogGame Game,
    string InstancedPath,
    TextureCatalogOccurrence EffectiveOccurrence,
    IReadOnlyList<TextureCatalogOccurrence> Occurrences)
{
    public string ObjectName => InstancedPath.Split('.').Last();
    public string DisplayOrigin => EffectiveOccurrence.OriginLabel;
    public string SourcePackagePath => EffectiveOccurrence.PackagePath;
}

/// <summary>Profile-owned discovery and ordering signals. They promote but never hide valid textures.</summary>
public sealed record TextureCatalogProfile(
    string Key,
    IReadOnlyList<string> PreferredPathFragments,
    IReadOnlyList<string> SharedPathFragments)
{
    public static TextureCatalogProfile Empty { get; } = new(string.Empty, [], []);
}

/// <summary>Filters candidates and keeps the profile ordering deterministic after every search.</summary>
public static class TextureCatalogSearch
{
    public static IReadOnlyList<TextureCatalogCandidate> FilterAndRank(
        IEnumerable<TextureCatalogCandidate> candidates,
        TextureCatalogProfile profile,
        string? activeTexturePath,
        string? searchText)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(profile);
        var query = searchText?.Trim() ?? string.Empty;
        return candidates
            .Where(candidate => Matches(candidate, query))
            .OrderBy(candidate => Rank(candidate, profile, activeTexturePath))
            .ThenBy(candidate => candidate.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Matches(TextureCatalogCandidate candidate, string query) =>
        query.Length == 0 ||
        candidate.ObjectName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        candidate.InstancedPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        candidate.EffectiveOccurrence.PackagePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        candidate.DisplayOrigin.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static int Rank(
        TextureCatalogCandidate candidate,
        TextureCatalogProfile profile,
        string? activeTexturePath)
    {
        if (!string.IsNullOrWhiteSpace(activeTexturePath) &&
            candidate.InstancedPath.Equals(activeTexturePath, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (ContainsAny(candidate.InstancedPath, profile.PreferredPathFragments)) return 1;
        if (ContainsAny(candidate.InstancedPath, profile.SharedPathFragments)) return 2;
        return 3;
    }

    private static bool ContainsAny(string path, IReadOnlyList<string> fragments) =>
        fragments.Any(fragment => !string.IsNullOrWhiteSpace(fragment) &&
            path.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
