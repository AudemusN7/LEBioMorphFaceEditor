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
        ICollection<string>? warnings = null,
        PackageCache? sourceCache = null,
        InstalledGameAssetResolver? installedAssets = null,
        ICollection<UserFacingMessage>? userWarnings = null)
        => Materialize(
            destination,
            identity.PackagePath,
            identity.InstancedPath,
            "SkeletalMesh",
            identity.UIndex,
            warnings,
            sourceCache,
            installedAssets,
            userWarnings);

    internal static ExportEntry Materialize(
        IMEPackage destination,
        string donorPackagePath,
        string instancedPath,
        string className,
        int sourceUIndex = 0,
        ICollection<string>? warnings = null,
        PackageCache? sourceCache = null,
        InstalledGameAssetResolver? installedAssets = null,
        ICollection<UserFacingMessage>? userWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(donorPackagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);

        using var ownedCache = sourceCache is null ? new PackageCache { CacheMaxSize = 64 } : null;
        var cache = sourceCache ?? ownedCache!;
        LegendaryExplorerCoreRuntime.Initialize();
        var resolver = installedAssets ?? new InstalledGameAssetResolver();
        var installed = resolver.Resolve(destination.Game,
            new AssetIdentity(donorPackagePath, instancedPath, sourceUIndex, className), cache);
        if (installed is not null)
            return MaterializeResolved(destination, installed, warnings, cache, resolver, userWarnings);
        if (destination.FindExport(instancedPath, className) is { } existing)
            return existing;
        if (!File.Exists(donorPackagePath))
        {
            throw new FileNotFoundException(
                $"The selected asset's donor package no longer exists: {donorPackagePath}",
                donorPackagePath);
        }

        var source = PackageSourceCache.GetPackage(cache, donorPackagePath)
            ?? throw new FileNotFoundException(
                $"The selected asset's donor package no longer exists: {donorPackagePath}", donorPackagePath);
        if (source.Game != destination.Game)
        {
            throw new InvalidDataException(
                $"{className} '{instancedPath}' is from {source.Game}, but the destination is {destination.Game}.");
        }
        var sourceExport = ResolveSourceExport(source, instancedPath, className, sourceUIndex);
        return MaterializeResolved(destination, sourceExport, warnings, cache, resolver, userWarnings);
    }

    internal static ExportEntry MaterializeResolved(
        IMEPackage destination,
        ExportEntry sourceExport,
        ICollection<string>? warnings,
        PackageCache? sourceCache = null,
        InstalledGameAssetResolver? installedAssets = null,
        ICollection<UserFacingMessage>? userWarnings = null)
    {
        var instancedPath = PccAssetPathPolicy.FromExport(sourceExport);
        var className = sourceExport.ClassName;
        var materialized = PccPackageWorkflow.ImportDependencyGraph(
            destination,
            sourceExport,
            destinationParent: null,
            textureCatalog: null,
            warnings: warnings,
            applyCorpusMaterialPolicy: true,
            targetPathOverride: instancedPath,
            installedAssets: installedAssets,
            sourceCache: sourceCache,
            userWarnings: userWarnings);
        if (
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
        return materialized;
    }

    /// <summary>
    /// Stages package ancestors before LEC walks the dependency graph, retaining
    /// existing destination parents and the source role of missing parents.
    /// Cooked MELE packages support exports beneath imported package parents.
    /// </summary>
    internal static void PrepareReferencedPackagePaths(
        IMEPackage destination,
        ExportEntry sourceExport,
        IReadOnlyDictionary<IEntry, IEntry>? plannedMappings = null)
    {
        var pending = new Queue<ExportEntry>();
        var visited = new HashSet<ExportEntry>();
        pending.Enqueue(sourceExport);
        while (pending.TryDequeue(out var referencedExport))
        {
            if (!visited.Add(referencedExport)) continue;
            // Preseeded dependencies already have their destination graph. Do
            // not reserve the discarded donor subtree as exports: it may have
            // been deliberately replaced by a native import.
            if (plannedMappings?.ContainsKey(referencedExport) == true) continue;
            foreach (var index in PackageIntegrity.References(referencedExport).Values.Where(value => value != 0).Distinct())
            {
                if (referencedExport.FileRef.GetEntry(index) is ExportEntry dependency)
                    pending.Enqueue(dependency);
            }
            var packages = new Stack<IEntry>();
            for (IEntry? entry = referencedExport; entry is not null; entry = entry.Parent)
            {
                if (entry.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase))
                {
                    packages.Push(entry);
                }
            }

            IEntry? parent = null;
            while (packages.TryPop(out var sourcePackage))
            {
                var path = parent is null
                    ? sourcePackage.ObjectName.Instanced
                    : $"{parent.InstancedFullPath}.{sourcePackage.ObjectName.Instanced}";
                parent = PackageIntegrity.FindExactEntry(destination, path, "Package") ??
                    (sourcePackage is ImportEntry
                        ? destination.CreatePackageImport(sourcePackage.ObjectName, parent)
                        : destination.CreatePackageExport(sourcePackage.ObjectName, parent));
            }
        }
    }

    private static ExportEntry ResolveSourceExport(
        IMEPackage source,
        string instancedPath,
        string className,
        int sourceUIndex)
    {
        bool Matches(ExportEntry export) =>
            export.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) &&
            (export.InstancedFullPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase) ||
             CanonicalPath(source, export).Equals(instancedPath, StringComparison.OrdinalIgnoreCase));

        if (sourceUIndex > 0 && source.IsUExport(sourceUIndex) &&
            source.GetUExport(sourceUIndex) is { } indexed &&
            Matches(indexed))
        {
            return indexed;
        }
        var matches = source.Exports.Where(Matches).Take(2).ToArray();
        var found = matches.Length == 1 ? matches[0] : null;
        return found ?? throw new InvalidDataException(
            $"{className} '{instancedPath}' was {(matches.Length == 0 ? "not found" : "ambiguous")} in '{source.FilePath}'.");
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
