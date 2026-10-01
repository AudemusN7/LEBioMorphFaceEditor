using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

public static class HairMeshManualSelectionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("manual hair mesh selection is distinct from programmatic changes", ManualSelectionIsDistinct),
        new("hair mesh picker shows all physical registry occurrences beside local assets", RegistryShowsAllPhysicalOccurrences)
    ];

    private static void ManualSelectionIsDistinct()
    {
        var first = new PackageAssetListItem(new AssetIdentity(
            "HairA.pcc", "BIOG_HMM_HIR_PRO.Hair.HMM_HIR_A_MDL", 1, "SkeletalMesh"));
        var second = new PackageAssetListItem(new AssetIdentity(
            "HairB.pcc", "BIOG_HMM_HIR_PRO.Hair.HMM_HIR_B_MDL", 2, "SkeletalMesh"));
        using var editor = new HairMeshEditorViewModel(
            new AssetReferenceEditingSession(first.Identity), [first, second], "Hair", 0);
        var manualSelections = 0;
        editor.ManualSelectionCommitted += (_, _) => manualSelections++;

        TestAssert.True(editor.TrySetRandomisedSelection(second.Identity),
            "The programmatic randomised selection did not change the mesh.");
        editor.SetImportedPreview(first.Identity);
        editor.Preview(editor.Options.Single(option => option.Identity == second.Identity));
        editor.CancelPreview();
        TestAssert.Equal(0, manualSelections);

        editor.Selected = editor.Options.Single(option => option.Identity == second.Identity);
        TestAssert.Equal(1, manualSelections);
        editor.Commit(editor.Options.Single(option => option.Identity == first.Identity));
        TestAssert.Equal(2, manualSelections);
    }

    private static void RegistryShowsAllPhysicalOccurrences()
    {
        const string canonical = "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Custom_MDL";
        var local = new AttachmentMeshOccurrence(
            "C:\\Custom\\OpenedHair.pcc", canonical, 11, 0, TextureCatalogOrigin.Manual, 31);
        var officialWinner = new AttachmentMeshOccurrence(
            "BIOG_HMF_HIR_PRO.pcc", canonical, 20, 100, TextureCatalogOrigin.BaseGame, 42);
        var officialShadowed = new AttachmentMeshOccurrence(
            "DLC_CON_Hair.pcc", canonical, 21, 80, TextureCatalogOrigin.OfficialDlc, 43);
        var mod = new AttachmentMeshOccurrence(
            "DLC_MOD_Hair\\ModHair.pcc", canonical, 22, 9000, TextureCatalogOrigin.Mod, 47);
        var manual = new AttachmentMeshOccurrence(
            "C:\\Custom\\AddedHair.pcc", canonical, 23, 0, TextureCatalogOrigin.Manual, 48);
        var excludedNonBiog = new AttachmentMeshOccurrence(
            "C:\\Custom\\OtherMesh.pcc", "Custom_Hair.Mesh.Custom_MDL", 24, 0,
            TextureCatalogOrigin.Manual, 49);
        var localAsset = new PackageAssetListItem(new AssetIdentity(
            local.PackagePath, canonical, local.ExportUIndex, "SkeletalMesh"));
        using var editor = new HairMeshEditorViewModel(
            new AssetReferenceEditingSession(localAsset.Identity), [localAsset], "Hair", 0);

        editor.UpdateRegistryCandidates(
            [
                new AttachmentMeshCandidate(canonical, officialWinner,
                    [officialWinner, officialShadowed, mod, manual, local]),
                new AttachmentMeshCandidate(excludedNonBiog.InstancedPath, excludedNonBiog, [excludedNonBiog])
            ],
            isPlayerWorkspace: true);

        var options = editor.Candidates.Skip(1).ToArray();
        TestAssert.Equal(local.PackagePath, options[0].Identity?.PackagePath);
        TestAssert.Equal(officialWinner.PackagePath, options[1].Identity?.PackagePath);
        foreach (var occurrence in new[] { officialWinner, officialShadowed, mod, manual })
        {
            TestAssert.True(options.Any(option =>
                    string.Equals(option.Identity?.PackagePath, occurrence.PackagePath, StringComparison.OrdinalIgnoreCase) &&
                    option.Identity?.UIndex == occurrence.ExportUIndex),
                $"The picker omitted physical mesh occurrence '{occurrence.PackagePath}' #{occurrence.ExportUIndex}.");
        }
        TestAssert.Equal(options.Length, options
            .Where(option => option.Identity is not null)
            .Select(option => $"{Path.GetFullPath(option.Identity!.PackagePath)}|{option.Identity.UIndex}")
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        TestAssert.True(options.All(option => option.Identity?.InstancedPath == canonical),
            "The player workspace exposed a non-BIOG mesh candidate or changed the canonical path.");
        TestAssert.Equal(localAsset.Identity, editor.Selected.Identity);
    }
}
