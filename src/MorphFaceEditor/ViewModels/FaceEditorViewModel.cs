using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Core.Materials;
using static MorphFaceEditor.Services.EditorRandomisationService;

namespace MorphFaceEditor.ViewModels;

public sealed record PreparedManualHairScalpPair(
    PreparedMaterialRandomisation Material, string StyleId)
{
    public bool RequiresCcScalpMaterial { get; init; }
    public bool FemaleScalp { get; init; }
    public ResolvedHeadMaterial? ScalpMaterial { get; init; }
}

/// <summary>
/// Composes one detached editing session into category controls, semantic history and scoped
/// randomisation commands. Package I/O and rendering remain outside this view model.
/// </summary>
public sealed class FaceEditorViewModel : ObservableObject, IDisposable
{
    private readonly MorphFaceEditingSession _session;
    private readonly EditorHistory _history;
    private readonly RelayCommand _undoCommand;
    private readonly RelayCommand _redoCommand;
    private readonly RelayCommand _setToDefaultsCommand;
    private readonly RelayCommand _randomiseCommand;
    private readonly RelayCommand _hairRandomiseCommand;
    private readonly List<RelayCommand> _subcategoryRandomiseCommands = [];
    private readonly MorphRandomisationCatalog _randomisationCatalog;
    private readonly EditorRandomisationService _randomisation;
    private readonly PackageReferenceService _references;
    private readonly string _profileKey;
    private readonly string _logIdentity;
    private readonly string _logGame;
    private readonly AssetReferenceEditingSession _scalpMaterialSession = new(null);
    private readonly ResolvedHeadMaterial? _sourceScalpMaterial;
    private readonly Dictionary<string, ResolvedHeadMaterial> _scalpMaterialDonors = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _materialRandomisationProfileKey;
    private readonly bool _allowsMorphRandomisation;
    private readonly Func<int> _randomSeedFactory;
    private readonly Action<string> _reportError;
    private MorphFaceEditor.Core.Domain.MorphFaceDocument _cleanState;
    private MorphFaceGeometryEditState _cleanGeometryEdits;
    private bool _featuresOnlySavedState;
    private EditorFeatureCategoryViewModel? _selectedCategory;
    private BoneTransformEditorViewModel? _selectedBoneTransform;
    private bool _isDirty;
    private bool _disposed;
    private long _randomisationGeneration;
    private int _morphRandomisationStrength = 50;
    private int _materialRandomisationStrength = 50;
    private bool _randomiseMorphs = true;
    private bool _randomiseMaterials;
    private bool _cursedMode;
    private CursedRandomisationBaseline? _cursedBaseline;
    private readonly IReadOnlySet<int> _morphGeometryLods;
    private readonly IReadOnlyList<MorphFaceEditor.Core.Domain.AssetIdentity?> _preservedOtherMeshes;
    private readonly RandomisationInclusionState _randomisationInclusionState;
    private readonly bool _allowsAttachmentEditing;
    private readonly IHeadEditorUiProfile _metadataCatalog;
    private readonly CustomMaterialWorkspace? _customMaterialWorkspace;
    private readonly IReadOnlyList<CustomMaterialAssignmentOption> _customMaterialOptions;
    private readonly MorphFaceGame? _playerRandomisationGame;
    private IReadOnlyList<TextureCatalogCandidate> _registryTextureCandidates = [];
    private bool _isTextureRegistryAvailable;
    private bool _isTextureRegistryPending;
    private readonly int _localMeshCandidateCount;
    private IReadOnlyList<AttachmentMeshCandidate> _registryAttachmentCandidates = [];
    private IReadOnlyDictionary<int, string> _cleanMaterialAssignments =
        new Dictionary<int, string>();

