using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

internal sealed record AttachmentTransferResult(
    IEntry? Hair,
    IReadOnlyList<IEntry> OtherMeshes,
    IReadOnlyList<string> Warnings);

internal static class MorphFaceAttachmentTransferEngine
{
    private static readonly string[] CommonDonorPackages =
    [
        "BIOG_HMM_HIR_PRO_R.pcc",
        "BIOG_HMF_HIR_PRO.pcc"
    ];

    internal static AttachmentTransferResult Transfer(
        IMEPackage destination,
        AssetIdentity? sourceHair,
        IReadOnlyList<AssetIdentity?> sourceOtherMeshes,
        MEGame sourceGame,
        string? targetTemplatePackagePath)
    {
        var warnings = new List<string>();
        using var donors = new DonorPackages();
        var hair = ResolveOne(
            destination, sourceHair, sourceGame, "Hair", targetTemplatePackagePath, donors, warnings);
        var others = sourceOtherMeshes
            .Select(value => ResolveOne(
                destination, value, sourceGame, "Other", targetTemplatePackagePath, donors, warnings))
            .Where(value => value is not null)
            .Cast<IEntry>()
            .ToArray();
        return new AttachmentTransferResult(hair, others, warnings);
    }

    private static IEntry? ResolveOne(
        IMEPackage destination,
        AssetIdentity? source,
        MEGame sourceGame,
        string kind,
        string? targetTemplatePackagePath,
        DonorPackages donors,
        ICollection<string> warnings)
    {
        if (source is null)
        {
            return null;
        }
        var requestedPath = source.InstancedPath;
        if (CrossGameAssetReconciliationCatalog.TryResolve(
                sourceGame, destination.Game, kind, null, source.InstancedPath, out var reviewedPath))
        {
            if (reviewedPath is null)
            {
                warnings.Add(
                    $"The canonical {sourceGame}->{destination.Game} corpora omit {kind.ToLowerInvariant()} " +
                    $"mesh '{source.InstancedPath}'; the converted face leaves it unset.");
                return null;
            }
            return MaterializeTargetMesh(
                destination, reviewedPath, targetTemplatePackagePath, donors, warnings);
        }

        var resolvedPath = ResolveTargetPath(
            destination, requestedPath, targetTemplatePackagePath, donors);
        if (resolvedPath is null)
        {
            warnings.Add(
                $"{kind} mesh '{source.InstancedPath}' has no unique {destination.Game} donor and was omitted.");
            return null;
        }
        return MaterializeTargetMesh(
            destination, resolvedPath, targetTemplatePackagePath, donors, warnings);
    }

    private static string? ResolveTargetPath(
        IMEPackage destination,
        string requestedPath,
        string? targetTemplatePackagePath,
        DonorPackages donors)
    {
        if (destination.FindEntry(requestedPath, "SkeletalMesh") is not null)
        {
            return requestedPath;
        }
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in DonorPaths(destination.Game, requestedPath, targetTemplatePackagePath))
        {
            if (!File.Exists(path) || SamePath(path, destination.FilePath))
            {
                continue;
            }
            var donor = donors.Open(path);
            if (donor.Game != destination.Game)
            {
                continue;
            }
            foreach (var entry in donor.Exports.Cast<IEntry>().Concat(donor.Imports).Where(entry =>
                         entry.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(CanonicalPath(donor, entry));
            }
        }
        if (candidates.Contains(requestedPath))
        {
            return requestedPath;
        }
        var objectName = requestedPath.Split('.').Last();
        var matches = candidates.Where(path =>
            path.Split('.').Last().Equals(objectName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static IEnumerable<string> DonorPaths(
        MEGame game,
        string requestedPath,
        string? targetTemplatePackagePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(targetTemplatePackagePath))
        {
            var template = Path.GetFullPath(targetTemplatePackagePath);
            if (seen.Add(template)) yield return template;
        }
        var loadedFiles = MELoadedFiles.GetFilesLoadedInGame(game, forceUseCached: true);
        var root = requestedPath.Split('.')[0] + ".pcc";
        if (loadedFiles.TryGetValue(root, out var rootPath) && seen.Add(rootPath)) yield return rootPath;
        foreach (var fileName in CommonDonorPackages)
        {
            if (loadedFiles.TryGetValue(fileName, out var path) && seen.Add(path)) yield return path;
        }
    }

    internal static IEntry EnsureImport(IMEPackage destination, string path)
    {
        if (destination.FindEntry(path, "SkeletalMesh") is { } existing)
        {
            return existing;
        }
        IEntry? parent = null;
        var segments = path.Split('.');
        var currentPath = string.Empty;
        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = index == 0 ? segments[index] : $"{currentPath}.{segments[index]}";
            var className = index == segments.Length - 1 ? "SkeletalMesh" : "Package";
            parent = destination.FindEntry(currentPath, className) ?? (index == 0
                ? destination.CreatePackageImport(NameReference.FromInstancedString(segments[index]))
                : destination.CreateImport(
                    className, NameReference.FromInstancedString(segments[index]), parent));
        }
        return parent!;
    }

    private static IEntry? MaterializeTargetMesh(
        IMEPackage destination,
        string canonicalPath,
        string? targetTemplatePackagePath,
        DonorPackages donors,
        ICollection<string> warnings)
    {
        if (destination.FindExport(canonicalPath, "SkeletalMesh") is { } existing)
        {
            return existing;
        }
        foreach (var path in DonorPaths(destination.Game, canonicalPath, targetTemplatePackagePath))
        {
            if (!File.Exists(path) || SamePath(path, destination.FilePath)) continue;
            var donor = donors.Open(path);
            if (donor.Game != destination.Game) continue;
            var export = donor.FindExport(canonicalPath, "SkeletalMesh")
                         ?? donor.Exports.FirstOrDefault(entry =>
                             entry.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                             CanonicalPath(donor, entry).Equals(
                                 canonicalPath, StringComparison.OrdinalIgnoreCase));
            export ??= FindUniqueExportByObjectName(donor, canonicalPath);
            if (export is null) continue;
            return ExternalSkeletalMeshMaterializer.Materialize(
                destination,
                path,
                canonicalPath,
                "SkeletalMesh",
                export.UIndex,
                warnings);
        }
        warnings.Add(
            $"Target SkeletalMesh '{canonicalPath}' was identified but could not be materialised; it was omitted.");
        return null;
    }

    private static ExportEntry? FindUniqueExportByObjectName(IMEPackage package, string canonicalPath)
    {
        var objectName = canonicalPath.Split('.').Last();
        var matches = package.Exports.Where(export =>
            export.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
            export.ObjectName.Instanced.Equals(objectName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string CanonicalPath(IMEPackage package, IEntry entry)
    {
        if (entry is ImportEntry || entry.InstancedFullPath.StartsWith("BIO", StringComparison.OrdinalIgnoreCase))
        {
            return entry.InstancedFullPath;
        }
        return $"{Path.GetFileNameWithoutExtension(package.FilePath)}.{entry.InstancedFullPath}";
    }

    private sealed class DonorPackages : IDisposable
    {
        private readonly Dictionary<string, IMEPackage> _packages = new(StringComparer.OrdinalIgnoreCase);

        internal IMEPackage Open(string path)
        {
            var fullPath = Path.GetFullPath(path);
            return _packages.TryGetValue(fullPath, out var package)
                ? package
                : _packages[fullPath] = MEPackageHandler.OpenMEPackage(fullPath, forceLoadFromDisk: true);
        }

        public void Dispose()
        {
            foreach (var package in _packages.Values) package.Dispose();
        }
    }
}
