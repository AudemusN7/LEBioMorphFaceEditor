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
public sealed class MaterialTextureEditorViewModel : ObservableObject, IDisposable
{
    private readonly MaterialEditingSession _session;
    private readonly MaterialParameterDefinition _definition;
    private readonly ITextureReferenceLoader _references;
    private readonly string _packagePath;
    private readonly Action<string> _reportError;
    private readonly IReadOnlyList<PackageAssetListItem> _localCandidates;
    private IReadOnlyList<TextureCatalogCandidate> _registryCandidates;
    private bool _isRegistryAvailable;
    private readonly List<MaterialTextureOption> _allCandidates;
    private readonly Dictionary<string, DecodedTextureAsset> _resolvedCandidateTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private MaterialTextureOption? _selectedTexture;
    private ImageSource? _previewThumbnail;
    private bool _isBusy;
    private bool _initializing;
    private long _selectionGeneration;
    private CancellationTokenSource? _selectionCancellation;
    private long _previewGeneration;
    private CancellationTokenSource? _previewCancellation;
    private bool _disposed;
    private string _searchText = string.Empty;

    public MaterialTextureEditorViewModel(
        MaterialEditingSession session,
        MaterialParameterDefinition definition,
        ITextureReferenceLoader references,
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
        _localCandidates = candidates;
        _registryCandidates = registryCandidates ?? [];
        // Keep the profile in the constructor contract for the merged catalogue,
        // but do not let profile relevance reorder an open picker.
        _ = registryProfile;
        _isRegistryAvailable = isRegistryAvailable;
        var currentTexture = session.GetSelectedTexture(Name);
        var defaultTextureName = session.GetDefaultTexture(Name)?.Source.InstancedPath.Split('.').Last();
        var noneLabel = defaultTextureName is null
            ? "None (material default)"
            : $"None (material default: {defaultTextureName})";
        _allCandidates = [new MaterialTextureOption(null, DisplayNameOverride: noneLabel)];
        _allCandidates.AddRange(BuildOptions(currentTexture));
        Candidates = [];
        _initializing = true;
        ApplySearch();
        var current = session.GetSelectedTexture(Name)?.Source.InstancedPath;
        _selectedTexture = FindOptionForCurrentTexture(current);
        _initializing = false;
        Refresh();
    }

