using System.Windows;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Views;

/// <summary>Selects the installed game assets used to construct a standalone player workspace.</summary>
public partial class StandaloneImportGameWindow : Window
{
    public StandaloneImportGameWindow(bool newPlayerMode = false)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        if (newPlayerMode)
        {
            Title = "New Player Morph";
            Height = 300;
            PromptText.Text = "Choose the game and player for your new morph.";
            SexPanel.Visibility = Visibility.Visible;
            ContinueButton.Content = "Continue";
        }
        GameBox.ItemsSource = new[]
        {
            new GameChoice(MorphFaceGame.LE1, "LE1"),
            new GameChoice(MorphFaceGame.LE2, "LE2"),
            new GameChoice(MorphFaceGame.LE3, "LE3")
        };
        GameBox.SelectedIndex = 0;
    }

    public StandalonePlayerSex SelectedSex => SexBox.SelectedIndex == 1 ? StandalonePlayerSex.Female : StandalonePlayerSex.Male;

    public MorphFaceGame? SelectedGame { get; private set; }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (GameBox.SelectedItem is not GameChoice choice)
        {
            ValidationText.Text = "Choose a target game.";
            return;
        }

        SelectedGame = choice.Game;
        DialogResult = true;
    }

    private sealed record GameChoice(MorphFaceGame Game, string Label);
}