    public FaceEditorViewModel(
        MorphFaceEditingSession session,
        IHeadEditorUiProfile metadataCatalog,
        MaterialEditingSession materialSession,
        IHdrColorDialogService colorDialog,
        PackageReferenceService references,
        string packagePath,
        IReadOnlyList<MorphFaceEditor.Models.PackageAssetListItem> textureCandidates,
        IReadOnlyList<MorphFaceEditor.Models.PackageAssetListItem> meshCandidates,
        MorphFaceEditor.Core.Domain.AssetIdentity? hairMeshReference,
        IReadOnlyList<MorphFaceEditor.Core.Domain.AssetIdentity?> otherMeshReferences,
        Action<string> reportError,
        string profileKey = "le1-human-male",
        MorphRandomisationCatalog? randomisationCatalog = null,
        Func<int>? randomSeedFactory = null,
        RandomisationInclusionState? randomisationInclusionState = null,
        IReadOnlyList<TextureCatalogCandidate>? registryTextureCandidates = null,
        TextureCatalogProfile? textureCatalogProfile = null,
        bool isTextureRegistryAvailable = false,
        bool ignoresAuthoredGeometry = false,
        bool allowsAttachmentEditing = true,
        CustomMaterialWorkspace? customMaterialWorkspace = null,
        IReadOnlyList<CustomMaterialAssignmentOption>? customMaterialOptions = null,
        string? materialRandomisationProfileKey = null,
        bool previewOnlyAttachments = false,
        MorphFaceGame? playerRandomisationGame = null,
        bool customMaterialRegistryAvailable = false,
        bool isTextureRegistryPending = false,
        PcsavHeadMorphWorkspace? headImportHistory = null)
    {
        _session = session;
        _references = references;
        var scalpMaterials = materialSession.BaseMaterials.Materials.Values
            .Where(material => material.Family == HeadMaterialFamily.Scalp).ToArray();
        _sourceScalpMaterial = scalpMaterials.Length == 1 ? scalpMaterials[0] : null;
        _metadataCatalog = metadataCatalog;
        _customMaterialWorkspace = customMaterialWorkspace;
        HasLimitedCustomMaterials = customMaterialWorkspace is not null && !customMaterialRegistryAvailable;
        _isTextureRegistryAvailable = isTextureRegistryAvailable;
        _isTextureRegistryPending = isTextureRegistryPending;
        _localMeshCandidateCount = meshCandidates.Count;
        _customMaterialOptions = customMaterialOptions ?? [];
        _registryTextureCandidates = registryTextureCandidates ?? [];
        _playerRandomisationGame = playerRandomisationGame;
        _profileKey = profileKey;
        _materialRandomisationProfileKey = materialRandomisationProfileKey ?? profileKey;
        _allowsMorphRandomisation = !ignoresAuthoredGeometry &&
                                    !profileKey.EndsWith("-vorcha", StringComparison.OrdinalIgnoreCase);
        if (!_allowsMorphRandomisation)
        {
            _randomiseMorphs = false;
        }
        _randomisationCatalog = randomisationCatalog ?? MorphRandomisationCatalog.Empty;
        _randomisation = new EditorRandomisationService(_randomisationCatalog);
        _randomSeedFactory = randomSeedFactory ?? Random.Shared.Next;
        _reportError = reportError;
        _randomisationInclusionState = randomisationInclusionState ?? new RandomisationInclusionState();
        _allowsAttachmentEditing = allowsAttachmentEditing;
        _morphGeometryLods = session.Evaluation.Resolution.Features
            .Where(feature => feature.Target is not null)
            .SelectMany(feature => feature.Target!.Lods)
            .Where(lod => lod.Vertices.Count > 0)
            .Select(lod => lod.LodIndex)
            .ToHashSet();
        Features = session.Evaluation.Resolution.Features
            .Where(_ => !ignoresAuthoredGeometry)
            .Select(feature => new MorphFeatureEditorViewModel(
                session,
                MorphFeatureLodMetadata.MarkLodCoverage(
                    metadataCatalog.Describe(feature, sessionCanEdit: true),
                    feature,
                    session.AvailableLodIndices),
                feature.Target is null || feature.Target.BoneOffsets.Count > 0
                    ? null
                    : feature.Target.Lods
                        .Where(lod => lod.Vertices.Count > 0)
                        .Select(lod => lod.LodIndex)
                        .ToHashSet(),
                reportError,
                session.CanEditMorphFeatures))
            .Where(feature => feature.IsVisible)
            .ToArray();
        Bones = ignoresAuthoredGeometry
            ? []
            : session.FinalSkeleton
                .SelectMany(bone => Enumerable.Range(0, 3)
                    .Select(axis => new BoneAxisEditorViewModel(session, bone.BoneName, axis)))
                .ToArray();
        BoneTransforms = Bones
            .GroupBy(axis => axis.BoneName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var axes = group.OrderBy(axis => axis.Axis).ToArray();
                return new BoneTransformEditorViewModel(session, axes[0], axes[1], axes[2]);
            })
            .ToArray();
        _selectedBoneTransform = BoneTransforms.FirstOrDefault(transform => transform.IsAvailable);
        var hairSession = new AssetReferenceEditingSession(hairMeshReference);
        var otherMeshSessions = Enumerable.Range(0, 1)
            .Select(index => new AssetReferenceEditingSession(otherMeshReferences.ElementAtOrDefault(index)))
            .ToArray();
        _preservedOtherMeshes = otherMeshReferences.Skip(1).ToArray();
        _history = new EditorHistory();
        session.History = materialSession.History = hairSession.History = _scalpMaterialSession.History = _history;
        foreach (var other in otherMeshSessions) other.History = _history;
        if (headImportHistory is not null) headImportHistory.History = _history;
        if (customMaterialWorkspace is not null) customMaterialWorkspace.History = _history;
        _undoCommand = new RelayCommand(() =>
        {
            _randomisationGeneration++;
            CancelPendingAttachmentSelections();
            _history.Undo();
        }, () => _history.CanUndo);
        _redoCommand = new RelayCommand(() =>
        {
            _randomisationGeneration++;
            CancelPendingAttachmentSelections();
            _history.Redo();
        }, () => _history.CanRedo);
        _setToDefaultsCommand = new RelayCommand(SetToDefaults, CanSetToDefaults);
        _randomiseCommand = new RelayCommand(
            () => Randomise(GlobalRandomisationScope(), includeCursedBones: true),
            () => CanRandomiseScope(GlobalRandomisationScope(), includeCursedBones: true));
        AttachmentMeshesHeading = previewOnlyAttachments
            ? Loc.Text("loc_VM_faceEditor_attachmentHeadingPreviewOnly")
            : Loc.Text("loc_VM_faceEditor_attachmentHeading");
        HairMesh = new HairMeshEditorViewModel(
            hairSession, meshCandidates, Loc.Text("loc_VM_faceEditor_hairMesh"), 0);
        _hairRandomiseCommand = new RelayCommand(
            () => Randomise(HairRandomisationScope(), includeCursedBones: false),
            CanRandomiseHair);
        HairMesh.SetRandomiseCommand(_hairRandomiseCommand);
        HairMesh.PropertyChanged += OnHairMeshPropertyChanged;
        OtherMeshes = otherMeshSessions
            .Select((otherSession, index) => new HairMeshEditorViewModel(
                otherSession,
                meshCandidates,
                Loc.Text("loc_VM_faceEditor_accessoryMesh"),
                index + 1))
            .ToArray();
        AttachmentMeshes = [HairMesh, .. OtherMeshes];
        Material = new MaterialEditorViewModel(
            materialSession, colorDialog, references, packagePath, textureCandidates, reportError, metadataCatalog,
            registryTextureCandidates, textureCatalogProfile, isTextureRegistryAvailable);
        CustomMaterialSlots = customMaterialWorkspace?.UsedSlots
            .Select(slot => new CustomMaterialSlotEditorViewModel(
                customMaterialWorkspace, slot, customMaterialOptions ?? [], reportError))
            .ToArray() ?? [];
        Categories = BuildCategories();
        _selectedCategory = Categories.FirstOrDefault();
        session.EvaluationChanged += OnEvaluationChanged;
        _history.HistoryChanged += OnHistoryChanged;
        Material.PreviewChanged += OnMaterialPreviewChanged;
        Material.ControlsChanged += OnMaterialControlsChanged;
        _scalpMaterialSession.ValueChanged += OnScalpMaterialSelectionChanged;
        _cleanState = CreateDraft();
        var logProfile = MorphFaceProfileRegistry.CreateDefault().Profiles.FirstOrDefault(profile => profile.Key == profileKey);
        _logIdentity = $"{logProfile?.ExportTag.Trim('[', ']') ?? "CUSTOM"}.{_cleanState.Source.InstancedPath.Split('.').Last()}";
        _logGame = (playerRandomisationGame ?? logProfile?.Game)?.ToString() ?? "UNKNOWN";
        _cleanGeometryEdits = session.CaptureGeometryEdits();
        _cleanMaterialAssignments = CaptureMaterialAssignments();
    }

    private IReadOnlyList<EditorFeatureCategoryViewModel> BuildCategories()
    {
        _subcategoryRandomiseCommands.Clear();
        // Controls and material assignments stay fixed during this category build.
        var eligibleTextures = EligibleMaterialTextureParameters();
        return _metadataCatalog.Categories
            .Select(category => new EditorFeatureCategoryViewModel(
                category,
                Features.Where(value => value.CategoryKey == category.Key).ToArray(),
                Material.Scalars.Where(value => value.CategoryKey == category.Key).ToArray(),
                Material.Vectors.Where(value => value.CategoryKey == category.Key).ToArray(),
                Material.Textures.Where(value => value.CategoryKey == category.Key).ToArray(),
                _metadataCatalog,
                scope =>
                {
                    if (!scope.HasValues ||
                        (scope.MorphFeatures.Count + scope.Scalars.Count + scope.Vectors.Count == 0 &&
                         !scope.Textures.Any(value => eligibleTextures.Contains(value.Name))))
                    {
                        return null;
                    }
                    var command = new RelayCommand(
                        () => Randomise(scope, includeCursedBones: false),
                        () => CanRandomiseScope(scope));
                    _subcategoryRandomiseCommands.Add(command);
                    return command;
                },
                _randomisationInclusionState,
                RaiseRandomisationCanExecuteChanged))
            .ToArray();
    }

    public event EventHandler? PreviewChanged;
    public event EventHandler? MaterialPreviewChanged;
    public event EventHandler? DirtyStateChanged;
    public string AttachmentMeshesHeading { get; }

    public IReadOnlyList<MorphFeatureEditorViewModel> Features { get; }
    public IReadOnlyList<EditorFeatureCategoryViewModel> Categories { get; private set; }
    public IReadOnlyList<CustomMaterialSlotEditorViewModel> CustomMaterialSlots { get; }
    public bool HasCustomMaterialSlots => CustomMaterialSlots.Count > 0;
    public CustomMaterialWorkspace? CustomMaterials => _customMaterialWorkspace;
    public IReadOnlyList<CustomMaterialAssignmentOption> CustomMaterialOptions => _customMaterialOptions;
    public bool CanExportTseMaterials => _customMaterialWorkspace is { } workspace && MeshMaterialInterchange.CanExportTse(workspace);
    public bool CanImportTseMaterials => _customMaterialWorkspace is { } workspace && MeshMaterialInterchange.CanImportTse(workspace);

    public void ApplyMaterialImport(PreparedMaterialImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        CancelPendingAttachmentSelections();
        using (var aggregate = _history.BeginAggregate())
        {
            Material.MergeMaterialData(import.Parameters, import.Textures);
            if (import.PreviewAttachments is { } attachments)
            {
                if (attachments.ApplyHair) HairMesh.SetImportedPreview(attachments.Hair);
                if (attachments.ApplyAccessory && OtherMeshes.Count > 0)
                    OtherMeshes[0].SetImportedPreview(attachments.Accessory);
            }
            aggregate.Commit();
        }
        RefreshDirtyState();
    }
    public EditorFeatureCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value);
    }
    public void SelectCategory(string? categoryKey)
    {
        if (string.IsNullOrWhiteSpace(categoryKey))
        {
            return;
        }
        SelectedCategory = Categories.FirstOrDefault(category =>
                               string.Equals(category.Key, categoryKey, StringComparison.OrdinalIgnoreCase))
                           ?? SelectedCategory;
    }
    public IReadOnlyList<BoneAxisEditorViewModel> Bones { get; }
    public IReadOnlyList<BoneTransformEditorViewModel> BoneTransforms { get; }
    public BoneTransformEditorViewModel? SelectedBoneTransform
    {
        get => _selectedBoneTransform;
        set => SetProperty(ref _selectedBoneTransform, value);
    }
    public MaterialEditorViewModel Material { get; }
    public bool HasLimitedCustomMaterials { get; }
    public bool IsMeshDatabaseUnavailable => !_isTextureRegistryPending && !_isTextureRegistryAvailable;
    public string AttachmentRegistryNotice => Loc.Format("loc_VM_texture_registryChoicesUnavailable", _localMeshCandidateCount);
    private PlayerRuntimeAssetAvailability? _playerReferenceAvailability;

    public void RestrictPlayerReferences(PlayerRuntimeAssetAvailability available)
    {
        _playerReferenceAvailability = available;
        _registryTextureCandidates = PlayerWorkspaceReferencePolicy.SelectRegistryTextures(_registryTextureCandidates, available);
        _registryAttachmentCandidates = PlayerWorkspaceReferencePolicy.SelectRegistryMeshes(_registryAttachmentCandidates, available);
        Material.RestrictPlayerReferences(available);
        foreach (var attachment in AttachmentMeshes) attachment.RestrictPlayerReferences(available);
    }

    public void UpdateRegistryTextureCandidates(
        IReadOnlyList<TextureCatalogCandidate> candidates,
        TextureCatalogProfile profile,
        bool isRegistryAvailable)
    {
        if (_playerReferenceAvailability is { } available)
            candidates = PlayerWorkspaceReferencePolicy.SelectRegistryTextures(candidates, available);
        _registryTextureCandidates = candidates;
        Material.UpdateRegistryCandidates(candidates, profile, isRegistryAvailable);
        _isTextureRegistryAvailable = isRegistryAvailable;
        _isTextureRegistryPending = false;
        OnPropertyChanged(nameof(IsMeshDatabaseUnavailable));
    }
    public void UpdateRegistryAttachmentMeshes(
        IReadOnlyList<AttachmentMeshCandidate> candidates,
        bool isPlayerWorkspace)
    {
        if (_playerReferenceAvailability is { } available)
            candidates = PlayerWorkspaceReferencePolicy.SelectRegistryMeshes(candidates, available);
        _registryAttachmentCandidates = candidates;
        foreach (var attachment in AttachmentMeshes)
            attachment.UpdateRegistryCandidates(candidates, isPlayerWorkspace);
    }
    public HairMeshEditorViewModel HairMesh { get; }
    public IReadOnlyList<HairMeshEditorViewModel> OtherMeshes { get; }
    public IReadOnlyList<HairMeshEditorViewModel> AttachmentMeshes { get; }
    public System.Windows.Input.ICommand UndoCommand => _undoCommand;
    public System.Windows.Input.ICommand RedoCommand => _redoCommand;
    public System.Windows.Input.ICommand SetToDefaultsCommand => _setToDefaultsCommand;
    public System.Windows.Input.ICommand RandomiseCommand => _randomiseCommand;
    internal Task WaitForTextureHistoryWritesAsync() => _history.WaitForTextureWritesAsync();
    public bool CanEditMorphFeatures => _session.CanEditMorphFeatures;
    public bool CanEditBones => _session.CanEditBones;
    public bool CanEditAttachments => _allowsAttachmentEditing &&
                                      _session.GeometryMode != MorphFaceGeometryMode.BaseMeshOnly;
    public bool UsesLiveDeformationPreview => _session.UsesLiveDeformationPreview || CanEditBones;
    /// <summary>
    /// Indicates whether this editor has any visible morph feature controls.
    /// Material-only and detached custom-mesh profiles keep their material
    /// controls while removing the unrelated morph-randomisation affordances.
    /// </summary>
    public bool HasMorphControls => Features.Count > 0;
    public bool CanFixMorph => _session.CanFixMorph;
    public bool HasPendingRepair => _session.HasPendingRepair;
    public string? EditBlockReason => _session.EditBlockReason;
    public MorphEditBlockReason? EditBlock => _session.EditBlock;
    public bool CanRandomise => CanRandomiseScope(GlobalRandomisationScope(), includeCursedBones: true);
    public bool AllowsMorphRandomisation => CanEditMorphFeatures && _allowsMorphRandomisation;
    public bool AllowsMaterialRandomisation =>
        Material.Scalars.Count + Material.Vectors.Count + Material.Textures.Count > 0;
    public bool AllowsCursedRandomisation =>
        AllowsMorphRandomisation || AllowsMaterialRandomisation;
    public bool CursedMode
    {
        get => _cursedMode;
        set
        {
            if (SetProperty(ref _cursedMode, value))
            {
                if (value)
                {
                    RandomiseMorphs = AllowsMorphRandomisation;
                    RandomiseMaterials = true;
                }
                if (!value)
                {
                    _cursedBaseline = null;
                }
                RaiseRandomisationCanExecuteChanged();
            }
        }
    }
    public bool RandomiseMorphs
    {
        get => _randomiseMorphs;
        set
        {
            var allowedValue = AllowsMorphRandomisation && value;
            if (SetProperty(ref _randomiseMorphs, allowedValue)) RaiseRandomisationCanExecuteChanged();
        }
    }
    public bool RandomiseMaterials
    {
        get => _randomiseMaterials;
        set
        {
            if (SetProperty(ref _randomiseMaterials, value)) RaiseRandomisationCanExecuteChanged();
        }
    }
    public int MorphRandomisationStrength
    {
        get => _morphRandomisationStrength;
        set => SetProperty(ref _morphRandomisationStrength, Math.Clamp(value, 0, 100));
    }
    public int MaterialRandomisationStrength
    {
        get => _materialRandomisationStrength;
        set => SetProperty(ref _materialRandomisationStrength, Math.Clamp(value, 0, 100));
    }
    public int RandomisationStrength
    {
        get => MorphRandomisationStrength;
        set => MorphRandomisationStrength = value;
    }
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                DirtyStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
    public MorphFaceEvaluation Evaluation => _session.Evaluation;
    public IReadOnlyList<int> AvailableLodIndices => _session.AvailableLodIndices;
    public MorphMeshFitResult FitMeshPositions(IReadOnlyList<MorphMeshPositionCandidate> candidates) =>
        _session.FitMeshPositions(candidates);
    public bool HasMorphGeometryAtLod(int lodIndex) => _morphGeometryLods.Contains(lodIndex);
    public void SetPreviewLod(int lodIndex)
    {
        var lodHasMorphGeometry = _morphGeometryLods.Contains(lodIndex);
        foreach (var feature in Features)
        {
            feature.SetPreviewLod(lodIndex, lodHasMorphGeometry);
        }
    }
    public void SetExtendedSliders(bool enabled)
    {
        foreach (var feature in Features)
        {
            feature.SetExtendedSliders(enabled);
        }
        foreach (var bone in Bones)
        {
            bone.SetExtendedSliders(enabled);
        }
        Material.SetExtendedSliders(enabled);
    }
    public MorphFaceEditor.Core.Domain.MorphFaceDocument CreateDraft() =>
        _session.CreateDraft(
            HairMesh.Value,
            OtherMeshes.Select(mesh => mesh.Value)
                .Concat(_preservedOtherMeshes)
                .ToArray(),
            Material.CreateOverrides()) with
        {
            ScalpMaterialSelection = _scalpMaterialSession.Value is { } donor && _sourceScalpMaterial is { } target
                ? new MorphFaceEditor.Core.Domain.ScalpMaterialSelection(target.Source, donor)
                : null
        };
    public void FixMorph()
    {
        _session.FixMorph();
        RefreshDirtyState();
    }
    public void ApplyMorphData(MorphFaceEditor.Core.Domain.MorphFaceMorphData data)
    {
        CancelPendingAttachmentSelections();
        _session.ApplyMorphData(data);
        RefreshDirtyState();
    }

    public void ApplyMorphFeatures(MorphFaceEditor.Core.Domain.MorphFaceMorphData data)
    {
        CancelPendingAttachmentSelections();
        _session.ImportFeatures(data.MorphFeatures.ToDictionary(v => v.Name, v => v.Offset, StringComparer.OrdinalIgnoreCase));
        RefreshDirtyState();
    }

    internal async Task<bool> TryImportMatchingHeadAsync(HeadMorphPayload head,
        StandalonePlayerAssetCatalog assets, Func<Task> validate,
        Action<MorphFaceDocument>? imported)
    {
        if (!CanEditAttachments || _preservedOtherMeshes.Count > 0 || head.AccessoryMeshes.Count > 1 ||
            _scalpMaterialSession.Value is not null) return false;
        AssetIdentity? Mesh(string path) => assets.SkeletalMeshes.SingleOrDefault(value =>
            value.InstancedPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        var hair = Mesh(head.HairMesh);
        var accessory = head.AccessoryMeshes.Count == 0 ? null : Mesh(head.AccessoryMeshes[0]);
        if ((!string.IsNullOrWhiteSpace(head.HairMesh) && !head.HairMesh.Equals("None", StringComparison.OrdinalIgnoreCase) && hair is null) ||
            (head.AccessoryMeshes.Count > 0 && accessory is null)) return false;
        var textures = head.MaterialData.Textures.Select(value => value.TextureReference is null ? value : value with
        {
            TextureReference = assets.Textures.SingleOrDefault(asset => asset.InstancedPath.Equals(
                value.TextureReference.InstancedPath, StringComparison.OrdinalIgnoreCase))
        }).ToArray();
        if (textures.Where((value, index) => value.TextureReference is null && head.MaterialData.Textures[index].TextureReference is not null).Any() ||
            new[] { hair, accessory }.OfType<AssetIdentity>().Any(value => _playerReferenceAvailability?.Allows(value) == false))
            return false;
        var applyMorph = _session.PrepareMatchingMorphImport(head.MorphData);
        if (applyMorph is null) return false;
        CancelPendingAttachmentSelections();
        await validate();
        var materialData = head.MaterialData with { Textures = textures };
        var materialTextures = await Material.PrepareMaterialDataAsync(materialData);
        if (_disposed) return false;
        using var aggregate = _history.BeginAggregate();
        applyMorph();
        Material.ApplyMaterialData(materialData, materialTextures);
        HairMesh.SetImportedPreview(hair);
        OtherMeshes[0].SetImportedPreview(accessory);
        _session.ClearCursedBoneOffsets();
        imported?.Invoke(CreateDraft());
        aggregate.Commit();
        RefreshDirtyState();
        return true;
    }
    private void SetToDefaults()
    {
        _randomisationGeneration++;
        CancelPendingAttachmentSelections();
        using var aggregate = _history.BeginAggregate();
        if (CanEditMorphFeatures || CanEditBones) _session.ResetToDefaults();
        _scalpMaterialSession.Set(null);
        Material.ResetToDefaults();
        _session.ClearCursedBoneOffsets();
        aggregate.Commit();
        _cursedBaseline = null;
    }

    private bool CanSetToDefaults() => CanEditMorphFeatures || CanEditBones || AllowsMaterialRandomisation;

    private void CancelPendingAttachmentSelections()
    {
        HairMesh.CancelPendingManualSelection();
        foreach (var other in OtherMeshes) other.CancelPendingManualSelection();
    }

    private async void Randomise(EditorRandomisationScope requested, bool includeCursedBones)
        => await RandomiseCoreAsync(requested, includeCursedBones, propagateErrors: false);

    /// <summary>Runs the same global operation as the editor command and exposes completion to automation.</summary>
    public Task RandomiseAsync()
    {
        if (!CanRandomise) throw Loc.Error(new InvalidOperationException(Loc.Text("loc_VM_faceEditor_noRandomisationScope")));
        return RandomiseCoreAsync(GlobalRandomisationScope(), includeCursedBones: true, propagateErrors: true);
    }

    private async Task RandomiseCoreAsync(EditorRandomisationScope requested, bool includeCursedBones,
        bool propagateErrors)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (requested.InclusionKey is { } key && !_randomisationInclusionState.IsIncluded(key)) return;
            CancelPendingAttachmentSelections();
            requested = CoordinateHairMorphScope(requested);
            var generation = ++_randomisationGeneration;
            if (CursedMode) _cursedBaseline ??= CaptureCursedBaseline();
            var request = CaptureRandomisationRequest(requested, globalRoll: includeCursedBones);
            if (!request.Cursed && request.Morphs.Scope.Count == 0 && !request.HasMaterial) return;
            int? morphSeed = request.Cursed || request.Morphs.Scope.Count > 0 ? _randomSeedFactory() : null;
            int? materialSeed = request.Cursed ? morphSeed : request.HasMaterial ? NextDistinctSeed(morphSeed) : null;
            // Capture the current mesh, scalp references and graph before asynchronous package reads.
            // Relocated copies use the same pairing rule as manual selection and mesh-lock protection.
            if (request.Human is { } human &&
                (request.ResetMaterials && request.MaterialCleanupScope is null ||
                 request.Material.TextureScope.Any(name =>
                     string.Equals(MaterialParameterControlKey.ScopeKey(name), human.ScopeKey,
                         StringComparison.OrdinalIgnoreCase) &&
                     IsHumanScalpTexture(MaterialParameterControlKey.ParameterName(name)))))
                request = request with { Hair = request.Hair with
                    { CurrentMeshIsPaired = await CurrentHairHasPairedScalpAsync(human) } };
            if (generation != _randomisationGeneration) return;
            var prepared = await _randomisation.PrepareAsync(request, morphSeed, materialSeed,
                Material.PrepareRandomisationAsync);
            if (generation != _randomisationGeneration) return;
            if (prepared.NoReadableFamily)
            {
                var warning = Loc.Text("loc_VM_faceEditor_noReadableTextureFamily");
                AppLog.Warning(warning);
                _reportError(warning);
            }
            var playerDetails = request.Player is not null && materialSeed is not null
                ? await PreparePlayerFacialDetailsAsync(request, materialSeed.Value)
                : new Dictionary<string, DecodedTextureAsset?>();
            var playerColours = request.Player is not null && materialSeed is not null &&
                                (!request.Cursed || request.RandomiseMaterials && request.MaterialStrength > 0)
                ? await PreparePlayer2DaAsync(request, playerDetails, materialSeed.Value)
                : null;
            var playerFaceScar = request.Player is not null && request.RandomiseMaterials && materialSeed is not null
                ? await PreparePlayerFaceScarAsync(request, materialSeed.Value)
                : null;
            var playerHair = request.Human is not null && materialSeed is not null
                ? await PrepareHumanHairAsync(request, materialSeed.Value, prepared.RetainHairMesh)
                : null;
            // A changed mesh lock cannot reject only the mesh after accepting its paired scalp/morphs.
            // Reset, replacement controls or a newer roll also supersede all prepared parts together.
            if (generation != _randomisationGeneration || request.Hair.Locked != HairMesh.IsRandomisationLocked) return;
            var material = PreparedMaterialRandomisation.Merge(playerColours, playerFaceScar, playerHair?.Material,
                playerDetails.Count == 0 ? null : PreparedMaterialRandomisation.Empty with
                    { Textures = playerDetails, AppliedTextureFamilies = playerDetails.Count },
                prepared.Material);
            var proposedMorphs = WithoutDonorHairMorphsWhenScalpRolled(prepared.Morphs, playerHair);
            var seed = materialSeed ?? morphSeed ?? 0;
            var morphs = request.Player is null && playerHair is null
                ? WithSelectedScalpHairMorphs(request, proposedMorphs, material, seed)
                : WithHumanHairMorphs(request, proposedMorphs, playerHair, seed);
            morphs = WithExclusivePlayerIconicMorphs(request, morphs, morphSeed ?? materialSeed ?? 0);
            var protectedScalp = request.Hair.CurrentMeshIsPaired && (request.Hair.Locked || prepared.RetainHairMesh)
                ? request.Material.Textures.Keys.Where(name =>
                        string.Equals(MaterialParameterControlKey.ScopeKey(name), request.Human?.ScopeKey,
                            StringComparison.OrdinalIgnoreCase) &&
                        IsHumanScalpTexture(MaterialParameterControlKey.ParameterName(name)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;

            using var aggregate = _history.BeginAggregate();
            if (morphs.Count > 0) _session.SetFeatures(morphs);
            if (request.IncludeBones) ApplyRandomisedBones(request, morphSeed ?? 0);
            if (request.ResetMaterials || material is not null &&
                (!request.Cursed || material.Scalars.Count + material.Vectors.Count + material.Textures.Count > 0))
                Material.ApplyRandomisation(material ?? PreparedMaterialRandomisation.Empty, request.Cursed,
                    protectedScalp, request.MaterialCleanupScope);
            if (playerHair is { } hair)
            {
                switch (hair.Style.MeshAction)
                {
                    case PlayerHairMeshAction.Clear:
                        HairMesh.TrySetRandomisedSelection(null);
                        break;
                    case PlayerHairMeshAction.Set:
                        HairMesh.TrySetRandomisedSelection(hair.Mesh);
                        break;
                }
            }
            aggregate.Commit();
            AppLog.Information(AppLogMessages.Randomisation(request.Cursed, _logIdentity, _logGame,
                request.MorphStrength, request.MaterialStrength, morphSeed, materialSeed) +
                $" MS:{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            var textures = _history.TextureStorageStatistics;
            AppLog.Information($"MEMORY:UNDO-TEXTURES HOT:{textures.HotBytes} PENDING:{textures.PendingBytes} " +
                $"ARCHIVED:{textures.ArchivedBytes} FALLBACK:{textures.FallbackBytes} FAILURES:{textures.StorageFailures} " +
                $"READS:{textures.ArchiveReads} READMS:{textures.ArchiveReadMilliseconds:F0}");
        }
        catch (Exception exception)
        {
            AppLog.Error("RAND:FAILED", exception);
            if (propagateErrors) throw;
            _reportError(Loc.Format("loc_VM_faceEditor_randomisationFailed", AppLog.FilePath));
        }
    }

    private EditorRandomisationRequest CaptureRandomisationRequest(EditorRandomisationScope requested, bool globalRoll)
    {
        var defaults = !CursedMode && RandomiseMaterials ? Material.CapturePendingRandomisationDefaults() : null;
        var player = GetPlayerRandomisationContext();
        var human = GetHumanScalpContext();
        var profiles = RandomiseMaterials ? GetMaterialRandomisationProfiles().Select(context => context with
        {
            InstalledDonors = _randomisationCatalog.ProjectInstalledMaterialDonors(
                context.Key, Material.CanResolveRandomisationTexturePath)
        }).ToArray() : [];
        var morphScope = RandomiseMorphs
            ? requested.MorphFeatures.Where(value => value.IsEditable && IsPlayerRandomisableMorph(value, player) &&
                !IsScalpControlledHairMorph(value, human) &&
                (!HairMesh.IsRandomisationLocked || human is not { } hair || !IsHumanHairMorph(value, hair.Sex)))
                .Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool IsNumericInScope(string name, MaterialParameterKind kind) => CursedMode ||
            player is { } context && string.Equals(Material.ParameterScopeKey(name), context.ScopeKey,
                StringComparison.OrdinalIgnoreCase) && Player2DaParameters.ContainsKey(Material.SourceParameterName(name)) ||
            profiles.Any(context => context.Supports(name, kind) && (kind == MaterialParameterKind.Scalar
                ? context.Profile.Scalars.ContainsKey(Material.SourceParameterName(name))
                : context.Profile.Vectors.ContainsKey(Material.SourceParameterName(name))));
        var scalarScope = requested.Scalars.Where(value => RandomiseMaterials && IsNumericInScope(value.Name, MaterialParameterKind.Scalar))
            .Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vectorScope = requested.Vectors.Where(value => RandomiseMaterials && IsNumericInScope(value.Name, MaterialParameterKind.Vector))
            .Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textureScope = requested.Textures.Where(value => RandomiseMaterials &&
                !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(value.Name) &&
                !IsProtectedPlayerEyeTexture(Material.SourceParameterName(value.Name)))
            .Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A scoped normal roll may neutralise only the controls it owns, including selected
        // Cursed-only numeric controls that have no normal corpus entry to replace them.
        var cleanupScalars = requested.Scalars.Where(_ => RandomiseMaterials).Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cleanupVectors = requested.Vectors.Where(_ => RandomiseMaterials).Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A fully included global roll retains the complete normal cleanup, even for
        // maps excluded from ordinary random selection (such as alignment-safe Player eyes).
        var fullMaterialCleanup = globalRoll &&
            Material.Scalars.All(value => cleanupScalars.Contains(value.Name)) &&
            Material.Vectors.All(value => cleanupVectors.Contains(value.Name)) &&
            Material.Textures.All(value => RandomiseMaterials && requested.Textures.Contains(value));
        var cleanupScope = fullMaterialCleanup ? null : new MaterialRandomisationScope(
            cleanupScalars, cleanupVectors, textureScope);
        var resetMaterials = defaults is not null &&
            (fullMaterialCleanup || cleanupScalars.Count + cleanupVectors.Count + textureScope.Count > 0);
        var includedGroups = Categories.SelectMany(category => category.SliderGroups)
            .Where(group => group.Inclusion?.IsIncluded != false).ToArray();
        var includedMorphs = includedGroups.SelectMany(group => group.MorphFeatures).Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentMorphs = Features.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
        var scalars = Material.Scalars.ToDictionary(value => value.Name,
            value => defaults is not null && (fullMaterialCleanup || cleanupScalars.Contains(value.Name))
                ? defaults.Scalars.GetValueOrDefault(value.Name, value.Value) : value.Value, StringComparer.OrdinalIgnoreCase);
        var vectors = Material.Vectors.ToDictionary(value => value.Name,
            value => defaults is not null && (fullMaterialCleanup || cleanupVectors.Contains(value.Name))
                ? defaults.Vectors.GetValueOrDefault(value.Name, value.Value) : value.Value, StringComparer.OrdinalIgnoreCase);
        return new(_profileKey, CursedMode, RandomiseMorphs, RandomiseMaterials,
            MorphRandomisationStrength, MaterialRandomisationStrength,
            new(currentMorphs, _cursedBaseline?.Features ?? currentMorphs,
                Features.Select(value => new MorphRandomisationFeatureBounds(value.Name, value.Metadata.Minimum, value.Metadata.Maximum)).ToArray(),
                morphScope, Features.Where(value => human is { } context && !IsPlayerRandomisableMorph(value.Name,
                        value.CategoryKey, value.SubcategoryKey, context.Sex))
                    .Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)),
            new(scalars, vectors, _cursedBaseline?.Scalars ?? scalars, _cursedBaseline?.Vectors ?? vectors,
                Material.Textures.ToDictionary(value => value.Name,
                    value => defaults is not null && (fullMaterialCleanup || textureScope.Contains(value.Name))
                        ? defaults.Textures.GetValueOrDefault(value.Name, "None") : value.SourceName, StringComparer.OrdinalIgnoreCase),
                Material.Scalars.Select(value => new MaterialRandomisationScalarBounds(value.Name,
                    value.RandomisationMinimum, value.RandomisationMaximum)).ToArray(),
                scalarScope, vectorScope, textureScope,
                includedGroups.SelectMany(group => group.Scalars).Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), profiles),
            player, human,
            new(HairMesh.IsRandomisationLocked, CanEditAttachments, CanEditMorphFeatures,
                false, requested.InclusionKey is not null,
                Features.Where(value => value.IsEditable && includedMorphs.Contains(value.Name) &&
                    human is { } context && IsHumanHairMorph(value, context.Sex)).Select(value => value.Name).ToArray(),
                _registryTextureCandidates, _registryAttachmentCandidates),
            globalRoll && RandomiseMorphs && CanEditBones, resetMaterials, cleanupScope);
    }

    private void ApplyRandomisedBones(EditorRandomisationRequest request, int seed)
    {
        // Contributions belong to the same bone payload, so replay also restores
        // the amount a later normal roll must subtract.
        var clean = _session.FinalSkeleton.Select(bone => bone with
        {
            Translation = bone.Translation - _session.CursedBoneOffsets.GetValueOrDefault(bone.BoneName)
        }).ToArray();
        var bones = request.Cursed
            ? CursedMorphRandomiser.CreateExtrasProposal(clean, new Dictionary<string, float>(),
                new Dictionary<string, System.Numerics.Vector4>(), request.MorphStrength, seed).BoneValues
            : clean;
        var offsets = new Dictionary<string, System.Numerics.Vector3>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bone, baseline) in bones.Zip(clean))
        {
            var offset = bone.Translation - baseline.Translation;
            if (offset != System.Numerics.Vector3.Zero) offsets[bone.BoneName] = offset;
        }
        var affected = bones.Where(bone => request.Cursed || _session.CursedBoneOffsets.ContainsKey(bone.BoneName)).ToArray();
        _session.SetRandomisedBones(affected, offsets);
    }

    private int NextDistinctSeed(int? otherSeed)
    {
        var seed = _randomSeedFactory();
        return otherSeed is not null && seed == otherSeed.Value ? unchecked(seed + 1) : seed;
    }

    // Normal Player rolls preserve alignment-safe eye maps. Cursed deliberately rolls the whole eye family.
    private bool IsProtectedPlayerEyeTexture(string name) =>
        !CursedMode && (_playerRandomisationGame is MorphFaceGame.LE2 or MorphFaceGame.LE3 &&
            name.Contains("EYE", StringComparison.OrdinalIgnoreCase));

    private static bool IsPlayerRandomisableMorph(
        MorphFeatureEditorViewModel feature,
        (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey)? player)
        => IsPlayerRandomisableMorph(feature.Name, feature.CategoryKey,
            feature.SubcategoryKey, player?.Sex);

    internal static bool IsPlayerRandomisableMorph(
        string name, string categoryKey, string subcategoryKey, PlayerHairSex? playerSex)
    {
        if (playerSex is null ||
            !categoryKey.Equals("facial-structure", StringComparison.OrdinalIgnoreCase) ||
            !subcategoryKey.Equals("character", StringComparison.OrdinalIgnoreCase))
            return true;
        return playerSex == PlayerHairSex.Male
            ? name.Equals("eastwood", StringComparison.OrdinalIgnoreCase)
            : name.Equals("iconic", StringComparison.OrdinalIgnoreCase) ||
              name.Equals("race_iconic", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, float> WithoutDonorHairMorphsWhenScalpRolled(
        IReadOnlyDictionary<string, float> proposed,
        (PreparedMaterialRandomisation Material, PlayerHairStyleOption Style,
            MorphFaceEditor.Core.Domain.AssetIdentity? Mesh)? playerHair)
    {
        if (playerHair is not { Style.Sex: PlayerHairSex.Male }) return proposed;
        return proposed.Where(value => !HumanMaleHairScalpPolicy.IsHairMorph(value.Key))
            .ToDictionary(value => value.Key, value => value.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<string, float> WithExclusivePlayerIconicMorphs(
        EditorRandomisationRequest request, IReadOnlyDictionary<string, float> proposed,
        int seed)
    {
        if (!request.RandomiseMorphs || request.Player is not { Sex: PlayerHairSex.Female }) return proposed;
        const string iconic = "iconic";
        const string raceIconic = "race_iconic";
        var currentIconic = request.Morphs.Current.GetValueOrDefault(iconic);
        var currentRaceIconic = request.Morphs.Current.GetValueOrDefault(raceIconic);
        return ResolveExclusivePlayerIconicMorphs(proposed, currentIconic, currentRaceIconic, seed);
    }

    internal static IReadOnlyDictionary<string, float> ResolveExclusivePlayerIconicMorphs(
        IReadOnlyDictionary<string, float> proposed, float currentIconic,
        float currentRaceIconic, int seed)
    {
        const string iconic = "iconic";
        const string raceIconic = "race_iconic";
        var proposedIconic = proposed.TryGetValue(iconic, out var iconicProposal);
        var proposedRaceIconic = proposed.TryGetValue(raceIconic, out var raceIconicProposal);
        if (!proposedIconic && !proposedRaceIconic) return proposed;
        var iconicValue = proposedIconic ? iconicProposal : currentIconic;
        var raceIconicValue = proposedRaceIconic ? raceIconicProposal : currentRaceIconic;
        if (iconicValue <= 0f || raceIconicValue <= 0f) return proposed;
        var result = new Dictionary<string, float>(proposed, StringComparer.OrdinalIgnoreCase);
        if (proposedIconic && proposedRaceIconic)
        {
            if (new Random(unchecked(seed ^ 0x49434F4E)).Next(2) == 0)
                result[raceIconic] = 0f;
            else result[iconic] = 0f;
        }
        else if (proposedIconic) result[iconic] = 0f;
        else result[raceIconic] = 0f;
        return result;
    }

    private EditorRandomisationScope GlobalRandomisationScope() => new(
        Categories.SelectMany(category => category.SliderGroups)
            .Where(group => group.Inclusion?.IsIncluded != false)
            .SelectMany(group => group.MorphFeatures).Distinct().ToArray(),
        Categories.SelectMany(category => category.SliderGroups)
            .Where(group => group.Inclusion?.IsIncluded != false)
            .SelectMany(group => group.Scalars).Distinct().ToArray(),
        Categories.SelectMany(category => category.ColourGroups)
            .Where(group => group.Inclusion?.IsIncluded != false)
            .SelectMany(group => group.Values).Distinct().ToArray(),
        Categories.Where(category => category.TextureInclusion?.IsIncluded != false)
            .SelectMany(category => category.Textures).Distinct().ToArray());

    private EditorRandomisationScope HairRandomisationScope()
    {
        var context = GetHumanScalpContext();
        var textures = context is { } human
            ? GlobalRandomisationScope().Textures.Where(value =>
                string.Equals(Material.ParameterScopeKey(value.Name), human.ScopeKey,
                    StringComparison.OrdinalIgnoreCase) &&
                IsHumanScalpTexture(Material.SourceParameterName(value.Name))).ToArray()
            : [];
        return new EditorRandomisationScope([], [], [], textures);
    }

    private static bool IsScalpControlledHairMorph(MorphFeatureEditorViewModel feature,
        (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey)? context) =>
        context is { Sex: PlayerHairSex.Male } && HumanMaleHairScalpPolicy.IsHairMorph(feature.Name);

    private EditorRandomisationScope CoordinateHairMorphScope(EditorRandomisationScope scope)
    {
        var context = GetHumanScalpContext();
        if (scope.MorphFeatures.Count == 0 || scope.Scalars.Count + scope.Vectors.Count + scope.Textures.Count > 0 ||
            !scope.MorphFeatures.All(feature => IsScalpControlledHairMorph(feature, context))) return scope;
        // HMM hair-only slider buttons roll the scalp family, which owns the
        // necessary hair geometry changes even when general morph rolls are off.
        var hair = HairRandomisationScope();
        return hair with { Textures = HairMesh.IsRandomisationLocked ? [] : hair.Textures,
            InclusionKey = scope.InclusionKey };
    }

    private bool CanRandomiseHair()
    {
        if (!RandomiseMaterials || !CanEditAttachments || HairMesh.IsRandomisationLocked ||
            GetHumanScalpContext() is not { } context) return false;
        var scope = HairRandomisationScope();
        var required = new[] { "HED_Scalp_Diff", "HED_Scalp_Norm", "HED_Scalp_Spec", "HED_Tang" }
            .Select(name => Material.FindControlName(context.ScopeKey, name, MaterialParameterKind.Texture))
            .Where(name => name is not null).ToArray();
        return required.Length >= 2 && required.All(name => scope.Textures.Any(value =>
            value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) && CanRandomiseScope(scope);
    }

    private bool CanRandomiseHumanHair(EditorRandomisationScope scope,
        (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey)? context)
    {
        if (!_isTextureRegistryAvailable || context is not { } human || scope.Textures.Count < 2)
            return false;
        var required = new[] { "HED_Scalp_Diff", "HED_Scalp_Norm", "HED_Scalp_Spec", "HED_Tang" }
            .Select(name => Material.FindControlName(human.ScopeKey, name, MaterialParameterKind.Texture))
            .Where(name => name is not null).ToArray();
        if (required.Length < 2 || !required.All(name => scope.Textures.Any(value =>
                value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))) return false;
        // Reviewed hairstyles form a separate catalogue; they need no ordinary
        // corpus texture variation. Resolve metadata only, without loading assets.
        return PlayerHairStyleCatalog.Resolve(human.Game, human.Sex,
            _registryTextureCandidates, _registryAttachmentCandidates,
            HairMesh.IsRandomisationLocked || !CanEditAttachments).Count > 0;
    }

    private static bool IsHumanHairMorph(MorphFeatureEditorViewModel feature, PlayerHairSex sex) =>
        sex == PlayerHairSex.Male
            ? HumanMaleHairScalpPolicy.IsHairMorph(feature.Name)
            : feature.CategoryKey.Equals("facial-structure", StringComparison.OrdinalIgnoreCase) &&
              feature.SubcategoryKey.Equals("hair", StringComparison.OrdinalIgnoreCase);

    private bool CanRandomiseScope(EditorRandomisationScope scope, bool includeCursedBones = false)
    {
        if (scope.InclusionKey is { } key && !_randomisationInclusionState.IsIncluded(key)) return false;
        scope = CoordinateHairMorphScope(scope);
        var humanContext = GetHumanScalpContext();
        var playerContext = GetPlayerRandomisationContext();
        var hasMorph = CanEditMorphFeatures && RandomiseMorphs && scope.MorphFeatures.Any(value =>
            value.IsEditable && IsPlayerRandomisableMorph(value, playerContext) &&
            !IsScalpControlledHairMorph(value, humanContext) &&
            (!HairMesh.IsRandomisationLocked || humanContext is not { } human ||
             !IsHumanHairMorph(value, human.Sex)));
        var hasRawNumericMaterial = RandomiseMaterials &&
                                    (scope.Scalars.Count + scope.Vectors.Count > 0);
        var materialProfiles = GetMaterialRandomisationProfiles();
        var eligibleTextures = EligibleMaterialTextureParameters(materialProfiles);
        var hasPlayerPalette = RandomiseMaterials && playerContext is { } paletteContext &&
            scope.Scalars.Select(value => value.Name)
                .Concat(scope.Vectors.Select(value => value.Name))
                .Concat(scope.Textures.Select(value => value.Name))
                .Any(name => string.Equals(Material.ParameterScopeKey(name),
                                 paletteContext.ScopeKey, StringComparison.OrdinalIgnoreCase) &&
                             Player2DaParameters.ContainsKey(Material.SourceParameterName(name)));
        var hasEligibleTexture = RandomiseMaterials &&
                                 scope.Textures.Any(value => eligibleTextures.Contains(value.Name)) &&
                                 materialProfiles.Count > 0;
        var hasCursedBones = includeCursedBones && CursedMode && RandomiseMorphs && CanEditBones;
        if (CursedMode) return hasMorph || hasCursedBones || hasRawNumericMaterial ||
                               hasEligibleTexture || hasPlayerPalette ||
                               RandomiseMaterials && CanRandomiseHumanHair(scope, humanContext);
        var hasEligibleMorph = hasMorph && _randomisationCatalog.HasDonors(_profileKey);
        var hasEligibleMaterial = RandomiseMaterials && materialProfiles.Count > 0 &&
            (scope.Scalars.Any(value => materialProfiles.Any(profile =>
                 profile.Supports(value.Name, MaterialParameterKind.Scalar) &&
                 profile.Profile.Scalars.ContainsKey(Material.SourceParameterName(value.Name)))) ||
             scope.Vectors.Any(value => materialProfiles.Any(profile =>
                 profile.Supports(value.Name, MaterialParameterKind.Vector) &&
                 profile.Profile.Vectors.ContainsKey(Material.SourceParameterName(value.Name)))) ||
             scope.Textures.Any(value => eligibleTextures.Contains(value.Name)));
        return hasEligibleMorph || hasEligibleMaterial || hasPlayerPalette;
    }

    private void RaiseRandomisationCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanRandomise));
        _randomiseCommand.RaiseCanExecuteChanged();
        _hairRandomiseCommand.RaiseCanExecuteChanged();
        foreach (var command in _subcategoryRandomiseCommands) command.RaiseCanExecuteChanged();
    }

    private CursedRandomisationBaseline CaptureCursedBaseline() => new(
        Features.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase),
        Material.Scalars.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase),
        Material.Vectors.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase));

    private sealed record CursedRandomisationBaseline(
        IReadOnlyDictionary<string, float> Features,
        IReadOnlyDictionary<string, float> Scalars,
        IReadOnlyDictionary<string, System.Numerics.Vector4> Vectors);

    public async Task ApplyMaterialDataAsync(MorphFaceEditor.Core.Domain.MorphFaceMaterialData data)
    {
        CancelPendingAttachmentSelections();
        await Material.ApplyDataAsync(data);
        RefreshDirtyState();
    }

    public async Task<IReadOnlyList<string>> MergeMaterialDataAsync(
        MorphFaceEditor.Core.Domain.MorphFaceMaterialData data)
    {
        var prepared = await Material.PrepareMaterialImportAsync(data);
        ApplyMaterialImport(prepared);
        return prepared.Warnings;
    }

    private void OnEvaluationChanged(object? sender, EventArgs e)
    {
        foreach (var feature in Features)
        {
            feature.SetSessionCanEdit(CanEditMorphFeatures);
            feature.Refresh();
        }
        foreach (var bone in Bones)
        {
            bone.Refresh();
        }
        foreach (var transform in BoneTransforms)
        {
            transform.RefreshAvailability();
        }
        if (SelectedBoneTransform is null || !SelectedBoneTransform.IsAvailable)
        {
            SelectedBoneTransform = BoneTransforms.FirstOrDefault(transform => transform.IsAvailable);
        }
        OnPropertyChanged(nameof(CanEditMorphFeatures));
        OnPropertyChanged(nameof(CanEditBones));
        OnPropertyChanged(nameof(UsesLiveDeformationPreview));
        OnPropertyChanged(nameof(CanFixMorph));
        OnPropertyChanged(nameof(HasPendingRepair));
        OnPropertyChanged(nameof(AllowsMorphRandomisation));
        OnPropertyChanged(nameof(AllowsCursedRandomisation));
        _setToDefaultsCommand.RaiseCanExecuteChanged();
        RaiseRandomisationCanExecuteChanged();
        RefreshDirtyState();
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        _undoCommand.RaiseCanExecuteChanged();
        _redoCommand.RaiseCanExecuteChanged();
        RefreshDirtyState();
    }

    private IReadOnlyDictionary<int, string> CaptureMaterialAssignments() =>
        _customMaterialWorkspace?.Assignments.ToDictionary(
            value => value.Slot.MaterialIndex,
            value => value.Option.Id) ?? new Dictionary<int, string>();

    private bool MaterialAssignmentsMatchCleanState()
    {
        var current = CaptureMaterialAssignments();
        return current.Count == _cleanMaterialAssignments.Count && current.All(value =>
            _cleanMaterialAssignments.TryGetValue(value.Key, out var clean) &&
            string.Equals(clean, value.Value, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<MaterialRandomisationProfileContext> GetMaterialRandomisationProfiles()
    {
        if (_customMaterialWorkspace is null)
        {
            var profile = _randomisationCatalog.GetMaterialProfile(_materialRandomisationProfileKey);
            return profile is not null && _randomisationCatalog.HasMaterialDonors(_materialRandomisationProfileKey)
                ? [new MaterialRandomisationProfileContext(_materialRandomisationProfileKey, profile, null, null)]
                : [];
        }
        return _customMaterialWorkspace.Assignments
            .Where(value => !string.IsNullOrWhiteSpace(value.Option.RandomisationProfileKey))
            .Select(value => new
            {
                Key = value.Option.RandomisationProfileKey!,
                ScopeKey = value.Option.EffectiveParameterScopeKey,
                value.Option.Template,
                value.Option.Id
            })
            .DistinctBy(value => $"{value.ScopeKey}|{value.Key}|{value.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(value => new
            {
                value.Key,
                value.ScopeKey,
                value.Template,
                Profile = _randomisationCatalog.GetMaterialProfile(value.Key)
            })
            .Where(value => value.Profile is not null && _randomisationCatalog.HasMaterialDonors(value.Key))
            .Select(value => new MaterialRandomisationProfileContext(
                value.Key, value.Profile!, value.ScopeKey, value.Template))
            .ToArray();
    }

    private (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey)? GetPlayerRandomisationContext()
    {
        if (_playerRandomisationGame is not { } game) return null;
        if (_customMaterialWorkspace is null)
        {
            if (_profileKey.EndsWith("-human-male", StringComparison.OrdinalIgnoreCase))
                return (game, PlayerHairSex.Male, null);
            if (_profileKey.EndsWith("-human-female", StringComparison.OrdinalIgnoreCase))
                return (game, PlayerHairSex.Female, null);
            return null;
        }

        var playerSkin = _customMaterialWorkspace.Assignments.FirstOrDefault(value =>
            value.Option.Family == HeadMaterialFamily.Skin &&
            (value.Option.Id.StartsWith($"{game}:human-male:", StringComparison.OrdinalIgnoreCase) ||
             value.Option.Id.StartsWith($"{game}:human-female:", StringComparison.OrdinalIgnoreCase)));
        if (playerSkin is null) return null;
        var sex = playerSkin.Option.Id.StartsWith($"{game}:human-male:", StringComparison.OrdinalIgnoreCase)
            ? PlayerHairSex.Male
            : PlayerHairSex.Female;
        return (game, sex, playerSkin.Option.EffectiveParameterScopeKey);
    }

    private (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey)? GetHumanScalpContext()
    {
        var player = GetPlayerRandomisationContext();
        if (player is not null) return player;
        if (_customMaterialWorkspace is not null) return null;
        return HeadProfileIdentity.TryParseHumanProfile(_profileKey, out var game, out var isMale)
            ? (game, isMale ? PlayerHairSex.Male : PlayerHairSex.Female, null)
            : null;
    }

    public async Task<PreparedManualHairScalpPair?> PrepareManualHairScalpPairAsync(
        MorphFaceEditor.Core.Domain.AssetIdentity mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (GetHumanScalpContext() is not { } context) return null;
        var diffuseControl = Material.FindControlName(context.ScopeKey, "HED_Scalp_Diff",
            MaterialParameterKind.Texture);
        var normalControl = Material.FindControlName(context.ScopeKey, "HED_Scalp_Norm",
            MaterialParameterKind.Texture);
        if (diffuseControl is null || normalControl is null) return null;
        var style = await ResolveHairScalpStyleAsync(mesh, DedicatedHairScalpStyles(context));
        if (style is null) return null;
        var scopedScalp = style.ScalpTextures
            .Select(value => new
            {
                Name = Material.FindControlName(context.ScopeKey, value.Key, MaterialParameterKind.Texture),
                value.Value
            })
            .Where(value => value.Name is not null)
            .ToDictionary(value => value.Name!, value => value.Value,
                StringComparer.OrdinalIgnoreCase);
        if (!scopedScalp.ContainsKey(diffuseControl) || !scopedScalp.ContainsKey(normalControl))
            return null;
        var requiresCc = RequiresCcScalpMaterial(style, context.Game);
        if (!requiresCc && PlayerHairStyleCatalog.MatchesScalpTextures(style, CurrentScalpTextureReferences(context)))
            return null;
        var prepared = await Material.PrepareRandomisationAsync(
            new Dictionary<string, float>(),
            new Dictionary<string, System.Numerics.Vector4>(),
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                // Share the scalp family's required Diff/Norm policy. Mask and Tang are
                // optional on some Player materials and should not suppress the prompt.
                ["manual|human-scalp"] = scopedScalp
            }, context.ScopeKey);
        if (prepared.FailedTextureFamilySignatures.Count > 0 ||
            !prepared.Textures.ContainsKey(diffuseControl) ||
            !prepared.Textures.ContainsKey(normalControl)) return null;
        if (requiresCc && _sourceScalpMaterial is null)
            throw new InvalidOperationException("The head must have one identifiable scalp material before switching its graph.");
        return new PreparedManualHairScalpPair(prepared, style.Id)
        {
            RequiresCcScalpMaterial = requiresCc,
            FemaleScalp = context.Sex == PlayerHairSex.Female
        };
    }

    public void ApplyManualHairScalpPair(PreparedManualHairScalpPair prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        using var aggregate = _history.BeginAggregate();
        ApplyManualHairScalpPairValues(prepared);
        aggregate.Commit();
    }

    public void CommitManualHairSelection(HairMeshOption selection, long generation,
        PreparedManualHairScalpPair? prepared)
    {
        if (!HairMesh.IsManualSelectionCurrent(selection, generation)) return;
        using var aggregate = _history.BeginAggregate();
        if (!HairMesh.CommitManualSelection(selection, generation)) return;
        if (prepared is not null) ApplyManualHairScalpPairValues(prepared);
        aggregate.Commit();
    }

    private void ApplyManualHairScalpPairValues(PreparedManualHairScalpPair prepared)
    {
        if (prepared.RequiresCcScalpMaterial &&
            prepared.ScalpMaterial?.PreviewModel != HumanMaterialPreviewModel.CcScalp)
            throw new InvalidOperationException("The matching CC scalp material has not been prepared.");
        if (prepared.RequiresCcScalpMaterial && prepared.ScalpMaterial is { } scalp)
        {
            _scalpMaterialDonors[scalp.Key] = scalp;
            _scalpMaterialSession.Set(scalp.Source);
        }
        Material.ApplyPreparedValues(prepared.Material);
        if (CanEditMorphFeatures && GetHumanScalpContext() is { Sex: PlayerHairSex.Male })
            _session.SetFeatures(Features.Where(value => value.Metadata.IsEditable &&
                    HumanMaleHairScalpPolicy.IsHairMorph(value.Name))
                .ToDictionary(value => value.Name, _ => 0f, StringComparer.OrdinalIgnoreCase));
    }

    private IReadOnlyDictionary<string, AssetIdentity?> CurrentScalpTextureReferences(
        (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey) context) =>
        Material.Textures.Where(value =>
                string.Equals(Material.ParameterScopeKey(value.Name), context.ScopeKey, StringComparison.OrdinalIgnoreCase) &&
                IsHumanScalpTexture(Material.SourceParameterName(value.Name)))
            .ToDictionary(value => Material.SourceParameterName(value.Name),
                value => Material.GetEffectiveTextureReference(value.Name), StringComparer.OrdinalIgnoreCase);

    private bool RequiresCcScalpMaterial(PlayerHairStyleOption style, MorphFaceGame game) =>
        _customMaterialWorkspace is null && game == MorphFaceGame.LE3 &&
        style.Id is "hmm-short-afro" or "hmf-short-afro" &&
        !(_sourceScalpMaterial is { } source && Material.Materials.Materials.TryGetValue(source.Key, out var current) &&
          current.PreviewModel == HumanMaterialPreviewModel.CcScalp);

    private PlayerHairStyleOption[] DedicatedHairScalpStyles(
        (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey) context) =>
        PlayerHairStyleCatalog.Resolve(context.Game, context.Sex,
                _registryTextureCandidates, _registryAttachmentCandidates, includeManualPairedVariants: true)
            .Where(style => style.HasDedicatedScalpPair).ToArray();

    private async Task<PlayerHairStyleOption?> ResolveHairScalpStyleAsync(
        AssetIdentity mesh, IReadOnlyList<PlayerHairStyleOption> styles)
    {
        var canonical = styles.FirstOrDefault(style => PlayerHairStyleCatalog.MatchesDedicatedScalpPair(style, mesh));
        if (canonical is not null) return canonical;
        var candidates = styles.SelectMany(style => style.MeshVariants).Distinct().ToArray();
        if (candidates.Length == 0) return null;
        var matches = await _references.FindEquivalentSkeletalMeshesAsync(mesh, candidates);
        var matchedStyles = styles.Where(style => style.MeshVariants.Any(matches.Contains)).ToArray();
        // Equivalent geometry can be shared by styles with different scalp requirements.
        // An ambiguous match is insufficient evidence to offer a particular scalp switch.
        return matchedStyles.Length == 1 ? matchedStyles[0] : null;
    }

    private async Task<bool> CurrentHairHasPairedScalpAsync(
        (MorphFaceGame Game, PlayerHairSex Sex, string? ScopeKey) context)
    {
        if (HairMesh.Value is not { } mesh) return false;
        var textures = CurrentScalpTextureReferences(context);
        var styles = DedicatedHairScalpStyles(context);
        var pairedStyles = styles.Where(style =>
            PlayerHairStyleCatalog.MatchesScalpTextures(style, textures) &&
            !RequiresCcScalpMaterial(style, context.Game)).ToArray();
        var resolved = await ResolveHairScalpStyleAsync(mesh, styles);
        return resolved is not null && pairedStyles.Contains(resolved);
    }

    private void OnScalpMaterialSelectionChanged(object? sender, EventArgs e)
    {
        if (_sourceScalpMaterial is not { } original) return;
        var selected = _scalpMaterialSession.Value is { } donor
            ? _scalpMaterialDonors[MaterialIdentityKey.Create(donor)]
            : original;
        Material.ReplaceScalpMaterial(original.Key, selected);
    }

    private async Task<PreparedMaterialRandomisation?> PreparePlayer2DaAsync(
        EditorRandomisationRequest request, IReadOnlyDictionary<string, DecodedTextureAsset?> playerDetails, int seed)
    {
        var context = request.Player!.Value;
        var input = request.Material;
        var allowed = input.ScalarScope.Concat(input.VectorScope).Concat(input.TextureScope)
            .Where(name => string.Equals(Material.ParameterScopeKey(name), context.ScopeKey,
                StringComparison.OrdinalIgnoreCase))
            .Select(Material.SourceParameterName)
            .Where(Player2DaParameters.ContainsKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed.Count == 0) return null;
        var palettes = allowed.Select(name => Player2DaParameters[name])
            .Where(palette => context.Sex == PlayerHairSex.Female ||
                palette is not (Player2DaPalette.Blush or Player2DaPalette.EyeMakeup or Player2DaPalette.Lips))
            .ToHashSet();
        if (palettes.Count == 0) return null;

        var current = new Dictionary<string, Player2DaValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in input.Scalars.Where(value =>
                     string.Equals(Material.ParameterScopeKey(value.Key), context.ScopeKey, StringComparison.OrdinalIgnoreCase) &&
                     Player2DaParameters.ContainsKey(Material.SourceParameterName(value.Key))))
            current[Material.SourceParameterName(name)] = Player2DaValue.FromScalar(value);
        foreach (var (name, value) in input.Vectors.Where(value =>
                     string.Equals(Material.ParameterScopeKey(value.Key), context.ScopeKey, StringComparison.OrdinalIgnoreCase) &&
                     Player2DaParameters.ContainsKey(Material.SourceParameterName(value.Key))))
            current[Material.SourceParameterName(name)] = Player2DaValue.FromVector(value);

        bool HasTexture(string parameter)
        {
            var name = input.FindControlName(context.ScopeKey, parameter, MaterialParameterKind.Texture);
            if (name is null) return false;
            if (playerDetails.TryGetValue(name, out var selected)) return selected is not null;
            return !input.Textures[name].Equals("None", StringComparison.OrdinalIgnoreCase);
        }
        var result = Player2DaRandomisationPolicy.Roll(new Player2DaRandomisationRequest(
                palettes, allowed, current,
                HasBrowTexture: HasTexture("HED_Brow"),
                HasBeardTexture: context.Sex == PlayerHairSex.Male && HasTexture("HED_Addn")),
            new Random(unchecked(seed ^ 0x32444150)));
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectors = new Dictionary<string, System.Numerics.Vector4>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parameter, value) in result.Values)
        {
            var kind = value.Kind switch
            {
                Player2DaValueKind.Scalar => MaterialParameterKind.Scalar,
                Player2DaValueKind.Vector => MaterialParameterKind.Vector,
                _ => MaterialParameterKind.Texture
            };
            var controlName = input.FindControlName(context.ScopeKey, parameter, kind);
            if (controlName is null) continue;
            switch (value.Kind)
            {
                case Player2DaValueKind.Scalar:
                    scalars[controlName] = value.Scalar;
                    break;
                case Player2DaValueKind.Vector:
                    vectors[controlName] = value.Vector;
                    break;
                case Player2DaValueKind.Texture when value.Texture is { } objectName:
                {
                    var asset = ResolvePlayer2DaTextureAsset(
                        request.Hair.Textures, context.Game, objectName);
                    if (asset is null) break;
                    var control = Material.Textures.First(value =>
                        value.Name.Equals(controlName, StringComparison.OrdinalIgnoreCase));
                    try { textures[controlName] = await control.ResolveReferenceAsync(asset); }
                    catch (Exception exception)
                    {
                        AppLog.Warning($"PLAYER:LASH-SKIP {objectName} REASON:unresolved {exception.Message}");
                    }
                    break;
                }
            }
        }
        if (request.Cursed && (scalars.Count > 0 || vectors.Count > 0))
        {
            var bounds = input.ScalarBounds.Where(value => input.ScalarScope.Contains(value.Name) && scalars.ContainsKey(value.Name))
                .ToArray();
            var variation = CursedMorphRandomiser.CreateExtrasProposal(
                [], scalars, vectors, request.MaterialStrength,
                unchecked(seed ^ 0x32444143), bounds);
            scalars = new Dictionary<string, float>(variation.ScalarValues,
                StringComparer.OrdinalIgnoreCase);
            vectors = variation.VectorValues.ToDictionary(value => value.Key,
                value => new System.Numerics.Vector4(value.Value.X, value.Value.Y,
                    value.Value.Z, vectors[value.Key].W), StringComparer.OrdinalIgnoreCase);
        }
        return scalars.Count + vectors.Count + textures.Count == 0 ? null :
            new PreparedMaterialRandomisation(scalars, vectors, textures,
                textures.Count, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    internal static MorphFaceEditor.Core.Domain.AssetIdentity? ResolvePlayer2DaTextureAsset(
        IReadOnlyList<TextureCatalogCandidate> candidates,
        MorphFaceGame game,
        string objectName)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        var identities = candidates
            .Where(candidate => candidate.Game == game &&
                candidate.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase) &&
                candidate.IsEligibleOfficialDefault &&
                candidate.EffectiveOccurrence.Origin is
                    TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
            .GroupBy(candidate => candidate.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(candidate => candidate.EffectiveOccurrence.MountPriority)
                .ThenBy(candidate => candidate.EffectiveOccurrence.PackagePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToArray();
        if (identities.Length != 1) return null;

        var selected = identities[0];
        return new MorphFaceEditor.Core.Domain.AssetIdentity(
            selected.EffectiveOccurrence.PackagePath,
            selected.InstancedPath,
            selected.EffectiveOccurrence.ExportUIndex,
            "Texture2D");
    }

    private async Task<IReadOnlyDictionary<string, DecodedTextureAsset?>> PreparePlayerFacialDetailsAsync(
        EditorRandomisationRequest request, int seed)
    {
        var context = request.Player!.Value;
        var requestedTextureNames = request.Material.TextureScope;
        var controls = Material.Textures
            .Where(value => requestedTextureNames.Contains(value.Name) &&
                            string.Equals(Material.ParameterScopeKey(value.Name), context.ScopeKey,
                                StringComparison.OrdinalIgnoreCase) &&
                            Material.SourceParameterName(value.Name) is "HED_Brow" or "HED_Addn")
            .ToArray();
        var current = controls.ToDictionary(value => Material.SourceParameterName(value.Name),
            value => request.Material.Textures[value.Name], StringComparer.OrdinalIgnoreCase);
        var profileKey = $"{context.Game.ToString().ToLowerInvariant()}-human-" +
                         (context.Sex == PlayerHairSex.Male ? "male" : "female");
        var proposal = PlayerFacialDetailTexturePolicy.CreateProposal(
            profileKey, current, request.Hair.Textures, seed);
        var prepared = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parameter, selection) in proposal)
        {
            var control = controls.FirstOrDefault(value =>
                Material.SourceParameterName(value.Name).Equals(parameter, StringComparison.OrdinalIgnoreCase));
            if (control is null) continue;
            try
            {
                prepared[control.Name] = await control.ResolveReferenceAsync(selection.Asset);
            }
            catch (Exception exception)
            {
                AppLog.Warning($"PLAYER:DETAIL-SKIP {selection.Asset.InstancedPath} REASON:unresolved {exception.Message}");
            }
        }
        return prepared;
    }

    private async Task<PreparedMaterialRandomisation?> PreparePlayerFaceScarAsync(EditorRandomisationRequest request, int seed)
    {
        var context = request.Player!.Value;
        var input = request.Material;
        var requestedTextureNames = input.TextureScope;
        var requestedScalarNames = input.ScalarScope;
        var profileKey = $"{context.Game.ToString().ToLowerInvariant()}-human-" +
                         (context.Sex == PlayerHairSex.Male ? "male" : "female");
        var textures = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var appliedFamilies = 0;

        var diffuseName = input.FindControlName(context.ScopeKey, "HED_Diff",
            MaterialParameterKind.Texture);
        var normalName = input.FindControlName(context.ScopeKey, "HED_Norm",
            MaterialParameterKind.Texture);
        if (diffuseName is not null && normalName is not null &&
            requestedTextureNames.Contains(diffuseName) && requestedTextureNames.Contains(normalName))
        {
            var diffuseControl = Material.Textures.First(value => value.Name.Equals(diffuseName,
                StringComparison.OrdinalIgnoreCase));
            var normalControl = Material.Textures.First(value => value.Name.Equals(normalName,
                StringComparison.OrdinalIgnoreCase));
            var npcPairs = _randomisationCatalog.CompatibleMaterialDonors(profileKey)
                .Select(donor => donor.MaterialTextureFamilies.GetValueOrDefault("human-face"))
                .Where(family => family is not null && family.ContainsKey("HED_Diff") &&
                                 family.ContainsKey("HED_Norm"))
                .Select(family => new PlayerFaceTextureSet(
                    family!["HED_Diff"], family["HED_Norm"]))
                .Where(pair => diffuseControl.CanResolveRandomisationPath(pair.DiffusePath) &&
                               normalControl.CanResolveRandomisationPath(pair.NormalPath))
                .ToArray();
            var face = PlayerFaceScarTexturePolicy.SelectFaceSet(
                profileKey, npcPairs, request.Hair.Textures, seed);
            if (face is not null)
            {
                try
                {
                    // Resolve both members before applying either one so a missing asset
                    // cannot leave a face diffuse and normal from different sets.
                    var diffuse = face.PlayerDiffuseAsset is { } playerDiffuse
                        ? await diffuseControl.ResolveReferenceAsync(playerDiffuse)
                        : await diffuseControl.ResolveRandomisationPathAsync(face.DiffusePath);
                    var normal = face.PlayerNormalAsset is { } playerNormal
                        ? await normalControl.ResolveReferenceAsync(playerNormal)
                        : await normalControl.ResolveRandomisationPathAsync(face.NormalPath);
                    textures[diffuseName] = diffuse;
                    textures[normalName] = normal;
                    appliedFamilies++;
                }
                catch (Exception exception)
                {
                    AppLog.Warning($"PLAYER:FACE-PAIR-SKIP DIFF:{face.DiffusePath} NORM:{face.NormalPath} REASON:unresolved {exception.Message}");
                }
            }
        }

        var scarName = input.FindControlName(context.ScopeKey, "HED_Scar",
            MaterialParameterKind.Texture);
        var customStrengthName = input.FindControlName(context.ScopeKey,
            "HED_Custom_Scar_Scalar", MaterialParameterKind.Scalar);
        var diffuseStrengthName = input.FindControlName(context.ScopeKey,
            "HED_Scar_Diffuse_Scalar", MaterialParameterKind.Scalar);
        var unlockedScalarNames = input.IncludedScalars;
        var scarStrengthsUnlocked =
            (customStrengthName is null || unlockedScalarNames.Contains(customStrengthName)) &&
            (diffuseStrengthName is null || unlockedScalarNames.Contains(diffuseStrengthName));
        if (scarName is not null && requestedTextureNames.Contains(scarName) && scarStrengthsUnlocked)
        {
            var scar = PlayerFaceScarTexturePolicy.SelectScar(
                profileKey, request.Hair.Textures, seed);
            if (scar is not null)
            {
                var scarResolved = !scar.HasScar;
                if (scar.TextureAsset is { } asset)
                {
                    try
                    {
                        var control = Material.Textures.First(value => value.Name.Equals(scarName,
                            StringComparison.OrdinalIgnoreCase));
                        textures[scarName] = await control.ResolveReferenceAsync(asset);
                        scarResolved = true;
                    }
                    catch (Exception exception)
                    {
                        AppLog.Warning($"PLAYER:SCAR-SKIP {asset.InstancedPath} REASON:unresolved {exception.Message}");
                    }
                }
                if (scarResolved)
                {
                    if (customStrengthName is not null)
                        scalars[customStrengthName] = scar.CustomScarScalar;
                    if (diffuseStrengthName is not null)
                        scalars[diffuseStrengthName] = context.Sex == PlayerHairSex.Female
                            ? 0f : scar.ScarDiffuseScalar;
                    appliedFamilies++;
                }
            }
        }

        if (context.Sex == PlayerHairSex.Female && diffuseStrengthName is not null &&
            requestedScalarNames.Contains(diffuseStrengthName) &&
            unlockedScalarNames.Contains(diffuseStrengthName))
            scalars[diffuseStrengthName] = 0f;

        return textures.Count + scalars.Count == 0 ? null : new PreparedMaterialRandomisation(
            scalars, new Dictionary<string, System.Numerics.Vector4>(), textures,
            appliedFamilies, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<(PreparedMaterialRandomisation Material, PlayerHairStyleOption Style,
        MorphFaceEditor.Core.Domain.AssetIdentity? Mesh)?> PrepareHumanHairAsync(
        EditorRandomisationRequest request, int seed, bool retainCurrentMesh)
    {
        var context = request.Human!.Value;
        var input = request.Material;
        var requestedTextureNames = input.TextureScope;
        var targetedTextureRoll = request.Hair.TargetedTextureRoll;
        var npcHairRules = request.Player is null;
        var diffuseControl = input.FindControlName(context.ScopeKey, "HED_Scalp_Diff",
            MaterialParameterKind.Texture);
        var normalControl = input.FindControlName(context.ScopeKey, "HED_Scalp_Norm",
            MaterialParameterKind.Texture);
        if (diffuseControl is null || normalControl is null ||
            !requestedTextureNames.Contains(diffuseControl) ||
            !requestedTextureNames.Contains(normalControl)) return null;

        // Roll the supported scalp components as one family. Some Player materials
        // have no Tang parameter, and a partially locked scalp family must stay put.
        var scalpControls = new[]
            {
                "HED_Scalp_Diff", "HED_Scalp_Norm", "HED_Scalp_Spec", "HED_Tang"
            }
            .Select(name => input.FindControlName(context.ScopeKey, name,
                MaterialParameterKind.Texture))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();
        if (scalpControls.Any(name => !requestedTextureNames.Contains(name))) return null;

        var preserveMesh = request.Hair.Locked || retainCurrentMesh;
        if (preserveMesh && request.Hair.CurrentMeshIsPaired)
            return null;

        var options = PlayerHairStyleCatalog.Resolve(context.Game, context.Sex,
            request.Hair.Textures, request.Hair.Meshes,
            preserveMesh || !request.Hair.CanEditMeshes, preferNativeNpcMeshVariants: npcHairRules)
            .Where(style => !targetedTextureRoll ||
                style.MeshAction != PlayerHairMeshAction.Set || style.HasDedicatedScalpPair)
            .ToArray();
        if (options.Length == 0) return null;
        var first = new Random(unchecked(seed ^ 0x504C4159)).Next(options.Length);
        for (var attempt = 0; attempt < options.Length; attempt++)
        {
            var style = options[(first + attempt) % options.Length];
            var supportedScalp = style.ScalpTextures
                .Where(member => input.FindControlName(context.ScopeKey, member.Key,
                    MaterialParameterKind.Texture) is not null)
                .ToDictionary(member => member.Key, member => member.Value,
                    StringComparer.OrdinalIgnoreCase);
            var prepared = await Material.PrepareRandomisationAsync(
                new Dictionary<string, float>(),
                new Dictionary<string, System.Numerics.Vector4>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["player-hair"] = supportedScalp
                }, context.ScopeKey);
            if (prepared.FailedTextureFamilySignatures.Count > 0 ||
                !prepared.Textures.ContainsKey(diffuseControl)) continue;

            MorphFaceEditor.Core.Domain.AssetIdentity? mesh = null;
            var selectedStyle = npcHairRules
                ? HumanMaleHairScalpPolicy.ApplyNpcStyleMorphCompatibility(style)
                : style;
            if (style.MeshAction == PlayerHairMeshAction.Set)
            {
                if (targetedTextureRoll && !style.RequiresMeshForScalp)
                    selectedStyle = style with { MeshAction = PlayerHairMeshAction.Clear };
                else
                {
                    if (style.MeshVariants.Count == 0) continue;
                    var variant = new Random(unchecked(seed ^ 0x4D455348));
                    var outcome = variant.Next(style.MeshVariants.Count +
                                               (style.IncludesNoMeshOutcome ? 1 : 0));
                    if (outcome == style.MeshVariants.Count)
                        selectedStyle = style with { MeshAction = PlayerHairMeshAction.Clear };
                    else mesh = style.MeshVariants[outcome];
                }
            }
            var adjustedTextures = new Dictionary<string, DecodedTextureAsset?>(
                prepared.Textures, StringComparer.OrdinalIgnoreCase);
            var adjustedScalars = new Dictionary<string, float>(prepared.Scalars,
                StringComparer.OrdinalIgnoreCase);
            if (!npcHairRules && context.Sex == PlayerHairSex.Male)
            {
                var scalpMask = input.FindControlName(context.ScopeKey,
                    "HED_Scalp_Mask_Scalar", MaterialParameterKind.Scalar);
                if (scalpMask is not null) adjustedScalars[scalpMask] = 1f;
            }
            var adjusted = new PreparedMaterialRandomisation(
                adjustedScalars, prepared.Vectors, adjustedTextures,
                prepared.AppliedTextureFamilies, prepared.FailedTextureFamilySignatures);
            return (adjusted, selectedStyle, mesh);
        }
        return null;
    }

    private IReadOnlyDictionary<string, float> WithHumanHairMorphs(
        EditorRandomisationRequest request, IReadOnlyDictionary<string, float> proposed,
        (PreparedMaterialRandomisation Material, PlayerHairStyleOption Style,
            MorphFaceEditor.Core.Domain.AssetIdentity? Mesh)? playerHair,
        int seed)
    {
        if (!request.RandomiseMaterials || !request.Hair.CanEditMorphs || request.Hair.Locked || playerHair is null)
            return proposed;
        var selected = playerHair.Value;
        if (selected.Style.Sex == PlayerHairSex.Female)
        {
            if (selected.Style.MeshAction != PlayerHairMeshAction.Set) return proposed;
            return ClearFemaleHairMorphsForMesh(proposed, request.Hair.IncludedMorphs);
        }
        var diffuse = selected.Material.Textures.FirstOrDefault(value =>
            Material.SourceParameterName(value.Key).Equals("HED_Scalp_Diff", StringComparison.OrdinalIgnoreCase));
        if (diffuse.Value is null) return proposed;
        var editable = request.Hair.IncludedMorphs;
        var compatible = selected.Style.HairMorphNames.Where(editable.Contains).ToArray();
        var chosen = compatible.Length == 0 ? null : compatible[
            new Random(unchecked(seed ^ 0x4D4F5250)).Next(compatible.Length)];
        var result = new Dictionary<string, float>(proposed, StringComparer.OrdinalIgnoreCase);
        foreach (var name in editable)
            result[name] = name.Equals(chosen, StringComparison.OrdinalIgnoreCase)
                ? selected.Style.HairMorphValue
                : 0f;
        return result;
    }

    internal static IReadOnlyDictionary<string, float> ClearFemaleHairMorphsForMesh(
        IReadOnlyDictionary<string, float> proposed, IEnumerable<string> unlockedHairMorphs)
    {
        var cleared = new Dictionary<string, float>(proposed, StringComparer.OrdinalIgnoreCase);
        foreach (var name in unlockedHairMorphs) cleared[name] = 0f;
        return cleared;
    }

    private IReadOnlySet<string> EligibleMaterialTextureParameters(
        IReadOnlyList<MaterialRandomisationProfileContext>? materialProfiles = null)
    {
        var profiles = materialProfiles ?? GetMaterialRandomisationProfiles();
        // Donor eligibility is shared by every texture control using the same profile.
        var eligibleParameters = profiles.Select(context => context.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(key => key, key => _randomisationCatalog.EligibleTextureParameters(key),
                StringComparer.OrdinalIgnoreCase);
        var player = GetPlayerRandomisationContext();
        return Material.Textures
            .Where(texture => !IsProtectedPlayerEyeTexture(Material.SourceParameterName(texture.Name)))
            .Where(texture => profiles.Any(context =>
                    context.Supports(texture.Name, MaterialParameterKind.Texture) &&
                    eligibleParameters[context.Key].Contains(Material.SourceParameterName(texture.Name))) ||
                player is { } context &&
                string.Equals(Material.ParameterScopeKey(texture.Name), context.ScopeKey,
                    StringComparison.OrdinalIgnoreCase) &&
                (IsPlayerExclusiveTexture(Material.SourceParameterName(texture.Name)) ||
                 Player2DaParameters.ContainsKey(Material.SourceParameterName(texture.Name))))
            .Select(texture => texture.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<string, float> WithSelectedScalpHairMorphs(
        EditorRandomisationRequest request, IReadOnlyDictionary<string, float> proposed,
        PreparedMaterialRandomisation? preparedMaterial,
        int seed)
    {
        if (!request.RandomiseMaterials || !request.Hair.CanEditMorphs || request.Hair.Locked ||
            !request.ProfileKey.Contains("human-male", StringComparison.OrdinalIgnoreCase) ||
            preparedMaterial is null) return proposed;
        var selectedScalp = preparedMaterial.Textures.FirstOrDefault(value =>
            Material.SourceParameterName(value.Key).Equals("HED_Scalp_Diff", StringComparison.OrdinalIgnoreCase));
        if (selectedScalp.Value is null) return proposed;
        var selectedPath = selectedScalp.Value.Source.InstancedPath;
        var editableHair = request.Hair.IncludedMorphs;
        var overrides = HumanMaleHairScalpPolicy.CreateMorphOverrides(selectedPath, editableHair, seed);
        if (overrides is null || overrides.Count == 0) return proposed;
        var result = new Dictionary<string, float>(proposed, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in overrides) result[name] = value;
        return result;
    }

    internal bool HasAssetReferenceChanges(MorphFaceEditor.Core.Domain.MorphFaceDocument draft) =>
        draft.HairMeshReference != _cleanState.HairMeshReference ||
        !draft.OtherMeshReferences.SequenceEqual(_cleanState.OtherMeshReferences) ||
        !draft.MaterialOverrides.Textures.SequenceEqual(_cleanState.MaterialOverrides.Textures);

    internal MorphFaceEditor.Core.Domain.MorphFaceDocument SavedDraft => _cleanState;

    internal void AcceptSavedState(bool materialsOnly = false, bool featuresOnly = false)
    {
        var draft = CreateDraft();
        if (featuresOnly)
        {
            _cleanState = _cleanState with { MorphFeatures = draft.MorphFeatures };
            _featuresOnlySavedState = true;
        }
        else
        {
            _cleanState = materialsOnly ? _cleanState with { MaterialOverrides = draft.MaterialOverrides } : draft;
            _cleanMaterialAssignments = CaptureMaterialAssignments();
            if (!materialsOnly)
            {
                _featuresOnlySavedState = false;
                _cleanGeometryEdits = _session.CaptureGeometryEdits();
            }
        }
        RefreshDirtyState();
    }

    private void RefreshDirtyState()
    {
        var draft = CreateDraft();
        if (_featuresOnlySavedState) draft = draft with { FinalSkeleton = _cleanState.FinalSkeleton, BakedLods = _cleanState.BakedLods };
        IsDirty = _session.HasPendingRepair || _featuresOnlySavedState && !_session.GeometryEditsMatch(_cleanGeometryEdits) ||
                  !MaterialAssignmentsMatchCleanState() ||
                  !MorphFaceEditorStateComparer.Equals(_cleanState, draft);
    }

    private void OnMaterialControlsChanged(object? sender, EventArgs e)
    {
        // A pending attachment load can replace its controls while a roll awaits
        // texture decoding. Its prepared values belong to the outgoing surface.
        _randomisationGeneration++;
        if (_cursedBaseline is { } baseline)
        {
            // Attachment selection can introduce supported numeric controls after the
            // first Cursed roll. Seed their baseline once without moving existing ones.
            var scalars = new Dictionary<string, float>(baseline.Scalars, StringComparer.OrdinalIgnoreCase);
            var vectors = new Dictionary<string, System.Numerics.Vector4>(baseline.Vectors,
                StringComparer.OrdinalIgnoreCase);
            foreach (var value in Material.Scalars) scalars.TryAdd(value.Name, value.Value);
            foreach (var value in Material.Vectors) vectors.TryAdd(value.Name, value.Value);
            _cursedBaseline = baseline with { Scalars = scalars, Vectors = vectors };
        }
        OnPropertyChanged(nameof(CanExportTseMaterials));
        OnPropertyChanged(nameof(CanImportTseMaterials));
        var selectedKey = SelectedCategory?.Key;
        Categories = BuildCategories();
        OnPropertyChanged(nameof(Categories));
        SelectedCategory = Categories.FirstOrDefault(category =>
                               string.Equals(category.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
                           ?? Categories.FirstOrDefault();
        OnPropertyChanged(nameof(AllowsMaterialRandomisation));
        OnPropertyChanged(nameof(AllowsCursedRandomisation));
        RaiseRandomisationCanExecuteChanged();
    }

    private void OnMaterialPreviewChanged(object? sender, EventArgs e) =>
        MaterialPreviewChanged?.Invoke(this, EventArgs.Empty);

    private void OnHairMeshPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HairMeshEditorViewModel.IsRandomisationLocked))
            RaiseRandomisationCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _randomisationGeneration++;
        _session.EvaluationChanged -= OnEvaluationChanged;
        _history.HistoryChanged -= OnHistoryChanged;
        Material.PreviewChanged -= OnMaterialPreviewChanged;
        Material.ControlsChanged -= OnMaterialControlsChanged;
        _scalpMaterialSession.ValueChanged -= OnScalpMaterialSelectionChanged;
        HairMesh.PropertyChanged -= OnHairMeshPropertyChanged;
        foreach (var slot in CustomMaterialSlots) slot.Dispose();
        Material.Dispose();
        HairMesh.Dispose();
        foreach (var otherMesh in OtherMeshes)
        {
            otherMesh.Dispose();
        }
        _disposed = true;
    }
}
