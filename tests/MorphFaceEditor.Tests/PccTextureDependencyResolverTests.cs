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
        new("PCC texture resolver: package-relative BIOG paths become PCC identities", RelativeBiogPathBecomesPccIdentity),
        new("D1 installed non-corpus BIOG texture becomes a package-qualified PCC export", InstalledNonCorpusTextureUsesDonorIdentity),
        new("D1 arbitrary custom texture materialises without corpus knowledge", ArbitraryCustomTextureUsesDonorIdentity),
        new("PCC texture resolver: exact path never remaps by object name", ExactPathNeverRemapsByObjectName),
        new("PCC texture resolver: human falls back to character creator", HumanFallsBackToCharacterCreator),
        new("PCC texture resolver: alien retains effective occurrence", AlienRetainsEffectiveOccurrence),
        new("PCC texture resolver: duplicate object names do not make exact identity ambiguous", DuplicateObjectNamesDoNotAffectExactIdentity),
        new("PCC texture materializer: external mip storage survives import", ExternalMipStorageSurvivesImport)
    ];

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

        var candidateAndOccurrence = store.Read(MorphFaceGame.LE2).Candidates
            .SelectMany(candidate => candidate.Occurrences
                .Where(occurrence => PccTextureDependencyResolver.IsSeekfreePackage(occurrence.PackagePath))
                .Select(occurrence => (Candidate: candidate, Occurrence: occurrence)))
            .Where(value => !value.Candidate.InstancedPath.StartsWith("BIO", StringComparison.OrdinalIgnoreCase))
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
                value.TargetPath) == MaterialOracleEntryKind.Unknown);
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
        TestAssert.True(
            destination.FindExport(candidateAndOccurrence.Candidate.InstancedPath, "Texture2D") is null,
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
                var parent = donor.CreatePackageExport("CustomTextures");
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
                donor.Save(donorPath);
            }

            using (var donor = MEPackageHandler.OpenMEPackage(donorPath, forceLoadFromDisk: true))
            using (var destination = MEPackageHandler.CreateMemoryEmptyPackage(outputPath, MEGame.LE2))
            {
                const string localPath = "CustomTextures.Ryan_D1_Arbitrary_Diff";
                var sourceTexture = donor.FindExport(localPath, "Texture2D")
                                    ?? throw new InvalidDataException("The saved custom donor texture is missing.");
                var targetPath = PccAssetPathPolicy.FromDonorOccurrence(localPath, donorPath);
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
            TestAssert.True(reopened.FindExport(expectedPath, "Texture2D") is not null,
                "The package-qualified arbitrary texture did not survive save/reopen.");
            PackageIntegrity.Verify(reopened);
        }
        finally
        {
            if (File.Exists(donorPath)) File.Delete(donorPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
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
        var candidate = new TextureCatalogCandidate(
            TextureCatalogGame.LE3,
            path,
            effective,
            [effective, alternate]);

        var resolved = PccTextureDependencyResolver.Resolve(
            new AssetIdentity(string.Empty, path, 1, "Texture2D"),
            [candidate],
            preferBiog: false);

        TestAssert.Equal(Path.GetFileName(effective.PackagePath), resolved.Occurrence.PackageName);

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
            source.InstancedFullPath,
            occurrence,
            [occurrence]);

        using var destination = MEPackageHandler.CreateMemoryEmptyPackage(
            "PccTextureStorageTest.pcc", MEGame.LE2);
        var imported = PccTextureDependencyResolver.Materialize(
            destination,
            new AssetIdentity(donorPath, source.InstancedFullPath, source.UIndex, "Texture2D"),
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
        new(TextureCatalogGame.LE3, path, occurrences[0], occurrences);

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
