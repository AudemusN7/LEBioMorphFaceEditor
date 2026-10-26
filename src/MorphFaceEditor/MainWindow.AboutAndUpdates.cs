using System.Net.Http;
using System.Windows;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;
using MorphFaceEditor.Views;

namespace MorphFaceEditor;

public partial class MainWindow
{
    private readonly HttpClient _updateHttpClient = new();
    private readonly CancellationTokenSource _updateCancellation = new();
    private GitHubUpdateService _updateService = null!;
    private GitHubReleaseInfo? _availableRelease;
    private bool _updatesClosed;

    private void InitializeAboutAndUpdates()
    {
        _updateService = new GitHubUpdateService(_updateHttpClient, ApplicationInfo.AssemblyVersion);
        ContentRendered += OnStartupUpdateCheck;
    }

    private async void OnStartupUpdateCheck(object? sender, EventArgs e)
    {
        ContentRendered -= OnStartupUpdateCheck;
        try
        {
            var result = await _updateService.CheckForUpdatesAsync(_updateCancellation.Token);
            if (_updatesClosed) return;

            if (result.Status == GitHubUpdateCheckStatus.Available)
            {
                _availableRelease = result.Release;
                UpdateAvailableBadge.ToolTip = Loc.Format("loc_Updates_BadgeTooltip", result.Release!.Version);
                UpdateAvailableBadge.Visibility = Visibility.Visible;
            }
            if (result.Status == GitHubUpdateCheckStatus.Failed)
                AppLog.Warning($"UPDATES:CHECK-FAILED REASON:{result.Failure} HTTP:{result.HttpStatusCode}");
        }
        catch (OperationCanceledException) when (_updatesClosed) { }
        catch (Exception exception)
        {
            AppLog.Error("UPDATES:CHECK-FAILED", exception);
        }
    }

    private void OnUpdateAvailableClick(object sender, RoutedEventArgs e)
    {
        if (_availableRelease is { } release) ExternalLinks.Open(this, release.ReleaseUrl);
    }

    private void OnNexusModsClick(object sender, RoutedEventArgs e) => ExternalLinks.Open(this, ExternalLinks.NexusMods);
    private void OnGitHubClick(object sender, RoutedEventArgs e) => ExternalLinks.Open(this, ExternalLinks.GitHub);
    private void OnDonateClick(object sender, RoutedEventArgs e) => ExternalLinks.Open(this, ExternalLinks.Donate);
    private void OnAboutMfeClick(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void CloseAboutAndUpdates()
    {
        _updatesClosed = true;
        ContentRendered -= OnStartupUpdateCheck;
        _updateCancellation.Cancel();
        _updateHttpClient.Dispose();
        _updateCancellation.Dispose();
    }
}
