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
            Title = "Unsaved BioMorphFace changes";
            SummaryText.Text = "The loaded BioMorphFace has unsaved changes.";
            SaveExplanationText.Text = "Save keeps these edits in the temporary package workspace.";
        }
        else if (scope == UnsavedChangesScope.DetachedMaterials)
        {
            Title = "Unsaved mesh material settings";
            SummaryText.Text = "The mesh workspace has unsaved edits.";
            SaveExplanationText.Text = "Export Materials keeps slot assignments and material settings in an MFE RON. Preview bones and attachments are not included.";
            SaveButton.Content = "Export Materials…";
        }
        else if (scope == UnsavedChangesScope.StandaloneFace)
        {
            Title = "Unsaved standalone morph changes";
            SummaryText.Text = "The loaded standalone morph has unsaved changes.";
            SaveExplanationText.Text =
                "Export opens Save to PCC. The installed game template is read-only and will not be modified.";
            SaveButton.Content = "Export…";
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
