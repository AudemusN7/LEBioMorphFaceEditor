using System.IO.Compression;
using System.Text.Json;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class ApplicationSettingsTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("diagnostics collect current workspace and registries without changing inputs", ArchiveCollectsFiles),
        new("failed diagnostics preserve an existing destination and remove the candidate", FailedArchivePreservesDestination),
        new("diagnostics cannot overwrite a collected input", ArchiveRejectsInputCollision),
        new("Clear Data deletes only application directories", ClearOnlyApplicationDirectories),
        new("Clear Data reports locked files and continues legacy cleanup", ClearReportsFailure),
        new("Clear Data does not follow directory links", ClearDoesNotFollowLinks)
    ];

    private static void ArchiveCollectsFiles() => WithScratch(root =>
    {
        var paths = new ApplicationDataPaths(root);
        var log = Write(Path.Combine(paths.CurrentDirectory, "Logs", "latest.log"), "latest log");
        Write(Path.Combine(paths.CurrentDirectory, "Logs", "old.log"), "old log");
        Write(Path.Combine(paths.CurrentDirectory, "TextureRegistries", "LE1.mftr"), "stock");
        Write(Path.Combine(paths.CurrentDirectory, "TextureRegistries", "LE2.manual.mftr"), "custom");
        var legacy = Write(Path.Combine(root, "BioMorphFaceEditor", "TextureRegistries", "LE3.mftr"), "legacy");
        Write(Path.Combine(paths.CurrentDirectory, "preferences.json"), "preferences");
        var workspace = Write(Path.Combine(root, "working.pcc"), "pending workspace");
        var destination = Path.Combine(root, "report.zip");
        var result = new DiagnosticArchiveService(paths, log).Export(destination, [workspace]);
        using var zip = ZipFile.OpenRead(destination);
        TestAssert.Equal(4, result.Entries.Count);
        TestAssert.Equal(5, zip.Entries.Count);
        TestAssert.Equal("latest log", Read(zip, "Logs/latest.log"));
        TestAssert.Equal("pending workspace", Read(zip, "Workspace/working.pcc"));
        TestAssert.Equal("stock", Read(zip, "TextureRegistries/LE1.mftr"));
        TestAssert.Equal("custom", Read(zip, "TextureRegistries/LE2.manual.mftr"));
        TestAssert.True(zip.GetEntry("LegacyTextureRegistries/LE3.mftr") is null, "An old registry was included.");
        TestAssert.Equal("legacy", File.ReadAllText(legacy));
        TestAssert.Equal("pending workspace", File.ReadAllText(workspace));
        using var manifest = JsonDocument.Parse(Read(zip, "Report.json"));
        TestAssert.True(manifest.RootElement.GetProperty("WorkspaceIncluded").GetBoolean(), "Workspace missing from manifest.");
    });

    private static void FailedArchivePreservesDestination() => WithScratch(root =>
    {
        var log = Write(Path.Combine(root, "latest.log"), "log");
        var destination = Write(Path.Combine(root, "report.zip"), "previous export");
        try
        {
            new DiagnosticArchiveService(new ApplicationDataPaths(root), log)
                .Export(destination, [Path.Combine(root, "missing.pcc")]);
            throw new Exception("Missing workspace unexpectedly exported.");
        }
        catch (FileNotFoundException) { }
        TestAssert.Equal("previous export", File.ReadAllText(destination));
        TestAssert.True(!Directory.EnumerateFiles(root, ".mfe-diagnostics-*.tmp").Any(), "Failed archive left a candidate.");
    });

    private static void ArchiveRejectsInputCollision() => WithScratch(root =>
    {
        var log = Write(Path.Combine(root, "latest.zip"), "log");
        try
        {
            new DiagnosticArchiveService(new ApplicationDataPaths(root), log).Export(log, []);
            throw new Exception("A collected input was overwritten.");
        }
        catch (IOException) { }
        TestAssert.Equal("log", File.ReadAllText(log));
    });

    private static void ClearOnlyApplicationDirectories() => WithScratch(root =>
    {
        var paths = new ApplicationDataPaths(root);
        Write(Path.Combine(paths.CurrentDirectory, "Workspaces", "temp.pcc"), "work");
        Write(Path.Combine(paths.LegacyDirectory, "TextureRegistries", "LE1.mftr"), "legacy");
        var neighbour = Write(Path.Combine(root, "LegendaryExplorer", "keep.txt"), "keep");
        TestAssert.Equal(0, paths.Clear().Count);
        TestAssert.True(!Directory.Exists(paths.CurrentDirectory) && !Directory.Exists(paths.LegacyDirectory), "MFE directories survived reset.");
        TestAssert.Equal("keep", File.ReadAllText(neighbour));
        TestAssert.Equal(0, paths.Clear().Count);
    });

    private static void ClearReportsFailure() => WithScratch(root =>
    {
        var paths = new ApplicationDataPaths(root);
        var locked = Write(Path.Combine(paths.CurrentDirectory, "locked.txt"), "keep");
        Write(Path.Combine(paths.LegacyDirectory, "old.txt"), "old");
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var failures = paths.Clear();
        TestAssert.Equal(1, failures.Count);
        TestAssert.True(failures[0].Contains(paths.CurrentDirectory), "Failed directory was not identified.");
        TestAssert.True(!Directory.Exists(paths.LegacyDirectory), "A failed current reset prevented legacy cleanup.");
    });

    private static void ClearDoesNotFollowLinks() => WithScratch(root =>
    {
        var paths = new ApplicationDataPaths(root);
        var outside = Path.Combine(root, "Outside");
        var preserved = Write(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateDirectory(paths.CurrentDirectory);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", Path.Combine(paths.CurrentDirectory, "link"), outside })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        TestAssert.True(process.ExitCode == 0, $"Could not create the test junction: {output} {error}");
        TestAssert.Equal(0, paths.Clear().Count);
        TestAssert.Equal("keep", File.ReadAllText(preserved));
    });

    private static string Read(ZipArchive zip, string entry)
    {
        using var reader = new StreamReader(zip.GetEntry(entry)!.Open());
        return reader.ReadToEnd();
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void WithScratch(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-SettingsTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

}
