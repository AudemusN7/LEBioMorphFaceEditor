using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class Le2LocalizedEyeOverrideImportRoleTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE2 localized eye override remains an import when its installed Startup donor resolves",
            () => ImportRoleSurvivesResolvedDonor(preserveStructure: true)),
        new("LE2 localized eye override remains an import through dependency graph materialisation",
            () => ImportRoleSurvivesResolvedDonor(preserveStructure: false)),
        new("LE2 discarded eye override export subtree does not reserve imported package ancestors",
            () => ImportRoleSurvivesResolvedDonor(preserveStructure: true, sourceIsExport: true)),
    ];

    private const string AssetPath = "BIOG_Humanoid_EYE_OVRD.HMM_EYE_MASTER_OVRD_MAT_USER";

    private static void ImportRoleSurvivesResolvedDonor(bool preserveStructure, bool sourceIsExport = false)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var donorPath = Path.Combine(Path.GetTempPath(), $"Startup_METR_Patch01_INT-{Guid.NewGuid():N}.pcc");
        var outputPath = Path.Combine(Path.GetTempPath(), $"MFE-LE2-EyeImportRole-{Guid.NewGuid():N}.pcc");
        try
        {
            TestAssert.Equal(MaterialOracleEntryKind.Import,
                MaterialDependencyOracle.Instance.GetKind(
                    MEGame.LE2, "RvrEffectsMaterialUser", AssetPath));
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage(
                       "Startup_METR_Patch01_INT.pcc", MEGame.LE2))
            {
                var group = donor.CreatePackageExport("BIOG_Humanoid_EYE_OVRD");
                _ = donor.CreateExport("HMM_EYE_MASTER_OVRD_MAT_USER", "RvrEffectsMaterialUser", group,
                    indexed: false);
                donor.Save(donorPath);
            }

            var installedAssets = new InstalledGameAssetResolver(
                loadedFiles: _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BIOG_Humanoid_EYE_OVRD.pcc"] = donorPath
                },
                snapshot: _ => null);

            using var source = MEPackageHandler.CreateMemoryEmptyPackage("LE2EyeImportSource.pcc", MEGame.LE2);
            IEntry sourceGroup = sourceIsExport
                ? source.CreatePackageExport("BIOG_Humanoid_EYE_OVRD")
                : source.CreatePackageImport("BIOG_Humanoid_EYE_OVRD");
            IEntry sourceImport = sourceIsExport
                ? source.CreateExport("HMM_EYE_MASTER_OVRD_MAT_USER", "RvrEffectsMaterialUser", (ExportEntry)sourceGroup, indexed: false)
                : source.CreateImport("RvrEffectsMaterialUser", "HMM_EYE_MASTER_OVRD_MAT_USER", sourceGroup);
            if (sourceImport is ExportEntry discarded)
            {
                var child = source.CreateExport("DiscardedDependency", "Object", (ExportEntry)sourceGroup, indexed: false);
                discarded.WriteProperty(new ObjectProperty(child, "DiscardedDependency"));
            }
            TestAssert.Equal(AssetPath, sourceImport.InstancedFullPath);

            using (var cache = new PackageCache())
            {
                var resolved = new GamePackageReferenceResolver(cache, installedAssets)
                    .Require(sourceImport, "localized eye override");
                TestAssert.Equal(Path.GetFullPath(donorPath), Path.GetFullPath(resolved.FileRef.FilePath));
                TestAssert.Equal(AssetPath, resolved.InstancedFullPath);
            }

            var sourceRoot = source.CreateExport("Face", "Object", indexed: false);
            sourceRoot.WriteProperty(new ObjectProperty(sourceImport, "EyeOverride"));
            using var destination = MEPackageHandler.CreateMemoryEmptyPackage(
                "LE2EyeImportDestination.pcc", MEGame.LE2);
            var ported = preserveStructure
                ? PccPackageWorkflow.ImportRootPreservingStructure(destination, sourceRoot,
                    installedAssets: installedAssets)
                : PccPackageWorkflow.ImportDependencyGraph(destination, sourceRoot, null, null,
                    installedAssets: installedAssets);
            var graph = PccPackageWorkflow.CaptureDependencyGraph(ported);
            TestAssert.True(destination.FindEntry("BIOG_Humanoid_EYE_OVRD", "Package") is ImportEntry,
                "The localized eye override package root was embedded as an export.");
            TestAssert.True(destination.FindEntry(AssetPath, "RvrEffectsMaterialUser") is ImportEntry,
                "The installed export donor changed the localized source import into an export.");
            TestAssert.True(destination.FindExport(AssetPath, "RvrEffectsMaterialUser") is null,
                "The localized eye override was embedded as an export.");
            TestAssert.True(destination.FindEntry("BIOG_Humanoid_EYE_OVRD.DiscardedDependency") is null,
                "The discarded donor subtree leaked into the destination graph.");

            destination.Save(outputPath);
            using var reopened = MEPackageHandler.OpenMEPackage(outputPath, forceLoadFromDisk: true);
            TestAssert.True(reopened.FindEntry("BIOG_Humanoid_EYE_OVRD", "Package") is ImportEntry,
                "The eye override package root import did not survive save and reopen.");
            TestAssert.True(reopened.FindEntry(AssetPath, "RvrEffectsMaterialUser") is ImportEntry,
                "The localized eye override import role did not survive save and reopen.");
            TestAssert.True(reopened.FindExport(AssetPath, "RvrEffectsMaterialUser") is null,
                "The reopened PCC contains an export at the localized eye override identity.");
            PccPackageWorkflow.VerifyDependencyGraph(reopened, graph);
        }
        finally
        {
            if (File.Exists(donorPath)) File.Delete(donorPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
