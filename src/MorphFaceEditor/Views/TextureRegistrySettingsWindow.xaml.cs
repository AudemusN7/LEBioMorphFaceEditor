using System.Windows;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Views;

public partial class TextureRegistrySettingsWindow : Window
{
    public TextureRegistrySettingsWindow(TextureRegistrySettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is TextureRegistrySettingsViewModel { CanClose: false }) e.Cancel = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnRebuildClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TextureRegistrySettingsRowViewModel row } &&
            DataContext is TextureRegistrySettingsViewModel settings)
            await settings.RebuildAsync(row.Game);
    }

    private async void OnRebuildAllClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TextureRegistrySettingsViewModel settings)
            await settings.RebuildAllAsync();
    }
}
