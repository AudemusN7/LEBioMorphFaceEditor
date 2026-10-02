using System.Numerics;

namespace MorphFaceEditor.Infrastructure;

// Retained only for reading the pre-consolidation document.
internal sealed record BackgroundColourPreference(float Red, float Green, float Blue);
internal sealed record StartupPreferences(bool SuppressWelcome = false, BackgroundColourPreference? BackgroundColour = null);

public sealed class StartupPreferencesService
{
    private readonly ApplicationPreferencesStore _store;
    internal StartupPreferencesService(string storagePath) : this(new ApplicationPreferencesStore(storagePath)) { }
    internal StartupPreferencesService(ApplicationPreferencesStore store) => _store = store;
    internal static StartupPreferencesService CreateDefault() => new(ApplicationPreferencesStore.CreateDefault());
    internal StartupPreferences Current => new(_store.Current.SuppressWelcome,
        _store.Current.ViewportBackground is { FollowTheme: false } background ? background.Colour : null);
    internal void SetSuppressWelcome(bool value) => Save(p => p with { SuppressWelcome = value });
    internal Vector4? BackgroundColor => Current.BackgroundColour is { } colour && ApplicationPreferencesStore.ValidColour(colour)
        ? new Vector4(colour.Red, colour.Green, colour.Blue, 1) : null;
    internal void SetBackgroundColor(Vector4 colour) => Save(p => p with
    {
        ViewportBackground = new(false, new(colour.X, colour.Y, colour.Z))
    });
    internal void FollowThemeBackground() => Save(p => p with { ViewportBackground = new() });
    private void Save(Func<ApplicationPreferences, ApplicationPreferences> change)
    {
        try { _store.Update(change); }
        catch (Exception exception) { AppLog.Warning($"Startup preferences could not be saved: {exception.Message}"); }
    }
}
