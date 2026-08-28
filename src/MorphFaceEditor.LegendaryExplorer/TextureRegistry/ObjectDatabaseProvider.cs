using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Resolves a usable v2 ObjectInstanceDB without retaining it after the caller finishes catalog work.</summary>
public sealed class ObjectDatabaseProvider(ObjectDatabasePaths paths)
{
    public ObjectDatabaseStatus GetStatus(MorphFaceGame game)
    {
        if (TryDescribe(game, ObjectDatabaseSource.MorphFaceEditor, out var owned))
        {
            return owned;
        }

        if (TryDescribe(game, ObjectDatabaseSource.LegendaryExplorer, out var shared))
        {
            return shared;
        }

        return new ObjectDatabaseStatus(game, ObjectDatabaseState.Missing, null, null, null, null, null);
    }

    public bool TryOpenActive(MorphFaceGame game, out ObjectInstanceDB? database)
    {
        database = null;
        var status = GetStatus(game);
        if (status.State != ObjectDatabaseState.Ready || status.FilePath is null)
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(status.FilePath);
            var loaded = ObjectInstanceDB.Deserialize(ToMeGame(game), stream);
            if (loaded.Version != 2)
            {
                return false;
            }

            database = loaded;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryDescribe(
        MorphFaceGame game,
        ObjectDatabaseSource source,
        out ObjectDatabaseStatus status)
    {
        var path = paths.GetPath(game, source);
        status = default!;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var database = ObjectInstanceDB.Deserialize(ToMeGame(game), stream);
            if (database.Version != 2)
            {
                return false;
            }

            var file = new FileInfo(path);
            status = new ObjectDatabaseStatus(
                game,
                ObjectDatabaseState.Ready,
                source,
                database.Version,
                path,
                file.Length,
                file.LastWriteTimeUtc);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static MEGame ToMeGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => MEGame.LE1,
        MorphFaceGame.LE2 => MEGame.LE2,
        MorphFaceGame.LE3 => MEGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Only Legendary Edition games have an ObjectInstanceDB.")
    };
}
