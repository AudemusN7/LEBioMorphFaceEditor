using System.Windows.Input;
using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

/// <summary>
/// Projects effective material state into grouped scalar, vector, texture and attachment controls.
/// All mutation and undo ownership stays in <see cref="MaterialEditingSession"/>.
/// </summary>
public sealed class MaterialEditorViewModel : ObservableObject, IDisposable
{
    private readonly MaterialEditingSession _session;
    private readonly IHdrColorDialogService _colorDialog;
    private readonly ITextureReferenceLoader _references;
    private readonly string _packagePath;
    private readonly IReadOnlyList<MorphFaceEditor.Models.PackageAssetListItem> _textureCandidates;
    private readonly Action<string> _reportError;
    private readonly IHeadEditorUiProfile _uiProfile;
    private IReadOnlyList<TextureCatalogCandidate>? _registryCandidates;
    private TextureCatalogProfile? _registryProfile;
    private bool _isRegistryAvailable;
    private IReadOnlyList<MaterialScalarEditorViewModel> _scalars = [];
    private IReadOnlyList<MaterialVectorEditorViewModel> _vectors = [];
    private IReadOnlyList<MaterialTextureEditorViewModel> _textures = [];
    private bool _disposed;

    public MaterialEditorViewModel(
        MaterialEditingSession session,
        IHdrColorDialogService colorDialog,
        ITextureReferenceLoader references,
        string packagePath,
        IReadOnlyList<MorphFaceEditor.Models.PackageAssetListItem> textureCandidates,
        Action<string> reportError,
        IHeadEditorUiProfile uiProfile,
        IReadOnlyList<TextureCatalogCandidate>? registryCandidates = null,
        TextureCatalogProfile? registryProfile = null,
        bool isRegistryAvailable = false)
    {
        _session = session;
        _colorDialog = colorDialog;
        _references = references;
        _packagePath = packagePath;
        _textureCandidates = textureCandidates;
        _reportError = reportError;
        _uiProfile = uiProfile;
        _registryCandidates = registryCandidates;
        _registryProfile = registryProfile;
        _isRegistryAvailable = isRegistryAvailable;
        RebuildControls();
        session.MaterialsChanged += OnMaterialsChanged;
        session.HistoryChanged += OnHistoryChanged;
    }

    public event EventHandler? PreviewChanged;
    public event EventHandler? ControlsChanged;

    public IReadOnlyList<MaterialScalarEditorViewModel> Scalars => _scalars;
    public IReadOnlyList<MaterialVectorEditorViewModel> Vectors => _vectors;
    public IReadOnlyList<MaterialTextureEditorViewModel> Textures => _textures;
    public MorphFaceMaterialData CaptureInterchangeData() => _session.CaptureInterchangeData();
    public void MergeMaterialData(MorphFaceMaterialData data, IReadOnlyDictionary<string, DecodedTextureAsset?> textures) =>
        _session.MergeMaterialData(data, textures);

    /// <summary>
    /// Resolves the texture references in a partial material import and merges
    /// the values into the current edit as one logical change. A texture that
    /// cannot be decoded is still retained as an authored reference by the
    /// editing session; callers receive the diagnostic so the rest of the
    /// material import can continue.
    /// </summary>
    public async Task<IReadOnlyList<string>> MergeMaterialDataAsync(MorphFaceMaterialData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var decoded = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var desired in data.Textures)
        {
            if (desired.TextureReference is not { } reference)
            {
                continue;
            }

            var controlName = _session.ResolveControlName(desired.Name, MaterialParameterKind.Texture);
            var editor = Textures.FirstOrDefault(value => value.Name.Equals(
                controlName, StringComparison.OrdinalIgnoreCase));
            if (editor is null)
            {
                continue;
            }

            try
            {
                decoded[controlName] = string.IsNullOrWhiteSpace(reference.PackagePath)
                    ? await editor.ResolveExactInstancedPathAsync(reference.InstancedPath)
                    : await editor.ResolveReferenceAsync(reference);
            }
            catch (Exception exception)
            {
                warnings.Add($"Texture '{desired.Name}' could not be resolved: {exception.Message}");
            }
        }

