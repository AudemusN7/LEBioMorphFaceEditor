using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

public sealed record TextureCatalogReadResult(
    TextureRegistryStatus RegistryStatus,
    IReadOnlyList<TextureCatalogCandidate> Candidates)
{
    public bool IsAvailable => RegistryStatus.State == TextureRegistryState.Ready;
}

/// <summary>Loads and caches the compact registry without opening an installed package.</summary>
public sealed class TextureCatalogService(TextureRegistryStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<MorphFaceGame, CachedCatalog> _cache = [];

    public async Task<TextureCatalogReadResult> ReadAsync(
        MorphFaceGame game,
        CancellationToken cancellationToken = default)
    {
        var initialStatus = store.GetStatus(game);
        if (initialStatus.State != TextureRegistryState.Ready || initialStatus.FilePath is null)
            return new TextureCatalogReadResult(initialStatus, []);

        var fingerprint = Fingerprint(initialStatus);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
                return new TextureCatalogReadResult(initialStatus, cached.Candidates);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = store.GetStatus(game);
            if (status.State != TextureRegistryState.Ready || status.FilePath is null)
                return new TextureCatalogReadResult(status, []);
            fingerprint = Fingerprint(status);
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
                    return new TextureCatalogReadResult(status, cached.Candidates);
            }

            var snapshot = await Task.Run(() => store.Read(game), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheLock) _cache[game] = new CachedCatalog(fingerprint, snapshot.Candidates);
            return new TextureCatalogReadResult(status, snapshot.Candidates);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(MorphFaceGame game)
    {
        lock (_cacheLock) _cache.Remove(game);
    }

    private static CatalogFingerprint Fingerprint(TextureRegistryStatus status) =>
        new(status.FilePath!, status.FileSize ?? 0, status.LastBuilt);

    private sealed record CatalogFingerprint(string FilePath, long FileSize, DateTimeOffset? LastBuilt);
    private sealed record CachedCatalog(CatalogFingerprint Fingerprint, IReadOnlyList<TextureCatalogCandidate> Candidates);
}
