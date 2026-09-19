using LegendaryExplorerCore.GameFilesystem;
using MorphFaceEditor.Core.Materials;

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

/// <summary>Builds one compact registry directly from a single traversal of effective packages.</summary>
public sealed class TextureRegistryBuilder : ITextureRegistryBuilder
{
    private readonly TextureRegistryStore _store;
    private readonly ITextureRegistryPackageScanner _scanner;
    private readonly Func<MorphFaceGame, IReadOnlyList<string>> _loadedFiles;
    private readonly Func<MorphFaceGame, string?> _cookedPath;

    public TextureRegistryBuilder(TextureRegistryStore store)
        : this(store, new LecTextureRegistryPackageScanner(), GetLoadedFiles,
            LegendaryExplorerCoreRuntime.GetCookedPath)
    {
    }

    internal TextureRegistryBuilder(
        TextureRegistryStore store,
        ITextureRegistryPackageScanner scanner,
        Func<MorphFaceGame, IReadOnlyList<string>> loadedFiles)
        : this(store, scanner, loadedFiles, _ => null)
    {
    }

    internal TextureRegistryBuilder(
        TextureRegistryStore store,
        ITextureRegistryPackageScanner scanner,
        Func<MorphFaceGame, IReadOnlyList<string>> loadedFiles,
        Func<MorphFaceGame, string?> cookedPath)
    {
        _store = store;
        _scanner = scanner;
        _loadedFiles = loadedFiles;
        _cookedPath = cookedPath;
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
        var effectiveFiles = _loadedFiles(game);
        var files = MergeScanPaths(effectiveFiles, _cookedPath(game));
        var occurrencesByPath = new Dictionary<string, List<TextureCatalogOccurrence>>(
            StringComparer.OrdinalIgnoreCase);
        var morphFaceTemplates = new List<MorphFaceTemplateCandidate>();
        progress?.Report(new TextureRegistryBuildProgress(
            game, TextureRegistryBuildPhase.ScanningPackages, 0, files.Count, null, 0));

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packagePath = files[index];
            var scan = _scanner.Scan(game, packagePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var texture in scan.Textures)
            {
                if (!occurrencesByPath.TryGetValue(texture.InstancedPath, out var occurrences))
                {
                    occurrencesByPath.Add(texture.InstancedPath, occurrences = []);
                }
                occurrences.Add(texture.Occurrence);
            }
            morphFaceTemplates.AddRange(scan.MorphFaceTemplates);
            progress?.Report(new TextureRegistryBuildProgress(
                game, TextureRegistryBuildPhase.ScanningPackages, index + 1, files.Count,
                Path.GetFileName(packagePath), occurrencesByPath.Count));
        }

        var catalogGame = ToCatalogGame(game);
        var candidates = occurrencesByPath
            .Select(pair =>
            {
                var occurrences = pair.Value
                    .OrderByDescending(value => value.MountPriority)
                    .ThenByDescending(value => value.Origin)
                    .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new TextureCatalogCandidate(catalogGame, pair.Key, occurrences[0], occurrences);
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
            MorphFaceTemplates = morphFaceTemplates
                .OrderBy(value => value.Origin)
                .ThenByDescending(value => value.MountPriority)
                .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.FacePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new TextureRegistryBuildProgress(
            game, TextureRegistryBuildPhase.WritingRegistry, files.Count, files.Count, null, candidates.Length));
        _store.WriteAtomic(snapshot, cancellationToken, () =>
            progress?.Report(new TextureRegistryBuildProgress(
                game, TextureRegistryBuildPhase.VerifyingRegistry,
                files.Count, files.Count, null, candidates.Length)));
        progress?.Report(new TextureRegistryBuildProgress(
            game, TextureRegistryBuildPhase.Ready, files.Count, files.Count, null, candidates.Length));
        return _store.GetStatus(game);
    }

    private static IReadOnlyList<string> GetLoadedFiles(MorphFaceGame game)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        return MELoadedFiles.GetFilesLoadedInGame(LecTextureRegistryPackageScanner.ToMeGame(game)).Values.ToArray();
    }

    /// <summary>
    /// Adds only physical base-game packages shadowed by an effective DLC package.
    /// The effective file list remains first so its occurrence wins mount precedence;
    /// the base package is a secondary source for native morph-face templates.
    /// </summary>
    internal static IReadOnlyList<string> MergeScanPaths(
        IEnumerable<string> effectiveFiles,
        string? cookedPath)
    {
        ArgumentNullException.ThrowIfNull(effectiveFiles);

        var paths = new List<string>();
        var seenAbsolutePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in effectiveFiles)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var absolutePath = Path.GetFullPath(path);
            if (seenAbsolutePaths.Add(absolutePath))
            {
                // Preserve the loaded-map spelling for effective paths. In production
                // these are absolute; preserving it also keeps injected scanners useful.
                paths.Add(path);
            }
        }

        if (string.IsNullOrWhiteSpace(cookedPath))
        {
            return paths;
        }

        var absoluteCookedPath = Path.GetFullPath(cookedPath);
        var effectiveCount = paths.Count;
        for (var index = 0; index < effectiveCount; index++)
        {
            var effectivePath = Path.GetFullPath(paths[index]);
            var dlcCookedPath = FindDlcCookedPath(effectivePath);
            if (dlcCookedPath is null)
            {
                continue;
            }

            var fileName = Path.GetFileName(effectivePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(dlcCookedPath, effectivePath);
            foreach (var basePath in new[]
                     {
                         Path.Combine(absoluteCookedPath, relativePath),
                         Path.Combine(absoluteCookedPath, fileName)
                     }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(basePath) || !seenAbsolutePaths.Add(basePath))
                {
                    continue;
                }

                paths.Add(basePath);
            }
        }

        return paths;
    }

    private static string? FindDlcCookedPath(string path)
    {
        var directory = new FileInfo(path).Directory;
        while (directory is not null &&
               !directory.Name.Equals("CookedPCConsole", StringComparison.OrdinalIgnoreCase))
        {
            directory = directory.Parent;
        }
        var dlcDirectory = directory?.Parent;
        return dlcDirectory is not null &&
               dlcDirectory.Name.StartsWith("DLC_", StringComparison.OrdinalIgnoreCase) &&
               dlcDirectory.Parent?.Name.Equals("DLC", StringComparison.OrdinalIgnoreCase) == true
            ? directory!.FullName
            : null;
    }

    private static TextureCatalogGame ToCatalogGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => TextureCatalogGame.LE1,
        MorphFaceGame.LE2 => TextureCatalogGame.LE2,
        MorphFaceGame.LE3 => TextureCatalogGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game,
            "Texture registries are available only for Legendary Edition games.")
    };
}
