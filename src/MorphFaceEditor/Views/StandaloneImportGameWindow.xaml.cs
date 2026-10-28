using MorphFaceEditor.Core.Domain;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Views;

/// <summary>Selects the game and optional archetype for a standalone workspace.</summary>
public partial class StandaloneImportGameWindow : Window
{
    private readonly TextureCatalogService? _textureCatalog;

    public StandaloneImportGameWindow(bool newPlayerMode = false, bool newNpcMode = false,
        TextureCatalogService? textureCatalog = null)
    {
        _textureCatalog = textureCatalog;
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
            TargetGameSelector.SelectionChanged += async (_, _) => await RefreshArchetypesAsync();
            Loaded += async (_, _) => await RefreshArchetypesAsync();
        }
    }

    public string? SelectedNpcArchetype => ArchetypeBox.SelectedValue as string;

    private async Task RefreshArchetypesAsync()
    {
        var previous = SelectedNpcArchetype;
        var game = TargetGameSelector.SelectedGame;
        var resolver = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault());
        var options = game is { } selectedGame ? resolver.Archetypes(selectedGame)
            .Where(option => option.Key is not "BAF").ToArray() : [];
        // TUF has no vanilla donor. Offer it only when the installed catalogue
        // can supply the same native face used by the ordinary workspace builder.
        ArchetypeBox.ItemsSource = options.Where(option => option.Key != "TUF").ToArray();
        ArchetypeBox.SelectedValue = previous;
        if (ArchetypeBox.SelectedIndex < 0) ArchetypeBox.SelectedIndex = 0;
        if (game is { } targetGame && _textureCatalog is not null)
        {
            var catalog = await _textureCatalog.ReadAsync(targetGame);
            if (TargetGameSelector.SelectedGame != game) return;
            if (catalog.IsAvailable)
            {
                try
                {
                    resolver.Resolve(targetGame, "TUF", catalog.MorphFaceTemplates, catalog.OfficialPackagePaths);
                    ArchetypeBox.ItemsSource = options;
                }
                catch (InvalidDataException) { /* The normal missing-donor case. */ }
            }
        }
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
