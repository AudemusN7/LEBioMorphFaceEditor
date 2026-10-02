using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MorphFaceEditor.Localisation;

/// <summary>Authoring information travels with the master as harmless XML comments.</summary>
public sealed record EntryAnnotation(string Key, string? ReviewedHash = null, bool DoNotTranslate = false, string Note = "", string? SourceHash = null);
public sealed record CatalogueItem(string Key, string Text, string Group);
public sealed record CatalogueData(XDocument Document, IReadOnlyDictionary<string, string> Strings,
    IReadOnlyDictionary<string, EntryAnnotation> Annotations, IReadOnlyList<CatalogueItem> Items);

/// <summary>String-only XML parsing and validation shared by the editor and authoring tool.</summary>
public static partial class StringCatalogue
{
    public const string AnnotationPrefix = "localiser: ";
    public static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static CatalogueData Read(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root?.Name != Presentation + "ResourceDictionary" || root.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
            throw new FormatException("Expected a string-only ResourceDictionary.");
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var annotations = new Dictionary<string, EntryAnnotation>(StringComparer.Ordinal);
        var items = new List<CatalogueItem>();
        string? group = null;
        foreach (var node in root.Nodes())
        {
            if (node is XComment comment)
            {
                var text = comment.Value.Trim();
                if (text.StartsWith(AnnotationPrefix, StringComparison.Ordinal))
                {
                    EntryAnnotation annotation;
                    try
                    {
                        annotation = JsonSerializer.Deserialize<EntryAnnotation>(text[AnnotationPrefix.Length..])
                            ?? throw new FormatException("Empty localiser annotation.");
                    }
                    catch (JsonException exception) { throw new FormatException("Invalid localiser annotation.", exception); }
                    if (annotation.Key is null || !KeyPattern().IsMatch(annotation.Key) || annotation.Note is null ||
                        !annotations.TryAdd(annotation.Key, annotation))
                        throw new FormatException("Missing or duplicate localiser annotation key.");
                }
                else if (text.Length > 0 && !text.Contains('.') && !text.Contains('\n')) group = text;
                continue;
            }
            if (node is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value)) continue;
            if (node is not XElement element || element.Name.LocalName != "String" ||
                element.Name.NamespaceName is not ("clr-namespace:System;assembly=mscorlib" or "clr-namespace:System;assembly=System.Runtime") ||
                element.HasElements || element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration &&
                    attribute.Name != Xaml + "Key" && attribute.Name != XNamespace.Xml + "space"))
                throw new FormatException("Localisation dictionaries may contain only keyed system:String values.");
            var key = (string?)element.Attribute(Xaml + "Key");
            if (key is null || !KeyPattern().IsMatch(key) || !strings.TryAdd(key, element.Value))
                throw new FormatException($"Missing, duplicate or invalid localisation key: {key}");
            items.Add(new CatalogueItem(key, element.Value, group ?? key[4..].Split('_')[0]));
        }
        return new CatalogueData(document, strings, annotations, items);
    }

    public static void ValidateOverlay(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> overlay,
        IReadOnlyDictionary<string, EntryAnnotation>? annotations = null)
    {
        foreach (var (key, value) in overlay)
        {
            if (!baseline.TryGetValue(key, out var original)) throw new FormatException($"Unknown localisation key: {key}");
            ValidateValue(key, original, value);
            if (annotations?.TryGetValue(key, out var annotation) == true && annotation.DoNotTranslate && value != original)
                throw new FormatException($"This string is excluded from translation: {key}");
        }
    }

    public static void ValidateValue(string key, string original, string value)
    {
        if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(original))
            throw new FormatException($"Empty wording: {key}");
        if (!FormatIndices(original).SequenceEqual(FormatIndices(value)))
            throw new FormatException($"Keep the same numbered placeholders as the saved wording: {key}");
        if (key.StartsWith("loc_EditorFileDrop_", StringComparison.Ordinal) && value.Contains('|'))
            throw new FormatException($"File filter labels cannot contain a separator: {key}");
        var sourceParts = original.Split('|');
        if (sourceParts.Length > 1 && sourceParts.Length % 2 == 0 &&
            sourceParts.Where((_, index) => index % 2 == 1).All(part => part.Contains('*')))
        {
            var translatedParts = value.Split('|');
            if (sourceParts.Length != translatedParts.Length || sourceParts.Where((_, index) => index % 2 == 1)
                .Where((part, index) => part != translatedParts[index * 2 + 1]).Any())
                throw new FormatException($"File filter patterns do not match: {key}");
        }
    }

    public static IReadOnlyList<int> FormatIndices(string value)
    {
        _ = CompositeFormat.Parse(value);
        var unescaped = value.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal);
        return PlaceholderPattern().Matches(unescaped).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct().Order().ToArray();
    }

    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static XComment AnnotationComment(EntryAnnotation annotation) =>
        new(AnnotationPrefix + JsonSerializer.Serialize(annotation).Replace("-", "\\u002D", StringComparison.Ordinal));
    public static bool IsAnnotation(XComment comment) => comment.Value.Trim().StartsWith(AnnotationPrefix, StringComparison.Ordinal);

    [GeneratedRegex(@"^loc_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)] private static partial Regex KeyPattern();
    [GeneratedRegex(@"\{(\d+)(?:[,}:])", RegexOptions.CultureInvariant)] private static partial Regex PlaceholderPattern();
}
