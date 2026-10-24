using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MorphFaceEditor.Infrastructure;

internal enum ThemeMode { System, Light, Dark }
internal sealed record ViewportBackgroundPreference(bool FollowTheme = true, BackgroundColourPreference? Colour = null);
internal sealed record BackgroundColourPreference(float Red, float Green, float Blue);
internal sealed record SidePaneWidthsPreference(double? FaceBrowser = null, double? FaceEditor = null);
internal sealed record ApplicationPreferences(
    bool SuppressWelcome = false,
    ThemeMode Mode = ThemeMode.System,
    double? AccentHue = null,
    ViewportBackgroundPreference? ViewportBackground = null,
    string[]? RecentFiles = null,
    string Language = "en-GB",
    SidePaneWidthsPreference? SidePaneWidths = null);

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

    internal ApplicationPreferencesStore(string path)
    {
        _path = Path.GetFullPath(path);
        Current = Read() ?? new();
    }

    internal static ApplicationPreferencesStore CreateDefault() => _default ??= new(
        Path.Combine(ApplicationDataPaths.CreateDefault().CurrentDirectory, "preferences.json"));

    private ApplicationPreferences? Read()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<ApplicationPreferences>(File.ReadAllText(_path), Options) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warning($"PREFERENCES:READ-FAILED FILE:{Path.GetFileName(_path)} REASON:{exception.Message}");
            return null;
        }
    }

    internal void Update(Func<ApplicationPreferences, ApplicationPreferences> change)
    {
        // Merge with the latest document so independent preference consumers cannot overwrite each other.
        var candidate = change(Read() ?? Current);
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

    internal static bool ValidHue(double? hue) => hue is null || double.IsFinite(hue.Value) && hue >= 0 && hue < 360;
    internal static bool ValidColour(BackgroundColourPreference? colour) => colour is not null &&
        float.IsFinite(colour.Red) && float.IsFinite(colour.Green) && float.IsFinite(colour.Blue) &&
        colour.Red is >= 0 and <= 1 && colour.Green is >= 0 and <= 1 && colour.Blue is >= 0 and <= 1;
}
