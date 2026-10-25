using MorphFaceEditor.Core.Domain;
using System.Windows;
using System.Windows.Controls;
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
            Title = Loc.Text("loc_View_MainWindow_NewPlayerMorph");
            Height = 350;
            PromptText.Text = Loc.Text("loc_StandaloneImportGameWindow_NewPlayerPrompt");
            ImportGamePanel.Visibility = Visibility.Collapsed;
            NewPlayerPanel.Visibility = Visibility.Visible;
            SexPanel.Visibility = Visibility.Visible;
            ValidationText.Visibility = Visibility.Visible;
            CancelButton.Margin = new Thickness(0, 0, 8, 0);
            ContinueButton.Visibility = Visibility.Visible;
            ContinueButton.IsDefault = true;
            ContinueButton.Content = Loc.Text("loc_StandaloneImportGameWindow_Continue");
        }
    }

    public StandalonePlayerSex SelectedSex => SexBox.SelectedIndex == 1 ? StandalonePlayerSex.Female : StandalonePlayerSex.Male;

    public MorphFaceGame? SelectedGame { get; private set; }

    private void OnImportGame(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MorphFaceGame game }) return;

        SelectedGame = game;
        DialogResult = true;
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (TargetGameSelector.SelectedGame is not MorphFaceGame game)
        {
            ValidationText.Text = Loc.Text("loc_ConvertMorphWindow_ChooseTargetGame");
            return;
        }

        SelectedGame = game;
        DialogResult = true;
    }
}
