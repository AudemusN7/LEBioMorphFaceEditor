using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class ShaderMapOnlyReaderTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("shader map only reader matches LEC map semantics and skips embedded bytecode", MapOnlyReadMatchesLec)
    ];

    private static void MapOnlyReadMatchesLec()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            var path = Path.Combine(Path.GetTempPath(), $"MFE-ShaderMapOnly-{game}-{Guid.NewGuid():N}.pcc");
            var parameters = (StaticParameterSet)Guid.NewGuid();
            try
            {
                WriteSource(path, game, "SelectedMap", "SelectedScalar", parameters);
                using (var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
                {
                    var cacheExport = reopened.FindExport("SeekFreeShaderCache", "ShaderCache")!;
                    var leCache = ObjectBinary.From<ShaderCache>(cacheExport);
                    var expected = leCache.MaterialShaderMaps[parameters];
                    var actual = ShaderMapOnlyReader.Read(cacheExport, parameters);

                    TestAssert.True(actual is not null, $"The {game} map-only reader did not find the selected local map.");
                    TestAssert.Equal(expected.ID, actual!.ID);
                    TestAssert.Equal(expected.FriendlyName, actual.FriendlyName);
                    TestAssert.True(expected.StaticParameters == actual.StaticParameters,
                        $"The {game} map-only reader changed the static parameter key.");
                    TestAssert.True(expected.GetNames(game).SequenceEqual(actual.GetNames(game)),
                        $"The {game} map-only reader returned different compiled expression evidence from LEC.");

                    var referenceStyle = ShaderMapOnlyReader.ReadReferenceCache(path, game, parameters);
                    TestAssert.True(referenceStyle is not null,
                        $"The {game} file-stream reference-cache reader did not find the selected map.");
                    TestAssert.Equal(expected.ID, referenceStyle!.ID);
                    TestAssert.True(expected.GetNames(game).SequenceEqual(referenceStyle.GetNames(game)),
                        $"The {game} file-stream reference-cache reader returned different map evidence from LEC.");

                    TestAssert.True(ShaderMapOnlyReader.Read(cacheExport, (StaticParameterSet)Guid.NewGuid()) is null,
                        $"A missing {game} static parameter key unexpectedly matched a local map.");
                }

                // Read the file again after changing its map payload to verify
                // the physical reference-cache route observes source updates.
                WriteSource(path, game, "UpdatedMap", "UpdatedScalar", parameters);
                var updated = ShaderMapOnlyReader.ReadReferenceCache(path, game, parameters);
                TestAssert.True(updated?.FriendlyName == "UpdatedMap",
                    $"The {game} reference-cache stream returned map data from the previous file contents.");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteSource(string path, MEGame game, string mapName, string scalarName,
        StaticParameterSet parameters)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(path, game);
        var cache = ShaderCache.Create();
        var shaderId = Guid.NewGuid();
        cache.Shaders.Add(shaderId, new FNULLPixelShader
        {
            Guid = shaderId,
            ShaderType = "FNULLPixelShader",
            Platform = game.IsLEGame() ? (byte)5 : (byte)0,
            Frequency = Shader.ShaderFrequency.Pixel,
            ShaderByteCode = new byte[2 * 1024 * 1024]
        });

        cache.MaterialShaderMaps.Add(parameters, CreateMap(parameters, mapName, scalarName));
        var otherKey = (StaticParameterSet)Guid.NewGuid();
        cache.MaterialShaderMaps.Add(otherKey, CreateMap(otherKey, "OtherMap", "OtherScalar"));
        package.CreateExport("SeekFreeShaderCache", "ShaderCache", indexed: false).WriteBinary(cache);
        package.Save(path, compress: false);
    }

    private static MaterialShaderMap CreateMap(StaticParameterSet key, string name, string scalar) => new()
    {
        ID = Guid.NewGuid(),
        FriendlyName = name,
        StaticParameters = key,
        Shaders = [],
        MeshShaderMaps = [],
        UniformPixelVectorExpressions = [],
        UniformPixelScalarExpressions =
        [
            new MaterialUniformExpressionScalarParameter
            {
                ExpressionType = "FMaterialUniformExpressionScalarParameter",
                ParameterName = scalar
            }
        ],
        Uniform2DTextureExpressions = [],
        UniformCubeTextureExpressions = [],
        UniformVertexVectorExpressions = [],
        UniformVertexScalarExpressions = []
    };
}
