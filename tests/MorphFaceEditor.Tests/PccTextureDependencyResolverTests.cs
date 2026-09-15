using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;

namespace MorphFaceEditor.Tests;

/// <summary>Focused policy tests for PCC texture donor selection.</summary>
public static class PccTextureDependencyResolverTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("PCC texture resolver: human prefers exact BIOG donor", HumanPrefersBiog),
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
