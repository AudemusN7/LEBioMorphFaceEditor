using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MorphFaceEditor.Localisation;

// XML validates the contents; lexical spans let saves retain untouched source bytes.
internal static class CatalogueLayout
{
    internal static byte[] Preserve(byte[] original, byte[] updated)
    {
        using var oldStream = new MemoryStream(original);
        using var newStream = new MemoryStream(updated);
        var before = StringCatalogue.Read(oldStream);
        var after = StringCatalogue.Read(newStream);
        using var reader = new StreamReader(new MemoryStream(original), Encoding.UTF8, true);
        var text = reader.ReadToEnd();
        var encoding = reader.CurrentEncoding;
        var edits = new List<(int Start, int Length, string Text)>();
        var elements = before.Document.Root!.Elements().GetEnumerator();
        var seenStrings = new HashSet<string>(StringComparer.Ordinal);
        var seenAnnotations = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;
        var start = 0;
        var contentStart = 0;
        var rootEnd = 0;
        XElement? element = null;
        foreach (Match token in Regex.Matches(text,
            "<!--[\\s\\S]*?-->|<!\\[CDATA\\[[\\s\\S]*?\\]\\]>|<(?:(?:\"[^\"]*\")|(?:'[^']*')|[^'\">])*>",
            RegexOptions.CultureInvariant))
        {
            var raw = token.Value;
            if (raw.StartsWith("<!--", StringComparison.Ordinal))
            {
                if (depth != 1) continue;
                var comment = new XComment(raw[4..^3]);
                if (!StringCatalogue.IsAnnotation(comment)) continue;
                var key = System.Text.Json.JsonSerializer.Deserialize<EntryAnnotation>(
                    comment.Value.Trim()[StringCatalogue.AnnotationPrefix.Length..])!.Key;
                seenAnnotations.Add(key);
                if (!after.Annotations.TryGetValue(key, out var annotation))
                    edits.Add((token.Index, token.Length, ""));
                else if (annotation != before.Annotations[key])
                    edits.Add((token.Index, token.Length, StringCatalogue.AnnotationComment(annotation).ToString()));
                continue;
            }
            if (raw.StartsWith("<!", StringComparison.Ordinal) || raw.StartsWith("<?", StringComparison.Ordinal)) continue;
            if (raw.StartsWith("</", StringComparison.Ordinal))
            {
                if (depth == 2) UpdateElement(token.Index, token.Index + token.Length, false);
                else if (depth == 1) rootEnd = token.Index;
                depth--;
            }
            else
            {
                if (depth == 1)
                {
                    if (!elements.MoveNext()) throw new FormatException("Catalogue layout does not match its strings.");
                    element = elements.Current;
                    start = token.Index;
                    contentStart = token.Index + token.Length;
                    if (raw.EndsWith("/>", StringComparison.Ordinal)) UpdateElement(contentStart, contentStart, true);
                }
                else if (depth == 0 && raw.EndsWith("/>", StringComparison.Ordinal) &&
                    (after.Strings.Count > 0 || after.Annotations.Count > 0))
                {
                    var prefix = before.Document.Root!.GetPrefixOfNamespace(before.Document.Root.Name.Namespace);
                    var tag = string.IsNullOrEmpty(prefix) ? "ResourceDictionary" : prefix + ":ResourceDictionary";
                    text = text[..(token.Index + token.Length - 2)] + ">" +
                        "</" + tag + ">" + text[(token.Index + token.Length)..];
                    rootEnd = token.Index + token.Length - 1;
                }
                if (!raw.EndsWith("/>", StringComparison.Ordinal)) depth++;
            }
        }
        var additions = new StringBuilder();
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var (key, annotation) in after.Annotations)
            if (!seenAnnotations.Contains(key) && !seenStrings.Contains(key)) additions.Append("  ").Append(StringCatalogue.AnnotationComment(annotation)).Append(newline);
        foreach (var item in after.Document.Root!.Elements())
            if (!seenStrings.Contains((string)item.Attribute(StringCatalogue.Xaml + "Key")!))
                additions.Append("  ").Append(item.ToString(SaveOptions.DisableFormatting)).Append(newline);
        if (additions.Length > 0) edits.Add((rootEnd, 0, additions.ToString()));
        var result = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(edit => edit.Start).ThenByDescending(edit => edit.Length))
            result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        var body = encoding.GetBytes(result.ToString());
        var preamble = encoding.GetPreamble();
        var bytes = preamble.Length > 0 && original.AsSpan().StartsWith(preamble)
            ? preamble.Concat(body).ToArray() : body;
        using var validation = new MemoryStream(bytes);
        var saved = StringCatalogue.Read(validation);
        if (saved.Strings.Count != after.Strings.Count || saved.Annotations.Count != after.Annotations.Count ||
            after.Strings.Any(pair => !saved.Strings.TryGetValue(pair.Key, out var value) || value != pair.Value) ||
            after.Annotations.Any(pair => !saved.Annotations.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new FormatException("Catalogue layout save changed its contents.");
        return bytes;

        void UpdateElement(int closingStart, int end, bool selfClosing)
        {
            var key = (string)element!.Attribute(StringCatalogue.Xaml + "Key")!;
            seenStrings.Add(key);
            if (!before.Annotations.ContainsKey(key) && after.Annotations.TryGetValue(key, out var annotation))
                edits.Add((start, 0, StringCatalogue.AnnotationComment(annotation) +
                    (text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n")));
            if (!after.Strings.TryGetValue(key, out var value)) edits.Add((start, end - start, ""));
            else if (value != element.Value)
            {
                var escaped = value.Replace("&", "&amp;", StringComparison.Ordinal)
                    .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
                    .Replace("\r", "&#xD;", StringComparison.Ordinal);
                if (selfClosing)
                {
                    var opening = text[start..contentStart];
                    var name = element.GetPrefixOfNamespace(element.Name.Namespace);
                    var tag = string.IsNullOrEmpty(name) ? "String" : name + ":String";
                    edits.Add((start, end - start, opening[..^2] + ">" + escaped + "</" + tag + ">"));
                }
                else edits.Add((contentStart, closingStart - contentStart, escaped));
            }
        }
    }
}
