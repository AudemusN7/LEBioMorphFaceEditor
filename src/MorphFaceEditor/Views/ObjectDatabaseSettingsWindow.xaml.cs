using System.Windows;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Views;

/// <summary>Displays the current per-game texture discovery database state.</summary>
public partial class ObjectDatabaseSettingsWindow : Window
{
    public ObjectDatabaseSettingsWindow(ObjectDatabaseSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnRebuildClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ObjectDatabaseSettingsRowViewModel row } &&
            DataContext is ObjectDatabaseSettingsViewModel settings)
            await settings.RebuildAsync(row.Game);
    }

    private async void OnRebuildAllClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ObjectDatabaseSettingsViewModel settings)
            await settings.RebuildAllAsync();
    }
}
