using System.Windows;
using Microsoft.Win32;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.ViewModels;
using ThemeMode = MorphFaceEditor.Infrastructure.ThemeMode;

namespace MorphFaceEditor.Views;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _operating;
    private readonly ThemeManager? _themes;
    private bool _updatingTheme;

    public SettingsWindow(MainWindowViewModel viewModel) : this(viewModel, (Application.Current as App)?.Themes) { }

    internal SettingsWindow(MainWindowViewModel viewModel, ThemeManager? themes)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _viewModel = viewModel;
        _themes = themes;
        ThemeSelector.IsEnabled = themes is not null;
        AccentColourButton.IsEnabled = themes is not null;
        if (themes is not null)
        {
            themes.Changed += OnThemeChanged;
            UpdateThemeSelection();
            Closed += (_, _) => themes.Changed -= OnThemeChanged;
        }
        Closing += (_, args) => args.Cancel = _operating;
    }

    public bool ClearDataRequested { get; private set; }

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
            OperationStatus.Text = $"Could not save your theme: {exception.Message}";
        }
    }
    private void OnAccentColourClick(object sender, RoutedEventArgs e)
    {
        if (_themes is not null) _ = new AccentColorPickerWindow(_themes) { Owner = this }.ShowDialog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnGenerateLogsClick(object sender, RoutedEventArgs e)
    {
        if (_operating || _viewModel.IsBusy) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export MFE diagnostic logs",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"MFE-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        SetOperating(true);
        OperationStatus.Text = "Collecting logs, workspace and databases…";
        try
        {
            var result = await _viewModel.GenerateDiagnosticsAsync(dialog.FileName);
            OperationStatus.Text = $"Exported {result.Entries.Count} file(s).";
            _ = new EditorMessageWindow("Diagnostic ZIP exported", $"Saved to:\n{result.Path}" +
                (result.Warnings.Count > 0 ? "\n\n" + string.Join("\n", result.Warnings) : ""))
                { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            OperationStatus.Text = "Export failed.";
            _ = new EditorMessageWindow("Diagnostic export failed", exception.Message)
                { Owner = this }.ShowDialog();
        }
        finally { SetOperating(false); }
    }

    private async void OnClearDataClick(object sender, RoutedEventArgs e)
    {
        if (_operating || _viewModel.IsBusy) return;
        var confirmation = new EditorMessageWindow("Clear Application Data?",
            "This removes all MFE preferences, logs, temporary workspaces and mesh/texture databases, " +
            "including legacy application data. MFE will close after checking for unsaved work.\n\n" +
            "Source files outside MFE's AppData folders remain in place. Databases will need to be rebuilt when MFE is reopened.",
            confirmation: true, primaryLabel: "Clear and Exit", secondaryLabel: "Cancel") { Owner = this };
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
        ClearDataButton.IsEnabled = !operating;
        CloseButton.IsEnabled = !operating;
        ThemeSelector.IsEnabled = !operating && _themes is not null;
        AccentColourButton.IsEnabled = !operating && _themes is not null;
    }
}
