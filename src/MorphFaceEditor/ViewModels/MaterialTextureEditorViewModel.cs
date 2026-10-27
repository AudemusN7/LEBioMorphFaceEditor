using System.IO;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;
using MorphFaceEditor.LegendaryExplorer;
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
    private IReadOnlyList<MaterialTextureOption> _defaultCandidates = [];
    private HashSet<string> _officialDefaultKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialTextureOption> _randomisationPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialTextureOption> _exactPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialTextureOption?> _randomisationObjectNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialTextureOption> _physicalOptions = new(StringComparer.OrdinalIgnoreCase);
    // Keep just the latest preview ready for a click. Older choices are cached by the reader.
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
    private bool _showAllPhysicalSources;
    private bool _physicalSourcesExpanded;

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
        _officialDefaultKeys = BuildOfficialDefaultKeys(_registryCandidates);
        var currentTexture = session.GetSelectedTexture(Name);
        var defaultTextureName = session.GetDefaultTexture(Name)?.Source.InstancedPath.Split('.').Last();
        var noneLabel = defaultTextureName is null
            ? Loc.Text("loc_VM_texture_noneMaterialDefault")
            : Loc.Format("loc_VM_texture_noneMaterialDefaultNamed", defaultTextureName);
        _allCandidates = [new MaterialTextureOption(null, DisplayNameOverride: noneLabel)];
        _allCandidates.AddRange(BuildOptions(currentTexture));
        RebuildLookupIndices();
        _defaultCandidates = BuildDefaultOptions(_allCandidates);
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
    public bool ShowAllPhysicalSources
    {
        get => _showAllPhysicalSources;
        set
        {
            if (!SetProperty(ref _showAllPhysicalSources, value)) return;
            CancelTexturePreview();
            if (value && !_physicalSourcesExpanded)
            {
                var none = _allCandidates[0];
                _allCandidates.Clear();
                _allCandidates.Add(none);
                _allCandidates.AddRange(BuildOptions(_session.GetSelectedTexture(Name), includePhysicalSources: true));
                RebuildLookupIndices();
                _physicalSourcesExpanded = true;
            }
            _defaultCandidates = BuildDefaultOptions(_allCandidates);
            ApplySearch();
            var wasInitializing = _initializing;
            _initializing = true;
            SelectedTexture = FindOptionForCurrentTexture(_session.GetTextureReference(Name)?.InstancedPath);
            _initializing = wasInitializing;
            RefreshPreview();
        }
    }
    public bool HasExternalRegistrySelection => SelectedTexture?.RegistryCandidate is not null;
    public string RegistryStatusLabel => _isRegistryAvailable
        ? Loc.Format("loc_VM_texture_registryChoicesAvailable", _registryCandidates.Count, _localCandidates.Count)
        : Loc.Format("loc_VM_texture_registryChoicesUnavailable", _localCandidates.Count);
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
            if (!_initializing && value?.Asset is { } asset && _playerReferences?.Allows(asset.Identity) == false) return;
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
        _session.GetPreviewTexture(Name)?.Source.InstancedPath ?? Loc.Text("loc_VM_texture_none");
    public string Details
    {
        get
        {
            var texture = _session.GetPreviewTexture(Name);
            if (_session.GetTextureReference(Name) is { } authored &&
                (texture is null || !SameCanonicalTexturePath(authored, texture.Source)))
                return Loc.Text("loc_VM_texture_unresolvedReferenceDefaultShown");
            var sourceWidth = texture?.SourceWidth > 0 ? texture.SourceWidth : texture?.Width;
            var sourceHeight = texture?.SourceHeight > 0 ? texture.SourceHeight : texture?.Height;
            var preview = texture is not null && (sourceWidth != texture.Width || sourceHeight != texture.Height)
                ? Loc.Format("loc_VM_texture_previewDimensions", texture.Width, texture.Height)
                : string.Empty;
            return texture is null
                ? Loc.Format("loc_VM_texture_roleAndColourSpace", LocalizedTextureRole(_definition.TextureRole), LocalizedColourSpace(_definition.ColorSpace))
                : Loc.Format("loc_VM_texture_details", sourceWidth, sourceHeight, preview, texture.SourceFormat, LocalizedColourSpace(texture.ColorSpace));
        }
    }

    private PlayerRuntimeAssetAvailability? _playerReferences;

    public void RestrictPlayerReferences(PlayerRuntimeAssetAvailability available)
    {
        _playerReferences = available;
        var none = _allCandidates[0];
        _allCandidates.Clear();
        _allCandidates.Add(none);
        _allCandidates.AddRange(BuildOptions(_session.GetSelectedTexture(Name), _physicalSourcesExpanded));
        RebuildLookupIndices();
        _defaultCandidates = BuildDefaultOptions(_allCandidates);
        ApplySearch();
        Refresh();
    }

    public void Refresh()
    {
        var reference = _session.GetTextureReference(Name);
        var current = reference?.InstancedPath;
        _initializing = true;
        if (reference is not null && _playerReferences?.Allows(reference) != false && !_exactPaths.ContainsKey(current!))
        {
            _allCandidates.Add(new MaterialTextureOption(new MorphFaceEditor.Models.PackageAssetListItem(reference),
                DisplayNameOverride: Loc.Format("loc_VM_texture_unresolvedReference", current)));
            RebuildLookupIndices();
            _defaultCandidates = BuildDefaultOptions(_allCandidates);
            ApplySearch();
        }
        SelectedTexture = FindOptionForCurrentTexture(current);
        _initializing = false;
        RefreshPreview();
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(Details));
    }

    private static string LocalizedTextureRole(TextureRole role) => role switch
    {
        TextureRole.Diffuse => Loc.Text("loc_VM_textureRole_diffuse"),
        TextureRole.Normal => Loc.Text("loc_VM_textureRole_normal"),
        TextureRole.Mask => Loc.Text("loc_VM_textureRole_mask"),
        TextureRole.Specular => Loc.Text("loc_VM_textureRole_specular"),
        TextureRole.Detail => Loc.Text("loc_VM_textureRole_detail"),
        TextureRole.Tangent => Loc.Text("loc_VM_textureRole_tangent"),
        _ => Loc.Text("loc_VM_textureRole_other")
    };

    private static string LocalizedColourSpace(TextureColorSpace colorSpace) => colorSpace switch
    {
        TextureColorSpace.Srgb => Loc.Text("loc_VM_textureColourSpace_srgb"),
        _ => Loc.Text("loc_VM_textureColourSpace_linear")
    };

    public async Task<DecodedTextureAsset> ResolveReferenceAsync(AssetIdentity reference)
    {
        var plan = PrepareReferenceRead(reference);
        var resolved = plan.CachedTexture ?? await _references.LoadTextureAsync(
            plan.Request.PackagePath, plan.Request.TexturePath, plan.Request.Definition);
        return plan.ApplyDonorIdentity(resolved);
    }

    private TextureReferenceReadPlan PrepareReferenceRead(AssetIdentity reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        MaterialTextureOption? option;
        if (string.IsNullOrWhiteSpace(reference.PackagePath))
            option = _exactPaths.GetValueOrDefault(reference.InstancedPath);
        else
            _physicalOptions.TryGetValue(PhysicalCandidateKey(reference.InstancedPath,
                reference.PackagePath, reference.UIndex), out option);
        if (option is not null && !option.MatchesIdentity(reference)) option = null;
        var cached = option is null ? null : ResolvedTexture(option);
        return new(_references, new TextureReadRequest(
            option?.RegistryCandidate?.EffectiveOccurrence.PackagePath ?? reference.PackagePath,
            reference.InstancedPath,
            _definition), cached, reference.IsExplicitTextureDonor);
    }

    /// <summary>Automatic rolls use installed official defaults; picker and file imports retain their own choices.</summary>
    public Task<DecodedTextureAsset> ResolveRandomisationPathAsync(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var candidate = FindRandomisationOption(instancedPath)
            ?? throw Loc.Error(new FileNotFoundException(Loc.Format("loc_VM_texture_notAvailableInSources", instancedPath)));
        return ResolveReferenceAsync(candidate.Asset!.Identity);
    }

    internal TextureReferenceReadPlan PrepareRandomisationTextureRead(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var candidate = FindRandomisationOption(instancedPath)
            ?? throw Loc.Error(new FileNotFoundException(Loc.Format("loc_VM_texture_notAvailableInSources", instancedPath)));
        return PrepareReferenceRead(candidate.Asset!.Identity);
    }

    /// <summary>Resolves file imports by full path, without the picker’s unique-name fallback.</summary>
    public Task<DecodedTextureAsset> ResolveExactInstancedPathAsync(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var candidate = _exactPaths.GetValueOrDefault(instancedPath)
            ?? throw Loc.Error(new FileNotFoundException(Loc.Format("loc_VM_texture_notAvailableAtFullPath", instancedPath)));
        var identity = candidate.Asset!.Identity;
        return ResolveReferenceAsync(identity);
    }

    public bool CanResolveRandomisationPath(string instancedPath) =>
        !string.IsNullOrWhiteSpace(instancedPath) && FindRandomisationOption(instancedPath) is not null;

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
        _officialDefaultKeys = BuildOfficialDefaultKeys(candidates);
        _isRegistryAvailable = isRegistryAvailable;
        CancelTexturePreview();
        CancelPendingSelection();
        var currentTexture = _session.GetSelectedTexture(Name);
        var defaultTextureName = _session.GetDefaultTexture(Name)?.Source.InstancedPath.Split('.').Last();
        var noneLabel = defaultTextureName is null
            ? Loc.Text("loc_VM_texture_noneMaterialDefault")
            : Loc.Format("loc_VM_texture_noneMaterialDefaultNamed", defaultTextureName);
        _allCandidates.Clear();
        _allCandidates.Add(new MaterialTextureOption(null, DisplayNameOverride: noneLabel));
        _physicalSourcesExpanded = ShowAllPhysicalSources;
        _allCandidates.AddRange(BuildOptions(currentTexture, _physicalSourcesExpanded));
        RebuildLookupIndices();
        _defaultCandidates = BuildDefaultOptions(_allCandidates);
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
                    _session.SetTextureReference(Name, resolved with
                    {
                        Source = resolved.Source with { IsExplicitTextureDonor = true }
                    });
                return;
            }
            var sourcePackage = selected.RegistryCandidate?.EffectiveOccurrence.PackagePath ?? selected.Asset?.Identity.PackagePath
                ?? throw Loc.Error(new ArgumentException(Loc.Text("loc_VM_texture_noneNotPackageTexture"), nameof(selected)));
            var instancedPath = selected.InstancedPath;
            var texture = await _references.LoadTextureAsync(
                sourcePackage,
                instancedPath,
                _definition,
                cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || generation != _selectionGeneration ||
                !ReferenceEquals(selected, SelectedTexture)) return;
            CacheResolvedTexture(selected, texture);
            selected.Asset?.SetThumbnail(texture);
            if (generation == _selectionGeneration && ReferenceEquals(selected, SelectedTexture))
            {
                _session.SetTextureReference(Name, texture with
                {
                    Source = texture.Source with { IsExplicitTextureDonor = true }
                });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == _selectionGeneration)
            {
                _reportError(Loc.Format("loc_VM_texture_referenceChangeFailed", UserFacingMessageFormatter.Format(exception)));
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
                    ?? throw Loc.Error(new ArgumentException(Loc.Text("loc_VM_texture_noneNotPackageTexture"), nameof(selected)));
                texture = await _references.LoadTextureAsync(
                    sourcePackage,
                    selected.InstancedPath,
                    _definition,
                    cancellation.Token);
            }

            if (_disposed || cancellation.IsCancellationRequested || generation != _previewGeneration)
            {
                return;
            }

            if (texture is not null)
            {
                CacheResolvedTexture(selected, texture);
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
                _reportError(Loc.Format("loc_VM_texture_previewLoadFailed", UserFacingMessageFormatter.Format(exception)));
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
        _resolvedCandidateTextures.Clear();
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
        var source = ShowAllPhysicalSources ? _allCandidates : _defaultCandidates;
        var none = source[0];
        var filtered = source.Skip(1)
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
            _resolvedCandidateTextures.Clear();
            _resolvedCandidateTextures[CandidateCacheKey(option)] = texture;
        }
    }

    private static string CandidateCacheKey(MaterialTextureOption option)
    {
        var packagePath = option.RegistryCandidate?.EffectiveOccurrence.PackagePath ??
                          option.Asset?.Identity.PackagePath ?? string.Empty;
        return $"{packagePath}|{option.InstancedPath}";
    }

    private IReadOnlyList<MaterialTextureOption> BuildOptions(
        DecodedTextureAsset? currentTexture, bool includePhysicalSources = false)
    {
        var options = (_playerReferences is { } available
                ? PlayerWorkspaceReferencePolicy.SelectPackageAssets(_localCandidates, available)
                : _localCandidates)
            .Where(asset => !TextureRegistryDiscovery.IsExcludedPath(
                TextureCatalogPicker.CanonicalPath(
                    asset.Identity.InstancedPath, asset.Identity.PackagePath)))
            .DistinctBy(asset => PhysicalCandidateKey(asset.Identity.InstancedPath,
                asset.Identity.PackagePath, asset.Identity.UIndex), StringComparer.OrdinalIgnoreCase)
            .Select(asset =>
            {
                var resolved = currentTexture is not null && asset.Identity == currentTexture.Source
                    ? currentTexture
                    : null;
                if (resolved is not null) asset.SetThumbnail(resolved);
                return new MaterialTextureOption(asset, resolved);
            })
            .ToList();
        var localDonors = options.Where(option => option.Asset is not null)
            .Select(option => PhysicalCandidateKey(option.Asset!.Identity.InstancedPath,
                option.Asset.Identity.PackagePath, option.Asset.Identity.UIndex))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_isRegistryAvailable)
        {
            options.AddRange(TextureCatalogPicker.Select(_registryCandidates)
                .SelectMany(candidate => PickerOccurrences(candidate, includePhysicalSources)
                    .Select(occurrence => candidate with { EffectiveOccurrence = occurrence }))
                .DistinctBy(candidate => PhysicalCandidateKey(candidate.InstancedPath,
                        candidate.EffectiveOccurrence.PackagePath, candidate.EffectiveOccurrence.ExportUIndex),
                    StringComparer.OrdinalIgnoreCase)
                .Where(candidate => !localDonors.Contains(PhysicalCandidateKey(candidate.InstancedPath,
                    candidate.EffectiveOccurrence.PackagePath, candidate.EffectiveOccurrence.ExportUIndex)))
                .Select(candidate =>
                {
                    var occurrence = candidate.EffectiveOccurrence;
                    var asset = new PackageAssetListItem(new AssetIdentity(
                        occurrence.PackagePath,
                        candidate.InstancedPath,
                        occurrence.ExportUIndex,
                        "Texture2D"));
                    asset.ConfigureThumbnailLoader(cancellationToken => _references.LoadTextureThumbnailAsync(
                        occurrence.PackagePath,
                        candidate.InstancedPath,
                        _definition,
                        cancellationToken));
                    return new MaterialTextureOption(asset, RegistryCandidate: candidate);
                }));
        }
        if (currentTexture is not null && !options.Any(option => option.MatchesIdentity(currentTexture.Source)))
        {
            var currentAsset = new PackageAssetListItem(currentTexture.Source);
            currentAsset.SetThumbnail(currentTexture);
            options.Add(new MaterialTextureOption(currentAsset, currentTexture));
        }
        return options.Where(option => option.Asset is null || _playerReferences?.Allows(option.Asset.Identity) != false).ToArray();
    }

    private static IEnumerable<TextureCatalogOccurrence> PickerOccurrences(
        TextureCatalogCandidate candidate, bool includePhysicalSources)
    {
        if (includePhysicalSources)
            return candidate.Occurrences.Append(candidate.EffectiveOccurrence).Distinct();
        if (candidate.IsEligibleOfficialDefault && candidate.EffectiveOccurrence.Origin is
                TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
            return [candidate.EffectiveOccurrence];
        // Keep unique custom paths selectable without constructing a row for
        // every installed copy. Physical alternatives are expanded on request.
        var alternatives = candidate.Occurrences.Append(candidate.EffectiveOccurrence);
        var selected = alternatives.Where(value => value.Origin is TextureCatalogOrigin.Manual or TextureCatalogOrigin.Mod)
            .OrderBy(value => value.Origin == TextureCatalogOrigin.Manual ? 0 : 1)
            .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ExportUIndex).FirstOrDefault();
        return [selected ?? candidate.EffectiveOccurrence];
    }

    private IReadOnlyList<MaterialTextureOption> BuildDefaultOptions(
        IReadOnlyList<MaterialTextureOption> physicalOptions)
    {
        var current = _session.GetTextureReference(Name);
        var selected = physicalOptions.Skip(1)
            .GroupBy(CanonicalOptionPath, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var values = group.ToArray();
                var explicitlySelected = current is { IsExplicitTextureDonor: true }
                    ? values.FirstOrDefault(option => option.MatchesIdentity(current))
                    : null;
                if (explicitlySelected is not null) return explicitlySelected;

                var official = values.FirstOrDefault(option => option.RegistryCandidate is { } candidate &&
                    _officialDefaultKeys.Contains(PhysicalCandidateKey(candidate.InstancedPath,
                        candidate.EffectiveOccurrence.PackagePath, candidate.EffectiveOccurrence.ExportUIndex)));
                if (official is not null) return official;

                return values
                    .Where(option => option.RegistryCandidate is null ||
                        option.RegistryCandidate.EffectiveOccurrence.Origin == TextureCatalogOrigin.Manual)
                    .OrderBy(option => option.RegistryCandidate?.EffectiveOccurrence.Origin == TextureCatalogOrigin.Manual ? 1 : 0)
                    .ThenBy(option => option.Asset?.Identity.PackagePath ??
                                      option.RegistryCandidate?.EffectiveOccurrence.PackagePath,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(option => option.Asset?.Identity.UIndex ??
                                      option.RegistryCandidate?.EffectiveOccurrence.ExportUIndex ?? 0)
                    .FirstOrDefault()
                    ?? values
                        .Where(option => option.RegistryCandidate?.EffectiveOccurrence.Origin == TextureCatalogOrigin.Mod)
                        .OrderBy(option => option.RegistryCandidate!.EffectiveOccurrence.PackagePath,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(option => option.RegistryCandidate!.EffectiveOccurrence.ExportUIndex)
                        .FirstOrDefault();
            })
            .Where(option => option is not null)
            .Cast<MaterialTextureOption>()
            .OrderBy(CanonicalOptionPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return [physicalOptions[0], .. selected];
    }

    private static string CanonicalOptionPath(MaterialTextureOption option)
    {
        var packagePath = option.RegistryCandidate?.EffectiveOccurrence.PackagePath ??
                          option.Asset?.Identity.PackagePath ?? string.Empty;
        return TextureCatalogPicker.CanonicalPath(option.InstancedPath, packagePath);
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

    private MaterialTextureOption? FindRandomisationOption(string instancedPath)
    {
        MaterialTextureOption? candidate;
        if (_randomisationPaths.TryGetValue(instancedPath, out var exact)) candidate = exact;
        else
        {
            var reviewedPath = ReviewedTexturePathPolicy.Normalize(instancedPath);
            if (!reviewedPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase) &&
                _randomisationPaths.TryGetValue(reviewedPath, out var reviewed)) candidate = reviewed;
            else candidate = _randomisationObjectNames.GetValueOrDefault(instancedPath.Split('.').Last());
        }
        return candidate is not null && File.Exists(candidate.RegistryCandidate!.EffectiveOccurrence.PackagePath)
            ? candidate : null;
    }

    // Corpus availability is queried thousands of times per material roll. Index
    // the donor choices once when the catalogue changes; misses must be cheap too.
    private void RebuildLookupIndices()
    {
        _randomisationPaths.Clear();
        _exactPaths.Clear();
        _randomisationObjectNames.Clear();
        _physicalOptions.Clear();
        foreach (var group in _allCandidates.Skip(1)
                     .GroupBy(option => option.InstancedPath, StringComparer.OrdinalIgnoreCase))
        {
            if (OfficialDefault(group) is not { } selected) continue;
            _randomisationPaths[group.Key] = selected;
            if (!_randomisationObjectNames.TryAdd(selected.ObjectName, selected))
                _randomisationObjectNames[selected.ObjectName] = null; // Same name under different official paths is ambiguous.
        }
        var exactAliases = new Dictionary<string, List<MaterialTextureOption>>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in _allCandidates.Skip(1))
        {
            if (option.Asset is not { } asset) continue;
            // An unresolved material-file reference remains a visible authored
            // path, but does not identify a physical donor to index.
            if (!string.IsNullOrWhiteSpace(asset.Identity.PackagePath))
            {
                _physicalOptions.TryAdd(PhysicalCandidateKey(option.InstancedPath,
                    asset.Identity.PackagePath, asset.Identity.UIndex), option);
                // Zero UIndex is allowed for an exact source address from a material file.
                _physicalOptions.TryAdd(PhysicalCandidateKey(option.InstancedPath,
                    asset.Identity.PackagePath, 0), option);
            }
            foreach (var alias in new[] { option.InstancedPath, MeshMaterialFileService.QualifyPath(asset.Identity) }
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!exactAliases.TryGetValue(alias, out var values))
                    exactAliases[alias] = values = [];
                values.Add(option);
            }
        }
        foreach (var (alias, options) in exactAliases)
            if (PreferOfficialOccurrence(options) is { } selected) _exactPaths[alias] = selected;
    }

    private MaterialTextureOption? PreferOfficialOccurrence(IEnumerable<MaterialTextureOption> candidates)
    {
        var matches = candidates.ToArray();
        if (OfficialDefault(matches) is { } official) return official;

        return matches.FirstOrDefault(option => option.RegistryCandidate is null) ??
               matches.Where(option => option.RegistryCandidate?.EffectiveOccurrence.Origin == TextureCatalogOrigin.Manual)
                   .OrderBy(option => option.RegistryCandidate!.EffectiveOccurrence.PackagePath,
                       StringComparer.OrdinalIgnoreCase)
                   .FirstOrDefault() ??
               matches.Where(option => option.RegistryCandidate?.EffectiveOccurrence.Origin == TextureCatalogOrigin.Mod)
                   .OrderBy(option => option.RegistryCandidate!.EffectiveOccurrence.PackagePath,
                       StringComparer.OrdinalIgnoreCase)
                   .ThenBy(option => option.RegistryCandidate!.EffectiveOccurrence.ExportUIndex)
                   .FirstOrDefault();
    }

    private MaterialTextureOption? OfficialDefault(IEnumerable<MaterialTextureOption> candidates) => candidates
            .Where(option => option.RegistryCandidate is { } candidate &&
                _officialDefaultKeys.Contains(PhysicalCandidateKey(candidate.InstancedPath,
                    candidate.EffectiveOccurrence.PackagePath, candidate.EffectiveOccurrence.ExportUIndex)))
            .OrderByDescending(option => option.RegistryCandidate!.EffectiveOccurrence.MountPriority)
            .ThenBy(option => option.RegistryCandidate!.EffectiveOccurrence.PackagePath,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static string PhysicalCandidateKey(string instancedPath, string packagePath, int exportUIndex) =>
        $"{TextureCatalogPicker.CanonicalPath(instancedPath, packagePath)}|" +
        $"{(string.IsNullOrWhiteSpace(packagePath) ? string.Empty : Path.GetFullPath(packagePath))}|{exportUIndex}";

    private static HashSet<string> BuildOfficialDefaultKeys(
        IReadOnlyList<TextureCatalogCandidate> candidates) => TextureCatalogPicker.Select(candidates)
        .Where(candidate => candidate.IsEligibleOfficialDefault &&
            candidate.EffectiveOccurrence.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
        .Select(candidate => PhysicalCandidateKey(candidate.InstancedPath,
            candidate.EffectiveOccurrence.PackagePath, candidate.EffectiveOccurrence.ExportUIndex))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool SameCanonicalTexturePath(AssetIdentity authored, AssetIdentity live) =>
        authored.ClassName.Equals(live.ClassName, StringComparison.OrdinalIgnoreCase) &&
        TextureCatalogPicker.CanonicalPath(authored.InstancedPath, authored.PackagePath).Equals(
            TextureCatalogPicker.CanonicalPath(live.InstancedPath, live.PackagePath),
            StringComparison.OrdinalIgnoreCase);

    private MaterialTextureOption FindOptionForCurrentTexture(string? instancedPath)
    {
        if (instancedPath is null)
        {
            return _allCandidates[0];
        }
        var current = _session.GetTextureReference(Name);
        var visible = ShowAllPhysicalSources ? _allCandidates : _defaultCandidates;
        return current is not null
            ? visible.FirstOrDefault(candidate => candidate.MatchesIdentity(current))
                ?? visible.FirstOrDefault(candidate => SameCanonicalTexturePath(
                    current,
                    new AssetIdentity(candidate.Asset?.Identity.PackagePath ??
                                      candidate.RegistryCandidate?.EffectiveOccurrence.PackagePath ?? string.Empty,
                        candidate.InstancedPath,
                        candidate.Asset?.Identity.UIndex ?? candidate.RegistryCandidate?.EffectiveOccurrence.ExportUIndex ?? 0,
                        "Texture2D")))
                ?? visible[0]
            : visible[0];
    }
}

internal sealed record TextureReferenceReadPlan(
    ITextureReferenceLoader Loader, TextureReadRequest Request,
    DecodedTextureAsset? CachedTexture, bool IsExplicitTextureDonor)
{
    internal DecodedTextureAsset ApplyDonorIdentity(DecodedTextureAsset texture) => IsExplicitTextureDonor
        ? texture with { Source = texture.Source with { IsExplicitTextureDonor = true } }
        : texture;
}

public sealed record MaterialTextureOption(
    PackageAssetListItem? Asset,
    DecodedTextureAsset? ResolvedTexture = null,
    string? DisplayNameOverride = null,
    TextureCatalogCandidate? RegistryCandidate = null)
{
    public bool IsNone => Asset is null && ResolvedTexture is null && RegistryCandidate is null;
    public string DisplayName => DisplayNameOverride ?? RegistryCandidate?.InstancedPath ?? Asset?.DisplayName ?? Loc.Text("loc_VM_texture_none");
    public string InstancedPath => RegistryCandidate?.InstancedPath ?? Asset?.Identity.InstancedPath ?? string.Empty;
    public string ObjectName => RegistryCandidate?.ObjectName ?? Asset?.ObjectName ?? string.Empty;
    public string SourceDescription => RegistryCandidate is { } candidate
        ? Loc.Format("loc_VM_texture_candidateSourceDetails", candidate.DisplayOrigin,
            candidate.EffectiveOccurrence.PackageName, candidate.EffectiveOccurrence.Width,
            candidate.EffectiveOccurrence.Height, candidate.EffectiveOccurrence.PixelFormat)
        : Asset is null ? string.Empty : Loc.Format("loc_VM_texture_openPackageSource", Asset.ObjectName);
    public bool MatchesIdentity(AssetIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (Asset?.Identity is { } assetIdentity &&
            assetIdentity.PackagePath.Equals(identity.PackagePath, StringComparison.OrdinalIgnoreCase) &&
            assetIdentity.InstancedPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase) &&
            assetIdentity.UIndex == identity.UIndex && assetIdentity.ClassName == identity.ClassName)
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
