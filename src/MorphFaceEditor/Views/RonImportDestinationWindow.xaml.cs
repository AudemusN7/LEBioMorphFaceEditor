using System.Collections.Generic;
using System.Windows;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Views;

/// <summary>Chooses the standalone RON route and, for NPC imports, the native racial archetype.</summary>
public partial class RonImportDestinationWindow : Window
{
    private readonly RonNpcArchetypeOption? _npcArchetype;

    public RonImportDestinationWindow(
        MorphFaceGame targetGame,
        IReadOnlyList<RonNpcArchetypeOption> archetypes,
        bool allowPlayer)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        TargetGameText.Text = targetGame switch
        {
            MorphFaceGame.LE1 => "LE1",
            MorphFaceGame.LE2 => "LE2",
            MorphFaceGame.LE3 => "LE3",
            _ => targetGame.ToString()
        };

        _npcArchetype = archetypes.Count == 1 ? archetypes[0] : null;
        ArchetypeText.Text = _npcArchetype?.DisplayName ?? "No compatible archetype available";
        PlayerButton.Visibility = allowPlayer ? Visibility.Visible : Visibility.Collapsed;
        PlayerButton.IsEnabled = allowPlayer;
        PlayerButton.IsDefault = allowPlayer;
        NpcButton.IsDefault = !allowPlayer;
    }

    public RonImportDestination? Destination { get; private set; }

    public string? SelectedNpcArchetypeKey { get; private set; }

    private void OnPlayerMorph(object sender, RoutedEventArgs e)
    {
        Complete(RonImportDestination.PlayerWorkspace);
    }

    private void OnNpcMorph(object sender, RoutedEventArgs e)
    {
        if (_npcArchetype is not { } archetype)
        {
            ValidationText.Text = "No NPC archetype is available.";
            return;
        }

        SelectedNpcArchetypeKey = archetype.Key;
        Complete(RonImportDestination.NpcFace);
    }

    private void Complete(RonImportDestination destination)
    {
        Destination = destination;
        DialogResult = true;
    }
}
