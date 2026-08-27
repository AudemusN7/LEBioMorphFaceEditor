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
