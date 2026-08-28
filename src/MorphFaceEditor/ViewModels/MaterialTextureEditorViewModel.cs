using System.Collections.ObjectModel;
using System.IO;
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
    private readonly IReadOnlyList<TextureCatalogCandidate> _registryCandidates;
    private readonly TextureCatalogProfile _registryProfile;
    private readonly bool _isRegistryAvailable;
    private readonly List<MaterialTextureOption> _allCandidates;
    private MaterialTextureOption? _selectedTexture;
    private ImageSource? _previewThumbnail;
    private bool _isBusy;
    private bool _initializing;
    private string _searchText = string.Empty;

    public MaterialTextureEditorViewModel(
        MaterialEditingSession session,
        MaterialParameterDefinition definition,
        PackageReferenceService references,
        string packagePath,
        IReadOnlyList<PackageAssetListItem> candidates,
        Action<string> reportError,
        IReadOnlyList<TextureCatalogCandidate>? registryCandidates = null,
        TextureCatalogProfile? registryProfile = null,
        bool isRegistryAvailable = false)
    {
        _session = session;
        _definition = definition;
        _references = references;
        _packagePath = packagePath;
        _reportError = reportError;
        _registryCandidates = registryCandidates ?? [];
        _registryProfile = registryProfile ?? TextureCatalogProfile.Empty;
        _isRegistryAvailable = isRegistryAvailable;
        // Preserve an already-authored external reference even when it is absent from local candidates.
        var currentTexture = session.GetSelectedTexture(Name);
        var options = _isRegistryAvailable
            ? TextureCatalogSearch.FilterAndRank(
                    _registryCandidates,
                    _registryProfile,
                    currentTexture?.Source.InstancedPath,
                    string.Empty)
                .Select(candidate => new MaterialTextureOption(null, RegistryCandidate: candidate))
                .ToList()
            : [];
        if (currentTexture is not null)
        {
            var currentAsset = new PackageAssetListItem(currentTexture.Source);
            currentAsset.SetThumbnail(currentTexture);
            options.Insert(0, new MaterialTextureOption(currentAsset, currentTexture));
        }
        var defaultTextureName = session.GetDefaultTexture(Name)?.Source.InstancedPath.Split('.').Last();
        var noneLabel = defaultTextureName is null
            ? "None (material default)"
            : $"None (material default: {defaultTextureName})";
        _allCandidates = [new MaterialTextureOption(null, DisplayNameOverride: noneLabel), .. options];
        Candidates = new ObservableCollection<MaterialTextureOption>(_allCandidates);
        _initializing = true;
        var current = session.GetSelectedTexture(Name)?.Source.InstancedPath;
        _selectedTexture = FindOptionForCurrentTexture(current);
        _initializing = false;
        RefreshPreview();
    }

    public string Name => _definition.Name;
    public string Label => _definition.Label;
    public string Group => _definition.Group;
    public string CategoryKey => _definition.Group;
    public string Description => _definition.Description;
    public ObservableCollection<MaterialTextureOption> Candidates { get; }
    public bool IsRegistryAvailable => _isRegistryAvailable;
    public bool HasExternalRegistrySelection => SelectedTexture?.RegistryCandidate is not null;
    public string RegistryStatusLabel => _isRegistryAvailable
        ? $"{_registryCandidates.Count:N0} installed texture choices"
        : "Texture registry unavailable — build the active game's database in Texture Databases.";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplySearch();
            }
        }
    }
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
        SelectedTexture = FindOptionForCurrentTexture(current);
        _initializing = false;
        RefreshPreview();
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(Details));
    }

    public async Task<DecodedTextureAsset> ResolveReferenceAsync(AssetIdentity reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var option = _allCandidates.FirstOrDefault(candidate => candidate.MatchesIdentity(reference));
        var cached = option?.ResolvedTexture;
        if (cached is not null) return cached;
        return await _references.LoadTextureAsync(
            option?.RegistryCandidate?.EffectiveOccurrence.PackagePath ?? reference.PackagePath,
            reference.InstancedPath,
            _definition);
    }

    public Task<DecodedTextureAsset> ResolveInstancedPathAsync(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var objectName = instancedPath.Split('.').Last();
        var candidate = _allCandidates.FirstOrDefault(value =>
            value.InstancedPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase) ||
            value.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase));
        var identity = candidate?.Asset?.Identity ?? new AssetIdentity(
            candidate?.RegistryCandidate?.EffectiveOccurrence.PackagePath ?? _packagePath,
            instancedPath, 0, "Texture2D");
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
        IsBusy = true;
        try
        {
            var sourcePackage = selected.RegistryCandidate?.EffectiveOccurrence.PackagePath ?? selected.Asset?.Identity.PackagePath
                ?? throw new ArgumentException("None does not identify a package texture.", nameof(selected));
            var instancedPath = selected.InstancedPath;
            var texture = await _references.LoadTextureAsync(
                sourcePackage,
                instancedPath,
                _definition);
            selected.Asset?.SetThumbnail(texture);
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

    private void ApplySearch()
    {
        var filtered = _isRegistryAvailable
            ? TextureCatalogSearch.FilterAndRank(
                _registryCandidates,
                _registryProfile,
                _session.GetSelectedTexture(Name)?.Source.InstancedPath,
                SearchText)
                .Select(candidate => _allCandidates.First(option => option.RegistryCandidate == candidate))
                .ToArray()
            : _allCandidates.Where(option => option.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToArray();
        var none = _allCandidates[0];
        Candidates.Clear();
        Candidates.Add(none);
        foreach (var option in filtered.Where(option => !ReferenceEquals(option, none)))
        {
            Candidates.Add(option);
        }
    }

    private MaterialTextureOption FindOptionForCurrentTexture(string? instancedPath)
    {
        if (instancedPath is null)
        {
            return _allCandidates[0];
        }
        var current = _session.GetSelectedTexture(Name)?.Source;
        return current is not null
            ? _allCandidates.FirstOrDefault(candidate => candidate.MatchesIdentity(current))
                ?? _allCandidates[0]
            : _allCandidates[0];
    }
}

public sealed record MaterialTextureOption(
    PackageAssetListItem? Asset,
    DecodedTextureAsset? ResolvedTexture = null,
    string? DisplayNameOverride = null,
    TextureCatalogCandidate? RegistryCandidate = null)
{
    public bool IsNone => Asset is null && ResolvedTexture is null && RegistryCandidate is null;
    public string DisplayName => DisplayNameOverride ?? RegistryCandidate?.InstancedPath ?? Asset?.DisplayName ?? "None";
    public string InstancedPath => RegistryCandidate?.InstancedPath ?? Asset?.Identity.InstancedPath ?? string.Empty;
    public string ObjectName => RegistryCandidate?.ObjectName ?? Asset?.ObjectName ?? string.Empty;
    public string SourceDescription => RegistryCandidate is { } candidate
        ? $"{candidate.DisplayOrigin} · {candidate.EffectiveOccurrence.PackageName} · {candidate.EffectiveOccurrence.Width}×{candidate.EffectiveOccurrence.Height} · {candidate.EffectiveOccurrence.PixelFormat}"
        : Asset is null ? string.Empty : $"Open package · {Asset.ObjectName}";
    public bool MatchesIdentity(AssetIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (Asset?.Identity == identity)
        {
            return true;
        }
        return RegistryCandidate is { } candidate &&
               candidate.InstancedPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase) &&
               Path.GetFullPath(candidate.EffectiveOccurrence.PackagePath).Equals(
                   Path.GetFullPath(identity.PackagePath), StringComparison.OrdinalIgnoreCase) &&
               candidate.EffectiveOccurrence.ExportUIndex == identity.UIndex;
    }
    public System.Windows.Media.ImageSource? Thumbnail => Asset?.Thumbnail;
    public string? ThumbnailError => Asset?.ThumbnailError;
    public Task EnsureThumbnailAsync() => Asset?.EnsureThumbnailAsync() ?? Task.CompletedTask;
    public override string ToString() => DisplayName;
}