        _session.MergeMaterialData(data, decoded);
        return warnings;
    }

    private void RebuildControls()
    {
        foreach (var texture in _textures) texture.Dispose();
        _scalars = _session.ScalarNames
            .Where(name => IsMaterialVisible(name, MaterialParameterKind.Scalar))
            .Select(name => new MaterialScalarEditorViewModel(
                _session,
                DescribeControl(name, MaterialParameterKind.Scalar)))
            .OrderBy(value => value.Group)
            .ThenBy(value => value.Label)
            .ToArray();
        _vectors = _session.VectorNames
            .Where(name => IsMaterialVisible(name, MaterialParameterKind.Vector))
            .Select(name => new MaterialVectorEditorViewModel(
                _session,
                DescribeControl(name, MaterialParameterKind.Vector),
                _colorDialog))
            .OrderBy(value => value.Group)
            .ThenBy(value => value.Label)
            .ToArray();
        _textures = _session.TextureParameters
            .Where(value => IsMaterialVisible(value.Name, MaterialParameterKind.Texture))
            .Select(value => new MaterialTextureEditorViewModel(
                _session,
                DescribeControl(value.Name, MaterialParameterKind.Texture),
                _references,
                _packagePath,
                _textureCandidates,
                _reportError,
                _registryCandidates,
                _registryProfile,
                _isRegistryAvailable))
            .OrderBy(value => value.Group)
            .ThenBy(value => value.Label)
            .ToArray();
        OnPropertyChanged(nameof(Scalars));
        OnPropertyChanged(nameof(Vectors));
        OnPropertyChanged(nameof(Textures));
    }

    private bool IsMaterialVisible(string controlName, MaterialParameterKind kind)
    {
        var parameterName = _session.GetSourceParameterName(controlName);
        return !(kind == MaterialParameterKind.Texture &&
                 HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(parameterName)) &&
               !IsEngineEditorOnlyParameter(parameterName) &&
               _uiProfile.IsMaterialVisible(parameterName, kind);
    }

    internal static bool IsEngineEditorOnlyParameter(string parameterName)
    {
        var normalized = parameterName
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.Equals("SelectionColor", StringComparison.OrdinalIgnoreCase);
    }

    private MaterialParameterDefinition DescribeControl(string controlName, MaterialParameterKind kind)
    {
        var parameterName = _session.GetSourceParameterName(controlName);
        var definition = HumanMaterialProfiles.Describe(parameterName, kind) with { Name = controlName };
        var described = _uiProfile.DescribeMaterial(definition);
        var scopeLabel = _session.GetParameterScopeLabel(controlName);
        return described with
        {
            Name = controlName,
            Label = scopeLabel is null ? described.Label : $"{scopeLabel} - {described.Label}"
        };
    }
    public string SourceParameterName(string controlName) => _session.GetSourceParameterName(controlName);
    public string? ParameterScopeKey(string controlName) => _session.GetParameterScopeKey(controlName);
    public string? FindControlName(string? scopeKey, string parameterName, MaterialParameterKind kind)
    {
        IEnumerable<string> controls = kind switch
        {
            MaterialParameterKind.Scalar => Scalars.Select(value => value.Name),
            MaterialParameterKind.Vector => Vectors.Select(value => value.Name),
            MaterialParameterKind.Texture => Textures.Select(value => value.Name),
            _ => []
        };
        return controls.FirstOrDefault(control =>
            string.Equals(SourceParameterName(control), parameterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ParameterScopeKey(control), scopeKey, StringComparison.OrdinalIgnoreCase));
    }
    public bool CanResolveTexturePath(string parameterName, string instancedPath) =>
        Textures.Where(texture => SourceParameterName(texture.Name).Equals(
                parameterName, StringComparison.OrdinalIgnoreCase))
            .Any(texture => texture.CanResolveInstancedPath(instancedPath));
    public void UpdateRegistryCandidates(
        IReadOnlyList<TextureCatalogCandidate> candidates,
        TextureCatalogProfile profile,
        bool isRegistryAvailable)
    {
        _registryCandidates = candidates;
        _registryProfile = profile;
        _isRegistryAvailable = isRegistryAvailable;
        foreach (var texture in Textures)
        {
            texture.UpdateRegistryCandidates(candidates, profile, isRegistryAvailable);
        }
    }
    public ResolvedHeadMaterialSet Materials => _session.Materials;
    public MorphFaceMaterialOverrides CreateOverrides() => _session.CreateOverrides();
    public void SetNumericValues(
        IReadOnlyDictionary<string, float> scalars,
        IReadOnlyDictionary<string, System.Numerics.Vector4> vectors)
    {
        ArgumentNullException.ThrowIfNull(scalars);
        ArgumentNullException.ThrowIfNull(vectors);
        foreach (var (name, value) in scalars)
        {
            _session.SetScalar(name, value);
        }
        foreach (var (name, value) in vectors)
        {
            _session.SetVector(name, value);
        }
    }
    public async Task<PreparedMaterialRandomisation> PrepareRandomisationAsync(
        IReadOnlyDictionary<string, float> scalars,
        IReadOnlyDictionary<string, System.Numerics.Vector4> vectors,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> textureFamilies,
        string? parameterScopeKey = null)
    {
        ArgumentNullException.ThrowIfNull(scalars);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(textureFamilies);
        var scalarNames = Scalars.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vectorNames = Vectors.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applicableScalars = scalars.Where(value => scalarNames.Contains(value.Key))
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        var applicableVectors = vectors.Where(value => vectorNames.Contains(value.Key))
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        var decoded = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        var failedTextureFamilySignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appliedFamilies = 0;
        foreach (var family in textureFamilies.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
        {
            var resolved = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var member in family.Value)
                {
                    var required = IsRequiredRandomisationTextureMember(family.Key, member.Key);
                    var editor = MaterialParameterControlKey.TryParse(member.Key, out _, out _)
                        ? Textures.FirstOrDefault(value =>
                            value.Name.Equals(member.Key, StringComparison.OrdinalIgnoreCase))
                        : Textures.FirstOrDefault(value =>
                            SourceParameterName(value.Name).Equals(member.Key, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(ParameterScopeKey(value.Name), parameterScopeKey,
                                StringComparison.OrdinalIgnoreCase));
                    if (editor is null)
                    {
                        if (required)
                        {
                            throw new InvalidDataException(
                                $"Texture family '{family.Key}' requires unavailable parameter '{member.Key}'.");
                        }
                        AppLog.Information(
                            $"Ignored unsupported optional member '{member.Key}' in randomisation texture family '{family.Key}'.");
                        continue;
                    }
                    try
                    {
                        resolved[editor.Name] = await editor.ResolveInstancedPathAsync(member.Value);
                    }
                    catch (Exception exception) when (!required)
                    {
                        AppLog.Information(
                            $"Ignored unresolved optional member '{member.Key}' in randomisation texture family " +
                            $"'{family.Key}': {exception.Message}");
                    }
                }
            }
            catch (Exception exception)
            {
                AppLog.Warning($"Skipped unresolved randomisation texture family '{family.Key}': {exception.Message}");
                failedTextureFamilySignatures.Add(MaterialRandomiser.TextureFamilySignature(family.Value));
                continue;
            }
            foreach (var value in resolved) decoded[value.Key] = value.Value;
            appliedFamilies++;
        }
        return new PreparedMaterialRandomisation(
            applicableScalars, applicableVectors, decoded, appliedFamilies, failedTextureFamilySignatures);
    }

    private static bool IsRequiredRandomisationTextureMember(string family, string parameterName) =>
        SourceFamilyName(family).Equals("human-face", StringComparison.OrdinalIgnoreCase)
            ? MaterialParameterControlKey.ParameterName(parameterName) is "HED_Diff" or "HED_Norm"
            : SourceFamilyName(family).Equals("human-scalp", StringComparison.OrdinalIgnoreCase)
                ? MaterialParameterControlKey.ParameterName(parameterName) is "HED_Scalp_Diff" or "HED_Scalp_Norm"
                : SourceFamilyName(family).EndsWith("-face", StringComparison.OrdinalIgnoreCase)
                    ? MaterialParameterControlKey.ParameterName(parameterName).EndsWith("_HED_Diff", StringComparison.OrdinalIgnoreCase) ||
                      MaterialParameterControlKey.ParameterName(parameterName).EndsWith("_HED_Norm", StringComparison.OrdinalIgnoreCase)
                : true;
    private static string SourceFamilyName(string family) =>
        family.Contains('|') ? family[(family.IndexOf('|') + 1)..] : family;
    public void ApplyRandomisation(PreparedMaterialRandomisation values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _session.SetValues(values.Scalars, values.Vectors, values.Textures);
    }
    public void ResetToDefaults() => _session.ResetToDefaults();
    public void ReplaceAttachmentMaterials(
        ResolvedHeadMaterialSet materials,
        ResolvedHeadMaterialSet? replacementTextureMaterials = null,
        bool hasMultiMaterialAttachment = false) =>
        _session.ReplaceAttachmentMaterials(materials, replacementTextureMaterials, hasMultiMaterialAttachment);
    public AttachmentMaterialState CaptureAttachmentState() => _session.CaptureAttachmentState();
    public void RestoreAttachmentState(AttachmentMaterialState state) => _session.RestoreAttachmentState(state);
    public async Task ApplyDataAsync(MorphFaceMaterialData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var decoded = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var desired in data.Textures)
        {
            if (desired.TextureReference is not { } reference)
            {
                continue;
            }
            var controlName = _session.ResolveControlName(desired.Name, MaterialParameterKind.Texture);
            var editor = Textures.FirstOrDefault(value => value.Name.Equals(
                controlName, StringComparison.OrdinalIgnoreCase));
            if (editor is not null)
            {
                decoded[controlName] = await editor.ResolveReferenceAsync(reference);
            }
        }
        _session.ApplyMaterialData(data, decoded);
    }
    public void SetExtendedSliders(bool enabled)
    {
        foreach (var scalar in Scalars)
        {
            scalar.SetExtendedSliders(enabled);
        }
    }

    private void OnMaterialsChanged(object? sender, MaterialChangedEventArgs e)
    {
        if (e.Kind == MaterialChangeKind.Surface)
        {
            RebuildControls();
            ControlsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Kind == MaterialChangeKind.Full)
        {
            foreach (var scalar in Scalars) scalar.Refresh();
            foreach (var vector in Vectors) vector.Refresh();
            foreach (var texture in Textures) texture.Refresh();
        }
        else if (e.ParameterName is not null)
        {
            switch (e.Kind)
            {
                case MaterialChangeKind.Scalar:
                    Scalars.FirstOrDefault(value => string.Equals(value.Name, e.ParameterName, StringComparison.OrdinalIgnoreCase))?.Refresh();
                    break;
                case MaterialChangeKind.Vector:
                    Vectors.FirstOrDefault(value => string.Equals(value.Name, e.ParameterName, StringComparison.OrdinalIgnoreCase))?.Refresh();
                    break;
                case MaterialChangeKind.Texture:
                    Textures.FirstOrDefault(value => string.Equals(value.Name, e.ParameterName, StringComparison.OrdinalIgnoreCase))?.Refresh();
                    break;
            }
        }
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _session.MaterialsChanged -= OnMaterialsChanged;
        _session.HistoryChanged -= OnHistoryChanged;
        foreach (var texture in Textures) texture.Dispose();
        _disposed = true;
    }
}

public sealed record PreparedMaterialRandomisation(
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, System.Numerics.Vector4> Vectors,
    IReadOnlyDictionary<string, DecodedTextureAsset?> Textures,
    int AppliedTextureFamilies,
    IReadOnlySet<string> FailedTextureFamilySignatures);