    public string Name => _definition.Name;
    public string Label => _definition.Label;
    public string Group => _definition.Group;
    public string CategoryKey => _definition.Group;
    public string Description => MetadataTooltipFormatter.Format(Label, _definition.Description);
    public IReadOnlyList<MaterialTextureOption> Candidates { get; private set; }
    public bool IsRegistryAvailable => _isRegistryAvailable;
    public bool HasExternalRegistrySelection => SelectedTexture?.RegistryCandidate is not null;
    public string RegistryStatusLabel => _isRegistryAvailable
        ? $"{_registryCandidates.Count:N0} installed choices · {_localCandidates.Count:N0} open-package choices"
        : $"{_localCandidates.Count:N0} open-package choices · installed choices unavailable — build the active game's registry in Texture Databases.";
    public string SearchText
    {
        get => _searchText;
        set
        {
            // An editable WPF ComboBox writes its selected item's display text back through
            // the Text binding. That is selection synchronisation, not a user search.
            if (_allCandidates.Any(candidate =>
                    string.Equals(value, candidate.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                if (_searchText.Length > 0)
                {
                    _searchText = string.Empty;
                    OnPropertyChanged();
                    ApplySearch();
                }
                return;
            }
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
            if (!_initializing && value is not null)
            {
                CancelTexturePreview();
            }
            var changed = SetProperty(ref _selectedTexture, value);
            if (!_initializing && value is not null && changed)
            {
                CancelPendingSelection();
                if (value.IsNone)
                {
                    _session.SetTextureReference(Name, null);
                }
                else
                {
                    var cancellation = new CancellationTokenSource();
                    _selectionCancellation = cancellation;
                    _ = SelectAsync(value, _selectionGeneration, cancellation);
                }
            }
        }
    }
    public ImageSource? PreviewThumbnail
    {
        get => _previewThumbnail;
        private set => SetProperty(ref _previewThumbnail, value);
    }
    public string SourceName => _session.GetTextureReference(Name)?.InstancedPath ??
        _session.GetPreviewTexture(Name)?.Source.InstancedPath ?? "None";
    public string Details
    {
        get
        {
            var texture = _session.GetPreviewTexture(Name);
            if (_session.GetTextureReference(Name) is { } authored && texture?.Source != authored)
                return "Unresolved reference retained · showing the material default";
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
        var reference = _session.GetTextureReference(Name);
        var current = reference?.InstancedPath;
        _initializing = true;
        if (reference is not null && !_allCandidates.Any(value => value.InstancedPath.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            _allCandidates.Add(new MaterialTextureOption(new MorphFaceEditor.Models.PackageAssetListItem(reference),
                DisplayNameOverride: $"{current} (unresolved reference)"));
            ApplySearch();
        }
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
        var cached = option is null ? null : ResolvedTexture(option);
        if (cached is not null) return cached;
        return await _references.LoadTextureAsync(
            option?.RegistryCandidate?.EffectiveOccurrence.PackagePath ?? reference.PackagePath,
            reference.InstancedPath,
            _definition);
    }

    public Task<DecodedTextureAsset> ResolveInstancedPathAsync(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var candidate = FindResolvableOption(instancedPath)
            ?? throw new FileNotFoundException($"Texture '{instancedPath}' is not available in the open package or installed registry.");
        var identity = candidate.Asset?.Identity ?? new AssetIdentity(
            candidate.RegistryCandidate!.EffectiveOccurrence.PackagePath,
            candidate.InstancedPath,
            candidate.RegistryCandidate.EffectiveOccurrence.ExportUIndex,
            "Texture2D");
        return ResolveReferenceAsync(identity);
    }

    /// <summary>Resolves file imports by full path, without the picker’s unique-name fallback.</summary>
    public Task<DecodedTextureAsset> ResolveExactInstancedPathAsync(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var candidate = _allCandidates.FirstOrDefault(value => value.Asset is { } asset &&
            (value.InstancedPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase) ||
             MeshMaterialFileService.QualifyPath(asset.Identity).Equals(instancedPath,
                 StringComparison.OrdinalIgnoreCase)))
            ?? throw new FileNotFoundException($"Texture '{instancedPath}' is not available at its full path.");
        var identity = candidate.Asset!.Identity;
        return ResolveReferenceAsync(identity);
    }

    public bool CanResolveInstancedPath(string instancedPath) =>
        !string.IsNullOrWhiteSpace(instancedPath) && FindResolvableOption(instancedPath) is not null;

    /// <summary>Loads a hovered candidate into the live preview without changing the committed material selection.</summary>
    public Task PreviewTextureAsync(MaterialTextureOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        CancelTexturePreview();
        var generation = _previewGeneration;
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        return PreviewAsync(option, generation, cancellation);
    }

    /// <summary>Stops a transient preview and restores the committed material state.</summary>
    public void CancelTexturePreview()
    {
        _previewGeneration++;
        var cancellation = _previewCancellation;
        _previewCancellation = null;
        cancellation?.Cancel();
        _session.ClearTexturePreview(Name);
    }

    /// <summary>Commits a candidate using the normal texture-selection path.</summary>
    public void CommitTexture(MaterialTextureOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        SelectedTexture = option;
    }

    /// <summary>Applies a catalogue that completed after the material editor became interactive.</summary>
    public void UpdateRegistryCandidates(
        IReadOnlyList<TextureCatalogCandidate> candidates,
        TextureCatalogProfile profile,
        bool isRegistryAvailable)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(profile);
        _registryCandidates = candidates;
        _isRegistryAvailable = isRegistryAvailable;
        CancelTexturePreview();
        CancelPendingSelection();
        var currentTexture = _session.GetSelectedTexture(Name);
        var defaultTextureName = _session.GetDefaultTexture(Name)?.Source.InstancedPath.Split('.').Last();
        var noneLabel = defaultTextureName is null
            ? "None (material default)"
            : $"None (material default: {defaultTextureName})";
        _allCandidates.Clear();
        _allCandidates.Add(new MaterialTextureOption(null, DisplayNameOverride: noneLabel));
        _allCandidates.AddRange(BuildOptions(currentTexture));
        _initializing = true;
        ApplySearch();
        SelectedTexture = FindOptionForCurrentTexture(currentTexture?.Source.InstancedPath);
        _initializing = false;
        OnPropertyChanged(nameof(IsRegistryAvailable));
        OnPropertyChanged(nameof(RegistryStatusLabel));
        Refresh();
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

    private async Task SelectAsync(
        MaterialTextureOption selected,
        long generation,
        CancellationTokenSource cancellation)
    {
        IsBusy = true;
        try
        {
            if (ResolvedTexture(selected) is { } resolved)
            {
                if (generation == _selectionGeneration)
                    _session.SetTextureReference(Name, resolved);
                return;
            }
            var sourcePackage = selected.RegistryCandidate?.EffectiveOccurrence.PackagePath ?? selected.Asset?.Identity.PackagePath
                ?? throw new ArgumentException("None does not identify a package texture.", nameof(selected));
            var instancedPath = selected.InstancedPath;
            var texture = await _references.LoadTextureAsync(
                sourcePackage,
                instancedPath,
                _definition,
                cancellation.Token);
            CacheResolvedTexture(selected, texture);
            selected.Asset?.SetThumbnail(texture);
            if (generation == _selectionGeneration && ReferenceEquals(selected, SelectedTexture))
            {
                _session.SetTextureReference(Name, texture);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == _selectionGeneration)
            {
                _reportError($"Texture reference could not be changed: {exception.Message}");
                Refresh();
            }
        }
        finally
        {
            cancellation.Dispose();
            if (generation == _selectionGeneration)
            {
                _selectionCancellation = null;
                IsBusy = false;
            }
        }
    }

    private async Task PreviewAsync(
        MaterialTextureOption selected,
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            DecodedTextureAsset? texture;
            if (selected.IsNone)
            {
                texture = null;
            }
            else if (ResolvedTexture(selected) is { } resolved)
            {
                texture = resolved;
            }
            else
            {
                var sourcePackage = selected.RegistryCandidate?.EffectiveOccurrence.PackagePath ?? selected.Asset?.Identity.PackagePath
                    ?? throw new ArgumentException("None does not identify a package texture.", nameof(selected));
                texture = await _references.LoadTextureAsync(
                    sourcePackage,
                    selected.InstancedPath,
                    _definition,
                    cancellation.Token);
                CacheResolvedTexture(selected, texture);
            }

            if (generation != _previewGeneration)
            {
                return;
            }

            if (texture is not null)
            {
                selected.Asset?.SetThumbnail(texture);
            }
            _session.PreviewTexture(Name, texture);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == _previewGeneration)
            {
                _reportError($"Texture preview could not be loaded: {exception.Message}");
            }
        }
        finally
        {
            cancellation.Dispose();
            if (generation == _previewGeneration)
            {
                _previewCancellation = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        CancelPendingSelection();
        CancelTexturePreview();
        _disposed = true;
    }

    private void CancelPendingSelection()
    {
        _selectionGeneration++;
        var cancellation = _selectionCancellation;
        _selectionCancellation = null;
        cancellation?.Cancel();
        IsBusy = false;
    }

    private void ApplySearch()
    {
        var none = _allCandidates[0];
        var filtered = _allCandidates.Skip(1)
            .Where(option => MatchesSearch(option, SearchText))
            .ToArray();
        Candidates = [none, .. filtered];
        OnPropertyChanged(nameof(Candidates));
    }

    private DecodedTextureAsset? ResolvedTexture(MaterialTextureOption option) =>
        option.ResolvedTexture ??
        (_resolvedCandidateTextures.TryGetValue(CandidateCacheKey(option), out var cached) ? cached : null);

    private void CacheResolvedTexture(MaterialTextureOption option, DecodedTextureAsset texture)
    {
        if (!option.IsNone)
        {
            _resolvedCandidateTextures[CandidateCacheKey(option)] = texture;
        }
    }

    private static string CandidateCacheKey(MaterialTextureOption option)
    {
        var packagePath = option.RegistryCandidate?.EffectiveOccurrence.PackagePath ??
                          option.Asset?.Identity.PackagePath ?? string.Empty;
        return $"{packagePath}|{option.InstancedPath}";
    }

    private IReadOnlyList<MaterialTextureOption> BuildOptions(DecodedTextureAsset? currentTexture)
    {
        var options = _localCandidates
            .GroupBy(asset => asset.Identity.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(asset =>
            {
                var resolved = currentTexture is not null && asset.Identity == currentTexture.Source
                    ? currentTexture
                    : null;
                if (resolved is not null) asset.SetThumbnail(resolved);
                return new MaterialTextureOption(asset, resolved);
            })
            .ToList();
        var localPaths = options.Select(option => option.InstancedPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_isRegistryAvailable)
        {
            options.AddRange(_registryCandidates
                .Where(candidate => !localPaths.Contains(candidate.InstancedPath))
                .Select(candidate =>
                {
                    var occurrence = candidate.EffectiveOccurrence;
                    var asset = new PackageAssetListItem(new AssetIdentity(
                        occurrence.PackagePath,
                        candidate.InstancedPath,
                        occurrence.ExportUIndex,
                        "Texture2D"));
                    asset.ConfigureThumbnailLoader(async () => TextureThumbnailFactory.Create(
                        await _references.LoadTextureAsync(
                            occurrence.PackagePath,
                            candidate.InstancedPath,
                            _definition)));
                    return new MaterialTextureOption(asset, RegistryCandidate: candidate);
                }));
        }
        if (currentTexture is not null && !options.Any(option => option.MatchesIdentity(currentTexture.Source)))
        {
            var currentAsset = new PackageAssetListItem(currentTexture.Source);
            currentAsset.SetThumbnail(currentTexture);
            options.Add(new MaterialTextureOption(currentAsset, currentTexture));
        }
        return options;
    }

    private static bool MatchesSearch(MaterialTextureOption option, string searchText)
    {
        var query = searchText.Trim();
        return query.Length == 0 ||
               option.ObjectName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               option.InstancedPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               option.SourceDescription.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               option.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private MaterialTextureOption? FindResolvableOption(string instancedPath)
    {
        var exact = _allCandidates.FirstOrDefault(value =>
            value.InstancedPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var objectName = instancedPath.Split('.').Last();
        var matches = _allCandidates
            .Where(value => value.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(value => value.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private MaterialTextureOption FindOptionForCurrentTexture(string? instancedPath)
    {
        if (instancedPath is null)
        {
            return _allCandidates[0];
        }
        var current = _session.GetTextureReference(Name);
        return current is not null
            ? _allCandidates.FirstOrDefault(candidate => candidate.MatchesIdentity(current))
                ?? _allCandidates.FirstOrDefault(candidate => candidate.InstancedPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase))
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
               (string.IsNullOrWhiteSpace(identity.PackagePath) || Path.GetFullPath(candidate.EffectiveOccurrence.PackagePath).Equals(
                   Path.GetFullPath(identity.PackagePath), StringComparison.OrdinalIgnoreCase)) &&
               (identity.UIndex == 0 || candidate.EffectiveOccurrence.ExportUIndex == identity.UIndex);
    }
    public System.Windows.Media.ImageSource? Thumbnail => Asset?.Thumbnail;
    public string? ThumbnailError => Asset?.ThumbnailError;
    public Task EnsureThumbnailAsync() => Asset?.EnsureThumbnailAsync() ?? Task.CompletedTask;
    public override string ToString() => DisplayName;
}
