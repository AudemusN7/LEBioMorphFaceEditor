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

/// <summary>Builds one compact registry directly from a single traversal of effective packages.</summary>
public sealed class TextureRegistryBuilder
{
    private readonly TextureRegistryStore _store;
    private readonly ITextureRegistryPackageScanner _scanner;
    private readonly Func<MorphFaceGame, IReadOnlyList<string>> _loadedFiles;

    public TextureRegistryBuilder(TextureRegistryStore store)
        : this(store, new LecTextureRegistryPackageScanner(), GetLoadedFiles)
    {
    }

    internal TextureRegistryBuilder(
        TextureRegistryStore store,
        ITextureRegistryPackageScanner scanner,
        Func<MorphFaceGame, IReadOnlyList<string>> loadedFiles)
    {
        _store = store;
        _scanner = scanner;
        _loadedFiles = loadedFiles;
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
        var files = _loadedFiles(game);
        var occurrencesByPath = new Dictionary<string, List<TextureCatalogOccurrence>>(
            StringComparer.OrdinalIgnoreCase);
        progress?.Report(new TextureRegistryBuildProgress(
            game, TextureRegistryBuildPhase.ScanningPackages, 0, files.Count, null, 0));

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packagePath = files[index];
            var textures = _scanner.Scan(game, packagePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var texture in textures)
            {
                if (!occurrencesByPath.TryGetValue(texture.InstancedPath, out var occurrences))
                {
                    occurrencesByPath.Add(texture.InstancedPath, occurrences = []);
                }
                occurrences.Add(texture.Occurrence);
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
            candidates);

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

    private static TextureCatalogGame ToCatalogGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => TextureCatalogGame.LE1,
        MorphFaceGame.LE2 => TextureCatalogGame.LE2,
        MorphFaceGame.LE3 => TextureCatalogGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game,
            "Texture registries are available only for Legendary Edition games.")
    };
}
