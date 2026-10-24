using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Models;

namespace MorphFaceEditor.Services;

public sealed record PackageReferenceCatalog(
    IReadOnlyList<PackageAssetListItem> Textures,
    IReadOnlyList<PackageAssetListItem> SkeletalMeshes);

public interface ITextureReferenceLoader
{
    Task<DecodedTextureAsset> LoadTextureAsync(
        string packagePath,
        string texturePath,
        MaterialParameterDefinition definition,
        CancellationToken cancellationToken = default);

    async Task<System.Windows.Media.ImageSource> LoadTextureThumbnailAsync(
        string packagePath,
        string texturePath,
        MaterialParameterDefinition definition,
        CancellationToken cancellationToken = default) => TextureThumbnailFactory.Create(
            await LoadTextureAsync(packagePath, texturePath, definition, cancellationToken), 128);
}

public sealed class PackageReferenceService(
    MorphFacePackageReader reader,
    TextureCatalogService textureCatalogService) : ITextureReferenceLoader
{
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly TextureCatalogService _textureCatalogService =
        textureCatalogService ?? throw new ArgumentNullException(nameof(textureCatalogService));
    private static readonly MaterialParameterDefinition ThumbnailDefinition = new(
        "Preview",
        "Preview",
        "Preview",
        MaterialParameterKind.Texture,
        HeadMaterialFamily.Unknown,
        TextureRole: TextureRole.Diffuse,
        ColorSpace: TextureColorSpace.Srgb,
        AlphaPolicy: TextureAlphaPolicy.Ignore);

    public void InvalidatePackage(string packagePath)
    {
        _readerGate.Wait();
        try
        {
            reader.InvalidatePackage(packagePath);
        }
        finally
        {
            _readerGate.Release();
        }
    }

    public Task<PackageReferenceCatalog> ReadCatalogAsync(
        string packagePath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CreateCatalog(PackageAssetInspector.Inventory(packagePath, ["Texture2D", "SkeletalMesh"]));
    }, cancellationToken);

    internal PackageReferenceCatalog CreateCatalog(PackageInventory inventory)
    {
        PackageAssetListItem[] Select(string className) => inventory.Entries
            .Where(entry => !entry.IsDefaultObject &&
                            string.Equals(entry.ClassName, className, StringComparison.OrdinalIgnoreCase) &&
                            (!className.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) ||
                             !CustomMaterialTemplateCatalogService.IsUnsafeAttachment(
                                 inventory.PackagePath, entry.InstancedPath)))
            .Select(entry => new PackageAssetListItem(new AssetIdentity(
                inventory.PackagePath,
                entry.InstancedPath,
                entry.UIndex,
                entry.ClassName)) { BoneCount = entry.BoneCount })
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var textures = Select("Texture2D");
        foreach (var texture in textures)
        {
            texture.ConfigureThumbnailLoader(cancellationToken => LoadTextureThumbnailAsync(
                inventory.PackagePath,
                texture.Identity.InstancedPath,
                cancellationToken));
        }
        return new PackageReferenceCatalog(textures, Select("SkeletalMesh"));
    }

    private Task<System.Windows.Media.ImageSource> LoadTextureThumbnailAsync(
        string packagePath,
        string texturePath,
        CancellationToken cancellationToken) => LoadTextureThumbnailAsync(
            packagePath, texturePath, ThumbnailDefinition, cancellationToken);

    public Task<System.Windows.Media.ImageSource> LoadTextureThumbnailAsync(
        string packagePath,
        string texturePath,
        MaterialParameterDefinition definition,
        CancellationToken cancellationToken = default) => ReadAsync(() => TextureThumbnailFactory.Create(
            reader.LoadTextureThumbnail(packagePath, texturePath, definition), 128), cancellationToken);

    public Task<DecodedTextureAsset> LoadTextureAsync(
        string packagePath,
        string texturePath,
        MaterialParameterDefinition definition,
        CancellationToken cancellationToken = default) => ReadAsync(
            () => reader.LoadTexture(packagePath, texturePath, definition),
            cancellationToken);

    public Task<TextureCatalogReadResult> ReadTextureCatalogAsync(
        MorphFaceGame game,
        CancellationToken cancellationToken = default) =>
        _textureCatalogService.ReadAsync(game, cancellationToken);

    public Task<LoadedAttachment> LoadAttachmentAsync(
        string packagePath,
        string facePath,
        string meshPath,
        CancellationToken cancellationToken = default) => ReadAsync(
            () => reader.LoadAttachment(packagePath, facePath, meshPath),
            cancellationToken);

    public Task<LoadedAttachment> LoadDetachedAttachmentAsync(
        string packagePath,
        string meshPath,
        CancellationToken cancellationToken = default) => ReadAsync(
            () => reader.LoadDetachedAttachment(packagePath, meshPath),
            cancellationToken);

    /// <summary>Compares only the supplied native candidates; exact authored identities are returned unchanged.</summary>
    public Task<IReadOnlyList<AssetIdentity>> FindEquivalentSkeletalMeshesAsync(
        AssetIdentity selected, IReadOnlyList<AssetIdentity> canonicalCandidates,
        CancellationToken cancellationToken = default) => ReadAsync<IReadOnlyList<AssetIdentity>>(() =>
    {
        var fingerprint = ReadFingerprint(selected);
        if (fingerprint is null) return [];
        var matches = new List<AssetIdentity>();
        foreach (var candidate in canonicalCandidates.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadFingerprint(candidate) == fingerprint) matches.Add(candidate);
        }
        return matches;

        string? ReadFingerprint(AssetIdentity identity)
        {
            try { return reader.ReadSkeletalMeshContentFingerprint(identity); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyNotFoundException)
            {
                return null; // Optional recognition never turns a missing/corrupt donor into a failed hair selection.
            }
        }
    }, cancellationToken);

    public Task<ResolvedHeadMaterial> LoadLe3CcScalpMaterialAsync(
        bool female, CancellationToken cancellationToken = default) =>
        ReadAsync(() => reader.LoadLe3CcScalpMaterial(female), cancellationToken);

    public async Task ValidateDetachedAttachmentAsync(
        string packagePath,
        string meshPath,
        CancellationToken cancellationToken = default) => await ReadAsync(() =>
    {
        reader.ValidateDetachedAttachment(packagePath, meshPath);
        return true;
    }, cancellationToken);

    public async Task<IReadOnlyDictionary<string, AssetIdentity>> ResolveInstalledSkeletalMeshesAsync(
        MorphFaceGame game,
        IEnumerable<string> requestedPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedPaths);
        var catalog = await _textureCatalogService.ReadAsync(game, cancellationToken);
        if (!catalog.IsAvailable)
            throw new InvalidOperationException(
                $"The {game} mesh/texture database is unavailable. Build it in Mesh/Texture Databases to preview installed attachments.");

        var resolved = new Dictionary<string, AssetIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in requestedPaths.Where(path => !string.IsNullOrWhiteSpace(path) &&
                     !path.Equals("None", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (AttachmentMeshCatalogResolver.Resolve(catalog.AttachmentMeshes, path) is { } identity)
                resolved.Add(path, identity);
        }
        return resolved;
    }

    private async Task<T> ReadAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        await _readerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readerGate.Release();
        }
    }
}
