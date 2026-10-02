using Microsoft.Win32;

namespace MorphFaceEditor.Infrastructure;

internal interface IWindowsThemeSource : IDisposable
{
    ThemeMode Mode { get; }
    event EventHandler? Changed;
}

internal sealed class WindowsThemeSource : IWindowsThemeSource
{
    public WindowsThemeSource() => SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    public event EventHandler? Changed;
    public ThemeMode Mode
    {
        get
        {
            try
            {
                return Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 0) is int value && value != 0 ? ThemeMode.Light : ThemeMode.Dark;
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or
                UnauthorizedAccessException or System.IO.IOException)
            {
                return ThemeMode.Dark;
            }
        }
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) =>
        Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
}
