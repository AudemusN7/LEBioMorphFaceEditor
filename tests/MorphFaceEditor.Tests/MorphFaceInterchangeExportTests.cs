using LegendaryExplorerCore.Packages;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

public static class MorphFaceInterchangeExportTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("UModel staging preserves indexed BioMorphFace mesh names", StagingPreservesIndexedMeshName)
    ];

    private static void StagingPreservesIndexedMeshName()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var sourcePath = Path.GetFullPath(Path.Combine(
            "tests", "Global Morphs", "LE2 GlobalMorphs.pcc"));
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The LE2 GlobalMorphs fixture is required for the indexed mesh-name regression.", sourcePath);
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"MFE-IndexedMeshName-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var stagingPath = MorphFaceInterchangeService.CreateAppliedMeshPackage(
                sourcePath,
                "HMM.Skel_HMM_1",
                temporaryDirectory,
                out var umodelMeshName,
                out var bakedPositions,
                out _);

            using var stagingPackage = MEPackageHandler.OpenMEPackage(stagingPath, forceLoadFromDisk: true);
            var mesh = stagingPackage.Exports.Single();
            TestAssert.Equal("Skel_HMM_1", umodelMeshName);
            TestAssert.Equal(mesh.ObjectName.Instanced, umodelMeshName);
            TestAssert.Equal("Skel_HMM", mesh.ObjectNameString);
            TestAssert.True(bakedPositions.Length > 0, "The staged indexed BioMorphFace had no baked geometry.");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }
}
