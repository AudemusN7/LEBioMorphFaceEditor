using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MorphFaceEditor.Tests;

internal static class NativeMaterialReaderTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("broken attachment materials fall back without blocking face authoring", BrokenAttachmentMaterialFallsBack),
    ];

    private static void BrokenAttachmentMaterialFallsBack()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var packagePath = Path.Combine(Path.GetTempPath(), $"MFE-AttachmentFallback-{Guid.NewGuid():N}.pcc");
        MEPackageHandler.CreateAndSavePackage(packagePath, MEGame.LE2);
        try
        {
            using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
            var face = package.CreateExport("Face", "BioMorphFace", indexed: false);
            var attachment = package.CreateExport(
                "HMM_HAT_Broken_MAT", "MaterialInstanceConstant", indexed: false);
            var missingRoot = package.CreatePackageImport("MissingAttachmentMaster");
            var missingGroup = package.CreateImport("Package", "Human", missingRoot);
            var missingParent = package.CreateImport("Material", "MissingParent", missingGroup);
            attachment.WriteProperty(new ObjectProperty(missingParent, "Parent"));
            package.Save();

            using var cache = new PackageCache();
            var materialReader = new MorphFaceMaterialReader(
                cache, new GamePackageReferenceResolver(cache));
            var result = materialReader.Read(face, [], [attachment]);
            var fallback = result.Materials.Find(MorphFacePackageReader.ToIdentity(attachment)!);

            TestAssert.True(fallback is not null,
                "The broken attachment material did not produce a blank fallback.");
            TestAssert.Equal(HeadMaterialFamily.Accessory, fallback!.Family);
            TestAssert.Equal(0, fallback.Scalars.Count);
            TestAssert.Equal(0, fallback.Vectors.Count);
            TestAssert.Equal(0, fallback.Textures.Count);
            TestAssert.Equal(1, result.Warnings.Count);
            TestAssert.True(result.Warnings[0].Contains("MissingParent", StringComparison.Ordinal),
                "The attachment fallback did not retain the material failure for diagnostics.");
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

}
