using MorphFaceEditor.Core.Domain;
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
    private bool _hasLoaded;
    private bool _refreshingStatus;
    public TextureRegistrySettingsWindow(TextureRegistrySettingsViewModel viewModel)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = viewModel;
        Closing += OnClosing;
        Loaded += async (_, _) =>
        {
            _hasLoaded = true;
            await RefreshStatusAsync();
        };
        Activated += async (_, _) =>
        {
            if (_hasLoaded) await RefreshStatusAsync();
        };
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshingStatus || _addingAssets ||
            DataContext is not TextureRegistrySettingsViewModel { IsBuilding: false } settings) return;
        _refreshingStatus = true;
        try { await settings.RefreshAsync(); }
        finally { _refreshingStatus = false; }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_addingAssets || DataContext is TextureRegistrySettingsViewModel { CanClose: false }) e.Cancel = true;
    }

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

    private async void OnRescanModsClick(object sender, RoutedEventArgs e)
    {
        if (_addingAssets) return;
        if (sender is FrameworkElement { DataContext: TextureRegistrySettingsRowViewModel row } &&
            DataContext is TextureRegistrySettingsViewModel settings && row.CanRescanMods)
            await settings.RebuildModsAsync(row.Game);
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
            Loc.Text("loc_TextureRegistrySettingsWindow_KeepCustomAssetsTitle"),
            Loc.Format("loc_TextureRegistrySettingsWindow_KeepCustomAssetsMessage", string.Join(", ", affected)),
            confirmation: true,
            primaryLabel: Loc.Text("loc_TextureRegistrySettingsWindow_Keep"),
            secondaryLabel: Loc.Text("loc_TextureRegistrySettingsWindow_Discard"),
            cancelLabel: Loc.Text("loc_Common_Cancel"))
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
            Title = Loc.Format("loc_TextureRegistrySettingsWindow_ChooseGamePccTitle", row.Game),
            Filter = EditorFileDrop.PackageFilter,
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
                throw Loc.Error(new InvalidDataException(Loc.Format(
                    "loc_TextureRegistrySettingsWindow_PccBelongsToDifferentGame", inventory.Game, row.Game)));
            var selection = new ManualAssetSelectionWindow(row.Game, inventory,
                settings.GetManualAssets(row.Game)) { Owner = this };
            if (selection.ShowDialog() != true) return;
            await settings.AppendManualAsync(row.Game, selection.SelectedAssets);
            _ = new EditorMessageWindow(Loc.Text("loc_TextureRegistrySettingsWindow_CustomAssetsAddedTitle"),
                Loc.Format("loc_TextureRegistrySettingsWindow_CustomAssetsAddedMessage", selection.SelectedAssets.Count, row.Game))
            {
                Owner = this
            }.ShowDialog();
        }
        catch (Exception exception)
        {
            AppLog.Error($"REGISTRY:ADD-ASSETS-FAILED {row.Game}", exception);
            _ = new EditorMessageWindow(Loc.Text("loc_TextureRegistrySettingsWindow_CustomAssetImportFailedTitle"), UserFacingMessageFormatter.Format(exception))
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
