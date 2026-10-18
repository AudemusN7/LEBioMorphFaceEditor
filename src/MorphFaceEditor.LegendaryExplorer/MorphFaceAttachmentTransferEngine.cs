using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

internal sealed record AttachmentTransferResult(
    IEntry? Hair,
    IReadOnlyList<IEntry> OtherMeshes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<UserFacingMessage> UserWarnings);

internal static class MorphFaceAttachmentTransferEngine
{
    internal static AttachmentTransferResult Transfer(
        IMEPackage destination,
        AssetIdentity? sourceHair,
        IReadOnlyList<AssetIdentity?> sourceOtherMeshes,
        MEGame sourceGame,
        string? targetTemplatePackagePath)
    {
        var warnings = new List<string>();
        var userWarnings = new List<UserFacingMessage>();
        using var donors = new DonorPackages();
        var hair = ResolveOne(
            destination, sourceHair, sourceGame, "Hair", targetTemplatePackagePath, donors, warnings, userWarnings);
        var others = sourceOtherMeshes
            .Select(value => ResolveOne(
                destination, value, sourceGame, "Other", targetTemplatePackagePath, donors, warnings, userWarnings))
            .Where(value => value is not null)
            .Cast<IEntry>()
            .ToArray();
        return new AttachmentTransferResult(hair, others, warnings, userWarnings);
    }

    private static IEntry? ResolveOne(
        IMEPackage destination,
        AssetIdentity? source,
        MEGame sourceGame,
        string kind,
        string? targetTemplatePackagePath,
        DonorPackages donors,
        ICollection<string> warnings,
        ICollection<UserFacingMessage> userWarnings)
    {
        if (source is null)
        {
            return null;
        }
        if (AttachmentMeshNamePolicy.IsExcludedNativeAttachment(source.InstancedPath))
        {
            var diagnostic = $"Optional attachment '{source.InstancedPath}' was omitted because it is a reviewed excluded native mesh.";
            warnings.Add(diagnostic);
            userWarnings.Add(UserFacingMessage.CreateWarning(
                "loc_Services_AttachmentTransfer_ExcludedNativeMesh", diagnostic, source.InstancedPath));
            return null;
        }

        var requestedPath = source.InstancedPath;
        if ((source.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
             ((kind.Equals("Hair", StringComparison.OrdinalIgnoreCase) &&
               CanonicalHairDonorPolicy.TryResolve(sourceGame, destination.Game, source.InstancedPath, out var reviewedPath)) ||
              CanonicalHatDonorPolicy.TryResolve(sourceGame, destination.Game, source.InstancedPath, out reviewedPath))) ||
            CrossGameAssetReconciliationCatalog.TryResolve(
                sourceGame, destination.Game, kind, null, source.InstancedPath, out reviewedPath))
        {
            if (reviewedPath is null)
            {
                var diagnostic =
                    $"The reviewed {sourceGame}->{destination.Game} donors omit {kind.ToLowerInvariant()} " +
                    $"mesh '{source.InstancedPath}'; the converted face leaves it unset.";
                warnings.Add(diagnostic);
                userWarnings.Add(UserFacingMessage.CreateWarning(
                    "loc_Services_AttachmentTransfer_ReviewedDonorMissing",
                    diagnostic,
                    sourceGame.ToString(), destination.Game.ToString(), source.InstancedPath));
                return null;
            }
            return MaterializeTargetMesh(
                destination, reviewedPath, targetTemplatePackagePath, donors, warnings);
        }

        return MaterializeTargetMesh(
            destination, requestedPath, targetTemplatePackagePath, donors, warnings);
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
        if (donors.ResolveOfficial(destination.Game, canonicalPath, "SkeletalMesh") is { } installed)
        {
            if (!HasSupportedMaterials(installed.FileRef, installed, out var unsupported))
            {
                warnings.Add(
                    $"Optional attachment '{canonicalPath}' was omitted because it requires unsupported material '{unsupported}'.");
                return null;
            }
            return ExternalSkeletalMeshMaterializer.MaterializeResolved(destination, installed, warnings);
        }

        var existingMatches = FindExactExports(destination, canonicalPath);
        if (existingMatches.Length > 1)
        {
            warnings.Add($"Target SkeletalMesh '{canonicalPath}' is ambiguous in the destination and was omitted.");
            return null;
        }
        if (existingMatches.SingleOrDefault() is { } existing)
        {
            if (!HasSupportedMaterials(destination, existing, out var unsupported))
            {
                warnings.Add(
                    $"Optional attachment '{canonicalPath}' was omitted because it requires unsupported material '{unsupported}'.");
                return null;
            }
            return existing;
        }

        if (!string.IsNullOrWhiteSpace(targetTemplatePackagePath) &&
            File.Exists(targetTemplatePackagePath) &&
            !SamePath(targetTemplatePackagePath, destination.FilePath))
        {
            var template = donors.Open(targetTemplatePackagePath);
            if (template.Game == destination.Game)
            {
                var templateMatches = FindExactExports(template, canonicalPath);
                if (templateMatches.Length > 1)
                {
                    warnings.Add(
                        $"Target SkeletalMesh '{canonicalPath}' has ambiguous exact exports in the template and was omitted.");
                    return null;
                }
                if (templateMatches.SingleOrDefault() is { } templateExport)
                {
                    if (!HasSupportedMaterials(template, templateExport, out var unsupported))
                    {
                        warnings.Add(
                            $"Optional attachment '{canonicalPath}' was omitted because it requires unsupported material '{unsupported}'.");
                        return null;
                    }
                    return ExternalSkeletalMeshMaterializer.MaterializeResolved(destination, templateExport, warnings);
                }
            }
        }

        warnings.Add(
            $"Target SkeletalMesh '{canonicalPath}' has no exact {destination.Game} installed or template donor and was omitted.");
        return null;
    }

