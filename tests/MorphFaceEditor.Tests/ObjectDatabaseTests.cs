using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace MorphFaceEditor.Tests;

/// <summary>Proves the deterministic database-selection rules before catalog projection is added.</summary>
public static class ObjectDatabaseTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("object database: MFE v2 takes precedence", MfeDatabaseTakesPrecedence),
        new("object database: shared v2 is used when MFE is absent", SharedDatabaseIsUsed),
        new("object database: active v2 database can be opened", ActiveV2DatabaseCanBeOpened),
        new("object database: invalid and v1 files are rejected", InvalidAndV1DatabasesAreRejected),
        new("object database: cancelled rebuild preserves active MFE file", CancelledRebuildPreservesActiveMfeFile),
        new("object database: successful rebuild replaces the MFE file", SuccessfulRebuildReplacesTheMfeFile),
        new("object database: rebuild reports package progress", RebuildReportsPackageProgress),
        new("object database: rebuild all is sequential", RebuildAllIsSequential)
    ];

    private static void MfeDatabaseTakesPrecedence()
    {
        using var fixture = ObjectDatabaseFixture.Create();
        fixture.WriteDatabase(MorphFaceGame.LE1, ObjectDatabaseSource.LegendaryExplorer, version: 2);
        fixture.WriteDatabase(MorphFaceGame.LE1, ObjectDatabaseSource.MorphFaceEditor, version: 2);

        var status = fixture.Provider.GetStatus(MorphFaceGame.LE1);

        TestAssert.Equal(ObjectDatabaseState.Ready, status.State);
        TestAssert.Equal(ObjectDatabaseSource.MorphFaceEditor, status.Source);
        TestAssert.Equal(2, status.SchemaVersion);
    }

    private static void SharedDatabaseIsUsed()
    {
        using var fixture = ObjectDatabaseFixture.Create();
        fixture.WriteDatabase(MorphFaceGame.LE2, ObjectDatabaseSource.LegendaryExplorer, version: 2);

        var status = fixture.Provider.GetStatus(MorphFaceGame.LE2);

        TestAssert.Equal(ObjectDatabaseState.Ready, status.State);
        TestAssert.Equal(ObjectDatabaseSource.LegendaryExplorer, status.Source);
        TestAssert.Equal(2, status.SchemaVersion);
    }

    private static void ActiveV2DatabaseCanBeOpened()
    {
        using var fixture = ObjectDatabaseFixture.Create();
        fixture.WriteDatabase(MorphFaceGame.LE2, ObjectDatabaseSource.LegendaryExplorer, version: 2);

        var opened = fixture.Provider.TryOpenActive(MorphFaceGame.LE2, out var database);

        TestAssert.True(opened, "The selected v2 ObjectInstanceDB could not be reopened.");
        TestAssert.Equal(2, database!.Version);
    }

    private static void InvalidAndV1DatabasesAreRejected()
    {
        using var fixture = ObjectDatabaseFixture.Create();
        fixture.WriteDatabase(MorphFaceGame.LE3, ObjectDatabaseSource.MorphFaceEditor, version: 1);
        fixture.WriteInvalidDatabase(MorphFaceGame.LE3, ObjectDatabaseSource.LegendaryExplorer);

        var status = fixture.Provider.GetStatus(MorphFaceGame.LE3);

        TestAssert.Equal(ObjectDatabaseState.Missing, status.State);
        TestAssert.Equal<ObjectDatabaseSource?>(null, status.Source);
        TestAssert.Equal<int?>(null, status.SchemaVersion);
    }

    private static void CancelledRebuildPreservesActiveMfeFile()
    {
        using var fixture = ObjectDatabaseFixture.Create();
        fixture.WriteDatabase(MorphFaceGame.LE1, ObjectDatabaseSource.MorphFaceEditor, version: 2);
        var before = File.ReadAllBytes(fixture.Paths.GetPath(MorphFaceGame.LE1, ObjectDatabaseSource.MorphFaceEditor));
        using var cancellation = new CancellationTokenSource();
        var builder = fixture.CreateBuilder(new FakeObjectDatabaseGenerator(
            fixture.CreateDatabase(MEGame.LE1, "rebuilt.pcc"),
            beforeProgress: cancellation.Cancel));

        try
        {
            builder.RebuildAsync(MorphFaceGame.LE1, null, cancellation.Token).GetAwaiter().GetResult();
            throw new Exception("The cancelled rebuild completed successfully.");
        }
        catch (OperationCanceledException)
        {
            // Expected: LEC reaches the callback boundary before MFE replaces its existing file.
        }

        var after = File.ReadAllBytes(fixture.Paths.GetPath(MorphFaceGame.LE1, ObjectDatabaseSource.MorphFaceEditor));
        TestAssert.True(before.SequenceEqual(after), "Cancellation replaced the active MFE object database.");
    }

    private static void SuccessfulRebuildReplacesTheMfeFile()
    {
        using var fixture = ObjectDatabaseFixture.Create();
        fixture.WriteDatabase(MorphFaceGame.LE2, ObjectDatabaseSource.MorphFaceEditor, version: 2);
        var rebuilt = fixture.CreateDatabase(MEGame.LE2, "rebuilt.pcc");
        var expected = Serialize(rebuilt);
        var builder = fixture.CreateBuilder(new FakeObjectDatabaseGenerator(rebuilt));

        var status = builder.RebuildAsync(MorphFaceGame.LE2).GetAwaiter().GetResult();

        var path = fixture.Paths.GetPath(MorphFaceGame.LE2, ObjectDatabaseSource.MorphFaceEditor);
        TestAssert.Equal(ObjectDatabaseState.Ready, status.State);
        TestAssert.Equal(ObjectDatabaseSource.MorphFaceEditor, status.Source);
        TestAssert.True(expected.SequenceEqual(File.ReadAllBytes(path)),
            "The successful rebuild did not atomically install the generated v2 database.");
        TestAssert.True(!File.Exists($"{path}.tmp"), "The successful rebuild left its temporary database file behind.");
    }

    private static void RebuildAllIsSequential()
    {
        using var fixture = ObjectDatabaseFixture.Create();
        var generator = new FakeObjectDatabaseGenerator(fixture.CreateDatabase(MEGame.LE1, "rebuilt.pcc"));
        var builder = fixture.CreateBuilder(generator);

        _ = builder.RebuildAllAsync().GetAwaiter().GetResult();

        TestAssert.True(generator.Games.SequenceEqual([MEGame.LE1, MEGame.LE2, MEGame.LE3]),
            "Rebuild all did not process LE1, LE2, and LE3 sequentially.");
    }

    private static void RebuildReportsPackageProgress()
    {
        using var fixture = ObjectDatabaseFixture.Create();
        var progress = new CapturingProgress<ObjectDatabaseProgress>();
        var builder = fixture.CreateBuilder(new FakeObjectDatabaseGenerator(
            fixture.CreateDatabase(MEGame.LE3, "rebuilt.pcc")));

        _ = builder.RebuildAsync(MorphFaceGame.LE3, progress).GetAwaiter().GetResult();

        TestAssert.Equal(new ObjectDatabaseProgress(MorphFaceGame.LE3, 1, 1), progress.Values.Single());
    }

    private sealed class ObjectDatabaseFixture : IDisposable
    {
        private const uint Magic = 0x1552D027;
        private readonly string _root;

        private ObjectDatabaseFixture(string root)
        {
            _root = root;
            Paths = new ObjectDatabasePaths(
                Path.Combine(root, "Mfe"),
                Path.Combine(root, "Shared"));
            Provider = new ObjectDatabaseProvider(Paths);
        }

        public ObjectDatabasePaths Paths { get; }
        public ObjectDatabaseProvider Provider { get; }

        public static ObjectDatabaseFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"MFE-ObjectDatabase-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new ObjectDatabaseFixture(root);
        }

        public void WriteDatabase(MorphFaceGame game, ObjectDatabaseSource source, uint version)
        {
            var path = Paths.GetPath(game, source);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic);
            writer.Write(version);
            writer.Write(0);
            writer.Write(0);
        }

        public void WriteInvalidDatabase(MorphFaceGame game, ObjectDatabaseSource source)
        {
            var path = Paths.GetPath(game, source);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0, 1, 2, 3]);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        public ObjectDatabaseBuilder CreateBuilder(IObjectDatabaseGenerator generator) => new(
            Paths,
            Provider,
            generator,
            _ => ["fixture.pcc"]);

        public ObjectInstanceDB CreateDatabase(MEGame game, string filePath)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(2u);
            writer.Write(1);
            var bytes = System.Text.Encoding.UTF8.GetBytes(filePath);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Write(0);
            stream.Position = 0;
            return ObjectInstanceDB.Deserialize(game, stream);
        }
    }

    private sealed class FakeObjectDatabaseGenerator(
        ObjectInstanceDB database,
        Action? beforeProgress = null) : IObjectDatabaseGenerator
    {
        public List<MEGame> Games { get; } = [];

        public ObjectInstanceDB Create(
            MEGame game,
            IReadOnlyList<string> files,
            Action<int> packageProcessed,
            Action<int> discoveredPackages)
        {
            Games.Add(game);
            beforeProgress?.Invoke();
            packageProcessed(1);
            return database;
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private static byte[] Serialize(ObjectInstanceDB database)
    {
        using var stream = new MemoryStream();
        database.Serialize(stream);
        return stream.ToArray();
    }
}
