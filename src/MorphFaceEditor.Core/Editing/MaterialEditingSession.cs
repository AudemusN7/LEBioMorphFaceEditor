using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Editing;

/// <summary>Tracks authored material deltas, inherited effective values and semantic undo/redo state.</summary>
public sealed class MaterialEditingSession : IUndoableEditSource
{
    private MorphFaceMaterialOverrides _originalOverrides;
    private ResolvedHeadMaterialSet _baseMaterials;
    private ResolvedHeadMaterialSet _attachmentMaterials = ResolvedHeadMaterialSet.Empty;
    private ResolvedHeadMaterialSet _originalMaterials;
    private IReadOnlyDictionary<string, float> _defaultScalars;
    private IReadOnlyDictionary<string, Vector4> _defaultVectors;
    private readonly Dictionary<string, float> _scalars;
    private readonly Dictionary<string, Vector4> _vectors;
    private readonly Dictionary<string, DecodedTextureAsset?> _textureReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _editedScalars = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _editedVectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly MaterialEditHistory _history = new();
    private bool _replaying;
    private bool _hasScopedMaterials;

    public MaterialEditingSession(
        MorphFaceMaterialOverrides overrides,
        ResolvedHeadMaterialSet materials,
        IReadOnlyCollection<string>? baseMaterialKeys = null)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        _originalOverrides = overrides;
        _originalMaterials = materials ?? throw new ArgumentNullException(nameof(materials));
        _hasScopedMaterials = HasScopedMaterials(materials);
        _baseMaterials = baseMaterialKeys is null
            ? materials
            : new ResolvedHeadMaterialSet(materials.Materials
                .Where(pair => baseMaterialKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        _defaultScalars = BuildDefaultScalars(materials, firstWins: false);
        _defaultVectors = BuildDefaultVectors(materials, firstWins: false);
        _scalars = _defaultScalars
            .Concat(overrides.Scalars.Select(value => new KeyValuePair<string, float>(value.Name, value.Value)))
            .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        _vectors = _defaultVectors
            .Concat(overrides.Vectors.Select(value => new KeyValuePair<string, Vector4>(value.Name, value.Value)))
            .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        TextureParameters = BuildTextureParameters(materials, overrides.Textures);
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
            AppliesTo(material, name, MaterialParameterKind.Scalar)))
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> VectorNames => _vectors.Keys
        .Where(name => _originalMaterials.Materials.Values.Any(material =>
            AppliesTo(material, name, MaterialParameterKind.Vector)))
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<TextureMaterialOverride> TextureParameters { get; private set; }
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

    public AssetIdentity? GetTextureReference(string name) =>
        _textureReferences.TryGetValue(name, out var selected) ? selected?.Source :
        _originalOverrides.Textures.FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            is { } authored ? authored.TextureReference : GetEffectiveTexture(name)?.Texture.Source;

