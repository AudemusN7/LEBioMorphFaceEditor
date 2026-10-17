using LegendaryExplorerCore.Packages;
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

        using var cache = new PackageCache();
        LegendaryExplorerCoreRuntime.Initialize();
        var sourceExport = new InstalledGameAssetResolver().Resolve(
            destination.Game, identity with { IsExplicitTextureDonor = true }, cache)
            ?? PackageAssetInspector.ResolveTextureExport(cache.GetCachedPackage(identity.PackagePath)
                ?? throw new FileNotFoundException("The selected texture's source package could not be opened.", identity.PackagePath), identity);
        if (sourceExport.Game != destination.Game)
            throw new InvalidDataException($"Texture '{identity.InstancedPath}' is from {sourceExport.Game}, but the destination is {destination.Game}.");
        identity = identity with { InstancedPath = PccAssetPathPolicy.FromExport(sourceExport) };
        var textureExport = PccPackageWorkflow.ImportDependencyGraph(
            destination,
            sourceExport,
            destinationParent: null,
            textureCatalog: null,
            warnings: warnings,
            applyCorpusMaterialPolicy: false,
            targetPathOverride: identity.InstancedPath);
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
        return textureExport;
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
