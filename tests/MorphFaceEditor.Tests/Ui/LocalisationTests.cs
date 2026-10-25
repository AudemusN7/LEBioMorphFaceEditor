using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Localisation;

namespace MorphFaceEditor.Tests;

internal static class LocalisationTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("localisation master, embedded dictionaries and source keys agree", CatalogueCoverage),
        new("localisation rejects unsafe markup, broken placeholders and changed file filters", InvalidCataloguesAreRejected),
        new("localisation keeps technical diagnostics out of UI explanations and formats nested labels", DiagnosticBoundary)
    ];

    private static void CatalogueCoverage()
    {
        var englishData = EmbeddedCatalogue.Read("en-GB");
        var english = englishData.Strings;
        TestAssert.True(english.Count > 0, "English catalogue is empty.");
        Loc.ValidateOverlay(english, english);
        var source = FindSource();
        foreach (var path in Directory.EnumerateFiles(Directory.GetParent(source)!.FullName, "*", SearchOption.AllDirectories).Where(path =>
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
            var authored = Loc.ReadDictionary(stream);
            Loc.ValidateOverlay(english, authored, englishData.RetiredKeys);
            var embedded = EmbeddedCatalogue.Read(locale.Code).Strings;
            TestAssert.Equal(authored.Count, embedded.Count);
            foreach (var (key, value) in authored)
                TestAssert.Equal(value, embedded[key]);
        }

        var french = EmbeddedCatalogue.Read("fr");
        var resources = new ResourceDictionary();
        Loc.Initialize(resources, "fr");
        foreach (var key in englishData.RetiredKeys)
            TestAssert.True(!resources.Contains(key), $"Retired key {key} leaked into runtime resources.");
        TestAssert.Equal(french.Strings.Keys.Count(english.ContainsKey), Loc.TranslatedStringCount);
        Loc.Initialize(new ResourceDictionary(), Loc.DefaultLanguage);
    }

    private static void DiagnosticBoundary()
    {
        using var suppressedLogs = AppLog.SuppressPersistentWrites();
        const string diagnostic = "UNTRANSLATED_LIBRARY_DIAGNOSTIC";
        TestAssert.Equal(Loc.Text("loc_Error_UnexpectedFailure"),
            UserFacingMessageFormatter.Format(new InvalidOperationException(diagnostic)));
        TestAssert.Equal(Loc.Text("loc_Error_TechnicalWarning"), UserFacingMessageFormatter.FormatDiagnostic(diagnostic));

        var localised = Loc.Error(new InvalidDataException(Loc.Text("loc_Pcsav_InvalidFile")));
        TestAssert.Equal(localised.Message, UserFacingMessageFormatter.Format(localised));
        var structured = UserFacingMessage.CreateError("loc_Error_InvalidData", diagnostic);
        TestAssert.Equal(Loc.Text("loc_Error_InvalidData"), UserFacingMessageFormatter.Format(structured));

        var warning = UserFacingMessage.Create("loc_RonImport_LegacyAttachmentOmitted",
            UserFacingMessage.Create("loc_Services_MeshMaterial_HairMeshKind"), "BIOG_Test.HairMesh");
        TestAssert.Equal(Loc.Format("loc_RonImport_LegacyAttachmentOmitted",
            Loc.Text("loc_Services_MeshMaterial_HairMeshKind"), "BIOG_Test.HairMesh"),
            UserFacingMessageFormatter.Format(warning));
    }

    private static void InvalidCataloguesAreRejected()
    {
        Reject(() => Read("<system:String x:Key=\"loc_test\">one</system:String><system:String x:Key=\"loc_test\">two</system:String>"));
        Reject(() => Read("<Button x:Key=\"loc_test\" Content=\"execute arbitrary XAML\" />"));
        Reject(() => Read("<system:String x:Key=\"loc_test\"><Run>nested markup</Run></system:String>"));
        Reject(() => Loc.ValidateOverlay(Loc.ReadEmbeddedEnglish(), new Dictionary<string, string> { ["loc_unknown"] = "text" }));
        var strings = Read("<system:String x:Key=\"loc_test\" xml:space=\"preserve\">  Français\n日本語  </system:String>");
        TestAssert.Equal("  Français\n日本語  ", strings["loc_test"]);
        var placeholders = new Dictionary<string, string> { ["loc_test"] = "Saved {0} files to {1}; {{literal}}" };
        Loc.ValidateOverlay(placeholders, new Dictionary<string, string> { ["loc_test"] = "To {1}: {0} saved; {{literal}}" });
        Reject(() => Loc.ValidateOverlay(placeholders, new Dictionary<string, string> { ["loc_test"] = "Saved {0}" }));
        Reject(() => Loc.ValidateOverlay(placeholders, new Dictionary<string, string> { ["loc_test"] = "Saved {0} to {2}" }));
        Reject(() => Loc.ValidateOverlay(placeholders, new Dictionary<string, string> { ["loc_test"] = "Saved {0" }));
        Reject(() => Loc.ValidateOverlay(placeholders, new Dictionary<string, string> { ["loc_test"] = " " }));
        var filter = new Dictionary<string, string> { ["loc_test"] = "Package (*.pcc)|*.pcc|All files (*.*)|*.*" };
        Loc.ValidateOverlay(filter, new Dictionary<string, string> { ["loc_test"] = "Paquet (*.pcc)|*.pcc|Tous (*.*)|*.*" });
        Reject(() => Loc.ValidateOverlay(filter, new Dictionary<string, string> { ["loc_test"] = "Paquet|*.zip|Tous|*.*" }));
        Reject(() => Loc.ValidateOverlay(filter, new Dictionary<string, string> { ["loc_test"] = "Paquet|*.pcc" }));
        Reject(() => Loc.ValidateOverlay(new Dictionary<string, string> { ["loc_EditorFileDrop_AllFilesLabel"] = "All files" },
            new Dictionary<string, string> { ["loc_EditorFileDrop_AllFilesLabel"] = "Tous|broken" }));
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
}
