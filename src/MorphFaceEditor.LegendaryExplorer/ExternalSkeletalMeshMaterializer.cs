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
        => Materialize(
            destination,
            identity.PackagePath,
            identity.InstancedPath,
            "SkeletalMesh",
            identity.UIndex,
            warnings);

    internal static ExportEntry Materialize(
        IMEPackage destination,
        string donorPackagePath,
        string instancedPath,
        string className,
        int sourceUIndex = 0,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(donorPackagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);

        if (destination.FindExport(instancedPath, className) is { } existing)
        {
            return existing;
        }
        if (!File.Exists(donorPackagePath))
        {
            throw new FileNotFoundException(
                $"The selected asset's donor package no longer exists: {donorPackagePath}",
                donorPackagePath);
        }

        using var source = MEPackageHandler.OpenMEPackage(donorPackagePath, forceLoadFromDisk: true);
        if (source.Game != destination.Game)
        {
            throw new InvalidDataException(
                $"{className} '{instancedPath}' is from {source.Game}, but the destination is {destination.Game}.");
        }
        var sourceExport = ResolveSourceExport(source, instancedPath, className, sourceUIndex)
                           ?? throw new InvalidDataException(
                               $"{className} '{instancedPath}' was not found in '{source.FilePath}'.");
        return MaterializeResolved(
            destination,
            source,
            sourceExport,
            instancedPath,
            className,
            warnings,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static ExportEntry MaterializeResolved(
        IMEPackage destination,
        IMEPackage source,
        ExportEntry sourceExport,
        string instancedPath,
        string className,
        ICollection<string>? warnings,
        HashSet<string> active)
    {
        if (destination.FindExport(instancedPath, className) is { } existing)
        {
            return existing;
        }

        var key = $"{className}:{instancedPath}";
        if (!active.Add(key))
        {
            throw new InvalidDataException($"Circular target-donor materialisation detected at '{instancedPath}'.");
        }

        var parent = PackageIntegrity.EnsurePackagePath(destination, instancedPath, className);
        PrepareReferencedPackagePaths(destination, sourceExport);

        foreach (var import in EntryImporter.GetAllReferencesOfExport(sourceExport).OfType<ImportEntry>()
                     .Where(import => import.ClassName is "Texture2D" or "SkeletalMesh"))
        {
            if (destination.FindEntry(import.InstancedFullPath, import.ClassName) is not null)
            {
                continue;
            }
            var dependency = ResolveSourceExport(
                source,
                import.InstancedFullPath,
                import.ClassName,
                sourceUIndex: 0,
                required: false);
            if (dependency is not null)
            {
                MaterializeResolved(
                    destination,
                    source,
                    dependency,
                    import.InstancedFullPath,
                    import.ClassName,
                    warnings,
                    active);
            }
        }

        var relinker = new RelinkerOptionsPackage
        {
            ImportExportDependencies = true,
            GenerateImportsForGlobalFiles = false
        };
        var imported = EntryImporter.ImportExport(destination, sourceExport, parent?.UIndex ?? 0, relinker);
        MaterialisationVerifier.Relink(relinker);

        if (imported is not ExportEntry materialized ||
            !materialized.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"LEC did not materialise {className} '{instancedPath}' as an export.");
        }
        if (!materialized.InstancedFullPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The materialised {className} path changed from '{instancedPath}' " +
                $"to '{materialized.InstancedFullPath}'.");
        }
        active.Remove(key);
        MaterialisationVerifier.Verify(materialized, source.Game, relinker, warnings);
        return materialized;
    }

    /// <summary>
    /// Reserves package ancestors as exports before LEC walks the dependency graph.
    /// Otherwise a referenced export can cause LEC to create its root as an import
    /// first, producing the invalid export-beneath-import hierarchy seen by Package Editor.
    /// </summary>
    internal static void PrepareReferencedPackagePaths(
        IMEPackage destination,
        ExportEntry sourceExport)
    {
        var referencedExports = EntryImporter.GetAllReferencesOfExport(sourceExport)
            .OfType<ExportEntry>()
            .Append(sourceExport);
        foreach (var referencedExport in referencedExports)
        {
            var packages = new Stack<IEntry>();
            for (IEntry? entry = referencedExport; entry is not null; entry = entry.Parent)
            {
                if (entry.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase))
                {
                    packages.Push(entry);
                }
            }

            ExportEntry? parent = null;
            while (packages.TryPop(out var sourcePackage))
            {
                var path = parent is null
                    ? sourcePackage.ObjectName.Instanced
                    : $"{parent.InstancedFullPath}.{sourcePackage.ObjectName.Instanced}";
                parent = PackageIntegrity.EnsurePackagePath(destination, path + ".__reserved_asset");
            }
        }
    }

    private static ExportEntry? ResolveSourceExport(
        IMEPackage source,
        string instancedPath,
        string className,
        int sourceUIndex,
        bool required = true)
    {
        if (sourceUIndex > 0 && source.IsUExport(sourceUIndex) &&
            source.GetUExport(sourceUIndex) is { } indexed &&
            indexed.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
        {
            return indexed;
        }
        var found = source.FindExport(instancedPath, className)
                    ?? source.Exports.FirstOrDefault(export =>
                        export.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) &&
                        CanonicalPath(source, export).Equals(instancedPath, StringComparison.OrdinalIgnoreCase));
        return found ?? (required
            ? throw new InvalidDataException(
                $"{className} '{instancedPath}' was not found in '{source.FilePath}'.")
            : null);
    }

    private static string CanonicalPath(IMEPackage package, IEntry entry)
    {
        if (entry.InstancedFullPath.StartsWith("BIO", StringComparison.OrdinalIgnoreCase))
        {
            return entry.InstancedFullPath;
        }
        return $"{Path.GetFileNameWithoutExtension(package.FilePath)}.{entry.InstancedFullPath}";
    }
}
