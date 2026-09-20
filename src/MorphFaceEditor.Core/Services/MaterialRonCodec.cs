using System.Globalization;
using System.Numerics;
using System.Text;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Services;

/// <summary>
/// Material-only RON interchange. Full player geometry validation belongs to the
/// separate player importer; neither of these readers applies geometry or attachments.
/// </summary>
public static class MaterialRonCodec
{
    public sealed record TseMaterialDocument(
        MorphFaceMaterialData Parameters,
        string? HairMesh,
        IReadOnlyList<string>? AccessoryMeshes);

    public static MorphFaceMaterialData ReadTse(string text)
    {
        var root = new MaterialRonReader(text).Read();
        if (root.ContainsKey("format")) throw new InvalidDataException("Use Import Materials for an MFE material file.");
        if (!new[] { "scalar_parameters", "vector_parameters", "texture_parameters" }.Any(root.ContainsKey))
            throw new InvalidDataException("The TSE RON contains no material parameter maps.");
        return ReadParameters(root);
    }

    public static TseMaterialDocument ReadTseDocument(string text)
    {
        var root = new MaterialRonReader(text).Read();
        if (root.ContainsKey("format")) throw new InvalidDataException("Use Import Materials for an MFE material file.");
        if (!new[] { "scalar_parameters", "vector_parameters", "texture_parameters" }.Any(root.ContainsKey))
            throw new InvalidDataException("The TSE RON contains no material parameter maps.");
        string? hair = null;
        if (root.TryGetValue("hair_mesh", out var hairValue))
            hair = hairValue as string ?? throw new InvalidDataException("TSE hair_mesh must be a string.");
        IReadOnlyList<string>? accessories = null;
        if (root.TryGetValue("accessory_mesh", out var accessoryValue))
            accessories = Array(accessoryValue).Select(value => value as string ??
                throw new InvalidDataException("Every TSE accessory_mesh value must be a string.")).ToArray();
        return new TseMaterialDocument(ReadParameters(root), hair, accessories);
    }

