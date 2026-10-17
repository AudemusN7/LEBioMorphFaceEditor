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
        new("LE2 HMF NPC export preserves standard eye parent through save reopen", NpcEyeParentSurvivesExport),
        new("randomisation corpus evidence retains authored MICs over installed mesh copies", CorpusReadsAuthoredMaterialEvidence)
    ];

    private const string AssetPath = "BIOG_Humanoid_EYE_OVRD.HMM_EYE_MASTER_OVRD_MAT_USER";

    private static void CorpusReadsAuthoredMaterialEvidence()
    {
        var sourcePath = Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE2 GlobalMorphs.pcc"));
        if (!File.Exists(sourcePath)) throw new TestSkippedException("LE2 GlobalMorphs fixture is missing.");
        var copyPath = Path.Combine(Path.GetTempPath(), $"MFE-CorpusEvidence-{Guid.NewGuid():N}.pcc");
        LegendaryExplorerCoreRuntime.Initialize();
        try
        {
            File.Copy(sourcePath, copyPath);
            using (var package = MEPackageHandler.OpenMEPackage(copyPath, forceLoadFromDisk: true))
            {
                var mic = package.FindExport("BIOG_HMF_HED_PROMorph_R.Average.HMF_EYE_PROAverage_MAT_1a", "MaterialInstanceConstant")!;
                var parameters = mic.GetProperty<ArrayProperty<StructProperty>>("ScalarParameterValues")
                    ?? new ArrayProperty<StructProperty>("ScalarParameterValues");
                parameters.Add(new StructProperty("ScalarParameterValue", new PropertyCollection
                {
                    new NameProperty("MFE_CorpusMarker", "ParameterName"),
                    new FloatProperty(0.12345f, "ParameterValue")
                }, "ScalarParameterValue"));
                mic.WriteProperty(parameters);
                package.Save();
            }
            using var reader = new MorphFacePackageReader();
            var evidence = reader.LoadRandomisationMaterialEvidence(copyPath, "HMF.face_helena");
            TestAssert.Equal(0.12345f, evidence.Scalars["MFE_CorpusMarker"]);
            TestAssert.Equal("BIOG_HMM_HED_PROMorph.Eye.EYE_Iris_Norm", evidence.Textures["EYE_Iris_Norm"]);
        }
        finally { if (File.Exists(copyPath)) File.Delete(copyPath); }
    }

    private static void NpcEyeParentSurvivesExport()
    {
        const string micPath = "BIOG_HMF_HED_PROMorph_R.Average.HMF_EYE_PROAverage_MAT_1a";
        const string parentPath = "EffectsMaterials.Users.HMM_EYE_MASTER_MAT_USER";
        var sourcePath = Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE2 GlobalMorphs.pcc"));
        if (!File.Exists(sourcePath)) throw new TestSkippedException("LE2 GlobalMorphs fixture is missing.");
        var outputPath = Path.Combine(Path.GetTempPath(), $"MFE-LE2-NpcEyes-{Guid.NewGuid():N}.pcc");
        LegendaryExplorerCoreRuntime.Initialize();
        try
        {
            using var source = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
            var sourceMic = source.FindExport(micPath, "MaterialInstanceConstant")!;
            TestAssert.Equal(parentPath, sourceMic.GetProperty<ObjectProperty>("Parent")!.ResolveToEntry(source)!.InstancedFullPath);
            using (var destination = MEPackageHandler.CreateMemoryEmptyPackage(outputPath, MEGame.LE2))
            {
                _ = PccPackageWorkflow.ImportRootPreservingStructure(destination,
                    source.FindExport("HMF.face_helena", "BioMorphFace")!);
                var mic = destination.Exports.Single(entry => entry.ClassName == "MaterialInstanceConstant" &&
                    entry.InstancedFullPath.StartsWith(micPath, StringComparison.OrdinalIgnoreCase));
                var parent = mic.GetProperty<ObjectProperty>("Parent")!.ResolveToEntry(destination)!;
                TestAssert.Equal(parentPath, parent.InstancedFullPath);
                TestAssert.True(parent is ImportEntry, "NPC eye effects user was exported.");
                destination.Save(outputPath);
            }
            using var reopened = MEPackageHandler.OpenMEPackage(outputPath, forceLoadFromDisk: true);
            TestAssert.Equal(parentPath, reopened.Exports.Single(entry => entry.ClassName == "MaterialInstanceConstant" &&
                    entry.InstancedFullPath.StartsWith(micPath, StringComparison.OrdinalIgnoreCase))
                .GetProperty<ObjectProperty>("Parent")!.ResolveToEntry(reopened)!.InstancedFullPath);
            var baseHead = reopened.FindExport("HMF.face_helena", "BioMorphFace")!
                .GetProperty<ObjectProperty>("m_oBaseHead")!.ResolveToEntry(reopened) as ExportEntry;
            var evidence = MorphFaceEditor.QA.QaNpcEyeGraphCheck.Inspect(reopened, baseHead!, "HMF",
                new InstalledGameAssetResolver());
            MorphFaceEditor.QA.QaNpcEyeGraphCheck.RequireValid(evidence);
        }
        finally { if (File.Exists(outputPath)) File.Delete(outputPath); }
    }

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
