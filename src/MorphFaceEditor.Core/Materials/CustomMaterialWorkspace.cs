using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Materials;

/// <summary>
/// Describes one distinct authored material slot in a detached mesh. Sections
/// are retained together because several draw sections can reference one slot.
/// </summary>
public sealed record CustomMaterialSlot(
    int MaterialIndex,
    IReadOnlyList<ImportedMeshSection> Sections)
{
    public string MaterialName => Sections.FirstOrDefault()?.MaterialName ?? $"Material {MaterialIndex}";

    public AssetIdentity CreatePreviewIdentity(string sourcePath) =>
        CustomMaterialWorkspace.CreatePreviewIdentity(sourcePath, MaterialIndex);
}

/// <summary>
/// Evidence-backed material supplied by an outer package/profile layer. Core
/// intentionally does not discover or infer these options.
/// </summary>
public sealed record CustomMaterialAssignmentOption(
    string Id,
    string Label,
    string AppearanceCompatibilityKey,
    string MaterialRole,
    HeadMaterialFamily Family,
    ResolvedHeadMaterial Template)
{
    public HeadMaterialFamily MaterialFamily => Family;
    public string? RandomisationProfileKey { get; init; }
    public string? ParameterScopeKey { get; init; }
    public string? ParameterScopeLabel { get; init; }

    public string EffectiveParameterScopeKey => ParameterScopeKey ??
        (AppearanceCompatibilityKey.StartsWith("human", StringComparison.OrdinalIgnoreCase)
            ? "human"
            : AppearanceCompatibilityKey.ToLowerInvariant());
    public string EffectiveParameterScopeLabel => ParameterScopeLabel ??
        (EffectiveParameterScopeKey == "human"
            ? "Human"
            : char.ToUpperInvariant(EffectiveParameterScopeKey[0]) + EffectiveParameterScopeKey[1..]);
}

/// <summary>One selected option and its exact detached slot identity.</summary>
public sealed record CustomMaterialAssignment(
    CustomMaterialSlot Slot,
    CustomMaterialAssignmentOption Option);

/// <summary>Reports a semantic assignment update and the resulting material set.</summary>
public sealed class CustomMaterialWorkspaceChangedEventArgs(
    IReadOnlyList<CustomMaterialAssignment> assignments,
    ResolvedHeadMaterialSet materials) : EventArgs
{
    public IReadOnlyList<CustomMaterialAssignment> Assignments { get; } = assignments;
    public ResolvedHeadMaterialSet Materials { get; } = materials;
}

/// <summary>
/// Owns detached custom-mesh material assignment state without owning or
/// modifying the imported source asset. Assignment options are deliberately
/// evidence-backed inputs from the package/profile layer.
/// </summary>
public sealed class CustomMaterialWorkspace : Editing.IUndoableEditSource
{
    public const int MaximumSupportedSlots = 4;

    private readonly Dictionary<int, CustomMaterialSlot> _slotsByIndex;
    private readonly Dictionary<int, CustomMaterialAssignmentOption> _assignments = new();
    private readonly Stack<IReadOnlyDictionary<int, CustomMaterialAssignmentOption>> _undo = new();
    private readonly Stack<IReadOnlyDictionary<int, CustomMaterialAssignmentOption>> _redo = new();
    private readonly string _workspaceKey;

    public CustomMaterialWorkspace(ImportedMeshAsset source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        var slots = source.Sections
            .Where(section => section.IndexCount > 0)
            .GroupBy(section => section.MaterialIndex)
            .Select(group => new CustomMaterialSlot(group.Key, group.ToArray()))
            .ToArray();
        if (slots.Length > MaximumSupportedSlots)
        {
            throw new InvalidDataException(
                $"A custom material workspace supports at most {MaximumSupportedSlots} used material slots.");
        }

        // GroupBy preserves first authored occurrence, giving slot enumeration
        // a stable order without rewriting the source section list.
        UsedSlots = slots;
        _slotsByIndex = slots.ToDictionary(slot => slot.MaterialIndex);
        _workspaceKey = Path.GetFullPath(source.SourcePath);
        PreviewMaterialIdentities = slots.ToDictionary(
            slot => slot.MaterialIndex,
            slot => CreatePreviewIdentity(_workspaceKey, slot.MaterialIndex));
        ActiveMaterials = ResolvedHeadMaterialSet.Empty;
    }

