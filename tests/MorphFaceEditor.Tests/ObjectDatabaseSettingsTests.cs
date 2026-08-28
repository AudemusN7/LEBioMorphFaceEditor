using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.ViewModels;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace MorphFaceEditor.Tests;

/// <summary>Exercises the detached settings state without requiring a WPF dispatcher.</summary>
public static class ObjectDatabaseSettingsTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("object database settings: ready MFE row exposes provenance", ReadyMfeRowExposesProvenance),
        new("object database settings: status uses traffic-light colours", StatusUsesTrafficLightColours),
        new("object database settings: rebuild actions become cancel actions while active", RebuildActionsBecomeCancelActions),
        new("object database settings: rebuilding one game refreshes only its row", RebuildingOneGameRefreshesItsRow),
        new("object database settings: rebuilding all refreshes every row", RebuildingAllRefreshesEveryRow),
        new("object database settings: completed rebuild invalidates that catalogue", CompletedRebuildInvalidatesCatalogue)
    ];

    private static void ReadyMfeRowExposesProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-ObjectDatabaseSettings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new ObjectDatabasePaths(Path.Combine(root, "Mfe"), Path.Combine(root, "Shared"));
            WriteEmptyV2(paths.GetPath(MorphFaceGame.LE3, ObjectDatabaseSource.MorphFaceEditor));
            var provider = new ObjectDatabaseProvider(paths);
            var settings = new ObjectDatabaseSettingsViewModel(
                provider,
                new ObjectDatabaseBuilder(paths, provider));

            var row = settings.Rows.Single(value => value.Game == MorphFaceGame.LE3);

            TestAssert.Equal("Ready", row.StatusLabel);
            TestAssert.Equal("MFE", row.SourceLabel);
            TestAssert.Equal("v2", row.SchemaLabel);
            TestAssert.True(!string.IsNullOrWhiteSpace(row.FileSizeLabel),
                "The ready settings row did not expose the database file size.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void WriteEmptyV2(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x1552D027u);
        writer.Write(2u);
        writer.Write(0);
        writer.Write(0);
    }

    private static void StatusUsesTrafficLightColours()
    {
        var ready = new ObjectDatabaseSettingsRowViewModel(
            MorphFaceGame.LE1,
            new ObjectDatabaseStatus(MorphFaceGame.LE1, ObjectDatabaseState.Ready,
                ObjectDatabaseSource.LegendaryExplorer, 2, "fixture.bin", 1, DateTimeOffset.UtcNow));
        var missing = new ObjectDatabaseSettingsRowViewModel(
            MorphFaceGame.LE2,
            new ObjectDatabaseStatus(MorphFaceGame.LE2, ObjectDatabaseState.Missing,
                null, null, null, null, null));

        TestAssert.Equal("#63C174", ready.StatusColour);
        TestAssert.Equal("#E36D6D", missing.StatusColour);
    }

    private static void RebuildActionsBecomeCancelActions()
    {
        var paths = new ObjectDatabasePaths(Path.GetTempPath(), Path.GetTempPath());
        var provider = new ObjectDatabaseProvider(paths);
        var settings = new ObjectDatabaseSettingsViewModel(provider, new ObjectDatabaseBuilder(paths, provider));

        TestAssert.Equal("Rebuild", settings.ActionLabel);
        settings.BeginBuild();
        TestAssert.Equal("Cancel", settings.ActionLabel);
    }

    private static void RebuildingOneGameRefreshesItsRow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-ObjectDatabaseSettings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new ObjectDatabasePaths(Path.Combine(root, "Mfe"), Path.Combine(root, "Shared"));
            var provider = new ObjectDatabaseProvider(paths);
            var builder = new ObjectDatabaseBuilder(
                paths,
                provider,
                new FakeGenerator(CreateDatabase(MEGame.LE2)),
                _ => ["fixture.pcc"]);
            var settings = new ObjectDatabaseSettingsViewModel(provider, builder);

            settings.RebuildAsync(MorphFaceGame.LE2).GetAwaiter().GetResult();

            var rebuilt = settings.Rows.Single(row => row.Game == MorphFaceGame.LE2);
            var untouched = settings.Rows.Single(row => row.Game == MorphFaceGame.LE1);
            TestAssert.Equal("Ready", rebuilt.StatusLabel);
            TestAssert.Equal("MFE", rebuilt.SourceLabel);
            TestAssert.Equal("Missing", untouched.StatusLabel);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void RebuildingAllRefreshesEveryRow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-ObjectDatabaseSettings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new ObjectDatabasePaths(Path.Combine(root, "Mfe"), Path.Combine(root, "Shared"));
            var provider = new ObjectDatabaseProvider(paths);
            var builder = new ObjectDatabaseBuilder(paths, provider, new FakeGenerator(CreateDatabase(MEGame.LE1)), _ => ["fixture.pcc"]);
            var settings = new ObjectDatabaseSettingsViewModel(provider, builder);

            settings.RebuildAllAsync().GetAwaiter().GetResult();

            TestAssert.True(settings.Rows.All(row => row.StatusLabel == "Ready" && row.SourceLabel == "MFE"),
                "Rebuild all did not refresh every game to its MFE-owned database status.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void CompletedRebuildInvalidatesCatalogue()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-ObjectDatabaseSettings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new ObjectDatabasePaths(Path.Combine(root, "Mfe"), Path.Combine(root, "Shared"));
            var provider = new ObjectDatabaseProvider(paths);
            var invalidated = new List<MorphFaceGame>();
            var settings = new ObjectDatabaseSettingsViewModel(
                provider,
                new ObjectDatabaseBuilder(paths, provider, new FakeGenerator(CreateDatabase(MEGame.LE1)), _ => ["fixture.pcc"]),
                invalidated.Add);

            settings.RebuildAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();

            TestAssert.True(invalidated.SequenceEqual([MorphFaceGame.LE1]),
                "A completed rebuild did not invalidate the compact catalogue for the rebuilt game.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static ObjectInstanceDB CreateDatabase(MEGame game)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(0x1552D027u);
        writer.Write(2u);
        writer.Write(0);
        writer.Write(0);
        stream.Position = 0;
        return ObjectInstanceDB.Deserialize(game, stream);
    }

    private sealed class FakeGenerator(ObjectInstanceDB database) : IObjectDatabaseGenerator
    {
        public ObjectInstanceDB Create(
            MEGame game,
            IReadOnlyList<string> files,
            Action<int> packageProcessed,
            Action<int> discoveredPackages)
        {
            packageProcessed(1);
            return database;
        }
    }
}
