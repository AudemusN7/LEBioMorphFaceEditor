namespace MorphFaceEditor.Core.Materials;

/// <summary>Detached storage metadata for one non-empty texture mip.</summary>
public sealed record TextureMipStorageRecord(
    int Index,
    int Width,
    int Height,
    int StorageType,
    int UncompressedSize,
    int CompressedSize,
    int ExternalOffset,
    string? TextureCacheName);

/// <summary>The complete compact registry payload for one installed Legendary Edition game.</summary>
public sealed record TextureRegistrySnapshot(
    int SchemaVersion,
    TextureCatalogGame Game,
    DateTimeOffset BuiltAtUtc,
    int InstalledPackageCount,
    IReadOnlyList<TextureCatalogCandidate> Candidates)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Shared, non-exclusive discovery rules used by the one-pass installed-package scanner.</summary>
public static class TextureRegistryDiscovery
{
    private static readonly string[] RelevantPathFragments =
    [
        "PROMorph",
        "HMM_HIR",
        "HMF_HIR",
        "HMM_EYE",
        "HMF_EYE",
        "HED_EYE",
        "ASA_EYE",
        "SAL_EYE",
        "TUR_EYE",
        "KRO_EYE",
        "BAT_EYE"
    ];

    public static bool IsRelevantPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return RelevantPathFragments.Any(fragment =>
            path.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Resolves texture paths against the local package plus installed registry without guessing
/// when the same object name exists at more than one UE3 path.
/// </summary>
public sealed class TextureCatalogAvailability
{
    private readonly IReadOnlyDictionary<string, string> _paths;
    private readonly IReadOnlyDictionary<string, string?> _uniqueObjectNames;

    public TextureCatalogAvailability(
        IEnumerable<string> localPaths,
        IEnumerable<TextureCatalogCandidate> installedCandidates)
    {
        ArgumentNullException.ThrowIfNull(localPaths);
        ArgumentNullException.ThrowIfNull(installedCandidates);

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in localPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            paths.TryAdd(path, path);
        }
        foreach (var candidate in installedCandidates)
        {
            paths.TryAdd(candidate.InstancedPath, candidate.InstancedPath);
        }

        var objectNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Values)
        {
            var objectName = path.Split('.').Last();
            if (!objectNames.TryAdd(objectName, path) &&
                !string.Equals(objectNames[objectName], path, StringComparison.OrdinalIgnoreCase))
            {
                objectNames[objectName] = null;
            }
        }

        _paths = paths;
        _uniqueObjectNames = objectNames;
    }

    public bool Contains(string requestedPath) => TryResolve(requestedPath, out _);

    public bool TryResolve(string requestedPath, out string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        if (_paths.TryGetValue(requestedPath, out canonicalPath!))
        {
            return true;
        }

        var objectName = requestedPath.Split('.').Last();
        if (_uniqueObjectNames.TryGetValue(objectName, out var uniquePath) && uniquePath is not null)
        {
            canonicalPath = uniquePath;
            return true;
        }

        canonicalPath = string.Empty;
        return false;
    }
}
