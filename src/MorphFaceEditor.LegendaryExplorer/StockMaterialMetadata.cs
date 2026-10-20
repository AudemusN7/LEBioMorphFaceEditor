using System.Numerics;
using System.Text.Json;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.LegendaryExplorer;

internal sealed record MaterialShaderMetadata(
    MaterialShaderParameterSupport Support,
    int[] TextureIndices,
    int[] CubeIndices,
    IReadOnlyDictionary<string, float> ScalarDefaults,
    IReadOnlyDictionary<string, Vector4> VectorDefaults);

/// <summary>
/// Reviewed stock compilation evidence, independent of MIC values and physical texture donors.
/// Unknown compilations continue through live inspection. A valid empty support set stays empty.
/// </summary>
internal sealed record StockMaterialMetadata(
    string Master,
    MaterialShaderMetadata Shader,
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, Vector4> Vectors,
    IReadOnlyDictionary<string, TextureDefault> Textures)
{
    private static readonly Lazy<IReadOnlyDictionary<(MEGame, Guid), StockMaterialMetadata>> Entries = new(Load);

    internal static StockMaterialMetadata? Find(ExportEntry master)
    {
        if (master.ClassName != "Material" || master.Game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3)) return null;
        Guid id;
        try { id = ObjectBinary.From<Material>(master).SM3MaterialResource.ID; }
        catch
        {
            // A custom graph may retain expressions without a usable compiled resource.
            // It still needs the existing live-value / missing-support path.
            return null;
        }
        var entry = Entries.Value.GetValueOrDefault((master.Game, id));
        return entry?.Master.Equals(master.ObjectName.Instanced, StringComparison.OrdinalIgnoreCase) == true ? entry : null;
    }

    private static IReadOnlyDictionary<(MEGame, Guid), StockMaterialMetadata> Load()
    {
        using var stream = typeof(StockMaterialMetadata).Assembly.GetManifestResourceStream("MorphFaceEditor.StockMaterialMetadata.json")
                           ?? throw new InvalidDataException("Embedded stock material metadata is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().ToDictionary(
            row => (Enum.Parse<MEGame>(row.GetProperty("game").GetString()!), row.GetProperty("id").GetGuid()),
            row =>
            {
                var supported = row.GetProperty("supported");
                var defaults = row.GetProperty("masterDefaults");
                var compiled = row.TryGetProperty("compiledDefaults", out var value) ? value : default;
                return new StockMaterialMetadata(row.GetProperty("master").GetString()!,
                    new MaterialShaderMetadata(
                        new MaterialShaderParameterSupport(Names(supported, "scalars"), Names(supported, "vectors"), Names(supported, "textures")),
                        row.GetProperty("2Dindices").EnumerateArray().Select(item => item.GetInt32()).ToArray(),
                        row.GetProperty("cubeIndices").EnumerateArray().Select(item => item.GetInt32()).ToArray(),
                        ScalarsFrom(compiled), VectorsFrom(compiled)),
                    ScalarsFrom(defaults), VectorsFrom(defaults),
                    defaults.GetProperty("textures").EnumerateObject().ToDictionary(item => item.Name,
                        item => new TextureDefault(item.Value.GetProperty("path").GetString()!, item.Value.GetProperty("className").GetString()!),
                        StringComparer.OrdinalIgnoreCase));
            });
    }

    private static IReadOnlySet<string> Names(JsonElement row, string name) =>
        row.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, float> ScalarsFrom(JsonElement row) => row.ValueKind == JsonValueKind.Undefined
        ? new Dictionary<string, float>()
        : row.GetProperty("scalars").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetSingle(), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, Vector4> VectorsFrom(JsonElement row) => row.ValueKind == JsonValueKind.Undefined
        ? new Dictionary<string, Vector4>()
        : row.GetProperty("vectors").EnumerateObject().ToDictionary(item => item.Name, item =>
        {
            var components = item.Value.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            return new Vector4(components[0], components[1], components[2], components[3]);
        }, StringComparer.OrdinalIgnoreCase);
}

internal sealed record TextureDefault(string Path, string ClassName);
