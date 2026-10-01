using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.QA;

namespace MorphFaceEditor.Tests;

internal static class QaNpcEyeGraphCheckTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE2 NPC eye graph accepts HMM HMF and Asari stock chains", AcceptsStockChains),
        new("LE2 NPC eye graph rejects the Player EYE_OVRD parent", RejectsPlayerOverride),
        new("LE2 NPC eye graph requires EffectsMaterials package exports and master import", RequiresNativeRoles)
    ];

    private static void AcceptsStockChains()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var family in new[] { "HMM", "HMF", "ASA" })
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage($"MFE-{family}-NpcEyeGraph.pcc", MEGame.LE2);
            var (baseHead, _) = AddEyeMaterial(package, family, UseStockEffectsImport(package));
            var evidence = QaNpcEyeGraphCheck.Inspect(package, baseHead, family);
            TestAssert.True(evidence.Passed,
                $"The correct direct HMM eye chain was rejected for {family}: {string.Join("; ", evidence.Failures)}");
            TestAssert.True(evidence.EyeMics.Any(mic => mic.MicRole == "Export" &&
                mic.ParentPath == "EffectsMaterials.Users.HMM_EYE_MASTER_MAT_USER"),
                $"The {family} eye MIC did not retain its exported local identity and direct stock parent.");
        }
    }

    private static void RejectsPlayerOverride()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MFE-LE2-WrongPlayerEyeGraph.pcc", MEGame.LE2);
        var wrongParent = PlayerOverrideImport(package);
        var (baseHead, _) = AddEyeMaterial(package, "HMM", wrongParent);
        var evidence = QaNpcEyeGraphCheck.Inspect(package, baseHead, "HMM");
        TestAssert.True(!evidence.Passed && evidence.Failures.Any(value =>
                value.Contains("does not inherit from imported EffectsMaterials.Users.HMM_EYE_MASTER_MAT_USER",
                    StringComparison.OrdinalIgnoreCase)),
            "The Player BIOG_Humanoid_EYE_OVRD chain passed the NPC eye graph check.");
    }

    private static void RequiresNativeRoles()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using (var package = MEPackageHandler.CreateMemoryEmptyPackage("MFE-LE2-EyeParentExport.pcc", MEGame.LE2))
        {
            var (baseHead, _) = AddEyeMaterial(package, "HMM", StockEffectsExport(package));
            var evidence = QaNpcEyeGraphCheck.Inspect(package, baseHead, "HMM");
            TestAssert.True(!evidence.Passed && evidence.Failures.Any(value =>
                    value.Contains("is not a RvrEffectsMaterialUser import", StringComparison.OrdinalIgnoreCase)),
                "The stock eye Effects Material export was accepted where the native graph requires an import.");
        }

        using (var package = MEPackageHandler.CreateMemoryEmptyPackage("MFE-LE2-EyePackageImports.pcc", MEGame.LE2))
        {
            var (baseHead, _) = AddEyeMaterial(package, "HMF", EffectsImportUnderPackageImports(package));
            var evidence = QaNpcEyeGraphCheck.Inspect(package, baseHead, "HMF");
            TestAssert.True(!evidence.Passed && evidence.Failures.Any(value =>
                    value.Contains("EffectsMaterials is not a Package export", StringComparison.OrdinalIgnoreCase)) &&
                    evidence.Failures.Any(value =>
                        value.Contains("EffectsMaterials.Users is not a Package export", StringComparison.OrdinalIgnoreCase)),
                "The eye graph passed without the required EffectsMaterials/Users Package exports.");
        }
    }

    private static IEntry UseStockEffectsImport(IMEPackage package)
    {
        _ = package.CreatePackageExport("EffectsMaterials");
        var users = package.CreatePackageExport("Users", package.FindExport("EffectsMaterials", "Package"));
        return package.CreateImport("RvrEffectsMaterialUser", "HMM_EYE_MASTER_MAT_USER", users);
    }

    private static ExportEntry StockEffectsExport(IMEPackage package)
    {
        var effects = package.CreatePackageExport("EffectsMaterials");
        var users = package.CreatePackageExport("Users", effects);
        return package.CreateExport("HMM_EYE_MASTER_MAT_USER", "RvrEffectsMaterialUser", users, indexed: false);
    }

    private static IEntry EffectsImportUnderPackageImports(IMEPackage package)
    {
        var effects = package.CreatePackageImport("EffectsMaterials");
        var users = package.CreateImport("Package", "Users", effects);
        return package.CreateImport("RvrEffectsMaterialUser", "HMM_EYE_MASTER_MAT_USER", users);
    }

    private static IEntry PlayerOverrideImport(IMEPackage package)
    {
        var player = package.CreatePackageImport("BIOG_Humanoid_EYE_OVRD");
        var users = package.CreateImport("Package", "Users", player);
        return package.CreateImport("RvrEffectsMaterialUser", "HMM_EYE_MASTER_OVRD_MAT_USER", users);
    }

    private static (ExportEntry BaseHead, ExportEntry Mic) AddEyeMaterial(
        IMEPackage package,
        string family,
        IEntry parent)
    {
        var root = package.CreatePackageExport($"BIOG_{family}_HED_PROMorph");
        var eye = package.CreatePackageExport("Eye", root);
        var mic = package.CreateExport($"{family}_EYE_MAT_1a", "MaterialInstanceConstant", eye, indexed: false);
        mic.WriteProperty(new ObjectProperty(parent, "Parent"));
        var baseHead = package.CreateExport($"{family}_NPC_BaseHead", "SkeletalMesh", indexed: false);
        var binary = SkeletalMesh.Create();
        binary.Materials = [mic.UIndex];
        baseHead.WriteBinary(binary);
        return (baseHead, mic);
    }
}
