using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

public sealed record TextureCatalogReadResult(
    TextureRegistryStatus RegistryStatus,
    IReadOnlyList<TextureCatalogCandidate> Candidates,
    IReadOnlyList<MorphFaceTemplateCandidate> MorphFaceTemplates)
{
    public bool IsAvailable => RegistryStatus.State == TextureRegistryState.Ready;
    public IReadOnlyList<AttachmentMeshCandidate> AttachmentMeshes { get; init; } = [];
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
        var fingerprint = (Installed: store.GetFileFingerprint(game),
            Manual: store.GetManualFileFingerprint(game));
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
                return new TextureCatalogReadResult(cached.Status, cached.Candidates, cached.MorphFaceTemplates)
                    { AttachmentMeshes = cached.AttachmentMeshes };
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            fingerprint = (Installed: store.GetFileFingerprint(game),
                Manual: store.GetManualFileFingerprint(game));
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
                    return new TextureCatalogReadResult(cached.Status, cached.Candidates, cached.MorphFaceTemplates)
                        { AttachmentMeshes = cached.AttachmentMeshes };
            }

            var stored = await Task.Run(() => store.ReadWithStatus(game), cancellationToken).ConfigureAwait(false);
            var status = stored.Status;
            if (stored.Snapshot is not { } snapshot)
                return new TextureCatalogReadResult(status, [], []);
            var manual = store.ReadManualWithStatus(game);
            if (manual.Status.State == TextureRegistryState.Ready && manual.Snapshot is not null)
                snapshot = TextureRegistryManualAssetService.Combine(snapshot, manual.Snapshot);
            else if (manual.Status.State is not TextureRegistryState.Missing)
                status = status with
                {
                    ErrorMessage = $"Custom asset database is {manual.Status.State}: {manual.Status.ErrorMessage}"
                };
            cancellationToken.ThrowIfCancellationRequested();
            fingerprint = (Installed: store.GetFileFingerprint(game),
                Manual: store.GetManualFileFingerprint(game));
            lock (_cacheLock) _cache[game] = new CachedCatalog(
                fingerprint, status, snapshot.Candidates, snapshot.MorphFaceTemplates,
                snapshot.AttachmentMeshes);
            return new TextureCatalogReadResult(status, snapshot.Candidates, snapshot.MorphFaceTemplates)
                { AttachmentMeshes = snapshot.AttachmentMeshes };
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
        ((string Path, long Length, DateTime LastWriteTimeUtc)? Installed,
            (string Path, long Length, DateTime LastWriteTimeUtc)? Manual) Fingerprint,
        TextureRegistryStatus Status,
        IReadOnlyList<TextureCatalogCandidate> Candidates,
        IReadOnlyList<MorphFaceTemplateCandidate> MorphFaceTemplates,
        IReadOnlyList<AttachmentMeshCandidate> AttachmentMeshes);
}
