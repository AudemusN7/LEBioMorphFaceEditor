using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

public static class SupportedMaterialGraphPolicyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("custom attachment MICs are supported through reviewed canonical base materials", CustomMicInheritsCanonicalBase),
        new("custom attachment MICs are supported through reviewed canonical effects materials", CustomMicInheritsCanonicalEffectsMaterial),
        new("stock Short02 LongHair and Jack MIC donors pass by canonical parent graph", StockHairMicDonorsUseCanonicalParents),
        new("custom attachment material values and texture survive package materialisation", CustomMaterialGraphRoundTrips),
        new("custom attachment MICs reject unknown roots unresolved parents and cycles", RejectsUnsupportedGraphs)
    ];

    private static void CustomMicInheritsCanonicalBase()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MFE-CustomMicBase.pcc", MEGame.LE1);
        var canonicalBase = CreateCanonicalBase(package);
        var customMic = package.CreateExport("MFE_CustomHairMic", "MaterialInstanceConstant", indexed: false);
        customMic.WriteProperty(new ObjectProperty(canonicalBase, "Parent"));
        var mesh = package.CreateExport("MFE_CustomHair", "SkeletalMesh", indexed: false);
        var binary = SkeletalMesh.Create();
        binary.Materials = [customMic.UIndex];
        mesh.WriteBinary(binary);

        TestAssert.True(MorphFaceAttachmentTransferEngine.HasSupportedMaterials(package, mesh, out var failure),
            $"An attachment with a custom MIC inheriting a reviewed stock base was rejected: {failure}");
    }

    private static void CustomMicInheritsCanonicalEffectsMaterial()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MFE-CustomMicEffects.pcc", MEGame.LE1);
        var canonicalBase = CreateCanonicalBase(package);
        var effectsPackage = package.CreatePackageExport("EffectsMaterials");
        var users = package.CreatePackageExport("Users", effectsPackage);
        var canonicalEffects = package.CreateExport(
            "HMN_HED_PRO_MASTER_HAIR_MAT_USER", "RvrEffectsMaterialUser", users, indexed: false);
        canonicalEffects.WriteProperty(new ObjectProperty(canonicalBase, "m_pBaseMaterial"));
        var customMic = package.CreateExport("MFE_CustomSkinMic", "MaterialInstanceConstant", indexed: false);
        customMic.WriteProperty(new ObjectProperty(canonicalEffects, "Parent"));

        TestAssert.True(IsSupported(package, customMic, out var failure),
            $"A custom MIC inheriting a reviewed Effects Material was rejected: {failure}");
    }

    private static void RejectsUnsupportedGraphs()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MFE-UnsupportedMaterialGraph.pcc", MEGame.LE1);
        var unknownBase = package.CreateExport("MFE_CustomBase", "Material", indexed: false);
        var unknownMic = package.CreateExport("MFE_UnknownBaseMic", "MaterialInstanceConstant", indexed: false);
        unknownMic.WriteProperty(new ObjectProperty(unknownBase, "Parent"));
        TestAssert.True(!IsSupported(package, unknownMic, out var unknownFailure) &&
                        unknownFailure.Contains("unreviewed base material", StringComparison.OrdinalIgnoreCase),
            $"An unreviewed material graph was accepted or misreported: {unknownFailure}");

        var unresolvedMic = package.CreateExport("MFE_UnresolvedMic", "MaterialInstanceConstant", indexed: false);
        unresolvedMic.WriteProperty(new ObjectProperty(0, "Parent"));
        TestAssert.True(!IsSupported(package, unresolvedMic, out var unresolvedFailure) &&
                        unresolvedFailure.Contains("unresolved Parent", StringComparison.OrdinalIgnoreCase),
            $"An unresolved material parent was accepted or misreported: {unresolvedFailure}");

        var first = package.CreateExport("MFE_CyclicMicA", "MaterialInstanceConstant", indexed: false);
        var second = package.CreateExport("MFE_CyclicMicB", "MaterialInstanceConstant", indexed: false);
        first.WriteProperty(new ObjectProperty(second, "Parent"));
        second.WriteProperty(new ObjectProperty(first, "Parent"));
        TestAssert.True(!IsSupported(package, first, out var cycleFailure) &&
                        cycleFailure.Contains("parent cycle", StringComparison.OrdinalIgnoreCase),
            $"A cyclic material graph was accepted or misreported: {cycleFailure}");

        var vfxPackage = package.CreatePackageExport("EffectsMaterials");
        var vfxUsers = package.CreatePackageExport("Users", vfxPackage);
        var vfxHairMaster = package.CreatePackageExport("HMN_HED_PRO_MASTER_HAIR_MAT_USER", vfxUsers);
        var combinedEffect = package.CreateExport("CombinedEffect_EMT_Adrenaline", "Material", vfxHairMaster, indexed: false);
        var vfxMic = package.CreateExport("MFE_VfxParentMic", "MaterialInstanceConstant", indexed: false);
        vfxMic.WriteProperty(new ObjectProperty(combinedEffect, "Parent"));
        TestAssert.True(!IsSupported(package, vfxMic, out var vfxFailure),
            $"An oracle-listed CombinedEffect dependency was accepted as a material root: {vfxFailure}");

        var canonicalBase = CreateCanonicalBase(package);
        var unknownEffects = package.CreateExport(
            "MFE_CustomEffectsUser", "RvrEffectsMaterialUser", indexed: false);
        unknownEffects.WriteProperty(new ObjectProperty(canonicalBase, "m_pBaseMaterial"));
        var unknownEffectsMic = package.CreateExport("MFE_UnknownEffectsMic", "MaterialInstanceConstant", indexed: false);
        unknownEffectsMic.WriteProperty(new ObjectProperty(unknownEffects, "Parent"));
        TestAssert.True(!IsSupported(package, unknownEffectsMic, out var unknownEffectsFailure) &&
                        unknownEffectsFailure.Contains("unreviewed effects material", StringComparison.OrdinalIgnoreCase),
            $"An unreviewed effects wrapper was accepted because it points at a canonical base: {unknownEffectsFailure}");

        using var vfxRootPackage = MEPackageHandler.CreateMemoryEmptyPackage("MFE-OracleVfxRoot.pcc", MEGame.LE3);
        var vfxMaster = CreateExportAtPath(
            vfxRootPackage, "BioVFX_Z_MATERIALS.Liquids.Liquid_Master_mat", "Material");
        var vfxMasterMic = vfxRootPackage.CreateExport("MFE_VfxMasterMic", "MaterialInstanceConstant", indexed: false);
        vfxMasterMic.WriteProperty(new ObjectProperty(vfxMaster, "Parent"));
        TestAssert.True(!IsSupported(vfxRootPackage, vfxMasterMic, out var vfxMasterFailure),
            $"An oracle-listed BioVFX material was accepted as an attachment root: {vfxMasterFailure}");

        using var stockPathPackage = MEPackageHandler.CreateMemoryEmptyPackage(
            "MFE-StockPathUnknownParent.pcc", MEGame.LE3);
        var stockPathMic = CreateExportAtPath(
            stockPathPackage,
            "BIOG_HMF_HIR_FTL_R.Materials.LongHair.HMF_HIR_FTL_LongHair_Mat1a",
            "MaterialInstanceConstant");
        var stockPathUnknownBase = stockPathPackage.CreateExport(
            "MFE_UnsupportedStockPathBase", "Material", indexed: false);
        stockPathMic.WriteProperty(new ObjectProperty(stockPathUnknownBase, "Parent"));
        var stockPathMesh = stockPathPackage.CreateExport("MFE_StockPathHair", "SkeletalMesh", indexed: false);
        var stockPathBinary = SkeletalMesh.Create();
        stockPathBinary.Materials = [stockPathMic.UIndex];
        stockPathMesh.WriteBinary(stockPathBinary);
        TestAssert.True(!MorphFaceAttachmentTransferEngine.HasSupportedMaterials(
                            stockPathPackage, stockPathMesh, out var stockPathFailure) &&
                        stockPathFailure.Contains("unreviewed base material", StringComparison.OrdinalIgnoreCase),
            $"An exported stock-path MIC with an unsupported authored parent passed the donor gate: {stockPathFailure}");

        using var hatPackage = MEPackageHandler.CreateMemoryEmptyPackage("MFE-BeretBodyDerivedParent.pcc", MEGame.LE3);
        var bodyMaterial = CreateExportAtPath(
            hatPackage, "BIOG_HMM_ARM_AND_R.Master.Andersonb_MASTER_MAT", "Material");
        var bodyEffects = CreateExportAtPath(
            hatPackage, "BIOG_HMM_ARM_AND_R.Master.Andersonb_MASTER_MAT_USER", "RvrEffectsMaterialUser");
        bodyEffects.WriteProperty(new ObjectProperty(bodyMaterial, "m_pBaseMaterial"));
        var beretMic = CreateExportAtPath(
            hatPackage, "BIOG_HMM_HIR_PRO_R.Hat_Beret.HMM_HAT_Brt_MAT_1a", "MaterialInstanceConstant");
        beretMic.WriteProperty(new ObjectProperty(bodyEffects, "Parent"));
        TestAssert.True(IsSupported(hatPackage, beretMic, out var bodyFailure),
            $"The canonical Beret chain through Anderson's shared effects/base materials was rejected: {bodyFailure}");

        using var capPackage = MEPackageHandler.CreateMemoryEmptyPackage("MFE-LE2-CapParent.pcc", MEGame.LE2);
        var cap = CreateExportAtPath(
            capPackage, "BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_MAT_2a", "Material");
        TestAssert.True(IsSupported(capPackage, cap, out var capFailure),
            $"The canonical LE2 Cap material root was rejected: {capFailure}");

        using var motoPackage = MEPackageHandler.CreateMemoryEmptyPackage("MFE-LE3-MotoParent.pcc", MEGame.LE3);
        var moto = CreateExportAtPath(
            motoPackage, "BIOG_HMM_HIR_PRO_R.Hat_Moto.HMM_HAT_Mtr_MASTER", "Material");
        TestAssert.True(IsSupported(motoPackage, moto, out var motoFailure),
            $"The canonical LE3 Moto hat material root was rejected: {motoFailure}");
    }

    private static void StockHairMicDonorsUseCanonicalParents()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        VerifyStockHairMic(
            MEGame.LE1,
            "BIOG_HMF_HIR_PRO.PROCustomShort02.HMF_HIR_PROCustomShort02_MAT_1a",
            "EffectsMaterials.Users.HMN_HED_PRO_MASTER_HAIR_MAT_USER",
            "BIOG_HMM_HIR_PRO_R.Short01.HMM_HIR_PROShort01_MAT_1a");
        VerifyStockHairMic(
            MEGame.LE2,
            "BIOG_HMF_HIR_PRO.PROCustomShort02.HMF_HIR_PROCustomShort02_MAT_1a",
            "EffectsMaterials.Users.Human.HMN_HED_PRO_MASTER_HAIR_MAT_USER",
            "BIOG_HMM_HIR_PRO_R.Short01.HMM_HIR_PROShort01_MAT_1a");
        VerifyStockHairMic(
            MEGame.LE2,
            "BIOG_HMF_HIR_PRO.Ashley.HMF_HIR_PROAshley_Mat_2a",
            "EffectsMaterials.Users.Human.HMN_HED_PRO_MASTER_HAIR_MAT_USER",
            "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HED_PRO_MASTER_HAIR_MAT");
        VerifyStockHairMic(
            MEGame.LE3,
            "BIOG_HMF_HIR_FTL_R.Materials.LongHair.HMF_HIR_FTL_LongHair_Mat1a",
            "EffectsMaterials.Users.Human.HMN_HED_PRO_MASTER_HAIR_MAT_USER",
            "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HED_PRO_MASTER_HAIR_MAT");
        VerifyStockHairMic(
            MEGame.LE3,
            "biog_hmf_hir_pro.Hair_PROJack.HMF_HIR_PROJack_Mat_1a",
            "EffectsMaterials.Users.Human.HMN_HED_PRO_MASTER_HAIR_MAT_USER",
            "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HED_PRO_MASTER_HAIR_MAT");
    }

    private static void CustomMaterialGraphRoundTrips()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var donorPath = Path.Combine(Path.GetTempPath(), $"BIOG_HMF_HIR_CustomDonor-{Guid.NewGuid():N}.pcc");
        var outputPath = Path.Combine(Path.GetTempPath(), $"MFE-CustomAttachment-{Guid.NewGuid():N}.pcc");
        const string meshPath = "BIOG_HMF_HIR_CustomDonor.Hair.MFE_CustomHair";
        const string micPath = "BIOG_HMF_HIR_CustomDonor.Hair.MFE_CustomHairMic";
        const string texturePath = "BIOG_HMF_HIR_CustomDonor.Textures.MFE_CustomHairTexture";
        try
        {
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage(
                       "BIOG_HMF_HIR_CustomDonor.pcc", MEGame.LE1))
            {
                var root = donor.CreatePackageExport("BIOG_HMF_HIR_CustomDonor");
                var hair = donor.CreatePackageExport("Hair", root);
                var textures = donor.CreatePackageExport("Textures", root);
                var baseMaterial = CreateExportAtPath(
                    donor, "BIOG_HMM_HIR_PRO_R.Short01.HMM_HIR_PROShort01_MAT_1a", "Material");
                var texture = donor.CreateExport("MFE_CustomHairTexture", "Texture2D", textures, indexed: false);
                texture.WriteBinary(UTexture2D.Create());
                var mic = donor.CreateExport("MFE_CustomHairMic", "MaterialInstanceConstant", hair, indexed: false);
                mic.WriteBinary(MaterialInstance.Create());
                mic.WriteProperty(new ObjectProperty(baseMaterial, "Parent"));
                mic.WriteProperty(new ArrayProperty<StructProperty>(
                [
                    new StructProperty("ScalarParameterValue", false,
                        GuidProperty(), new NameProperty("MFE_CustomHairScalar", "ParameterName"),
                        new FloatProperty(1.375f, "ParameterValue"))
                ], "ScalarParameterValues"));
                mic.WriteProperty(new ArrayProperty<StructProperty>(
                [
                    new StructProperty("VectorParameterValue", false,
                        GuidProperty(), new NameProperty("MFE_CustomHairTint", "ParameterName"),
                        new StructProperty("LinearColor", false,
                            new FloatProperty(0.125f, "R"), new FloatProperty(0.25f, "G"),
                            new FloatProperty(0.5f, "B"), new FloatProperty(0.875f, "A"))
                        { Name = "ParameterValue", IsImmutable = true })
                ], "VectorParameterValues"));
                mic.WriteProperty(new ArrayProperty<StructProperty>(
                [
                    new StructProperty("TextureParameterValue", false,
                        GuidProperty(), new NameProperty("MFE_CustomHairMap", "ParameterName"),
                        new ObjectProperty(texture, "ParameterValue"))
                ], "TextureParameterValues"));
                var mesh = donor.CreateExport("MFE_CustomHair", "SkeletalMesh", hair, indexed: false);
                var binary = SkeletalMesh.Create();
                binary.Materials = [mic.UIndex];
                mesh.WriteBinary(binary);
                donor.Save(donorPath);
            }

            using var source = MEPackageHandler.OpenMEPackage(donorPath, forceLoadFromDisk: true);
            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("MFE-CustomAttachment.pcc", MEGame.LE1);
            var sourceMesh = source.FindExport(meshPath, "SkeletalMesh")
                             ?? throw new InvalidDataException($"Test donor is missing '{meshPath}'.");
            TestAssert.True(source.FindExport(micPath, "MaterialInstanceConstant")!
                    .GetProperty<ArrayProperty<StructProperty>>("ScalarParameterValues")?.Count == 1,
                "The custom donor fixture did not serialize its scalar override.");
            _ = ExternalSkeletalMeshMaterializer.Materialize(
                destination, donorPath, meshPath, "SkeletalMesh", sourceMesh.UIndex);
            destination.Save(outputPath);

            using var reopened = MEPackageHandler.OpenMEPackage(outputPath, forceLoadFromDisk: true);
            var savedMesh = reopened.FindExport(meshPath, "SkeletalMesh")
                            ?? throw new InvalidDataException("Materialised custom attachment mesh was not saved.");
            var savedMic = reopened.FindExport(micPath, "MaterialInstanceConstant")
                           ?? throw new InvalidDataException("Materialised custom attachment MIC was not saved.");
            TestAssert.Equal(1.375f, savedMic.GetProperty<ArrayProperty<StructProperty>>("ScalarParameterValues")!
                .Single().GetProp<FloatProperty>("ParameterValue")!.Value);
            var color = savedMic.GetProperty<ArrayProperty<StructProperty>>("VectorParameterValues")!
                .Single().GetProp<StructProperty>("ParameterValue")!;
            TestAssert.Equal(0.125f, color.GetProp<FloatProperty>("R")!.Value);
            TestAssert.Equal(0.25f, color.GetProp<FloatProperty>("G")!.Value);
            TestAssert.Equal(0.5f, color.GetProp<FloatProperty>("B")!.Value);
            TestAssert.Equal(0.875f, color.GetProp<FloatProperty>("A")!.Value);
            var savedTexture = savedMic.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")!
                .Single().GetProp<ObjectProperty>("ParameterValue")!.ResolveToEntry(reopened);
            TestAssert.True(savedTexture is ExportEntry &&
                            savedTexture.InstancedFullPath.Equals(texturePath, StringComparison.OrdinalIgnoreCase),
                "Custom MIC texture reference did not survive materialisation at its original identity.");
            TestAssert.Equal(savedMic.UIndex, savedMesh.GetBinaryData<SkeletalMesh>().Materials.Single());
        }
        finally
        {
            if (File.Exists(donorPath)) File.Delete(donorPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    private static void VerifyStockHairMic(MEGame game, string micPath, string effectsPath, string basePath)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage($"MFE-StockHair-{game}.pcc", game);
        var baseMaterial = CreateExportAtPath(package, basePath, "Material");
        var effects = CreateExportAtPath(package, effectsPath, "RvrEffectsMaterialUser");
        effects.WriteProperty(new ObjectProperty(baseMaterial, "m_pBaseMaterial"));
        var mic = CreateExportAtPath(package, micPath, "MaterialInstanceConstant");
        mic.WriteProperty(new ObjectProperty(effects, "Parent"));
        var mesh = package.CreateExport($"MFE_{Guid.NewGuid():N}", "SkeletalMesh", indexed: false);
        var binary = SkeletalMesh.Create();
        binary.Materials = [mic.UIndex];
        mesh.WriteBinary(binary);

        TestAssert.True(MorphFaceAttachmentTransferEngine.HasSupportedMaterials(package, mesh, out var failure),
            $"Stock MIC '{micPath}' should pass through its canonical parent graph: {failure}");
    }

    private static ExportEntry CreateExportAtPath(IMEPackage package, string path, string className)
    {
        var parts = path.Split('.');
        IEntry? parent = null;
        var currentPath = string.Empty;
        foreach (var part in parts[..^1])
        {
            currentPath = currentPath.Length == 0 ? part : $"{currentPath}.{part}";
            parent = package.FindEntry(currentPath, "Package") ?? package.CreatePackageExport(
                NameReference.FromInstancedString(part), parent as ExportEntry);
        }
        return package.CreateExport(parts[^1], className, parent as ExportEntry, indexed: false);
    }

    private static StructProperty GuidProperty() => new(
        "Guid", false,
        new IntProperty(0, "A"), new IntProperty(0, "B"),
        new IntProperty(0, "C"), new IntProperty(0, "D"))
    { Name = "ExpressionGUID", IsImmutable = true };

    private static ExportEntry CreateCanonicalBase(IMEPackage package)
    {
        var root = package.CreatePackageExport("BIOG_HMM_HIR_PRO_R");
        var shortHair = package.CreatePackageExport("Short01", root);
        return package.CreateExport("HMM_HIR_PROShort01_MAT_1a", "Material", shortHair, indexed: false);
    }

    private static bool IsSupported(IMEPackage package, ExportEntry material, out string failure)
    {
        using var cache = new PackageCache();
        var resolver = new GamePackageReferenceResolver(cache,
            new InstalledGameAssetResolver(_ => new Dictionary<string, string>(), _ => null));
        return SupportedMaterialGraphPolicy.IsSupported(package.Game, material, resolver, out failure);
    }
}
