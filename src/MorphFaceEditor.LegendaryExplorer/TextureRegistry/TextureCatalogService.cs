using System.Runtime.CompilerServices;
using MorphFaceEditor.Core.Domain;
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

/// <summary>Reuses the registry until its files change. Assets are checked when requested.</summary>
public sealed class TextureCatalogService(TextureRegistryStore store)
{
    private readonly TextureRegistryStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<MorphFaceGame, CachedCatalog> _cache = [];
    private static readonly ConditionalWeakTable<TextureRegistrySnapshot, TextureRegistrySnapshot> Projections = new();

    public async Task<TextureCatalogReadResult> ReadAsync(
        MorphFaceGame game, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = GetFingerprint(game);
        lock (_cacheLock)
            if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
                return cached.Result;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            fingerprint = GetFingerprint(game);
            lock (_cacheLock)
                if (_cache.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint)
                    return cached.Result;

            var result = await Task.Run(() => Read(game), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // A concurrent rebuild must not label an older snapshot with the new file stamps.
            if (fingerprint == GetFingerprint(game))
                lock (_cacheLock) _cache[game] = new(fingerprint, result);
            return result;
        }
        finally { _gate.Release(); }
    }

    private TextureCatalogReadResult Read(MorphFaceGame game)
    {
        var stored = _store.ReadWithStatus(game);
        var status = stored.Status;
        if (stored.Snapshot is not { } snapshot) return new(status, [], []);
        snapshot = IncludeIndexedHatMeshes(snapshot);
        foreach (var sidecar in new[] { _store.ReadModsWithStatus(game), _store.ReadManualWithStatus(game) })
        {
            if (sidecar.Status.State == TextureRegistryState.Ready && sidecar.Snapshot is { } additional)
                snapshot = TextureRegistryManualAssetService.Combine(snapshot, additional);
            else if (sidecar.Status.State != TextureRegistryState.Missing)
                status = status with { ErrorMessage = string.Join(" ", new[] { status.ErrorMessage,
                    $"Asset sidecar is {sidecar.Status.State}: {sidecar.Status.ErrorMessage}" }.Where(value => value is not null)) };
        }
        snapshot = Projections.GetValue(snapshot, ProjectOfficialDefaultEligibility);
        return new(status, snapshot.Candidates, snapshot.MorphFaceTemplates)
        { AttachmentMeshes = snapshot.AttachmentMeshes, OfficialPackagePaths = snapshot.OfficialPackagePaths };
    }

    public void Invalidate(MorphFaceGame game)
    {
        lock (_cacheLock) _cache.Remove(game);
    }

    private RegistryFingerprint GetFingerprint(MorphFaceGame game) => new(
        _store.GetFileFingerprint(game), _store.GetModsFileFingerprint(game), _store.GetManualFileFingerprint(game));

    private static TextureRegistrySnapshot ProjectOfficialDefaultEligibility(TextureRegistrySnapshot snapshot)
    {
        var officialPackages = snapshot.OfficialPackagePaths.Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot with
        {
            Candidates = snapshot.Candidates.Select(candidate => candidate with
            {
                IsEligibleOfficialDefault =
                    (candidate.EffectiveOccurrence.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc) &&
                    officialPackages.Contains(Path.GetFullPath(candidate.EffectiveOccurrence.PackagePath))
            }).ToArray(),
            AttachmentMeshes = snapshot.AttachmentMeshes.Select(candidate => candidate with
            {
                IsEligibleOfficialDefault =
                    AttachmentMeshNamePolicy.IsInstalledHeadAttachment(candidate.CanonicalPath) &&
                    (candidate.EffectiveOccurrence.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc) &&
                    officialPackages.Contains(Path.GetFullPath(candidate.EffectiveOccurrence.PackagePath))
            }).ToArray()
        };
    }

    private static TextureRegistrySnapshot IncludeIndexedHatMeshes(TextureRegistrySnapshot snapshot)
    {
        var officialPackages = snapshot.OfficialPackagePaths.Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = snapshot.AttachmentMeshes.Select(candidate => candidate.CanonicalPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = snapshot.Assets.Where(asset =>
                asset.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                (asset.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc) &&
                officialPackages.Contains(Path.GetFullPath(asset.PackagePath)) &&
                asset.InstancedPath.Split('.').Last().Contains("HAT", StringComparison.OrdinalIgnoreCase) &&
                AttachmentMeshNamePolicy.IsInstalledHeadAttachment(asset.InstancedPath) &&
                !known.Contains(asset.InstancedPath))
            .GroupBy(asset => asset.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(asset => asset.MountPriority)
                .ThenBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(asset => asset.ExportUIndex).First())
            .Select(asset =>
            {
                var occurrence = new AttachmentMeshOccurrence(asset.PackagePath, asset.InstancedPath,
                    asset.ExportUIndex, asset.MountPriority, asset.Origin);
                return new AttachmentMeshCandidate(asset.InstancedPath, occurrence, [occurrence]);
            }).ToArray();
        return additions.Length == 0 ? snapshot : snapshot with
        {
            AttachmentMeshes = snapshot.AttachmentMeshes.Concat(additions)
                .OrderBy(candidate => candidate.CanonicalPath, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private readonly record struct RegistryFingerprint(
        (string Path, long Length, DateTime LastWriteTimeUtc)? Installed,
        (string Path, long Length, DateTime LastWriteTimeUtc)? Mods,
        (string Path, long Length, DateTime LastWriteTimeUtc)? Manual);
    private sealed record CachedCatalog(RegistryFingerprint Fingerprint, TextureCatalogReadResult Result);
}
