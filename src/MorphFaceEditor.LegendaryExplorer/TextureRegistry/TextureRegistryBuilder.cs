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

        var catalogGame = ToCatalogGame(game);
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
                return new TextureCatalogCandidate(catalogGame, pair.Key,
                    officialWinnerOccurrence ?? occurrences[0], occurrences)
                {
                    IsEligibleOfficialDefault = officialWinnerOccurrence is not null
                };
            })
            .OrderBy(value => value.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var snapshot = new TextureRegistrySnapshot(
            TextureRegistrySnapshot.CurrentSchemaVersion,
            catalogGame,
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
        _store.WriteAtomic(snapshot, cancellationToken, () =>
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

    internal static IReadOnlyList<string> GetPhysicalPackagePaths(MorphFaceGame game)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game);
        return string.IsNullOrWhiteSpace(cookedPath) ? [] : GetPhysicalPackagePaths(game, cookedPath);
    }

    /// <summary>
    /// Captures the installed package set and its directory-entry stamps. Runtime catalog reads
    /// can validate the stamps cheaply and only enumerate PCC files again after a directory or
    /// mount-layout change.
    /// </summary>
    internal static PhysicalPackageLayoutSnapshot CapturePhysicalPackageLayout(MorphFaceGame game)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var meGame = LecTextureRegistryPackageScanner.ToMeGame(game);
        var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game);
        if (string.IsNullOrWhiteSpace(cookedPath) || !Directory.Exists(cookedPath))
            return new PhysicalPackageLayoutSnapshot(string.Empty, [], [], [], []);

        cookedPath = Path.GetFullPath(cookedPath);
        var gameRoot = Directory.GetParent(cookedPath)?.Parent?.FullName;
        var enabledDlc = gameRoot is null
            ? []
            : MELoadedDLC.GetEnabledDLCFolders(meGame, gameRoot)
                .Where(Directory.Exists).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var roots = new[] { cookedPath }
            .Concat(enabledDlc.Select(path => Path.Combine(path, "CookedPCConsole")))
            .Where(Directory.Exists).ToArray();
        var packagePaths = new List<string>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            directories.Add(Path.GetFullPath(root));
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                directories.Add(Path.GetFullPath(directory));
            foreach (var packagePath in Directory.EnumerateFiles(root, "*.pcc", SearchOption.AllDirectories))
            {
                if (meGame == MEGame.LE3 && packagePath.EndsWith(
                        Path.Combine("DLC_METR_Patch01", "CookedPCConsole", "Startup.pcc"),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                packagePaths.Add(Path.GetFullPath(packagePath));
            }
        }

        var mounts = enabledDlc.Select(directory => new DlcMountFingerprint(
            Path.GetFullPath(directory),
            MELoadedDLC.GetMountPriority(directory, meGame),
            IsOfficialPath(Path.Combine(directory, "CookedPCConsole", "Mount.dlc"), meGame)))
            .OrderBy(value => value.DlcDirectory, StringComparer.OrdinalIgnoreCase).ToArray();
        var directoryStamps = directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new PackageDirectoryFingerprint(path, Directory.GetLastWriteTimeUtc(path)))
            .ToArray();
        return new PhysicalPackageLayoutSnapshot(cookedPath, enabledDlc,
            packagePaths.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => IsOfficialPath(path, meGame) ? 0 : 1)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            directoryStamps, mounts);
    }

    internal static bool IsCurrentMountConfiguration(MorphFaceGame game, PhysicalPackageLayoutSnapshot layout)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var meGame = LecTextureRegistryPackageScanner.ToMeGame(game);
        var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game);
        if (string.IsNullOrWhiteSpace(cookedPath) ||
            !Path.GetFullPath(cookedPath).Equals(layout.CookedPath, StringComparison.OrdinalIgnoreCase))
            return false;
        var gameRoot = Directory.GetParent(cookedPath)?.Parent?.FullName;
        var enabledDlc = gameRoot is null
            ? []
            : MELoadedDLC.GetEnabledDLCFolders(meGame, gameRoot)
                .Where(Directory.Exists).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!enabledDlc.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(layout.EnabledDlcDirectories))
            return false;
        var current = enabledDlc.ToDictionary(path => path, path => new DlcMountFingerprint(
            path, MELoadedDLC.GetMountPriority(path, meGame),
            IsOfficialPath(Path.Combine(path, "CookedPCConsole", "Mount.dlc"), meGame)),
            StringComparer.OrdinalIgnoreCase);
        return layout.Mounts.All(mount => current.TryGetValue(mount.DlcDirectory, out var value) &&
            value.MountPriority == mount.MountPriority && value.IsOfficial == mount.IsOfficial);
    }

    internal static bool SnapshotMountPrioritiesMatch(
        TextureRegistrySnapshot snapshot, PhysicalPackageLayoutSnapshot layout, MorphFaceGame game)
    {
        var mounts = layout.Mounts.ToDictionary(value => value.DlcDirectory,
            StringComparer.OrdinalIgnoreCase);
        foreach (var asset in snapshot.Assets)
        {
            var dlc = LecTextureRegistryPackageScanner.FindDlcDirectory(asset.PackagePath);
            DlcMountFingerprint? mount = null;
            if (dlc is not null)
                mounts.TryGetValue(Path.GetFullPath(dlc), out mount);
            var priority = mount?.MountPriority ?? 0;
            var origin = mount is null
                ? TextureCatalogOrigin.BaseGame
                : mount.IsOfficial ? TextureCatalogOrigin.OfficialDlc : TextureCatalogOrigin.Mod;
            if (priority != asset.MountPriority || asset.Origin != origin) return false;
        }
        var winners = SelectOfficialFilenameWinners(layout.PackagePaths, game)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return winners.SetEquals(snapshot.OfficialPackagePaths);
    }

    internal sealed record PhysicalPackageLayoutSnapshot(
        string CookedPath,
        IReadOnlyList<string> EnabledDlcDirectories,
        IReadOnlyList<string> PackagePaths,
        IReadOnlyList<PackageDirectoryFingerprint> Directories,
        IReadOnlyList<DlcMountFingerprint> Mounts)
    {
        public bool DirectoryStampsAreCurrent() => Directories.All(directory => directory.IsCurrent());

        public bool HasSamePackageSet(PhysicalPackageLayoutSnapshot other) =>
            PackagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(other.PackagePaths);

        public bool HasSameMountConfiguration(PhysicalPackageLayoutSnapshot other) =>
            CookedPath.Equals(other.CookedPath, StringComparison.OrdinalIgnoreCase) &&
            EnabledDlcDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(other.EnabledDlcDirectories) &&
            Mounts.ToDictionary(value => value.DlcDirectory, StringComparer.OrdinalIgnoreCase)
                .All(pair => other.Mounts.Any(value => value.DlcDirectory.Equals(pair.Key,
                    StringComparison.OrdinalIgnoreCase) && value.MountPriority == pair.Value.MountPriority &&
                    value.IsOfficial == pair.Value.IsOfficial));
    }

    internal sealed record PackageDirectoryFingerprint(string DirectoryPath, DateTime LastWriteTimeUtc)
    {
        public bool IsCurrent() => Directory.Exists(DirectoryPath) &&
            Directory.GetLastWriteTimeUtc(DirectoryPath) == LastWriteTimeUtc;
    }

    internal sealed record DlcMountFingerprint(string DlcDirectory, int MountPriority, bool IsOfficial);

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

    private static TextureCatalogGame ToCatalogGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => TextureCatalogGame.LE1,
        MorphFaceGame.LE2 => TextureCatalogGame.LE2,
        MorphFaceGame.LE3 => TextureCatalogGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game,
            "Texture registries are available only for Legendary Edition games.")
    };
}
