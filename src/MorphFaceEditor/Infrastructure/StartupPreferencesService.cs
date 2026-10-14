using System.Numerics;

namespace MorphFaceEditor.Infrastructure;

public sealed class StartupPreferencesService
{
    private readonly ApplicationPreferencesStore _store;
    internal StartupPreferencesService(string storagePath) : this(new ApplicationPreferencesStore(storagePath)) { }
    internal StartupPreferencesService(ApplicationPreferencesStore store) => _store = store;
    internal static StartupPreferencesService CreateDefault() => new(ApplicationPreferencesStore.CreateDefault());
    internal bool SuppressWelcome => _store.Current.SuppressWelcome;
    internal void SetSuppressWelcome(bool value) => Save(p => p with { SuppressWelcome = value });
    internal Vector4? BackgroundColor => _store.Current.ViewportBackground is { FollowTheme: false, Colour: { } colour } &&
        ApplicationPreferencesStore.ValidColour(colour)
        ? new Vector4(colour.Red, colour.Green, colour.Blue, 1) : null;
    internal void SetBackgroundColor(Vector4 colour) => Save(p => p with
    {
        ViewportBackground = new(false, new(colour.X, colour.Y, colour.Z))
    });
    internal void FollowThemeBackground() => Save(p => p with { ViewportBackground = new() });
    private void Save(Func<ApplicationPreferences, ApplicationPreferences> change)
    {
        try { _store.Update(change); }
        catch (Exception exception) { AppLog.Warning($"PREFERENCES:STARTUP-SAVE-FAILED REASON:{exception.Message}"); }
    }
}
