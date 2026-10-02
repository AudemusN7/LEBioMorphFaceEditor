using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml;
using MorphFaceEditor.Localisation;

namespace MorphFaceEditor.Infrastructure;

/// <summary>One catalogue for XAML, generated UI text and metadata. Language is applied at startup.</summary>
public static class Loc
{
    public const string DefaultLanguage = "en-GB";
    private static readonly Lazy<CatalogueData> English = new(ReadEmbeddedCatalogue);
    private static IReadOnlyDictionary<string, string>? _active;
    public static string Language { get; private set; } = DefaultLanguage;
    public static int TranslatedStringCount { get; private set; }
    public static string Text(string key) => (_active ?? English.Value.Strings).TryGetValue(key, out var value)
        ? value : throw new KeyNotFoundException($"Unknown localisation key: {key}");
    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    internal static string NormalizeLanguage(string? language) =>
        LanguageOption.All.FirstOrDefault(option => string.Equals(option.Code, language, StringComparison.OrdinalIgnoreCase))?.Code
        ?? DefaultLanguage;

    internal static void Initialize(ResourceDictionary resources, string? language, string? directory = null)
    {
        var selected = NormalizeLanguage(language);
        var values = new Dictionary<string, string>(English.Value.Strings, StringComparer.Ordinal);
        var annotations = new Dictionary<string, EntryAnnotation>(English.Value.Annotations, StringComparer.Ordinal);
        directory ??= Path.Combine(AppContext.BaseDirectory, "Localisation");
        ApplyFile(Path.Combine(directory, $"Strings.{DefaultLanguage}.xaml"), values, annotations, master: true);
        TranslatedStringCount = selected == DefaultLanguage ? values.Count
            : ApplyFile(Path.Combine(directory, $"Strings.{selected}.xaml"), values, annotations, master: false);
        // Replace strings only. Theme resources and unrelated application resources are untouched.
        foreach (var (key, value) in values) resources[key] = value;
        Language = selected;
        _active = values;
        // UI language must never change decimal parsing or file-format serialization.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(selected);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
    }

    private static int ApplyFile(string path, Dictionary<string, string> target, Dictionary<string, EntryAnnotation> annotations, bool master)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            using var stream = File.OpenRead(path);
            var translated = StringCatalogue.Read(stream);
            foreach (var key in translated.Annotations.Keys)
                if (!target.ContainsKey(key)) throw new FormatException($"Unknown localisation key: {key}");
            StringCatalogue.ValidateOverlay(target, translated.Strings, master ? null : annotations);
            foreach (var (key, value) in translated.Strings) target[key] = value;
            if (master)
            {
                foreach (var key in translated.Strings.Keys) annotations.Remove(key);
                foreach (var (key, annotation) in translated.Annotations) annotations[key] = annotation;
            }
            return translated.Strings.Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or FormatException)
        {
            AppLog.Warning($"Localisation file {Path.GetFileName(path)} was ignored: {exception.Message}");
            return 0;
        }
    }

    internal static IReadOnlyDictionary<string, string> ReadEmbeddedEnglish() => ReadEmbeddedCatalogue().Strings;

    private static CatalogueData ReadEmbeddedCatalogue()
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("MorphFaceEditor.Localisation.Strings.en-GB.xaml")
            ?? throw new InvalidOperationException("The embedded English localisation catalogue is missing.");
        return StringCatalogue.Read(stream);
    }

    internal static IReadOnlyDictionary<string, string> ReadDictionary(Stream stream) => StringCatalogue.Read(stream).Strings;

    internal static void ValidateOverlay(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> overlay)
        => StringCatalogue.ValidateOverlay(baseline, overlay);
}

public sealed record LanguageOption(string Code, string NativeNameKey, string FlagKey)
{
    public string NativeName => Loc.Text(NativeNameKey);
    public System.Windows.Media.ImageSource? Flag => Application.Current?.TryFindResource(FlagKey) as System.Windows.Media.ImageSource;
    public static IReadOnlyList<LanguageOption> All { get; } =
    [
        new("en-GB", "loc_Language_en_GB", "Flag.UK"), new("en-US", "loc_Language_en_US", "Flag.US"),
        new("fr", "loc_Language_fr", "Flag.FR"), new("es", "loc_Language_es", "Flag.ES"),
        new("de", "loc_Language_de", "Flag.DE"), new("it", "loc_Language_it", "Flag.IT"),
        new("pl", "loc_Language_pl", "Flag.PL"), new("ru", "loc_Language_ru", "Flag.RU"),
        new("zh-Hans", "loc_Language_zh_Hans", "Flag.CN"), new("ja", "loc_Language_ja", "Flag.JP")
    ];
}
