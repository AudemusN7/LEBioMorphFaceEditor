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
    private readonly IReadOnlyList<PackageAssetListItem> _localCandidates;
    private IReadOnlyList<TextureCatalogCandidate> _registryCandidates;
    private TextureCatalogProfile _registryProfile;
    private bool _isRegistryAvailable;
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
        _localCandidates = candidates;
        _registryCandidates = registryCandidates ?? [];
        _registryProfile = registryProfile ?? TextureCatalogProfile.Empty;
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
        ? $"{_registryCandidates.Count:N0} installed choices · {_localCandidates.Count:N0} open-package choices"
        : $"{_localCandidates.Count:N0} open-package choices · installed choices unavailable — build the active game's registry in Texture Databases.";
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
        var candidate = FindResolvableOption(instancedPath)
            ?? throw new FileNotFoundException($"Texture '{instancedPath}' is not available in the open package or installed registry.");
        var identity = candidate.Asset?.Identity ?? new AssetIdentity(
            candidate.RegistryCandidate!.EffectiveOccurrence.PackagePath,
            candidate.InstancedPath,
            candidate.RegistryCandidate.EffectiveOccurrence.ExportUIndex,
            "Texture2D");
        return ResolveReferenceAsync(identity);
    }

    public bool CanResolveInstancedPath(string instancedPath) =>
        !string.IsNullOrWhiteSpace(instancedPath) && FindResolvableOption(instancedPath) is not null;

    /// <summary>Applies a catalogue that completed after the material editor became interactive.</summary>
    public void UpdateRegistryCandidates(
        IReadOnlyList<TextureCatalogCandidate> candidates,
        TextureCatalogProfile profile,
        bool isRegistryAvailable)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(profile);
        _registryCandidates = candidates;
        _registryProfile = profile;
        _isRegistryAvailable = isRegistryAvailable;
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
        var none = _allCandidates[0];
        var activePath = _session.GetSelectedTexture(Name)?.Source.InstancedPath;
        var filtered = _allCandidates.Skip(1)
            .Where(option => MatchesSearch(option, SearchText))
            .OrderBy(option => Rank(option, activePath))
            .ThenBy(option => option.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Candidates.Clear();
        Candidates.Add(none);
        foreach (var option in filtered)
        {
            Candidates.Add(option);
        }
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
                .Select(candidate => new MaterialTextureOption(null, RegistryCandidate: candidate)));
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

    private int Rank(MaterialTextureOption option, string? activePath)
    {
        if (!string.IsNullOrWhiteSpace(activePath) &&
            option.InstancedPath.Equals(activePath, StringComparison.OrdinalIgnoreCase)) return 0;
        if (ContainsAny(option.InstancedPath, _registryProfile.PreferredPathFragments)) return 1;
        if (ContainsAny(option.InstancedPath, _registryProfile.SharedPathFragments)) return 2;
        return option.Asset is not null ? 3 : 4;
    }

    private static bool ContainsAny(string path, IReadOnlyList<string> fragments) =>
        fragments.Any(fragment => !string.IsNullOrWhiteSpace(fragment) &&
            path.Contains(fragment, StringComparison.OrdinalIgnoreCase));

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
