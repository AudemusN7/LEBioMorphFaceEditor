using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

/// <summary>Protects the compact registry's independent, atomic on-disk contract.</summary>
public static class TextureRegistryStoreTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("texture registry paths: default root aligns with editor AppData", DefaultRootAlignsWithEditorAppData),
        new("texture registry store: snapshot round trip preserves metadata", SnapshotRoundTripPreservesMetadata),
        new("texture registry store: all-occurrence index compacts package metadata and round trips", InstalledAssetIndexCompactsAndRoundTrips),
        new("texture registry store: wrong game payload is rejected", WrongGamePayloadIsRejected),
        new("texture registry store: unsupported schema is outdated", UnsupportedSchemaIsOutdated),
        new("texture registry store: legacy manual sidecars survive schema migration", LegacyManualAssetsSurvive),
        new("texture registry store: malformed payload is failed", MalformedPayloadIsFailed),
        new("texture registry store: cancelled write preserves active file", CancelledWritePreservesActiveFile),
        new("texture registry store: failed paired write preserves both registries", FailedPairWritePreservesBoth),
        new("texture registry store: shared cache reuses parsed snapshots across stores", SharedCacheReusesSnapshot),
        new("texture registry store: concurrent writes use independent temporary files", ConcurrentWritesUseIndependentTemporaryFiles),
        new("texture registry builder: scans each package once", BuilderScansEachPackageOnce),
        new("texture registry builder: scans every physical package and records official filename winners", BuilderIndexesAllPhysicalPackages),
        new("texture registry builder: keeps official templates and texture donors beneath mod overrides", BuilderRetainsNativeTemplatesBeneathModOverrides),
        new("texture registry builder: official texture occurrence is the default", BuilderGroupsPathsByMountPrecedence),
        new("texture registry builder: mod additions merge from sidecar without stock replacements", ModAdditionsMergeFromSidecar),
        new("texture registry builder: filename winner without the asset excludes shadowed official defaults", BuilderExcludesShadowedOfficialDefault),
        new("texture registry builder: groups HIR meshes under canonical paths", BuilderGroupsAttachmentMeshes),
        new("texture registry builder: reports scan write verify phases", BuilderReportsEveryPhase),
        new("texture registry builder: cancelled rebuild preserves active file", CancelledBuildPreservesActiveFile),
        new("texture registry builder: rebuild all is sequential", BuilderRebuildAllIsSequential),
        new("texture registry runtime: reads compact file without source packages", RuntimeReadsWithoutSourcePackages)
        ,new("texture registry runtime: restores hats and CC hair from legacy asset index", RuntimeRestoresLegacyHatMeshes)
        ,new("texture registry runtime: changed or removed package leaves catalogue available", ChangedPackageInvalidatesCachedCatalogue)
        ,new("texture registry runtime: warm reads reuse catalogue and refresh only registry files", WarmReadsUseLayoutStamps)
        ,new("custom assets: sidecar merges without changing installed registry", ManualSidecarMergesWithoutChangingInstalled)
        ,new("custom assets: missing PCC remains recorded with a relink report", MissingManualPccProducesRelinkReport)
        ,new("custom assets: selected export appends to sidecar", SelectedExportAppendsToSidecar)
        ,new("custom assets: preview attachments require and use merged database", PreviewAttachmentsUseMergedDatabase)
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
        var manual = Snapshot(TextureCatalogGame.LE3);
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

    private static void RuntimeRestoresLegacyHatMeshes()
    {
        using var fixture = RegistryFixture.Create();
        const string officialPath = @"C:\Games\LE1\BioGame\CookedPCConsole\BIOG_HMF_HIR_PRO.pcc";
        const string excludedPath = @"C:\Games\LE1\BioGame\CookedPCConsole\BIOG_HMF_HIR_PRO.pcc";
        var hatPath = "BIOG_HMF_HIR_PRO.Beanie.HMF_HAT_Bn1_MDL";
        var excludedPathName = "BIOG_HMF_HIR_PRO.Beanie.HMF_HAT_Bn1_MDL_Remaster";
        const string ccHair = "BIOG_HMF_HIR_PRO.Hair_PROCustom.HMF_HIR_PROCustom01_MDL_CC";
        const string ccHat = "BIOG_HMF_HIR_PRO.Beanie.HMF_HAT_Bn1_MDL_CC";
        fixture.Store.WriteAtomic(new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
            TextureCatalogGame.LE1, DateTimeOffset.UtcNow, 1, [])
        {
            OfficialPackagePaths = [officialPath],
            MountedPackagePaths = [officialPath],
            AttachmentMeshes =
            [
                new AttachmentMeshCandidate("biostage_twrasa.ST_twrasa_thirdworker_mesh",
                    new AttachmentMeshOccurrence(officialPath, "biostage_twrasa.ST_twrasa_thirdworker_mesh",
                        21, 0, TextureCatalogOrigin.BaseGame),
                    [new AttachmentMeshOccurrence(officialPath, "biostage_twrasa.ST_twrasa_thirdworker_mesh",
                        21, 0, TextureCatalogOrigin.BaseGame)])
            ],
            Assets =
            [
                new InstalledAssetOccurrence(hatPath, "SkeletalMesh", officialPath, 17, 0,
                    TextureCatalogOrigin.BaseGame),
                new InstalledAssetOccurrence(ccHair, "SkeletalMesh", officialPath, 22, 0,
                    TextureCatalogOrigin.BaseGame),
                new InstalledAssetOccurrence(ccHat, "SkeletalMesh", officialPath, 23, 0,
                    TextureCatalogOrigin.BaseGame),
                new InstalledAssetOccurrence(excludedPathName, "SkeletalMesh", excludedPath, 18, 0,
                    TextureCatalogOrigin.BaseGame),
                new InstalledAssetOccurrence("BioStage_ZyaVTL.ST_Bridge_chat_mesh", "SkeletalMesh",
                    officialPath, 20, 0, TextureCatalogOrigin.BaseGame),
                new InstalledAssetOccurrence("BIOG_HMF_HIR_PRO.Beanie.HMF_HAT_Mod_MDL", "SkeletalMesh",
                    @"C:\Games\LE1\BioGame\DLC\DLC_MOD_Test\CookedPCConsole\BIOG_HMF_HIR_PRO.pcc",
                    19, 9000, TextureCatalogOrigin.Mod)
            ]
        });

        var catalog = new TextureCatalogService(fixture.Store)
            .ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();

        var restored = catalog.AttachmentMeshes.Single(candidate => candidate.CanonicalPath == hatPath);
        TestAssert.Equal(officialPath, restored.EffectiveOccurrence.PackagePath);
        TestAssert.True(restored.IsEligibleOfficialDefault,
            "A legacy hat recovered from indexed metadata was not an eligible official donor.");
        TestAssert.True(catalog.AttachmentMeshes.Single(candidate => candidate.CanonicalPath == ccHair).IsEligibleOfficialDefault,
            "Supported CC hair was not recovered from the existing all-asset index.");
        TestAssert.True(!catalog.AttachmentMeshes.Any(candidate => candidate.CanonicalPath == ccHat),
            "The CC hair exception enabled excluded CC hats.");
        TestAssert.True(!catalog.AttachmentMeshes.Any(candidate => candidate.CanonicalPath == excludedPathName),
            "The excluded remaster variant was recovered from the all-asset index.");
        TestAssert.True(!catalog.AttachmentMeshes.Any(candidate => candidate.CanonicalPath.Contains("chat_mesh")),
            "An incidental HAT substring restored a scenery mesh as a hat.");
        TestAssert.True(!catalog.AttachmentMeshes.Single(candidate =>
                candidate.CanonicalPath.Contains("thirdworker")).IsEligibleOfficialDefault,
            "A scenery mesh already in a legacy attachment list remained an eligible official choice.");
        TestAssert.True(!catalog.AttachmentMeshes.Any(candidate =>
                candidate.CanonicalPath.Contains("HAT_Mod", StringComparison.OrdinalIgnoreCase)),
            "An automatic mod hat was inserted into the official catalogue projection.");
    }

    private static void ChangedPackageInvalidatesCachedCatalogue()
    {
        using var fixture = RegistryFixture.Create();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Paths.GetPath(MorphFaceGame.LE1))!, "Mounted.pcc");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1) with
            { MountedPackages = [MountedPackageFingerprint.Read(path)] });
        var service = new TextureCatalogService(fixture.Store);
        var first = service.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        File.WriteAllBytes(path, [1, 2]);
        var cached = service.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        TestAssert.True(cached.IsAvailable, "A changed source package invalidated the whole catalogue.");
        TestAssert.True(ReferenceEquals(first, cached), "A source package change rebuilt the parsed catalogue.");
        File.Delete(path);
        var fresh = new TextureCatalogService(fixture.Store).ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        TestAssert.True(fresh.IsAvailable, "A missing asset package invalidated unrelated catalogue entries.");
        TestAssert.Equal(first.Candidates.Count, fresh.Candidates.Count);
    }

    private static void WarmReadsUseLayoutStamps()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1));
        var service = new TextureCatalogService(fixture.Store);
        var first = service.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        TestAssert.True(ReferenceEquals(first, service.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult()),
            "A warm read rebuilt the unchanged registry catalogue.");
        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1) with { Candidates = [] });
        var refreshed = service.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        TestAssert.True(refreshed.IsAvailable, "A rebuilt MFTR was unavailable.");
        TestAssert.Equal(0, refreshed.Candidates.Count);
        TestAssert.True(!ReferenceEquals(first, refreshed), "A changed MFTR reused its old catalogue.");
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

    private static void PreviewAttachmentsUseMergedDatabase()
    {
        using var fixture = RegistryFixture.Create();
        var sourcePath = Path.Combine(Path.GetTempPath(), "MFE-custom-hair.pcc");
        const string canonicalPath = "MFE-custom-hair.Hair.HMM_HIR_Custom_MDL";
        var occurrence = new AttachmentMeshOccurrence(sourcePath, "Hair.HMM_HIR_Custom_MDL",
            7, 0, TextureCatalogOrigin.Manual, 42);
        fixture.Store.WriteManualAtomic(new TextureRegistrySnapshot(
            TextureRegistrySnapshot.CurrentSchemaVersion, TextureCatalogGame.LE1,
            DateTimeOffset.UtcNow, 0, [])
        {
            AttachmentMeshes = [new AttachmentMeshCandidate(canonicalPath, occurrence, [occurrence])],
            ManualAssets = [new ManualRegistryAsset(sourcePath, 7, occurrence.InstancedPath, "SkeletalMesh")]
        });
        using var reader = new MorphFacePackageReader();
        var references = new PackageReferenceService(reader,
            new TextureCatalogService(fixture.Store));
        try
        {
            _ = references.ResolveInstalledSkeletalMeshesAsync(MorphFaceGame.LE1, [canonicalPath])
                .GetAwaiter().GetResult();
            throw new Exception("Preview attachment discovery succeeded without an installed database.");
        }
        catch (InvalidOperationException)
        {
            // The manual sidecar cannot stand in for the required installed database.
        }

        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1));
        var resolved = references.ResolveInstalledSkeletalMeshesAsync(MorphFaceGame.LE1,
            [canonicalPath, occurrence.InstancedPath, "missing.Hair.HMM_HIR_NoSuchMesh_MDL"])
            .GetAwaiter().GetResult();
        TestAssert.Equal(2, resolved.Count);
        TestAssert.Equal(sourcePath, resolved[canonicalPath].PackagePath);
        TestAssert.Equal(7, resolved[canonicalPath].UIndex);
        TestAssert.Equal(sourcePath, resolved[occurrence.InstancedPath].PackagePath);
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

    private static void BuilderIndexesAllPhysicalPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-TextureRegistry-Merge-{Guid.NewGuid():N}");
        var cookedPath = Path.Combine(root, "Game", "LE3", "BioGame", "CookedPCConsole");
        var modDirectory = Path.Combine(root, "Game", "LE3", "BioGame", "DLC", "DLC_MOD_Test");
        var modCookedPath = Path.Combine(modDirectory, "CookedPCConsole");
        Directory.CreateDirectory(cookedPath);
        Directory.CreateDirectory(modCookedPath);
        var basePackage = Path.Combine(cookedPath, "BIOG_Override.pcc");
        var modPackage = Path.Combine(modCookedPath, "BIOG_Override.pcc");
        var modOnlyPackage = Path.Combine(modCookedPath, "ModOnly.pcc");
        File.WriteAllBytes(basePackage, [0x01]);
        File.WriteAllBytes(modPackage, [0x02]);
        File.WriteAllBytes(modOnlyPackage, [0x03]);

        try
        {
            var paths = TextureRegistryBuilder.GetPhysicalPackagePaths(
                MorphFaceGame.LE3, cookedPath, [modDirectory]);
            var officialWinners = TextureRegistryBuilder.SelectOfficialFilenameWinners(paths, MorphFaceGame.LE3);
            TestAssert.True(paths.Contains(Path.GetFullPath(basePackage), StringComparer.OrdinalIgnoreCase) &&
                            paths.Contains(Path.GetFullPath(modPackage), StringComparer.OrdinalIgnoreCase) &&
                            paths.Contains(Path.GetFullPath(modOnlyPackage), StringComparer.OrdinalIgnoreCase),
                "The physical inventory omitted a shadowed official or mod package.");
            TestAssert.Equal(1, officialWinners.Count(path => Path.GetFileName(path).Equals("BIOG_Override.pcc", StringComparison.OrdinalIgnoreCase)));
            TestAssert.Equal(Path.GetFullPath(basePackage), officialWinners.Single(path =>
                Path.GetFileName(path).Equals("BIOG_Override.pcc", StringComparison.OrdinalIgnoreCase)));
            TestAssert.Equal(Path.GetFullPath(basePackage), paths[0]);
            TestAssert.Equal(1, TextureRegistryBuilder.MergeScanPaths([basePackage, basePackage.ToLowerInvariant()]).Count);
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
        var modDirectory = Path.Combine(root, "Game", "ME1", "BioGame", "DLC", "DLC_MOD_Test");
        var modCookedPath = Path.Combine(modDirectory, "CookedPCConsole");
        Directory.CreateDirectory(cookedPath);
        Directory.CreateDirectory(modCookedPath);
        var basePackage = Path.Combine(cookedPath, "BIOA_STA60_01nodest_DSG.pcc");
        var modPackage = Path.Combine(modCookedPath, "STA", "BIOA_STA60_01nodest_DSG.pcc");
        File.WriteAllBytes(basePackage, [0x01]);
        Directory.CreateDirectory(Path.GetDirectoryName(modPackage)!);
        File.WriteAllBytes(modPackage, [0x02]);
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
            var physicalPaths = TextureRegistryBuilder.GetPhysicalPackagePaths(
                MorphFaceGame.LE1, cookedPath, [modDirectory]);
            var builder = new TextureRegistryBuilder(fixture.Store, scanner, _ => physicalPaths);

            _ = builder.RebuildAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
            var snapshot = fixture.Store.Read(MorphFaceGame.LE1);

            TestAssert.True(scanner.Paths.SequenceEqual([basePackage, modPackage]),
                "Official packages were not scanned before their filename override.");
            TestAssert.Equal(1, snapshot.MountedPackagePaths.Count);
            TestAssert.Equal(Path.GetFullPath(modPackage), fixture.Store.ReadMods(MorphFaceGame.LE1).MountedPackagePaths.Single());
            TestAssert.Equal(Path.GetFullPath(basePackage), snapshot.OfficialPackagePaths.Single());
            TestAssert.Equal(Path.GetFullPath(basePackage), snapshot.MorphFaceTemplates.Single().PackagePath);
            TestAssert.Equal("ASA.sta60_amb_asari01", snapshot.MorphFaceTemplates.Single().FacePath);
            TestAssert.Equal(Path.GetFullPath(basePackage), snapshot.Candidates.Single().EffectiveOccurrence.PackagePath);
            TestAssert.Equal(1, snapshot.Candidates.Single().Occurrences.Count);
            TestAssert.Equal(0, fixture.Store.ReadMods(MorphFaceGame.LE1).Candidates.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void BuilderGroupsPathsByMountPrecedence()
    {
        using var fixture = RegistryFixture.Create();
        var root = Path.Combine(Path.GetTempPath(), $"MFE-TextureRegistry-Official-{Guid.NewGuid():N}");
        var cookedPath = Path.Combine(root, "Game", "LE3", "BioGame", "CookedPCConsole");
        var modDirectory = Path.Combine(root, "Game", "LE3", "BioGame", "DLC", "DLC_MOD_Test");
        var modCooked = Path.Combine(modDirectory, "CookedPCConsole");
        Directory.CreateDirectory(cookedPath);
        Directory.CreateDirectory(modCooked);
        var basePackage = Path.Combine(cookedPath, "BIOG_SAL.pcc");
        var modPackage = Path.Combine(modCooked, "BIOG_SAL.pcc");
        File.WriteAllBytes(basePackage, [1]);
        File.WriteAllBytes(modPackage, [2]);
        const string texturePath = "BIOG_SAL_HED_PROMorph_R.Add.SAL_HED_PRO_Add1";
        try
        {
            var scanner = new FakePackageScanner(
                new Dictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>
                {
                    [basePackage] = [new(texturePath, Occurrence(basePackage, 0, TextureCatalogOrigin.BaseGame))],
                    [modPackage] = [new(texturePath, Occurrence(modPackage, 9021, TextureCatalogOrigin.Mod))]
                },
                assets: new Dictionary<string, IReadOnlyList<InstalledAssetOccurrence>>
                {
                    [basePackage] = [new(texturePath, "Texture2D", basePackage, 5, 0, TextureCatalogOrigin.BaseGame)],
                    [modPackage] = [new(texturePath, "Texture2D", modPackage, 7, 9021, TextureCatalogOrigin.Mod)]
                });
            var physicalPaths = TextureRegistryBuilder.GetPhysicalPackagePaths(
                MorphFaceGame.LE3, cookedPath, [modDirectory]);
            var builder = fixture.CreateBuilder(scanner, _ => physicalPaths);

            _ = builder.RebuildAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();
            var candidate = fixture.Store.Read(MorphFaceGame.LE3).Candidates.Single();

            TestAssert.Equal(Path.GetFullPath(basePackage), candidate.EffectiveOccurrence.PackagePath);
            TestAssert.Equal(1, candidate.Occurrences.Count);
            var indexedAssets = fixture.Store.Read(MorphFaceGame.LE3).Assets;
            TestAssert.Equal(1, indexedAssets.Count);
            TestAssert.Equal(0, fixture.Store.ReadMods(MorphFaceGame.LE3).Assets.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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

    private static void BuilderGroupsAttachmentMeshes()
    {
        using var fixture = RegistryFixture.Create();
        const string shortPath = "Hair.HMF_HIR_Custom_MDL";
        const string fullPath = "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Custom_MDL";
        var root = Path.Combine(Path.GetTempPath(), $"MFE-TextureRegistry-Hair-{Guid.NewGuid():N}");
        var cookedPath = Path.Combine(root, "Game", "LE3", "BioGame", "CookedPCConsole");
        var modDirectory = Path.Combine(root, "Game", "LE3", "BioGame", "DLC", "DLC_MOD_Test");
        var modCooked = Path.Combine(modDirectory, "CookedPCConsole");
        Directory.CreateDirectory(cookedPath);
        Directory.CreateDirectory(modCooked);
        var biogPackage = Path.Combine(cookedPath, "BIOG_HMF_HIR_PRO.pcc");
        var levelPackage = Path.Combine(modCooked, "BIOA_TEST.pcc");
        File.WriteAllBytes(biogPackage, [1]);
        File.WriteAllBytes(levelPackage, [2]);
        try
        {
            var scanner = new FakePackageScanner(meshes: new Dictionary<string, IReadOnlyList<AttachmentMeshOccurrence>>
            {
                [biogPackage] = [new(biogPackage, shortPath, 7, 0, TextureCatalogOrigin.BaseGame)],
                [levelPackage] = [new(levelPackage, fullPath, 10, 9000, TextureCatalogOrigin.Mod)]
            });
            var physicalPaths = TextureRegistryBuilder.GetPhysicalPackagePaths(
                MorphFaceGame.LE3, cookedPath, [modDirectory]);
            var builder = fixture.CreateBuilder(scanner, _ => physicalPaths);

            _ = builder.RebuildAsync(MorphFaceGame.LE3).GetAwaiter().GetResult();
            var mesh = fixture.Store.Read(MorphFaceGame.LE3).AttachmentMeshes.Single();
            TestAssert.Equal(fullPath, mesh.CanonicalPath);
            TestAssert.Equal(1, mesh.Occurrences.Count);
            TestAssert.Equal(Path.GetFullPath(biogPackage), mesh.EffectiveOccurrence.PackagePath);
            TestAssert.Equal(shortPath, mesh.EffectiveOccurrence.InstancedPath);
            TestAssert.Equal(1, fixture.Store.ReadMods(MorphFaceGame.LE3).AttachmentMeshes.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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

    private static void InstalledAssetIndexCompactsAndRoundTrips()
    {
        using var fixture = RegistryFixture.Create();
        var packages = Enumerable.Range(0, 4)
            .Select(index => $"C:\\Games\\Mass Effect\\Game\\LE3\\BioGame\\DLC\\DLC_MOD_LongPackageName{index}\\CookedPCConsole\\BIOG_HMF_HIR_PRO.pcc")
            .ToArray();
        var assets = Enumerable.Range(0, 8000)
            .Select(index => new InstalledAssetOccurrence(
                $"BIOG_HMF_HIR_PRO.Hair.CustomHair_{index:D5}",
                index % 3 == 0 ? "SkeletalMesh" : "Texture2D",
                packages[index % packages.Length],
                index + 1,
                index % packages.Length * 100,
                TextureCatalogOrigin.OfficialDlc))
            .ToArray();
        var flatSize = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(assets).LongLength;

        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE3) with { Assets = assets });

        var roundTrip = fixture.Store.Read(MorphFaceGame.LE3).Assets;
        TestAssert.True(assets.SequenceEqual(roundTrip),
            "Compact asset serialization changed an installed class/path/package/export identity.");
        using var input = File.OpenRead(fixture.Paths.GetPath(MorphFaceGame.LE3));
        using var reader = new BinaryReader(input);
        _ = reader.ReadUInt32();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        var compactSize = reader.ReadInt64();
        TestAssert.True(compactSize < flatSize * 3 / 4,
            $"The all-occurrence index was not compact enough ({compactSize} raw bytes vs {flatSize} flat bytes).");
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

    private static void LegacyManualAssetsSurvive()
    {
        using var fixture = RegistryFixture.Create();
        foreach (var version in new[] { 10, 11, 12 })
        {
            var manual = Snapshot(TextureCatalogGame.LE1) with
            {
                SchemaVersion = version,
                ManualAssets = [new ManualRegistryAsset("Custom.pcc", 42, "Hair.Custom", "SkeletalMesh")],
                Assets = [new InstalledAssetOccurrence("BIOG_HMF_HIR_PRO.Hair.Custom", "SkeletalMesh",
                    "BIOG_HMF_HIR_PRO.pcc", 43, 0, TextureCatalogOrigin.BaseGame)]
            };
            var path = fixture.Paths.GetManualPath(MorphFaceGame.LE1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manual);
            using (var output = File.Create(path))
            {
                using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(TextureRegistryStore.Magic);
                    writer.Write(version);
                    writer.Write((int)MorphFaceGame.LE1);
                    writer.Write((long)json.Length);
                }
                using var compressed = new System.IO.Compression.BrotliStream(output,
                    System.IO.Compression.CompressionLevel.Optimal);
                compressed.Write(json);
            }
            TestAssert.Equal(TextureRegistryState.Ready, fixture.Store.GetManualStatus(MorphFaceGame.LE1).State);
            var migrated = fixture.Store.ReadManual(MorphFaceGame.LE1);
            TestAssert.Equal(TextureRegistrySnapshot.CurrentSchemaVersion, migrated.SchemaVersion);
            TestAssert.Equal(manual.ManualAssets.Single(), migrated.ManualAssets.Single());
            TestAssert.Equal(manual.Assets.Single(), migrated.Assets.Single());
            if (version == 12)
            {
                File.Copy(path, fixture.Paths.GetPath(MorphFaceGame.LE1));
                TestAssert.Equal(TextureRegistryState.Outdated, fixture.Store.GetStatus(MorphFaceGame.LE1).State);
            }
        }
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

    private static void FailedPairWritePreservesBoth()
    {
        using var fixture = RegistryFixture.Create();
        var originalInstalled = Snapshot(TextureCatalogGame.LE1) with { InstalledPackageCount = 11 };
        var originalMods = Snapshot(TextureCatalogGame.LE1) with { InstalledPackageCount = 22 };
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

    private static void SharedCacheReusesSnapshot()
    {
        using var fixture = RegistryFixture.Create();
        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1));
        var first = fixture.Store.ReadWithStatus(MorphFaceGame.LE1).Snapshot!;
        var secondStore = new TextureRegistryStore(fixture.Paths);
        var second = secondStore.ReadWithStatus(MorphFaceGame.LE1).Snapshot!;
        TestAssert.True(ReferenceEquals(first, second),
            "Separate store instances reparsed an unchanged registry file.");

        fixture.Store.WriteAtomic(Snapshot(TextureCatalogGame.LE1) with { Candidates = [] });
        var refreshed = secondStore.ReadWithStatus(MorphFaceGame.LE1).Snapshot!;
        TestAssert.True(!ReferenceEquals(first, refreshed),
            "A changed registry file reused the old parsed snapshot.");
        TestAssert.Equal(0, refreshed.Candidates.Count);
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
        IReadOnlyDictionary<string, IReadOnlyList<AttachmentMeshOccurrence>>? meshes = null,
        IReadOnlyDictionary<string, IReadOnlyList<InstalledAssetOccurrence>>? assets = null) : ITextureRegistryPackageScanner
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<TextureRegistryScannedTexture>> _results =
            results ?? new Dictionary<string, IReadOnlyList<TextureRegistryScannedTexture>>();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<MorphFaceTemplateCandidate>> _templates =
            templates ?? new Dictionary<string, IReadOnlyList<MorphFaceTemplateCandidate>>();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<AttachmentMeshOccurrence>> _meshes =
            meshes ?? new Dictionary<string, IReadOnlyList<AttachmentMeshOccurrence>>();
        private readonly IReadOnlyDictionary<string, IReadOnlyList<InstalledAssetOccurrence>> _assets =
            assets ?? new Dictionary<string, IReadOnlyList<InstalledAssetOccurrence>>();

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
                AttachmentMeshes = _meshes.GetValueOrDefault(packagePath) ?? [],
                Assets = _assets.GetValueOrDefault(packagePath) ?? []
            };
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }
}
