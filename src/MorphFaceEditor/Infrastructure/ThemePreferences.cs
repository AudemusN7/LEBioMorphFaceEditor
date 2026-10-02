namespace MorphFaceEditor.Infrastructure;

internal enum ThemeMode { System, Light, Dark }
internal sealed record ThemePreferences(ThemeMode Mode = ThemeMode.System, double? AccentHue = null);

internal sealed class ThemePreferencesStore
{
    private readonly ApplicationPreferencesStore _store;
    public ThemePreferences Current => Enum.IsDefined(_store.Current.Mode) && ApplicationPreferencesStore.ValidHue(_store.Current.AccentHue)
        ? new(_store.Current.Mode, _store.Current.AccentHue) : new();
    public ThemePreferencesStore(string path) : this(new ApplicationPreferencesStore(path)) { }
    internal ThemePreferencesStore(ApplicationPreferencesStore store) => _store = store;
    public static ThemePreferencesStore CreateDefault() => new(ApplicationPreferencesStore.CreateDefault());
    public void Save(ThemePreferences value) => _store.Update(p => p with { Mode = value.Mode, AccentHue = value.AccentHue });
}
