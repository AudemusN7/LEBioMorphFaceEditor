using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

internal sealed record TextureRegistryScannedTexture(
    string InstancedPath,
    TextureCatalogOccurrence Occurrence);

internal sealed record TextureRegistryPackageScan(
    IReadOnlyList<TextureRegistryScannedTexture> Textures,
    IReadOnlyList<MorphFaceTemplateCandidate> MorphFaceTemplates);

internal interface ITextureRegistryPackageScanner
{
    TextureRegistryPackageScan Scan(
        MorphFaceGame game,
        string packagePath,
        CancellationToken cancellationToken);
}

/// <summary>Opens one installed package once and detaches only relevant Texture2D metadata.</summary>
internal sealed class LecTextureRegistryPackageScanner : ITextureRegistryPackageScanner
{
    public TextureRegistryPackageScan Scan(
        MorphFaceGame game,
        string packagePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LegendaryExplorerCoreRuntime.Initialize();
        var meGame = ToMeGame(game);
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var results = new List<TextureRegistryScannedTexture>();
        var templates = new List<MorphFaceTemplateCandidate>();
        var origin = GetOrigin(packagePath, meGame);
        var mountPriority = GetMountPriority(packagePath, meGame);
        foreach (var export in package.Exports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!export.IsDefaultObject &&
                export.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase))
            {
                var baseHead = export.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package);
                templates.Add(new MorphFaceTemplateCandidate(
                    Path.GetFullPath(packagePath),
                    export.UIndex,
                    export.InstancedFullPath,
                    baseHead?.InstancedFullPath,
                    mountPriority,
                    origin));
            }
            if (export.IsDefaultObject ||
                !export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
                (!TextureRegistryDiscovery.IsRelevantPath(export.InstancedFullPath) &&
                 !CrossGameAssetReconciliationCatalog.IsReviewedTexturePath(export.InstancedFullPath)))
            {
                continue;
            }

            var texture = new Texture2D(export);
            var topMip = texture.GetTopMip();
            var mips = texture.Mips
                .Where(mip => mip.storageType != StorageTypes.empty)
                .Select(mip => new TextureMipStorageRecord(
                    mip.index,
                    mip.width,
                    mip.height,
                    (int)mip.storageType,
                    mip.uncompressedSize,
                    mip.compressedSize,
                    mip.externalOffset,
                    mip.TextureCacheName))
                .ToArray();
            var hasExternalMips = texture.Mips.Any(mip =>
                mip.storageType != StorageTypes.empty &&
                ((int)mip.storageType & (int)StorageFlags.externalFile) != 0);
            var occurrence = new TextureCatalogOccurrence(
                Path.GetFullPath(packagePath),
                export.UIndex,
                mountPriority,
                origin,
                topMip?.width ?? 0,
                topMip?.height ?? 0,
                export.GetProperty<EnumProperty>("Format")?.Value.Name ?? texture.TextureFormat ?? "Unknown",
                export.GetProperty<EnumProperty>("LODGroup")?.Value.Name ?? "Unknown",
                hasExternalMips,
                export.GetProperty<NameProperty>("TextureFileCacheName")?.Value.Instanced)
            {
                Mips = mips
            };
            results.Add(new TextureRegistryScannedTexture(export.InstancedFullPath, occurrence));
        }

        return new TextureRegistryPackageScan(results, templates);
    }

    private static TextureCatalogOrigin GetOrigin(string packagePath, MEGame game)
    {
        var dlcDirectory = FindDlcDirectory(packagePath);
        if (dlcDirectory is null) return TextureCatalogOrigin.BaseGame;
        return MELoadedDLC.IsOfficialDLC(dlcDirectory, game)
            ? TextureCatalogOrigin.OfficialDlc
            : TextureCatalogOrigin.Mod;
    }

    private static int GetMountPriority(string packagePath, MEGame game)
    {
        var dlcDirectory = FindDlcDirectory(packagePath);
        return dlcDirectory is null ? 0 : MELoadedDLC.GetMountPriority(dlcDirectory, game);
    }

    private static string? FindDlcDirectory(string packagePath)
    {
        var current = new FileInfo(packagePath).Directory;
        while (current?.Parent is not null)
        {
            if (current.Parent.Name.Equals("DLC", StringComparison.OrdinalIgnoreCase) &&
                current.Name.StartsWith("DLC_", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }

    internal static MEGame ToMeGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => MEGame.LE1,
        MorphFaceGame.LE2 => MEGame.LE2,
        MorphFaceGame.LE3 => MEGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game,
            "Texture registries are available only for Legendary Edition games.")
    };
}
