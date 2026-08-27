using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed record MorphFaceMeshReferences(
    string PackagePath,
    MorphFaceGame Game,
    int FaceUIndex,
    string FacePath,
    string? BaseHeadPath,
    string? HairMeshPath,
    IReadOnlyList<string> OtherMeshPaths);

public static class MorphFaceReferenceInspector
{
    public static IReadOnlyList<MorphFaceMeshReferences> Inspect(string packagePath)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Package file was not found.", fullPath);
        }

        using var package = MEPackageHandler.OpenMEPackage(fullPath, forceLoadFromDisk: true);
        return package.Exports
            .Where(export => !export.IsDefaultObject &&
                             string.Equals(export.ClassName, "BioMorphFace", StringComparison.OrdinalIgnoreCase))
            .Select(export =>
            {
                var properties = export.GetProperties();
                return new MorphFaceMeshReferences(
                    fullPath,
                    package.Game switch
                    {
                        MEGame.LE1 => MorphFaceGame.LE1,
                        MEGame.LE2 => MorphFaceGame.LE2,
                        MEGame.LE3 => MorphFaceGame.LE3,
                        _ => MorphFaceGame.Unsupported
                    },
                    export.UIndex,
                    export.InstancedFullPath,
                    properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package)?.InstancedFullPath,
                    properties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package)?.InstancedFullPath,
                    properties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
                        .Select(item => item.ResolveToEntry(package)?.InstancedFullPath)
                        .Where(path => path is not null)
                        .Cast<string>()
                        .ToArray() ?? []);
            })
            .ToArray();
    }
}
