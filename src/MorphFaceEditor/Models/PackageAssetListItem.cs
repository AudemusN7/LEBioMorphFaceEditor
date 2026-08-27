using System.Windows.Media;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Models;

public sealed class PackageAssetListItem : ObservableObject
{
    private Func<Task<ImageSource>>? _thumbnailLoader;
    private Task? _thumbnailTask;
    private ImageSource? _thumbnail;
    private bool _isThumbnailLoading;
    private string? _thumbnailError;

    public PackageAssetListItem(AssetIdentity identity) => Identity = identity;

    public AssetIdentity Identity { get; }
    public string DisplayName => Identity.InstancedPath;
    public string ObjectName => Identity.InstancedPath.Split('.').Last();
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }
    public bool IsThumbnailLoading
    {
        get => _isThumbnailLoading;
        private set => SetProperty(ref _isThumbnailLoading, value);
    }
    public string? ThumbnailError
    {
        get => _thumbnailError;
        private set => SetProperty(ref _thumbnailError, value);
    }

    public void ConfigureThumbnailLoader(Func<Task<ImageSource>> loader) =>
        _thumbnailLoader ??= loader ?? throw new ArgumentNullException(nameof(loader));

    public Task EnsureThumbnailAsync() => _thumbnailTask ??= LoadThumbnailAsync();

    public void SetThumbnail(DecodedTextureAsset texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (Thumbnail is null)
        {
            Thumbnail = TextureThumbnailFactory.Create(texture);
        }
    }

    private async Task LoadThumbnailAsync()
    {
        if (_thumbnailLoader is null || Thumbnail is not null)
        {
            return;
        }

        IsThumbnailLoading = true;
        ThumbnailError = null;
        try
        {
            Thumbnail = await _thumbnailLoader();
        }
        catch (Exception exception)
        {
            ThumbnailError = exception.Message;
        }
        finally
        {
            IsThumbnailLoading = false;
        }
    }

    public override string ToString() => DisplayName;
}
