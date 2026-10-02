using System.Windows;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Views;

public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow(string assetPath, UnsavedChangesScope scope)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        FacePathText.Text = assetPath;
        if (scope == UnsavedChangesScope.Face)
        {
            Title = Loc.Text("loc_UnsavedChangesWindow_UnsavedBioMorphFaceChanges");
            SummaryText.Text = Loc.Text("loc_UnsavedChangesWindow_LoadedBioMorphFaceHasUnsavedChanges");
            SaveExplanationText.Text = Loc.Text("loc_UnsavedChangesWindow_CommitPackageChangesExplanation");
            DiscardButton.Content = Loc.Text("loc_UnsavedChangesWindow_DontCommit");
            SaveButton.Content = Loc.Text("loc_UnsavedChangesWindow_Commit");
        }
        else if (scope == UnsavedChangesScope.DetachedMaterials)
        {
            Title = Loc.Text("loc_UnsavedChangesWindow_UnsavedMeshMaterialSettings");
            SummaryText.Text = Loc.Text("loc_UnsavedChangesWindow_MeshWorkspaceHasUnsavedEdits");
            SaveExplanationText.Text = Loc.Text("loc_UnsavedChangesWindow_ExportMaterialsExplanation");
            SaveButton.Content = Loc.Text("loc_UnsavedChangesWindow_ExportMaterials");
        }
        else if (scope == UnsavedChangesScope.StandaloneFace)
        {
            Title = Loc.Text("loc_UnsavedChangesWindow_UnsavedStandaloneMorphChanges");
            SummaryText.Text = Loc.Text("loc_UnsavedChangesWindow_StandaloneMorphHasUnsavedChanges");
            SaveExplanationText.Text =
                Loc.Text("loc_UnsavedChangesWindow_ExportStandaloneMorphExplanation");
            SaveButton.Content = Loc.Text("loc_UnsavedChangesWindow_Export");
        }
    }

    public UnsavedChangesChoice Choice { get; private set; } = UnsavedChangesChoice.Cancel;

    private void OnSave(object sender, RoutedEventArgs e) => Complete(UnsavedChangesChoice.Save);
    private void OnDiscard(object sender, RoutedEventArgs e) => Complete(UnsavedChangesChoice.Discard);
    private void OnCancel(object sender, RoutedEventArgs e) => Complete(UnsavedChangesChoice.Cancel);

    private void Complete(UnsavedChangesChoice choice)
    {
        Choice = choice;
        DialogResult = choice is not UnsavedChangesChoice.Cancel;
    }
}
