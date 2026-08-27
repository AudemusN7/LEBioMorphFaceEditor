using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

public static class MorphTargetPackageReader
{
    public static IReadOnlyList<(string SetName, string ExportPath)> FindSets(
        string packagePath,
        IReadOnlySet<string> setNames)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(setNames);
        using var package = MEPackageHandler.OpenMEPackage(Path.GetFullPath(packagePath), forceLoadFromDisk: true);
        return package.Exports
            .Where(export => !export.IsDefaultObject &&
                             string.Equals(export.ClassName, "MorphTargetSet", StringComparison.OrdinalIgnoreCase) &&
                             setNames.Contains(export.ObjectName.Instanced))
            .Select(export => (export.ObjectName.Instanced, export.InstancedFullPath))
            .ToArray();
    }

    public static MorphTargetAsset Load(string packagePath, string exportSelector)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Morph-target package was not found.", fullPath);
        }

        using var package = MEPackageHandler.OpenMEPackage(fullPath, forceLoadFromDisk: true);
        var export = ExportSelector.Find(package, exportSelector, "MorphTarget");
        return Read(export);
    }

    public static IReadOnlyList<MorphTargetAsset> LoadAll(string packagePath)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Morph-target package was not found.", fullPath);
        }
        using var package = MEPackageHandler.OpenMEPackage(fullPath, forceLoadFromDisk: true);
        return package.Exports
            .Where(export => !export.IsDefaultObject &&
                             string.Equals(export.ClassName, "MorphTarget", StringComparison.OrdinalIgnoreCase))
            .Select(Read)
            .ToArray();
    }

    public static IReadOnlyList<MorphTargetAsset> LoadSet(string packagePath, string setSelector)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(setSelector);
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Morph-target package was not found.", fullPath);
        }

        using var package = MEPackageHandler.OpenMEPackage(fullPath, forceLoadFromDisk: true);
        var set = ExportSelector.Find(package, setSelector, "MorphTargetSet");
        var references = set.GetProperty<ArrayProperty<ObjectProperty>>("Targets")
            ?? throw new InvalidDataException($"MorphTargetSet '{set.InstancedFullPath}' has no Targets array.");
        using var cache = new PackageCache();
        return references
            .Select(reference => reference.ResolveToExport(package, cache))
            .Where(export => export is { ClassName: "MorphTarget" })
            .Select(export => Read(export!))
            .ToArray();
    }

    private static MorphTargetAsset Read(ExportEntry export)
    {
        var binary = export.GetBinaryData<MorphTarget>();
        var lods = binary.MorphLODModels?
            .Select((lod, lodIndex) => new MorphTargetLod(
                lodIndex,
                lod.NumBaseMeshVerts,
                lod.Vertices?.Select(vertex => new MorphVertexDelta(
                    vertex.SourceIdx,
                    vertex.PositionDelta,
                    (Vector3)vertex.TangentZDelta)).ToArray() ?? []))
            .ToArray() ?? [];
        var bones = binary.BoneOffsets?
            .Select(offset => new MorphTargetBoneOffset(offset.Bone.Instanced, offset.Offset))
            .ToArray() ?? [];

        return new MorphTargetAsset(
            MorphFacePackageReader.ToIdentity(export)!,
            lods,
            bones);
    }
}
