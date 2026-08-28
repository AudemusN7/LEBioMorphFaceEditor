using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>One compact catalogue read, including the database state that produced it.</summary>
public sealed record TextureCatalogReadResult(
    ObjectDatabaseStatus DatabaseStatus,
    IReadOnlyList<TextureCatalogCandidate> Candidates)
{
    public bool IsAvailable => DatabaseStatus.State == ObjectDatabaseState.Ready;
}

/// <summary>
/// Loads an OIDB only long enough to project verified texture candidates. The
/// large OIDB object graph is never retained after this operation completes.
/// </summary>
public sealed class TextureCatalogService(ObjectDatabaseProvider provider)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(MorphFaceGame Game, string ProfileKey), CachedCatalog> _cache = [];

    public async Task<TextureCatalogReadResult> ReadAsync(
        MorphFaceGame game,
        TextureCatalogProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var status = provider.GetStatus(game);
        if (status.State != ObjectDatabaseState.Ready || status.FilePath is null)
        {
            return new TextureCatalogReadResult(status, []);
        }

        var fingerprint = new CatalogFingerprint(status.FilePath, status.FileSize ?? 0, status.LastWriteTime);
        var cacheKey = (game, profile.Key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.Fingerprint == fingerprint)
            {
                return new TextureCatalogReadResult(status, cached.Candidates);
            }

            return await Task.Run(() => Build(game, profile, status, fingerprint, cacheKey, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(MorphFaceGame game)
    {
        foreach (var key in _cache.Keys.Where(key => key.Game == game).ToArray())
        {
            _cache.Remove(key);
        }
    }

    private TextureCatalogReadResult Build(
        MorphFaceGame game,
        TextureCatalogProfile profile,
        ObjectDatabaseStatus status,
        CatalogFingerprint fingerprint,
        (MorphFaceGame Game, string ProfileKey) cacheKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!provider.TryOpenActive(game, out var database) || database is null)
        {
            var unavailable = provider.GetStatus(game) with { State = ObjectDatabaseState.Missing };
            return new TextureCatalogReadResult(unavailable, []);
        }

        using var resolver = new PackageTextureCatalogOccurrenceResolver(game);
        var entries = database.GetAllObjectPaths(alphabetical: false)
            .Where(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return TextureCatalogProjector.IsRelevantPath(path, profile);
            })
            .Select(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new TextureCatalogIndexEntry(path, database.GetFilesContainingObject(path) ?? []);
            });
        var candidates = TextureCatalogProjector.Project(game, profile, entries, resolver);
        _cache[cacheKey] = new CachedCatalog(fingerprint, candidates);
        return new TextureCatalogReadResult(status, candidates);
    }

    private sealed record CatalogFingerprint(string FilePath, long FileSize, DateTimeOffset? LastWriteTime);
    private sealed record CachedCatalog(CatalogFingerprint Fingerprint, IReadOnlyList<TextureCatalogCandidate> Candidates);
}

/// <summary>Resolves OIDB locations in bounded batches and captures source metadata for later preview/porting.</summary>
internal sealed class PackageTextureCatalogOccurrenceResolver : ITextureCatalogOccurrenceResolver, IDisposable
{
    private const int MaximumOpenPackages = 16;
    private readonly MorphFaceGame _game;
    private readonly MEGame _meGame;
    private readonly Dictionary<string, IMEPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _packageOrder = [];

    public PackageTextureCatalogOccurrenceResolver(MorphFaceGame game)
    {
        _game = game;
        _meGame = game switch
        {
            MorphFaceGame.LE1 => MEGame.LE1,
            MorphFaceGame.LE2 => MEGame.LE2,
            MorphFaceGame.LE3 => MEGame.LE3,
            _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Texture catalogues are available only for Legendary Edition games.")
        };
        LegendaryExplorerCoreRuntime.Initialize();
    }

    public bool TryResolve(
        MorphFaceGame game,
        string packagePath,
        string instancedPath,
        out TextureCatalogOccurrence occurrence)
    {
        occurrence = default!;
        if (game != _game)
        {
            return false;
        }
        try
        {
            var fullPath = ResolvePackagePath(packagePath);
            var package = GetPackage(fullPath);
            if (package.FindEntry(instancedPath, "Texture2D") is not ExportEntry textureExport)
            {
                return false;
            }

            var texture = new Texture2D(textureExport);
            var topMip = texture.GetTopMip();
            var hasExternalMips = texture.Mips.Any(mip =>
                mip.storageType != StorageTypes.empty &&
                ((int)mip.storageType & (int)StorageFlags.externalFile) != 0);
            occurrence = new TextureCatalogOccurrence(
                fullPath,
                textureExport.UIndex,
                GetMountPriority(fullPath),
                GetOrigin(fullPath),
                topMip?.width ?? 0,
                topMip?.height ?? 0,
                textureExport.GetProperty<EnumProperty>("Format")?.Value.Name ?? texture.TextureFormat ?? "Unknown",
                textureExport.GetProperty<EnumProperty>("LODGroup")?.Value.Name ?? "Unknown",
                hasExternalMips,
                textureExport.GetProperty<NameProperty>("TextureFileCacheName")?.Value.Instanced);
            return true;
        }
        catch (Exception)
        {
            // An OIDB can legitimately contain stale/unreadable entries. Exact
            // export verification rejects just that occurrence, not the catalogue.
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var package in _packages.Values)
        {
            package.Dispose();
        }
        _packages.Clear();
    }

    private IMEPackage GetPackage(string path)
    {
        if (_packages.TryGetValue(path, out var cached))
        {
            return cached;
        }
        var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        _packages[path] = package;
        _packageOrder.Enqueue(path);
        if (_packageOrder.Count > MaximumOpenPackages)
        {
            var expiredPath = _packageOrder.Dequeue();
            if (_packages.Remove(expiredPath, out var expired))
            {
                expired.Dispose();
            }
        }
        return package;
    }

    private string ResolvePackagePath(string packagePath)
    {
        if (Path.IsPathRooted(packagePath))
        {
            return packagePath;
        }
        var gameRoot = MEDirectories.GetDefaultGamePath(_meGame)
            ?? throw new DirectoryNotFoundException($"No installed {_game} game path is configured in Legendary Explorer.");
        return Path.Combine(gameRoot, packagePath);
    }

    private TextureCatalogOrigin GetOrigin(string packagePath)
    {
        var dlcDirectory = FindDlcDirectory(packagePath);
        if (dlcDirectory is null) return TextureCatalogOrigin.BaseGame;
        return MELoadedDLC.IsOfficialDLC(dlcDirectory, _meGame)
            ? TextureCatalogOrigin.OfficialDlc
            : TextureCatalogOrigin.Mod;
    }

    private int GetMountPriority(string packagePath)
    {
        var dlcDirectory = FindDlcDirectory(packagePath);
        return dlcDirectory is null ? 0 : MELoadedDLC.GetMountPriority(dlcDirectory, _meGame);
    }

    private static string? FindDlcDirectory(string packagePath)
    {
        var current = new FileInfo(packagePath).Directory;
        while (current?.Parent is not null)
        {
            if (current.Parent.Name.Equals("DLC", StringComparison.OrdinalIgnoreCase) &&
                current.Name.StartsWith("DLC_", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }
}
