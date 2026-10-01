using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

public sealed record TextureCatalogReadResult(
    TextureRegistryStatus RegistryStatus,
    IReadOnlyList<TextureCatalogCandidate> Candidates,
    IReadOnlyList<MorphFaceTemplateCandidate> MorphFaceTemplates)
{
    public bool IsAvailable => RegistryStatus.State == TextureRegistryState.Ready;
    public IReadOnlyList<AttachmentMeshCandidate> AttachmentMeshes { get; init; } = [];
    public IReadOnlyList<string> OfficialPackagePaths { get; init; } = [];
}

/// <summary>Loads and caches the compact registry without opening an installed package.</summary>
public sealed class TextureCatalogService
{
    private readonly TextureRegistryStore store;
    private readonly Func<MorphFaceGame, IReadOnlyList<string>, bool> _mountedPackagesCurrent =
        ArePhysicalPackagesCurrent;
    private readonly Func<MorphFaceGame, TextureRegistryBuilder.PhysicalPackageLayoutSnapshot?>? _captureLayout;
    private readonly Func<MorphFaceGame, TextureRegistryBuilder.PhysicalPackageLayoutSnapshot, bool>
        _mountConfigurationCurrent = TextureRegistryBuilder.IsCurrentMountConfiguration;
    private readonly Func<MorphFaceGame, TextureRegistrySnapshot,
        TextureRegistryBuilder.PhysicalPackageLayoutSnapshot, bool> _snapshotMatchesLayout =
        (game, snapshot, layout) => TextureRegistryBuilder.SnapshotMountPrioritiesMatch(snapshot, layout, game);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<MorphFaceGame, CachedCatalog> _cache = [];

    public TextureCatalogService(TextureRegistryStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        _captureLayout = TextureRegistryBuilder.CapturePhysicalPackageLayout;
    }

