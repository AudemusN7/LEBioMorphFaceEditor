using System.Text.RegularExpressions;

namespace MorphFaceEditor.Localisation;

public static partial class SourceReferenceIndex
{
    public static IReadOnlyDictionary<string, string> Build(string repositoryRoot, IEnumerable<string> keys)
    {
        var references = keys.ToDictionary(key => key, _ => new List<string>(), StringComparer.Ordinal);
        var source = Path.Combine(repositoryRoot, "src");
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(path =>
                     Path.GetExtension(path) is ".cs" or ".xaml" &&
                     !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj" or "Localisation")))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                foreach (var key in KeyReferences().Matches(line).Select(match => match.Value).Distinct(StringComparer.Ordinal))
                    if (references.TryGetValue(key, out var list))
                        list.Add($"{Path.GetRelativePath(repositoryRoot, path)}:{lineNumber}");
            }
        }
        return references.ToDictionary(pair => pair.Key,
            pair => pair.Value.Count == 0 ? "No literal source references found. This key may be selected dynamically." : string.Join(Environment.NewLine, pair.Value),
            StringComparer.Ordinal);
    }

    public static string? FindRepository(string startingDirectory)
    {
        for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MorphFaceEditor.slnx"))) return directory.FullName;
        return null;
    }

    [GeneratedRegex(@"loc_[A-Za-z0-9_]+", RegexOptions.CultureInvariant)] private static partial Regex KeyReferences();
}
