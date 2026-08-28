using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Builds MFE-owned v2 databases one game at a time, preserving the last usable file on every failure path.</summary>
public sealed class ObjectDatabaseBuilder
{
    private readonly ObjectDatabasePaths _paths;
    private readonly ObjectDatabaseProvider _provider;
    private readonly IObjectDatabaseGenerator _generator;
    private readonly Func<MEGame, IReadOnlyList<string>> _loadedFiles;

    public ObjectDatabaseBuilder(ObjectDatabasePaths paths, ObjectDatabaseProvider provider)
        : this(paths, provider, new LecObjectDatabaseGenerator(), GetLoadedFiles)
    {
    }

    internal ObjectDatabaseBuilder(
        ObjectDatabasePaths paths,
        ObjectDatabaseProvider provider,
        IObjectDatabaseGenerator generator,
        Func<MEGame, IReadOnlyList<string>> loadedFiles)
    {
        _paths = paths;
        _provider = provider;
        _generator = generator;
        _loadedFiles = loadedFiles;
    }

    public Task<ObjectDatabaseStatus> RebuildAsync(
        MorphFaceGame game,
        IProgress<ObjectDatabaseProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Rebuild(game, progress, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<ObjectDatabaseStatus>> RebuildAllAsync(
        IProgress<ObjectDatabaseProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var statuses = new List<ObjectDatabaseStatus>(3);
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses.Add(await RebuildAsync(game, progress, cancellationToken).ConfigureAwait(false));
        }

        return statuses;
    }

    private ObjectDatabaseStatus Rebuild(
        MorphFaceGame game,
        IProgress<ObjectDatabaseProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LegendaryExplorerCoreRuntime.Initialize();
        var meGame = ToMeGame(game);
        var files = _loadedFiles(meGame);
        var totalPackages = files.Count;
        var targetPath = _paths.GetPath(game, ObjectDatabaseSource.MorphFaceEditor);
        var temporaryPath = $"{targetPath}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        try
        {
            var database = _generator.Create(
                meGame,
                files,
                packagesProcessed =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new ObjectDatabaseProgress(game, packagesProcessed, totalPackages));
                },
                additionalPackages => totalPackages += additionalPackages);
            cancellationToken.ThrowIfCancellationRequested();

            using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                database.Serialize(output);
                output.Flush(flushToDisk: true);
            }

            using (var verification = File.OpenRead(temporaryPath))
            {
                var rebuilt = ObjectInstanceDB.Deserialize(meGame, verification);
                if (rebuilt.Version != 2)
                {
                    throw new InvalidDataException("The rebuilt ObjectInstanceDB was not schema v2.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
            return _provider.GetStatus(game);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static IReadOnlyList<string> GetLoadedFiles(MEGame game)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        return MELoadedFiles.GetFilesLoadedInGame(game).Values.ToArray();
    }

    private static MEGame ToMeGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => MEGame.LE1,
        MorphFaceGame.LE2 => MEGame.LE2,
        MorphFaceGame.LE3 => MEGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Only Legendary Edition games have an ObjectInstanceDB.")
    };
}

/// <summary>Separates the static LEC build API from deterministic unit tests.</summary>
internal interface IObjectDatabaseGenerator
{
    ObjectInstanceDB Create(
        MEGame game,
        IReadOnlyList<string> files,
        Action<int> packageProcessed,
        Action<int> discoveredPackages);
}

internal sealed class LecObjectDatabaseGenerator : IObjectDatabaseGenerator
{
    public ObjectInstanceDB Create(
        MEGame game,
        IReadOnlyList<string> files,
        Action<int> packageProcessed,
        Action<int> discoveredPackages) =>
        ObjectInstanceDB.Create(game, files.ToList(), packageProcessed, discoveredPackages);
}
