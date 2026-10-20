using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.DataCompiler;

/// <summary>Regenerates the reviewed stock identity set from installed official packages.</summary>
internal static class StockMaterialMetadataCompiler
{
    internal static void Run(string manifestPath, string outputPath)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var entries = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsArray();
        using var cache = new PackageCache { CacheMaxSize = 6 };
        var installed = new InstalledGameAssetResolver();
        var resolver = new GamePackageReferenceResolver(cache, installed);
        foreach (var entry in entries)
        {
            var row = entry!.AsObject();
            var game = Enum.Parse<MEGame>(row["game"]!.GetValue<string>());
            var source = row["source"]!;
            var file = installed.GetPackageFiles(game)[source["package"]!.GetValue<string>()];
            var package = PackageSourceCache.GetPackage(cache, file)!;
            var master = package.FindExport(source["path"]!.GetValue<string>(), "Material")
                         ?? throw new InvalidDataException($"Reviewed master missing from '{file}': {source["path"]}.");
            var id = ObjectBinary.From<Material>(master).SM3MaterialResource.ID;
            if (id != Guid.Parse(row["id"]!.GetValue<string>()) || master.ObjectName.Instanced != row["master"]!.GetValue<string>())
                throw new InvalidDataException($"Reviewed master identity changed: {master.InstancedFullPath}.");
            var parameters = (StaticParameterSet)id;
            var map = package.FindExport("SeekFreeShaderCache", "ShaderCache") is { } local
                ? ShaderMapOnlyReader.Read(local, parameters) : null;
            map ??= ShaderMapOnlyReader.ReadReferenceCache(RefShaderCacheReader.ShaderFilePath(game), game, parameters)
                    ?? throw new InvalidDataException($"Compiled evidence missing: {master.InstancedFullPath}.");
            var shader = MaterialShaderUniformInspector.Inspect(master, map);
            row["supported"] = JsonSerializer.SerializeToNode(new
            {
                scalars = shader.Support.Scalars.Order(StringComparer.Ordinal),
                vectors = shader.Support.Vectors.Order(StringComparer.Ordinal),
                textures = shader.Support.Textures.Order(StringComparer.Ordinal)
            });
            row["2Dindices"] = JsonSerializer.SerializeToNode(shader.TextureIndices);
            row["cubeIndices"] = JsonSerializer.SerializeToNode(shader.CubeIndices);
            if (row.ContainsKey("compiledDefaults"))
                row["compiledDefaults"] = JsonSerializer.SerializeToNode(new
                {
                    scalars = shader.ScalarDefaults,
                    vectors = shader.VectorDefaults.ToDictionary(pair => pair.Key, pair => Components(pair.Value))
                });

            var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var vectors = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var textures = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in (IEnumerable<ObjectProperty>?)master.GetProperty<ArrayProperty<ObjectProperty>>("Expressions") ?? [])
            {
                if (reference.Value == 0) continue;
                var expression = resolver.Require(reference.ResolveToEntry(package), "master expression");
                var properties = expression.GetProperties();
                var name = properties.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (expression.IsA("MaterialExpressionScalarParameter"))
                    scalars[name] = properties.GetProp<FloatProperty>("DefaultValue")?.Value ?? 0;
                else if (expression.IsA("MaterialExpressionVectorParameter"))
                {
                    var value = properties.GetProp<StructProperty>("DefaultValue");
                    vectors[name] = new[] { "R", "G", "B", "A" }.Select(channel => value?.GetProp<FloatProperty>(channel)?.Value ?? 0).ToArray();
                }
                else if (expression.IsA("MaterialExpressionTextureSampleParameter") &&
                         properties.GetProp<ObjectProperty>("Texture")?.ResolveToEntry(expression.FileRef) is { } texture)
                    textures[name] = new
                    {
                        path = (texture is ExportEntry export ? PccAssetPathPolicy.FromExport(export) : texture.InstancedFullPath).ToLowerInvariant(),
                        className = texture.ClassName
                    };
            }
            row["masterDefaults"] = JsonSerializer.SerializeToNode(new
            {
                scalars, vectors, textures,
                blendMode = master.GetProperty<EnumProperty>("BlendMode")?.Value.Instanced,
                twoSided = master.GetProperty<BoolProperty>("bTwoSided")?.Value ?? master.GetProperty<BoolProperty>("TwoSided")?.Value
            });
            Console.WriteLine($"Read {game} {master.ObjectName.Instanced} ({id}).");
        }
        // Publish only after every reviewed identity has been regenerated successfully.
        File.WriteAllText(outputPath, entries.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static float[] Components(Vector4 value) => [value.X, value.Y, value.Z, value.W];
}
