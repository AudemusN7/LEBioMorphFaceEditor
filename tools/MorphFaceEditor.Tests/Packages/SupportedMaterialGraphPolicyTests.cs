using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

public static class SupportedMaterialGraphPolicyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("custom attachment MICs are supported through reviewed canonical base materials", CustomMicInheritsCanonicalBase),
        new("custom attachment MICs are supported through reviewed canonical effects materials", CustomMicInheritsCanonicalEffectsMaterial),
        new("custom attachment MICs reject unknown roots unresolved parents and cycles", RejectsUnsupportedGraphs),
        new("material recognition uses the reached master and preserves child values", MasterAuthorityAndValues)
    ];

    private static void MasterAuthorityAndValues()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var path = Path.Combine(Path.GetTempPath(), $"MFE-MasterAuthority-{Guid.NewGuid():N}.pcc");
        try
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE1);
            var master = CreateExportAtPath(package, "Modded.Place.HMM_HED_PRONPC_MASTER_FACE_MAT", "Material");
            var scalar = package.CreateExport("Strength", "MaterialExpressionScalarParameter", master, indexed: false);
            scalar.WriteProperty(new NameProperty("HED_Norm_Blend", "ParameterName"));
            scalar.WriteProperty(new FloatProperty(0.25f, "DefaultValue"));
            master.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(scalar)], "Expressions"));
            master.WriteBinary(Material.Create());
            var mic = package.CreateExport("Misleading_Alien_Eye_Name", "MaterialInstanceConstant", indexed: false);
            mic.WriteProperty(new ObjectProperty(master, "Parent"));
            WriteScalar(mic, 0.7f);
            var broken = package.CreateExport("HMF_HED_PROCustom_MASTER_FACE_MAT", "MaterialInstanceConstant", indexed: false);
            WriteScalar(broken, 0.3f);
            var cyclic = package.CreateExport("HMN_HED_PRO_MASTER_HAIR_MAT", "MaterialInstanceConstant", indexed: false);
            cyclic.WriteProperty(new ObjectProperty(cyclic, "Parent"));
            WriteScalar(cyclic, 0.4f);
            package.Save(path);
            using var cache = new PackageCache();
            var reader = new MorphFaceMaterialReader(cache, new GamePackageReferenceResolver(cache,
                new InstalledGameAssetResolver(_ => new Dictionary<string, string>(), _ => null)));
            var material = reader.ReadMaterialEvidence(mic, decodeTextures: false);
            TestAssert.True(MorphFacePackageReader.DescribeMaterialMaster(MEGame.LE2, master) is null,
                "A foreign-game material was treated as a local game's master.");
            TestAssert.Equal(HeadMaterialFamily.Skin, material.Family);
            TestAssert.Equal(HumanFaceMaterialKind.MaleNpc, material.HumanFace);
            TestAssert.Equal(0.7f, material.Scalars["HED_Norm_Blend"]);
            foreach (var (entry, value) in new[] { (broken, 0.3f), (cyclic, 0.4f) })
            {
                var unsupported = reader.ReadMaterialEvidence(entry, decodeTextures: false);
                TestAssert.Equal(HeadMaterialFamily.Unknown, unsupported.Family);
                TestAssert.True(!unsupported.ContributesFaceControls && !unsupported.HasCompiledParameterSupport,
                    "A MIC name without a terminal master granted material controls.");
                TestAssert.Equal(value, unsupported.Scalars["HED_Norm_Blend"]);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        static void WriteScalar(ExportEntry mic, float value) => mic.WriteProperty(new ArrayProperty<StructProperty>(
            [new StructProperty("ScalarParameterValue", new PropertyCollection
            {
                new NameProperty("HED_Norm_Blend", "ParameterName"), new FloatProperty(value, "ParameterValue")
            })], "ScalarParameterValues"));
    }

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
        TestAssert.True(IsSupported(package, unknownEffectsMic, out var unknownEffectsFailure),
            $"A renamed effects wrapper leading to a known master was rejected: {unknownEffectsFailure}");
        var relocatedMaster = package.CreateExport(canonicalBase.ObjectName.Instanced, "Material",
            package.CreatePackageExport("Relocated"), indexed: false);
        unknownEffects.WriteProperty(new ObjectProperty(relocatedMaster, "m_pBaseMaterial"));
        TestAssert.True(IsSupported(package, unknownEffectsMic, out var relocatedFailure),
            $"A relocated exact master was rejected: {relocatedFailure}");

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
