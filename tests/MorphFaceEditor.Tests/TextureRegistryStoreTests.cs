using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.Tests;

/// <summary>Protects the compact registry's independent, atomic on-disk contract.</summary>
public static class TextureRegistryStoreTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("texture registry paths: default root aligns with editor AppData", DefaultRootAlignsWithEditorAppData),
        new("texture registry store: snapshot round trip preserves metadata", SnapshotRoundTripPreservesMetadata),
        new("texture registry store: wrong game payload is rejected", WrongGamePayloadIsRejected),
        new("texture registry store: unsupported schema is outdated", UnsupportedSchemaIsOutdated),
        new("texture registry store: malformed payload is failed", MalformedPayloadIsFailed),
        new("texture registry store: cancelled write preserves active file", CancelledWritePreservesActiveFile),
        new("texture registry store: concurrent writes use independent temporary files", ConcurrentWritesUseIndependentTemporaryFiles),
        new("texture registry builder: scans each package once", BuilderScansEachPackageOnce),
        new("texture registry builder: adds only shadowed physical base packages", BuilderAddsOnlyShadowedPhysicalBasePackages),
        new("texture registry builder: retains native templates beneath mod overrides", BuilderRetainsNativeTemplatesBeneathModOverrides),
        new("texture registry builder: groups paths by mount precedence", BuilderGroupsPathsByMountPrecedence),
        new("texture registry builder: groups HIR meshes under canonical paths", BuilderGroupsAttachmentMeshes),
        new("texture registry builder: reports scan write verify phases", BuilderReportsEveryPhase),
        new("texture registry builder: cancelled rebuild preserves active file", CancelledBuildPreservesActiveFile),
        new("texture registry builder: rebuild all is sequential", BuilderRebuildAllIsSequential),
        new("texture registry runtime: reads compact file without source packages", RuntimeReadsWithoutSourcePackages)
        ,new("custom assets: sidecar merges without changing installed registry", ManualSidecarMergesWithoutChangingInstalled)
        ,new("custom assets: missing PCC remains recorded with a relink report", MissingManualPccProducesRelinkReport)
        ,new("custom assets: selected export appends to sidecar", SelectedExportAppendsToSidecar)
    ];

    private static void DefaultRootAlignsWithEditorAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LE BioMorphFace Editor",
            "TextureRegistries",
            "LE1.mftr");

        TestAssert.Equal(expected, TextureRegistryPaths.CreateDefault().GetPath(MorphFaceGame.LE1));
    }

    private static void RuntimeReadsWithoutSourcePackages()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1));
        var service = new TextureCatalogService(fixture.Store);

        var result = service.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();

        TestAssert.True(result.IsAvailable, "A verified compact registry was reported unavailable.");
        TestAssert.Equal(1, result.Candidates.Count);
        TestAssert.Equal(1, result.AttachmentMeshes.Count);
        TestAssert.Equal("DLC_MOD_Custom\\CookedPCConsole\\BioG_Sal.pcc",
            result.Candidates.Single().EffectiveOccurrence.PackagePath);
    }

    private static void ManualSidecarMergesWithoutChangingInstalled()
    {
        using var fixture = RegistryFixture.Create();
        var installed = Snapshot(TextureCatalogGame.LE1);
        fixture.Store.WriteAtomic(installed);
        var manualPath = "BIOG_HMM_HIR_PRO_R.Hair.HMM_HIR_Explicit_CC";
        var packagePath = "C:\\Custom\\Explicit.pcc";
        var occurrence = new TextureCatalogOccurrence(packagePath, 3, 0,
            TextureCatalogOrigin.Manual, 256, 256, "PF_DXT5", "Character", false, null);
        var manual = new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
            TextureCatalogGame.LE1, DateTimeOffset.UtcNow, 0,
            [new TextureCatalogCandidate(TextureCatalogGame.LE1, manualPath, occurrence, [occurrence])])
        {
            ManualAssets = [new ManualRegistryAsset(packagePath, 3, manualPath, "Texture2D")]
        };
        fixture.Store.WriteManualAtomic(manual);

        TestAssert.Equal(1, fixture.Store.Read(MorphFaceGame.LE1).Candidates.Count);
        var catalog = new TextureCatalogService(fixture.Store)
            .ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        TestAssert.Equal(2, catalog.Candidates.Count);
        TestAssert.True(catalog.Candidates.Any(value => value.InstancedPath == manualPath),
            "An explicitly added texture excluded by automatic discovery was hidden.");
    }

    private static void MissingManualPccProducesRelinkReport()
    {
        using var fixture = RegistryFixture.Create();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetManualPath(MorphFaceGame.LE1))!,
            "missing-custom.pcc");
        var occurrence = new AttachmentMeshOccurrence(path, "Hair.HMM_HIR_Custom_MDL", 3, 0,
            TextureCatalogOrigin.Manual, 42);
        var manual = new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
            TextureCatalogGame.LE1, DateTimeOffset.UtcNow, 0, [])
        {
            AttachmentMeshes = [new AttachmentMeshCandidate("missing-custom.Hair.HMM_HIR_Custom_MDL",
                occurrence, [occurrence])],
            ManualAssets = [new ManualRegistryAsset(path, 3, occurrence.InstancedPath, "SkeletalMesh")]
        };
        fixture.Store.WriteManualAtomic(manual);

        var failures = new TextureRegistryManualAssetService(fixture.Store).Revalidate(MorphFaceGame.LE1);
        TestAssert.True(failures.Count == 1,
            $"Expected one missing-PCC failure; got {failures.Count}: {string.Join(" | ", failures)}");
        TestAssert.True(File.Exists(fixture.Store.GetManualReportPath(MorphFaceGame.LE1)),
            "The missing manual PCC was not written to a relink report.");
        TestAssert.True(fixture.Store.ReadManual(MorphFaceGame.LE1).ManualAssets.Single().IsMissing,
            "The missing selection was discarded instead of retained for relinking.");
        TestAssert.True(fixture.Store.ReadManual(MorphFaceGame.LE1).AttachmentMeshes.Count == 1,
            "The missing mesh occurrence was not retained in the manual MFTR.");
    }

    private static void SelectedExportAppendsToSidecar()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1));
        var path = Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc");
        var export = PackageAssetInspector.Inventory(path, ["Texture2D", "SkeletalMesh"]).Entries
            .First(value => !value.IsDefaultObject);
        var selected = new ManualRegistryAsset(path, export.UIndex,
            export.InstancedPath, export.ClassName);
        var missingPath = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetManualPath(MorphFaceGame.LE1))!,
            "old-location.pcc");
        fixture.Store.WriteManualAtomic(new TextureRegistrySnapshot(
            TextureRegistrySnapshot.CurrentSchemaVersion, TextureCatalogGame.LE1,
            DateTimeOffset.UtcNow, 0, [])
        {
            ManualAssets = [selected with { PackagePath = missingPath, IsMissing = true }]
        });

        new TextureRegistryManualAssetService(fixture.Store).Append(MorphFaceGame.LE1, [selected]);

        var manual = fixture.Store.ReadManual(MorphFaceGame.LE1);
        TestAssert.Equal(1, manual.ManualAssets.Count);
        TestAssert.Equal(export.InstancedPath, manual.ManualAssets[0].InstancedPath);
        TestAssert.Equal(path, manual.ManualAssets[0].PackagePath);
        TestAssert.True(!manual.ManualAssets[0].IsMissing,
            "Selecting the export at its new PCC path did not relink the missing asset.");
        TestAssert.Equal(1, fixture.Store.Read(MorphFaceGame.LE1).Candidates.Count);
        TestAssert.True(manual.Candidates.Count + manual.AttachmentMeshes.Count == 1,
            "The selected export was not stored in its custom MFTR.");
    }

    private static void BuilderScansEachPackageOnce()
    {
        using var fixture = RegistryFixture.Create();
        var scanner = new FakePackageScanner();
        var builder = fixture.CreateBuilder(scanner, _ => ["A.pcc", "B.pcc", "C.pcc"]);

        _ = builder.RebuildAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();

        TestAssert.True(scanner.Paths.OrderBy(value => value).SequenceEqual(["A.pcc", "B.pcc", "C.pcc"]),
            "The builder skipped or reopened an effective package.");
    }

    private static void BuilderAddsOnlyShadowedPhysicalBasePackages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-TextureRegistry-Merge-{Guid.NewGuid():N}");
        var cookedPath = Path.Combine(root, "Game", "ME1", "BioGame", "CookedPCConsole");
        var modCookedPath = Path.Combine(root, "Game", "ME1", "BioGame", "DLC", "DLC_MOD_Test",
            "CookedPCConsole");
        Directory.CreateDirectory(cookedPath);
        Directory.CreateDirectory(modCookedPath);
        var basePackage = Path.Combine(cookedPath, "BIOA_STA60_01nodest_DSG.pcc");
        var modPackage = Path.Combine(modCookedPath, "STA", "BIOA_STA60_01nodest_DSG.pcc");
        File.WriteAllBytes(basePackage, [0x01]);

        try
        {
            var paths = TextureRegistryBuilder.MergeScanPaths(
                [modPackage, modPackage.ToLowerInvariant(), Path.Combine(root, "outside", "unrelated.pcc")],
                cookedPath);

            TestAssert.True(paths.SequenceEqual(
                    [modPackage, Path.Combine(root, "outside", "unrelated.pcc"), basePackage]),
                "The merge did not preserve effective order, absolute-path deduplication, and the matching base package.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void BuilderRetainsNativeTemplatesBeneathModOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-TextureRegistry-Templates-{Guid.NewGuid():N}");
        var cookedPath = Path.Combine(root, "Game", "ME1", "BioGame", "CookedPCConsole");
        var modCookedPath = Path.Combine(root, "Game", "ME1", "BioGame", "DLC", "DLC_MOD_Test",
            "CookedPCConsole");
        Directory.CreateDirectory(cookedPath);
        Directory.CreateDirectory(modCookedPath);
        var basePackage = Path.Combine(cookedPath, "BIOA_STA60_01nodest_DSG.pcc");
        var modPackage = Path.Combine(modCookedPath, "STA", "BIOA_STA60_01nodest_DSG.pcc");
        File.WriteAllBytes(basePackage, [0x01]);
        try
        {
            using var fixture = RegistryFixture.Create();
            const string texturePath = "BIOG_ASA_HED_PROMorph_R.PROBase.ASA_HED_PROBase_Diff";
            var scanner = new FakePackageScanner(
                new Dictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>
                {
                    [modPackage] = [new(texturePath, Occurrence(modPackage, 9000, TextureCatalogOrigin.Mod))],
                    [basePackage] = [new(texturePath, Occurrence(basePackage, 0, TextureCatalogOrigin.BaseGame))]
                },
                templates: new Dictionary<string, IReadOnlyList<MorphFaceTemplateCandidate>>
                {
                    [modPackage] = [new(modPackage, 10, "ASA.Mod_Asari", "BIOG_ASA_HED_PROMorph_R.PROBase.ASA_HED_PROBASE_MDL", 9000, TextureCatalogOrigin.Mod)],
                    [basePackage] = [new(basePackage, 20, "ASA.sta60_amb_asari01", "BIOG_ASA_HED_PROMorph_R.PROBase.ASA_HED_PROBASE_MDL", 0, TextureCatalogOrigin.BaseGame)]
                });
            var builder = new TextureRegistryBuilder(fixture.Store, scanner, _ => [modPackage], _ => cookedPath);

            _ = builder.RebuildAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
            var snapshot = fixture.Store.Read(MorphFaceGame.LE1);

            TestAssert.True(scanner.Paths.SequenceEqual([modPackage, basePackage]),
                "The shadowed physical base package was not scanned after the effective mod package.");
            TestAssert.Equal(basePackage, snapshot.MorphFaceTemplates[0].PackagePath);
            TestAssert.Equal("ASA.sta60_amb_asari01", snapshot.MorphFaceTemplates[0].FacePath);
            TestAssert.Equal(modPackage, snapshot.Candidates.Single().EffectiveOccurrence.PackagePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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

    private static void BuilderGroupsAttachmentMeshes()
    {
        using var fixture = RegistryFixture.Create();
        const string shortPath = "Hair.HMF_HIR_Custom_MDL";
        const string fullPath = "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Custom_MDL";
        const string biogPackage = "BIOG_HMF_HIR_PRO.pcc";
        const string levelPackage = "BIOA_TEST.pcc";
        var scanner = new FakePackageScanner(meshes: new Dictionary<string, IReadOnlyList<AttachmentMeshOccurrence>>
        {
            [biogPackage] = [new(biogPackage, shortPath, 7, 0, TextureCatalogOrigin.BaseGame)],
            [levelPackage] = [new(levelPackage, fullPath, 10, 0, TextureCatalogOrigin.BaseGame)]
        });
        var builder = fixture.CreateBuilder(scanner, _ => [biogPackage, levelPackage]);

        _ = builder.RebuildAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();
        var mesh = fixture.Store.Read(MorphFaceGame.LE3).AttachmentMeshes.Single();
        TestAssert.Equal(fullPath, mesh.CanonicalPath);
        TestAssert.Equal(2, mesh.Occurrences.Count);
        TestAssert.Equal(biogPackage, mesh.EffectiveOccurrence.PackagePath);
        TestAssert.Equal(shortPath, mesh.EffectiveOccurrence.InstancedPath);
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
        TestAssert.Equal(1, reopened.MorphFaceTemplates.Count);
        TestAssert.Equal(1, reopened.AttachmentMeshes.Count);
        TestAssert.Equal("HMF.BioFace_Test", reopened.MorphFaceTemplates.Single().FacePath);
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
        fixture.WriteHeaderOnly(MorphFaceGame.LE3, schemaVersion: 1);

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

    private static void ConcurrentWritesUseIndependentTemporaryFiles()
    {
        using var fixture = RegistryFixture.Create();
        var secondStore = new TextureRegistryStore(fixture.Paths);
        using var firstReadyToVerify = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var firstSnapshot = Snapshot(TextureCatalogGame.LE2) with { InstalledPackageCount = 101 };
        var secondSnapshot = Snapshot(TextureCatalogGame.LE2) with { InstalledPackageCount = 202 };
        var first = Task.Run(() => fixture.Store.WriteAtomic(
            firstSnapshot,
            CancellationToken.None,
            () =>
            {
                firstReadyToVerify.Set();
                releaseFirst.Wait();
            }));
        TestAssert.True(firstReadyToVerify.Wait(TimeSpan.FromSeconds(2)),
            "The first registry write did not reach verification.");

        var second = Task.Run(() => secondStore.WriteAtomic(secondSnapshot));
        second.GetAwaiter().GetResult();
        releaseFirst.Set();
        first.GetAwaiter().GetResult();

        var reopened = fixture.Store.Read(MorphFaceGame.LE2);
        TestAssert.True(reopened.InstalledPackageCount is 101 or 202,
            "Concurrent verified writers produced an unexpected registry payload.");
        TestAssert.Equal(0, Directory.GetFiles(
            Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE2))!, "*.tmp").Length);
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
            [new TextureCatalogCandidate(game, "BIOG_SAL_HED_PROMorph_R.Add.SAL_HED_PRO_Add1", occurrence, [occurrence])])
        {
            AttachmentMeshes =
            [
                new AttachmentMeshCandidate(
                    "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Test_MDL",
                    new AttachmentMeshOccurrence("BIOG_HMF_HIR_PRO.pcc", "Hair.HMF_HIR_Test_MDL",
                        8, 0, TextureCatalogOrigin.BaseGame),
                    [new AttachmentMeshOccurrence("BIOG_HMF_HIR_PRO.pcc", "Hair.HMF_HIR_Test_MDL",
                        8, 0, TextureCatalogOrigin.BaseGame)])
            ],
            MorphFaceTemplates =
            [
                new MorphFaceTemplateCandidate(
                    "BioA_Test.pcc", 7, "HMF.BioFace_Test",
                    "BIOG_HMF_HED_PROMorph_R.PROBase.HMF_HED_PROBase_MDL",
                    0, TextureCatalogOrigin.BaseGame)
            ]
        };
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
        Action? onScan = null,
        IReadOnlyDictionary<string, IReadOnlyList<MorphFaceTemplateCandidate>>? templates = null,
        IReadOnlyDictionary<string, IReadOnlyList<AttachmentMeshOccurrence>>? meshes = null) : ITextureRegistryPackageScanner
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<TextureRegistryScannedTexture>> _results =
            results ?? new Dictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<MorphFaceTemplateCandidate>> _templates =
            templates ?? new Dictionary<string, IReadOnlyList<MorphFaceTemplateCandidate>>();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<AttachmentMeshOccurrence>> _meshes =
            meshes ?? new Dictionary<string, IReadOnlyList<AttachmentMeshOccurrence>>();

        public List<string> Paths { get; } = [];
        public List<MorphFaceGame> Games { get; } = [];

        public TextureRegistryPackageScan Scan(
            MorphFaceGame game,
            string packagePath,
            CancellationToken cancellationToken)
        {
            Games.Add(game);
            Paths.Add(packagePath);
            onScan?.Invoke();
            return new TextureRegistryPackageScan(
                _results.GetValueOrDefault(packagePath) ?? [],
                _templates.GetValueOrDefault(packagePath) ?? [])
            {
                AttachmentMeshes = _meshes.GetValueOrDefault(packagePath) ?? []
            };
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }
}
