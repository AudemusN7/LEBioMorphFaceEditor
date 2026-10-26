using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class LoggingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("workspace cleanup preserves active leases, unowned files and legacy ownership", WorkspaceCleanup),
        new("log retention deletes only expired owned logs", Retention)
    ];

    private static void WorkspaceCleanup() => WithScratch(root =>
    {
        var source = Write(root, "source.pcc");
        var orphan = Write(root, "old.workspace.pcc");
        var neighbour = Write(root, "keep.pcc");
        var released = Write(root, "released.workspace.pcc");
        Write(root, "released.workspace.pcc.lease");
        string working;
        using (var workspace = new MorphFacePackageWorkspace(source, canCommit: false, root))
        {
            working = workspace.WorkingPath;
            TestAssert.Equal(0, MorphFacePackageWorkspace.CleanupOrphanedFiles(root, preserveUnleased: true).Count);
            TestAssert.True(File.Exists(orphan) && !File.Exists(released),
                "Legacy fallback removed an unleased workspace or retained a released orphan.");
            TestAssert.Equal(0, MorphFacePackageWorkspace.CleanupOrphanedFiles(root).Count);
            TestAssert.True(!File.Exists(orphan) && File.Exists(working) && File.Exists(working + ".lease") &&
                            File.Exists(neighbour), "Cleanup removed an active workspace or an unrelated file.");
        }
        TestAssert.True(!File.Exists(working) && !File.Exists(working + ".lease"),
            "Disposal did not release workspace files.");
        TestAssert.Equal("test content", File.ReadAllText(source));
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
