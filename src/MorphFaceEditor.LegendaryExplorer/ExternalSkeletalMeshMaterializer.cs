using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Ports an externally selected skeletal mesh and its dependency graph into the
/// destination package while preserving the mesh's original UE3 path.
/// </summary>
internal static class ExternalSkeletalMeshMaterializer
{
    internal static ExportEntry Materialize(
        IMEPackage destination,
        AssetIdentity identity,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(identity);

        if (destination.FindExport(identity.InstancedPath, "SkeletalMesh") is { } existing)
        {
            return existing;
        }
        if (!File.Exists(identity.PackagePath))
        {
            throw new FileNotFoundException(
                $"The selected skeletal mesh's source package no longer exists: {identity.PackagePath}",
                identity.PackagePath);
        }

        using var source = MEPackageHandler.OpenMEPackage(identity.PackagePath, forceLoadFromDisk: true);
        if (source.Game != destination.Game)
        {
            throw new InvalidDataException(
                $"SkeletalMesh '{identity.InstancedPath}' is from {source.Game}, but the destination is {destination.Game}.");
        }
        var sourceExport = ResolveSourceExport(source, identity);
        var parent = EnsurePackagePath(destination, identity.InstancedPath);
        var relinker = new RelinkerOptionsPackage
        {
            ImportExportDependencies = true,
            GenerateImportsForGlobalFiles = false
        };
        var imported = EntryImporter.ImportExport(destination, sourceExport, parent?.UIndex ?? 0, relinker);
        Relinker.RelinkAll(relinker);
        AddWarnings(warnings, relinker.RelinkReport.Select(item => item.Message));

        if (imported is not ExportEntry meshExport ||
            !meshExport.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"LEC did not materialise SkeletalMesh '{identity.InstancedPath}' as an export.");
        }
        if (!meshExport.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The materialised skeletal mesh path changed from '{identity.InstancedPath}' " +
                $"to '{meshExport.InstancedFullPath}'.");
        }
        return meshExport;
    }

    private static ExportEntry ResolveSourceExport(IMEPackage source, AssetIdentity identity)
    {
        if (identity.UIndex > 0 && source.IsUExport(identity.UIndex) &&
            source.GetUExport(identity.UIndex) is { } indexed &&
            indexed.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
            indexed.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase))
        {
            return indexed;
        }
        return source.FindExport(identity.InstancedPath, "SkeletalMesh")
               ?? throw new InvalidDataException(
                   $"SkeletalMesh '{identity.InstancedPath}' was not found in '{identity.PackagePath}'.");
    }

    private static ExportEntry? EnsurePackagePath(IMEPackage destination, string meshPath)
    {
        var segments = meshPath.Split('.');
        ExportEntry? parent = null;
        var currentPath = string.Empty;
        foreach (var segment in segments.Take(segments.Length - 1))
        {
            currentPath = currentPath.Length == 0 ? segment : $"{currentPath}.{segment}";
            parent = destination.FindExport(currentPath, "Package")
                     ?? destination.CreatePackageExport(
                         NameReference.FromInstancedString(segment),
                         parent);
        }
        return parent;
    }

    private static void AddWarnings(ICollection<string>? target, IEnumerable<string> warnings)
    {
        if (target is null) return;
        foreach (var warning in warnings.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            target.Add(warning);
        }
    }
}
