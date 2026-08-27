using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Editing;

/// <summary>Tracks authored material deltas, inherited effective values and semantic undo/redo state.</summary>
public sealed class MaterialEditingSession : IUndoableEditSource
{
    private MorphFaceMaterialOverrides _originalOverrides;
    private readonly ResolvedHeadMaterialSet _baseMaterials;
    private ResolvedHeadMaterialSet _originalMaterials;
    private readonly Dictionary<string, float> _scalars;
    private readonly Dictionary<string, Vector4> _vectors;
    private readonly Dictionary<string, DecodedTextureAsset?> _textureReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _editedScalars = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _editedVectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly MaterialEditHistory _history = new();
    private bool _replaying;

    public MaterialEditingSession(
        MorphFaceMaterialOverrides overrides,
        ResolvedHeadMaterialSet materials,
        IReadOnlyCollection<string>? baseMaterialKeys = null)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        _originalOverrides = overrides;
        _originalMaterials = materials ?? throw new ArgumentNullException(nameof(materials));
        _baseMaterials = baseMaterialKeys is null
            ? materials
            : new ResolvedHeadMaterialSet(materials.Materials
                .Where(pair => baseMaterialKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        _scalars = materials.Materials.Values
            .SelectMany(material => material.Scalars.Select(value => new ScalarMaterialOverride(value.Key, value.Value)))
            .Concat(overrides.Scalars)
            .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        _vectors = materials.Materials.Values
            .SelectMany(material => material.Vectors.Select(value => new VectorMaterialOverride(value.Key, value.Value)))
            .Concat(overrides.Vectors)
            .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        TextureParameters = materials.Materials.Values
            .SelectMany(material => material.Textures.Values.Select(value => new TextureMaterialOverride(value.ParameterName, value.Texture.Source)))
            .Concat(overrides.Textures)
            .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Where(value => materials.Materials.Values.Any(material =>
                material.Supports(value.Name, MaterialParameterKind.Texture)))
            .ToArray();
        foreach (var value in overrides.Textures.Where(value => value.TextureReference is null))
        {
            _textureReferences[value.Name] = null;
        }
        // Build the same effective material set used after every edit before the
        // first preview is created. In particular, face-global overrides may
        // need to be projected across more than one resolved material (Turian
        // head/eyes are the obvious case).
        Materials = BuildMaterials();
    }

    public event EventHandler<MaterialChangedEventArgs>? MaterialsChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? EditCommitted;

    public IReadOnlyList<string> ScalarNames => _scalars.Keys
        .Where(name => _originalMaterials.Materials.Values.Any(material =>
            material.Supports(name, MaterialParameterKind.Scalar)))
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> VectorNames => _vectors.Keys
        .Where(name => _originalMaterials.Materials.Values.Any(material =>
            material.Supports(name, MaterialParameterKind.Vector)))
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<TextureMaterialOverride> TextureParameters { get; }
    public ResolvedHeadMaterialSet Materials { get; private set; }
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public int ChangedTextureCount => _textureReferences.Count;

    public float GetScalar(string name) => _scalars[name];
    public Vector4 GetVector(string name) => _vectors[name];
    public DecodedTextureAsset? GetSelectedTexture(string name) =>
        _textureReferences.TryGetValue(name, out var selected)
            ? selected
            : GetEffectiveTexture(name)?.Texture;

    public DecodedTextureAsset? GetPreviewTexture(string name) => Materials.Materials.Values
        .Select(material => material.Textures.GetValueOrDefault(name)?.Texture)
        .FirstOrDefault(texture => texture is not null);

    public DecodedTextureAsset? GetDefaultTexture(string name) => _originalMaterials.Materials.Values
        .Select(material => (material.DefaultTextures.Count == 0 ? material.Textures : material.DefaultTextures)
            .GetValueOrDefault(name)?.Texture)
        .FirstOrDefault(texture => texture is not null);

    public MaterialTextureBinding? GetEffectiveTexture(string name) => _originalMaterials.Materials.Values
        .Select(material => material.Textures.GetValueOrDefault(name))
        .FirstOrDefault(value => value is not null);

    public MorphFaceMaterialOverrides CreateOverrides()
    {
        var scalars = _originalOverrides.Scalars
            .ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _editedScalars)
        {
            scalars[name] = _scalars[name];
        }

        var vectors = _originalOverrides.Vectors
            .ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _editedVectors)
        {
            vectors[name] = _vectors[name];
        }

