namespace MorphFaceEditor.Models;

public static class BioMorphFaceSearch
{
    private static readonly string[] KnownTags = ["HMM", "HMF", "ASA", "SAL", "TUR", "BAT", "KRO"];

    public static bool Matches(BioMorphFaceListItem face, string? query)
    {
        var searchText = query?.Trim();
        if (string.IsNullOrEmpty(searchText))
        {
            return true;
        }

        var tag = face.ProfileTag.Trim('[', ']', ' ');
        if (searchText[0] == '[')
        {
            var close = searchText.IndexOf(']');
            var separator = searchText.IndexOf(' ');
            var end = close >= 0 ? close : separator >= 0 ? separator : searchText.Length;
            var requestedTag = searchText[1..end].Trim();
            var remainderStart = close >= 0 ? close + 1 : end;
            var remainder = searchText[remainderStart..].Trim();
            return requestedTag.Length > 0 &&
                   tag.StartsWith(requestedTag, StringComparison.OrdinalIgnoreCase) &&
                   MatchesText(face, remainder);
        }

        var firstSeparator = searchText.IndexOf(' ');
        var firstToken = firstSeparator < 0 ? searchText : searchText[..firstSeparator];
        if (KnownTags.Contains(firstToken, StringComparer.OrdinalIgnoreCase))
        {
            var remainder = firstSeparator < 0 ? string.Empty : searchText[(firstSeparator + 1)..].Trim();
            return string.Equals(tag, firstToken, StringComparison.OrdinalIgnoreCase) &&
                   MatchesText(face, remainder);
        }

        return MatchesText(face, searchText);
    }

    private static bool MatchesText(BioMorphFaceListItem face, string text) =>
        string.IsNullOrEmpty(text) ||
        face.ObjectName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        face.InstancedPath.Contains(text, StringComparison.OrdinalIgnoreCase);
}
