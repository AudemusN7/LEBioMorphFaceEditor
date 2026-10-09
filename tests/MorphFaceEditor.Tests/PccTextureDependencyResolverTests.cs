using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;

namespace MorphFaceEditor.Tests;

/// <summary>Focused policy tests for PCC texture donor selection.</summary>
public static class PccTextureDependencyResolverTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("PCC texture resolver: human prefers exact BIOG donor", HumanPrefersBiog),
        new("PCC texture resolver: highest official exact donor beats a lower BIOG alias", BiogAliasBeatsCookedLevel),
        new("PCC texture resolver: a higher-mounted mod cannot replace the official donor", OfficialDonorBeatsMod),
        new("PCC texture resolver: shadowed official occurrences are excluded", ShadowedOfficialIsExcluded),
        new("PCC texture resolver: package-relative BIOG paths become PCC identities", RelativeBiogPathBecomesPccIdentity),
        new("D1 installed non-corpus BIOG texture becomes a package-qualified PCC export", InstalledNonCorpusTextureUsesDonorIdentity),
        new("D1 arbitrary custom texture materialises without corpus knowledge", ArbitraryCustomTextureUsesDonorIdentity),
        new("PCC direct texture materializer honors explicit donor intent", DirectMaterializerHonorsSelection),
        new("PCC direct texture materializer rejects a non-texture donor", DirectMaterializerRejectsNonTexture),
        new("PCC texture resolver: exact path never remaps by object name", ExactPathNeverRemapsByObjectName),
        new("PCC texture resolver: human falls back to character creator", HumanFallsBackToCharacterCreator),
        new("PCC texture resolver: alien uses the highest eligible official occurrence", AlienRetainsEffectiveOccurrence),
        new("PCC texture resolver: duplicate object names do not make exact identity ambiguous", DuplicateObjectNamesDoNotAffectExactIdentity),
        new("PCC texture materializer: external mip storage survives import", ExternalMipStorageSurvivesImport)
    ];

    private static void DirectMaterializerRejectsNonTexture()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"MFE-NonTexture-{Guid.NewGuid():N}.pcc");
        try
        {
            using (var source = MEPackageHandler.CreateMemoryEmptyPackage(sourcePath, MEGame.LE3))
            {
                var parent = source.CreatePackageExport("MFE_NotTexture");
                var mesh = source.CreateExport("Mesh", "SkeletalMesh", parent, indexed: false);
                mesh.WriteBinary(LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh.Create());
                source.Save(sourcePath);
            }
            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("MFE-TextureTarget.pcc", MEGame.LE3);
            try
            {
                PccTextureDependencyResolver.MaterializeDirect(destination,
                    new AssetIdentity(sourcePath, "MFE_NotTexture.Mesh", 2, "SkeletalMesh")
                        { IsExplicitTextureDonor = true });
                throw new InvalidOperationException("A skeletal mesh was accepted as a texture donor.");
            }
            catch (InvalidDataException exception)
            {
                TestAssert.True(exception.Message.Contains("Texture2D"), "The texture class rejection was unclear.");
            }
            TestAssert.Equal(0, destination.ExportCount);
            TestAssert.Equal(0, destination.ImportCount);
        }
        finally { File.Delete(sourcePath); }
    }

    private static void HumanPrefersBiog()
    {
        const string path = "BIOG_HMF_HED_PROMorph_R.PROBase.HMF_HED_PROBase_Diff";
        var seekfree = Occurrence("BIOG_HMF_HED_PROMorph_R.pcc", 100);
        var characterCreator = Occurrence("BioP_Char.pcc", 10);
        var candidate = Candidate(path, seekfree, characterCreator);

        var resolved = PccTextureDependencyResolver.Resolve(
            new AssetIdentity("BIOG_HMF_HED_PROMorph_R.pcc", path, 1, "Texture2D"),
            [candidate],
            preferBiog: true);

        TestAssert.Equal(Path.GetFileName("BIOG_HMF_HED_PROMorph_R.pcc"), resolved.Occurrence.PackageName);
    }

    private static void BiogAliasBeatsCookedLevel()
    {
        const string shortPath = "Hair.HMF_HIR_Test_Diff";
        const string fullPath = "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Test_Diff";
        var biog = Candidate(shortPath, Occurrence("BIOG_HMF_HIR_PRO.pcc", 0));
        var level = Candidate(fullPath, Occurrence("BIOA_TEST.pcc", 9000));

        var resolved = PccTextureDependencyResolver.Resolve(
            new AssetIdentity("BIOA_TEST.pcc", fullPath, 2, "Texture2D"),
            [level, biog], preferBiog: true);
        TestAssert.Equal("BIOA_TEST.pcc", resolved.Occurrence.PackagePath);
        TestAssert.Equal(fullPath, resolved.InstancedPath);
    }

    private static void OfficialDonorBeatsMod()
    {
        const string path = "BIOG_Test.Asset";
        var official = Occurrence("BIOG_Test.pcc", 10) with { Origin = TextureCatalogOrigin.BaseGame };
        var mod = Occurrence("BIOG_Test_Mod.pcc", 10000) with { Origin = TextureCatalogOrigin.Mod };
        var candidate = Candidate(path, mod, official);
        var resolved = PccTextureDependencyResolver.Resolve(
            new AssetIdentity("BIOG_Test_Mod.pcc", path, 5, "Texture2D"),
            [candidate],
            preferBiog: false);
        TestAssert.Equal("BIOG_Test.pcc", resolved.Occurrence.PackageName);
    }

    private static void HumanFallsBackToCharacterCreator()
    {
        const string path = "HMM_HED_PROBase.PROBase.HMM_HED_PROBase_Diff";
        var characterCreator = Occurrence("BioP_Char.pcc", 100);
        var candidate = Candidate(path, characterCreator);

        var resolved = PccTextureDependencyResolver.Resolve(
            new AssetIdentity("Missing.pcc", path, 1, "Texture2D"),
            [candidate],
            preferBiog: true);

        TestAssert.Equal(Path.GetFileName("BioP_Char.pcc"), resolved.Occurrence.PackageName);
    }

    private static void ShadowedOfficialIsExcluded()
    {
        const string path = "BIOG_Test.Asset";
        var shadowed = Candidate(path, Occurrence("BIOG_Test.pcc", 0)) with
        {
            IsEligibleOfficialDefault = false
        };
        try
        {
            _ = PccTextureDependencyResolver.Resolve(
                new AssetIdentity(string.Empty, path, 0, "Texture2D"), [shadowed], preferBiog: true);
            throw new Exception("A filename-shadowed official copy became a default donor.");
        }
        catch (KeyNotFoundException)
        {
        }
    }

    private static void RelativeBiogPathBecomesPccIdentity()
    {
        const string localPath = "Hair_CrewCut.HMF_HIR_Cru_Diff";
        var seekfree = Occurrence("BIOG_HMF_HIR_PRO.pcc", 100);
        var characterCreator = Occurrence("BioP_Char.pcc", 10);
        var candidate = Candidate(localPath, seekfree, characterCreator);

        var resolved = PccTextureDependencyResolver.Resolve(
            new AssetIdentity("BioP_Char.pcc", localPath, 1, "Texture2D"),
            [candidate],
            preferBiog: true);

        TestAssert.Equal("BIOG_HMF_HIR_PRO.Hair_CrewCut.HMF_HIR_Cru_Diff", resolved.InstancedPath);
        TestAssert.Equal(Path.GetFileName("BIOG_HMF_HIR_PRO.pcc"), resolved.Occurrence.PackageName);

        var alreadyQualified = PccTextureDependencyResolver.Resolve(
            new AssetIdentity(
                "BioP_Char.pcc",
                "BIOG_HMF_HIR_PRO.Hair_CrewCut.HMF_HIR_Cru_Diff",
                1,
                "Texture2D"),
            [candidate],
            preferBiog: true);
        TestAssert.Equal(resolved.InstancedPath, alreadyQualified.InstancedPath);
        TestAssert.Equal(resolved.Occurrence.PackagePath, alreadyQualified.Occurrence.PackagePath);
    }

    private static void InstalledNonCorpusTextureUsesDonorIdentity()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(MorphFaceGame.LE2).State != TextureRegistryState.Ready)
        {
            throw new InvalidOperationException("The LE2 texture database is required for the D1 non-corpus texture test.");
        }

        var catalog = new TextureCatalogService(store).ReadAsync(MorphFaceGame.LE2).GetAwaiter().GetResult();
        using var donorCache = new PackageCache { CacheMaxSize = 8 };
        var candidateAndOccurrence = catalog.Candidates
            .Where(candidate => candidate.IsEligibleOfficialDefault)
            .Select(candidate => (Candidate: candidate, Occurrence: candidate.EffectiveOccurrence))
            .Where(value => PccTextureDependencyResolver.IsSeekfreePackage(value.Occurrence.PackagePath))
            .Where(value => File.Exists(value.Occurrence.PackagePath))
            .Select(value => (
                value.Candidate,
                value.Occurrence,
                TargetPath: PccAssetPathPolicy.FromDonorOccurrence(
                    value.Candidate.InstancedPath,
                    value.Occurrence.PackagePath)))
            .FirstOrDefault(value => MaterialDependencyOracle.Instance.GetKind(
                MEGame.LE2,
                "Texture2D",
                value.TargetPath) == MaterialOracleEntryKind.Unknown &&
                !InstalledGameAssetResolver.RequireExact(donorCache, value.Occurrence.PackagePath,
                    value.TargetPath, "Texture2D", value.Occurrence.ExportUIndex).InstancedFullPath
                    .Equals(value.TargetPath, StringComparison.OrdinalIgnoreCase));
        if (candidateAndOccurrence.Candidate is null)
        {
            throw new InvalidOperationException(
                "The LE2 registry contains no package-relative BIOG texture outside the Global Morphs oracle.");
        }

        var resolved = PccTextureDependencyResolver.Resolve(
            new AssetIdentity(
                candidateAndOccurrence.Occurrence.PackagePath,
                candidateAndOccurrence.Candidate.InstancedPath,
                candidateAndOccurrence.Occurrence.ExportUIndex,
                "Texture2D"),
            [candidateAndOccurrence.Candidate],
            preferBiog: true);
        TestAssert.Equal(candidateAndOccurrence.TargetPath, resolved.InstancedPath);

        using var destination = MEPackageHandler.CreateMemoryEmptyPackage(
            "D1InstalledNonCorpusTexture.pcc",
            MEGame.LE2);
        var materialised = PccTextureDependencyResolver.Materialize(
            destination,
            new AssetIdentity(
                candidateAndOccurrence.Occurrence.PackagePath,
                candidateAndOccurrence.Candidate.InstancedPath,
                candidateAndOccurrence.Occurrence.ExportUIndex,
                "Texture2D"),
            [candidateAndOccurrence.Candidate],
            preferBiog: true);
        TestAssert.Equal(candidateAndOccurrence.TargetPath, materialised.InstancedFullPath);
        var localPath = InstalledGameAssetResolver.RequireExact(donorCache, candidateAndOccurrence.Occurrence.PackagePath,
            candidateAndOccurrence.TargetPath, "Texture2D", candidateAndOccurrence.Occurrence.ExportUIndex).InstancedFullPath;
        TestAssert.True(
            destination.FindExport(localPath, "Texture2D") is null,
            "The PCC retained an unqualified duplicate of the installed BIOG texture.");
    }

    private static void ArbitraryCustomTextureUsesDonorIdentity()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-D1-CustomTexture-{Guid.NewGuid():N}.pcc");
        var outputPath = Path.Combine(Path.GetTempPath(), $"MFE-D1-CustomTextureOutput-{Guid.NewGuid():N}.pcc");
        try
        {
            using (var source = MEPackageHandler.OpenMEPackage(
                       Path.GetFullPath("tests/Global Morphs/LE2 GlobalMorphs.pcc"),
                       forceLoadFromDisk: true))
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage(donorPath, MEGame.LE2))
            {
                var sourceTexture = source.Exports.First(export =>
                    export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                    new Texture2D(export).Mips.Any(mip => mip.storageType != StorageTypes.empty));
                var parent = donor.CreatePackageExport("CustomTextures", forcedExport: false);
                var imported = EntryImporter.ImportExport(
                    donor,
                    sourceTexture,
                    parent.UIndex,
                    new RelinkerOptionsPackage
                    {
                        ImportExportDependencies = false,
                        GenerateImportsForGlobalFiles = false
                    }) as ExportEntry ?? throw new InvalidDataException("The custom donor texture was not cloned.");
                imported.ObjectName = new NameReference("Ryan_D1_Arbitrary_Diff");
                // This fixture is package-local, unlike its global corpus donor.
                imported.ExportFlags &= ~UnrealFlags.EExportFlags.ForcedExport;
                donor.Save(donorPath);
            }

            using (var donor = MEPackageHandler.OpenMEPackage(donorPath, forceLoadFromDisk: true))
            using (var destination = MEPackageHandler.CreateMemoryEmptyPackage(outputPath, MEGame.LE2))
            {
                const string localPath = "CustomTextures.Ryan_D1_Arbitrary_Diff";
                var sourceTexture = donor.FindExport(localPath, "Texture2D")
                                    ?? throw new InvalidDataException("The saved custom donor texture is missing.");
                var targetPath = PccAssetPathPolicy.FromDonorOccurrence(localPath, donorPath);
                TestAssert.True(!sourceTexture.IsForcedExport,
                    "The custom fixture must remain package-local through its ancestor chain.");
                TestAssert.Equal(targetPath, sourceTexture.MemoryFullPath);
                TestAssert.Equal(
                    MaterialOracleEntryKind.Unknown,
                    MaterialDependencyOracle.Instance.GetKind(MEGame.LE2, "Texture2D", targetPath));
                var materialised = PccTextureDependencyResolver.MaterializeDirect(
                    destination,
                    new AssetIdentity(donorPath, localPath, sourceTexture.UIndex, "Texture2D"));
                TestAssert.Equal(targetPath, materialised.InstancedFullPath);
                TestAssert.True(destination.FindExport(localPath, "Texture2D") is null,
                    "The custom texture was duplicated at its package-relative path.");
                destination.Save(outputPath);
            }

            using var reopened = MEPackageHandler.OpenMEPackage(outputPath, forceLoadFromDisk: true);
            var expectedPath = PccAssetPathPolicy.FromDonorOccurrence(
                "CustomTextures.Ryan_D1_Arbitrary_Diff",
                donorPath);
            var savedTexture = reopened.FindExport(expectedPath, "Texture2D");
            TestAssert.True(savedTexture is not null,
                "The package-qualified arbitrary texture did not survive save/reopen.");
            TestAssert.True(savedTexture!.IsForcedExport,
                "The embedded custom texture must retain its original donor identity.");
            TestAssert.Equal(expectedPath, savedTexture.MemoryFullPath);
            PackageIntegrity.Verify(reopened);
        }
        finally
        {
            if (File.Exists(donorPath)) File.Delete(donorPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    private static void DirectMaterializerHonorsSelection()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        var catalog = new TextureCatalogService(store).ReadAsync(MorphFaceGame.LE2).GetAwaiter().GetResult();
        TestAssert.True(catalog.IsAvailable, "The LE2 asset database is required.");
        var candidate = catalog.Candidates.First(value => value.IsEligibleOfficialDefault &&
            value.ObjectName.EndsWith("_Diff", StringComparison.OrdinalIgnoreCase));
        var selectedPath = Path.Combine(Path.GetTempPath(), $"MFE-SelectedTexture-{Guid.NewGuid():N}.pcc");
        try
        {
            using var cache = new PackageCache();
            var official = new InstalledGameAssetResolver().Resolve(
                MEGame.LE2, candidate.InstancedPath, "Texture2D", cache)!;
            using (var selected = MEPackageHandler.CreateMemoryEmptyPackage(selectedPath, MEGame.LE2))
            {
                var parent = PackageIntegrity.EnsurePackagePath(selected, candidate.InstancedPath, "Texture2D");
                var copy = EntryImporter.ImportExport(selected, official, parent?.UIndex ?? 0,
                    new RelinkerOptionsPackage { ImportExportDependencies = false, GenerateImportsForGlobalFiles = false })
                    as ExportEntry ?? throw new Exception("The selected texture fixture was not cloned.");
                copy.WriteProperty(new IntProperty(9000, "MFE_DonorMarker"));
                selected.Save(selectedPath);
            }
            using var selectedDonor = MEPackageHandler.OpenMEPackage(selectedPath, forceLoadFromDisk: true);
            var texture = selectedDonor.FindExport(candidate.InstancedPath, "Texture2D")!;
            var identity = new AssetIdentity(selectedPath, candidate.InstancedPath, texture.UIndex, "Texture2D");
            using var automaticOutput = MEPackageHandler.CreateMemoryEmptyPackage("AutomaticTexture.pcc", MEGame.LE2);
            var automatic = PccTextureDependencyResolver.MaterializeDirect(automaticOutput, identity);
            TestAssert.True(automatic.GetProperty<IntProperty>("MFE_DonorMarker") is null,
                "An unselected external texture replaced the official donor.");
            using var explicitOutput = MEPackageHandler.CreateMemoryEmptyPackage("SelectedTexture.pcc", MEGame.LE2);
            var explicitTexture = PccTextureDependencyResolver.MaterializeDirect(
                explicitOutput, identity with { IsExplicitTextureDonor = true });
            TestAssert.Equal(9000, explicitTexture.GetProperty<IntProperty>("MFE_DonorMarker")!.Value);
            PackageIntegrity.Verify(automaticOutput);
            PackageIntegrity.Verify(explicitOutput);
        }
        finally
        {
            if (File.Exists(selectedPath)) File.Delete(selectedPath);
        }
    }

    private static void ExactPathNeverRemapsByObjectName()
    {
        const string seekfreePath = "BIOG_HMF_HED_PROMorph_R.PROBase.HMF_HED_PROBase_Diff";
        const string characterCreatorPath = "HMF_HED_PROBase.PROBase.HMF_HED_PROBase_Diff";
        var seekfree = Candidate(
            seekfreePath,
            Occurrence("BIOG_HMF_HED_PROMorph_R.pcc", 100));
        var characterCreator = Candidate(
            characterCreatorPath,
            Occurrence("BioP_Char.pcc", 10));

        try
        {
            _ = PccTextureDependencyResolver.Resolve(
                new AssetIdentity("Missing.pcc", "Missing.HMF_HED_PROBase_Diff", 1, "Texture2D"),
                [seekfree, characterCreator],
                preferBiog: true);
            throw new Exception("An absent exact texture path was silently remapped by object name.");
        }
        catch (KeyNotFoundException)
        {
            // Exact logical identity is mandatory.
        }
    }

    private static void AlienRetainsEffectiveOccurrence()
    {
        const string path = "BIOG_TUR_HED_PROMorph_R.PROBase.TUR_HED_PROBase_Diff";
        var effective = Occurrence("BIOG_TUR_HED_PROMorph_R.pcc", 100);
        var alternate = Occurrence("BioP_Char.pcc", 200);
        var candidate = Candidate(path, effective, alternate);

        var resolved = PccTextureDependencyResolver.Resolve(
            new AssetIdentity(string.Empty, path, 1, "Texture2D"),
            [candidate],
            preferBiog: false);

        TestAssert.Equal(Path.GetFileName(alternate.PackagePath), resolved.Occurrence.PackageName);

        var explicitlySelected = PccTextureDependencyResolver.Resolve(
            new AssetIdentity(alternate.PackagePath, path, 1, "Texture2D"),
            [candidate],
            preferBiog: false);
        TestAssert.Equal(Path.GetFileName(alternate.PackagePath), explicitlySelected.Occurrence.PackageName);
    }

    private static void DuplicateObjectNamesDoNotAffectExactIdentity()
    {
        var first = Candidate(
            "BIOG_HMF_HED_PROMorph_R.A.HMF_Unknown_Diff",
            Occurrence("BIOG_TUR_HED_PROMorph_R.pcc", 100));
        var second = Candidate(
            "HMF_HED_PROBase.A.HMF_Unknown_Diff",
            Occurrence("BIOG_KRO_HED_PROMorph_R.pcc", 100));

        var resolved = PccTextureDependencyResolver.Resolve(
            new AssetIdentity(string.Empty, first.InstancedPath, 0, "Texture2D"),
            [first, second],
            preferBiog: false);
        TestAssert.Equal(first.InstancedPath, resolved.InstancedPath);
    }

    private static void ExternalMipStorageSurvivesImport()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cookedPath = LegendaryExplorerCoreRuntime.DefaultLe2CookedPath;
        var donorPath = cookedPath is null ? string.Empty : Path.Combine(cookedPath, "BioP_Char.pcc");
        if (!File.Exists(donorPath))
        {
            // Installed-game coverage is optional on machines that only have the
            // test corpus; policy tests above still run in that environment.
            return;
        }

        using var donor = MEPackageHandler.OpenMEPackage(donorPath, forceLoadFromDisk: true);
        var source = donor.Exports.FirstOrDefault(export =>
            export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
            export.InstancedFullPath.Contains("HMF_", StringComparison.OrdinalIgnoreCase) &&
            new Texture2D(export).Mips.Any(mip =>
                mip.storageType != StorageTypes.empty && !mip.IsPackageStored));
        if (source is null)
        {
            return;
        }

        var sourceTexture = new Texture2D(source);
        var sourceMips = sourceTexture.Mips
            .Where(mip => mip.storageType != StorageTypes.empty)
            .ToArray();
        var occurrence = new TextureCatalogOccurrence(
            donorPath,
            source.UIndex,
            0,
            TextureCatalogOrigin.BaseGame,
            sourceTexture.GetTopMip()?.width ?? 0,
            sourceTexture.GetTopMip()?.height ?? 0,
            source.GetProperty<EnumProperty>("Format")?.Value.Name ?? "Unknown",
            source.GetProperty<EnumProperty>("LODGroup")?.Value.Name ?? "Unknown",
            true,
            source.GetProperty<NameProperty>("TextureFileCacheName")?.Value.Instanced)
        {
            Mips = sourceMips.Select(mip => new TextureMipStorageRecord(
                mip.index,
                mip.width,
                mip.height,
                (int)mip.storageType,
                mip.uncompressedSize,
                mip.compressedSize,
                mip.externalOffset,
                mip.TextureCacheName)).ToArray()
        };
        var candidate = new TextureCatalogCandidate(
            TextureCatalogGame.LE2,
            PccAssetPathPolicy.FromExport(source),
            occurrence,
            [occurrence]) { IsEligibleOfficialDefault = true };

        using var destination = MEPackageHandler.CreateMemoryEmptyPackage(
            "PccTextureStorageTest.pcc", MEGame.LE2);
        var imported = PccTextureDependencyResolver.Materialize(
            destination,
            new AssetIdentity(donorPath, candidate.InstancedPath, source.UIndex, "Texture2D"),
            [candidate],
            preferBiog: true);
        var importedTexture = new Texture2D(imported);
        var importedMips = importedTexture.Mips
            .Where(mip => mip.storageType != StorageTypes.empty)
            .ToArray();

        AssertExternalMipMetadata(sourceMips, importedMips);

        var savedPath = Path.Combine(Path.GetTempPath(), $"MFE-PccTextureStorage-{Guid.NewGuid():N}.pcc");
        try
        {
            destination.Save(savedPath);
            using var reopened = MEPackageHandler.OpenMEPackage(savedPath, forceLoadFromDisk: true);
            var reopenedEntry = reopened.FindExport(source.InstancedFullPath, "Texture2D");
            TestAssert.True(reopenedEntry is not null, "Saved PCC lost its imported Texture2D export.");
            var reopenedMips = new Texture2D(reopenedEntry!).Mips
                .Where(mip => mip.storageType != StorageTypes.empty)
                .ToArray();
            AssertExternalMipMetadata(sourceMips, reopenedMips);
        }
        finally
        {
            File.Delete(savedPath);
        }
    }

    private static void AssertExternalMipMetadata(
        IReadOnlyList<Texture2DMipInfo> sourceMips,
        IReadOnlyList<Texture2DMipInfo> importedMips)
    {
        TestAssert.Equal(sourceMips.Count, importedMips.Count);
        for (var index = 0; index < sourceMips.Count; index++)
        {
            TestAssert.Equal(sourceMips[index].storageType, importedMips[index].storageType);
            var externallyStored = ((int)sourceMips[index].storageType & (int)StorageFlags.externalFile) != 0;
            if (externallyStored)
            {
                TestAssert.Equal(sourceMips[index].externalOffset, importedMips[index].externalOffset);
                TestAssert.Equal(sourceMips[index].TextureCacheName, importedMips[index].TextureCacheName);
                TestAssert.True(!importedMips[index].IsPackageStored,
                    "PCC texture import converted an externally stored mip to package storage.");
            }
        }
    }

    private static TextureCatalogCandidate Candidate(
        string path,
        params TextureCatalogOccurrence[] occurrences) =>
        new(TextureCatalogGame.LE3, path,
            occurrences.Where(value => value.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
                .OrderByDescending(value => value.MountPriority).FirstOrDefault() ?? occurrences[0], occurrences)
        {
            IsEligibleOfficialDefault = occurrences.Any(value => value.Origin is
                TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
        };

    private static TextureCatalogOccurrence Occurrence(string packagePath, int mountPriority) =>
        new(
            packagePath,
            1,
            mountPriority,
            TextureCatalogOrigin.BaseGame,
            1024,
            1024,
            "PF_DXT5",
            "TEXTUREGROUP_Character",
            true,
            "Textures_Test");
}