    public ImportedMeshAsset Source { get; }
    public IReadOnlyList<CustomMaterialSlot> UsedSlots { get; }
    public IReadOnlyDictionary<int, AssetIdentity> PreviewMaterialIdentities { get; }
    public IReadOnlyList<CustomMaterialAssignment> Assignments =>
        _assignments.OrderBy(value => value.Key)
            .Select(value => new CustomMaterialAssignment(_slotsByIndex[value.Key], value.Value))
            .ToArray();
    public IReadOnlyList<string> ActiveRandomisationProfileKeys => _assignments
        .OrderBy(value => value.Key)
        .Select(value => value.Value.RandomisationProfileKey)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public ResolvedHeadMaterialSet ActiveMaterials { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event EventHandler<CustomMaterialWorkspaceChangedEventArgs>? ActiveMaterialsChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? EditCommitted;

    public bool TryGetSlot(int materialIndex, out CustomMaterialSlot? slot) =>
        _slotsByIndex.TryGetValue(materialIndex, out slot);

    public AssetIdentity GetPreviewMaterialIdentity(int materialIndex)
    {
        if (!PreviewMaterialIdentities.TryGetValue(materialIndex, out var identity))
        {
            throw new ArgumentOutOfRangeException(nameof(materialIndex));
        }
        return identity;
    }

    public AssetIdentity GetPreviewMaterialIdentity(CustomMaterialSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return GetPreviewMaterialIdentity(slot.MaterialIndex);
    }

    public void Assign(int materialIndex, CustomMaterialAssignmentOption option)
    {
        if (!_slotsByIndex.TryGetValue(materialIndex, out var slot))
        {
            throw new ArgumentOutOfRangeException(nameof(materialIndex),
                $"Material slot {materialIndex} is not a used imported slot.");
        }
        Assign(slot, option);
    }

    public void Assign(CustomMaterialSlot slot, CustomMaterialAssignmentOption option)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(option);
        if (!_slotsByIndex.TryGetValue(slot.MaterialIndex, out var exactSlot) ||
            !ReferenceEquals(exactSlot, slot) && !Equals(exactSlot, slot))
        {
            throw new ArgumentException("The material slot does not belong to this custom workspace.", nameof(slot));
        }
        ValidateOption(option);
        ValidateAppearanceCompatibility(option, materialIndexBeingReplaced: slot.MaterialIndex);

        if (_assignments.TryGetValue(slot.MaterialIndex, out var current) && Equals(current, option))
        {
            return;
        }
        var before = Snapshot();
        _assignments[slot.MaterialIndex] = option;
        Commit(before);
    }

    public void Unassign(int materialIndex)
    {
        if (!_slotsByIndex.ContainsKey(materialIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(materialIndex));
        }
        if (!_assignments.ContainsKey(materialIndex))
        {
            return;
        }
        var before = Snapshot();
        _assignments.Remove(materialIndex);
        Commit(before);
    }

    /// <summary>Validates a complete incoming assignment set before publishing one semantic change.</summary>
    public void ReplaceAssignments(IReadOnlyDictionary<int, CustomMaterialAssignmentOption> assignments)
    {
        var validation = new CustomMaterialWorkspace(Source);
        foreach (var value in assignments.OrderBy(value => value.Key)) validation.Assign(value.Key, value.Value);
        if (_assignments.Count == assignments.Count && assignments.All(value =>
                _assignments.TryGetValue(value.Key, out var current) && current == value.Value)) return;
        var before = Snapshot();
        _assignments.Clear();
        foreach (var value in assignments) _assignments[value.Key] = value.Value;
        Commit(before);
    }

    public void Undo() => Replay(_undo, _redo);
    public void Redo() => Replay(_redo, _undo);

