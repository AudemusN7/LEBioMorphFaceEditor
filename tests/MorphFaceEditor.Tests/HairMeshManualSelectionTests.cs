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
        new("hair mesh picker keeps local and explicit sources but one official row per mesh", RegistryShowsAllPhysicalOccurrences),
        new("randomised donor maps to the existing canonical picker row", RandomisedDonorUsesCanonicalPickerRow),
        new("attachment choices exclude base heads and sort across local and external meshes", AttachmentChoicesExcludeHeadsAndSort)
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

    private static void AttachmentChoicesExcludeHeadsAndSort()
    {
        var head = new PackageAssetListItem(new AssetIdentity("Opened.pcc",
            "BIOG_KRO_HED_PROMorph.Master.KRO_HED_PROBase_MDL", 1, "SkeletalMesh"));
        var hair = new PackageAssetListItem(new AssetIdentity("Opened.pcc",
            "BIOG_HMM_HIR_PRO.Hair.HMM_HIR_A_MDL", 2, "SkeletalMesh"));
        using var editor = new HairMeshEditorViewModel(
            new AssetReferenceEditingSession(head.Identity), [head, hair], "Hair", 0);
        const string hatPath = "BIOG_HMF_HIR_PRO.Beanie.HMF_HAT_Bn1_MDL";
        var hat = new AttachmentMeshOccurrence("External.pcc", hatPath, 3, 0,
            TextureCatalogOrigin.Manual);
        editor.UpdateRegistryCandidates([new AttachmentMeshCandidate(hatPath, hat, [hat])], false);
        TestAssert.Equal(3, editor.Candidates.Count);
        TestAssert.True(editor.Candidates[0].Identity is null, "None must remain first.");
        TestAssert.Equal(hatPath, editor.Candidates[1].Identity?.InstancedPath);
        TestAssert.Equal(hair.Identity.InstancedPath, editor.Candidates[2].Identity?.InstancedPath);
        TestAssert.Equal(head.Identity, editor.Value);
        editor.SearchText = "KRO";
        TestAssert.Equal(1, editor.Candidates.Count);
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
                    [officialWinner, officialShadowed, mod, manual, local])
                {
                    IsEligibleOfficialDefault = true
                },
                new AttachmentMeshCandidate(
                    "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Other_MDL",
                    new AttachmentMeshOccurrence("BIOG_HMF_HIR_PRO.pcc",
                        "Hair.HMF_HIR_Other_MDL", 30, 100, TextureCatalogOrigin.BaseGame, 45),
                    [
                        new AttachmentMeshOccurrence("BIOG_HMF_HIR_PRO.pcc",
                            "Hair.HMF_HIR_Other_MDL", 30, 100, TextureCatalogOrigin.BaseGame, 45),
                        new AttachmentMeshOccurrence("DLC_CON_Hair.pcc",
                            "Hair.HMF_HIR_Other_MDL", 31, 80, TextureCatalogOrigin.OfficialDlc, 46)
                    ])
                {
                    IsEligibleOfficialDefault = true
                },
                new AttachmentMeshCandidate(excludedNonBiog.InstancedPath, excludedNonBiog, [excludedNonBiog])
            ],
            isPlayerWorkspace: true);

        var options = editor.Candidates.Skip(1).ToArray();
        TestAssert.Equal(local.PackagePath, options[0].Identity?.PackagePath);
        TestAssert.True(!options.Any(option => option.Identity?.InstancedPath == canonical &&
                option.Identity.PackagePath == officialWinner.PackagePath),
            "A local authored source was duplicated by its official canonical donor row.");
        TestAssert.Equal("BIOG_HMF_HIR_PRO.pcc", options.Single(option =>
            option.Identity?.InstancedPath == "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Other_MDL").Identity?.PackagePath);
        TestAssert.True(!options.Any(option => option.Identity?.PackagePath == officialShadowed.PackagePath),
            "A second official donor row duplicated the same canonical mesh.");
        foreach (var occurrence in new[] { mod, manual })
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
        TestAssert.True(options.All(option => option.Identity?.InstancedPath.StartsWith("BIOG_", StringComparison.OrdinalIgnoreCase) == true),
            "The player workspace exposed a non-BIOG mesh candidate or changed the canonical path.");
        TestAssert.Equal(localAsset.Identity, editor.Selected.Identity);
    }

    private static void RandomisedDonorUsesCanonicalPickerRow()
    {
        const string canonical = "BIOG_HMM_HIR_PRO.Hair.HMM_HIR_One_MDL";
        var preferred = new AttachmentMeshOccurrence("BIOG_HMM_HIR_PRO.pcc",
            canonical, 41, 100, TextureCatalogOrigin.BaseGame, 42);
        var repeatedOfficial = new AttachmentMeshOccurrence("BIOA_TEST.pcc",
            canonical, 42, 50, TextureCatalogOrigin.OfficialDlc, 42);
        using var editor = new HairMeshEditorViewModel(new AssetReferenceEditingSession(null), [], "Hair", 0);
        editor.UpdateRegistryCandidates(
            [new AttachmentMeshCandidate(canonical, preferred, [preferred, repeatedOfficial])
            {
                IsEligibleOfficialDefault = true
            }],
            isPlayerWorkspace: false);
        TestAssert.Equal(2, editor.Candidates.Count);

        var randomisedIdentity = new AssetIdentity(repeatedOfficial.PackagePath, canonical,
            repeatedOfficial.ExportUIndex, "SkeletalMesh");
        TestAssert.True(editor.TrySetRandomisedSelection(randomisedIdentity),
            "The randomiser did not preserve its selected physical donor.");
        TestAssert.Equal(repeatedOfficial.PackagePath, editor.Value?.PackagePath);
        TestAssert.Equal(2, editor.Candidates.Count);
        TestAssert.Equal(preferred.PackagePath, editor.Selected.Identity?.PackagePath);

        editor.UpdateRegistryCandidates(
            [new AttachmentMeshCandidate(canonical, preferred, [preferred, repeatedOfficial])
            {
                IsEligibleOfficialDefault = true
            }],
            isPlayerWorkspace: false);
        TestAssert.Equal(2, editor.Candidates.Count);
        TestAssert.Equal(repeatedOfficial.PackagePath, editor.Value?.PackagePath);
        TestAssert.Equal(preferred.PackagePath, editor.Selected.Identity?.PackagePath);
    }
}
