using System.Globalization;
using System.Numerics;
using System.Text;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;

namespace MorphFaceEditor.LegendaryExplorer;

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

public sealed record HeadMorphRonDocument(HeadMorphPayload Head, RonExportProvenance? Provenance);

/// <summary>
/// Reads and writes Trilogy Save Editor's serde/RON HeadMorph schema. This is
/// intentionally independent of LEC's older line parser, which loses non-empty
/// accessory arrays and is sensitive to the current numeric culture.
/// </summary>
public static class TseHeadMorphRon
{
    public static bool IsMorphFeaturesOnly(string path) => File.ReadLines(path).Any(IsMorphFeaturesMarker);
    public static bool IsMorphFeaturesOnly(RonInputSnapshot input) => input.Source.Split('\n').Any(IsMorphFeaturesMarker);
    private static bool IsMorphFeaturesMarker(string line) => line.Trim().Equals("// MFE RON Content: Morphs", StringComparison.Ordinal);
    public static void WriteMorphFeatures(string path, MorphFaceMorphData data, RonExportProvenance provenance) =>
        Write(path, new("None", [], new(data.MorphFeatures, [], []), new([], [], [])), provenance, true);
    public static MorphFaceMorphData ReadMorphData(string path) => Read(path).MorphData;
    public static void Validate(string path) => _ = Read(path);

    public static HeadMorphPayload Read(string path) => ReadDocument(path).Head;
    public static HeadMorphPayload Read(RonInputSnapshot input) => HeadMorphRonSchema.Read(input, IsMorphFeaturesOnly(input));

    public static HeadMorphRonDocument ReadDocument(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var source = File.ReadAllText(path);
        var provenance = ParseProvenance(source);
        var input = RonInputSnapshot.Parse(source, "Trilogy Save Editor");
        return new(Read(input), provenance);
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

    public static RonExportProvenance? ReadProvenance(RonInputSnapshot input) => ParseProvenance(input.Source);

    internal static void Write(
        string path,
        HeadMorphPayload morph,
        RonExportProvenance? provenance = null,
        bool morphFeaturesOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(morph);
        var builder = new StringBuilder();
        if (morphFeaturesOnly) builder.AppendLine("// MFE RON Content: Morphs");
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
        WriteStringMap(builder, "texture_parameters", morph.MaterialData.Textures
                .Where(value => !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(value.Name)).ToArray(),
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

    private static string Format(float value) => float.IsFinite(value)
        ? value.ToString("R", CultureInfo.InvariantCulture)
        : throw UserFacingMessage.CreateError("loc_RonFiles_NonFiniteExport",
            "RON export cannot represent NaN or Infinity values.");
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

}
