using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;
using System.Windows.Media;

namespace MorphFaceEditor.ViewModels;

/// <summary>Manages one texture parameter's candidates, inherited default, preview and async selection.</summary>
public sealed class MaterialTextureEditorViewModel : ObservableObject
{
    private readonly MaterialEditingSession _session;
    private readonly MaterialParameterDefinition _definition;
    private readonly PackageReferenceService _references;
    private readonly string _packagePath;
    private readonly Action<string> _reportError;
    private MaterialTextureOption? _selectedTexture;
    private ImageSource? _previewThumbnail;
    private bool _isBusy;
    private bool _initializing;

    public MaterialTextureEditorViewModel(
        MaterialEditingSession session,
        MaterialParameterDefinition definition,
        PackageReferenceService references,
        string packagePath,
        IReadOnlyList<PackageAssetListItem> candidates,
        Action<string> reportError)
    {
        _session = session;
        _definition = definition;
        _references = references;
        _packagePath = packagePath;
        _reportError = reportError;
        // Preserve an already-authored external reference even when it is absent from local candidates.
        var currentTexture = session.GetSelectedTexture(Name);
        var options = candidates.Select(candidate => new MaterialTextureOption(candidate)).ToList();
        if (currentTexture is not null && !options.Any(option =>
                option.Asset?.Identity == currentTexture.Source))
        {
            var currentAsset = new PackageAssetListItem(currentTexture.Source);
            currentAsset.SetThumbnail(currentTexture);
            options.Insert(0, new MaterialTextureOption(currentAsset, currentTexture));
        }
        var defaultTextureName = session.GetDefaultTexture(Name)?.Source.InstancedPath.Split('.').Last();
        var noneLabel = defaultTextureName is null
            ? "None (material default)"
            : $"None (material default: {defaultTextureName})";
        Candidates = [new MaterialTextureOption(null, DisplayNameOverride: noneLabel), .. options];
        _initializing = true;
        var current = session.GetSelectedTexture(Name)?.Source.InstancedPath;
        _selectedTexture = Candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Asset?.Identity.InstancedPath, current, StringComparison.OrdinalIgnoreCase))
            ?? Candidates[0];
        _initializing = false;
        RefreshPreview();
    }

    public string Name => _definition.Name;
    public string Label => _definition.Label;
    public string Group => _definition.Group;
    public string CategoryKey => _definition.Group;
    public string Description => _definition.Description;
    public IReadOnlyList<MaterialTextureOption> Candidates { get; }
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }
    public MaterialTextureOption? SelectedTexture
    {
        get => _selectedTexture;
        set
        {
            if (SetProperty(ref _selectedTexture, value) && !_initializing && value is not null)
            {
                if (value.IsNone)
                {
                    _session.SetTextureReference(Name, null);
                }
                else
                {
                    _ = SelectAsync(value);
                }
            }
        }
    }
    public ImageSource? PreviewThumbnail
    {
        get => _previewThumbnail;
        private set => SetProperty(ref _previewThumbnail, value);
    }
    public string SourceName => _session.GetPreviewTexture(Name)?.Source.InstancedPath ?? "None";
    public string Details
    {
        get
        {
            var texture = _session.GetPreviewTexture(Name);
            var sourceWidth = texture?.SourceWidth > 0 ? texture.SourceWidth : texture?.Width;
            var sourceHeight = texture?.SourceHeight > 0 ? texture.SourceHeight : texture?.Height;
            var preview = texture is not null && (sourceWidth != texture.Width || sourceHeight != texture.Height)
                ? $" · {texture.Width}×{texture.Height} preview"
                : string.Empty;
            return texture is null
                ? $"{_definition.TextureRole} · {_definition.ColorSpace}"
                : $"{sourceWidth}×{sourceHeight}{preview} · {texture.SourceFormat} · {texture.ColorSpace}";
        }
    }

    public void Refresh()
    {
        var current = _session.GetSelectedTexture(Name)?.Source.InstancedPath;
        _initializing = true;
        SelectedTexture = Candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Asset?.Identity.InstancedPath, current, StringComparison.OrdinalIgnoreCase))
            ?? Candidates[0];
        _initializing = false;
        RefreshPreview();
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(Details));
    }

    public async Task<DecodedTextureAsset> ResolveReferenceAsync(AssetIdentity reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var cached = Candidates.FirstOrDefault(candidate => string.Equals(
            candidate.Asset?.Identity.InstancedPath,
            reference.InstancedPath,
            StringComparison.OrdinalIgnoreCase))?.ResolvedTexture;
        return cached ?? await _references.LoadTextureAsync(
            _packagePath,
            reference.InstancedPath,
            _definition);
    }

    public Task<DecodedTextureAsset> ResolveInstancedPathAsync(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var objectName = instancedPath.Split('.').Last();
        var candidate = Candidates.FirstOrDefault(value => value.Asset is not null &&
            (value.Asset.Identity.InstancedPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase) ||
             value.Asset.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase)));
        var identity = candidate?.Asset?.Identity ?? new AssetIdentity(
            _packagePath, instancedPath, 0, "Texture2D");
        return ResolveReferenceAsync(identity);
    }

    private void RefreshPreview()
    {
        var texture = _session.GetPreviewTexture(Name);
        PreviewThumbnail = texture is null ? null : TextureThumbnailFactory.Create(texture);
        if (SelectedTexture?.Asset is not null && texture is not null &&
            SelectedTexture.Asset.Identity == texture.Source)
        {
            SelectedTexture.Asset.SetThumbnail(texture);
        }
    }

    private async Task SelectAsync(MaterialTextureOption selected)
    {
        if (selected.ResolvedTexture is not null)
        {
            _session.SetTextureReference(Name, selected.ResolvedTexture);
            return;
        }
        var asset = selected.Asset ?? throw new ArgumentException("None does not identify a package texture.", nameof(selected));
        IsBusy = true;
        try
        {
            var texture = await _references.LoadTextureAsync(
                _packagePath,
                asset.Identity.InstancedPath,
                _definition);
            asset.SetThumbnail(texture);
            if (ReferenceEquals(selected, SelectedTexture))
            {
                _session.SetTextureReference(Name, texture);
            }
        }
        catch (Exception exception)
        {
            _reportError($"Texture reference could not be changed: {exception.Message}");
            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed record MaterialTextureOption(
    PackageAssetListItem? Asset,
    DecodedTextureAsset? ResolvedTexture = null,
    string? DisplayNameOverride = null)
{
    public bool IsNone => Asset is null && ResolvedTexture is null;
    public string DisplayName => DisplayNameOverride ?? Asset?.DisplayName ?? "None";
    public System.Windows.Media.ImageSource? Thumbnail => Asset?.Thumbnail;
    public string? ThumbnailError => Asset?.ThumbnailError;
    public Task EnsureThumbnailAsync() => Asset?.EnsureThumbnailAsync() ?? Task.CompletedTask;
    public override string ToString() => DisplayName;
}
