using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MorphFaceEditor.Infrastructure;

internal sealed record ViewportBackgroundPreference(bool FollowTheme = true, BackgroundColourPreference? Colour = null);
internal sealed record ApplicationPreferences(
    bool SuppressWelcome = false,
    ThemeMode Mode = ThemeMode.System,
    double? AccentHue = null,
    ViewportBackgroundPreference? ViewportBackground = null,
    string[]? RecentFiles = null);

/// <summary>One atomic preferences document shared by all application settings.</summary>
internal sealed class ApplicationPreferencesStore
{
    private readonly string _path;
    private static ApplicationPreferencesStore? _default;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter<ThemeMode>(allowIntegerValues: false) }
    };
    internal ApplicationPreferences Current { get; private set; }

    internal ApplicationPreferencesStore(string path, bool migrateLegacy = false)
    {
        _path = Path.GetFullPath(path);
        Current = Read<ApplicationPreferences>(_path) ?? new();
        if (migrateLegacy && !File.Exists(_path)) Migrate();
    }

    internal static ApplicationPreferencesStore CreateDefault() => _default ??= new(
        Path.Combine(ApplicationDataPaths.CreateDefault().CurrentDirectory, "preferences.json"), migrateLegacy: true);

    private static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warning($"Preferences could not be read from {Path.GetFileName(path)}: {exception.Message}");
            return default;
        }
    }

    internal void Update(Func<ApplicationPreferences, ApplicationPreferences> change)
    {
        // Merge with the latest document so independent preference consumers cannot overwrite each other.
        var candidate = change(Read<ApplicationPreferences>(_path) ?? Current);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(candidate, Options));
            File.Move(temporary, _path, overwrite: true);
            Current = candidate;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Migrate()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var startupPath = Path.Combine(directory, "startup-preferences.json");
        var themePath = Path.Combine(directory, "theme-preferences.json");
        var recentPath = Path.Combine(directory, "recent-files.json");
        var startup = Read<StartupPreferences>(startupPath);
        var theme = Read<ThemePreferences>(themePath);
        var recent = Read<string[]>(recentPath);
        var colour = startup?.BackgroundColour;
        var custom = ValidColour(colour) && !IsLegacyDefault(colour!);
        Current = new(startup?.SuppressWelcome ?? false,
            theme is not null && Enum.IsDefined(theme.Mode) ? theme.Mode : ThemeMode.System,
            ValidHue(theme?.AccentHue) ? theme?.AccentHue : null,
            new(!custom, custom ? colour : null), recent);
        try
        {
            Update(_ => Current);
            // Remove only successfully imported legacy documents, after the replacement is on disk.
            foreach (var path in new[] { startup is null ? null : startupPath, theme is null ? null : themePath,
                         recent is null ? null : recentPath }.OfType<string>())
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Preferences migration could not finish: {exception.Message}");
        }
    }

    internal static bool ValidHue(double? hue) => hue is null || double.IsFinite(hue.Value) && hue >= 0 && hue < 360;
    internal static bool ValidColour(BackgroundColourPreference? colour) => colour is not null &&
        float.IsFinite(colour.Red) && float.IsFinite(colour.Green) && float.IsFinite(colour.Blue) &&
        colour.Red is >= 0 and <= 1 && colour.Green is >= 0 and <= 1 && colour.Blue is >= 0 and <= 1;

    private static bool IsLegacyDefault(BackgroundColourPreference colour)
    {
        bool Matches(float red, float green, float blue) =>
            Math.Abs(colour.Red - red) < .0001f && Math.Abs(colour.Green - green) < .0001f && Math.Abs(colour.Blue - blue) < .0001f;
        return Matches(.035f, .043f, .055f) || Matches(8 / 255f, 8 / 255f, 13 / 255f) ||
               Matches(242 / 255f, 242 / 255f, 242 / 255f);
    }
}
