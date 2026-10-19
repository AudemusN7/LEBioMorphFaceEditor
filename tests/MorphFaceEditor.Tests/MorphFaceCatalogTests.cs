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
        new("LE1 EntryMenu Player staging hides only Broke Asari and Krogan", Le1EntryMenuPlayerStagingHidesOnlyKnownFaces),
        new("LE1 staging projection does not affect default or other-game catalogues", Le1StagingProjectionIsRouteAndGameScoped),
        new("LE1 EntryMenu projection policy is limited to detached Player staging", Le1EntryMenuProjectionPolicyIsNarrow),
        new("workspace catalogues preserve face and picker data and refresh changed PCCs", WorkspaceCataloguesStayFresh)
    ];

    private static void WorkspaceCataloguesStayFresh()
    {
        var catalogService = new MorphFaceCatalogService();
        using var reader = new MorphFacePackageReader();
        var referenceService = new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService());
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            var sourcePath = Path.GetFullPath(Path.Combine("tests", "Global Morphs", $"{game} GlobalMorphs.pcc"));
            var expectedFaces = catalogService.ReadAsync(sourcePath).GetAwaiter().GetResult();
            var expectedAssets = PackageAssetInspector.Inventory(sourcePath, ["Texture2D", "SkeletalMesh"]);
            var expectedReferences = referenceService.ReadCatalogAsync(sourcePath).GetAwaiter().GetResult();
            var inventory = PackageAssetInspector.InventoryWorkspace(sourcePath);
            var preparation = catalogService.ReadWorkspaceAsync(sourcePath, referenceService).GetAwaiter().GetResult();
            var references = preparation.References;
            TestAssert.Equal(game, preparation.Faces.Game);
            TestAssert.True(expectedFaces.Faces.SequenceEqual(preparation.Faces.Faces),
                $"{game} shared preparation changed face identity, ordering, profiles or RON eligibility.");
            TestAssert.Equal(expectedAssets.PackagePath, inventory.Assets.PackagePath);
            TestAssert.Equal(expectedAssets.Fingerprint, inventory.Assets.Fingerprint);
            TestAssert.True(expectedAssets.Entries.SequenceEqual(inventory.Assets.Entries),
                $"{game} shared preparation changed asset identity, default-object flags or mesh bone counts.");
            TestAssert.True(expectedReferences.Textures.Select(value => value.Identity)
                    .SequenceEqual(references.Textures.Select(value => value.Identity)) &&
                expectedReferences.SkeletalMeshes.Select(value => (value.Identity, value.BoneCount))
                    .SequenceEqual(references.SkeletalMeshes.Select(value => (value.Identity, value.BoneCount))) &&
                references.Textures.All(value => value.CanLoadThumbnail),
                $"{game} shared preparation changed picker filtering, ordering or thumbnail availability.");
        }

        using var workspace = new MorphFacePackageWorkspace(
            Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE1 GlobalMorphs.pcc")), canCommit: false);
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

    private static void Le1EntryMenuPlayerStagingHidesOnlyKnownFaces()
    {
        var faces = new[]
        {
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Broke"),
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Asari"),
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Krogan"),
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Player_Base_Male"),
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Player_Base_Female")
        };

        var visible = faces
            .Where(face => !MorphFaceCatalogService.IsHiddenForProjection(
                face, MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging))
            .Select(face => face.FacePath)
            .ToArray();

        TestAssert.Equal(2, visible.Length);
        TestAssert.True(visible.SequenceEqual(
                ["BIOG_MORPH_FACE.Player_Base_Male", "BIOG_MORPH_FACE.Player_Base_Female"]),
            "The LE1 Player staging projection hid a face other than the three known EntryMenu exports.");
    }

    private static void Le1StagingProjectionIsRouteAndGameScoped()
    {
        var hiddenNames = new[] { "Broke", "Asari", "Krogan" };
        foreach (var name in hiddenNames)
        {
            var le1 = Reference(MorphFaceGame.LE1, $"BIOG_MORPH_FACE.{name}");
            var le2 = Reference(MorphFaceGame.LE2, le1.FacePath);

            TestAssert.True(
                MorphFaceCatalogService.IsHiddenForProjection(
                    le1, MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging),
                $"LE1 {le1.FacePath} was not hidden by the explicit staging projection.");
            TestAssert.True(
                !MorphFaceCatalogService.IsHiddenForProjection(
                    le2, MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging),
                $"The LE1 staging projection leaked to another game for {le2.FacePath}.");
            TestAssert.True(
                !MorphFaceCatalogService.IsHiddenForProjection(
                    le1, MorphFaceCatalogProjection.Default),
                $"Default package face discovery hid {le1.FacePath}.");
        }
    }

    private static void Le1EntryMenuProjectionPolicyIsNarrow()
    {
        TestAssert.Equal(
            MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging,
            MorphFaceCatalogProjectionPolicy.ForPlayerWorkspace(
                MorphFaceGame.LE1, @"D:\Game\ME1\BioGame\CookedPCConsole\EntryMenu.pcc", false));
        TestAssert.Equal(
            MorphFaceCatalogProjection.Default,
            MorphFaceCatalogProjectionPolicy.ForPlayerWorkspace(
                MorphFaceGame.LE1, @"D:\Game\ME1\BioGame\CookedPCConsole\BioP_Char.pcc", false));
        TestAssert.Equal(
            MorphFaceCatalogProjection.Default,
            MorphFaceCatalogProjectionPolicy.ForPlayerWorkspace(
                MorphFaceGame.LE2, @"D:\Game\ME2\BioGame\CookedPCConsole\EntryMenu.pcc", false));
        TestAssert.Equal(
            MorphFaceCatalogProjection.Default,
            MorphFaceCatalogProjectionPolicy.ForPlayerWorkspace(
                MorphFaceGame.LE1, @"D:\Game\ME1\BioGame\CookedPCConsole\EntryMenu.pcc", true));
    }

    private static MorphFaceMeshReferences Reference(MorphFaceGame game, string path) =>
        new("EntryMenu.pcc", game, 1, path, null, null, []);
}
