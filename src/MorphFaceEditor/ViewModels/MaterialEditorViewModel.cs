using System.Windows.Input;
using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
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
    private bool _disposed;

    public MaterialEditorViewModel(
        MaterialEditingSession session,
        IHdrColorDialogService colorDialog,
        PackageReferenceService references,
        string packagePath,
        IReadOnlyList<MorphFaceEditor.Models.PackageAssetListItem> textureCandidates,
        Action<string> reportError,
        IHeadEditorUiProfile uiProfile,
        IReadOnlyList<TextureCatalogCandidate>? registryCandidates = null,
        TextureCatalogProfile? registryProfile = null,
        bool isRegistryAvailable = false)
    {
        _session = session;
        Scalars = session.ScalarNames
            .Select(name => new MaterialScalarEditorViewModel(
                session,
                uiProfile.DescribeMaterial(HumanMaterialProfiles.Describe(name, MaterialParameterKind.Scalar))))
            .OrderBy(value => value.Group)
            .ThenBy(value => value.Label)
            .ToArray();
        Vectors = session.VectorNames
            .Select(name => new MaterialVectorEditorViewModel(
                session,
                uiProfile.DescribeMaterial(HumanMaterialProfiles.Describe(name, MaterialParameterKind.Vector)),
                colorDialog))
            .OrderBy(value => value.Group)
            .ThenBy(value => value.Label)
            .ToArray();
        Textures = session.TextureParameters
            .Where(value => uiProfile.IsMaterialVisible(value.Name, MaterialParameterKind.Texture))
            .Select(value => new MaterialTextureEditorViewModel(
                session,
                uiProfile.DescribeMaterial(HumanMaterialProfiles.Describe(value.Name, MaterialParameterKind.Texture)),
                references,
                packagePath,
                textureCandidates,
                reportError,
                registryCandidates,
                registryProfile,
                isRegistryAvailable))
            .OrderBy(value => value.Group)
            .ThenBy(value => value.Label)
            .ToArray();
        session.MaterialsChanged += OnMaterialsChanged;
        session.HistoryChanged += OnHistoryChanged;
    }

    public event EventHandler? PreviewChanged;

    public IReadOnlyList<MaterialScalarEditorViewModel> Scalars { get; }
    public IReadOnlyList<MaterialVectorEditorViewModel> Vectors { get; }
    public IReadOnlyList<MaterialTextureEditorViewModel> Textures { get; }
    public bool HasExternalRegistrySelections => Textures.Any(texture => texture.HasExternalRegistrySelection);
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
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> textureFamilies)
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
        var appliedFamilies = 0;
        foreach (var family in textureFamilies.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
        {
            var resolved = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var member in family.Value)
                {
                    var required = IsRequiredRandomisationTextureMember(family.Key, member.Key);
                    var editor = Textures.FirstOrDefault(value =>
                        value.Name.Equals(member.Key, StringComparison.OrdinalIgnoreCase));
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
                        resolved[member.Key] = await editor.ResolveInstancedPathAsync(member.Value);
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
                continue;
            }
            foreach (var value in resolved) decoded[value.Key] = value.Value;
            appliedFamilies++;
        }
        return new PreparedMaterialRandomisation(
            applicableScalars, applicableVectors, decoded, appliedFamilies);
    }

    private static bool IsRequiredRandomisationTextureMember(string family, string parameterName) =>
        family.Equals("human-face", StringComparison.OrdinalIgnoreCase)
            ? parameterName is "HED_Diff" or "HED_Norm"
            : family.Equals("human-scalp", StringComparison.OrdinalIgnoreCase)
                ? parameterName is "HED_Scalp_Diff" or "HED_Scalp_Norm"
                : true;
    public void ApplyRandomisation(PreparedMaterialRandomisation values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _session.SetValues(values.Scalars, values.Vectors, values.Textures);
    }
    public void ReplaceAttachmentMaterials(
        ResolvedHeadMaterialSet materials,
        ResolvedHeadMaterialSet? replacementTextureMaterials = null) =>
        _session.ReplaceAttachmentMaterials(materials, replacementTextureMaterials);
    public AttachmentMaterialState CaptureAttachmentState() => _session.CaptureAttachmentState();
    public void RestoreAttachmentState(AttachmentMaterialState state) => _session.RestoreAttachmentState(state);
    public async Task ApplyDataAsync(MorphFaceMaterialData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var desiredTextures = data.Textures.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var decoded = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var editor in Textures)
        {
            if (desiredTextures.GetValueOrDefault(editor.Name)?.TextureReference is not { } reference)
            {
                continue;
            }
            decoded[editor.Name] = await editor.ResolveReferenceAsync(reference);
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
        if (e.Kind == MaterialChangeKind.Full)
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
        _disposed = true;
    }
}

public sealed record PreparedMaterialRandomisation(
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, System.Numerics.Vector4> Vectors,
    IReadOnlyDictionary<string, DecodedTextureAsset?> Textures,
    int AppliedTextureFamilies);
