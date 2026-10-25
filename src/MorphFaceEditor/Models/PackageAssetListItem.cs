using System.Windows.Media;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Models;

public sealed class PackageAssetListItem : ObservableObject
{
    private Func<CancellationToken, Task<ImageSource>>? _thumbnailLoader;
    private Task? _thumbnailTask;
    private CancellationTokenSource? _thumbnailCancellation;
    private ImageSource? _thumbnail;
    private bool _isThumbnailLoading;
    private string? _thumbnailError;

    public PackageAssetListItem(AssetIdentity identity) => Identity = identity;

    public AssetIdentity Identity { get; }
    public int? BoneCount { get; init; }
    public string DisplayName => Identity.InstancedPath;
    public string ObjectName => Identity.InstancedPath.Split('.').Last();
    public bool CanLoadThumbnail => _thumbnailLoader is not null && Thumbnail is null && ThumbnailError is null;
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

    public void ConfigureThumbnailLoader(Func<Task<ImageSource>> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _thumbnailLoader ??= _ => loader();
    }

    public void ConfigureThumbnailLoader(Func<CancellationToken, Task<ImageSource>> loader) =>
        _thumbnailLoader ??= loader ?? throw new ArgumentNullException(nameof(loader));

    public Task EnsureThumbnailAsync() => EnsureThumbnailAsync(CancellationToken.None);

    public Task EnsureThumbnailAsync(CancellationToken cancellationToken)
    {
        if (_thumbnailLoader is null || Thumbnail is not null || ThumbnailError is not null)
        {
            return Task.CompletedTask;
        }

        if (_thumbnailTask is { IsCompleted: false } existing)
        {
            return existing.WaitAsync(cancellationToken);
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _thumbnailCancellation = linkedCancellation;
        _thumbnailTask = LoadThumbnailAsync(linkedCancellation);
        return _thumbnailTask;
    }

    public void SetThumbnail(DecodedTextureAsset texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (Thumbnail is null)
        {
            Thumbnail = TextureThumbnailFactory.Create(texture);
            _thumbnailCancellation?.Cancel();
        }
    }

    private async Task LoadThumbnailAsync(CancellationTokenSource cancellation)
    {
        IsThumbnailLoading = true;
        ThumbnailError = null;
        try
        {
            var thumbnail = await _thumbnailLoader!(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Thumbnail = thumbnail;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ThumbnailError = UserFacingMessageFormatter.Format(exception);
        }
        finally
        {
            IsThumbnailLoading = false;
            if (ReferenceEquals(_thumbnailCancellation, cancellation))
            {
                _thumbnailCancellation = null;
                _thumbnailTask = null;
            }
            cancellation.Dispose();
        }
    }

    public override string ToString() => DisplayName;
}
