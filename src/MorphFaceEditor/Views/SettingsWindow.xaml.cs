using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.ViewModels;
using ThemeMode = MorphFaceEditor.Infrastructure.ThemeMode;

namespace MorphFaceEditor.Views;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _operating;
    private readonly ThemeManager? _themes;
    private bool _updatingTheme;
    private bool _updatingLanguage;
    private readonly ApplicationPreferencesStore _preferences;
    private string _savedGameDirectoryText;

    public SettingsWindow(MainWindowViewModel viewModel) : this(viewModel, (Application.Current as App)?.Themes) { }

    internal SettingsWindow(MainWindowViewModel viewModel, ThemeManager? themes, ApplicationPreferencesStore? preferences = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _viewModel = viewModel;
        _preferences = preferences ?? ApplicationPreferencesStore.CreateDefault();
        _savedGameDirectoryText = _preferences.Current.LegendaryEditionDirectory ??
            LegendaryExplorerCoreRuntime.LegendaryEditionDirectory ?? string.Empty;
        GameDirectoryPath.Text = _savedGameDirectoryText;
        LanguageSelector.ItemsSource = LanguageOption.All;
        UpdateLanguageSelection();
        _themes = themes;
        ThemeSelector.IsEnabled = themes is not null;
        AccentColourButton.IsEnabled = themes is not null;
        if (themes is not null)
        {
            themes.Changed += OnThemeChanged;
            UpdateThemeSelection();
            Closed += (_, _) => themes.Changed -= OnThemeChanged;
        }
        Closing += (_, args) =>
        {
            args.Cancel = _operating || !SaveGameDirectory();
        };
    }

    public bool ClearDataRequested { get; private set; }

    private void UpdateLanguageSelection()
    {
        _updatingLanguage = true;
        LanguageSelector.SelectedItem = LanguageOption.All.First(option => option.Code == Loc.NormalizeLanguage(_preferences.Current.Language));
        _updatingLanguage = false;
    }

    private void OnLanguageSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingLanguage || _preferences is null || LanguageSelector.SelectedItem is not LanguageOption selected) return;
        try
        {
            _preferences.Update(current => current with { Language = selected.Code });
            OperationStatus.Text = Loc.Text("loc_Settings_RestartNotice");
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            UpdateLanguageSelection();
            OperationStatus.Text = Loc.Format("loc_Settings_LanguageSaveFailed", UserFacingMessageFormatter.Format(exception));
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateThemeSelection();
    private void UpdateThemeSelection()
    {
        _updatingTheme = true;
        ThemeSelector.SelectedIndex = (int)_themes!.Mode;
        _updatingTheme = false;
    }
    private void OnThemeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingTheme || _themes is null || ThemeSelector.SelectedIndex < 0) return;
        try { _themes.SetMode((ThemeMode)ThemeSelector.SelectedIndex); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            UpdateThemeSelection();
            OperationStatus.Text = Loc.Format("loc_Settings_ThemeSaveFailed", UserFacingMessageFormatter.Format(exception));
        }
    }
    private void OnAccentColourClick(object sender, RoutedEventArgs e)
    {
        if (_themes is not null) _ = new AccentColorPickerWindow(_themes) { Owner = this }.ShowDialog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnGameDirectoryLostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveGameDirectory();

    private void OnGameDirectoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SaveGameDirectory();
    }

    private void OnBrowseGameDirectoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Text("loc_Settings_LegendaryEditionDirectory"),
            InitialDirectory = Directory.Exists(GameDirectoryPath.Text) ? GameDirectoryPath.Text : null,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        GameDirectoryPath.Text = dialog.FolderName;
        SaveGameDirectory();
    }

    private bool SaveGameDirectory()
    {
        var path = GameDirectoryPath.Text.Trim();
        if (path == _savedGameDirectoryText) return true;
        if (path.Length > 0 && !LegendaryExplorerCoreRuntime.IsValidLegendaryEditionDirectory(path))
        {
            OperationStatus.Text = Loc.Text("loc_Settings_InvalidGameDirectory");
            return false;
        }
        try
        {
            var directory = path.Length == 0 ? null : Path.GetFullPath(path);
            _preferences.Update(current => current with { LegendaryEditionDirectory = directory });
            var compressionAvailable = false;
            try { compressionAvailable = LegendaryExplorerCoreRuntime.SetLegendaryEditionDirectory(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException)
            {
                // The directory is already applied; a DLL retry failure must not masquerade as a failed save.
                AppLog.Error("GAME:DECOMPRESSION-FAILED after directory change", exception);
            }
            if (!compressionAvailable)
                AppLog.Warning("GAME:DECOMPRESSION-UNAVAILABLE after directory change");
            AppLog.Information($"GAME:DIRECTORY {LegendaryExplorerCoreRuntime.LegendaryEditionDirectory ?? "(not detected)"}");
            _savedGameDirectoryText = LegendaryExplorerCoreRuntime.LegendaryEditionDirectory ?? string.Empty;
            GameDirectoryPath.Text = _savedGameDirectoryText;
            OperationStatus.Text = Loc.Text(compressionAvailable
                ? "loc_Settings_GameDirectoryApplied" : "loc_Settings_GameDirectoryCompressionUnavailable");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            OperationStatus.Text = Loc.Format("loc_Settings_GameDirectorySaveFailed", UserFacingMessageFormatter.Format(exception));
            return false;
        }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppLog.FilePath) { UseShellExecute = true });
            OperationStatus.Text = string.Empty;
        }
        catch (Exception exception)
        {
            AppLog.Error("SETTINGS:OPEN-LOG-FAILED", exception);
            OperationStatus.Text = Loc.Text("loc_Settings_OpenLogFailed");
        }
    }

    private async void OnGenerateLogsClick(object sender, RoutedEventArgs e)
    {
        if (_operating || _viewModel.IsBusy) return;
        var dialog = new SaveFileDialog
        {
            Title = Loc.Text("loc_Settings_ExportLogsTitle"),
            Filter = Loc.Text("loc_Settings_ZipFilter"),
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"MFE-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        SetOperating(true);
        OperationStatus.Text = Loc.Text("loc_Settings_CollectingLogs");
        try
        {
            var result = await _viewModel.GenerateDiagnosticsAsync(dialog.FileName);
            OperationStatus.Text = Loc.Format("loc_Settings_ExportedFiles", result.Entries.Count);
            _ = new EditorMessageWindow(Loc.Text("loc_Settings_ExportedTitle"), Loc.Format("loc_Settings_SavedTo", result.Path) +
                (result.Warnings.Count > 0 ? "\n\n" + string.Join("\n", result.Warnings) : ""))
                { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            OperationStatus.Text = Loc.Text("loc_Settings_ExportFailed");
            _ = new EditorMessageWindow(Loc.Text("loc_Settings_ExportFailedTitle"), UserFacingMessageFormatter.Format(exception))
                { Owner = this }.ShowDialog();
        }
        finally { SetOperating(false); }
    }

    private async void OnClearDataClick(object sender, RoutedEventArgs e)
    {
        if (_operating || _viewModel.IsBusy) return;
        var confirmation = new EditorMessageWindow(Loc.Text("loc_Settings_ClearQuestion"),
            Loc.Text("loc_Settings_ClearConfirmation"),
            confirmation: true, primaryLabel: Loc.Text("loc_Settings_ClearExit"), secondaryLabel: Loc.Text("loc_Common_Cancel")) { Owner = this };
        if (confirmation.ShowDialog() != true) return;
        SetOperating(true);
        try
        {
            if (!await _viewModel.ConfirmCloseAsync()) return;
            ClearDataRequested = true;
        }
        finally { SetOperating(false); }
        if (ClearDataRequested) DialogResult = true;
    }

    private void SetOperating(bool operating)
    {
        _operating = operating;
        GenerateLogsButton.IsEnabled = !operating;
        OpenLogButton.IsEnabled = !operating;
        ClearDataButton.IsEnabled = !operating;
        CloseButton.IsEnabled = !operating;
        ThemeSelector.IsEnabled = !operating && _themes is not null;
        AccentColourButton.IsEnabled = !operating && _themes is not null;
        LanguageSelector.IsEnabled = !operating;
        GameDirectoryPath.IsEnabled = !operating;
        BrowseGameDirectoryButton.IsEnabled = !operating;
    }
}
