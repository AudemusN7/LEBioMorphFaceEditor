using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>One OIDB path with its candidate package locations.</summary>
public sealed record TextureCatalogIndexEntry(string InstancedPath, IReadOnlyList<string> PackagePaths);

/// <summary>
/// Verifies a single OIDB occurrence against the authoritative export in its PCC.
/// It deliberately returns a detached model so neither OIDB nor package objects
/// survive in the editor's catalogue.
/// </summary>
public interface ITextureCatalogOccurrenceResolver
{
    bool TryResolve(
        MorphFaceGame game,
        string packagePath,
        string instancedPath,
        out TextureCatalogOccurrence occurrence);
}

/// <summary>
/// Projects relevant OIDB paths into a compact catalogue. Exact Texture2D
/// verification is a hard admission boundary, not an optional display hint.
/// </summary>
public static class TextureCatalogProjector
{
    public static IReadOnlyList<TextureCatalogCandidate> Project(
        MorphFaceGame game,
        TextureCatalogProfile profile,
        IEnumerable<TextureCatalogIndexEntry> entries,
        ITextureCatalogOccurrenceResolver resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(resolver);

        return entries
            .Where(entry => IsRelevantPath(entry.InstancedPath, profile))
            .Select(entry => ProjectEntry(game, entry, resolver, cancellationToken))
            .Where(candidate => candidate is not null)
            .Cast<TextureCatalogCandidate>()
            .OrderBy(candidate => candidate.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TextureCatalogCandidate? ProjectEntry(
        MorphFaceGame game,
        TextureCatalogIndexEntry entry,
        ITextureCatalogOccurrenceResolver resolver,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = new List<TextureCatalogOccurrence>();
        foreach (var packagePath in entry.PackagePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolver.TryResolve(game, packagePath, entry.InstancedPath, out var occurrence))
            {
                resolved.Add(occurrence);
            }
        }
        var occurrences = resolved
            .OrderByDescending(occurrence => occurrence.MountPriority)
            .ThenByDescending(occurrence => occurrence.Origin)
            .ThenBy(occurrence => occurrence.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return occurrences.Length == 0
            ? null
            : new TextureCatalogCandidate(ToCatalogGame(game), entry.InstancedPath, occurrences[0], occurrences);
    }

    /// <summary>Fast path-only admission filter used before resolving OIDB locations.</summary>
    public static bool IsRelevantPath(string path, TextureCatalogProfile profile) =>
        path.Contains("PROMorph", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("HMM_HIR", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("HMF_HIR", StringComparison.OrdinalIgnoreCase) ||
        profile.SharedPathFragments.Any(fragment => !string.IsNullOrWhiteSpace(fragment) &&
            path.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static TextureCatalogGame ToCatalogGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => TextureCatalogGame.LE1,
        MorphFaceGame.LE2 => TextureCatalogGame.LE2,
        MorphFaceGame.LE3 => TextureCatalogGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Texture catalogues are available only for Legendary Edition games.")
    };
}
