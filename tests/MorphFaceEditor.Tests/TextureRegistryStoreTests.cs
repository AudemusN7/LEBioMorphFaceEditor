using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.Tests;

/// <summary>Protects the compact registry's independent, atomic on-disk contract.</summary>
public static class TextureRegistryStoreTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("texture registry store: snapshot round trip preserves metadata", SnapshotRoundTripPreservesMetadata),
        new("texture registry store: wrong game payload is rejected", WrongGamePayloadIsRejected),
        new("texture registry store: unsupported schema is outdated", UnsupportedSchemaIsOutdated),
        new("texture registry store: malformed payload is failed", MalformedPayloadIsFailed),
        new("texture registry store: cancelled write preserves active file", CancelledWritePreservesActiveFile),
        new("texture registry builder: scans each package once", BuilderScansEachPackageOnce),
        new("texture registry builder: groups paths by mount precedence", BuilderGroupsPathsByMountPrecedence),
        new("texture registry builder: reports scan write verify phases", BuilderReportsEveryPhase),
        new("texture registry builder: cancelled rebuild preserves active file", CancelledBuildPreservesActiveFile),
        new("texture registry builder: rebuild all is sequential", BuilderRebuildAllIsSequential)
    ];

    private static void BuilderScansEachPackageOnce()
    {
        using var fixture = RegistryFixture.Create();
        var scanner = new FakePackageScanner();
        var builder = fixture.CreateBuilder(scanner, _ => ["A.pcc", "B.pcc", "C.pcc"]);

        _ = builder.RebuildAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();

        TestAssert.True(scanner.Paths.OrderBy(value => value).SequenceEqual(["A.pcc", "B.pcc", "C.pcc"]),
            "The builder skipped or reopened an effective package.");
    }

    private static void BuilderGroupsPathsByMountPrecedence()
    {
        using var fixture = RegistryFixture.Create();
        const string texturePath = "BIOG_SAL_HED_PROMorph_R.Add.SAL_HED_PRO_Add1";
        var scanner = new FakePackageScanner(new Dictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>
        {
            ["Base.pcc"] = [new(texturePath, Occurrence("Base.pcc", 0, TextureCatalogOrigin.BaseGame))],
            ["Mod.pcc"] = [new(texturePath, Occurrence("Mod.pcc", 9021, TextureCatalogOrigin.Mod))]
        });
        var builder = fixture.CreateBuilder(scanner, _ => ["Base.pcc", "Mod.pcc"]);

        _ = builder.RebuildAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();
        var candidate = fixture.Store.Read(MorphFaceGame.LE3).Candidates.Single();

        TestAssert.Equal("Mod.pcc", candidate.EffectiveOccurrence.PackagePath);
        TestAssert.Equal(2, candidate.Occurrences.Count);
    }

    private static void BuilderReportsEveryPhase()
    {
        using var fixture = RegistryFixture.Create();
        var progress = new CapturingProgress<TextureRegistryBuildProgress>();
        var builder = fixture.CreateBuilder(new FakePackageScanner(), _ => ["A.pcc", "B.pcc"]);

        _ = builder.RebuildAsync(MorphFaceGame.LE2, progress).GetAwaiter().GetResult();

        TestAssert.True(progress.Values.Select(value => value.Phase).Distinct().SequenceEqual(
                [TextureRegistryBuildPhase.ScanningPackages, TextureRegistryBuildPhase.WritingRegistry,
                 TextureRegistryBuildPhase.VerifyingRegistry, TextureRegistryBuildPhase.Ready]),
            "The builder did not report scan, write, verify, and ready in order.");
        TestAssert.Equal(2, progress.Values.Last().PackagesProcessed);
    }

    private static void CancelledBuildPreservesActiveFile()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1));
        var path = fixture.Paths.GetPath(MorphFaceGame.LE1);
        var before = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        var scanner = new FakePackageScanner(onScan: cancellation.Cancel);
        var builder = fixture.CreateBuilder(scanner, _ => ["A.pcc", "B.pcc"]);

        try
        {
            _ = builder.RebuildAsync(MorphFaceGame.LE1, null, cancellation.Token).GetAwaiter().GetResult();
            throw new Exception("A cancelled registry build completed.");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        TestAssert.True(before.SequenceEqual(File.ReadAllBytes(path)),
            "Cancellation replaced the previously verified registry.");
    }

    private static void BuilderRebuildAllIsSequential()
    {
        using var fixture = RegistryFixture.Create();
        var scanner = new FakePackageScanner();
        var builder = fixture.CreateBuilder(scanner, game => [$"{game}.pcc"]);

        _ = builder.RebuildAllAsync().GetAwaiter().GetResult();

        TestAssert.True(scanner.Games.SequenceEqual([MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3]),
            "Rebuild All did not process LE1, LE2, and LE3 sequentially.");
    }

    private static void SnapshotRoundTripPreservesMetadata()
    {
        using var fixture = RegistryFixture.Create();
        var snapshot = Snapshot(TextureCatalogGame.LE1);

        fixture.Store.WriteAtomic(snapshot);
        var reopened = fixture.Store.Read(MorphFaceGame.LE1);
        var status = fixture.Store.GetStatus(MorphFaceGame.LE1);

        TestAssert.Equal(TextureRegistrySnapshot.CurrentSchemaVersion, reopened.SchemaVersion);
        TestAssert.Equal(TextureCatalogGame.LE1, reopened.Game);
        TestAssert.Equal(37, reopened.InstalledPackageCount);
        TestAssert.Equal(1, reopened.Candidates.Count);
        var occurrence = reopened.Candidates.Single().EffectiveOccurrence;
        TestAssert.Equal(9021, occurrence.MountPriority);
        TestAssert.Equal("Textures_DLC_MOD", occurrence.TextureFileCacheName);
        TestAssert.Equal(4096, occurrence.Mips.Single().ExternalOffset);
        TestAssert.Equal(TextureRegistryState.Ready, status.State);
        TestAssert.Equal<int?>(1, status.TextureCount);
    }

    private static void WrongGamePayloadIsRejected()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1));
        File.Copy(fixture.Paths.GetPath(MorphFaceGame.LE1), fixture.Paths.GetPath(MorphFaceGame.LE2));

        try
        {
            _ = fixture.Store.Read(MorphFaceGame.LE2);
            throw new Exception("A registry payload for the wrong game was accepted.");
        }
        catch (InvalidDataException)
        {
            // Expected: the payload's game identity is authoritative.
        }
    }

    private static void UnsupportedSchemaIsOutdated()
    {
        using var fixture = RegistryFixture.Create();
        fixture.WriteHeaderOnly(MorphFaceGame.LE3, schemaVersion: 99);

        TestAssert.Equal(TextureRegistryState.Outdated, fixture.Store.GetStatus(MorphFaceGame.LE3).State);
    }

    private static void MalformedPayloadIsFailed()
    {
        using var fixture = RegistryFixture.Create();
        var path = fixture.Paths.GetPath(MorphFaceGame.LE3);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x01, 0x02, 0x03]);

        TestAssert.Equal(TextureRegistryState.Failed, fixture.Store.GetStatus(MorphFaceGame.LE3).State);
    }

    private static void CancelledWritePreservesActiveFile()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE2));
        var path = fixture.Paths.GetPath(MorphFaceGame.LE2);
        var before = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE2) with { InstalledPackageCount = 999 }, cancellation.Token);
            throw new Exception("A cancelled registry write completed.");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        TestAssert.True(before.SequenceEqual(File.ReadAllBytes(path)),
            "Cancellation changed the previously verified registry.");
        TestAssert.True(!File.Exists($"{path}.tmp"), "Cancellation left an adjacent temporary file.");
    }

    private static TextureRegistrySnapshot Snapshot(TextureCatalogGame game)
    {
        var occurrence = new TextureCatalogOccurrence(
            "DLC_MOD_Custom\\CookedPCConsole\\BioG_Sal.pcc",
            42,
            9021,
            TextureCatalogOrigin.Mod,
            1024,
            512,
            "PF_DXT5",
            "TEXTUREGROUP_Character",
            true,
            "Textures_DLC_MOD")
        {
            Mips = [new TextureMipStorageRecord(0, 1024, 512, 0x11, 524288, 131072, 4096, "Textures_DLC_MOD")]
        };
        return new TextureRegistrySnapshot(
            TextureRegistrySnapshot.CurrentSchemaVersion,
            game,
            new DateTimeOffset(2026, 8, 28, 3, 19, 0, TimeSpan.Zero),
            37,
            [new TextureCatalogCandidate(game, "BIOG_SAL_HED_PROMorph_R.Add.SAL_HED_PRO_Add1", occurrence, [occurrence])]);
    }

    private static TextureCatalogOccurrence Occurrence(
        string packagePath,
        int mountPriority,
        TextureCatalogOrigin origin) => new(
        packagePath, 42, mountPriority, origin, 1024, 1024,
        "PF_DXT1", "TEXTUREGROUP_Character", false, null);

    private sealed class RegistryFixture : IDisposable
    {
        private readonly string _root;

        private RegistryFixture(string root)
        {
            _root = root;
            Paths = new TextureRegistryPaths(root);
            Store = new TextureRegistryStore(Paths);
        }

        public TextureRegistryPaths Paths { get; }
        public TextureRegistryStore Store { get; }

        public TextureRegistryBuilder CreateBuilder(
            ITextureRegistryPackageScanner scanner,
            Func<MorphFaceGame, IReadOnlyList<string>> loadedFiles) =>
            new(Store, scanner, loadedFiles);

        public static RegistryFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"MFE-TextureRegistry-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new RegistryFixture(root);
        }

        public void WriteHeaderOnly(MorphFaceGame game, int schemaVersion)
        {
            var path = Paths.GetPath(game);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(TextureRegistryStore.Magic);
            writer.Write(schemaVersion);
            writer.Write((int)game);
            writer.Write(0L);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakePackageScanner(
        IReadOnlyDictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>? results = null,
        Action? onScan = null) : ITextureRegistryPackageScanner
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<TextureRegistryScannedTexture>> _results =
            results ?? new Dictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>();

        public List<string> Paths { get; } = [];
        public List<MorphFaceGame> Games { get; } = [];

        public IReadOnlyList<TextureRegistryScannedTexture> Scan(
            MorphFaceGame game,
            string packagePath,
            CancellationToken cancellationToken)
        {
            Games.Add(game);
            Paths.Add(packagePath);
            onScan?.Invoke();
            return _results.GetValueOrDefault(packagePath) ?? [];
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }
}
