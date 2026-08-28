using System.IO;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Owns deterministic ObjectInstanceDB locations while keeping MFE and LEX files separate.</summary>
public sealed class ObjectDatabasePaths(string mfeDirectory, string legendaryExplorerDirectory)
{
    private readonly string _mfeDirectory = Path.GetFullPath(mfeDirectory);
    private readonly string _legendaryExplorerDirectory = Path.GetFullPath(legendaryExplorerDirectory);

    public static ObjectDatabasePaths CreateDefault() => new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BioMorphFaceEditor",
            "ObjectDatabases"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LegendaryExplorer",
            "ObjectDatabases"));

    public string GetPath(MorphFaceGame game, ObjectDatabaseSource source) => Path.Combine(
        source == ObjectDatabaseSource.MorphFaceEditor ? _mfeDirectory : _legendaryExplorerDirectory,
        GetFileName(game));

    private static string GetFileName(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => "LE1.bin",
        MorphFaceGame.LE2 => "LE2.bin",
        MorphFaceGame.LE3 => "LE3.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Only Legendary Edition games have an ObjectInstanceDB.")
    };
}