    /// <summary>Captures effective values, explicit inheritance and unresolved references for material interchange.</summary>
    public MorphFaceMaterialData CaptureInterchangeData()
    {
        var scalarNames = ScalarNames.Concat(_editedScalars).Concat(_originalOverrides.Scalars.Select(value => value.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vectorNames = VectorNames.Concat(_editedVectors).Concat(_originalOverrides.Vectors.Select(value => value.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(
            _scalars.Where(value => scalarNames.Contains(value.Key))
                .OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .Select(value => new ScalarMaterialOverride(value.Key, value.Value)).ToArray(),
            _vectors.Where(value => vectorNames.Contains(value.Key))
                .OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .Select(value => new VectorMaterialOverride(value.Key, value.Value)).ToArray(),
            TextureParameters.Select(value => value.Name).Concat(_originalOverrides.Textures.Select(value => value.Name))
                .Concat(_textureReferences.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(name => new TextureMaterialOverride(name, GetTextureReference(name))).ToArray());
    }

    public string GetSourceParameterName(string controlName) =>
        MaterialParameterControlKey.ParameterName(controlName);
    public string? GetParameterScopeKey(string controlName) =>
        MaterialParameterControlKey.ScopeKey(controlName);
    public string ResolveControlName(string parameterName, MaterialParameterKind kind)
    {
        IReadOnlyList<string> controls = kind switch
        {
            MaterialParameterKind.Scalar => ScalarNames,
            MaterialParameterKind.Vector => VectorNames,
            MaterialParameterKind.Texture => TextureParameters.Select(value => value.Name).ToArray(),
            _ => []
        };
        if (controls.Contains(parameterName, StringComparer.OrdinalIgnoreCase)) return parameterName;
        var matches = controls.Where(control => GetSourceParameterName(control).Equals(
                parameterName, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => parameterName,
            _ => throw new InvalidDataException(
                $"Material parameter '{parameterName}' exists in more than one MESH racial scope; " +
                "use a scoped MFE material payload.")
        };
    }
    public string? GetParameterScopeLabel(string controlName)
    {
        var scopeKey = GetParameterScopeKey(controlName);
        return scopeKey is null
            ? null
            : _originalMaterials.Materials.Values
                .FirstOrDefault(material => string.Equals(
                    EffectiveScopeKey(material), scopeKey, StringComparison.OrdinalIgnoreCase))
                ?.ParameterScopeLabel ?? scopeKey;
    }

    public DecodedTextureAsset? GetPreviewTexture(string name) => Materials.Materials.Values
        .Where(material => AppliesTo(material, name, MaterialParameterKind.Texture))
        .Select(material => material.Textures.GetValueOrDefault(GetSourceParameterName(name))?.Texture)
        .FirstOrDefault(texture => texture is not null);

    public DecodedTextureAsset? GetDefaultTexture(string name) => _originalMaterials.Materials.Values
        .Where(material => AppliesTo(material, name, MaterialParameterKind.Texture))
        .Select(material => (material.DefaultTextures.Count == 0 ? material.Textures : material.DefaultTextures)
            .GetValueOrDefault(GetSourceParameterName(name))?.Texture)
        .FirstOrDefault(texture => texture is not null);

    public MaterialTextureBinding? GetEffectiveTexture(string name) => _originalMaterials.Materials.Values
        .Where(material => AppliesTo(material, name, MaterialParameterKind.Texture))
        .Select(material => material.Textures.GetValueOrDefault(GetSourceParameterName(name)))
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
        _attachmentMaterials = attachmentMaterials;
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
        Refresh(MaterialChangeKind.Surface);
    }

    /// <summary>
    /// Replaces the active material surface after custom slot assignment. This
    /// is a surface rebase, not a user edit: existing history is retained and
    /// no second history item is recorded. Custom surfaces are ordered by the
    /// workspace from lowest material slot first, so the first default wins.
    /// </summary>
    public void RebaseMaterialSurface(ResolvedHeadMaterialSet materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var previousScalars = new Dictionary<string, float>(_scalars, StringComparer.OrdinalIgnoreCase);
        var previousVectors = new Dictionary<string, Vector4>(_vectors, StringComparer.OrdinalIgnoreCase);
        var previousTextures = new Dictionary<string, DecodedTextureAsset?>(
            _textureReferences, StringComparer.OrdinalIgnoreCase);

        _baseMaterials = materials;
        _originalMaterials = MergeMaterialSets(_baseMaterials, _attachmentMaterials);
        _hasScopedMaterials = HasScopedMaterials(_baseMaterials);
        _defaultScalars = BuildDefaultScalars(_originalMaterials, firstWins: true);
        _defaultVectors = BuildDefaultVectors(_originalMaterials, firstWins: true);

        _scalars.Clear();
        foreach (var value in _defaultScalars)
        {
            _scalars[value.Key] = ShouldRetainValue(value.Key, _editedScalars,
                _originalOverrides.Scalars.Select(item => item.Name),
                previousScalars)
                ? previousScalars[value.Key]
                : value.Value;
        }
        // Unsupported source overrides are intentionally retained for a later
        // RON/output path even when the new active surface cannot preview them.
        foreach (var value in _originalOverrides.Scalars)
        {
            if (!_scalars.ContainsKey(value.Name)) _scalars[value.Name] = value.Value;
        }
        foreach (var name in _editedScalars)
        {
            if (previousScalars.TryGetValue(name, out var value)) _scalars[name] = value;
        }

        _vectors.Clear();
        foreach (var value in _defaultVectors)
        {
            _vectors[value.Key] = ShouldRetainValue(value.Key, _editedVectors,
                _originalOverrides.Vectors.Select(item => item.Name),
                previousVectors)
                ? previousVectors[value.Key]
                : value.Value;
        }
        foreach (var value in _originalOverrides.Vectors)
        {
            if (!_vectors.ContainsKey(value.Name)) _vectors[value.Name] = value.Value;
        }
        foreach (var name in _editedVectors)
        {
            if (previousVectors.TryGetValue(name, out var value)) _vectors[name] = value;
        }

        TextureParameters = BuildTextureParameters(_originalMaterials, _originalOverrides.Textures);
        _textureReferences.Clear();
        foreach (var value in previousTextures) _textureReferences[value.Key] = value.Value;
        Refresh(MaterialChangeKind.Surface);
    }

    /// <summary>Convenience overload that makes custom lowest-slot ordering explicit.</summary>
    public void RebaseMaterialSurface(MorphFaceEditor.Core.Materials.CustomMaterialWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        RebaseMaterialSurface(workspace.ActiveMaterials);
    }

    public AttachmentMaterialState CaptureAttachmentState() => new(
        _baseMaterials,
        _attachmentMaterials,
        _originalMaterials,
        new Dictionary<string, float>(_defaultScalars, StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, Vector4>(_defaultVectors, StringComparer.OrdinalIgnoreCase),
        TextureParameters.ToArray(),
        new Dictionary<string, DecodedTextureAsset?>(_textureReferences, StringComparer.OrdinalIgnoreCase),
        Materials);

    public void RestoreAttachmentState(AttachmentMaterialState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _baseMaterials = state.BaseMaterials;
        _attachmentMaterials = state.AttachmentMaterials;
        _originalMaterials = state.OriginalMaterials;
        _hasScopedMaterials = HasScopedMaterials(_baseMaterials);
        _defaultScalars = state.DefaultScalars;
        _defaultVectors = state.DefaultVectors;
        TextureParameters = state.TextureParameters;
        _textureReferences.Clear();
        foreach (var value in state.TextureReferences)
        {
            _textureReferences[value.Key] = value.Value;
        }
        Materials = state.Materials;
        MaterialsChanged?.Invoke(this, new MaterialChangedEventArgs(MaterialChangeKind.Surface, null));
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
                GetSourceParameterName(name), MaterialParameterKind.Texture).Family == HeadMaterialFamily.Hair)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in hairTextureNames)
        {
            var parameterName = GetSourceParameterName(name);
            var replacement = replacementHair
                .Select(material => material.Textures.GetValueOrDefault(parameterName))
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
        var before = new MaterialTextureState(_textureReferences.GetValueOrDefault(name), _textureReferences.ContainsKey(name));
        if (GetTextureReference(name) == texture?.Source)
        {
            return;
        }
        _textureReferences[name] = texture;
        if (!_replaying)
        {
            CommitHistory(_history.Record(new MaterialSemanticEdit(TextureKey(name), before, new MaterialTextureState(texture, true))));
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

    /// <summary>Clears every authored override and restores inherited material values as one undoable edit.</summary>
    public void ResetToDefaults()
    {
        if (_originalOverrides.Scalars.Count == 0 && _originalOverrides.Vectors.Count == 0 &&
            _originalOverrides.Textures.Count == 0 && _editedScalars.Count == 0 &&
            _editedVectors.Count == 0 &&
            TextureParameters.All(value => _textureReferences.GetValueOrDefault(value.Name) is null) &&
            _defaultScalars.All(value => _scalars.GetValueOrDefault(value.Key) == value.Value) &&
            _defaultVectors.All(value => _vectors.GetValueOrDefault(value.Key) == value.Value))
        {
            return;
        }

        var before = CaptureResetState();
        _originalOverrides = new MorphFaceMaterialOverrides(_originalOverrides.Source, [], [], []);
        _scalars.Clear();
        foreach (var value in _defaultScalars) _scalars[value.Key] = value.Value;
        _vectors.Clear();
        foreach (var value in _defaultVectors) _vectors[value.Key] = value.Value;
        _textureReferences.Clear();
        foreach (var parameter in TextureParameters) _textureReferences[parameter.Name] = null;
        _editedScalars.Clear();
        _editedVectors.Clear();
        var after = CaptureResetState();
        if (!_replaying)
        {
            CommitHistory(_history.Record(new MaterialSemanticEdit("defaults", before, after)));
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
        IReadOnlyDictionary<string, DecodedTextureAsset?> decodedTextures,
        bool preserveUnspecifiedTextures = false)
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

        var mappedScalars = data.Scalars.Select(value => value with
        {
            Name = ResolveControlName(value.Name, MaterialParameterKind.Scalar)
        }).ToArray();
        var mappedVectors = data.Vectors.Select(value => value with
        {
            Name = ResolveControlName(value.Name, MaterialParameterKind.Vector)
        }).ToArray();
        var mappedTextures = data.Textures.Select(value => value with
        {
            Name = ResolveControlName(value.Name, MaterialParameterKind.Texture)
        }).ToArray();
        ValidateUnique(mappedScalars.Select(value => value.Name), "mapped scalar");
        ValidateUnique(mappedVectors.Select(value => value.Name), "mapped vector");
        ValidateUnique(mappedTextures.Select(value => value.Name), "mapped texture");
        var before = CaptureResetState();
        _originalOverrides = new MorphFaceMaterialOverrides(
            _originalOverrides.Source,
            mappedScalars,
            mappedVectors,
            mappedTextures);
        _editedScalars.Clear();
        _editedVectors.Clear();
        _scalars.Clear();
        foreach (var value in _defaultScalars) _scalars[value.Key] = value.Value;
        _vectors.Clear();
        foreach (var value in _defaultVectors) _vectors[value.Key] = value.Value;
        foreach (var value in mappedScalars)
        {
            _scalars[value.Name] = value.Value;
        }
        foreach (var value in mappedVectors)
        {
            _vectors[value.Name] = value.Value;
        }

        _textureReferences.Clear();
        if (preserveUnspecifiedTextures)
        {
            foreach (var value in before.Textures) _textureReferences[value.Key] = value.Value;
        }
        else foreach (var parameter in TextureParameters)
        {
            _textureReferences[parameter.Name] = null;
        }
        foreach (var value in mappedTextures)
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
            else
            {
                // Keep the authored path even when preview decoding failed. Absence from
                // the decoded map is different from an explicit None override.
                _textureReferences.Remove(value.Name);
            }
        }
        if (!_replaying) CommitHistory(_history.Record(new MaterialSemanticEdit("defaults", before, CaptureResetState())));
        Refresh(MaterialChangeKind.Full);
    }

    /// <summary>Applies a partial material file as one undoable edit; absent values retain their current state.</summary>
    public void MergeMaterialData(MorphFaceMaterialData data, IReadOnlyDictionary<string, DecodedTextureAsset?> decodedTextures)
    {
        var current = CreateOverrides();
        var scalars = current.Scalars.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var vectors = current.Vectors.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var textures = current.Textures.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        ValidateUnique(data.Scalars.Select(value => value.Name), "scalar");
        ValidateUnique(data.Vectors.Select(value => value.Name), "vector");
        ValidateUnique(data.Textures.Select(value => value.Name), "texture");
        foreach (var value in data.Scalars) scalars[value.Name] = value;
        foreach (var value in data.Vectors) vectors[value.Name] = value;
        foreach (var value in data.Textures) textures[value.Name] = value;
        var decoded = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in current.Textures)
        {
            var texture = GetSelectedTexture(value.Name);
            if (texture?.Source == value.TextureReference) decoded[value.Name] = texture;
        }
        foreach (var value in data.Textures) decoded.Remove(value.Name);
        foreach (var value in decodedTextures) decoded[value.Key] = value.Value;
        ApplyMaterialData(new(scalars.Values.ToArray(), vectors.Values.ToArray(), textures.Values.ToArray()), decoded,
            preserveUnspecifiedTextures: true);
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
            foreach (var value in _scalars.Where(value => AppliesTo(material, value.Key, MaterialParameterKind.Scalar)))
            {
                scalars[GetSourceParameterName(value.Key)] = value.Value;
            }
            foreach (var value in _vectors.Where(value => AppliesTo(material, value.Key, MaterialParameterKind.Vector)))
            {
                vectors[GetSourceParameterName(value.Key)] = value.Value;
            }
            foreach (var value in _textureReferences.Where(value => AppliesTo(material, value.Key, MaterialParameterKind.Texture)))
            {
                var parameterName = GetSourceParameterName(value.Key);
                if (value.Value is null)
                {
                    var defaults = material.DefaultTextures.Count == 0
                        ? material.Textures
                        : material.DefaultTextures;
                    if (defaults.TryGetValue(parameterName, out var defaultTexture))
                    {
                        textures[parameterName] = defaultTexture;
                    }
                    else
                    {
                        textures.Remove(parameterName);
                    }
                }
                else
                {
                    textures[parameterName] = new MaterialTextureBinding(parameterName, value.Value);
                }
            }
            return material with { Scalars = scalars, Vectors = vectors, Textures = textures };
        }).ToDictionary(material => material.Key, StringComparer.OrdinalIgnoreCase);
        return new ResolvedHeadMaterialSet(materials);
    }

    private Dictionary<string, float> BuildDefaultScalars(
        ResolvedHeadMaterialSet materials,
        bool firstWins)
    {
        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials.Materials.Values)
        {
            var defaults = material.DefaultScalars.Count == 0 ? material.Scalars : material.DefaultScalars;
            foreach (var value in defaults)
            {
                var controlName = ControlName(material, value.Key);
                if (firstWins && values.ContainsKey(controlName)) continue;
                values[controlName] = value.Value;
            }
            foreach (var value in material.Scalars)
            {
                var controlName = ControlName(material, value.Key);
                if (!defaults.ContainsKey(value.Key) && !values.ContainsKey(controlName))
                {
                    values[controlName] = value.Value;
                }
            }
            foreach (var name in material.SupportedScalars)
            {
                var controlName = ControlName(material, name);
                if (!values.ContainsKey(controlName)) values[controlName] = material.Scalars.GetValueOrDefault(name);
            }
        }
        return values;
    }

    private static ResolvedHeadMaterialSet MergeMaterialSets(
        ResolvedHeadMaterialSet baseMaterials,
        ResolvedHeadMaterialSet attachmentMaterials)
    {
        var merged = new Dictionary<string, ResolvedHeadMaterial>(
            baseMaterials.Materials, StringComparer.OrdinalIgnoreCase);
        foreach (var material in attachmentMaterials.Materials) merged[material.Key] = material.Value;
        return new ResolvedHeadMaterialSet(merged);
    }

    private Dictionary<string, Vector4> BuildDefaultVectors(
        ResolvedHeadMaterialSet materials,
        bool firstWins)
    {
        var values = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials.Materials.Values)
        {
            var defaults = material.DefaultVectors.Count == 0 ? material.Vectors : material.DefaultVectors;
            foreach (var value in defaults)
            {
                var controlName = ControlName(material, value.Key);
                if (firstWins && values.ContainsKey(controlName)) continue;
                values[controlName] = value.Value;
            }
            foreach (var value in material.Vectors)
            {
                var controlName = ControlName(material, value.Key);
                if (!defaults.ContainsKey(value.Key) && !values.ContainsKey(controlName))
                {
                    values[controlName] = value.Value;
                }
            }
            foreach (var name in material.SupportedVectors)
            {
                var controlName = ControlName(material, name);
                if (!values.ContainsKey(controlName)) values[controlName] = material.Vectors.GetValueOrDefault(name);
            }
        }
        return values;
    }

