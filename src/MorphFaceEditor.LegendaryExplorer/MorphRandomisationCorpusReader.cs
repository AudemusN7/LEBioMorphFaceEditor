using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using System.Numerics;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed record RawMorphRandomisationFace(
    MorphFaceGame Game,
    string PackagePath,
    string FacePath,
    string? BaseHeadPath,
    IReadOnlyList<MorphFeatureValue> Features)
{
    public IReadOnlyDictionary<string, float> MaterialScalars { get; init; } =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, Vector4> MaterialVectors { get; init; } =
        new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> MaterialTextures { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? MaterialEvidenceError { get; init; }
}

/// <summary>Extracts only classification and slider data needed by the offline randomiser audit.</summary>
public static class MorphRandomisationCorpusReader
{
    public static IReadOnlyList<RawMorphRandomisationFace> Read(IEnumerable<string> packagePaths)
    {
        ArgumentNullException.ThrowIfNull(packagePaths);
        LegendaryExplorerCoreRuntime.Initialize();
        var result = new List<RawMorphRandomisationFace>();
        using var materialReader = new MorphFacePackageReader();
        foreach (var inputPath in packagePaths)
        {
            var path = Path.GetFullPath(inputPath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("GlobalMorphs package was not found.", path);
            }
            using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            var game = package.Game switch
            {
                MEGame.LE1 => MorphFaceGame.LE1,
                MEGame.LE2 => MorphFaceGame.LE2,
                MEGame.LE3 => MorphFaceGame.LE3,
                _ => MorphFaceGame.Unsupported
            };
            foreach (var export in package.Exports.Where(value =>
                         !value.IsDefaultObject &&
                         string.Equals(value.ClassName, "BioMorphFace", StringComparison.OrdinalIgnoreCase)))
            {
                var properties = export.GetProperties();
                var features = properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")?
                    .Select(item => new MorphFeatureValue(
                        item.GetProp<NameProperty>("sFeatureName")?.Value.Instanced ?? string.Empty,
                        item.GetProp<FloatProperty>("Offset")?.Value ?? 0f))
                    .ToArray() ?? [];
                var raw = new RawMorphRandomisationFace(
                    game,
                    path,
                    export.InstancedFullPath,
                    properties.GetProp<ObjectProperty>("m_oBaseHead")?
                        .ResolveToEntry(package)?.InstancedFullPath,
                    features);
                try
                {
                    var evidence = materialReader.LoadRandomisationMaterialEvidence(path, export.InstancedFullPath);
                    raw = raw with
                    {
                        MaterialScalars = evidence.Scalars,
                        MaterialVectors = evidence.Vectors,
                        MaterialTextures = evidence.Textures
                    };
                }
                catch (Exception exception)
                {
                    raw = raw with { MaterialEvidenceError = exception.Message };
                }
                result.Add(raw);
            }
        }
        return result;
    }
}
