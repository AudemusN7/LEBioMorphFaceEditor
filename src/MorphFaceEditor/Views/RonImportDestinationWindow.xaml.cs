using System.Windows;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Views;

/// <summary>Resolves the player-versus-selected-profile ambiguity of a same-game RON import.</summary>
public partial class RonImportDestinationWindow : Window
{
    public RonImportDestinationWindow(string selectedFaceDisplayName)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        SelectedFaceText.Text = selectedFaceDisplayName;
    }

    public RonImportDestination? Destination { get; private set; }

    private void OnPlayerWorkspace(object sender, RoutedEventArgs e) =>
        Complete(RonImportDestination.PlayerWorkspace);

    private void OnSelectedPccFace(object sender, RoutedEventArgs e) =>
        Complete(RonImportDestination.SelectedPccFace);

    private void Complete(RonImportDestination destination)
    {
        Destination = destination;
        DialogResult = true;
    }
}
