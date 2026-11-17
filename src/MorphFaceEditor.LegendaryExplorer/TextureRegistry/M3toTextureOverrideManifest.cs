using System.Text.Json;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

internal static class M3toTextureOverrideManifest
{
    internal static IReadOnlySet<string> ReadTexturePaths(MorphFaceGame game, string packagePath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(Path.GetFullPath(packagePath))!;
        var packageName = Path.GetFileName(packagePath);

        try
        {
            foreach (var manifestPath in Directory.EnumerateFiles(directory, "*.m3to"))
            {
                try
                {
                    using var stream = File.OpenRead(manifestPath);
                    using var document = JsonDocument.Parse(stream);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("game", out var manifestGame) ||
                        manifestGame.ValueKind != JsonValueKind.String ||
                        !string.Equals(manifestGame.GetString(), game.ToString(), StringComparison.Ordinal) ||
                        !root.TryGetProperty("textures", out var textures) ||
                        textures.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var texture in textures.EnumerateArray())
                    {
                        if (texture.ValueKind != JsonValueKind.Object ||
                            !texture.TryGetProperty("sourcepackage", out var source) ||
                            source.ValueKind != JsonValueKind.String ||
                            !texture.TryGetProperty("textureifp", out var texturePath) ||
                            texturePath.ValueKind != JsonValueKind.String)
                            continue;

                        var sourceName = source.GetString();
                        var path = texturePath.GetString();
                        if (string.IsNullOrWhiteSpace(sourceName) ||
                            sourceName.IndexOfAny(['/', '\\', ':']) >= 0 ||
                            !string.Equals(sourceName, packageName, StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrWhiteSpace(path))
                            continue;

                        paths.Add(path);
                    }
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    // An unreadable or invalid manifest cannot establish an override.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The package directory may disappear or become inaccessible during a scan.
        }

        return paths;
    }
}
