namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Owns deterministic per-game paths for MFE's compact registries.</summary>
public sealed class TextureRegistryPaths(string directory)
{
    private readonly string _directory = Path.GetFullPath(directory);

    public static TextureRegistryPaths CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BioMorphFaceEditor",
        "TextureRegistries"));

    public string GetPath(MorphFaceGame game) => Path.Combine(_directory, game switch
    {
        MorphFaceGame.LE1 => "LE1.mftr",
        MorphFaceGame.LE2 => "LE2.mftr",
        MorphFaceGame.LE3 => "LE3.mftr",
        _ => throw new ArgumentOutOfRangeException(nameof(game), game,
            "Texture registries are available only for Legendary Edition games.")
    });
}
