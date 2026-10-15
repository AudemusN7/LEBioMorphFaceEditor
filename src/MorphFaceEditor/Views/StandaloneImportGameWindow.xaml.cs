using MorphFaceEditor.Core.Domain;
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
            Title = Loc.Text("loc_StandaloneImportGameWindow_NewPlayerMorph");
            Height = 300;
            PromptText.Text = Loc.Text("loc_StandaloneImportGameWindow_NewPlayerPrompt");
            SexPanel.Visibility = Visibility.Visible;
            ContinueButton.Content = Loc.Text("loc_StandaloneImportGameWindow_Continue");
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
            ValidationText.Text = Loc.Text("loc_StandaloneImportGameWindow_ChooseTargetGame");
            return;
        }

        SelectedGame = choice.Game;
        DialogResult = true;
    }

    private sealed record GameChoice(MorphFaceGame Game, string Label);
}
