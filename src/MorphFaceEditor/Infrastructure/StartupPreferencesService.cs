using System.IO;
using System.Numerics;
using System.Text.Json;

namespace MorphFaceEditor.Infrastructure;

internal sealed record BackgroundColourPreference(float Red, float Green, float Blue);

internal sealed record StartupPreferences(
    bool SuppressWelcome = false,
    BackgroundColourPreference? BackgroundColour = null);

public sealed class StartupPreferencesService
{
    private readonly string _storagePath;

    internal StartupPreferencesService(string storagePath)
    {
        _storagePath = storagePath;
        Current = Load(storagePath);
    }

    internal static StartupPreferencesService CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LE BioMorphFace Editor",
        "startup-preferences.json"));

    internal StartupPreferences Current { get; private set; }

    internal void SetSuppressWelcome(bool suppressWelcome)
    {
        Current = Current with { SuppressWelcome = suppressWelcome };
        Save();
    }

    internal Vector4? BackgroundColor
    {
        get
        {
            var colour = Current.BackgroundColour;
            return colour is not null &&
                   float.IsFinite(colour.Red) && float.IsFinite(colour.Green) && float.IsFinite(colour.Blue)
                ? new Vector4(colour.Red, colour.Green, colour.Blue, 1)
                : null;
        }
    }

    internal void SetBackgroundColor(Vector4 color)
    {
        Current = Current with
        {
            BackgroundColour = new BackgroundColourPreference(color.X, color.Y, color.Z)
        };
        Save();
    }

    private static StartupPreferences Load(string storagePath)
    {
        if (!File.Exists(storagePath))
        {
            return new StartupPreferences();
        }
        try
        {
            return JsonSerializer.Deserialize<StartupPreferences>(File.ReadAllText(storagePath))
                   ?? new StartupPreferences();
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Startup preferences could not be read: {exception.Message}");
            return new StartupPreferences();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storagePath)
                ?? throw new InvalidOperationException("The startup-preferences path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _storagePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current));
            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Startup preferences could not be saved: {exception.Message}");
        }
    }
}
