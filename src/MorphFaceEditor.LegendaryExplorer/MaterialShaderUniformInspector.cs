using System.Reflection;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace MorphFaceEditor.LegendaryExplorer;

internal sealed record MaterialShaderParameterSupport(
    IReadOnlySet<string> Scalars,
    IReadOnlySet<string> Vectors,
    IReadOnlySet<string> Textures);

/// <summary>Projects compiled support, sampler indices and defaults without retaining shader bytecode.</summary>
internal static class MaterialShaderUniformInspector
{
    public static MaterialShaderMetadata Inspect(
        ExportEntry master,
        MaterialShaderMap shaderMap)
    {
        var expressions = GetUniformExpressions(master, shaderMap);
        return new MaterialShaderMetadata(
            new MaterialShaderParameterSupport(ParameterNames(expressions.Scalars),
                ParameterNames(expressions.Vectors), ParameterNames(expressions.Textures)),
            expressions.Textures.Select(value => value.TextureIndex).ToArray(),
            (master.Game is MEGame.ME1 or MEGame.ME2
                ? ObjectBinary.From<Material>(master).SM3MaterialResource.UniformCubeTextureExpressions
                : shaderMap.UniformCubeTextureExpressions)?.Select(value => value.TextureIndex).ToArray() ?? [],
            shaderMap.UniformPixelScalarExpressions.OfType<MaterialUniformExpressionScalarParameter>()
                .Where(value => !string.IsNullOrWhiteSpace(value.ParameterName.Instanced))
                .GroupBy(value => value.ParameterName.Instanced, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().DefaultValue, StringComparer.OrdinalIgnoreCase),
            shaderMap.UniformPixelVectorExpressions.OfType<MaterialUniformExpressionVectorParameter>()
                .Where(value => !string.IsNullOrWhiteSpace(value.ParameterName.Instanced))
                .GroupBy(value => value.ParameterName.Instanced, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group =>
                {
                    var value = group.First().DefaultValue;
                    return new Vector4(value.R, value.G, value.B, value.A);
                }, StringComparer.OrdinalIgnoreCase));
    }

    private static (
        IReadOnlyList<MaterialUniformExpression> Vectors,
        IReadOnlyList<MaterialUniformExpression> Scalars,
        IReadOnlyList<MaterialUniformExpressionTexture> Textures) GetUniformExpressions(
        ExportEntry master,
        MaterialShaderMap shaderMap)
    {
        if (master.Game is MEGame.ME1 or MEGame.ME2)
        {
            var resource = ObjectBinary.From<Material>(master).SM3MaterialResource;
            return (
                resource.UniformPixelVectorExpressions,
                resource.UniformPixelScalarExpressions,
                resource.Uniform2DTextureExpressions);
        }

        return (
            shaderMap.UniformPixelVectorExpressions,
            shaderMap.UniformPixelScalarExpressions,
            shaderMap.Uniform2DTextureExpressions);
    }

    private static IReadOnlySet<string> ParameterNames(IReadOnlyList<MaterialUniformExpression> expressions) =>
        expressions.SelectMany(FindParameterNames).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> FindParameterNames(MaterialUniformExpression expression)
    {
        // Parameters can be wrapped in arithmetic expressions, so inspect the compiled expression tree recursively.
        switch (expression)
        {
            case MaterialUniformExpressionScalarParameter scalar:
                yield return scalar.ParameterName.Instanced;
                break;
            case MaterialUniformExpressionVectorParameter vector:
                yield return vector.ParameterName.Instanced;
                break;
            case MaterialUniformExpressionTextureParameter texture:
                yield return texture.ParameterName.Instanced;
                break;
        }

        foreach (var field in expression.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (field.GetValue(expression) is not MaterialUniformExpression child)
            {
                continue;
            }

            foreach (var name in FindParameterNames(child))
            {
                yield return name;
            }
        }
    }
}
