using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml;
using System.Xml.Linq;

namespace MorphFaceEditor.Infrastructure;

/// <summary>One catalogue for XAML, generated UI text and metadata. Language is applied at startup.</summary>
public static class Loc
{
    public const string DefaultLanguage = "en-GB";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(ReadEmbeddedEnglish);
    private static IReadOnlyDictionary<string, string>? _active;
    public static string Language { get; private set; } = DefaultLanguage;
    public static int TranslatedStringCount { get; private set; }
    public static string Text(string key) => (_active ?? English.Value).TryGetValue(key, out var value)
        ? value : throw new KeyNotFoundException($"Unknown localisation key: {key}");
    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    internal static string NormalizeLanguage(string? language) =>
        LanguageOption.All.FirstOrDefault(option => string.Equals(option.Code, language, StringComparison.OrdinalIgnoreCase))?.Code
        ?? DefaultLanguage;

    internal static void Initialize(ResourceDictionary resources, string? language, string? directory = null)
    {
        var selected = NormalizeLanguage(language);
        var values = new Dictionary<string, string>(English.Value, StringComparer.Ordinal);
        directory ??= Path.Combine(AppContext.BaseDirectory, "Localisation");
        ApplyFile(Path.Combine(directory, $"Strings.{DefaultLanguage}.xaml"), values);
        TranslatedStringCount = selected == DefaultLanguage ? values.Count
            : ApplyFile(Path.Combine(directory, $"Strings.{selected}.xaml"), values);
        // Replace strings only. Theme resources and unrelated application resources are untouched.
        foreach (var (key, value) in values) resources[key] = value;
        Language = selected;
        _active = values;
        // UI language must never change decimal parsing or file-format serialization.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(selected);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
    }

    private static int ApplyFile(string path, Dictionary<string, string> target)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            using var stream = File.OpenRead(path);
            var translated = ReadDictionary(stream);
            ValidateOverlay(English.Value, translated);
            foreach (var (key, value) in translated) target[key] = value;
            return translated.Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or FormatException)
        {
            AppLog.Warning($"Localisation file {Path.GetFileName(path)} was ignored: {exception.Message}");
            return 0;
        }
    }

    internal static IReadOnlyDictionary<string, string> ReadEmbeddedEnglish()
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("MorphFaceEditor.Localisation.Strings.en-GB.xaml")
            ?? throw new InvalidOperationException("The embedded English localisation catalogue is missing.");
        return ReadDictionary(stream);
    }

    internal static IReadOnlyDictionary<string, string> ReadDictionary(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = document.Root;
        if (root?.Name != presentation + "ResourceDictionary" || root.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
            throw new FormatException("Expected a string-only ResourceDictionary.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in root.Elements())
        {
            if (element.Name.LocalName != "String" ||
                element.Name.NamespaceName is not ("clr-namespace:System;assembly=mscorlib" or "clr-namespace:System;assembly=System.Runtime") ||
                element.HasElements || element.Attributes().Any(attribute =>
                    !attribute.IsNamespaceDeclaration && attribute.Name != xaml + "Key" && attribute.Name != XNamespace.Xml + "space"))
                throw new FormatException("Localisation dictionaries may contain only keyed system:String values.");
            var key = (string?)element.Attribute(xaml + "Key");
            if (key is null || !key.StartsWith("loc_", StringComparison.Ordinal) || !result.TryAdd(key, element.Value))
                throw new FormatException($"Missing, duplicate or invalid localisation key: {key}");
        }
        return result;
    }

    internal static void ValidateOverlay(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (!baseline.TryGetValue(key, out var original)) throw new FormatException($"Unknown localisation key: {key}");
            if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(original))
                throw new FormatException($"Empty translation: {key}");
            if (!FormatIndices(original).SequenceEqual(FormatIndices(value)))
                throw new FormatException($"Formatting placeholders do not match: {key}");
            if (key.StartsWith("loc_EditorFileDrop_", StringComparison.Ordinal) && value.Contains('|'))
                throw new FormatException($"File filter labels cannot contain a separator: {key}");
            // Native file-dialog filter wildcards are syntax, not translation text.
            if (original.Contains('|') && original.Split('|').Length % 2 == 0 && original.Split('|').Where((_, index) => index % 2 == 1).All(part => part.Contains('*')))
            {
                var sourceParts = original.Split('|');
                var translatedParts = value.Split('|');
                if (sourceParts.Length != translatedParts.Length || sourceParts.Where((_, index) => index % 2 == 1)
                    .Where((part, index) => part != translatedParts[index * 2 + 1]).Any())
                    throw new FormatException($"File filter patterns do not match: {key}");
            }
        }
    }

    private static IEnumerable<int> FormatIndices(string value)
    {
        // Parse first so malformed braces are rejected before a dictionary is published.
        _ = CompositeFormat.Parse(value);
        var unescaped = value.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal);
        return Regex.Matches(unescaped, @"\{(\d+)(?:[,}:])").Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order();
    }
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
