using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Textures;
using LegendaryExplorerCore.Unreal;
using LecTexture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Ports an externally selected texture into the package at its original UE3 path and
/// converts its mip chain to package storage so the saved PCC has no donor-TFC dependency.
/// </summary>
internal static class ExternalTextureMaterializer
{
    internal static ExportEntry Materialize(
        IMEPackage destination,
        AssetIdentity identity,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(identity);

        if (destination.FindExport(identity.InstancedPath, "Texture2D") is { } existing)
        {
            return existing;
        }
        if (!File.Exists(identity.PackagePath))
        {
            throw new FileNotFoundException(
                $"The selected texture's source package no longer exists: {identity.PackagePath}",
                identity.PackagePath);
        }

        using var source = MEPackageHandler.OpenMEPackage(identity.PackagePath, forceLoadFromDisk: true);
        if (source.Game != destination.Game)
        {
            throw new InvalidDataException(
                $"Texture '{identity.InstancedPath}' is from {source.Game}, but the destination is {destination.Game}.");
        }
        var sourceExport = ResolveSourceExport(source, identity);
        var parent = EnsurePackagePath(destination, identity.InstancedPath);
        ExternalSkeletalMeshMaterializer.PrepareReferencedPackagePaths(destination, sourceExport);
        var relinker = new RelinkerOptionsPackage { ImportExportDependencies = true };
        var imported = EntryImporter.ImportExport(destination, sourceExport, parent?.UIndex ?? 0, relinker);
        MaterialisationVerifier.Relink(relinker);

        if (imported is not ExportEntry textureExport ||
            !textureExport.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"LEC did not materialise Texture2D '{identity.InstancedPath}' as an export.");
        }
        if (!textureExport.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The materialised texture path changed from '{identity.InstancedPath}' " +
                $"to '{textureExport.InstancedFullPath}'.");
        }

        var sourceTexture = new LecTexture2D(sourceExport);
        var pixelFormat = Image.getPixelFormatType(sourceTexture.TextureFormat);
        var image = sourceTexture.ToImage(pixelFormat);
        var replacement = new LecTexture2D(textureExport);
        AddWarnings(warnings, replacement.Replace(
            image,
            textureExport.GetProperties(),
            isPackageStored: true));
        MaterialisationVerifier.Verify(textureExport, source.Game, relinker, warnings);
        return textureExport;
    }

    internal static ExportEntry ResolveSourceExport(IMEPackage source, AssetIdentity identity)
    {
        // A package can store Scars.Texture locally while RON and the registry
        // address it as Package.Scars.Texture. Only the source package's exact
        // name may qualify that local path; UIndex alone is not identity proof.
        bool Matches(ExportEntry export) =>
            export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
            (export.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase) ||
             $"{Path.GetFileNameWithoutExtension(source.FilePath)}.{export.InstancedFullPath}"
                 .Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase));

        if (identity.UIndex > 0 && source.IsUExport(identity.UIndex) &&
            source.GetUExport(identity.UIndex) is { } indexed &&
            Matches(indexed))
        {
            return indexed;
        }
        var matches = source.Exports.Where(Matches).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException(
            $"Texture2D '{identity.InstancedPath}' was {(matches.Length == 0 ? "not found" : "ambiguous")} in '{identity.PackagePath}'.");
    }

    private static ExportEntry? EnsurePackagePath(IMEPackage destination, string assetPath)
        => PackageIntegrity.EnsurePackagePath(destination, assetPath, "Texture2D");

    private static void AddWarnings(ICollection<string>? target, IEnumerable<string> warnings)
    {
        if (target is null) return;
        foreach (var warning in warnings.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            target.Add(warning);
        }
    }
}
