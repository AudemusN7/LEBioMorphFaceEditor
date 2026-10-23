using System.Text.Json;
using System.Text.Json.Nodes;

namespace MorphFaceEditor.QA;

internal static class QaOutputFiles
{
    // Retain one directory of uniquely named companion files so flattening the run is safe.
    public static IReadOnlyDictionary<string, string> NamespaceMeshExport(QaContext c, string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(Path.GetFullPath).Order().ToArray();
        var names = files.ToDictionary(path => path,
            path => Path.Combine(directory, c.UniqueName(Path.GetFileName(path))), StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Where(path => path.EndsWith(".mfe.json", StringComparison.OrdinalIgnoreCase)))
            if (names.TryGetValue(path[..^".mfe.json".Length], out var mesh)) names[path] = mesh + ".mfe.json";
        foreach (var path in files.Where(path => Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase)))
        {
            var document = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("Exported glTF is empty.");
            foreach (var arrayName in new[] { "buffers", "images" })
                if (document[arrayName] is JsonArray array)
                    foreach (var item in array.OfType<JsonObject>())
                        if (item["uri"]?.GetValue<string>() is { } uri && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            var old = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, Uri.UnescapeDataString(uri)));
                            QaContext.Require(names.TryGetValue(old, out var companion), "glTF companion was not retained: " + uri);
                            item["uri"] = Uri.EscapeDataString(Path.GetFileName(companion!));
                        }
            File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var (old, renamed) in names) File.Move(old, renamed);
        return names;
    }

    public static string FindResults(string directory)
    {
        var legacy = Path.Combine(directory, "results.json");
        if (File.Exists(legacy)) return legacy;
        var results = Directory.GetFiles(directory, "qa-*-results.json");
        QaContext.Require(results.Length == 1, "Audit directory must contain exactly one run results file.");
        return results[0];
    }

    public static IReadOnlyList<QaResult> ReadArtifactOwners(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindResults(directory)));
        return document.RootElement.GetProperty("Results").EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("Id").GetString()!;
            var result = new QaResult(id, id);
            result.Artifacts.AddRange(item.GetProperty("Artifacts").EnumerateArray()
                .Select(artifact => artifact.GetString()!));
            return result;
        }).ToArray();
    }
}
