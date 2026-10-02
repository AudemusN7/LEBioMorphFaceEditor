using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Views;
using ThemeMode = MorphFaceEditor.Infrastructure.ThemeMode;

namespace MorphFaceEditor.Tests;

internal static class LocalisationTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("localisation: master dictionary and every source reference agree", CatalogueCoverage),
        new("localisation: XAML user-facing text is resource-backed", XamlCoverage),
        new("localisation: translation placeholders may reorder but cannot disappear", PlaceholderValidation),
        new("localisation: dictionary rejects duplicate keys, objects and unknown translations", DictionaryValidation),
        new("localisation: file-dialog filter syntax is preserved", FilterValidation),
        new("localisation: language preferences persist and preserve independent settings", PreferencesRoundTrip)
    ];

    private static void CatalogueCoverage()
    {
        var english = Loc.ReadEmbeddedEnglish();
        TestAssert.True(english.Count > 0, "English catalogue is empty.");
        Loc.ValidateOverlay(english, english);
        var source = FindSource();
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(path =>
                     Path.GetExtension(path) is ".cs" or ".xaml" &&
                     !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                     !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                     !path.Contains(Path.DirectorySeparatorChar + "Localisation" + Path.DirectorySeparatorChar)))
        {
            foreach (var match in Regex.Matches(File.ReadAllText(path), @"loc_[A-Za-z0-9_]+").Cast<Match>().Where(match => !match.Value.EndsWith('_')))
                TestAssert.True(english.ContainsKey(match.Value), $"Missing string {match.Value} in {path}.");
        }
        foreach (var locale in LanguageOption.All)
        {
            using var stream = File.OpenRead(Path.Combine(source, "Localisation", $"Strings.{locale.Code}.xaml"));
            Loc.ValidateOverlay(english, Loc.ReadDictionary(stream));
        }
    }

    private static void XamlCoverage()
    {
        var properties = new HashSet<string> { "Title", "Text", "Content", "Header", "ToolTip", "InputGestureText", "AutomationProperties.Name", "AutomationProperties.HelpText" };
        foreach (var path in Directory.EnumerateFiles(FindSource(), "*.xaml", SearchOption.AllDirectories).Where(path =>
                     !path.Contains(Path.DirectorySeparatorChar + "Localisation" + Path.DirectorySeparatorChar) &&
                     !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                     !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes().Where(attribute => properties.Contains(attribute.Name.LocalName)))
                    Check(attribute.Value, path);
                if (element.Name.LocalName == "Setter" && properties.Contains((string?)element.Attribute("Property") ?? ""))
                    Check((string?)element.Attribute("Value") ?? "", path);
                if (element.Name.LocalName is "TextBlock" or "Run" or "Button" or "Label" or "MenuItem" or "Hyperlink")
                    foreach (var text in element.Nodes().OfType<XText>()) Check(text.Value, path);
            }
        }
        static void Check(string value, string path) =>
            TestAssert.True(string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith('{') || !value.Any(char.IsLetter),
                $"Unextracted XAML text in {path}: {value}");
    }

    private static void PlaceholderValidation()
    {
        var baseline = new Dictionary<string, string> { ["loc_test"] = "Saved {0} files to {1}; {{literal}}" };
        Loc.ValidateOverlay(baseline, new Dictionary<string, string> { ["loc_test"] = "To {1}: {0} saved; {{literal}}" });
        Reject(() => Loc.ValidateOverlay(baseline, new Dictionary<string, string> { ["loc_test"] = "Saved {0}" }));
        Reject(() => Loc.ValidateOverlay(baseline, new Dictionary<string, string> { ["loc_test"] = "Saved {0} to {2}" }));
        Reject(() => Loc.ValidateOverlay(baseline, new Dictionary<string, string> { ["loc_test"] = "Saved {0" }));
        Reject(() => Loc.ValidateOverlay(baseline, new Dictionary<string, string> { ["loc_test"] = " " }));
    }

    private static void DictionaryValidation()
    {
        Reject(() => Read("<system:String x:Key=\"loc_test\">one</system:String><system:String x:Key=\"loc_test\">two</system:String>"));
        Reject(() => Read("<Button x:Key=\"loc_test\" Content=\"execute arbitrary XAML\" />"));
        Reject(() => Read("<system:String x:Key=\"loc_test\"><Run>nested markup</Run></system:String>"));
        Reject(() => Loc.ValidateOverlay(Loc.ReadEmbeddedEnglish(), new Dictionary<string, string> { ["loc_unknown"] = "text" }));
        var strings = Read("<system:String x:Key=\"loc_test\" xml:space=\"preserve\">  Français\n日本語  </system:String>");
        TestAssert.Equal("  Français\n日本語  ", strings["loc_test"]);
    }

    private static void FilterValidation()
    {
        var baseline = new Dictionary<string, string> { ["loc_test"] = "Package (*.pcc)|*.pcc|All files (*.*)|*.*" };
        Loc.ValidateOverlay(baseline, new Dictionary<string, string> { ["loc_test"] = "Paquet (*.pcc)|*.pcc|Tous (*.*)|*.*" });
        Reject(() => Loc.ValidateOverlay(baseline, new Dictionary<string, string> { ["loc_test"] = "Paquet|*.zip|Tous|*.*" }));
        Reject(() => Loc.ValidateOverlay(baseline, new Dictionary<string, string> { ["loc_test"] = "Paquet|*.pcc" }));
        Reject(() => Loc.ValidateOverlay(new Dictionary<string, string> { ["loc_EditorFileDrop_AllFilesLabel"] = "All files" },
            new Dictionary<string, string> { ["loc_EditorFileDrop_AllFilesLabel"] = "Tous|broken" }));
    }

    private static void PreferencesRoundTrip() => WithScratch(root =>
    {
        var path = Path.Combine(root, "preferences.json");
        var store = new ApplicationPreferencesStore(path);
        TestAssert.Equal("en-GB", store.Current.Language);
        store.Update(current => current with { SuppressWelcome = true, AccentHue = 123, RecentFiles = ["keep.pcc"] });
        var independent = new ApplicationPreferencesStore(path);
        independent.Update(current => current with { Language = "ja" });
        store.Update(current => current with { Mode = ThemeMode.Dark });
        var read = new ApplicationPreferencesStore(path).Current;
        TestAssert.Equal("ja", read.Language);
        TestAssert.Equal(ThemeMode.Dark, read.Mode);
        TestAssert.True(read.SuppressWelcome && read.AccentHue == 123 && read.RecentFiles is ["keep.pcc"], "Language clobbered unrelated preferences.");
        File.WriteAllText(path, "{\"SuppressWelcome\":true}");
        TestAssert.Equal("en-GB", new ApplicationPreferencesStore(path).Current.Language);
        TestAssert.Equal("en-GB", Loc.NormalizeLanguage("../malicious"));
        TestAssert.Equal("en-GB", Loc.NormalizeLanguage(null));
        TestAssert.Equal("en-US", Loc.NormalizeLanguage("EN-us"));
    });

    // Called within the existing STA application smoke check, without starting the product app.
    internal static void VerifyWindow(Application app, SettingsWindow settings, string preferencesPath)
    {
        var selector = (ComboBox)settings.FindName("LanguageSelector");
        TestAssert.True(selector.IsEnabled, "Language selector is disabled.");
        TestAssert.Equal(10, selector.Items.Count);
        TestAssert.Equal("en-GB", ((LanguageOption)selector.SelectedItem).Code);
        foreach (var option in LanguageOption.All)
        {
            var image = app.TryFindResource(option.FlagKey) as DrawingImage;
            TestAssert.True(image is not null && !image.Drawing.Bounds.IsEmpty, $"Missing flag: {option.Code}");
        }
        selector.SelectedItem = LanguageOption.All.Single(option => option.Code == "ja");
        TestAssert.Equal("ja", new ApplicationPreferencesStore(preferencesPath).Current.Language);

        // Selecting a language persists the request, leaving the current session coherent.
        TestAssert.Equal("Settings", settings.Title);
        TestAssert.Equal("en-GB", Loc.Language);
        selector.SelectedItem = LanguageOption.All[0];
        WithScratch(root =>
        {
            var culture = CultureInfo.CurrentCulture;
            var uiCulture = CultureInfo.CurrentUICulture;
            var defaultUi = CultureInfo.DefaultThreadCurrentUICulture;
            try
            {
                File.WriteAllText(Path.Combine(root, "Strings.fr.xaml"), DictionaryXml("<system:String x:Key=\"loc_Settings_Title\">Paramètres</system:String>"));
                Loc.Initialize(app.Resources, "fr", root);
                TestAssert.Equal("Paramètres", settings.Title);
                TestAssert.Equal("Paramètres", Loc.Text("loc_Settings_Title"));
                TestAssert.Equal("Cancel", Loc.Text("loc_Settings_Cancel"));
                TestAssert.Equal(culture, CultureInfo.CurrentCulture);
                TestAssert.Equal("fr", CultureInfo.CurrentUICulture.Name);
                File.WriteAllText(Path.Combine(root, "Strings.fr.xaml"), DictionaryXml("<system:String x:Key=\"loc_Settings_Title\">Paramètres</system:String><system:String x:Key=\"loc_Settings_SavedTo\">Missing placeholder</system:String>"));
                Loc.Initialize(app.Resources, "fr", root);
                TestAssert.Equal("Settings", settings.Title);
                TestAssert.Equal(0, Loc.TranslatedStringCount);
            }
            finally
            {
                Loc.Initialize(app.Resources, "en-GB", root);
                CultureInfo.CurrentUICulture = uiCulture;
                CultureInfo.DefaultThreadCurrentUICulture = defaultUi;
            }
        });
    }

    private static string DictionaryXml(string entries) =>
        "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:system=\"clr-namespace:System;assembly=mscorlib\">" + entries + "</ResourceDictionary>";
    private static IReadOnlyDictionary<string, string> Read(string entries)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DictionaryXml(entries)));
        return Loc.ReadDictionary(stream);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("An invalid localisation was accepted.");
    }
    private static string FindSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var source = Path.Combine(directory.FullName, "src", "MorphFaceEditor");
            if (Directory.Exists(source)) return source;
        }
        throw new DirectoryNotFoundException("Cannot find localisation sources.");
    }
    private static void WithScratch(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-Localisation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