    internal TextureCatalogService(TextureRegistryStore store,
        Func<MorphFaceGame, IReadOnlyList<string>, bool> mountedPackagesCurrent)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        _mountedPackagesCurrent = mountedPackagesCurrent ?? throw new ArgumentNullException(nameof(mountedPackagesCurrent));
    }

    internal TextureCatalogService(TextureRegistryStore store,
        Func<MorphFaceGame, TextureRegistryBuilder.PhysicalPackageLayoutSnapshot?> captureLayout,
        Func<MorphFaceGame, TextureRegistryBuilder.PhysicalPackageLayoutSnapshot, bool> mountConfigurationCurrent,
        Func<MorphFaceGame, TextureRegistrySnapshot, TextureRegistryBuilder.PhysicalPackageLayoutSnapshot, bool> snapshotMatchesLayout)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        _captureLayout = captureLayout ?? throw new ArgumentNullException(nameof(captureLayout));
        _mountConfigurationCurrent = mountConfigurationCurrent ?? throw new ArgumentNullException(nameof(mountConfigurationCurrent));
        _snapshotMatchesLayout = snapshotMatchesLayout ?? throw new ArgumentNullException(nameof(snapshotMatchesLayout));
    }

    public async Task<TextureCatalogReadResult> ReadAsync(
        MorphFaceGame game,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = (Installed: store.GetFileFingerprint(game),
            Manual: store.GetManualFileFingerprint(game));
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
            {
                if (!IsCacheCurrent(game, cached, out var refreshed))
                    return Outdated(cached.Status);
                if (refreshed is not null) _cache[game] = refreshed;
                return new TextureCatalogReadResult(cached.Status, cached.Candidates, cached.MorphFaceTemplates)
                    { AttachmentMeshes = cached.AttachmentMeshes, OfficialPackagePaths = cached.OfficialPackagePaths };
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            fingerprint = (Installed: store.GetFileFingerprint(game),
                Manual: store.GetManualFileFingerprint(game));
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
                {
                    if (!IsCacheCurrent(game, cached, out var refreshed))
                        return Outdated(cached.Status);
                    if (refreshed is not null) _cache[game] = refreshed;
                    return new TextureCatalogReadResult(cached.Status, cached.Candidates, cached.MorphFaceTemplates)
                        { AttachmentMeshes = cached.AttachmentMeshes, OfficialPackagePaths = cached.OfficialPackagePaths };
                }
            }

            var stored = await Task.Run(() => store.ReadWithStatus(game), cancellationToken).ConfigureAwait(false);
            var status = stored.Status;
            if (stored.Snapshot is not { } snapshot)
                return new TextureCatalogReadResult(status, [], []);
            var layout = _captureLayout?.Invoke(game);
            var packageSetCurrent = layout is not null
                ? layout.PackagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(snapshot.MountedPackagePaths) &&
                  _snapshotMatchesLayout(game, snapshot, layout)
                : _mountedPackagesCurrent(game, snapshot.MountedPackagePaths);
            if (!packageSetCurrent || snapshot.MountedPackages.Any(file => !file.IsCurrent()))
                return Outdated(status);
            var manual = store.ReadManualWithStatus(game);
            if (manual.Status.State == TextureRegistryState.Ready && manual.Snapshot is not null)
                snapshot = TextureRegistryManualAssetService.Combine(snapshot, manual.Snapshot);
            else if (manual.Status.State is not TextureRegistryState.Missing)
                status = status with
                {
                    ErrorMessage = $"Custom asset database is {manual.Status.State}: {manual.Status.ErrorMessage}"
                };
            snapshot = ProjectOfficialDefaultEligibility(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            fingerprint = (Installed: store.GetFileFingerprint(game),
                Manual: store.GetManualFileFingerprint(game));
            lock (_cacheLock) _cache[game] = new CachedCatalog(
                fingerprint, status, snapshot.Candidates, snapshot.MorphFaceTemplates,
                snapshot.AttachmentMeshes, snapshot.OfficialPackagePaths, snapshot.MountedPackagePaths,
                snapshot.MountedPackages, layout);
            return new TextureCatalogReadResult(status, snapshot.Candidates, snapshot.MorphFaceTemplates)
                { AttachmentMeshes = snapshot.AttachmentMeshes, OfficialPackagePaths = snapshot.OfficialPackagePaths };
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

    private static TextureCatalogReadResult Outdated(TextureRegistryStatus status) =>
        new(status with { State = TextureRegistryState.Outdated,
            ErrorMessage = "The mounted game packages have changed. Rebuild this game's asset database." }, [], []);

    private bool IsCacheCurrent(MorphFaceGame game, CachedCatalog cached, out CachedCatalog? refreshed)
    {
        refreshed = null;
        if (cached.MountedPackages.Any(file => !file.IsCurrent())) return false;
        if (cached.Layout is null)
            return _mountedPackagesCurrent(game, cached.MountedPackagePaths);
        if (!_mountConfigurationCurrent(game, cached.Layout)) return false;
        if (cached.Layout.DirectoryStampsAreCurrent()) return true;

        // A directory timestamp change may be unrelated to PCC membership. Re-enumerate
        // only on that slow path, then keep the cache warm if the mounted package set and
        // all relevant priorities still match.
        var current = _captureLayout?.Invoke(game);
        if (current is null) return false;
        if (!cached.Layout.HasSameMountConfiguration(current) ||
            !cached.Layout.HasSamePackageSet(current) ||
            !current.PackagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(cached.MountedPackagePaths) ||
            cached.MountedPackages.Any(file => !file.IsCurrent()))
            return false;
        refreshed = cached with { Layout = current };
        return true;
    }

    private static bool ArePhysicalPackagesCurrent(MorphFaceGame game, IReadOnlyList<string> paths) =>
        paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(TextureRegistryBuilder.GetPhysicalPackagePaths(game));

    private static TextureRegistrySnapshot ProjectOfficialDefaultEligibility(TextureRegistrySnapshot snapshot)
    {
        var officialPackages = snapshot.OfficialPackagePaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = snapshot.Candidates.Select(candidate => candidate with
        {
            IsEligibleOfficialDefault =
                (candidate.EffectiveOccurrence.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc) &&
                officialPackages.Contains(Path.GetFullPath(candidate.EffectiveOccurrence.PackagePath))
        }).ToArray();
        var attachmentMeshes = snapshot.AttachmentMeshes.Select(candidate => candidate with
        {
            IsEligibleOfficialDefault =
                (candidate.EffectiveOccurrence.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc) &&
                officialPackages.Contains(Path.GetFullPath(candidate.EffectiveOccurrence.PackagePath))
        }).ToArray();
        return snapshot with { Candidates = candidates, AttachmentMeshes = attachmentMeshes };
    }

    private sealed record CachedCatalog(
        ((string Path, long Length, DateTime LastWriteTimeUtc)? Installed,
            (string Path, long Length, DateTime LastWriteTimeUtc)? Manual) Fingerprint,
        TextureRegistryStatus Status,
        IReadOnlyList<TextureCatalogCandidate> Candidates,
        IReadOnlyList<MorphFaceTemplateCandidate> MorphFaceTemplates,
        IReadOnlyList<AttachmentMeshCandidate> AttachmentMeshes,
        IReadOnlyList<string> OfficialPackagePaths,
        IReadOnlyList<string> MountedPackagePaths,
        IReadOnlyList<MountedPackageFingerprint> MountedPackages,
        TextureRegistryBuilder.PhysicalPackageLayoutSnapshot? Layout);
}
