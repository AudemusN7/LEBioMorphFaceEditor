using MorphFaceEditor.Core.Domain;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

public enum TextureRegistryBuildPhase
{
    ScanningPackages,
    WritingRegistry,
    VerifyingRegistry,
    Ready
}

public sealed record TextureRegistryBuildProgress(
    MorphFaceGame Game,
    TextureRegistryBuildPhase Phase,
    int PackagesProcessed,
    int TotalPackages,
    string? CurrentPackageName,
    int TextureCount);

public interface ITextureRegistryBuilder
{
    Task<TextureRegistryStatus> RebuildAsync(
        MorphFaceGame game,
        IProgress<TextureRegistryBuildProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TextureRegistryStatus>> RebuildAllAsync(
        IProgress<TextureRegistryBuildProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Builds one compact registry from one traversal of all physical game packages.</summary>
public sealed class TextureRegistryBuilder : ITextureRegistryBuilder
{
    private readonly TextureRegistryStore _store;
    private readonly ITextureRegistryPackageScanner _scanner;
    private readonly Func<MorphFaceGame, IReadOnlyList<string>> _physicalPackagePaths;

    public TextureRegistryBuilder(TextureRegistryStore store)
        : this(store, new LecTextureRegistryPackageScanner(), GetPhysicalPackagePaths)
    {
    }

    internal TextureRegistryBuilder(
        TextureRegistryStore store,
        ITextureRegistryPackageScanner scanner,
        Func<MorphFaceGame, IReadOnlyList<string>> physicalPackagePaths)
    {
        _store = store;
        _scanner = scanner;
        _physicalPackagePaths = physicalPackagePaths;
    }

    public Task<TextureRegistryStatus> RebuildAsync(
        MorphFaceGame game,
        IProgress<TextureRegistryBuildProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Rebuild(game, progress, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<TextureRegistryStatus>> RebuildAllAsync(
        IProgress<TextureRegistryBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var statuses = new List<TextureRegistryStatus>(3);
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses.Add(await RebuildAsync(game, progress, cancellationToken).ConfigureAwait(false));
        }
        return statuses;
    }

    private TextureRegistryStatus Rebuild(
        MorphFaceGame game,
        IProgress<TextureRegistryBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = MergeScanPaths(_physicalPackagePaths(game));
        var officialPackagePaths = SelectOfficialFilenameWinners(files, game);
        var officialPaths = officialPackagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fingerprints = files.Where(File.Exists).Select(MountedPackageFingerprint.Read).ToArray();
        var occurrencesByPath = new Dictionary<string, List<TextureCatalogOccurrence>>(
            StringComparer.OrdinalIgnoreCase);
        var morphFaceTemplates = new List<MorphFaceTemplateCandidate>();
        var meshesByPath = new Dictionary<string, List<AttachmentMeshOccurrence>>(
            StringComparer.OrdinalIgnoreCase);
        var assets = new List<InstalledAssetOccurrence>();
        progress?.Report(new TextureRegistryBuildProgress(
            game, TextureRegistryBuildPhase.ScanningPackages, 0, files.Count, null, 0));

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packagePath = files[index];
            var scan = _scanner.Scan(game, packagePath, cancellationToken);
            assets.AddRange(scan.Assets);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var texture in scan.Textures)
            {
                var canonicalPath = texture.CanonicalPath ?? PccAssetPathPolicy.FromDonorOccurrence(texture.InstancedPath, texture.Occurrence.PackagePath);
                if (!occurrencesByPath.TryGetValue(canonicalPath, out var occurrences))
                {
                    occurrencesByPath.Add(canonicalPath, occurrences = []);
                }
                occurrences.Add(texture.Occurrence);
            }
            if (officialPaths.Contains(Path.GetFullPath(packagePath)))
                morphFaceTemplates.AddRange(scan.MorphFaceTemplates);
            foreach (var mesh in scan.AttachmentMeshes)
            {
                var canonicalPath = PccAssetPathPolicy.FromDonorOccurrence(
                    mesh.InstancedPath, mesh.PackagePath);
                if (!meshesByPath.TryGetValue(canonicalPath, out var occurrences))
                    meshesByPath.Add(canonicalPath, occurrences = []);
                occurrences.Add(mesh);
            }
            progress?.Report(new TextureRegistryBuildProgress(
                game, TextureRegistryBuildPhase.ScanningPackages, index + 1, files.Count,
                Path.GetFileName(packagePath), occurrencesByPath.Count));
        }

        var candidates = occurrencesByPath
            .Select(pair =>
            {
                var occurrences = pair.Value
                    .OrderBy(value => SourceRank(value.Origin))
                    .ThenByDescending(value => value.MountPriority)
                    .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var officialWinnerOccurrence = occurrences
                    .Where(value => officialPaths.Contains(Path.GetFullPath(value.PackagePath)))
                    .OrderByDescending(value => value.MountPriority)
                    .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                return new TextureCatalogCandidate(game, pair.Key,
                    officialWinnerOccurrence ?? occurrences[0], occurrences)
                {
                    IsEligibleOfficialDefault = officialWinnerOccurrence is not null
                };
            })
            .OrderBy(value => value.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var snapshot = new TextureRegistrySnapshot(
            TextureRegistrySnapshot.CurrentSchemaVersion,
            game,
            DateTimeOffset.UtcNow,
            files.Count,
            candidates)
        {
            Assets = assets.DistinctBy(value => $"{value.ClassName}|{value.InstancedPath}|{value.PackagePath}|{value.ExportUIndex}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value.InstancedPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.ClassName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => SourceRank(value.Origin))
                .ThenByDescending(value => value.MountPriority)
                .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            OfficialPackagePaths = officialPackagePaths,
            MountedPackagePaths = files.Select(Path.GetFullPath).ToArray(),
            MountedPackages = fingerprints,
            AttachmentMeshes = meshesByPath.Select(pair =>
                {
                    var occurrences = pair.Value
                        .OrderBy(value => SourceRank(value.Origin))
                        .ThenByDescending(value => value.MountPriority)
                        .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var officialWinnerOccurrence = occurrences
                        .Where(value => officialPaths.Contains(Path.GetFullPath(value.PackagePath)))
                        .OrderByDescending(value => value.MountPriority)
                        .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    return new AttachmentMeshCandidate(pair.Key,
                        officialWinnerOccurrence ?? occurrences[0], occurrences)
                    {
                        IsEligibleOfficialDefault = officialWinnerOccurrence is not null
                    };
                })
                .OrderBy(value => value.CanonicalPath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            MorphFaceTemplates = morphFaceTemplates
                .OrderBy(value => SourceRank(value.Origin))
                .ThenByDescending(value => value.MountPriority)
                .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.FacePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new TextureRegistryBuildProgress(
            game, TextureRegistryBuildPhase.WritingRegistry, files.Count, files.Count, null, candidates.Length));
        var officialIdentities = snapshot.Assets
            .Where(value => value.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
            .Select(value => AssetIdentity(value.InstancedPath, value.ClassName))
            .Concat(snapshot.Candidates.Where(candidate => candidate.Occurrences.Any(value =>
                    value.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc))
                .Select(value => AssetIdentity(value.InstancedPath, "Texture2D")))
            .Concat(snapshot.AttachmentMeshes.SelectMany(value => value.Occurrences)
                .Where(value => value.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
                .Select(value => AssetIdentity(value.InstancedPath, "SkeletalMesh")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = FilterSnapshot(snapshot, value => value.Origin is
            TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc,
            officialIdentities, includeModOnly: false);
        var mods = FilterSnapshot(snapshot, value => value.Origin == TextureCatalogOrigin.Mod,
            officialIdentities, includeModOnly: true);

        _store.WriteInstalledAndModsAtomic(installed, mods, cancellationToken, () =>
        {
            if (fingerprints.Any(source => !source.IsCurrent()) ||
                !MergeScanPaths(_physicalPackagePaths(game)).SequenceEqual(files, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("An installed package changed during the registry scan. Rebuild after package changes finish.");
            progress?.Report(new TextureRegistryBuildProgress(
                game, TextureRegistryBuildPhase.VerifyingRegistry,
                files.Count, files.Count, null, candidates.Length));
        });
        progress?.Report(new TextureRegistryBuildProgress(
            game, TextureRegistryBuildPhase.Ready, files.Count, files.Count, null, candidates.Length));
        return _store.GetStatus(game);
    }

    private static TextureRegistrySnapshot FilterSnapshot(
        TextureRegistrySnapshot source,
        Func<TextureCatalogOccurrence, bool> includeTexture,
        HashSet<string> officialIdentities,
        bool includeModOnly)
    {
        var candidates = source.Candidates.Select(candidate =>
        {
            var occurrences = candidate.Occurrences.Where(includeTexture).ToArray();
            return occurrences.Length == 0 ? null : candidate with
            {
                EffectiveOccurrence = !includeModOnly && occurrences.Contains(candidate.EffectiveOccurrence)
                    ? candidate.EffectiveOccurrence : ChooseTextureOccurrence(occurrences),
                Occurrences = occurrences,
                IsEligibleOfficialDefault = includeModOnly ? false : candidate.IsEligibleOfficialDefault
            };
        }).Where(value => value is not null).Cast<TextureCatalogCandidate>()
          .Where(candidate => !includeModOnly || !officialIdentities.Contains(AssetIdentity(candidate.InstancedPath, "Texture2D")))
          .ToArray();

        var meshes = source.AttachmentMeshes.Select(candidate =>
        {
            var occurrences = candidate.Occurrences.Where(value => includeModOnly
                ? value.Origin == TextureCatalogOrigin.Mod
                : value.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc).ToArray();
            return occurrences.Length == 0 ? null : candidate with
            {
                EffectiveOccurrence = !includeModOnly && occurrences.Contains(candidate.EffectiveOccurrence)
                    ? candidate.EffectiveOccurrence : ChooseMeshOccurrence(occurrences),
                Occurrences = occurrences,
                IsEligibleOfficialDefault = includeModOnly ? false : candidate.IsEligibleOfficialDefault
            };
        }).Where(value => value is not null).Cast<AttachmentMeshCandidate>()
          .Where(candidate => !includeModOnly || !officialIdentities.Contains(AssetIdentity(candidate.EffectiveOccurrence.InstancedPath, "SkeletalMesh")))
          .ToArray();

        var assets = source.Assets.Where(asset => includeModOnly
                ? asset.Origin == TextureCatalogOrigin.Mod && !officialIdentities.Contains(AssetIdentity(asset.InstancedPath, asset.ClassName))
                : (asset.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc) &&
                  source.OfficialPackagePaths.Contains(Path.GetFullPath(asset.PackagePath), StringComparer.OrdinalIgnoreCase))
            .GroupBy(asset => AssetIdentity(asset.InstancedPath, asset.ClassName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(asset => asset.MountPriority)
                .ThenBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(asset => asset.ExportUIndex).First())
            .ToArray();
        var game = source.Game switch
        {
            MorphFaceGame.LE1 => MEGame.LE1,
            MorphFaceGame.LE2 => MEGame.LE2,
            MorphFaceGame.LE3 => MEGame.LE3,
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        var packagePaths = source.MountedPackagePaths.Where(path => includeModOnly
                ? LecTextureRegistryPackageScanner.GetOrigin(path, game) == TextureCatalogOrigin.Mod
                : LecTextureRegistryPackageScanner.GetOrigin(path, game) != TextureCatalogOrigin.Mod)
            .ToArray();
        var includedPackages = packagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return source with
        {
            InstalledPackageCount = packagePaths.Length,
            MountedPackagePaths = packagePaths,
            MountedPackages = source.MountedPackages.Where(value => includedPackages.Contains(value.PackagePath)).ToArray(),
            OfficialPackagePaths = includeModOnly ? [] : source.OfficialPackagePaths,
            Candidates = candidates,
            AttachmentMeshes = meshes,
            Assets = assets,
            MorphFaceTemplates = includeModOnly ? [] : source.MorphFaceTemplates
        };
    }

    private static string AssetIdentity(string path, string className) => $"{className}|{path}";

    private static TextureCatalogOccurrence ChooseTextureOccurrence(IReadOnlyList<TextureCatalogOccurrence> occurrences) =>
        occurrences.OrderByDescending(value => value.MountPriority)
            .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase).First();

    private static AttachmentMeshOccurrence ChooseMeshOccurrence(IReadOnlyList<AttachmentMeshOccurrence> occurrences) =>
        occurrences.OrderByDescending(value => value.MountPriority)
            .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase).First();

    internal static IReadOnlyList<string> GetPhysicalPackagePaths(MorphFaceGame game)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game);
        return string.IsNullOrWhiteSpace(cookedPath) ? [] : GetPhysicalPackagePaths(game, cookedPath);
    }

    internal static IReadOnlyList<string> GetPhysicalPackagePaths(
        MorphFaceGame game,
        string cookedPath,
        IEnumerable<string>? enabledDlcDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(cookedPath) || !Directory.Exists(cookedPath)) return [];
        var meGame = LecTextureRegistryPackageScanner.ToMeGame(game);
        var gameRoot = Directory.GetParent(cookedPath)?.Parent?.FullName;
        var roots = new List<string> { cookedPath };
        if (gameRoot is not null)
        {
            roots.AddRange((enabledDlcDirectories ?? MELoadedDLC.GetEnabledDLCFolders(meGame, gameRoot))
                .Select(dlc => Path.Combine(dlc, "CookedPCConsole")));
        }
        return roots.Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.pcc", SearchOption.AllDirectories))
            .Where(path => !(meGame == MEGame.LE3 && path.EndsWith(
                Path.Combine("DLC_METR_Patch01", "CookedPCConsole", "Startup.pcc"), StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => IsOfficialPath(path, meGame) ? 0 : 1)
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> SelectOfficialFilenameWinners(
        IEnumerable<string> packagePaths, MorphFaceGame game)
    {
        ArgumentNullException.ThrowIfNull(packagePaths);
        var meGame = LecTextureRegistryPackageScanner.ToMeGame(game);
        return packagePaths.Where(path => IsOfficialPath(path, meGame))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(path => LecTextureRegistryPackageScanner.GetMountPriority(path, meGame))
                .ThenBy(path => Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase).First())
            .Select(Path.GetFullPath)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> MergeScanPaths(IEnumerable<string> packagePaths)
    {
        ArgumentNullException.ThrowIfNull(packagePaths);

        var paths = new List<string>();
        var seenAbsolutePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in packagePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var absolutePath = Path.GetFullPath(path);
            if (seenAbsolutePaths.Add(absolutePath))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static bool IsOfficialPath(string path, MEGame game) =>
        LecTextureRegistryPackageScanner.GetOrigin(path, game) is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc;

    private static int SourceRank(TextureCatalogOrigin origin) => origin switch
    {
        TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc => 0,
        TextureCatalogOrigin.Manual => 1,
        _ => 2
    };

}
