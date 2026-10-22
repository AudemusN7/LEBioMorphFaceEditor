using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LecTexture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;

using static MorphFaceEditor.Tests.PackageTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class ConversionTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("cross-game conversion requires current texture databases", ConversionRequiresTextureDatabases),
        new("unsupported optional attachment material omits its mesh with a warning", UnsupportedAttachmentMaterialIsOmitted),
        new("attachment lookup rejects a same-named mesh at a different path", AttachmentLookupRejectsSameNameWrongPath),
        new("reviewed hair proxies use exact directional donor identities", ReviewedHairProxiesAreDirectional),
        new("reviewed cross-game texture policy aliases donors and omits inert overrides", ApplyReviewedTextureTransferPolicy),
        new("later-game alien textures embed package-stored when LE1 has no stock equivalent", EmbedMissingAlienTextureIntoLe1)
    ];

    private static void ApplyReviewedTextureTransferPolicy()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var targetGame in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            TestAssert.Equal(
                "BIOG_HMM_HED_PROMorph.Normal.HMM_HED_PROBase_Face_Norm_Stack",
                CrossGameAssetReconciliationCatalog.NormalizeTargetPath(
                    targetGame,
                    "BIOG_HMM_HED_PROMorph_R.Normal.HMM_HED_PROBase_Face_Norm_Stack"));
        }
        WithPackageCopy("LE3 GlobalMorphs.pcc", le3Destination =>
        {
            using var package = MEPackageHandler.OpenMEPackage(le3Destination, forceLoadFromDisk: true);
            var source = MaterialWithTextures(
                ("HAIR_Diff", "BIOG_HMF_HIR_PRO.Afro.HMF_HIR_Afr_Diff"),
                ("HAIR_Mask", "BIOG_HMF_HIR_PRO.Afro.HMF_HIR_Afr_Mask"),
                ("HAIR_Tang", "BIOG_HMF_HIR_PRO.Afro.HMF_HIR_Afr_Tang"),
                ("HED_Scalp_Diff", "BIOG_HMF_HED_PROMorph_R.PROAshley.HMF_HED_PROAshley_Scalp_Stack"),
                ("HED_Scalp_SpecShift", "BIOG_HMF_HIR_PRO.Human.HMF_HIR_PROAll_SpecShift"));

            var result = TransferTextures(
                package,
                source,
                MEGame.LE1,
                "le1-human-female",
                FixturePath("LE1 GlobalMorphs.pcc"),
                FixturePath("LE3 GlobalMorphs.pcc"));

            TestAssert.Equal(0, result.MaterialData.Textures.Count);
        });

        WithPackageCopy("LE3 GlobalMorphs.pcc", le3Destination =>
        {
            using var package = MEPackageHandler.OpenMEPackage(le3Destination, forceLoadFromDisk: true);
            var result = TransferTextures(
                package,
                MaterialWithTextures(
                    ("HED_Lash_Diff", "BIOG_HMF_HED_PROMorph_R.Average.HMF_HED_PROLash_Opac_M01"),
                    ("HED_Scalp_Spec", "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_White")),
                MEGame.LE1,
                "le1-human-female",
                FixturePath("LE1 GlobalMorphs.pcc"),
                FixturePath("LE3 GlobalMorphs.pcc"));

            TestAssert.Equal(
                "BIOG_Humanoid_MASTER_MTR_R.Human.HMF_HED_PROLash_Opac_M01",
                result.MaterialData.Textures.Single(value => value.Name == "HED_Lash_Diff")
                    .TextureReference?.InstancedPath);

            result = TransferTextures(
                package,
                MaterialWithTextures(("HED_Scalp_Spec", "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_White")),
                MEGame.LE2,
                "le2-human-female",
                FixturePath("LE2 GlobalMorphs.pcc"),
                FixturePath("LE3 GlobalMorphs.pcc"));
            TestAssert.Equal(
                "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black",
                result.MaterialData.Textures.Single().TextureReference?.InstancedPath);
        });

        WithPackageCopy("LE1 GlobalMorphs.pcc", le1Destination =>
        {
            using var package = MEPackageHandler.OpenMEPackage(le1Destination, forceLoadFromDisk: true);
            var result = TransferTextures(
                package,
                MaterialWithTextures(("Hair_Colour", "BIOG_HMF_HIR_PRO.Human.GBL_HMF_HIR_White")),
                MEGame.LE2,
                "le2-human-female",
                FixturePath("LE2 GlobalMorphs.pcc"),
                FixturePath("LE1 GlobalMorphs.pcc"));

            TestAssert.Equal(1, result.MaterialData.Textures.Count);
            TestAssert.Equal(
                "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_White",
                result.MaterialData.Textures[0].TextureReference?.InstancedPath);
        });

        WithPackageCopy("LE2 GlobalMorphs.pcc", le2Destination =>
        {
            using var package = MEPackageHandler.OpenMEPackage(le2Destination, forceLoadFromDisk: true);
            var result = TransferTextures(
                package,
                MaterialWithTextures(
                    ("HED_Diff", "BIOG_HMM_HED_PROMorph_R.Joker.HMM_HED_PROJoker_FaceSD_Diff"),
                    ("HED_Scalp_Diff", "BIOG_HMM_HED_PROMorph_R.Joker.HMM_HED_PROJoker_ScalpSD_Diff"),
                    ("HED_Scalp_Norm", "BIOG_HMF_HED_PROMorph_R.Normal.HMF_HED_PROBase_Scalp_Bald_Norm")),
                MEGame.LE3,
                "le3-human-male",
                FixturePath("LE3 GlobalMorphs.pcc"),
                FixturePath("LE2 GlobalMorphs.pcc"));

            TestAssert.Equal(2, result.MaterialData.Textures.Count);
            TestAssert.Equal(
                "BIOG_HMM_HED_PROMorph.Joker.HMM_HED_PROJoker_Face_Diff_Stack",
                result.MaterialData.Textures.Single(value => value.Name == "HED_Diff").TextureReference?.InstancedPath);
            TestAssert.Equal(
                "BIOG_HMM_HED_PROMorph.Joker.HMM_HED_PROJoker_Scalp_Diff_Stack",
                result.MaterialData.Textures.Single(value => value.Name == "HED_Scalp_Diff").TextureReference?.InstancedPath);
        });
    }

    private static void ConversionRequiresTextureDatabases()
    {
        var service = new MorphFaceConversionService(
            MorphFaceProfileRegistry.CreateDefault(),
            new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            TestFixtures.CreateMissingTextureCatalogService());
        var destination = Path.Combine(Path.GetTempPath(), $"MFE-missing-registry-{Guid.NewGuid():N}.pcc");
        try
        {
            service.Convert(new MorphFaceConversionRequest(
                FixturePath("LE1 GlobalMorphs.pcc"),
                "HMF.BIOA_PRC2_HMF_Guard01",
                MorphFaceGame.LE2,
                destination,
                CreateNewPackage: true,
                FixturePath("LE1 to LE2 GlobalMorphs.pcc")));
            throw new Exception("Cross-game conversion proceeded without a texture database.");
        }
        catch (InvalidOperationException exception)
        {
            TestAssert.True(
                exception.Message.Contains("texture database is unavailable", StringComparison.OrdinalIgnoreCase),
                $"The missing-database failure was unclear: {exception.Message}");
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static void UnsupportedAttachmentMaterialIsOmitted()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-UnsupportedAttachment-{Guid.NewGuid():N}.pcc");
        var donorPackageName = Path.GetFileNameWithoutExtension(donorPath);
        var meshPath = $"{donorPackageName}.UnsupportedAttachment";
        try
        {
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage(donorPath, MEGame.LE3))
            {
                var material = donor.CreateExport("MFE_CustomAttachmentMaterial", "Material", indexed: false);
                var mesh = donor.CreateExport("UnsupportedAttachment", "SkeletalMesh", indexed: false);
                var binary = SkeletalMesh.Create();
                binary.Materials = [material.UIndex];
                mesh.WriteBinary(binary);
                donor.Save();
            }

            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("MFE-AttachmentTarget.pcc", MEGame.LE3);
            var result = MorphFaceAttachmentTransferEngine.Transfer(
                destination,
                null,
                [new AssetIdentity(donorPath, meshPath, 0, "SkeletalMesh")],
                MEGame.LE3,
                donorPath);

            TestAssert.True(result.OtherMeshes.Count == 0,
                "An attachment with a material outside the canonical material oracle was retained.");
            TestAssert.True(result.Warnings.Any(value =>
                    value.Contains("unsupported material", StringComparison.OrdinalIgnoreCase) &&
                    value.Contains("MFE_CustomAttachmentMaterial", StringComparison.OrdinalIgnoreCase)),
                "Omitting the unsupported attachment did not explain the unsupported material.");
            TestAssert.True(destination.FindExport(meshPath, "SkeletalMesh") is null,
                "The unsupported optional attachment was materialised before being rejected.");
        }
        finally
        {
            if (File.Exists(donorPath)) File.Delete(donorPath);
        }
    }

    private static void ReviewedHairProxiesAreDirectional()
    {
        TestAssert.True(CanonicalHairDonorPolicy.TryResolve(MEGame.LE2, MEGame.LE3,
            "BIOG_HMM_HIR_PRO_R.FauxHawk.HMM_HIR_Fhwk_01_MDL", out var proxy), "Reviewed proxy was not resolved.");
        TestAssert.Equal("BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_01_MDL", proxy);
        TestAssert.True(!CanonicalHairDonorPolicy.TryResolve(MEGame.LE2, MEGame.LE3,
            "WrongPackage.FauxHawk.HMM_HIR_Fhwk_01_MDL", out _), "A same-leaf lookalike was mapped.");
        TestAssert.True(CanonicalHairDonorPolicy.TryResolve(MEGame.LE3, MEGame.LE2,
            "BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_PROMiranda_MDL", out var miranda), "LE2 Miranda was not resolved.");
        TestAssert.Equal("BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_MIR_MDL", miranda);
        TestAssert.True(CanonicalHairDonorPolicy.TryResolve(MEGame.LE3, MEGame.LE1,
            "BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_PROMiranda_MDL", out var absent) && absent is null,
            "LE2 Miranda mapping was incorrectly reused for LE1.");
        TestAssert.True(CanonicalHairDonorPolicy.TryResolve(MEGame.LE3, MEGame.LE2,
            "BIOG_HMM_HIR_PRO_R.Hair_James.HMM_HIR_PROJames_MDL", out var james) && james is null,
            "LE3 James should be an explicit destination omission.");
    }

    private static void AttachmentLookupRejectsSameNameWrongPath()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-WrongPathAttachment-{Guid.NewGuid():N}.pcc");
        var donorPackageName = Path.GetFileNameWithoutExtension(donorPath);
        var donorMeshPath = $"{donorPackageName}.Container.SameName_MDL";
        const string requestedPath = "BIOG_HMF_HIR_PRO.Beret.SameName_MDL";
        try
        {
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage(donorPath, MEGame.LE3))
            {
                var container = donor.CreatePackageExport("Container");
                var mesh = donor.CreateExport("SameName_MDL", "SkeletalMesh", container, indexed: false);
                mesh.WriteBinary(SkeletalMesh.Create());
                donor.Save();
            }

            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("MFE-WrongPathTarget.pcc", MEGame.LE3);
            var result = MorphFaceAttachmentTransferEngine.Transfer(
                destination,
                new AssetIdentity(donorPath, requestedPath, 0, "SkeletalMesh"),
                [],
                MEGame.LE3,
                donorPath);

            TestAssert.Equal(0, result.OtherMeshes.Count);
            TestAssert.True(result.Hair is null,
                "An attachment with only a same-named, different-path template export was guessed.");
            TestAssert.True(result.Warnings.Any(value =>
                    value.Contains(requestedPath, StringComparison.OrdinalIgnoreCase) &&
                    value.Contains("no exact", StringComparison.OrdinalIgnoreCase)),
                "Rejecting the wrong-path candidate did not identify the missing exact donor path.");
            TestAssert.True(destination.FindExport(requestedPath, "SkeletalMesh") is null,
                "The same-named, different-path mesh was materialised under the requested path.");
        }
        finally
        {
            if (File.Exists(donorPath)) File.Delete(donorPath);
        }
    }

    private static void EmbedMissingAlienTextureIntoLe1()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        WithPackageCopy("LE1 GlobalMorphs.pcc", le1Destination =>
        {
            using (var package = MEPackageHandler.OpenMEPackage(le1Destination, forceLoadFromDisk: true))
            {
                var result = TransferTextures(
                    package,
                    MaterialWithTextures(
                        ("HED_Tattoo", "BIOG_ASA_HED_PROMorph_R.Masks.ASA_HED_PRO_Tat2"),
                        ("HED_Add_Batarian", "BIOG_BAT_HED_PROMorph_R.PROBase.BAT_HED_PROMorph_Add4"),
                        ("HED_Add_Turian", "BIOG_TUR_HED_PROMorph_R.Add.TUR_HED_PRO_Add2")),
                    MEGame.LE3,
                    "le3-asari",
                    FixturePath("LE3 GlobalMorphs.pcc"),
                    FixturePath("LE1 GlobalMorphs.pcc"));

                TestAssert.Equal(3, result.MaterialData.Textures.Count);
                var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "BIOG_ASA_HED_PROMorph_R.Masks.ASA_HED_PRO_Tat2",
                    "BIOG_BAT_HED_PROMorph_R.PROBase.BAT_HED_PROMorph_Add4",
                    "BIOG_TUR_HED_PROMorph_R.Add.TUR_HED_PRO_Add2"
                };
                TestAssert.True(result.MaterialData.Textures.All(value =>
                        value.TextureReference is { } reference && expectedPaths.Remove(reference.InstancedPath)) &&
                    expectedPaths.Count == 0,
                    "At least one missing LE1 alien texture was not embedded at its canonical UE3 path.");
                TestAssert.True(result.Warnings.Count(value =>
                        value.Contains("package-stored", StringComparison.OrdinalIgnoreCase) &&
                        value.Contains("mod's TFC", StringComparison.OrdinalIgnoreCase)) == 3,
                    "The conversion report did not include one package-storage/TFC advisory per embedded texture.");
                package.Save();
            }

            using var reopened = MEPackageHandler.OpenMEPackage(le1Destination, forceLoadFromDisk: true);
            var embedded = reopened.Exports.Where(entry =>
                new[] { "ASA_HED_PRO_Tat2", "BAT_HED_PROMorph_Add4", "TUR_HED_PRO_Add2" }
                    .Contains(entry.ObjectNameString, StringComparer.OrdinalIgnoreCase) &&
                string.Equals(entry.ClassName, "Texture2D", StringComparison.OrdinalIgnoreCase)).ToArray();
            TestAssert.Equal(3, embedded.Length);
            TestAssert.True(embedded.All(entry => new LecTexture2D(entry).GetTopMip().IsPackageStored),
                "At least one embedded alien texture was not serialized as package-stored.");
            TestAssert.True(embedded.All(entry => entry.Parent?.ClassName == "Package"),
                "At least one embedded alien texture lost its package parent.");
        });
    }

    private static TextureTransferResult TransferTextures(
        IMEPackage destination,
        MorphFaceMaterialData source,
        MEGame sourceGame,
        string sourceProfileKey,
        string sourcePackagePath,
        string targetTemplatePackagePath) => MorphFaceTextureTransferEngine.Transfer(
            destination,
            source,
            new HashSet<string>(source.Scalars.Select(value => value.Name), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(source.Vectors.Select(value => value.Name), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(source.Textures.Select(value => value.Name), StringComparer.OrdinalIgnoreCase),
            sourceGame,
            sourceProfileKey,
            sourcePackagePath,
            targetTemplatePackagePath,
            [],
            []);

    private static MorphFaceMaterialData MaterialWithTextures(params (string Parameter, string Path)[] textures) =>
        new([], [], textures.Select(value => new TextureMaterialOverride(
            value.Parameter,
            new AssetIdentity(string.Empty, value.Path, 0, "Texture2D"))).ToArray());

}
