using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace MorphFaceEditor.Core.Services;

/// <summary>One captured RON input and its read-only syntax tree, before format-specific validation.</summary>
public sealed class RonInputSnapshot
{
    private readonly int _omittedCommaPosition;
    private RonInputSnapshot(string source, RonMap root, int omittedCommaPosition)
        => (Source, Root, _omittedCommaPosition) = (source, root, omittedCommaPosition);

    public string Source { get; }
    public RonMap Root { get; }

    public static RonInputSnapshot Parse(string source, string formatName = "material")
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new Reader(source, formatName);
        var root = parser.Read();
        return new(source, root, parser.OmittedCommaPosition);
    }

    internal IReadOnlyDictionary<string, object> MaterialRoot()
    {
        // Head files historically allow omitted entry commas; material files do not.
        if (_omittedCommaPosition >= 0)
            throw new InvalidDataException($"Invalid material RON near character {_omittedCommaPosition}: Expected ','.");
        return Root;
    }

    private sealed class Reader(string source, string formatName)
    {
        private int _index;
        public int OmittedCommaPosition { get; private set; } = -1;

        public RonMap Read()
        {
            var root = Value(0) as RonMap ?? throw Error("Expected a RON struct.");
            Trivia();
            if (_index != source.Length) throw Error("Unexpected trailing content.");
            return root;
        }

        private object Value(int depth)
        {
            if (depth > 32) throw Error("Too many nested values.");
            Trivia();
            if (_index == source.Length) throw Error("Unexpected end of file.");
            if (source[_index] == '"') return String();
            var open = source[_index];
            if (open is '(' or '{' or '[')
            {
                _index++;
                var close = open == '(' ? ')' : open == '{' ? '}' : ']';
                Trivia();
                var map = open == '{' || open == '(' && _index < source.Length &&
                    (char.IsAsciiLetter(source[_index]) || source[_index] is '_' or ')');
                var fields = new OrderedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var items = new List<object>();
                var omitted = false;
                while (!Consume(close))
                {
                    if (map)
                    {
                        var key = open == '{' ? String() : Identifier();
                        Expect(':');
                        if (!fields.TryAdd(key, Value(depth + 1))) throw Error($"Duplicate field '{key}'.");
                    }
                    else items.Add(Value(depth + 1));
                    if (Consume(close)) break;
                    if (!Consume(','))
                    {
                        omitted = true;
                        if (OmittedCommaPosition < 0) OmittedCommaPosition = _index;
                    }
                }
                return map ? new RonMap(open, fields) : new RonSequence(open, items, omitted);
            }
            var start = _index;
            while (_index < source.Length && (char.IsAsciiDigit(source[_index]) || source[_index] is '+' or '-' or '.' or 'e' or 'E')) _index++;
            if (start == _index || !float.TryParse(source[start.._index], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var number) || !float.IsFinite(number))
                throw Error("Expected a finite number.");
            return number;
        }

        private string Identifier()
        {
            Trivia();
            var start = _index;
            while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_')) _index++;
            if (start == _index) throw Error("Expected a field name.");
            return source[start.._index];
        }

        private string String()
        {
            Expect('"');
            var text = new StringBuilder();
            while (_index < source.Length)
            {
                var character = source[_index++];
                if (character == '"') return text.ToString();
                if (character == '\\')
                {
                    if (_index == source.Length) break;
                    character = source[_index++] switch
                    {
                        'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\',
                        _ => throw Error("Unsupported string escape.")
                    };
                }
                text.Append(character);
            }
            throw Error("Unterminated string.");
        }

        private bool Consume(char character)
        {
            Trivia();
            if (_index >= source.Length || source[_index] != character) return false;
            _index++;
            return true;
        }

        private void Expect(char character)
        {
            if (!Consume(character)) throw Error($"Expected '{character}'.");
        }

        private void Trivia()
        {
            while (_index < source.Length)
            {
                if (char.IsWhiteSpace(source[_index]) || source[_index] == '\uFEFF') { _index++; continue; }
                if (source.AsSpan(_index).StartsWith("//"))
                {
                    while (_index < source.Length && source[_index] != '\n') _index++;
                    continue;
                }
                if (source.AsSpan(_index).StartsWith("/*"))
                {
                    var end = source.IndexOf("*/", _index + 2, StringComparison.Ordinal);
                    if (end < 0) throw Error("Unterminated comment.");
                    _index = end + 2;
                    continue;
                }
                break;
            }
        }

        private InvalidDataException Error(string message) =>
            new($"Invalid {formatName} RON near character {_index}: {message}");
    }
}

/// <summary>Map/struct delimiters are retained for schemas which distinguish them.</summary>
public sealed class RonMap : ReadOnlyDictionary<string, object>
{
    internal RonMap(char opening, IDictionary<string, object> fields) : base(fields) => Opening = opening;
    public char Opening { get; }
}

/// <summary>Array/tuple delimiters and separator evidence are retained for schema validation.</summary>
public sealed class RonSequence : ReadOnlyCollection<object>
{
    internal RonSequence(char opening, IList<object> items, bool omittedCommas) : base(items)
        => (Opening, HasOmittedCommas) = (opening, omittedCommas);
    public char Opening { get; }
    public bool HasOmittedCommas { get; }
}