    public void ClearRedo()
    {
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ValidateOption(CustomMaterialAssignmentOption option)
    {
        if (string.IsNullOrWhiteSpace(option.Id) || string.IsNullOrWhiteSpace(option.Label) ||
            string.IsNullOrWhiteSpace(option.AppearanceCompatibilityKey) ||
            string.IsNullOrWhiteSpace(option.MaterialRole))
        {
            throw new ArgumentException("A material assignment option requires stable id, label, role and compatibility key.", nameof(option));
        }
        if (option.Family == HeadMaterialFamily.Unknown ||
            !HeadMaterialClassifier.IsAssignableHeadFamily(option.Family))
        {
            throw new ArgumentException("Unknown material families cannot be assigned to custom mesh slots.", nameof(option));
        }
        ArgumentNullException.ThrowIfNull(option.Template);
    }

    private void ValidateAppearanceCompatibility(
        CustomMaterialAssignmentOption option,
        int materialIndexBeingReplaced)
    {
        var role = CompatibilityRole(option);
        if (role == CustomMaterialCompatibilityRole.None)
        {
            return;
        }

        var sameRoleAssignments = _assignments
            .Where(value => value.Key != materialIndexBeingReplaced)
            .Select(value => value.Value)
            .Where(value => CompatibilityRole(value) == role);
        var incompatible = role switch
        {
            // HMM and HMF/Asari eyes still share one human control surface.
            // Racial eye materials have independent scopes and may be mixed freely.
            CustomMaterialCompatibilityRole.Eyes when option.Family == HeadMaterialFamily.Eyes =>
                sameRoleAssignments.FirstOrDefault(value =>
                    value.Family == HeadMaterialFamily.Eyes &&
                    !string.Equals(value.Id, option.Id, StringComparison.OrdinalIgnoreCase)),
            CustomMaterialCompatibilityRole.Lashes or CustomMaterialCompatibilityRole.Hair =>
                sameRoleAssignments.FirstOrDefault(value =>
                    !string.Equals(value.Id, option.Id, StringComparison.OrdinalIgnoreCase)),
            _ => null
        };
        if (incompatible is not null)
        {
            throw new ArgumentException(
                $"{role} assignments on one custom head must use the same material.",
                nameof(option));
        }
    }

    private static CustomMaterialCompatibilityRole CompatibilityRole(CustomMaterialAssignmentOption option) =>
        option.Family is HeadMaterialFamily.Eyes or HeadMaterialFamily.SalarianEyes or
            HeadMaterialFamily.TurianEyes or HeadMaterialFamily.KroganEyes or
            HeadMaterialFamily.VorchaEyes || option.MaterialRole.Contains("eye", StringComparison.OrdinalIgnoreCase)
            ? CustomMaterialCompatibilityRole.Eyes
            : option.Family == HeadMaterialFamily.Lashes ||
              option.MaterialRole.Contains("lash", StringComparison.OrdinalIgnoreCase)
                ? CustomMaterialCompatibilityRole.Lashes
                : option.Family is HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair ||
                  option.MaterialRole.Contains("hair", StringComparison.OrdinalIgnoreCase)
                    ? CustomMaterialCompatibilityRole.Hair
                    : CustomMaterialCompatibilityRole.None;

    private enum CustomMaterialCompatibilityRole
    {
        None,
        Eyes,
        Lashes,
        Hair
    }

    private IReadOnlyDictionary<int, CustomMaterialAssignmentOption> Snapshot() =>
        new Dictionary<int, CustomMaterialAssignmentOption>(_assignments);

    private void Commit(IReadOnlyDictionary<int, CustomMaterialAssignmentOption> before)
    {
        _undo.Push(before);
        _redo.Clear();
        EditCommitted?.Invoke(this, EventArgs.Empty);
        RebuildActiveMaterials();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Replay(
        Stack<IReadOnlyDictionary<int, CustomMaterialAssignmentOption>> from,
        Stack<IReadOnlyDictionary<int, CustomMaterialAssignmentOption>> to)
    {
        if (from.Count == 0)
        {
            return;
        }
        var target = from.Pop();
        to.Push(Snapshot());
        _assignments.Clear();
        foreach (var value in target) _assignments[value.Key] = value.Value;
        RebuildActiveMaterials();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildActiveMaterials()
    {
        var materials = new Dictionary<string, ResolvedHeadMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in _assignments.OrderBy(value => value.Key))
        {
            var synthetic = CreatePreviewIdentity(_workspaceKey, assignment.Key);
            var key = MaterialIdentityKey.Create(synthetic);
            var template = assignment.Value.Template;
            materials[key] = template with
            {
                Key = key,
                Source = synthetic,
                Scalars = new Dictionary<string, float>(template.Scalars, StringComparer.OrdinalIgnoreCase),
                Vectors = new Dictionary<string, System.Numerics.Vector4>(template.Vectors, StringComparer.OrdinalIgnoreCase),
                Textures = new Dictionary<string, MaterialTextureBinding>(template.Textures, StringComparer.OrdinalIgnoreCase),
                DefaultScalars = new Dictionary<string, float>(template.DefaultScalars, StringComparer.OrdinalIgnoreCase),
                DefaultVectors = new Dictionary<string, System.Numerics.Vector4>(template.DefaultVectors, StringComparer.OrdinalIgnoreCase),
                DefaultTextures = new Dictionary<string, MaterialTextureBinding>(template.DefaultTextures, StringComparer.OrdinalIgnoreCase),
                SupportedScalars = new HashSet<string>(template.SupportedScalars, StringComparer.OrdinalIgnoreCase),
                SupportedVectors = new HashSet<string>(template.SupportedVectors, StringComparer.OrdinalIgnoreCase),
                SupportedTextures = new HashSet<string>(template.SupportedTextures, StringComparer.OrdinalIgnoreCase),
                ParameterScopeKey = assignment.Value.EffectiveParameterScopeKey,
                ParameterScopeLabel = assignment.Value.EffectiveParameterScopeLabel
            };
        }
        ActiveMaterials = new ResolvedHeadMaterialSet(materials);
        ActiveMaterialsChanged?.Invoke(this,
            new CustomMaterialWorkspaceChangedEventArgs(Assignments, ActiveMaterials));
    }

    internal static AssetIdentity CreatePreviewIdentity(string sourcePath, int materialIndex) => new(
        Path.GetFullPath(sourcePath),
        $"[MESH].MaterialSlot[{materialIndex}]",
        -1 - materialIndex,
        "MaterialInstanceConstant");
}