    public static MeshMaterialDocument ReadMfe(string text)
    {
        var root = new MaterialRonReader(text).Read();
        if (String(root, "format") != "MorphFaceEditor.Materials")
            throw new InvalidDataException("This is not an MFE material file. Use Import TSE RON for a player RON.");
        if (Number(root["version"]) != MeshMaterialDocument.CurrentVersion)
            throw new InvalidDataException("This MFE material file uses an unsupported version.");
        var game = String(root, "game");
        if (game is not ("LE1" or "LE2" or "LE3")) throw new InvalidDataException("Invalid material file game.");
        var slots = Array(root["slots"]).Select(item =>
        {
            var slot = Object(item);
            var index = Number(slot["index"]);
            if (index < 0 || index >= 2147483648f || index != MathF.Truncate(index))
                throw new InvalidDataException("A material slot index must be a non-negative integer.");
            if (!Enum.TryParse<HeadMaterialFamily>(String(slot, "family"), out var family) || !Enum.IsDefined(family))
                throw new InvalidDataException("Unknown material family in MFE file.");
            return new MeshMaterialSlotData((int)index, String(slot, "slot_name"), String(slot, "material_id"),
                String(slot, "material_name"), String(slot, "scope"), family);
        }).ToArray();
        if (slots.Length > CustomMaterialWorkspace.MaximumSupportedSlots ||
            slots.Select(value => value.Index).Distinct().Count() != slots.Length)
            throw new InvalidDataException("The material file contains too many or duplicate slots.");
        var scopes = Object(root["parameters"]);
        foreach (var scope in scopes.Keys)
            if (string.IsNullOrWhiteSpace(scope) || scope.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new InvalidDataException($"Invalid material scope '{scope}'.");
        return new MeshMaterialDocument(game, String(root, "mesh_name"), slots,
            scopes.ToDictionary(value => value.Key, value => ReadParameters(Object(value.Value)), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Reads only the material maps; face workspaces do not use MESH slot or source-game metadata.</summary>
    public static IReadOnlyDictionary<string, MorphFaceMaterialData> ReadMfeParameterScopes(string text)
    {
        var root = new MaterialRonReader(text).Read();
        if (String(root, "format") != "MorphFaceEditor.Materials")
            throw new InvalidDataException("This is not an MFE material file.");
        var scopes = Object(root["parameters"]);
        return scopes.ToDictionary(value => value.Key,
            value => ReadParameters(Object(value.Value)), StringComparer.OrdinalIgnoreCase);
    }

    public static string WriteMfe(MeshMaterialDocument document)
    {
        var output = new StringBuilder("// Material settings only; no authored geometry or bone offsets.\n");
        output.AppendLine($"// MFE material export version: {MeshMaterialDocument.CurrentVersion}");
        output.AppendLine($"// Material target game: {document.Game}");
        output.AppendLine("(");
        Field(output, "format", "MorphFaceEditor.Materials", 1);
        output.AppendLine($"    version: {MeshMaterialDocument.CurrentVersion},");
        Field(output, "game", document.Game, 1);
        Field(output, "mesh_name", document.MeshName, 1);
        output.AppendLine("    slots: [");
        foreach (var slot in document.Slots.OrderBy(value => value.Index))
        {
            output.AppendLine("        (");
            output.AppendLine($"            index: {slot.Index},");
            Field(output, "slot_name", slot.SlotName, 3);
            Field(output, "material_id", slot.MaterialId, 3);
            Field(output, "material_name", slot.MaterialName, 3);
            Field(output, "scope", slot.Scope, 3);
            Field(output, "family", slot.Family.ToString(), 3);
            output.AppendLine("        ),");
        }
        output.AppendLine("    ],\n    parameters: {");
        foreach (var scope in document.Parameters.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
        {
            output.Append("        ").Append(Quote(scope.Key)).AppendLine(": (");
            WriteParameters(output, scope.Value, 3);
            output.AppendLine("        ),");
        }
        output.AppendLine("    },\n)");
        var text = output.ToString();
        _ = ReadMfe(text);
        return text;
    }

    public static string WriteTse(MorphFaceMaterialData data, string hair, IReadOnlyList<string> accessories)
    {
        var output = new StringBuilder("// Material settings only; no authored geometry or bone offsets.\n(\n");
        Field(output, "hair_mesh", hair, 1);
        output.Append("    accessory_mesh: [").AppendJoin(", ", accessories.Select(Quote)).AppendLine("],");
        // TSE deserializes a complete HeadMorph struct; omitted fields are not optional.
        output.AppendLine("    morph_features: {},\n    offset_bones: {},\n    lod0_vertices: [],\n    lod1_vertices: [],\n    lod2_vertices: [],\n    lod3_vertices: [],");
        WriteParameters(output, data, 1);
        output.AppendLine(")");
        var text = output.ToString();
        _ = ReadTse(text);
        return text;
    }

    private static MorphFaceMaterialData ReadParameters(IReadOnlyDictionary<string, object> value)
    {
        IReadOnlyDictionary<string, object> Map(string name)
        {
            var parameters = value.TryGetValue(name, out var map) ? Object(map) : new Dictionary<string, object>();
            foreach (var parameter in parameters.Keys) ValidateParameterName(parameter);
            return parameters;
        }
        return new(
            Map("scalar_parameters").Select(item => new ScalarMaterialOverride(item.Key, Number(item.Value))).ToArray(),
            Map("vector_parameters").Select(item =>
            {
                var vector = Array(item.Value).Select(Number).ToArray();
                if (vector.Length != 4) throw new InvalidDataException("A material colour requires four components.");
                return new VectorMaterialOverride(item.Key, new Vector4(vector[0], vector[1], vector[2], vector[3]));
            }).ToArray(),
            Map("texture_parameters").Select(item =>
            {
                var path = item.Value as string ?? throw new InvalidDataException("A texture reference must be a string.");
                return new TextureMaterialOverride(item.Key, string.IsNullOrWhiteSpace(path) ||
                    path.Equals("None", StringComparison.OrdinalIgnoreCase)
                    ? null : new AssetIdentity(string.Empty, path, 0, "Texture2D"));
            }).ToArray());
    }

    private static void WriteParameters(StringBuilder output, MorphFaceMaterialData data, int indent)
    {
        Map("scalar_parameters", data.Scalars.Select(value => (value.Name, Format(value.Value))));
        Map("vector_parameters", data.Vectors.Select(value => (value.Name,
            $"({Format(value.Value.X)}, {Format(value.Value.Y)}, {Format(value.Value.Z)}, {Format(value.Value.W)})")));
        Map("texture_parameters", data.Textures.Select(value => (value.Name, Quote(value.TextureReference?.InstancedPath ?? "None"))));
        void Map(string name, IEnumerable<(string Name, string Value)> items)
        {
            var pad = new string(' ', indent * 4);
            output.Append(pad).Append(name).AppendLine(": {");
            foreach (var item in items.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                ValidateParameterName(item.Name);
                output.Append(pad).Append("    ").Append(Quote(item.Name)).Append(": ").Append(item.Value).AppendLine(",");
            }
            output.Append(pad).AppendLine("},");
        }
    }

    private static void ValidateParameterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@'))
            throw new InvalidDataException("Material files require raw shader parameter names inside their scope.");
    }

    private static string Format(float value) => float.IsFinite(value)
        ? value.ToString("R", CultureInfo.InvariantCulture)
        : throw new InvalidDataException("Material values must be finite.");
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    private static void Field(StringBuilder output, string name, string value, int indent) =>
        output.Append(' ', indent * 4).Append(name).Append(": ").Append(Quote(value)).AppendLine(",");
    private static IReadOnlyDictionary<string, object> Object(object value) => value as IReadOnlyDictionary<string, object>
        ?? throw new InvalidDataException("Expected a RON field map.");
    private static IReadOnlyList<object> Array(object value) => value as IReadOnlyList<object>
        ?? throw new InvalidDataException("Expected a RON array or tuple.");
    private static float Number(object value) => value is float number ? number
        : throw new InvalidDataException("Expected a finite number.");
    private static string String(IReadOnlyDictionary<string, object> value, string key) =>
        value.TryGetValue(key, out var text) && text is string result ? result
        : throw new InvalidDataException($"Missing RON string field '{key}'.");
}