    private static ExportEntry[] FindExactExports(IMEPackage package, string canonicalPath) =>
        package.Exports.Where(export =>
                export.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                (export.InstancedFullPath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase) ||
                 PccAssetPathPolicy.FromExport(export).Equals(canonicalPath, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();

    internal static bool HasSupportedMaterials(IMEPackage package, ExportEntry mesh, out string unsupported)
    {
        var materialIndexes = mesh.GetBinaryData<SkeletalMesh>().Materials;
        if (materialIndexes is null || materialIndexes.Length == 0)
        {
            unsupported = string.Empty;
            return true;
        }

        using var cache = new PackageCache { CacheMaxSize = 8 };
        var referenceResolver = new GamePackageReferenceResolver(cache);
        foreach (var materialIndex in materialIndexes)
        {
            if (materialIndex == 0)
            {
                continue;
            }
            var materialReference = package.GetEntry(materialIndex);
            var material = materialReference is null ? null :
                CanonicalAttachmentMaterialPolicy.ResolveReplacement(mesh, materialReference, cache)
                ?? referenceResolver.Resolve(materialReference);
            if (material is null)
            {
                unsupported = $"unresolved material reference {materialIndex}";
                return false;
            }

            if (SupportedMaterialGraphPolicy.IsSupported(
                    package.Game, material, referenceResolver, out var graphFailure))
            {
                continue;
            }

            unsupported = $"{material.ClassName} '{PccAssetPathPolicy.FromExport(material)}': {graphFailure}";
            return false;
        }

        unsupported = string.Empty;
        return true;
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed class DonorPackages : IDisposable
    {
        private readonly PackageCache _cache = new() { CacheMaxSize = 8 };
        private readonly InstalledGameAssetResolver _installedAssets = new();

        internal IMEPackage Open(string path) => _cache.GetCachedPackage(Path.GetFullPath(path))!;

        internal ExportEntry? ResolveOfficial(MEGame game, string path, string className) =>
            _installedAssets.Resolve(game, path, className, _cache);

        public void Dispose() => _cache.Dispose();
    }
}
