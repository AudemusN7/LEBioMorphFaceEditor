namespace MorphFaceEditor.QA;

internal sealed record QaMaterialValue(string Name, string Value);

// Parameter names ignore casing; order does not matter, but duplicates still count.
internal static class QaMaterialValues
{
    internal static QaMaterialValue[] Normalise<T>(IEnumerable<T> rows, Func<T, string> name, Func<T, string> value) =>
        rows.Select(row => new QaMaterialValue(name(row).ToUpperInvariant(), value(row)))
            .OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Value, StringComparer.Ordinal).ToArray();

    internal static IEnumerable<string> Differences<T>(IEnumerable<T> before, IEnumerable<T> after,
        Func<T, string> name, Func<T, string> value, string kind)
    {
        static Dictionary<string, string[]> Group(QaMaterialValue[] rows) => rows.GroupBy(row => row.Name)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Value).ToArray(), StringComparer.Ordinal);
        var expected = Group(Normalise(before, name, value));
        var actual = Group(Normalise(after, name, value));
        foreach (var key in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            var left = expected.GetValueOrDefault(key, []);
            var right = actual.GetValueOrDefault(key, []);
            if (!left.SequenceEqual(right))
                yield return $"{kind} '{key}': expected [{string.Join(", ", left)}]; actual [{string.Join(", ", right)}].";
        }
    }
}