    private TextureMaterialOverride[] BuildTextureParameters(
        ResolvedHeadMaterialSet materials,
        IEnumerable<TextureMaterialOverride> authored)
    {
        var values = new Dictionary<string, AssetIdentity?>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials.Materials.Values)
        {
            foreach (var value in material.Textures.Values)
                values.TryAdd(ControlName(material, value.ParameterName), value.Texture.Source);
            foreach (var value in material.DefaultTextures.Values)
                values.TryAdd(ControlName(material, value.ParameterName), value.Texture.Source);
            foreach (var name in material.SupportedTextures) values.TryAdd(ControlName(material, name), null);
        }
        foreach (var value in authored) values.TryAdd(value.Name, value.TextureReference);
        return values
            .Where(value => materials.Materials.Values.Any(material =>
                AppliesTo(material, value.Key, MaterialParameterKind.Texture)))
            .Select(value => new TextureMaterialOverride(value.Key, value.Value))
            .ToArray();
    }

    private static bool HasScopedMaterials(ResolvedHeadMaterialSet materials) =>
        materials.Materials.Values.Any(material => !string.IsNullOrWhiteSpace(material.ParameterScopeKey));

    private string ControlName(ResolvedHeadMaterial material, string parameterName)
    {
        var scopeKey = EffectiveScopeKey(material);
        return scopeKey is null
            ? parameterName
            : MaterialParameterControlKey.Create(scopeKey, parameterName);
    }

    private string? EffectiveScopeKey(ResolvedHeadMaterial material)
    {
        if (!_hasScopedMaterials) return null;
        if (!string.IsNullOrWhiteSpace(material.ParameterScopeKey)) return material.ParameterScopeKey;
        // Preview-only human hair attachments remain linked to the shared Human
        // colour controls without becoming independently authored MESH slots.
        return material.Family is HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair or
            HeadMaterialFamily.Scalp
            ? "human"
            : null;
    }

    private bool AppliesTo(
        ResolvedHeadMaterial material,
        string controlName,
        MaterialParameterKind kind)
    {
        var parameterName = GetSourceParameterName(controlName);
        if (!material.Supports(parameterName, kind)) return false;
        var controlScope = GetParameterScopeKey(controlName);
        var materialScope = EffectiveScopeKey(material);
        return string.Equals(controlScope, materialScope, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRetainValue<T>(
        string name,
        IReadOnlySet<string> edited,
        IEnumerable<string> authoredNames,
        IReadOnlyDictionary<string, T> previous)
        where T : notnull =>
        previous.ContainsKey(name) &&
        (edited.Contains(name) || authoredNames.Any(value =>
            string.Equals(value, name, StringComparison.OrdinalIgnoreCase)));

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
                case "defaults":
                    RestoreResetState((MaterialResetState)value!);
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
                    var texture = (MaterialTextureState)value!;
                    if (texture.Explicit) _textureReferences[parts[1]] = texture.Value;
                    else _textureReferences.Remove(parts[1]);
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

    private MaterialResetState CaptureResetState() => new(
        _originalOverrides,
        new Dictionary<string, float>(_scalars, StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, Vector4>(_vectors, StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, DecodedTextureAsset?>(_textureReferences, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(_editedScalars, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(_editedVectors, StringComparer.OrdinalIgnoreCase));

    private void RestoreResetState(MaterialResetState state)
    {
        _originalOverrides = state.OriginalOverrides;
        _scalars.Clear();
        foreach (var value in state.Scalars) _scalars[value.Key] = value.Value;
        _vectors.Clear();
        foreach (var value in state.Vectors) _vectors[value.Key] = value.Value;
        _textureReferences.Clear();
        foreach (var value in state.Textures) _textureReferences[value.Key] = value.Value;
        _editedScalars.Clear();
        foreach (var value in state.EditedScalars) _editedScalars.Add(value);
        _editedVectors.Clear();
        foreach (var value in state.EditedVectors) _editedVectors.Add(value);
    }

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
    ResolvedHeadMaterialSet BaseMaterials,
    ResolvedHeadMaterialSet AttachmentMaterials,
    ResolvedHeadMaterialSet OriginalMaterials,
    IReadOnlyDictionary<string, float> DefaultScalars,
    IReadOnlyDictionary<string, Vector4> DefaultVectors,
    IReadOnlyList<TextureMaterialOverride> TextureParameters,
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
internal sealed record MaterialResetState(
    MorphFaceMaterialOverrides OriginalOverrides,
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, Vector4> Vectors,
    IReadOnlyDictionary<string, DecodedTextureAsset?> Textures,
    IReadOnlySet<string> EditedScalars,
    IReadOnlySet<string> EditedVectors);

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
