using System.Text.Json.Serialization;

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
    Mod,
    Manual
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
    public IReadOnlyList<TextureMipStorageRecord> Mips { get; init; } = [];
    public string PackageName => Path.GetFileName(PackagePath);
    public string OriginLabel => Origin switch
    {
        TextureCatalogOrigin.BaseGame => "Base game",
        TextureCatalogOrigin.OfficialDlc => "Official DLC",
        TextureCatalogOrigin.Mod => "Mod",
        TextureCatalogOrigin.Manual => "Custom",
        _ => Origin.ToString()
    };
}

/// <summary>
/// A single UE3-facing Texture2D path with all installed verified occurrences.
/// The effective occurrence is the canonical official default. Pickers can
/// expose each physical occurrence with its donor provenance.
/// </summary>
public sealed record TextureCatalogCandidate(
    TextureCatalogGame Game,
    string InstancedPath,
    TextureCatalogOccurrence EffectiveOccurrence,
    IReadOnlyList<TextureCatalogOccurrence> Occurrences)
{
    /// <summary>True only when the effective official donor is from its filename-winning package.</summary>
    [JsonIgnore]
    public bool IsEligibleOfficialDefault { get; init; }

    public string ObjectName => InstancedPath.Split('.').Last();
    public string DisplayOrigin => EffectiveOccurrence.OriginLabel;
    public string SourcePackagePath => EffectiveOccurrence.PackagePath;
}

/// <summary>Picker-only projection; registry occurrences retain their exact export paths.</summary>
public static class TextureCatalogPicker
{
    public static string CanonicalPath(string instancedPath, string packagePath)
    {
        var packageName = Path.GetFileNameWithoutExtension(packagePath);
        return !IsBiogPackage(packagePath) ||
               instancedPath.StartsWith("BIO", StringComparison.OrdinalIgnoreCase) ||
               instancedPath.StartsWith($"{packageName}.", StringComparison.OrdinalIgnoreCase)
            ? instancedPath
            : $"{packageName}.{instancedPath}";
    }

    public static IReadOnlyList<TextureCatalogCandidate> Select(
        IEnumerable<TextureCatalogCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Select(candidate => (Candidate: candidate, Occurrence: candidate.EffectiveOccurrence,
                Canonical: CanonicalPath(candidate.InstancedPath, candidate.EffectiveOccurrence.PackagePath)))
            .GroupBy(value => value.Canonical, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var values = group.ToArray();
                if (values.Length == 1) return values[0].Candidate;
                var primary = values.OrderByDescending(value => value.Candidate.IsEligibleOfficialDefault &&
                                                                  IsOfficial(value.Occurrence.Origin))
                    .ThenBy(value => value.Occurrence.Origin switch
                    {
                        TextureCatalogOrigin.Manual => 0,
                        TextureCatalogOrigin.Mod => 1,
                        _ => 2
                    })
                    .ThenByDescending(value => value.Occurrence.MountPriority)
                    .ThenBy(value => value.Occurrence.PackagePath, StringComparer.OrdinalIgnoreCase)
                    .First();
                var occurrences = values
                    .SelectMany(value => value.Candidate.Occurrences.Append(value.Occurrence))
                    .DistinctBy(value => $"{Path.GetFullPath(value.PackagePath)}|{value.ExportUIndex}",
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => SourceRank(value.Origin))
                    .ThenByDescending(value => value.MountPriority)
                    .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var rawPaths = values.Select(value => value.Candidate.InstancedPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return primary.Candidate with
                {
                    InstancedPath = rawPaths.Length > 1 ? group.Key : primary.Candidate.InstancedPath,
                    EffectiveOccurrence = primary.Occurrence,
                    Occurrences = occurrences,
                    IsEligibleOfficialDefault = primary.Candidate.IsEligibleOfficialDefault &&
                                                IsOfficial(primary.Occurrence.Origin)
                };
            })
            .ToArray();
    }

    private static bool IsOfficial(TextureCatalogOrigin origin) =>
        origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc;

    private static int SourceRank(TextureCatalogOrigin origin) => origin switch
    {
        TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc => 0,
        TextureCatalogOrigin.Manual => 1,
        _ => 2
    };

    private static bool IsBiogPackage(string packagePath) =>
        Path.GetFileNameWithoutExtension(packagePath).StartsWith("BIOG", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Profile-owned discovery and ordering signals. They promote but never hide valid textures.</summary>
public sealed record TextureCatalogProfile(
    string Key,
    IReadOnlyList<string> PreferredPathFragments,
    IReadOnlyList<string> SharedPathFragments)
{
    public static TextureCatalogProfile Empty { get; } = new(string.Empty, [], []);
}
