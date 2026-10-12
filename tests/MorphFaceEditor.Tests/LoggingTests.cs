using System.IO.Compression;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class LoggingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("logging: compact records, session numbering and midnight rollover", SessionsAndRollover),
        new("logging: concurrent sessions reserve distinct numbers", ConcurrentSessions),
        new("logging: verbose records are opt-in while warnings and errors remain", VerboseFiltering),
        new("logging: retention removes only owned logs older than seven days", Retention),
        new("logging: diagnostic ZIP includes today and the preceding two days", RecentArchive),
        new("logging: cleanup preserves live workspaces and deletes released orphans", WorkspaceCleanup),
        new("logging: workspace owner holds a lease until disposal", WorkspaceOwnerLease),
        new("logging: legacy workspace cleanup defers while another editor may own files", LegacyWorkspaceCleanup),
        new("logging: compact randomisation retains normal and cursed seeds", RandomisationSeeds),
        new("logging: QA suppression retains verbose diagnostics for observers", SuppressionObservers)
    ];

    private static void SessionsAndRollover() => WithScratch(root =>
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 34, 56, 789, TimeSpan.FromHours(1));
        var first = new AppLogFileStore(root, () => now);
        first.Append("INFO", "Application starting VERSION:test");
        first.Append("WARN", "TEST:WARNING");
        var second = new AppLogFileStore(root, () => now);
        second.Append("ERROR", "TEST:ERROR", new IOException("sample failure"));
        var text = File.ReadAllText(first.CurrentFilePath);
        TestAssert.True(text.StartsWith("SESSION 1\r\n2026-10-12 12:34:56.789 +01:00 [INF] Application starting", StringComparison.Ordinal), text);
        TestAssert.True(text.Contains("[WAR] TEST:WARNING") && text.Contains("[ERR] TEST:ERROR"), text);
        TestAssert.True(text.Contains("SESSION 2") && text.Contains("System.IO.IOException: sample failure"), text);
        TestAssert.Equal(2, File.ReadLines(first.CurrentFilePath).Count(line => line.StartsWith("SESSION ")));
        var oldFile = first.CurrentFilePath;
        now = now.AddDays(1);
        TestAssert.Equal(oldFile, first.LatestFilePath);
        first.Append("INFO", "TEST:NEW-DAY");
        TestAssert.Equal(first.CurrentFilePath, first.LatestFilePath);
        TestAssert.True(first.CurrentFilePath != oldFile, "Logger did not roll over at midnight.");
        TestAssert.True(File.ReadAllText(first.CurrentFilePath).StartsWith("SESSION 1"), "New daily file did not start a session.");
        TestAssert.True(!File.ReadAllText(oldFile).Contains("NEW-DAY"), "New-day record was appended to yesterday.");
    });

    private static void ConcurrentSessions() => WithScratch(root =>
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        Parallel.For(0, 8, index =>
        {
            var store = new AppLogFileStore(root, () => now);
            store.Append("INFO", $"TEST:{index}");
            store.Append("INFO", $"TEST:{index}:END");
        });
        var lines = File.ReadAllLines(new AppLogFileStore(root, () => now).CurrentFilePath);
        var sessions = lines.Where(line => line.StartsWith("SESSION ")).ToArray();
        TestAssert.Equal(8, sessions.Length);
        TestAssert.Equal(8, sessions.Distinct().Count());
        TestAssert.Equal(16, lines.Count(line => line.Contains("[INF] TEST:")));
    });

    private static void Retention() => WithScratch(root =>
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var expired = Write(root, "morph-face-editor-20261004.log");
        var boundary = Write(root, "morph-face-editor-20261005.log");
        var unknown = Write(root, "unrelated.log");
        var invalid = Write(root, "morph-face-editor-not-a-date.log");
        File.SetLastWriteTimeUtc(expired, now.UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(boundary, now.UtcDateTime.AddDays(-7));
        File.SetLastWriteTimeUtc(unknown, now.UtcDateTime.AddDays(-20));
        File.SetLastWriteTimeUtc(invalid, now.UtcDateTime.AddDays(-20));
        TestAssert.Equal(0, new AppLogFileStore(root, () => now).Prune().Count);
        TestAssert.True(!File.Exists(expired), "Expired application log survived.");
        TestAssert.True(File.Exists(boundary) && File.Exists(unknown) && File.Exists(invalid), "Retention deleted a boundary or unowned file.");
    });

    private static void VerboseFiltering() => WithScratch(root =>
    {
        var store = new AppLogFileStore(root);
        store.Append("INFO", "TEST:quiet-preview", verbose: true);
        TestAssert.True(!File.Exists(store.CurrentFilePath), "Suppressed preview created a log.");
        store.Append("INFO", "TEST:normal");
        store.Append("WARN", "TEST:warning");
        store.Append("ERROR", "TEST:error");
        store.VerboseLogging = true;
        store.Append("INFO", "TEST:enabled-preview", verbose: true);
        var text = File.ReadAllText(store.CurrentFilePath);
        TestAssert.True(!text.Contains("quiet-preview") && text.Contains("enabled-preview"), "Verbose mode was not opt-in.");
        TestAssert.True(text.Contains("normal") && text.Contains("[WAR] TEST:warning") && text.Contains("[ERR] TEST:error"), "Normal diagnostics were filtered out.");
    });

    private static void WorkspaceOwnerLease() => WithScratch(root =>
    {
        var source = Write(root, "source.pcc");
        string working;
        using (var workspace = new MorphFacePackageWorkspace(source, canCommit: false, root))
        {
            working = workspace.WorkingPath;
            TestAssert.True(File.Exists(working + ".lease"), "Workspace constructor did not create its lease.");
            TestAssert.Equal(0, MorphFacePackageWorkspace.CleanupOrphanedFiles(root).Count);
            TestAssert.True(File.Exists(working), "Cleanup deleted a workspace held by its owner.");
        }
        TestAssert.True(!File.Exists(working) && !File.Exists(working + ".lease"), "Disposal did not release workspace files.");
        TestAssert.Equal("test content", File.ReadAllText(source));
    });

    private static void RecentArchive() => WithScratch(root =>
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var current = Write(root, "morph-face-editor-20261012.log");
        Write(root, "morph-face-editor-20261011.log");
        Write(root, "morph-face-editor-20261010.log");
        Write(root, "morph-face-editor-20261009.log");
        Write(root, "morph-face-editor-20261013.log");
        var destination = Path.Combine(root, "report.zip");
        new DiagnosticArchiveService(new ApplicationDataPaths(root), current, now).Export(destination, []);
        using var zip = ZipFile.OpenRead(destination);
        var logs = zip.Entries.Where(entry => entry.FullName.StartsWith("Logs/")).ToArray();
        TestAssert.Equal(3, logs.Length);
        TestAssert.True(logs.All(entry => entry.Name is "morph-face-editor-20261010.log" or "morph-face-editor-20261011.log" or "morph-face-editor-20261012.log"), "Archive included an out-of-range log.");
    });

    private static void WorkspaceCleanup() => WithScratch(root =>
    {
        var orphan = Write(root, "old.workspace.pcc");
        var live = Write(root, "live.workspace.pcc");
        var neighbour = Write(root, "keep.pcc");
        Write(root, "empty.workspace.pcc.lease");
        using (var lease = new FileStream(live + ".lease", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            TestAssert.Equal(0, MorphFacePackageWorkspace.CleanupOrphanedFiles(root).Count);
            TestAssert.True(!File.Exists(orphan) && File.Exists(live) && File.Exists(neighbour), "Cleanup removed a live or unrelated package.");
            TestAssert.True(!File.Exists(Path.Combine(root, "empty.workspace.pcc.lease")), "Orphan lease survived.");
        }
        TestAssert.Equal(0, MorphFacePackageWorkspace.CleanupOrphanedFiles(root).Count);
        TestAssert.True(!File.Exists(live) && !File.Exists(live + ".lease"), "Released orphan survived cleanup.");
    });

    private static void LegacyWorkspaceCleanup() => WithScratch(root =>
    {
        var legacy = Write(root, "legacy.workspace.pcc");
        var released = Write(root, "released.workspace.pcc");
        Write(root, "released.workspace.pcc.lease");
        TestAssert.Equal(0, MorphFacePackageWorkspace.CleanupOrphanedFiles(root, preserveUnleased: true).Count);
        TestAssert.True(File.Exists(legacy) && !File.Exists(released), "Legacy ownership fallback did not preserve unleased workspace.");
    });

    private static void RandomisationSeeds()
    {
        TestAssert.Equal("RAND:NOR HMM.Test LE2 STR50/25 SEED12/34", AppLogMessages.Randomisation(false, "HMM.Test", "LE2", 50, 25, 12, 34));
        TestAssert.Equal("RAND:NOR HMM.Test LE2 STR50/25 SEED12", AppLogMessages.Randomisation(false, "HMM.Test", "LE2", 50, 25, 12, null));
        TestAssert.Equal("RAND:NOR HMM.Test LE2 STR50/25 SEED-/34", AppLogMessages.Randomisation(false, "HMM.Test", "LE2", 50, 25, null, 34));
        TestAssert.Equal("RAND:CUR HMM.Test LE2 STR50/25 SEED12", AppLogMessages.Randomisation(true, "HMM.Test", "LE2", 50, 25, 12, 12));
    }

    private static void SuppressionObservers()
    {
        var observed = new List<string>();
        void Observe(string level, string message) => observed.Add(level + " " + message);
        AppLog.MessageWritten += Observe;
        try
        {
            using (AppLog.SuppressPersistentWrites())
            using (AppLog.SuppressPersistentWrites())
            {
                AppLog.Verbose("TEST:unresolved optional asset");
                AppLog.Warning("TEST:warning");
            }
            TestAssert.Equal(2, observed.Count);
            TestAssert.Equal("INFO TEST:unresolved optional asset", observed[0]);
            TestAssert.Equal("WARN TEST:warning", observed[1]);
        }
        finally { AppLog.MessageWritten -= Observe; }
    }

    private static string Write(string root, string name)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, "test content");
        return path;
    }

    private static void WithScratch(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-LoggingTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
