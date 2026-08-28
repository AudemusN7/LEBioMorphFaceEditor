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
        var fingerprint = store.GetFileFingerprint(game);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
                return new TextureCatalogReadResult(cached.Status, cached.Candidates);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            fingerprint = store.GetFileFingerprint(game);
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
                    return new TextureCatalogReadResult(cached.Status, cached.Candidates);
            }

            var status = await Task.Run(() => store.GetStatus(game), cancellationToken).ConfigureAwait(false);
            if (status.State != TextureRegistryState.Ready || status.FilePath is null)
                return new TextureCatalogReadResult(status, []);

            var snapshot = await Task.Run(() => store.Read(game), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            fingerprint = store.GetFileFingerprint(game);
            lock (_cacheLock) _cache[game] = new CachedCatalog(fingerprint, status, snapshot.Candidates);
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

    private sealed record CachedCatalog(
        (string Path, long Length, DateTime LastWriteTimeUtc)? Fingerprint,
        TextureRegistryStatus Status,
        IReadOnlyList<TextureCatalogCandidate> Candidates);
}
