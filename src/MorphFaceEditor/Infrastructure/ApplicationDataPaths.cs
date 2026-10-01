using System.IO;

namespace MorphFaceEditor.Infrastructure;

/// <summary>Only these two application-owned directories are eligible for a reset.</summary>
internal sealed class ApplicationDataPaths(string localAppData)
{
    public string CurrentDirectory { get; } = Path.Combine(Path.GetFullPath(localAppData), "LE BioMorphFace Editor");
    public string LegacyDirectory { get; } = Path.Combine(Path.GetFullPath(localAppData), "BioMorphFaceEditor");
    public static ApplicationDataPaths CreateDefault() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public IReadOnlyList<string> Clear()
    {
        var failures = new List<string>();
        foreach (var directory in new[] { CurrentDirectory, LegacyDirectory })
        {
            try
            {
                if (Directory.Exists(directory)) DeleteWithoutFollowingLinks(directory);
            }
            catch (Exception exception)
            {
                failures.Add($"{directory}: {exception.Message}");
            }
        }
        return failures;
    }

    private static void DeleteWithoutFollowingLinks(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                DeleteWithoutFollowingLinks(entry);
            else
                File.Delete(entry);
        }
        Directory.Delete(directory);
    }
}
