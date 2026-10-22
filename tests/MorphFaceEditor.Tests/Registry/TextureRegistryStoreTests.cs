using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.Tests;

/// <summary>Protects the compact registry's independent, atomic on-disk contract.</summary>
public static class TextureRegistryStoreTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("texture registry store: snapshot round trip preserves metadata", SnapshotRoundTripPreservesMetadata),
        new("texture registry store: wrong game payload is rejected", WrongGamePayloadIsRejected),
        new("texture registry store: unsupported and malformed files are rejected without modification", RejectedPayloadsPreserveFiles),
        new("texture registry store: cancelled write preserves active file", CancelledWritePreservesActiveFile),
        new("texture registry store: failed paired write preserves both registries", FailedPairWritePreservesBoth),
        new("texture registry store: concurrent writes use independent temporary files", ConcurrentWritesUseIndependentTemporaryFiles),
        new("texture registry scanner: persisted donors follow highest official mount and keep only mod additions", RealScannerPreservesOfficialDonors),
        new("texture registry builder: mod additions merge from sidecar without stock replacements", ModAdditionsMergeFromSidecar),
        new("texture registry builder: filename winner without the asset excludes shadowed official defaults", BuilderExcludesShadowedOfficialDefault),
        new("texture registry resolver: official discovery filters mod roots and rechecks changed mounts", OfficialDiscoveryFiltersModsAndRechecksMounts),
        new("texture registry builder: cancelled rebuild preserves active file", CancelledBuildPreservesActiveFile),
        new("texture registry builder: keeps filename-winning official MIC variants after lookup read", OfficialMicVariantsSurviveLookupRead),
        new("texture registry builder: mod-only rebuild scans only mods and preserves other files", ModsOnlyRebuildIsScoped),
        new("texture registry builder: mod-only prerequisite and changed-index failures are typed and atomic", ModsOnlyFailuresAreTyped),
        new("texture registry status: detects stale represented sources without disabling catalogues", StaleSourcesAreReportedSeparately),
        new("texture registry runtime: changed or removed package leaves catalogue available", ChangedPackageInvalidatesCachedCatalogue),
        new("custom assets: missing PCC remains recorded with a relink report", MissingManualPccProducesRelinkReport),
        new("custom assets: selected export appends to sidecar", SelectedExportAppendsToSidecar),
    ];

    private static void ModAdditionsMergeFromSidecar()
    {
        using var fixture = RegistryFixture.Create();
        var root = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE3))!, "Game", "BioGame");
        var basePackage = Path.Combine(root, "CookedPCConsole", "BIOG_HMF_HED.pcc");
        var modPackage = Path.Combine(root, "DLC", "DLC_MOD_Test", "CookedPCConsole", "BIOG_Custom.pcc");
        Directory.CreateDirectory(Path.GetDirectoryName(basePackage)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modPackage)!);
        File.WriteAllBytes(basePackage, [1]);
        File.WriteAllBytes(modPackage, [2]);
        const string stock = "BIOG_HMF_HED.Stock.Diff";
        const string addition = "BIOG_Custom.Head.Diff";
        var scanner = new FakePackageScanner(new Dictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>
        {
            [basePackage] = [new(stock, Occurrence(basePackage, 0, TextureCatalogOrigin.BaseGame))],
            [modPackage] = [new(stock, Occurrence(modPackage, 9000, TextureCatalogOrigin.Mod)),
                new(addition, Occurrence(modPackage, 9000, TextureCatalogOrigin.Mod))]
        });
        var manual = Snapshot(MorphFaceGame.LE3);
        fixture.Store.WriteManualAtomic(manual);
        var manualBytes = File.ReadAllBytes(fixture.Paths.GetManualPath(MorphFaceGame.LE3));
        fixture.CreateBuilder(scanner, _ => [basePackage, modPackage])
            .RebuildAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();
        TestAssert.Equal(stock, fixture.Store.Read(MorphFaceGame.LE3).Candidates.Single().InstancedPath);
        TestAssert.Equal(addition, fixture.Store.ReadMods(MorphFaceGame.LE3).Candidates.Single().InstancedPath);
        TestAssert.True(manualBytes.SequenceEqual(File.ReadAllBytes(fixture.Paths.GetManualPath(MorphFaceGame.LE3))),
            "An automatic rescan replaced the manual sidecar.");
        var catalog = new TextureCatalogService(fixture.Store).ReadAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();
        TestAssert.True(catalog.IsAvailable && catalog.Candidates.Any(value => value.InstancedPath == addition),
            "A mod-only addition was absent from the merged picker catalogue.");
        TestAssert.True(catalog.Candidates.Single(value => value.InstancedPath == stock).IsEligibleOfficialDefault,
            "A mod replacement displaced the official randomisation donor.");
        TestAssert.True(!catalog.Candidates.Single(value => value.InstancedPath == addition).IsEligibleOfficialDefault,
            "A mod addition became an automatic official randomisation donor.");
    }

    private static void ChangedPackageInvalidatesCachedCatalogue()
    {
        using var fixture = RegistryFixture.Create();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE1))!, "Mounted.pcc");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        fixture.Store.WriteAtomic(Snapshot(MorphFaceGame.LE1) with
            { MountedPackages = [MountedPackageFingerprint.Read(path)] });
        var service = new TextureCatalogService(fixture.Store);
        var first = service.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        File.WriteAllBytes(path, [1, 2]);
        var cached = service.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        TestAssert.True(cached.IsAvailable, "A changed source package invalidated the whole catalogue.");
        TestAssert.True(fixture.Store.GetStatus(MorphFaceGame.LE1).IsStale,
            "A changed represented source package was not reported as stale.");
        TestAssert.True(ReferenceEquals(first, cached), "A source package change rebuilt the parsed catalogue.");
        File.Delete(path);
        var fresh = new TextureCatalogService(fixture.Store).ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        TestAssert.True(fresh.IsAvailable, "A missing asset package invalidated unrelated catalogue entries.");
        TestAssert.Equal(first.Candidates.Count, fresh.Candidates.Count);
    }

    private static void OfficialMicVariantsSurviveLookupRead()
    {
        using var fixture = RegistryFixture.Create();
        var root = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE3))!, "Game", "BioGame", "CookedPCConsole");
        var firstPackage = Path.Combine(root, "BIOG_One.pcc");
        var secondPackage = Path.Combine(root, "BioD_Two.pcc");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(firstPackage, [1]);
        File.WriteAllBytes(secondPackage, [2]);
        const string micPath = "BIOG_HMF_HED_PROMorph_R.PROBase.HMF_HED_PROBase_EYE_MAT_1a";
        var scanner = new FakePackageScanner(assets: new Dictionary<string, IReadOnlyList<InstalledAssetOccurrence>>
        {
            [firstPackage] = [new(micPath, "MaterialInstanceConstant", firstPackage, 17, 0, TextureCatalogOrigin.BaseGame)],
            [secondPackage] = [new(micPath, "MaterialInstanceConstant", secondPackage, 31, 0, TextureCatalogOrigin.BaseGame)]
        });
        fixture.CreateBuilder(scanner, _ => [firstPackage, secondPackage])
            .RebuildAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();

        var reopened = fixture.Store.ReadForLookup(MorphFaceGame.LE3);
        var locations = reopened.Assets
            .Where(value => value.InstancedPath.Equals(micPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase).ToArray();
        TestAssert.Equal(2, locations.Length);
        TestAssert.True(locations.Select(value => value.ExportUIndex).Order().SequenceEqual([17, 31]),
            "Lookup compaction removed one of the official MIC exports.");
        TestAssert.True(locations.All(value => reopened.OfficialPackagePaths.Contains(
                value.PackagePath, StringComparer.OrdinalIgnoreCase)),
            "A retained official MIC variant was outside the filename-winner packages.");
    }

    private static void OfficialDiscoveryFiltersModsAndRechecksMounts()
    {
        using var fixture = RegistryFixture.Create();
        var bioGame = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE3))!, "Game", "BioGame");
        var cooked = Path.Combine(bioGame, "CookedPCConsole");
        var lowerDlc = Path.Combine(bioGame, "DLC", "DLC_CON_PRO1");
        var higherDlc = Path.Combine(bioGame, "DLC", "DLC_METR_Patch01");
        var modDlc = Path.Combine(bioGame, "DLC", "DLC_MOD_Discovery");
        var disabledDlc = Path.Combine(bioGame, "DLC", "DLC_MOD_Disabled");
        foreach (var directory in new[] { cooked, lowerDlc, higherDlc, modDlc, disabledDlc })
            Directory.CreateDirectory(Path.Combine(directory, "CookedPCConsole"));

        var basePackage = Path.Combine(cooked, "BIOG_Shared.pcc");
        var lowerPackage = Path.Combine(lowerDlc, "CookedPCConsole", "BIOG_Shared.pcc");
        var higherPackage = Path.Combine(higherDlc, "CookedPCConsole", "BIOG_Shared.pcc");
        var modPackage = Path.Combine(modDlc, "CookedPCConsole", "BIOG_Shared.pcc");
        var disabledPackage = Path.Combine(disabledDlc, "CookedPCConsole", "BIOG_Disabled.pcc");
        foreach (var path in new[] { basePackage, lowerPackage, higherPackage, modPackage, disabledPackage })
            File.WriteAllBytes(path, [1]);
        WriteLe3Mount(lowerDlc, 2000);
        WriteLe3Mount(higherDlc, 4000);

        var enabledDlc = new[] { lowerDlc, higherDlc, modDlc };
        var allEnabledPaths = TextureRegistryBuilder.GetPhysicalPackagePaths(
            MorphFaceGame.LE3, cooked, enabledDlc);
        var officialPaths = TextureRegistryBuilder.GetOfficialPhysicalPackagePaths(
            MorphFaceGame.LE3, cooked, enabledDlc);
        TestAssert.True(allEnabledPaths.Contains(modPackage, StringComparer.OrdinalIgnoreCase),
            "The full physical-path seam stopped including enabled mods.");
        TestAssert.True(!allEnabledPaths.Contains(disabledPackage, StringComparer.OrdinalIgnoreCase),
            "Physical discovery included a DLC absent from the enabled-folder input.");
        TestAssert.True(!officialPaths.Contains(modPackage, StringComparer.OrdinalIgnoreCase),
            "Official-only discovery descended into an enabled mod root.");
        TestAssert.Equal(3, officialPaths.Count);
        var allPathWinners = TextureRegistryBuilder.SelectOfficialFilenameWinners(allEnabledPaths, MorphFaceGame.LE3);
        var filteredWinners = TextureRegistryBuilder.SelectOfficialFilenameWinners(officialPaths, MorphFaceGame.LE3);
        TestAssert.True(allPathWinners.SequenceEqual(filteredWinners, StringComparer.OrdinalIgnoreCase),
            "Filtering DLC roots changed the official filename-winner sequence.");
        TestAssert.Equal(Path.GetFullPath(higherPackage), filteredWinners.Single());

        // A new discovery operation must observe changed native mount data.
        WriteLe3Mount(higherDlc, 1000);
        var nextOperationWinners = TextureRegistryBuilder.SelectOfficialFilenameWinners(
            TextureRegistryBuilder.GetOfficialPhysicalPackagePaths(MorphFaceGame.LE3, cooked, enabledDlc),
            MorphFaceGame.LE3);
        TestAssert.Equal(Path.GetFullPath(lowerPackage), nextOperationWinners.Single());
    }

    private static void ModsOnlyRebuildIsScoped()
    {
        using var fixture = RegistryFixture.Create();
        var bioGame = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE3))!, "Game", "BioGame");
        var baseCooked = Path.Combine(bioGame, "CookedPCConsole");
        var winnerCooked = Path.Combine(bioGame, "DLC", "DLC_METR_Patch01", "CookedPCConsole");
        var modCooked = Path.Combine(bioGame, "DLC", "DLC_MOD_Test", "CookedPCConsole");
        foreach (var directory in new[] { baseCooked, winnerCooked, modCooked }) Directory.CreateDirectory(directory);
        var shadowed = Path.Combine(baseCooked, "BIOG_Shadowed.pcc");
        var winner = Path.Combine(winnerCooked, "BIOG_Shadowed.pcc");
        var mod = Path.Combine(modCooked, "BIOG_Shadowed.pcc");
        foreach (var (path, value) in new[] { (shadowed, (byte)1), (winner, (byte)2), (mod, (byte)3) })
            File.WriteAllBytes(path, [value]);
        WriteLe3Mount(Path.Combine(bioGame, "DLC", "DLC_METR_Patch01"), 4000);
        const string stockPath = "BIOG_Shadowed.Hair.Stock_MDL";
        const string newPath = "BIOG_Shadowed.Hair.New_MDL";
        var scanner = new FakePackageScanner(assets: new Dictionary<string, IReadOnlyList<InstalledAssetOccurrence>>
        {
            [shadowed] = [new(stockPath, "SkeletalMesh", shadowed, 10, 0, TextureCatalogOrigin.BaseGame)],
            [mod] = [new(stockPath, "SkeletalMesh", mod, 20, 9000, TextureCatalogOrigin.Mod),
                new(newPath, "SkeletalMesh", mod, 21, 9000, TextureCatalogOrigin.Mod)]
        });
        var builder = fixture.CreateBuilder(scanner, _ => [shadowed, winner, mod]);
        builder.RebuildAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();
        var officialBefore = File.ReadAllBytes(fixture.Paths.GetPath(MorphFaceGame.LE3));
        fixture.Store.WriteManualAtomic(Snapshot(MorphFaceGame.LE3));
        var manualBefore = File.ReadAllBytes(fixture.Paths.GetManualPath(MorphFaceGame.LE3));
        var modsBefore = File.ReadAllBytes(fixture.Paths.GetModsPath(MorphFaceGame.LE3));
        scanner.ScannedPaths.Clear();

        var status = builder.RebuildModsAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();

        TestAssert.Equal(1, scanner.ScannedPaths.Count);
        TestAssert.Equal(Path.GetFullPath(mod), Path.GetFullPath(scanner.ScannedPaths.Single()));
        TestAssert.True(officialBefore.SequenceEqual(File.ReadAllBytes(fixture.Paths.GetPath(MorphFaceGame.LE3))),
            "A mod-only rebuild changed the installed registry.");
        TestAssert.True(manualBefore.SequenceEqual(File.ReadAllBytes(fixture.Paths.GetManualPath(MorphFaceGame.LE3))),
            "A mod-only rebuild changed the manual registry.");
        TestAssert.True(!modsBefore.SequenceEqual(File.ReadAllBytes(fixture.Paths.GetModsPath(MorphFaceGame.LE3))),
            "A mod-only rebuild did not publish its new sidecar.");
        TestAssert.Equal(TextureRegistryState.Ready, status.State);
        var modAssets = fixture.Store.ReadMods(MorphFaceGame.LE3).Assets;
        TestAssert.True(modAssets.All(value => !value.InstancedPath.Equals(stockPath, StringComparison.OrdinalIgnoreCase)),
            "A mod replacement was retained even though its official identity exists in a shadowed package.");
        TestAssert.True(modAssets.Any(value => value.InstancedPath.Equals(newPath, StringComparison.OrdinalIgnoreCase)),
            "A mod-only addition was lost during the scoped rebuild.");
    }

    private static void ModsOnlyFailuresAreTyped()
    {
        using var fixture = RegistryFixture.Create();
        var missingScanner = new FakePackageScanner();
        try
        {
            fixture.CreateBuilder(missingScanner, _ => []).RebuildModsAsync(MorphFaceGame.LE1)
                .GetAwaiter().GetResult();
            throw new Exception("A mod-only rebuild proceeded without an installed database.");
        }
        catch (TextureRegistryBuildException exception)
        {
            TestAssert.Equal(TextureRegistryBuildFailure.OfficialDatabaseRequired, exception.Reason);
        }
        TestAssert.Equal(0, missingScanner.ScannedPaths.Count);

        fixture.CreateBuilder(new FakePackageScanner(), _ => []).RebuildAsync(MorphFaceGame.LE1)
            .GetAwaiter().GetResult();
        var modsPath = fixture.Paths.GetModsPath(MorphFaceGame.LE1);
        var modsBefore = File.ReadAllBytes(modsPath);
        var bioGame = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE1))!, "Game", "BioGame");
        var modPackage = Path.Combine(bioGame, "DLC", "DLC_MOD_Race", "CookedPCConsole", "BIOG_Mod.pcc");
        Directory.CreateDirectory(Path.GetDirectoryName(modPackage)!);
        File.WriteAllBytes(modPackage, [7]);
        var racingScanner = new FakePackageScanner(onScan: () =>
            File.WriteAllBytes(fixture.Paths.GetPath(MorphFaceGame.LE1), [0]));
        try
        {
            fixture.CreateBuilder(racingScanner, _ => [modPackage])
                .RebuildModsAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
            throw new Exception("A mod-only rebuild published after the installed index changed.");
        }
        catch (TextureRegistryBuildException exception)
        {
            TestAssert.Equal(TextureRegistryBuildFailure.OfficialDatabaseChanged, exception.Reason);
        }
        TestAssert.True(modsBefore.SequenceEqual(File.ReadAllBytes(modsPath)),
            "A rejected mod-only race changed the active mod database.");
    }

    private static void StaleSourcesAreReportedSeparately()
    {
        using var fixture = RegistryFixture.Create();
        var root = Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE1))!;
        var baseSource = Path.Combine(root, "base.pcc");
        var modSource = Path.Combine(root, "mod.pcc");
        var manualSource = Path.Combine(root, "manual.pcc");
        foreach (var path in new[] { baseSource, modSource, manualSource }) File.WriteAllBytes(path, [1]);
        fixture.Store.WriteAtomic(Snapshot(MorphFaceGame.LE1) with
        {
            MountedPackagePaths = [baseSource], MountedPackages = [MountedPackageFingerprint.Read(baseSource)]
        });
        fixture.Store.WriteManualAtomic(Snapshot(MorphFaceGame.LE1) with
        {
            MountedPackagePaths = [manualSource], MountedPackages = [MountedPackageFingerprint.Read(manualSource)]
        });
        fixture.Store.WriteModsAtomic(Snapshot(MorphFaceGame.LE1) with
        {
            MountedPackagePaths = [modSource], MountedPackages = [MountedPackageFingerprint.Read(modSource)]
        });
        TestAssert.True(!fixture.Store.GetStatus(MorphFaceGame.LE1).IsStale &&
                        !fixture.Store.GetCustomStatus(MorphFaceGame.LE1).IsStale,
            "Unchanged represented sources were marked stale.");
        File.WriteAllBytes(baseSource, [1, 2]);
        TestAssert.True(fixture.Store.GetStatus(MorphFaceGame.LE1).IsStale,
            "A changed base source was not marked stale.");
        TestAssert.True(!fixture.Store.GetCustomStatus(MorphFaceGame.LE1).IsStale,
            "A stale base source contaminated the sidecar freshness status.");
        File.WriteAllBytes(manualSource, [1, 2]);
        TestAssert.True(fixture.Store.GetCustomStatus(MorphFaceGame.LE1).IsStale,
            "A changed manual sidecar source was not included in custom freshness.");
        var catalog = new TextureCatalogService(fixture.Store).ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        TestAssert.True(catalog.IsAvailable, "Stale source diagnostics disabled the parsed picker catalogue.");
    }

    private static void MissingManualPccProducesRelinkReport()
    {
        using var fixture = RegistryFixture.Create();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetManualPath(MorphFaceGame.LE1))!,
            "missing-custom.pcc");
        var occurrence = new AttachmentMeshOccurrence(path, "Hair.HMM_HIR_Custom_MDL", 3, 0,
            TextureCatalogOrigin.Manual, 42);
        var manual = new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
            MorphFaceGame.LE1, DateTimeOffset.UtcNow, 0, [])
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
        fixture.Store.WriteAtomic(Snapshot(MorphFaceGame.LE1));
        var path = Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc");
        var export = PackageAssetInspector.Inventory(path, ["Texture2D", "SkeletalMesh"]).Entries
            .First(value => !value.IsDefaultObject);
        var selected = new ManualRegistryAsset(path, export.UIndex,
            export.InstancedPath, export.ClassName);
        var missingPath = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetManualPath(MorphFaceGame.LE1))!,
            "old-location.pcc");
        fixture.Store.WriteManualAtomic(new TextureRegistrySnapshot(
            TextureRegistrySnapshot.CurrentSchemaVersion, MorphFaceGame.LE1,
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

    private static void RealScannerPreservesOfficialDonors()
    {
        using var fixture = RegistryFixture.Create();
        var bioGame = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE3))!, "BioGame");
        var locations = new[] { ("CookedPCConsole", 0), ("DLC/DLC_CON_PRO1/CookedPCConsole", 2060),
            ("DLC/DLC_METR_Patch01/CookedPCConsole", 4000), ("DLC/DLC_MOD_Test/CookedPCConsole", 9000) };
        var paths = new List<string>();
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var (relative, mount) in locations)
        {
            var cooked = Path.GetFullPath(Path.Combine(bioGame, relative));
            Directory.CreateDirectory(cooked);
            if (mount != 0) WriteLe3Mount(Directory.GetParent(cooked)!.FullName, mount);
            var path = Path.Combine(cooked, "BIOG_ASA_Test.pcc");
            using var package = LegendaryExplorerCore.Packages.MEPackageHandler.CreateMemoryEmptyPackage(path,
                LegendaryExplorerCore.Packages.MEGame.LE3);
            var root = package.CreatePackageExport("BIOG_ASA_HED_PROMorph_R");
            var group = package.CreatePackageExport("PROBase", root);
            var texture = package.CreateExport("ASA_HED_PROBase_Diff", "Texture2D", group, indexed: false);
            texture.WriteBinary(LegendaryExplorerCore.Unreal.BinaryConverters.UTexture2D.Create());
            if (mount == 9000)
                package.CreateExport("MFE_Custom_Diff", "Texture2D", group, indexed: false)
                    .WriteBinary(LegendaryExplorerCore.Unreal.BinaryConverters.UTexture2D.Create());
            package.Save(path);
            paths.Add(path);
        }
        var builder = fixture.CreateBuilder(new LecTextureRegistryPackageScanner(), _ => paths);
        _ = builder.RebuildAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();
        var official = fixture.Store.Read(MorphFaceGame.LE3);
        TestAssert.Equal(paths[2], official.Candidates.Single().EffectiveOccurrence.PackagePath);
        TestAssert.Equal(TextureCatalogOrigin.OfficialDlc, official.Candidates.Single().EffectiveOccurrence.Origin);
        TestAssert.Equal(paths[2], official.OfficialPackagePaths.Single());
        var mods = fixture.Store.ReadMods(MorphFaceGame.LE3);
        TestAssert.Equal("BIOG_ASA_HED_PROMorph_R.PROBase.MFE_Custom_Diff", mods.Candidates.Single().InstancedPath);
        TestAssert.Equal(TextureCatalogOrigin.Mod, mods.Candidates.Single().EffectiveOccurrence.Origin);
    }

    private static void BuilderExcludesShadowedOfficialDefault()
    {
        using var fixture = RegistryFixture.Create();
        var root = Path.Combine(Path.GetTempPath(), $"MFE-TextureRegistry-Shadowed-{Guid.NewGuid():N}");
        var bioGame = Path.Combine(root, "Game", "LE3", "BioGame");
        var cookedPath = Path.Combine(bioGame, "CookedPCConsole");
        var lowerDlc = Path.Combine(bioGame, "DLC", "DLC_CON_PRO1");
        var winnerDlc = Path.Combine(bioGame, "DLC", "DLC_METR_Patch01");
        var modDlc = Path.Combine(bioGame, "DLC", "DLC_MOD_Shadowed");
        var lowerCooked = Path.Combine(lowerDlc, "CookedPCConsole");
        var winnerCooked = Path.Combine(winnerDlc, "CookedPCConsole");
        var modCooked = Path.Combine(modDlc, "CookedPCConsole");
        Directory.CreateDirectory(cookedPath);
        Directory.CreateDirectory(lowerCooked);
        Directory.CreateDirectory(winnerCooked);
        Directory.CreateDirectory(modCooked);
        var lowerPackage = Path.Combine(lowerCooked, "BIOG_Shadowed.pcc");
        var winnerPackage = Path.Combine(winnerCooked, "BIOG_Shadowed.pcc");
        var modPackage = Path.Combine(modCooked, "BIOG_Shadowed.pcc");
        File.WriteAllBytes(lowerPackage, [0x01]);
        File.WriteAllBytes(winnerPackage, [0x02]);
        File.WriteAllBytes(modPackage, [0x03]);
        WriteLe3Mount(lowerDlc, 2060);
        WriteLe3Mount(winnerDlc, 4000);
        const string texturePath = "BIOG_Shadowed.Textures.OnlyInLower";
        const string meshPath = "BIOG_HMF_HIR_PRO.Hair.OnlyInLower_MDL";
        try
        {
            var scanner = new FakePackageScanner(
                new Dictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>
                {
                    [lowerPackage] = [new(texturePath, Occurrence(lowerPackage, 2060, TextureCatalogOrigin.OfficialDlc))],
                    [modPackage] = [new(texturePath, Occurrence(modPackage, 9000, TextureCatalogOrigin.Mod))]
                },
                meshes: new Dictionary<string, IReadOnlyList<AttachmentMeshOccurrence>>
                {
                    [lowerPackage] = [new(lowerPackage, meshPath, 10, 2060, TextureCatalogOrigin.OfficialDlc)],
                    [modPackage] = [new(modPackage, meshPath, 11, 9000, TextureCatalogOrigin.Mod)]
                });
            var physicalPaths = TextureRegistryBuilder.GetPhysicalPackagePaths(MorphFaceGame.LE3,
                cookedPath, [lowerDlc, winnerDlc, modDlc]);
            var builder = fixture.CreateBuilder(scanner, _ => physicalPaths);

            _ = builder.RebuildAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();
            var snapshot = fixture.Store.Read(MorphFaceGame.LE3);
            TestAssert.Equal(Path.GetFullPath(winnerPackage), snapshot.OfficialPackagePaths.Single());
            var texture = snapshot.Candidates.Single();
            TestAssert.Equal(Path.GetFullPath(lowerPackage), texture.EffectiveOccurrence.PackagePath);
            TestAssert.True(!texture.IsEligibleOfficialDefault,
                "A lower official occurrence was marked eligible although the filename winner lacks that path.");
            TestAssert.Equal(1, texture.Occurrences.Count);
            TestAssert.Equal(0, fixture.Store.ReadMods(MorphFaceGame.LE3).Candidates.Count);

            var mesh = snapshot.AttachmentMeshes.Single();
            TestAssert.True(!mesh.IsEligibleOfficialDefault,
                "A lower official attachment was marked eligible although the filename winner lacks that path.");
            TestAssert.Equal(1, mesh.Occurrences.Count);
            TestAssert.Equal(0, fixture.Store.ReadMods(MorphFaceGame.LE3).AttachmentMeshes.Count);
            TestAssert.True(AttachmentMeshCatalogResolver.Resolve(snapshot.AttachmentMeshes, meshPath) is null,
                "An exact attachment lookup selected the shadowed official occurrence as a default.");
            var modOnly = new AttachmentMeshCandidate(meshPath,
                new AttachmentMeshOccurrence(modPackage, meshPath, 11, 9000, TextureCatalogOrigin.Mod),
                [new AttachmentMeshOccurrence(modPackage, meshPath, 11, 9000, TextureCatalogOrigin.Mod)]);
            TestAssert.Equal(modPackage,
                AttachmentMeshCatalogResolver.Resolve([modOnly], meshPath)!.PackagePath);
            var manual = new AttachmentMeshOccurrence("manual.pcc", meshPath, 12, 0, TextureCatalogOrigin.Manual);
            var manualOnly = new AttachmentMeshCandidate(meshPath, manual, [manual]);
            TestAssert.Equal("manual.pcc",
                AttachmentMeshCatalogResolver.Resolve([manualOnly], meshPath)!.PackagePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteLe3Mount(string dlcDirectory, int priority)
    {
        var mountPath = Path.Combine(dlcDirectory, "CookedPCConsole", "Mount.dlc");
        var bytes = new byte[0x24];
        BitConverter.GetBytes(1).CopyTo(bytes, 0);
        BitConverter.GetBytes(0x2AD).CopyTo(bytes, 4);
        BitConverter.GetBytes(0xCD).CopyTo(bytes, 8);
        BitConverter.GetBytes(0x3006B).CopyTo(bytes, 12);
        BitConverter.GetBytes(priority).CopyTo(bytes, 16);
        File.WriteAllBytes(mountPath, bytes);
    }

    private static void CancelledBuildPreservesActiveFile()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(MorphFaceGame.LE1));
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

    private static void SnapshotRoundTripPreservesMetadata()
    {
        using var fixture = RegistryFixture.Create();
        var snapshot = Snapshot(MorphFaceGame.LE1) with
        {
            Assets =
            [
                new("BIOG_Test.Asset", "SkeletalMesh", "BIOG_Test.pcc", 7, 0, TextureCatalogOrigin.BaseGame),
                new("BIOG_Test.Asset", "Texture2D", "DLC_CON_Test/BIOG_Test.pcc", 12, 500,
                    TextureCatalogOrigin.OfficialDlc)
            ]
        };

        fixture.Store.WriteAtomic(snapshot);
        var reopened = fixture.Store.Read(MorphFaceGame.LE1);
        var status = fixture.Store.GetStatus(MorphFaceGame.LE1);

        TestAssert.Equal(TextureRegistrySnapshot.CurrentSchemaVersion, reopened.SchemaVersion);
        TestAssert.Equal(MorphFaceGame.LE1, reopened.Game);
        TestAssert.Equal(37, reopened.InstalledPackageCount);
        TestAssert.Equal(1, reopened.Candidates.Count);
        TestAssert.Equal(1, reopened.MorphFaceTemplates.Count);
        TestAssert.Equal(1, reopened.AttachmentMeshes.Count);
        TestAssert.Equal("HMF.BioFace_Test", reopened.MorphFaceTemplates.Single().FacePath);
        var occurrence = reopened.Candidates.Single().EffectiveOccurrence;
        TestAssert.Equal(9021, occurrence.MountPriority);
        TestAssert.Equal("Textures_DLC_MOD", occurrence.TextureFileCacheName);
        TestAssert.Equal(4096, occurrence.Mips.Single().ExternalOffset);
        TestAssert.True(snapshot.Assets.SequenceEqual(reopened.Assets),
            "The registry changed an installed class, path or physical donor identity.");
        TestAssert.Equal(TextureRegistryState.Ready, status.State);
        TestAssert.Equal<int?>(1, status.TextureCount);
    }

    private static void WrongGamePayloadIsRejected()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(MorphFaceGame.LE1));
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

    private static void RejectedPayloadsPreserveFiles()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(MorphFaceGame.LE1));
        var validFile = File.ReadAllBytes(fixture.Paths.GetPath(MorphFaceGame.LE1));
        var revision = 0;
        foreach (var (path, status, read) in new (string Path, Func<TextureRegistryStatus> Status, Action Read)[]
        {
            (fixture.Paths.GetPath(MorphFaceGame.LE1), () => fixture.Store.GetStatus(MorphFaceGame.LE1),
                () => fixture.Store.Read(MorphFaceGame.LE1)),
            (fixture.Paths.GetModsPath(MorphFaceGame.LE1), () => fixture.Store.ReadModsWithStatus(MorphFaceGame.LE1).Status,
                () => fixture.Store.ReadMods(MorphFaceGame.LE1)),
            (fixture.Paths.GetManualPath(MorphFaceGame.LE1), () => fixture.Store.GetManualStatus(MorphFaceGame.LE1),
                () => fixture.Store.ReadManual(MorphFaceGame.LE1))
        })
        foreach (var (magic, version, expected) in new[]
        {
            (TextureRegistryStore.DevelopmentMagic, TextureRegistrySnapshot.CurrentSchemaVersion, TextureRegistryState.Outdated),
            (TextureRegistryStore.Magic, TextureRegistrySnapshot.CurrentSchemaVersion - 1, TextureRegistryState.Outdated),
            (TextureRegistryStore.Magic, TextureRegistrySnapshot.CurrentSchemaVersion + 1, TextureRegistryState.Outdated),
            (TextureRegistryStore.Magic, TextureRegistrySnapshot.CurrentSchemaVersion, TextureRegistryState.Failed)
        })
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(magic);
                writer.Write(version);
                if (expected == TextureRegistryState.Failed)
                {
                    writer.Write((int)MorphFaceGame.LE1);
                    writer.Write(0L);
                }
                else
                {
                    const int prefixSize = sizeof(uint) + sizeof(int);
                    writer.Write(validFile, prefixSize, validFile.Length - prefixSize);
                }
            }
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(++revision));
            var before = File.ReadAllBytes(path);
            TestAssert.Equal(expected, status().State);
            var rejected = false;
            try { read(); }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            { rejected = true; }
            TestAssert.True(rejected, $"The direct reader accepted a rejected registry at {path}.");
            TestAssert.True(before.SequenceEqual(File.ReadAllBytes(path)), "A rejected database was modified.");
        }
    }

    private static void CancelledWritePreservesActiveFile()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(MorphFaceGame.LE2));
        var path = fixture.Paths.GetPath(MorphFaceGame.LE2);
        var before = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            fixture.Store.WriteAtomic(Snapshot(MorphFaceGame.LE2) with { InstalledPackageCount = 999 }, cancellation.Token);
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

    private static void FailedPairWritePreservesBoth()
    {
        using var fixture = RegistryFixture.Create();
        var originalInstalled = Snapshot(MorphFaceGame.LE1) with { InstalledPackageCount = 11 };
        var originalMods = Snapshot(MorphFaceGame.LE1) with { InstalledPackageCount = 22 };
        fixture.Store.WriteInstalledAndModsAtomic(originalInstalled, originalMods);
        var replacementInstalled = originalInstalled with { InstalledPackageCount = 101 };
        var replacementMods = originalMods with { InstalledPackageCount = 202 };
        using var cancellation = new CancellationTokenSource();
        try
        {
            fixture.Store.WriteInstalledAndModsAtomic(replacementInstalled, replacementMods, cancellation.Token,
                () => cancellation.Cancel());
            throw new Exception("A cancelled paired registry write unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
            // Cancellation after staging must leave the previous pair intact.
        }

        TestAssert.Equal(11, fixture.Store.Read(MorphFaceGame.LE1).InstalledPackageCount);
        TestAssert.Equal(22, fixture.Store.ReadMods(MorphFaceGame.LE1).InstalledPackageCount);
        TestAssert.Equal(0, Directory.GetFiles(Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE1))!, "*.tmp").Length);

        try
        {
            fixture.Store.WriteInstalledAndModsAtomic(replacementInstalled, replacementMods,
                beforeVerification: () => throw new InvalidDataException("probe failure"));
            throw new Exception("A failed paired registry verification unexpectedly succeeded.");
        }
        catch (InvalidDataException exception) when (exception.Message == "probe failure")
        {
            // A failed verification must leave both old files untouched.
        }
        TestAssert.Equal(11, fixture.Store.Read(MorphFaceGame.LE1).InstalledPackageCount);
        TestAssert.Equal(22, fixture.Store.ReadMods(MorphFaceGame.LE1).InstalledPackageCount);
    }

    private static void ConcurrentWritesUseIndependentTemporaryFiles()
    {
        using var fixture = RegistryFixture.Create();
        var secondStore = new TextureRegistryStore(fixture.Paths);
        using var firstReadyToVerify = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var firstSnapshot = Snapshot(MorphFaceGame.LE2) with { InstalledPackageCount = 101 };
        var secondSnapshot = Snapshot(MorphFaceGame.LE2) with { InstalledPackageCount = 202 };
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

    private static TextureRegistrySnapshot Snapshot(MorphFaceGame game)
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

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakePackageScanner(
        IReadOnlyDictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>? results = null,
        Action? onScan = null,
        IReadOnlyDictionary<string, IReadOnlyList<AttachmentMeshOccurrence>>? meshes = null,
        IReadOnlyDictionary<string, IReadOnlyList<InstalledAssetOccurrence>>? assets = null) : ITextureRegistryPackageScanner
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<TextureRegistryScannedTexture>> _results =
            results ?? new Dictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<AttachmentMeshOccurrence>> _meshes =
            meshes ?? new Dictionary<string, IReadOnlyList<AttachmentMeshOccurrence>>();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<InstalledAssetOccurrence>> _assets =
            assets ?? new Dictionary<string, IReadOnlyList<InstalledAssetOccurrence>>();
        public List<string> ScannedPaths { get; } = [];

        public TextureRegistryPackageScan Scan(
            MorphFaceGame game,
            string packagePath,
            CancellationToken cancellationToken)
        {
            onScan?.Invoke();
            ScannedPaths.Add(packagePath);
            return new TextureRegistryPackageScan(
                _results.GetValueOrDefault(packagePath) ?? [], [])
            {
                AttachmentMeshes = _meshes.GetValueOrDefault(packagePath) ?? [],
                Assets = _assets.GetValueOrDefault(packagePath) ?? []
            };
        }
    }

}
