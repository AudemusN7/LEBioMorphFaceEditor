using MorphFaceEditor.Core.Domain;
using System.Windows;
using System.Windows.Controls;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Views;

/// <summary>Selects the game and optional archetype for a standalone workspace.</summary>
public partial class StandaloneImportGameWindow : Window
{
    public StandaloneImportGameWindow(bool newPlayerMode = false, bool newNpcMode = false)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        if (newPlayerMode || newNpcMode)
        {
            Title = Loc.Text(newNpcMode ? "loc_View_MainWindow_NewNpcMorph" : "loc_View_MainWindow_NewPlayerMorph");
            Height = 350;
            PromptText.Text = Loc.Text(newNpcMode ? "loc_StandaloneImportGameWindow_NewNpcPrompt" : "loc_StandaloneImportGameWindow_NewPlayerPrompt");
            ImportGamePanel.Visibility = Visibility.Collapsed;
            NewPlayerPanel.Visibility = Visibility.Visible;
            SexPanel.Visibility = newNpcMode ? Visibility.Collapsed : Visibility.Visible;
            ValidationText.Visibility = Visibility.Visible;
            CancelButton.Margin = new Thickness(0, 0, 8, 0);
            ContinueButton.Visibility = Visibility.Visible;
            ContinueButton.IsDefault = true;
            ContinueButton.Content = Loc.Text("loc_StandaloneImportGameWindow_Continue");
        }
        if (newNpcMode)
        {
            ArchetypePanel.Visibility = Visibility.Visible;
            TargetGameSelector.SelectionChanged += (_, _) => RefreshArchetypes();
            RefreshArchetypes();
        }
    }

    public string? SelectedNpcArchetype => ArchetypeBox.SelectedValue as string;

    private void RefreshArchetypes()
    {
        var previous = SelectedNpcArchetype;
        ArchetypeBox.ItemsSource = TargetGameSelector.SelectedGame is { } game
            ? new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault()).Archetypes(game)
                .Where(option => option.Key is not ("TUF" or "BAF")).ToArray()
            : [];
        ArchetypeBox.SelectedValue = previous;
        if (ArchetypeBox.SelectedIndex < 0) ArchetypeBox.SelectedIndex = 0;
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
