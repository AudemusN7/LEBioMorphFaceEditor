using System.Globalization;
using System.Numerics;
using System.Text;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

internal sealed record TseHeadMorph(
    string HairMesh,
    IReadOnlyList<string> AccessoryMeshes,
    MorphFaceMorphData MorphData,
    MorphFaceMaterialData MaterialData);

public enum RonExportProducer
{
    MFE,
    LEX
}

/// <summary>
/// Optional provenance stamped by a trusted RON exporter. The metadata is a
/// routing hint for standalone NPC imports; it does not replace the selected
/// native base or its compatibility checks.
/// </summary>
public sealed record RonExportProvenance(
    RonExportProducer Producer,
    string Version,
    MorphFaceGame Game,
    string Archetype,
    bool PlayerMorph);

/// <summary>
/// Reads and writes Trilogy Save Editor's serde/RON HeadMorph schema. This is
/// intentionally independent of LEC's older line parser, which loses non-empty
/// accessory arrays and is sensitive to the current numeric culture.
/// </summary>
public static class TseHeadMorphRon
{
    internal static TseHeadMorph Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var source = File.ReadAllText(path);
        _ = ParseProvenance(source);
        var parser = new Parser(source);
        return parser.ReadHeadMorph();
    }

    /// <summary>Reads the optional trusted exporter header from a RON file.</summary>
    /// <exception cref="InvalidDataException">
    /// A recognised header is malformed, incomplete, duplicated, or uses an
    /// unsupported producer, game, or archetype.
    /// </exception>
    public static RonExportProvenance? ReadProvenance(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseProvenance(File.ReadAllText(path));
    }

    internal static void Write(
        string path,
        TseHeadMorph morph,
        RonExportProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(morph);
        var builder = new StringBuilder();
        if (provenance is not null)
        {
            WriteProvenance(builder, ValidateProvenance(provenance));
        }
        builder.AppendLine("(");
        WriteStringField(builder, "hair_mesh", morph.HairMesh);
        WriteStringArray(builder, "accessory_mesh", morph.AccessoryMeshes);
        WriteScalarMap(builder, "morph_features", morph.MorphData.MorphFeatures,
            value => value.Name, value => value.Offset);
        WriteVectorMap(builder, "offset_bones", morph.MorphData.FinalSkeleton,
            value => value.BoneName, value => value.Translation);
        for (var lod = 0; lod < 4; lod++)
        {
            WriteVectorArray(builder, $"lod{lod}_vertices",
                morph.MorphData.BakedLods.ElementAtOrDefault(lod) ?? []);
        }
        WriteScalarMap(builder, "scalar_parameters", morph.MaterialData.Scalars,
            value => value.Name, value => value.Value);
        WriteColourMap(builder, "vector_parameters", morph.MaterialData.Vectors);
        WriteStringMap(builder, "texture_parameters", morph.MaterialData.Textures,
            value => value.Name,
            value => value.TextureReference?.InstancedPath ?? "None");
        builder.AppendLine(")");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static readonly HashSet<string> SupportedArchetypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALN", "ASA", "BAT", "HMM", "HMF", "KRO", "SAL", "TUR", "TUF"
    };

    private static RonExportProvenance? ParseProvenance(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RonExportProducer? producer = null;
        string? version = null;
        MorphFaceGame? game = null;
        string? archetype = null;
        bool? playerMorph = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recognised = 0;
        var lineNumber = 0;

        foreach (var line in source.Split('\n'))
        {
            lineNumber++;
            var comment = line.TrimStart();
            if (!comment.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var body = comment[2..].Trim();
            if (!TryGetMetadataField(body, lineNumber, out var key, out var value))
            {
                continue;
            }
            recognised++;
            if (!seen.Add(key))
            {
                throw MetadataError(lineNumber, $"duplicate '{key}' field");
            }

            switch (key)
            {
                case "Exported from":
                    (producer, version) = ParseProducer(value, lineNumber);
                    break;
                case "Game":
                    game = ParseGame(value, lineNumber);
                    break;
                case "Archetype":
                    archetype = ParseArchetype(value, lineNumber);
                    break;
                case "Player Morph":
                    playerMorph = ParsePlayerMorph(value, lineNumber);
                    break;
            }
        }

        if (recognised == 0)
        {
            return null;
        }

        var missing = new[]
        {
            producer is null || version is null ? "Exported from" : null,
            game is null ? "Game" : null,
            archetype is null ? "Archetype" : null,
            playerMorph is null ? "Player Morph" : null
        }.Where(value => value is not null).ToArray();
        if (missing.Length > 0)
        {
            throw MetadataError(lineNumber,
                $"incomplete header; missing {string.Join(", ", missing.Select(value => $"'{value}'"))}");
        }

        return ValidateProvenance(new RonExportProvenance(
            producer!.Value,
            version!,
            game!.Value,
            archetype!,
            playerMorph!.Value));
    }

    private static bool TryGetMetadataField(
        string body,
        int lineNumber,
        out string key,
        out string value)
    {
        foreach (var candidate in new[] { "Exported from", "Player Morph", "Archetype", "Game" })
        {
            if (!body.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = body[candidate.Length..].TrimStart();
            if (!remainder.StartsWith(':'))
            {
                throw MetadataError(lineNumber, $"'{candidate}' field must use the form '// {candidate}: value'");
            }
            key = candidate;
            value = remainder[1..].Trim();
            return true;
        }

        key = string.Empty;
        value = string.Empty;
        return false;
    }

    private static (RonExportProducer Producer, string Version) ParseProducer(string value, int lineNumber)
    {
        var separator = value.IndexOfAny([' ', '\t']);
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw MetadataError(lineNumber,
                "'Exported from' must name MFE or LEX followed by a version");
        }
        var producerText = value[..separator];
        var version = value[separator..].Trim();
        if (!Enum.TryParse<RonExportProducer>(producerText, ignoreCase: true, out var producer) ||
            !Enum.IsDefined(producer))
        {
            throw MetadataError(lineNumber, $"unsupported trusted producer '{producerText}'");
        }
        if (version.Length == 0 || version.Contains('\r') || version.Contains('\n'))
        {
            throw MetadataError(lineNumber, "exporter version must be non-empty");
        }
        return (producer, version);
    }

    private static MorphFaceGame ParseGame(string value, int lineNumber)
    {
        if (!Enum.TryParse<MorphFaceGame>(value, ignoreCase: true, out var game) ||
            game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3))
        {
            throw MetadataError(lineNumber, $"unsupported game '{value}'");
        }
        return game;
    }

    private static string ParseArchetype(string value, int lineNumber)
    {
        var archetype = value.ToUpperInvariant();
        if (!SupportedArchetypes.Contains(archetype))
        {
            throw MetadataError(lineNumber, $"unsupported archetype '{value}'");
        }
        return archetype;
    }

    private static bool ParsePlayerMorph(string value, int lineNumber) => value.ToUpperInvariant() switch
    {
        "YES" => true,
        "NO" => false,
        _ => throw MetadataError(lineNumber, $"Player Morph must be YES or NO, not '{value}'")
    };

    private static RonExportProvenance ValidateProvenance(RonExportProvenance provenance)
    {
        if (!Enum.IsDefined(provenance.Producer))
        {
            throw new InvalidDataException($"Unsupported RON export producer '{provenance.Producer}'.");
        }
        if (string.IsNullOrWhiteSpace(provenance.Version) ||
            provenance.Version.Contains('\r') || provenance.Version.Contains('\n'))
        {
            throw new InvalidDataException("RON export metadata requires a non-empty single-line version.");
        }
        if (provenance.Game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3))
        {
            throw new InvalidDataException($"Unsupported RON export game '{provenance.Game}'.");
        }
        var archetype = provenance.Archetype?.Trim().ToUpperInvariant();
        if (archetype is null || !SupportedArchetypes.Contains(archetype))
        {
            throw new InvalidDataException($"Unsupported RON export archetype '{provenance.Archetype}'.");
        }
        return provenance with { Version = provenance.Version.Trim(), Archetype = archetype };
    }

    private static void WriteProvenance(StringBuilder builder, RonExportProvenance provenance)
    {
        builder.Append("// Exported from: ").Append(provenance.Producer).Append(' ')
            .AppendLine(provenance.Version);
        builder.Append("// Game: ").AppendLine(provenance.Game.ToString());
        builder.Append("// Archetype: ").AppendLine(provenance.Archetype);
        builder.Append("// Player Morph: ").AppendLine(provenance.PlayerMorph ? "YES" : "NO");
    }

    private static InvalidDataException MetadataError(int lineNumber, string message) =>
        new(lineNumber > 0
            ? $"Invalid RON export metadata on line {lineNumber}: {message}."
            : $"Invalid RON export metadata: {message}.");

    private static void WriteStringField(StringBuilder builder, string name, string value) =>
        builder.Append("    ").Append(name).Append(": \"").Append(Escape(value)).AppendLine("\",");

    private static void WriteStringArray(StringBuilder builder, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            builder.Append("    ").Append(name).AppendLine(": [],");
            return;
        }
        builder.Append("    ").Append(name).AppendLine(": [");
        foreach (var value in values)
        {
            builder.Append("        \"").Append(Escape(value)).AppendLine("\",");
        }
        builder.AppendLine("    ],");
    }

    private static void WriteScalarMap<T>(
        StringBuilder builder,
        string name,
        IReadOnlyList<T> values,
        Func<T, string> key,
        Func<T, float> number)
    {
        if (values.Count == 0)
        {
            builder.Append("    ").Append(name).AppendLine(": {},");
            return;
        }
        builder.Append("    ").Append(name).AppendLine(": {");
        foreach (var value in values)
        {
            builder.Append("        \"").Append(Escape(key(value))).Append("\": ")
                .Append(Format(number(value))).AppendLine(",");
        }
        builder.AppendLine("    },");
    }

    private static void WriteVectorMap<T>(
        StringBuilder builder,
        string name,
        IReadOnlyList<T> values,
        Func<T, string> key,
        Func<T, Vector3> vector)
    {
        if (values.Count == 0)
        {
            builder.Append("    ").Append(name).AppendLine(": {},");
            return;
        }
        builder.Append("    ").Append(name).AppendLine(": {");
        foreach (var value in values)
        {
            var item = vector(value);
            builder.Append("        \"").Append(Escape(key(value))).AppendLine("\": (");
            builder.Append("            x: ").Append(Format(item.X)).AppendLine(",");
            builder.Append("            y: ").Append(Format(item.Y)).AppendLine(",");
            builder.Append("            z: ").Append(Format(item.Z)).AppendLine(",");
            builder.AppendLine("        ),");
        }
        builder.AppendLine("    },");
    }

    private static void WriteVectorArray(StringBuilder builder, string name, IReadOnlyList<Vector3> values)
    {
        if (values.Count == 0)
        {
            builder.Append("    ").Append(name).AppendLine(": [],");
            return;
        }
        builder.Append("    ").Append(name).AppendLine(": [");
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            builder.AppendLine("        (");
            builder.Append("            x: ").Append(Format(value.X)).AppendLine(",");
            builder.Append("            y: ").Append(Format(value.Y)).AppendLine(",");
            builder.Append("            z: ").Append(Format(value.Z)).AppendLine(",");
            builder.Append("        ), // [").Append(index).AppendLine("]");
        }
        builder.AppendLine("    ],");
    }

    private static void WriteColourMap(
        StringBuilder builder,
        string name,
        IReadOnlyList<Core.Materials.VectorMaterialOverride> values)
    {
        if (values.Count == 0)
        {
            builder.Append("    ").Append(name).AppendLine(": {},");
            return;
        }
        builder.Append("    ").Append(name).AppendLine(": {");
        foreach (var value in values)
        {
            builder.Append("        \"").Append(Escape(value.Name)).Append("\": (")
                .Append(Format(value.Value.X)).Append(", ")
                .Append(Format(value.Value.Y)).Append(", ")
                .Append(Format(value.Value.Z)).Append(", ")
                .Append(Format(value.Value.W)).AppendLine("),");
        }
        builder.AppendLine("    },");
    }

    private static void WriteStringMap<T>(
        StringBuilder builder,
        string name,
        IReadOnlyList<T> values,
        Func<T, string> key,
        Func<T, string> text)
    {
        if (values.Count == 0)
        {
            builder.Append("    ").Append(name).AppendLine(": {},");
            return;
        }
        builder.Append("    ").Append(name).AppendLine(": {");
        foreach (var value in values)
        {
            builder.Append("        \"").Append(Escape(key(value))).Append("\": \"")
                .Append(Escape(text(value))).AppendLine("\",");
        }
        builder.AppendLine("    },");
    }

    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class Parser
    {
        private readonly string _source;
        private int _index;

        public Parser(string source) => _source = source ?? throw new ArgumentNullException(nameof(source));

        public TseHeadMorph ReadHeadMorph()
        {
            string hair = "None";
            IReadOnlyList<string> accessories = [];
            IReadOnlyList<MorphFeatureValue> features = [];
            IReadOnlyList<BoneTranslation> bones = [];
            var lods = new Vector3[4][];
            IReadOnlyList<Core.Materials.ScalarMaterialOverride> scalars = [];
            IReadOnlyList<Core.Materials.VectorMaterialOverride> colours = [];
            IReadOnlyList<(string Name, string Path)> textures = [];
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Expect('(');
            while (!TryConsume(')'))
            {
                var field = ReadIdentifier();
                if (!fields.Add(field))
                {
                    throw Error($"Duplicate HeadMorph field '{field}'.");
                }
                Expect(':');
                switch (field)
                {
                    case "hair_mesh": hair = ReadString(); break;
                    case "accessory_mesh": accessories = ReadStringArray(); break;
                    case "morph_features": features = ReadFloatMap()
                        .Select(value => new MorphFeatureValue(value.Key, value.Value)).ToArray(); break;
                    case "offset_bones": bones = ReadVectorMap()
                        .Select(value => new BoneTranslation(value.Key, value.Value)).ToArray(); break;
                    case "lod0_vertices": lods[0] = ReadVectorArray(); break;
                    case "lod1_vertices": lods[1] = ReadVectorArray(); break;
                    case "lod2_vertices": lods[2] = ReadVectorArray(); break;
                    case "lod3_vertices": lods[3] = ReadVectorArray(); break;
                    case "scalar_parameters": scalars = ReadFloatMap()
                        .Select(value => new Core.Materials.ScalarMaterialOverride(value.Key, value.Value)).ToArray(); break;
                    case "vector_parameters": colours = ReadColourMap()
                        .Select(value => new Core.Materials.VectorMaterialOverride(value.Key, value.Value)).ToArray(); break;
                    case "texture_parameters": textures = ReadStringMap(); break;
                    default: throw Error($"Unsupported HeadMorph field '{field}'.");
                }
                _ = TryConsume(',');
            }
            SkipTrivia();
            if (_index != _source.Length)
            {
                throw Error("Unexpected content after the HeadMorph value.");
            }
            var lastLod = Array.FindLastIndex(lods, value => value is { Length: > 0 });
            var baked = lastLod < 0 ? [] : lods.Take(lastLod + 1).Cast<Vector3[]>().ToArray();
            if (baked.Length == 0 || baked.Any(value => value.Length == 0))
            {
                throw Error("HeadMorph LOD arrays must begin at LOD0 and remain contiguous.");
            }
            return new TseHeadMorph(
                hair,
                accessories,
                new MorphFaceMorphData(features, bones, baked),
                new MorphFaceMaterialData(
                    scalars,
                    colours,
                    textures.Select(value => new Core.Materials.TextureMaterialOverride(
                        value.Name,
                        string.IsNullOrWhiteSpace(value.Path) ||
                        string.Equals(value.Path, "None", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : new AssetIdentity(string.Empty, value.Path, 0, "Texture2D"))).ToArray()));
        }

        private IReadOnlyList<KeyValuePair<string, float>> ReadFloatMap() => ReadMap(ReadNumber);
        private IReadOnlyList<KeyValuePair<string, Vector3>> ReadVectorMap() => ReadMap(ReadVector3);
        private IReadOnlyList<KeyValuePair<string, Vector4>> ReadColourMap() => ReadMap(ReadVector4);
        private IReadOnlyList<(string Name, string Path)> ReadStringMap() =>
            ReadMap(ReadString).Select(value => (value.Key, value.Value)).ToArray();

        private IReadOnlyList<KeyValuePair<string, T>> ReadMap<T>(Func<T> readValue)
        {
            var values = new List<KeyValuePair<string, T>>();
            Expect('{');
            while (!TryConsume('}'))
            {
                var key = ReadString();
                Expect(':');
                if (values.Any(value => string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase)))
                {
                    throw Error($"Duplicate map key '{key}'.");
                }
                values.Add(new KeyValuePair<string, T>(key, readValue()));
                _ = TryConsume(',');
            }
            return values;
        }

        private IReadOnlyList<string> ReadStringArray() => ReadArray(ReadString);
        private Vector3[] ReadVectorArray() => ReadArray(ReadVector3).ToArray();

        private IReadOnlyList<T> ReadArray<T>(Func<T> readValue)
        {
            var values = new List<T>();
            Expect('[');
            while (!TryConsume(']'))
            {
                values.Add(readValue());
                _ = TryConsume(',');
            }
            return values;
        }

        private Vector3 ReadVector3()
        {
            Expect('(');
            SkipTrivia();
            Vector3 result;
            if (PeekIsIdentifier())
            {
                var components = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                while (!TryConsume(')'))
                {
                    var key = ReadIdentifier();
                    Expect(':');
                    if (components.ContainsKey(key))
                    {
                        throw Error($"Duplicate vector component '{key}'.");
                    }
                    components[key] = ReadNumber();
                    _ = TryConsume(',');
                }
                if (components.Keys.Any(key => key is not ("x" or "y" or "z")) ||
                    components.Count != 3)
                {
                    throw Error("A vector must contain exactly x, y and z components.");
                }
                result = new Vector3(
                    components.GetValueOrDefault("x"),
                    components.GetValueOrDefault("y"),
                    components.GetValueOrDefault("z"));
            }
            else
            {
                result = new Vector3(ReadNumber(), ReadCommaNumber(), ReadCommaNumber());
                _ = TryConsume(',');
                Expect(')');
            }
            EnsureFinite(result);
            return result;
        }

        private Vector4 ReadVector4()
        {
            Expect('(');
            var value = new Vector4(ReadNumber(), ReadCommaNumber(), ReadCommaNumber(), ReadCommaNumber());
            _ = TryConsume(',');
            Expect(')');
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
                !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            {
                throw Error("RON colour values must be finite.");
            }
            return value;
        }

        private float ReadCommaNumber()
        {
            Expect(',');
            return ReadNumber();
        }

        private float ReadNumber()
        {
            SkipTrivia();
            var start = _index;
            while (_index < _source.Length &&
                   (char.IsDigit(_source[_index]) || _source[_index] is '+' or '-' or '.' or 'e' or 'E'))
            {
                _index++;
            }
            if (start == _index || !float.TryParse(
                    _source[start.._index],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value) || !float.IsFinite(value))
            {
                throw Error("Expected a finite floating-point number.");
            }
            return value;
        }

        private string ReadIdentifier()
        {
            SkipTrivia();
            var start = _index;
            while (_index < _source.Length &&
                   (char.IsLetterOrDigit(_source[_index]) || _source[_index] == '_'))
            {
                _index++;
            }
            return start == _index ? throw Error("Expected an identifier.") : _source[start.._index];
        }

        private string ReadString()
        {
            SkipTrivia();
            ExpectRaw('\"');
            var builder = new StringBuilder();
            while (_index < _source.Length)
            {
                var character = _source[_index++];
                if (character == '\"')
                {
                    return builder.ToString();
                }
                if (character == '\\')
                {
                    if (_index >= _source.Length)
                    {
                        break;
                    }
                    var escaped = _source[_index++];
                    builder.Append(escaped switch
                    {
                        '\\' => '\\',
                        '\"' => '\"',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => throw Error($"Unsupported string escape '\\{escaped}'.")
                    });
                }
                else
                {
                    builder.Append(character);
                }
            }
            throw Error("Unterminated string literal.");
        }

        private bool PeekIsIdentifier()
        {
            SkipTrivia();
            return _index < _source.Length && (char.IsLetter(_source[_index]) || _source[_index] == '_');
        }

        private void Expect(char character)
        {
            SkipTrivia();
            ExpectRaw(character);
        }

        private void ExpectRaw(char character)
        {
            if (_index >= _source.Length || _source[_index] != character)
            {
                throw Error($"Expected '{character}'.");
            }
            _index++;
        }

        private bool TryConsume(char character)
        {
            SkipTrivia();
            if (_index >= _source.Length || _source[_index] != character)
            {
                return false;
            }
            _index++;
            return true;
        }

        private void SkipTrivia()
        {
            while (_index < _source.Length)
            {
                if (char.IsWhiteSpace(_source[_index]))
                {
                    _index++;
                    continue;
                }
                if (_index + 1 < _source.Length && _source[_index] == '/' && _source[_index + 1] == '/')
                {
                    _index += 2;
                    while (_index < _source.Length && _source[_index] != '\n')
                    {
                        _index++;
                    }
                    continue;
                }
                break;
            }
        }

        private InvalidDataException Error(string message) =>
            new($"Invalid Trilogy Save Editor RON near character {_index}: {message}");

        private void EnsureFinite(Vector3 value)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            {
                throw Error("RON vector values must be finite.");
            }
        }
    }
}
