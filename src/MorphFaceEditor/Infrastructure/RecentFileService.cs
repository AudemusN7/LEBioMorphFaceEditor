using System.IO;
using System.Text.Json;

namespace MorphFaceEditor.Infrastructure;

public sealed class RecentFileService
{
    internal const int MaximumFiles = 10;
    private readonly string? _storagePath;
    private readonly List<string> _paths;

    internal RecentFileService(string? storagePath = null)
    {
        _storagePath = storagePath;
        _paths = Load(storagePath);
    }

    internal static RecentFileService CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LE BioMorphFace Editor",
        "recent-files.json"));

    internal IReadOnlyList<string> Paths => _paths;

    internal void Add(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _paths.RemoveAll(candidate => string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase));
        _paths.Insert(0, fullPath);
        if (_paths.Count > MaximumFiles)
        {
            _paths.RemoveRange(MaximumFiles, _paths.Count - MaximumFiles);
        }
        Save();
    }

    internal void Remove(string path)
    {
        if (_paths.RemoveAll(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
        }
    }

    private static List<string> Load(string? storagePath)
    {
        if (storagePath is null || !File.Exists(storagePath))
        {
            return [];
        }
        try
        {
            return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(storagePath)) ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumFiles)
                .ToList();
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Recent-file history could not be read: {exception.Message}");
            return [];
        }
    }

    private void Save()
    {
        if (_storagePath is null)
        {
            return;
        }
        try
        {
            var directory = Path.GetDirectoryName(_storagePath)
                ?? throw new InvalidOperationException("The recent-file path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _storagePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_paths));
            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Recent-file history could not be saved: {exception.Message}");
        }
    }
}
