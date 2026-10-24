using System.Numerics;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

internal static class UiPresentationTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("independent preference consumers preserve each other's settings", ApplicationPreferencesPersist)
    ];

    private static void ApplicationPreferencesPersist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-Preferences-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "preferences.json");
            var colour = new Vector4(.2f, .4f, .6f, 1);
            var recentPath = Path.Combine(root, "face.pcc");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                SuppressWelcome = true, Mode = "Light", AccentHue = 285,
                ViewportBackground = new ViewportBackgroundPreference(false, new(colour.X, colour.Y, colour.Z)),
                RecentFiles = new[] { recentPath }, Language = "fr-FR", SidePaneWidths = new SidePaneWidthsPreference(350, 900)
            }));
            var store = new ApplicationPreferencesStore(path);
            var startup = new StartupPreferencesService(store);
            var recent = new RecentFileService(store);
            var independentTheme = new ApplicationPreferencesStore(path);
            startup.SetSuppressWelcome(false);
            independentTheme.Update(preferences => preferences with { Mode = ThemeMode.Dark, AccentHue = 120 });
            recent.Add(Path.Combine(root, "second.pcc"));

            var reloaded = new ApplicationPreferencesStore(path);
            TestAssert.True(!reloaded.Current.SuppressWelcome, "A separate consumer erased startup preferences.");
            TestAssert.Equal(ThemeMode.Dark, reloaded.Current.Mode);
            TestAssert.Equal(120d, reloaded.Current.AccentHue);
            TestAssert.Equal(2, reloaded.Current.RecentFiles!.Length);
            TestAssert.Equal(colour, new StartupPreferencesService(reloaded).BackgroundColor!.Value);
            TestAssert.Equal("fr-FR", reloaded.Current.Language);
            TestAssert.Equal(new SidePaneWidthsPreference(350, 900), reloaded.Current.SidePaneWidths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
