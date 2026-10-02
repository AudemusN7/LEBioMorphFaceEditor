using System.IO;

namespace MorphFaceEditor.Infrastructure;

public sealed class RecentFileService
{
    internal const int MaximumFiles = 10;
    private readonly ApplicationPreferencesStore? _store;
    private readonly List<string> _paths;
    internal RecentFileService(string? storagePath = null) : this(storagePath is null ? null : new ApplicationPreferencesStore(storagePath)) { }
    internal RecentFileService(ApplicationPreferencesStore? store)
    {
        _store = store;
        _paths = (store?.Current.RecentFiles ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumFiles).ToList();
    }
    internal static RecentFileService CreateDefault() => new(ApplicationPreferencesStore.CreateDefault());
    internal IReadOnlyList<string> Paths => _paths;
    internal void Add(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _paths.RemoveAll(candidate => string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase));
        _paths.Insert(0, fullPath);
        if (_paths.Count > MaximumFiles) _paths.RemoveRange(MaximumFiles, _paths.Count - MaximumFiles);
        Save();
    }
    internal void Remove(string path)
    {
        if (_paths.RemoveAll(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)) > 0) Save();
    }
    private void Save()
    {
        if (_store is null) return;
        try { _store.Update(p => p with { RecentFiles = _paths.ToArray() }); }
        catch (Exception exception) { AppLog.Warning($"Recent-file history could not be saved: {exception.Message}"); }
    }
}
