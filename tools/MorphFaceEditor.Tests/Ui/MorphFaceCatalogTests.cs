using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class MorphFaceCatalogTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("changed PCC refreshes inventories without mutating published snapshots", WorkspaceCataloguesStayFresh)
    ];

    private static void WorkspaceCataloguesStayFresh()
    {
        var catalogService = new MorphFaceCatalogService();
        using var reader = new MorphFacePackageReader();
        var referenceService = new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService());
        using var workspace = new MorphFacePackageWorkspace(
            Path.GetFullPath(Path.Combine("fixtures", "Global Morphs", "LE1 GlobalMorphs.pcc")), canCommit: false);
        var before = catalogService.ReadWorkspaceAsync(workspace.WorkingPath, referenceService).GetAwaiter().GetResult();
        var clone = new MorphFacePackageContextService().CloneMorph(workspace.WorkingPath,
            before.Faces.Faces.First(face => face.ProfileKey == "le1-human-male").InstancedPath, "MFE_CatalogueFace");
        string addedMeshPath;
        using (var package = MEPackageHandler.OpenMEPackage(workspace.WorkingPath, forceLoadFromDisk: true))
        {
            var mesh = EntryCloner.CloneEntry(package.Exports.First(export =>
                export.ClassName == "SkeletalMesh" && !export.IsDefaultObject));
            mesh.ObjectName = new NameReference("MFE_CatalogueMesh");
            addedMeshPath = mesh.InstancedFullPath;
            package.Save();
        }
        var after = catalogService.ReadWorkspaceAsync(workspace.WorkingPath, referenceService).GetAwaiter().GetResult();
        TestAssert.Equal(before.Faces.Faces.Count + 1, after.Faces.Faces.Count);
        TestAssert.True(after.Faces.Faces.Any(face => face.InstancedPath == clone.FaceInstancedPath) &&
            after.References.SkeletalMeshes.Any(mesh => mesh.Identity.InstancedPath == addedMeshPath),
            "Workspace refresh reused stale face or asset inventory after writing the same PCC.");
        TestAssert.True(!before.Faces.Faces.Any(face => face.InstancedPath == clone.FaceInstancedPath) &&
            !before.References.SkeletalMeshes.Any(mesh => mesh.Identity.InstancedPath == addedMeshPath),
            "A published inventory changed when the package was edited later.");
    }
}