        var textures = _originalOverrides.Textures
            .ToDictionary(value => value.Name, value => value.TextureReference, StringComparer.OrdinalIgnoreCase);
        foreach (var value in _textureReferences)
        {
            if (value.Value is null)
            {
                textures.Remove(value.Key);
            }
            else
            {
                textures[value.Key] = value.Value.Source;
            }
        }

        return new MorphFaceMaterialOverrides(
            _originalOverrides.Source,
            scalars.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .Select(value => new ScalarMaterialOverride(value.Key, value.Value)).ToArray(),
            vectors.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .Select(value => new VectorMaterialOverride(value.Key, value.Value)).ToArray(),
            textures.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .Select(value => new TextureMaterialOverride(value.Key, value.Value)).ToArray());
    }

    public void ReplaceAttachmentMaterials(
        ResolvedHeadMaterialSet attachmentMaterials,
        ResolvedHeadMaterialSet? replacementTextureMaterials = null)
    {
        ArgumentNullException.ThrowIfNull(attachmentMaterials);
        var merged = new Dictionary<string, ResolvedHeadMaterial>(
            _baseMaterials.Materials,
            StringComparer.OrdinalIgnoreCase);
        foreach (var material in attachmentMaterials.Materials)
        {
            merged[material.Key] = material.Value;
        }
        _originalMaterials = new ResolvedHeadMaterialSet(merged);
        if (replacementTextureMaterials is not null)
        {
            RebaseAttachmentTextures(replacementTextureMaterials);
        }
        Refresh(MaterialChangeKind.Full);
    }

    public AttachmentMaterialState CaptureAttachmentState() => new(
        _originalMaterials,
        new Dictionary<string, DecodedTextureAsset?>(_textureReferences, StringComparer.OrdinalIgnoreCase),
        Materials);

    public void RestoreAttachmentState(AttachmentMaterialState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _originalMaterials = state.OriginalMaterials;
        _textureReferences.Clear();
        foreach (var value in state.TextureReferences)
        {
            _textureReferences[value.Key] = value.Value;
        }
        Materials = state.Materials;
        MaterialsChanged?.Invoke(this, new MaterialChangedEventArgs(MaterialChangeKind.Full, null));
    }

    private void RebaseAttachmentTextures(ResolvedHeadMaterialSet replacementMaterials)
    {
        var replacementHair = replacementMaterials.Materials.Values
            .Where(material => material.Family == HeadMaterialFamily.Hair)
            .ToArray();
        if (replacementHair.Length == 0)
        {
            return;
        }

        // BioMaterialOverride is face-global rather than material-scoped. Hair
        // maps therefore have to follow the selected mesh explicitly, otherwise
        // saving the face writes the previous hairstyle's maps back over it.
        var hairTextureNames = TextureParameters
            .Select(value => value.Name)
            .Where(name => HumanMaterialProfiles.Describe(
                name, MaterialParameterKind.Texture).Family == HeadMaterialFamily.Hair)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in hairTextureNames)
        {
            var replacement = replacementHair
                .Select(material => material.Textures.GetValueOrDefault(name))
                .FirstOrDefault(binding => binding is not null);
            _textureReferences[name] = replacement?.Texture;
        }
    }

    public void BeginScalarEdit(string name) => _history.Begin(
        ScalarKey(name), new MaterialNumericState(GetScalar(name), _editedScalars.Contains(name)));
    public void EndScalarEdit(string name) => EndNumericEdit(
        ScalarKey(name), new MaterialNumericState(GetScalar(name), _editedScalars.Contains(name)));
    public void BeginVectorEdit(string name, int component) => _history.Begin(
        VectorKey(name, component), new MaterialNumericState(GetVectorComponent(name, component), _editedVectors.Contains(name)));
    public void EndVectorEdit(string name, int component) => EndNumericEdit(
        VectorKey(name, component), new MaterialNumericState(GetVectorComponent(name, component), _editedVectors.Contains(name)));

    public void SetScalar(string name, float value)
    {
        ValidateFinite(value);
        var before = GetScalar(name);
        if (before == value)
        {
            return;
        }
        var wasEdited = _editedScalars.Contains(name);
        _scalars[name] = value;
        _editedScalars.Add(name);
        if (!_replaying)
        {
            CommitHistory(_history.Record(new MaterialSemanticEdit(
                ScalarKey(name),
                new MaterialNumericState(before, wasEdited),
                new MaterialNumericState(value, true))));
        }
        Refresh(MaterialChangeKind.Scalar, name);
    }

    public void SetVectorComponent(string name, int component, float value)
    {
        ValidateFinite(value);
        var before = GetVectorComponent(name, component);
        if (before == value)
        {
            return;
        }
        var vector = GetVector(name);
        vector = component switch
        {
            0 => vector with { X = value },
            1 => vector with { Y = value },
            2 => vector with { Z = value },
            3 => vector with { W = value },
            _ => throw new ArgumentOutOfRangeException(nameof(component))
        };
        var wasEdited = _editedVectors.Contains(name);
        _vectors[name] = vector;
        _editedVectors.Add(name);
        if (!_replaying)
        {
            CommitHistory(_history.Record(new MaterialSemanticEdit(
                VectorKey(name, component),
                new MaterialNumericState(before, wasEdited),
                new MaterialNumericState(value, true))));
        }
        Refresh(MaterialChangeKind.Vector, name);
    }

    public void SetVector(string name, Vector4 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Material values must be finite.");
        }
        var before = GetVector(name);
        if (before == value)
        {
            return;
        }
        var wasEdited = _editedVectors.Contains(name);
        _vectors[name] = value;
        _editedVectors.Add(name);
        if (!_replaying)
        {
            CommitHistory(_history.Record(new MaterialSemanticEdit(
                VectorValueKey(name),
                new MaterialVectorState(before, wasEdited),
                new MaterialVectorState(value, true))));
        }
        Refresh(MaterialChangeKind.Vector, name);
    }

    public void PreviewVector(string name, Vector4 value)
    {
        ValidateFinite(value.X);
        ValidateFinite(value.Y);
        ValidateFinite(value.Z);
        ValidateFinite(value.W);
        _vectors[name] = value;
        Refresh(MaterialChangeKind.Vector, name);
    }

    public void CompleteVectorPreview(string name, Vector4 before, Vector4 after, bool apply)
    {
        _vectors[name] = apply ? after : before;
        if (apply && before != after)
        {
            var wasEdited = _editedVectors.Contains(name);
            _editedVectors.Add(name);
            CommitHistory(_history.Record(new MaterialSemanticEdit(
                VectorValueKey(name),
                new MaterialVectorState(before, wasEdited),
                new MaterialVectorState(after, true))));
        }
        Refresh(MaterialChangeKind.Vector, name);
    }

    public void SetTextureReference(string name, DecodedTextureAsset? texture)
    {
        var before = GetSelectedTexture(name);
        if (before?.Source == texture?.Source && (before is null) == (texture is null))
        {
            return;
        }
        _textureReferences[name] = texture;
        if (!_replaying)
        {
            CommitHistory(_history.Record(new MaterialSemanticEdit(TextureKey(name), before, texture)));
        }
        Refresh(MaterialChangeKind.Texture, name);
    }

    /// <summary>Applies one logical material randomisation as a single history and preview update.</summary>
    public void SetValues(
        IReadOnlyDictionary<string, float> scalars,
        IReadOnlyDictionary<string, Vector4> vectors,
        IReadOnlyDictionary<string, DecodedTextureAsset?> textures)
    {
        ArgumentNullException.ThrowIfNull(scalars);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(textures);
        if (scalars.Any(value => !_scalars.ContainsKey(value.Key) || !float.IsFinite(value.Value)) ||
            vectors.Any(value => !_vectors.ContainsKey(value.Key) || !IsFinite(value.Value)) ||
            textures.Any(value => !TextureParameters.Any(parameter =>
                parameter.Name.Equals(value.Key, StringComparison.OrdinalIgnoreCase))))
        {
            throw new ArgumentException("Material batch contains an unknown parameter or non-finite value.");
        }

        var changedScalars = scalars.Where(value => _scalars[value.Key] != value.Value)
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        var changedVectors = vectors.Where(value => _vectors[value.Key] != value.Value)
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        var changedTextures = textures.Where(value => GetSelectedTexture(value.Key)?.Source != value.Value?.Source)
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        var before = CaptureBatchState(changedScalars.Keys, changedVectors.Keys, changedTextures.Keys);
        foreach (var value in changedScalars)
        {
            _scalars[value.Key] = value.Value;
            _editedScalars.Add(value.Key);
        }
        foreach (var value in changedVectors)
        {
            _vectors[value.Key] = value.Value;
            _editedVectors.Add(value.Key);
        }
        foreach (var value in changedTextures) _textureReferences[value.Key] = value.Value;
        var after = CaptureBatchState(changedScalars.Keys, changedVectors.Keys, changedTextures.Keys);
        if (BatchEquals(before, after))
        {
            RestoreBatchState(before);
            return;
        }
        if (!_replaying)
        {
            CommitHistory(_history.Record(new MaterialSemanticEdit("batch", before, after)));
        }
        Refresh(MaterialChangeKind.Full);
    }

    /// <summary>
    /// Replaces the authored BioMaterialOverride values in the live session.
    /// Unsupported parameters remain in the detached save payload while the
    /// supported subset updates the controls and material preview immediately.
    /// </summary>
    public void ApplyMaterialData(
        MorphFaceMaterialData data,
        IReadOnlyDictionary<string, DecodedTextureAsset?> decodedTextures)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(decodedTextures);
        ValidateUnique(data.Scalars.Select(value => value.Name), "scalar");
        ValidateUnique(data.Vectors.Select(value => value.Name), "vector");
        ValidateUnique(data.Textures.Select(value => value.Name), "texture");
        if (data.Scalars.Any(value => !float.IsFinite(value.Value)) ||
            data.Vectors.Any(value =>
                !float.IsFinite(value.Value.X) || !float.IsFinite(value.Value.Y) ||
                !float.IsFinite(value.Value.Z) || !float.IsFinite(value.Value.W)))
        {
            throw new InvalidDataException("Pasted material values must be finite.");
        }

        _originalOverrides = new MorphFaceMaterialOverrides(
            _originalOverrides.Source,
            data.Scalars.ToArray(),
            data.Vectors.ToArray(),
            data.Textures.ToArray());
        _editedScalars.Clear();
        _editedVectors.Clear();
        foreach (var value in data.Scalars)
        {
            _scalars[value.Name] = value.Value;
        }
        foreach (var value in data.Vectors)
        {
            _vectors[value.Name] = value.Value;
        }

        _textureReferences.Clear();
        foreach (var parameter in TextureParameters)
        {
            _textureReferences[parameter.Name] = null;
        }
        foreach (var value in data.Textures)
        {
            if (value.TextureReference is null)
            {
                _textureReferences[value.Name] = null;
                continue;
            }
            if (decodedTextures.TryGetValue(value.Name, out var texture))
            {
                _textureReferences[value.Name] = texture;
            }
        }
        Refresh(MaterialChangeKind.Full);
    }

    public void Undo() => Replay(_history.PopUndo(), useAfter: false);
    public void Redo() => Replay(_history.PopRedo(), useAfter: true);
    public void ClearRedo() => _history.ClearRedo();

    private void Refresh(MaterialChangeKind kind, string? parameterName = null)
    {
        Materials = BuildMaterials();
        MaterialsChanged?.Invoke(this, new MaterialChangedEventArgs(kind, parameterName));
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private ResolvedHeadMaterialSet BuildMaterials()
    {
        var materials = _originalMaterials.Materials.Values.Select(material =>
        {
            var scalars = new Dictionary<string, float>(material.Scalars, StringComparer.OrdinalIgnoreCase);
            var vectors = new Dictionary<string, Vector4>(material.Vectors, StringComparer.OrdinalIgnoreCase);
            var textures = new Dictionary<string, MaterialTextureBinding>(material.Textures, StringComparer.OrdinalIgnoreCase);
            foreach (var value in _scalars.Where(value => material.Supports(value.Key, MaterialParameterKind.Scalar)))
            {
                scalars[value.Key] = value.Value;
            }
            foreach (var value in _vectors.Where(value => material.Supports(value.Key, MaterialParameterKind.Vector)))
            {
                vectors[value.Key] = value.Value;
            }
            foreach (var value in _textureReferences.Where(value => material.Supports(value.Key, MaterialParameterKind.Texture)))
            {
                if (value.Value is null)
                {
                    var defaults = material.DefaultTextures.Count == 0
                        ? material.Textures
                        : material.DefaultTextures;
                    if (defaults.TryGetValue(value.Key, out var defaultTexture))
                    {
                        textures[value.Key] = defaultTexture;
                    }
                    else
                    {
                        textures.Remove(value.Key);
                    }
                }
                else
                {
                    textures[value.Key] = new MaterialTextureBinding(value.Key, value.Value);
                }
            }
            return material with { Scalars = scalars, Vectors = vectors, Textures = textures };
        }).ToDictionary(material => material.Key, StringComparer.OrdinalIgnoreCase);
        return new ResolvedHeadMaterialSet(materials);
    }

    private void Replay(MaterialSemanticEdit edit, bool useAfter)
    {
        var value = useAfter ? edit.After : edit.Before;
        _replaying = true;
        try
        {
            var parts = edit.Key.Split(':');
            switch (parts[0])
            {
                case "batch":
                    RestoreBatchState((MaterialBatchState)value!);
                    Refresh(MaterialChangeKind.Full);
                    break;
                case "scalar":
                    var scalar = (MaterialNumericState)value!;
                    SetScalar(parts[1], scalar.Value);
                    SetEdited(_editedScalars, parts[1], scalar.Edited);
                    break;
                case "vector":
                    var component = (MaterialNumericState)value!;
                    SetVectorComponent(parts[1], int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), component.Value);
                    SetEdited(_editedVectors, parts[1], component.Edited);
                    break;
                case "vector4":
                    var vector = (MaterialVectorState)value!;
                    SetVector(parts[1], vector.Value);
                    SetEdited(_editedVectors, parts[1], vector.Edited);
                    break;
                case "texture":
                    var texture = value as DecodedTextureAsset;
                    var original = GetEffectiveTexture(parts[1])?.Texture;
                    if (texture?.Source == original?.Source && (texture is null) == (original is null))
                    {
                        _textureReferences.Remove(parts[1]);
                    }
                    else
                    {
                        _textureReferences[parts[1]] = texture;
                    }
                    Refresh(MaterialChangeKind.Texture, parts[1]);
                    break;
            }
        }
        finally
        {
            _replaying = false;
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EndNumericEdit(string key, MaterialNumericState value)
    {
        CommitHistory(_history.End(key, value));
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetEdited(HashSet<string> edited, string name, bool value)
    {
        if (value)
        {
            edited.Add(name);
        }
        else
        {
            edited.Remove(name);
        }
    }

    private MaterialBatchState CaptureBatchState(
        IEnumerable<string> scalarNames,
        IEnumerable<string> vectorNames,
        IEnumerable<string> textureNames) => new(
        scalarNames.ToDictionary(name => name,
            name => new MaterialNumericState(_scalars[name], _editedScalars.Contains(name)),
            StringComparer.OrdinalIgnoreCase),
        vectorNames.ToDictionary(name => name,
            name => new MaterialVectorState(_vectors[name], _editedVectors.Contains(name)),
            StringComparer.OrdinalIgnoreCase),
        textureNames.ToDictionary(name => name, name =>
        {
            var explicitValue = _textureReferences.TryGetValue(name, out var value);
            return new MaterialTextureState(explicitValue ? value : GetSelectedTexture(name), explicitValue);
        }, StringComparer.OrdinalIgnoreCase));

    private void RestoreBatchState(MaterialBatchState state)
    {
        foreach (var value in state.Scalars)
        {
            _scalars[value.Key] = value.Value.Value;
            SetEdited(_editedScalars, value.Key, value.Value.Edited);
        }
        foreach (var value in state.Vectors)
        {
            _vectors[value.Key] = value.Value.Value;
            SetEdited(_editedVectors, value.Key, value.Value.Edited);
        }
        foreach (var value in state.Textures)
        {
            if (value.Value.Explicit) _textureReferences[value.Key] = value.Value.Value;
            else _textureReferences.Remove(value.Key);
        }
    }

    private static bool BatchEquals(MaterialBatchState first, MaterialBatchState second) =>
        first.Scalars.Count == second.Scalars.Count && first.Vectors.Count == second.Vectors.Count &&
        first.Textures.Count == second.Textures.Count &&
        first.Scalars.All(value => second.Scalars.GetValueOrDefault(value.Key) == value.Value) &&
        first.Vectors.All(value => second.Vectors.GetValueOrDefault(value.Key) == value.Value) &&
        first.Textures.All(value => second.Textures.TryGetValue(value.Key, out var other) &&
            other.Explicit == value.Value.Explicit && other.Value?.Source == value.Value.Value?.Source);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private float GetVectorComponent(string name, int component)
    {
        var vector = GetVector(name);
        return component switch
        {
            0 => vector.X,
            1 => vector.Y,
            2 => vector.Z,
            3 => vector.W,
            _ => throw new ArgumentOutOfRangeException(nameof(component))
        };
    }

    private static void ValidateFinite(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Material values must be finite.");
        }
    }

    private static void ValidateUnique(IEnumerable<string> names, string kind)
    {
        var values = names.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            throw new InvalidDataException($"Pasted material {kind} parameters must have unique names.");
        }
    }

    private static string ScalarKey(string name) => $"scalar:{name}";
    private static string VectorKey(string name, int component) => $"vector:{name}:{component}";
    private static string VectorValueKey(string name) => $"vector4:{name}";
    private static string TextureKey(string name) => $"texture:{name}";

    private void CommitHistory(bool committed)
    {
        if (committed)
        {
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed record AttachmentMaterialState(
    ResolvedHeadMaterialSet OriginalMaterials,
    IReadOnlyDictionary<string, DecodedTextureAsset?> TextureReferences,
    ResolvedHeadMaterialSet Materials);

internal sealed record MaterialSemanticEdit(string Key, object? Before, object? After);
internal sealed record MaterialNumericState(float Value, bool Edited);
internal sealed record MaterialVectorState(Vector4 Value, bool Edited);
internal sealed record MaterialTextureState(DecodedTextureAsset? Value, bool Explicit);
internal sealed record MaterialBatchState(
    IReadOnlyDictionary<string, MaterialNumericState> Scalars,
    IReadOnlyDictionary<string, MaterialVectorState> Vectors,
    IReadOnlyDictionary<string, MaterialTextureState> Textures);

internal sealed class MaterialEditHistory
{
    private readonly Stack<MaterialSemanticEdit> _undo = new();
    private readonly Stack<MaterialSemanticEdit> _redo = new();
    private string? _activeKey;
    private object? _activeBefore;
    private object? _activeAfter;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Begin(string key, object currentValue)
    {
        if (string.Equals(_activeKey, key, StringComparison.Ordinal))
        {
            return;
        }
        CommitActive();
        _activeKey = key;
        _activeBefore = currentValue;
        _activeAfter = currentValue;
    }

    public bool End(string key, object currentValue)
    {
        if (_activeKey is null || !string.Equals(_activeKey, key, StringComparison.Ordinal))
        {
            return false;
        }
        _activeAfter = currentValue;
        return CommitActive();
    }

    public bool Record(MaterialSemanticEdit edit)
    {
        if (_activeKey is null)
        {
            return Push(edit);
        }
        else if (string.Equals(_activeKey, edit.Key, StringComparison.Ordinal))
        {
            _activeAfter = edit.After;
            return false;
        }
        else
        {
            var committed = CommitActive();
            return Push(edit) || committed;
        }
    }

    public MaterialSemanticEdit PopUndo()
    {
        EnsureIdle();
        var edit = _undo.Pop();
        _redo.Push(edit);
        return edit;
    }

    public MaterialSemanticEdit PopRedo()
    {
        EnsureIdle();
        var edit = _redo.Pop();
        _undo.Push(edit);
        return edit;
    }

    public void ClearRedo() => _redo.Clear();

    private bool Push(MaterialSemanticEdit edit)
    {
        if (Equals(edit.Before, edit.After))
        {
            return false;
        }
        _undo.Push(edit);
        _redo.Clear();
        return true;
    }

    private void EnsureIdle()
    {
        _ = CommitActive();
    }

    private bool CommitActive()
    {
        if (_activeKey is null)
        {
            return false;
        }
        var edit = new MaterialSemanticEdit(_activeKey, _activeBefore, _activeAfter);
        _activeKey = null;
        return Push(edit);
    }
}
