using System.Globalization;
using System.IO;
using System.Windows;
using MorphFaceEditor.Localisation;

namespace MorphFaceEditor.Infrastructure;

/// <summary>One catalogue for XAML, generated UI text and metadata. Language is applied at startup.</summary>
public static class Loc
{
    public const string DefaultLanguage = "en-GB";
    private const string LocalisedErrorMarker = "MFE.LocalisedError";
    private static readonly Lazy<CatalogueData> English = new(() => EmbeddedCatalogue.Read(DefaultLanguage));
    private static IReadOnlyDictionary<string, string>? _active;
    public static string Language { get; private set; } = DefaultLanguage;
    public static int TranslatedStringCount { get; private set; }
    public static string Text(string key) => (_active ?? English.Value.Strings).TryGetValue(key, out var value)
        ? value : throw new KeyNotFoundException($"Unknown localisation key: {key}");
    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    // App-layer exceptions already authored through Loc retain their exact
    // localized message and exception type at the UI reporting boundary.
    internal static T Error<T>(T exception) where T : Exception
    {
        exception.Data[LocalisedErrorMarker] = true;
        return exception;
    }

    internal static bool IsLocalisedError(Exception exception) => exception.Data[LocalisedErrorMarker] is true;

    internal static string NormalizeLanguage(string? language) =>
        LanguageOption.All.FirstOrDefault(option => string.Equals(option.Code, language, StringComparison.OrdinalIgnoreCase))?.Code
        ?? DefaultLanguage;

    internal static void Initialize(ResourceDictionary resources, string? language)
    {
        var selected = NormalizeLanguage(language);
        var values = new Dictionary<string, string>(English.Value.Strings, StringComparer.Ordinal);
        TranslatedStringCount = values.Count;
        if (selected != DefaultLanguage)
        {
            var translated = EmbeddedCatalogue.Read(selected);
            StringCatalogue.ValidateOverlay(values, translated.Strings, English.Value.Annotations, English.Value.RetiredKeys);
            foreach (var (key, value) in translated.Strings)
                if (values.ContainsKey(key)) values[key] = value;
            TranslatedStringCount = translated.Strings.Keys.Count(values.ContainsKey);
        }
        // Replace strings only. Theme resources and unrelated application resources are untouched.
        foreach (var (key, value) in values) resources[key] = value;
        Language = selected;
        _active = values;
        // UI language must never change decimal parsing or file-format serialization.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(selected);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
    }

    internal static IReadOnlyDictionary<string, string> ReadEmbeddedEnglish() => English.Value.Strings;

    internal static IReadOnlyDictionary<string, string> ReadDictionary(Stream stream) => StringCatalogue.Read(stream).Strings;

    internal static void ValidateOverlay(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> overlay,
        IReadOnlySet<string>? retiredKeys = null)
        => StringCatalogue.ValidateOverlay(baseline, overlay, retiredKeys: retiredKeys);
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
