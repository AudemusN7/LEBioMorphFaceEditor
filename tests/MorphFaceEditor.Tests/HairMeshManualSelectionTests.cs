using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

public static class HairMeshManualSelectionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("manual hair mesh selection is distinct from programmatic changes", ManualSelectionIsDistinct)
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
}
