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
}

public sealed class PackageReferenceService(
    MorphFacePackageReader reader,
    TextureCatalogService? textureCatalogService = null) : ITextureReferenceLoader
{
    private readonly SemaphoreSlim _readerGate = new(1, 1);
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
        var inventory = PackageAssetInspector.Inventory(packagePath, ["Texture2D", "SkeletalMesh"]);
        PackageAssetListItem[] Select(string className) => inventory.Entries
            .Where(entry => !entry.IsDefaultObject && string.Equals(entry.ClassName, className, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new PackageAssetListItem(new AssetIdentity(
                inventory.PackagePath,
                entry.InstancedPath,
                entry.UIndex,
                entry.ClassName)))
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var textures = Select("Texture2D");
        foreach (var texture in textures)
        {
            texture.ConfigureThumbnailLoader(() => LoadTextureThumbnailAsync(
                inventory.PackagePath,
                texture.Identity.InstancedPath));
        }
        return new PackageReferenceCatalog(textures, Select("SkeletalMesh"));
    }, cancellationToken);

    private Task<System.Windows.Media.ImageSource> LoadTextureThumbnailAsync(
        string packagePath,
        string texturePath) => ReadAsync(() => TextureThumbnailFactory.Create(
            reader.LoadTexture(packagePath, texturePath, ThumbnailDefinition)), CancellationToken.None);

    public Task<DecodedTextureAsset> LoadTextureAsync(
        string packagePath,
        string texturePath,
        MaterialParameterDefinition definition,
        CancellationToken cancellationToken = default) => ReadAsync(
            () => reader.LoadTexture(packagePath, texturePath, definition),
            cancellationToken);

    public Task<TextureCatalogReadResult> ReadTextureCatalogAsync(
        MorphFaceGame game,
        CancellationToken cancellationToken = default) => textureCatalogService is null
        ? Task.FromResult(new TextureCatalogReadResult(
            new TextureRegistryStatus(game, TextureRegistryState.Missing, null, null, null, null, null), []))
        : textureCatalogService.ReadAsync(game, cancellationToken);

    public Task<LoadedAttachment> LoadAttachmentAsync(
        string packagePath,
        string facePath,
        string meshPath,
        CancellationToken cancellationToken = default) => ReadAsync(
            () => reader.LoadAttachment(packagePath, facePath, meshPath),
            cancellationToken);

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
