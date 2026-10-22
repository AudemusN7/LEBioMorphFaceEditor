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
    public static bool IsMorphFeaturesOnly(string path) =>
        IsMorphFeaturesOnly(RonInputSnapshot.Parse(File.ReadAllText(path)));
    public static bool IsMorphFeaturesOnly(RonInputSnapshot input) =>
        MfeRonMetadata.TryRead(input)?.Format == MfeRonMetadata.Morphs;
    public static void WriteMorphFeatures(string path, MorphFaceMorphData data, RonExportProvenance provenance) =>
        Write(path, new("None", [], new(data.MorphFeatures, [], []), new([], [], [])), provenance, true);
    public static MorphFaceMorphData ReadMorphData(string path) => Read(path).MorphData;
    public static void Validate(string path) => _ = Read(path);
    public static HeadMorphPayload Read(string path) => ReadDocument(path).Head;
    public static HeadMorphPayload Read(RonInputSnapshot input) => ReadDocument(input).Head;
    public static HeadMorphRonDocument ReadDocument(string path) =>
        ReadDocument(RonInputSnapshot.Parse(File.ReadAllText(path), "Trilogy Save Editor"));
    public static HeadMorphRonDocument ReadDocument(RonInputSnapshot input)
    {
        var provenance = ReadProvenance(input);
        return new(HeadMorphRonSchema.Read(input, IsMorphFeaturesOnly(input)), provenance);
    }
    public static RonExportProvenance? ReadProvenance(string path) =>
        ReadProvenance(RonInputSnapshot.Parse(File.ReadAllText(path)));

    public static RonExportProvenance? ReadProvenance(RonInputSnapshot input)
    {
        if (!input.Root.ContainsKey("format")) return ReadLexProvenance(input.Source);
        var metadata = MfeRonMetadata.TryRead(input);
        if (metadata?.Format is not (MfeRonMetadata.Head or MfeRonMetadata.Morphs) || metadata.Target is not { } target)
            throw MetadataError();
        var profile = MorphFaceProfileRegistry.CreateDefault().Profiles.SingleOrDefault(profile =>
            profile.Key == target.ProfileKey && profile.Game.ToString() == metadata.Game);
        if (profile is null) throw MetadataError();
        return new(RonExportProducer.MFE, profile.Game, profile.ExportTag.Trim('[', ']'), target.PlayerMorph);
    }

    internal static void Write(
        string path,
        HeadMorphPayload morph,
        RonExportProvenance? provenance = null,
        bool morphFeaturesOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(morph);
        var builder = new StringBuilder();
        if (provenance is not null)
        {
            if (provenance.Producer != RonExportProducer.MFE) throw MetadataError();
            var profile = MorphFaceProfileRegistry.CreateDefault().Profiles.SingleOrDefault(profile =>
                profile.Game == provenance.Game && profile.ExportTag.Trim('[', ']').Equals(provenance.Archetype, StringComparison.OrdinalIgnoreCase));
            if (profile is null) throw MetadataError();
            new MfeRonMetadata(morphFeaturesOnly ? MfeRonMetadata.Morphs : MfeRonMetadata.Head,
                provenance.Game.ToString(), new(profile.Key, provenance.PlayerMorph)).Write(builder);
        }
        else builder.AppendLine("(");
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

    private static RonExportProvenance? ReadLexProvenance(string source)
    {
        var comments = source.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("//", StringComparison.Ordinal))
            .Select(line => line[2..].Trim()).ToArray();
        string? Header(string key)
        {
            var matches = comments.Where(line => line.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length != 1) throw MetadataError();
            var remainder = matches[0][key.Length..].TrimStart();
            if (!remainder.StartsWith(':')) throw MetadataError();
            return remainder[1..].Trim();
        }
        var exporter = Header("Exported from");
        if (exporter is null || !exporter.StartsWith("LEX", StringComparison.OrdinalIgnoreCase)) return null;
        var archetype = Header("Archetype")?.ToUpperInvariant();
        var player = Header("Player Morph")?.ToUpperInvariant();
        if (exporter.Length < 4 || exporter[3] is not (' ' or '\t') || string.IsNullOrWhiteSpace(exporter[4..]) ||
            !Enum.TryParse<MorphFaceGame>(Header("Game"), true, out var game) ||
            game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3) ||
            archetype is not ("ALN" or "ASA" or "BAT" or "HMM" or "HMF" or "KRO" or "SAL" or "TUR" or "TUF") ||
            player is not ("YES" or "NO")) throw MetadataError();
        return new(RonExportProducer.LEX, game, archetype, player == "YES");
    }

    private static InvalidDataException MetadataError() => UserFacingMessage.CreateError(
        "loc_RonFiles_InvalidMetadata", "The RON file has invalid or unsupported export metadata.");

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
