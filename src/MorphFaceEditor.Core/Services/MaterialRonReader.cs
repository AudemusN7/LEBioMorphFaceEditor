using System.Globalization;
using System.Text;

namespace MorphFaceEditor.Core.Services;

/// <summary>Reads the RON maps, structs, arrays and numeric tuples used by material interchange.</summary>
internal sealed class MaterialRonReader(string source)
{
    private int _index;

    public IReadOnlyDictionary<string, object> Read()
    {
        var value = Value(0) as IReadOnlyDictionary<string, object>
            ?? throw Error("Expected a RON struct.");
        Trivia();
        if (_index != source.Length) throw Error("Unexpected trailing content.");
        return value;
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
            var map = open == '{' || open == '(' && (_index < source.Length &&
                (char.IsLetter(source[_index]) || source[_index] == '_' || source[_index] == ')'));
            var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var items = new List<object>();
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
                Expect(',');
            }
            return map ? fields : items;
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
    private InvalidDataException Error(string message) => new($"Invalid material RON near character {_index}: {message}");
}
