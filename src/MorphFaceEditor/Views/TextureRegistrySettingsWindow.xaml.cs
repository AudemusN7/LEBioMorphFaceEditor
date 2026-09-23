using System.IO;
using System.Windows;
using Microsoft.Win32;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Views;

public partial class TextureRegistrySettingsWindow : Window
{
    private bool _addingAssets;
    public TextureRegistrySettingsWindow(TextureRegistrySettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_addingAssets || DataContext is TextureRegistrySettingsViewModel { CanClose: false }) e.Cancel = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnRebuildClick(object sender, RoutedEventArgs e)
    {
        if (_addingAssets) return;
        if (sender is FrameworkElement { DataContext: TextureRegistrySettingsRowViewModel row } &&
            DataContext is TextureRegistrySettingsViewModel settings)
        {
            if (settings.IsBuilding)
            {
                settings.CancelBuild();
                return;
            }
            var keep = AskWhetherToKeepManualAssets(settings, [row.Game]);
            if (keep is not null)
                await settings.RebuildAsync(row.Game, keepManualAssets: keep.Value);
        }
    }

    private async void OnRebuildAllClick(object sender, RoutedEventArgs e)
    {
        if (_addingAssets) return;
        if (DataContext is TextureRegistrySettingsViewModel settings)
        {
            if (settings.IsBuilding)
            {
                settings.CancelBuild();
                return;
            }
            var keep = AskWhetherToKeepManualAssets(settings,
                [MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3]);
            if (keep is not null)
                await settings.RebuildAllAsync(keepManualAssets: keep.Value);
        }
    }

    private bool? AskWhetherToKeepManualAssets(TextureRegistrySettingsViewModel settings,
        IReadOnlyList<MorphFaceGame> games)
    {
        var affected = games.Where(settings.HasManualAssets).ToArray();
        if (affected.Length == 0) return true;
        var dialog = new EditorMessageWindow(
            "Keep custom assets?",
            $"Keep manually added assets for {string.Join(", ", affected)} during rebuild?\n\n" +
            "Yes: keep them and check their source PCCs. Missing assets will be listed in an error log.\n" +
            "No: discard their custom asset databases after a successful rebuild.",
            confirmation: true,
            primaryLabel: "Keep",
            secondaryLabel: "Discard",
            cancelLabel: "Cancel")
        {
            Owner = this
        };
        var answer = dialog.ShowDialog();
        return dialog.WasCancelled ? null : answer;
    }

    private async void OnAddCustomAssetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TextureRegistrySettingsRowViewModel row } ||
            DataContext is not TextureRegistrySettingsViewModel settings || !row.CanAddCustomAsset)
            return;
        var fileDialog = new OpenFileDialog
        {
            Title = $"Choose a {row.Game} PCC containing custom assets",
            Filter = "Mass Effect packages (*.pcc)|*.pcc",
            CheckFileExists = true,
            Multiselect = false
        };
        if (fileDialog.ShowDialog(this) != true) return;
        _addingAssets = true;
        var clickedButton = sender as FrameworkElement;
        if (clickedButton is not null) clickedButton.IsEnabled = false;
        try
        {
            var inventory = await Task.Run(() => PackageAssetInspector.Inventory(
                fileDialog.FileName, ["Texture2D", "SkeletalMesh"]));
            if (!inventory.Game.Equals(row.Game.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"This PCC belongs to {inventory.Game}, not {row.Game}.");
            var selection = new ManualAssetSelectionWindow(row.Game, inventory,
                settings.GetManualAssets(row.Game)) { Owner = this };
            if (selection.ShowDialog() != true) return;
            await settings.AppendManualAsync(row.Game, selection.SelectedAssets);
            _ = new EditorMessageWindow("Custom assets added",
                $"Added {selection.SelectedAssets.Count} asset(s) to the {row.Game} custom database.")
            {
                Owner = this
            }.ShowDialog();
        }
        catch (Exception exception)
        {
            AppLog.Error($"Could not add custom assets to {row.Game}.", exception);
            _ = new EditorMessageWindow("Custom asset import failed", exception.Message)
            {
                Owner = this
            }.ShowDialog();
        }
        finally
        {
            _addingAssets = false;
            if (clickedButton is not null) clickedButton.IsEnabled = row.CanAddCustomAsset;
        }
    }
}
