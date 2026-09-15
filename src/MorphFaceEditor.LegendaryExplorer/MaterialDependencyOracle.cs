using System.Reflection;
using System.Text.Json;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

internal enum MaterialOracleEntryKind
{
    Unknown,
    Import,
    Export
}

/// <summary>
/// Compact runtime projection of the three native GlobalMorphs packages. The
/// fixtures are the authority for stock material/texture identity and whether
/// an identity is stored as an import or export. Player-only authored textures
/// are intentionally absent and remain an exact texture-database lookup.
/// </summary>
internal sealed class MaterialDependencyOracle
{
    private const string ResourceName = "MorphFaceEditor.MaterialDependencyOracle.json";
    private static readonly Lazy<MaterialDependencyOracle> Shared = new(Load);
    private readonly IReadOnlyDictionary<string, MaterialOracleEntryKind> _entries;

    private MaterialDependencyOracle(IReadOnlyDictionary<string, MaterialOracleEntryKind> entries)
    {
        _entries = entries;
    }

    internal static MaterialDependencyOracle Instance => Shared.Value;

    internal MaterialOracleEntryKind GetKind(MEGame game, string className, string path)
    {
        if (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(path))
        {
            return MaterialOracleEntryKind.Unknown;
        }
        return _entries.GetValueOrDefault(Key(GameName(game), className, path));
    }

    private static MaterialDependencyOracle Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidDataException(
                               $"Embedded material dependency oracle '{ResourceName}' is missing.");
        var document = JsonSerializer.Deserialize<OracleDocument>(stream)
                       ?? throw new InvalidDataException("The material dependency oracle is empty.");
        if (document.FormatVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported material dependency oracle version {document.FormatVersion}.");
        }

        var entries = new Dictionary<string, MaterialOracleEntryKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (game, gameDocument) in document.Games)
        {
            foreach (var entry in gameDocument.Entries)
            {
                var kind = entry.Kind.Equals("Export", StringComparison.OrdinalIgnoreCase)
                    ? MaterialOracleEntryKind.Export
                    : entry.Kind.Equals("Import", StringComparison.OrdinalIgnoreCase)
                        ? MaterialOracleEntryKind.Import
                        : throw new InvalidDataException(
                            $"Unknown oracle entry kind '{entry.Kind}' for '{entry.Path}'.");
                var key = Key(game, entry.ClassName, entry.Path);
                if (entries.TryGetValue(key, out var existing) && existing != kind)
                {
                    throw new InvalidDataException(
                        $"The material dependency oracle assigns conflicting roles to '{entry.Path}' ({entry.ClassName}).");
                }
                entries[key] = kind;
            }
        }
        return new MaterialDependencyOracle(entries);
    }

    private static string Key(string game, string className, string path) =>
        $"{game}|{className}|{path}";

    private static string GameName(MEGame game) => game switch
    {
        MEGame.LE1 => "LE1",
        MEGame.LE2 => "LE2",
        MEGame.LE3 => "LE3",
        _ => throw new NotSupportedException($"Material oracle does not support {game}.")
    };

    private sealed record OracleDocument(
        int FormatVersion,
        IReadOnlyDictionary<string, OracleGame> Games);

    private sealed record OracleGame(IReadOnlyList<OracleEntry> Entries);
    private sealed record OracleEntry(string ClassName, string Path, string Kind);
}
