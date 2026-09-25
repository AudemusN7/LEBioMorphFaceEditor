using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.ViewModels;

public sealed record PreparedManualHairScalpPair(
    PreparedMaterialRandomisation Material, string StyleId);

/// <summary>
/// Composes one detached editing session into category controls, semantic history and scoped
/// randomisation commands. Package I/O and rendering remain outside this view model.
/// </summary>
public sealed class FaceEditorViewModel : ObservableObject, IDisposable
{
    private readonly MorphFaceEditingSession _session;
    private readonly EditorUndoCoordinator _history;
    private readonly RelayCommand _undoCommand;
    private readonly RelayCommand _redoCommand;
    private readonly RelayCommand _setToDefaultsCommand;
    private readonly RelayCommand _randomiseCommand;
    private readonly List<RelayCommand> _subcategoryRandomiseCommands = [];
    private readonly MorphRandomisationCatalog _randomisationCatalog;
    private readonly string _profileKey;
    private readonly string _materialRandomisationProfileKey;
    private readonly bool _allowsMorphRandomisation;
    private readonly Func<int> _randomSeedFactory;
    private readonly Action<string> _reportError;
    private MorphFaceEditor.Core.Domain.MorphFaceDocument _cleanState;
    private EditorFeatureCategoryViewModel? _selectedCategory;
    private BoneTransformEditorViewModel? _selectedBoneTransform;
    private bool _isDirty;
    private bool _disposed;
    private int _morphRandomisationStrength = 50;
    private int _materialRandomisationStrength = 50;
    private bool _randomiseMorphs = true;
    private bool _randomiseMaterials;
    private bool _cursedMode;
    private CursedRandomisationBaseline? _cursedBaseline;
    private readonly Dictionary<string, System.Numerics.Vector3> _cursedBoneOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlySet<int> _morphGeometryLods;
    private readonly IReadOnlyList<MorphFaceEditor.Core.Domain.AssetIdentity?> _preservedOtherMeshes;
    private readonly RandomisationInclusionState _randomisationInclusionState;
    private readonly bool _allowsAttachmentEditing;
    private readonly IHeadEditorUiProfile _metadataCatalog;
    private readonly CustomMaterialWorkspace? _customMaterialWorkspace;
    private readonly IReadOnlyList<CustomMaterialAssignmentOption> _customMaterialOptions;
    private readonly MorphFaceEditor.LegendaryExplorer.MorphFaceGame? _playerRandomisationGame;
    private IReadOnlyList<TextureCatalogCandidate> _registryTextureCandidates = [];
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
        MorphFaceEditor.LegendaryExplorer.MorphFaceGame? playerRandomisationGame = null)
    {
        _session = session;
        _metadataCatalog = metadataCatalog;
        _customMaterialWorkspace = customMaterialWorkspace;
        _customMaterialOptions = customMaterialOptions ?? [];
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
        var historySources = new List<IUndoableEditSource>
        {
            session, materialSession, hairSession
        };
        historySources.AddRange(otherMeshSessions);
        if (customMaterialWorkspace is not null) historySources.Add(customMaterialWorkspace);
        _history = new EditorUndoCoordinator(historySources.ToArray());
        _undoCommand = new RelayCommand(_history.Undo, () => _history.CanUndo);
        _redoCommand = new RelayCommand(_history.Redo, () => _history.CanRedo);
        _setToDefaultsCommand = new RelayCommand(SetToDefaults, CanSetToDefaults);
        _randomiseCommand = new RelayCommand(
            () => Randomise(GlobalRandomisationScope(), includeCursedBones: true),
            () => CanRandomiseScope(GlobalRandomisationScope(), includeCursedBones: true));
        HairMesh = new HairMeshEditorViewModel(
            hairSession, meshCandidates, previewOnlyAttachments ? "Hair mesh (preview only)" : "m_oHairMesh", 0);
        HairMesh.PropertyChanged += OnHairMeshPropertyChanged;
        OtherMeshes = otherMeshSessions
            .Select((otherSession, index) => new HairMeshEditorViewModel(
                otherSession,
                meshCandidates,
                previewOnlyAttachments ? "Accessory mesh (preview only)" : "m_oOtherMeshes",
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
        _cleanState = CreateDraft();
        _cleanMaterialAssignments = CaptureMaterialAssignments();
    }

    private IReadOnlyList<EditorFeatureCategoryViewModel> BuildCategories()
    {
        _subcategoryRandomiseCommands.Clear();
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
                    var eligibleTextures = EligibleMaterialTextureParameters();
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

    public IReadOnlyList<MorphFeatureEditorViewModel> Features { get; }
    public IReadOnlyList<EditorFeatureCategoryViewModel> Categories { get; private set; }
    public IReadOnlyList<CustomMaterialSlotEditorViewModel> CustomMaterialSlots { get; }
    public bool HasCustomMaterialSlots => CustomMaterialSlots.Count > 0;
    public CustomMaterialWorkspace? CustomMaterials => _customMaterialWorkspace;
    public IReadOnlyList<CustomMaterialAssignmentOption> CustomMaterialOptions => _customMaterialOptions;
    public bool CanExportTseMaterials => _customMaterialWorkspace is { } workspace && MeshMaterialInterchange.CanExportTse(workspace);
    public bool CanImportTseMaterials => _customMaterialWorkspace is { } workspace && MeshMaterialInterchange.CanImportTse(workspace);

    public void ApplyMeshMaterialImport(PreparedMeshMaterialImport import)
    {
        if (_customMaterialWorkspace is null) throw new InvalidOperationException("A MESH material workspace is required.");
        using (var aggregate = _history.BeginAggregate())
        {
            if (import.Assignments is not null) _customMaterialWorkspace.ReplaceAssignments(import.Assignments);
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
    public void UpdateRegistryTextureCandidates(
        IReadOnlyList<TextureCatalogCandidate> candidates,
        TextureCatalogProfile profile,
        bool isRegistryAvailable)
    {
        _registryTextureCandidates = candidates;
        Material.UpdateRegistryCandidates(candidates, profile, isRegistryAvailable);
    }
    public void UpdateRegistryAttachmentMeshes(
        IReadOnlyList<AttachmentMeshCandidate> candidates,
        bool isPlayerWorkspace)
    {
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
    public bool CanEdit => CanEditMorphFeatures;
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
                    _cursedBoneOffsets.Clear();
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
            Material.CreateOverrides());
    public void FixMorph()
    {
        _session.FixMorph();
        RefreshDirtyState();
    }
    public void ApplyMorphData(MorphFaceEditor.Core.Domain.MorphFaceMorphData data)
    {
        _session.ApplyMorphData(data);
        RefreshDirtyState();
    }
    private void SetToDefaults()
    {
        using var aggregate = _history.BeginAggregate();
        if (CanEditMorphFeatures || CanEditBones) _session.ResetToDefaults();
        Material.ResetToDefaults();
        aggregate.Commit();
        _cursedBaseline = null;
        _cursedBoneOffsets.Clear();
    }

    private bool CanSetToDefaults() => CanEditMorphFeatures || CanEditBones || AllowsMaterialRandomisation;

    private async void Randomise(EditorRandomisationScope requested, bool includeCursedBones)
    {
        try
        {
            if (requested.InclusionKey is { } key && !_randomisationInclusionState.IsIncluded(key)) return;
            if (CursedMode)
            {
                await RandomiseCursedAsync(requested, includeCursedBones);
                return;
            }

            var playerContext = GetPlayerRandomisationContext();
            var morphScope = RandomiseMorphs
                ? requested.MorphFeatures.Where(value => value.IsEditable &&
                    IsPlayerRandomisableMorph(value, playerContext))
                    .Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (playerContext is { Sex: PlayerHairSex.Male } && HairMesh.IsRandomisationLocked)
                morphScope.RemoveWhere(HumanMaleHairScalpPolicy.IsHairMorph);
            var materialProfiles = GetMaterialRandomisationProfiles();
            var scalarScope = RandomiseMaterials
                ? requested.Scalars.Select(value => value.Name)
                    .Where(name => playerContext is { } player &&
                        string.Equals(Material.ParameterScopeKey(name), player.ScopeKey,
                            StringComparison.OrdinalIgnoreCase) &&
                        Player2DaParameters.ContainsKey(Material.SourceParameterName(name)) ||
                        materialProfiles.Any(value =>
                        value.Supports(Material, name, MaterialParameterKind.Scalar) &&
                        value.Profile.Scalars.ContainsKey(Material.SourceParameterName(name))))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vectorScope = RandomiseMaterials
                ? requested.Vectors.Select(value => value.Name)
                    .Where(name => playerContext is { } player &&
                        string.Equals(Material.ParameterScopeKey(name), player.ScopeKey,
                            StringComparison.OrdinalIgnoreCase) &&
                        Player2DaParameters.ContainsKey(Material.SourceParameterName(name)) ||
                        materialProfiles.Any(value =>
                        value.Supports(Material, name, MaterialParameterKind.Vector) &&
                        value.Profile.Vectors.ContainsKey(Material.SourceParameterName(name))))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requestedTextureNames = RandomiseMaterials
                ? requested.Textures.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var npcTextureNames = playerContext is null
                ? requestedTextureNames
                : requestedTextureNames.Where(name =>
                        !IsPlayerExclusiveTexture(Material.SourceParameterName(name)) &&
                        !Player2DaParameters.ContainsKey(Material.SourceParameterName(name)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasMaterial = scalarScope.Count + vectorScope.Count + requestedTextureNames.Count > 0;
            if (morphScope.Count == 0 && !hasMaterial) return;

            int? morphSeed = morphScope.Count > 0 ? _randomSeedFactory() : null;
            int? materialSeed = hasMaterial ? NextDistinctSeed(morphSeed) : null;
            MorphRandomisationDonor? morphDonor = morphSeed is not null
                ? _randomisationCatalog.SelectDonor(_profileKey, morphSeed.Value, requireMaterial: false)
                : null;
            MorphRandomisationProposal? morphProposal = null;
            if (morphDonor is not null && morphSeed is not null)
            {
                morphProposal = _randomisationCatalog.CreateMorphProposal(
                    morphDonor,
                    Features.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase),
                    Features.Select(value => new MorphRandomisationFeatureBounds(
                        value.Name, value.Metadata.Minimum, value.Metadata.Maximum)).ToArray(),
                    morphScope, MorphRandomisationStrength, morphSeed.Value);
            }

            PreparedMaterialRandomisation? npcMaterial = null;
            var materialValueCount = 0;
            if (hasMaterial && materialProfiles.Count > 0)
            {
                var npcScalarScope = playerContext is null ? scalarScope : scalarScope
                    .Where(name => !Player2DaParameters.ContainsKey(Material.SourceParameterName(name)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var npcVectorScope = playerContext is null ? vectorScope : vectorScope
                    .Where(name => !Player2DaParameters.ContainsKey(Material.SourceParameterName(name)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                npcMaterial = await PrepareMaterialRandomisationAsync(
                    materialProfiles, npcScalarScope, npcVectorScope, npcTextureNames,
                    materialSeed!.Value, morphDonor?.Id);
                materialValueCount = scalarScope.Count + vectorScope.Count;
            }
            var playerDetails = playerContext is not null && materialSeed is not null
                ? await PreparePlayerFacialDetailsAsync(playerContext.Value, requestedTextureNames,
                    materialSeed.Value)
                : new Dictionary<string, DecodedTextureAsset?>();
            var playerColours = playerContext is not null && materialSeed is not null
                ? await PreparePlayer2DaAsync(playerContext.Value, requested, playerDetails,
                    materialSeed.Value, cursed: false)
                : null;
            var playerFaceScar = playerContext is not null && materialSeed is not null
                ? await PreparePlayerFaceScarAsync(playerContext.Value, requestedTextureNames,
                    scalarScope, materialSeed.Value)
                : null;
            var playerHair = playerContext is not null && materialSeed is not null
                ? await PreparePlayerHairAsync(playerContext.Value, requestedTextureNames,
                    materialSeed.Value, requested.InclusionKey is not null)
                : null;
            var preparedMaterial = MergePreparedMaterial(npcMaterial, playerDetails,
                playerHair?.Material, playerFaceScar, playerColours);

            using (var aggregate = _history.BeginAggregate())
            {
                var proposedMorphs = WithoutDonorHairMorphsWhenScalpRolled(
                    morphProposal?.Values ?? new Dictionary<string, float>(), playerHair);
                var morphValues = playerContext is null
                    ? WithSelectedScalpHairMorphs(proposedMorphs, preparedMaterial,
                        materialSeed ?? morphSeed ?? 0)
                    : WithPlayerHairMorphs(proposedMorphs, playerHair,
                        materialSeed ?? morphSeed ?? 0);
                morphValues = WithExclusivePlayerIconicMorphs(morphValues, playerContext,
                    morphSeed ?? materialSeed ?? 0);
                if (morphValues.Count > 0) _session.SetFeatures(morphValues);
                if (preparedMaterial is not null) Material.ApplyRandomisation(preparedMaterial);
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
            }
            AppLog.Information(
                $"Randomised {morphScope.Count} morph and {materialValueCount} material values " +
                $"at morph/material strengths {MorphRandomisationStrength}%/{MaterialRandomisationStrength}% " +
                $"with RNG seeds {morphSeed?.ToString() ?? "n/a"}/{materialSeed?.ToString() ?? "n/a"}.");
        }
        catch (Exception exception)
        {
            AppLog.Error("Face randomisation failed.", exception);
            _reportError($"The face could not be randomised. Details were written to {AppLog.FilePath}");
        }
    }

    private async Task RandomiseCursedAsync(EditorRandomisationScope requested, bool includeBones)
    {
        _cursedBaseline ??= CaptureCursedBaseline();
        var seed = _randomSeedFactory();
        var playerContext = GetPlayerRandomisationContext();
        var features = RandomiseMorphs
            ? requested.MorphFeatures.Where(value => value.IsEditable &&
                    IsPlayerRandomisableMorph(value, playerContext))
                .ToDictionary(value => value.Name, value => _cursedBaseline.Features[value.Name], StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (playerContext is { Sex: PlayerHairSex.Male } && HairMesh.IsRandomisationLocked)
            foreach (var name in features.Keys.Where(HumanMaleHairScalpPolicy.IsHairMorph).ToArray())
                features.Remove(name);
        var scalars = RandomiseMaterials
            ? requested.Scalars.ToDictionary(value => value.Name,
                value => _cursedBaseline.Scalars[value.Name], StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectors = RandomiseMaterials
            ? requested.Vectors.ToDictionary(value => value.Name,
                value => _cursedBaseline.Vectors[value.Name], StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, System.Numerics.Vector4>(StringComparer.OrdinalIgnoreCase);
        var featureValues = CursedMorphRandomiser.CreateFeatureValues(
            features, MorphRandomisationStrength, seed);
        var materialValues = CursedMorphRandomiser.CreateExtrasProposal(
            [], scalars, vectors, MaterialRandomisationStrength, seed,
            requested.Scalars.Select(value => new MaterialRandomisationScalarBounds(
                value.Name, value.Minimum, value.Maximum)).ToArray());
        var cursedTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);
        var requestedTextureNames = RandomiseMaterials
            ? requested.Textures.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (requestedTextureNames.Count > 0)
        {
            var profileIndex = 0;
            foreach (var context in GetMaterialRandomisationProfiles())
            {
                var requestedNames = requested.Textures
                    .Where(value => context.Supports(Material, value.Name, MaterialParameterKind.Texture))
                    .Select(value => Material.SourceParameterName(value.Name))
                    .Where(name => playerContext is null ||
                        !IsPlayerExclusiveTexture(name) && !Player2DaParameters.ContainsKey(name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var availableTextureNames = Material.Textures
                    .Where(value => context.Supports(Material, value.Name, MaterialParameterKind.Texture))
                    .Select(value => Material.SourceParameterName(value.Name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var donor = _randomisationCatalog.SelectMaterialDonor(
                    context.Key, unchecked(seed + profileIndex++ * 7919));
                foreach (var family in donor.MaterialTextureFamilies.Where(family =>
                             IsTextureFamilyInScope(family.Value, requestedNames, availableTextureNames)))
                {
                    var scopedMembers = family.Value
                        .Select(member => new
                        {
                            Name = Material.FindControlName(
                                context.ParameterScopeKey, member.Key, MaterialParameterKind.Texture),
                            member.Value
                        })
                        .Where(member => member.Name is not null)
                        .ToDictionary(member => member.Name!, member => member.Value,
                            StringComparer.OrdinalIgnoreCase);
                    if (scopedMembers.Count > 0)
                    {
                        cursedTextureFamilies.TryAdd(
                            $"{context.ParameterScopeKey}|{family.Key}", scopedMembers);
                    }
                }
            }
        }
        var npcCursedScalars = playerContext is null ? materialValues.ScalarValues :
            materialValues.ScalarValues.Where(value =>
                    !Player2DaParameters.ContainsKey(Material.SourceParameterName(value.Key)))
                .ToDictionary(value => value.Key, value => value.Value,
                    StringComparer.OrdinalIgnoreCase);
        var npcCursedVectors = playerContext is null ? materialValues.VectorValues :
            materialValues.VectorValues.Where(value =>
                    !Player2DaParameters.ContainsKey(Material.SourceParameterName(value.Key)))
                .ToDictionary(value => value.Key, value => value.Value,
                    StringComparer.OrdinalIgnoreCase);
        var npcMaterial = await Material.PrepareRandomisationAsync(
            npcCursedScalars, npcCursedVectors,
            cursedTextureFamilies);
        var playerDetails = playerContext is not null && requestedTextureNames.Count > 0
            ? await PreparePlayerFacialDetailsAsync(playerContext.Value, requestedTextureNames, seed)
            : new Dictionary<string, DecodedTextureAsset?>();
        var playerColours = playerContext is not null && RandomiseMaterials &&
                            MaterialRandomisationStrength > 0
            ? await PreparePlayer2DaAsync(playerContext.Value, requested, playerDetails,
                seed, cursed: true)
            : null;
        var requestedScalarNames = RandomiseMaterials
            ? requested.Scalars.Select(value => value.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var playerFaceScar = playerContext is not null && RandomiseMaterials
            ? await PreparePlayerFaceScarAsync(playerContext.Value, requestedTextureNames,
                requestedScalarNames, seed)
            : null;
        var playerHair = playerContext is not null && requestedTextureNames.Count > 0
            ? await PreparePlayerHairAsync(playerContext.Value, requestedTextureNames, seed,
                requested.InclusionKey is not null)
            : null;
        var preparedMaterial = MergePreparedMaterial(npcMaterial, playerDetails,
            playerHair?.Material, playerFaceScar, playerColours)!;
        CursedMorphRandomisationExtras? boneValues = null;
        Dictionary<string, System.Numerics.Vector3>? nextBoneOffsets = null;
        var fuzzedBoneCount = 0;

        using (var aggregate = _history.BeginAggregate())
        {
            var proposedMorphs = WithoutDonorHairMorphsWhenScalpRolled(featureValues, playerHair);
            var morphValues = playerContext is null
                ? WithSelectedScalpHairMorphs(proposedMorphs, preparedMaterial, seed)
                : WithPlayerHairMorphs(proposedMorphs, playerHair, seed);
            morphValues = WithExclusivePlayerIconicMorphs(morphValues, playerContext, seed);
            if (morphValues.Count > 0) _session.SetFeatures(morphValues);
            if (includeBones && RandomiseMorphs && CanEditBones)
            {
                var postMorphBones = _session.FinalSkeleton.Select(bone => bone with
                {
                    Translation = bone.Translation - _cursedBoneOffsets.GetValueOrDefault(bone.BoneName)
                }).ToArray();
                boneValues = CursedMorphRandomiser.CreateExtrasProposal(
                    postMorphBones, new Dictionary<string, float>(),
                    new Dictionary<string, System.Numerics.Vector4>(), MorphRandomisationStrength, seed);
                fuzzedBoneCount = boneValues.BoneValues.Zip(postMorphBones)
                    .Count(value => value.First.Translation != value.Second.Translation);
                foreach (var bone in boneValues.BoneValues)
                {
                    _session.SetBoneAxis(bone.BoneName, 0, bone.Translation.X);
                    _session.SetBoneAxis(bone.BoneName, 1, bone.Translation.Y);
                    _session.SetBoneAxis(bone.BoneName, 2, bone.Translation.Z);
                }
                nextBoneOffsets = new Dictionary<string, System.Numerics.Vector3>(
                    _cursedBoneOffsets, StringComparer.OrdinalIgnoreCase);
                foreach (var (fuzzed, clean) in boneValues.BoneValues.Zip(postMorphBones))
                {
                    var offset = fuzzed.Translation - clean.Translation;
                    if (offset == System.Numerics.Vector3.Zero) nextBoneOffsets.Remove(fuzzed.BoneName);
                    else nextBoneOffsets[fuzzed.BoneName] = offset;
                }
            }
            if (preparedMaterial.Scalars.Count + preparedMaterial.Vectors.Count +
                preparedMaterial.Textures.Count > 0)
                Material.ApplyRandomisation(preparedMaterial);
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
        }
        if (nextBoneOffsets is not null)
        {
            _cursedBoneOffsets.Clear();
            foreach (var value in nextBoneOffsets) _cursedBoneOffsets[value.Key] = value.Value;
        }
        AppLog.Information(
            $"Cursed-randomised {features.Count} morph sliders, {fuzzedBoneCount} facial bones and " +
            $"{scalars.Count + vectors.Count} material values at morph/material strengths " +
            $"{MorphRandomisationStrength}%/{MaterialRandomisationStrength}% with RNG seed {seed}.");
    }

    private int NextDistinctSeed(int? otherSeed)
    {
        var seed = _randomSeedFactory();
        return otherSeed is not null && seed == otherSeed.Value ? unchecked(seed + 1) : seed;
    }

    private static bool IsPlayerExclusiveTexture(string name) =>
        name.Equals("HED_Addn", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Brow", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Diff", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Norm", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Scar", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Scalp_Diff", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Scalp_Norm", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Scalp_Spec", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HED_Scalp_Tang", StringComparison.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, Player2DaPalette> Player2DaParameters =
        new Dictionary<string, Player2DaPalette>(StringComparer.OrdinalIgnoreCase)
        {
            ["SkinTone"] = Player2DaPalette.SkinTone,
            ["EYE_White_Colour_Vector"] = Player2DaPalette.SkinTone,
            ["HED_Spec_Add_Vector"] = Player2DaPalette.SkinTone,
            ["HED_Scar_Vector"] = Player2DaPalette.SkinTone,
            ["HED_Teeth_Vector"] = Player2DaPalette.SkinTone,
            ["HED_Hair_Colour_Vector"] = Player2DaPalette.HairColour,
            ["HAIR_Shine_Desaturate_Scalar"] = Player2DaPalette.HairColour,
            ["HED_Scalp_PhongSpec_Scalar"] = Player2DaPalette.HairColour,
            ["Highlight1SpecExp_Scalar"] = Player2DaPalette.HairColour,
            ["Highlight2SpecExp_Scalar"] = Player2DaPalette.HairColour,
            ["Hair_Spec_Aniso_Exp_Scalar"] = Player2DaPalette.HairColour,
            ["HAIR_Spec_Contribution_Scalar"] = Player2DaPalette.HairColour,
            ["HAIR_SPwr_Scalar"] = Player2DaPalette.HairColour,
            ["HED_Addn_Colour_Vector"] = Player2DaPalette.FacialHairColour,
            ["blonde"] = Player2DaPalette.FacialHairColour,
            ["HED_Addn_Colour_02_Scalar"] = Player2DaPalette.FacialHairColour,
            ["HED_Addn_Blowout_Scalar"] = Player2DaPalette.FacialHairColour,
            ["EYE_Iris_Colour_Vector"] = Player2DaPalette.IrisColour,
            ["HED_Blush_Scalar"] = Player2DaPalette.Blush,
            ["HED_Blush_Vector"] = Player2DaPalette.Blush,
            ["HED_EyeShadow_Tint_Scalar"] = Player2DaPalette.EyeMakeup,
            ["HED_EyeShadow_Tint_Vector"] = Player2DaPalette.EyeMakeup,
            ["HED_Brow_Tint_Scalar"] = Player2DaPalette.EyeMakeup,
            ["HED_Brow_Tint_Vector"] = Player2DaPalette.EyeMakeup,
            ["HED_Lash_Diff"] = Player2DaPalette.EyeMakeup,
            ["HED_Lips_Tint_Scalar"] = Player2DaPalette.Lips,
            ["HED_Lips_Tint_Vector"] = Player2DaPalette.Lips,
            ["HED_Addn_SPwr_Lips_Scalar"] = Player2DaPalette.Lips,
            ["HED_Addn_Spec_Lips_Scalar"] = Player2DaPalette.Lips
        };

    private static bool IsPlayerRandomisableMorph(
        MorphFeatureEditorViewModel feature,
        (TextureCatalogGame Game, PlayerHairSex Sex, string? ScopeKey)? player)
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
        IReadOnlyDictionary<string, float> proposed,
        (TextureCatalogGame Game, PlayerHairSex Sex, string? ScopeKey)? player,
        int seed)
    {
        if (!RandomiseMorphs || player is not { Sex: PlayerHairSex.Female }) return proposed;
        const string iconic = "iconic";
        const string raceIconic = "race_iconic";
        var currentIconic = Features.FirstOrDefault(value => value.Name.Equals(iconic,
            StringComparison.OrdinalIgnoreCase))?.Value ?? 0f;
        var currentRaceIconic = Features.FirstOrDefault(value => value.Name.Equals(raceIconic,
            StringComparison.OrdinalIgnoreCase))?.Value ?? 0f;
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

    internal static bool IsTextureFamilyInScope(
        IReadOnlyDictionary<string, string> family,
        IReadOnlySet<string> requestedTextureNames,
        IReadOnlySet<string> availableTextureNames)
    {
        var applicableMembers = family.Keys.Where(availableTextureNames.Contains).ToArray();
        return applicableMembers.Length > 0 && applicableMembers.All(requestedTextureNames.Contains);
    }

    internal static bool WereAllTextureFamiliesRejected(
        int eligibleSignatureCount,
        int excludedSignatureCount,
        int appliedTextureFamilies) =>
        eligibleSignatureCount > 0 &&
        excludedSignatureCount >= eligibleSignatureCount &&
        appliedTextureFamilies == 0;

    private bool CanRandomiseScope(EditorRandomisationScope scope, bool includeCursedBones = false)
    {
        if (scope.InclusionKey is { } key && !_randomisationInclusionState.IsIncluded(key)) return false;
        var lockedMalePlayerHair = GetPlayerRandomisationContext() is { Sex: PlayerHairSex.Male } &&
                                   HairMesh.IsRandomisationLocked;
        var playerContext = GetPlayerRandomisationContext();
        var hasMorph = CanEditMorphFeatures && RandomiseMorphs && scope.MorphFeatures.Any(value =>
            value.IsEditable && IsPlayerRandomisableMorph(value, playerContext) &&
            (!lockedMalePlayerHair ||
                                 !HumanMaleHairScalpPolicy.IsHairMorph(value.Name)));
        var hasRawNumericMaterial = RandomiseMaterials &&
                                    (scope.Scalars.Count + scope.Vectors.Count > 0);
        var materialProfiles = GetMaterialRandomisationProfiles();
        var eligibleTextures = EligibleMaterialTextureParameters();
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
                               hasEligibleTexture || hasPlayerPalette;
        var hasEligibleMorph = hasMorph && _randomisationCatalog.HasDonors(_profileKey);
        var hasEligibleMaterial = RandomiseMaterials && materialProfiles.Count > 0 &&
            (scope.Scalars.Any(value => materialProfiles.Any(profile =>
                 profile.Supports(Material, value.Name, MaterialParameterKind.Scalar) &&
                 profile.Profile.Scalars.ContainsKey(Material.SourceParameterName(value.Name)))) ||
             scope.Vectors.Any(value => materialProfiles.Any(profile =>
                 profile.Supports(Material, value.Name, MaterialParameterKind.Vector) &&
                 profile.Profile.Vectors.ContainsKey(Material.SourceParameterName(value.Name)))) ||
             scope.Textures.Any(value => eligibleTextures.Contains(value.Name)));
        return hasEligibleMorph || hasEligibleMaterial || hasPlayerPalette;
    }

    private void RaiseRandomisationCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanRandomise));
        _randomiseCommand.RaiseCanExecuteChanged();
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

    private sealed record MaterialProfileContext(
        string Key,
        MaterialRandomisationProfile Profile,
        string? ParameterScopeKey,
        ResolvedHeadMaterial? Template)
    {
        public bool Supports(
            MaterialEditorViewModel material,
            string controlName,
            MaterialParameterKind kind) =>
            string.Equals(material.ParameterScopeKey(controlName), ParameterScopeKey,
                StringComparison.OrdinalIgnoreCase) &&
            (Template is null || Template.Supports(material.SourceParameterName(controlName), kind));
    }
    public async Task ApplyMaterialDataAsync(MorphFaceEditor.Core.Domain.MorphFaceMaterialData data)
    {
        await Material.ApplyDataAsync(data);
        RefreshDirtyState();
    }

    public async Task<IReadOnlyList<string>> MergeMaterialDataAsync(
        MorphFaceEditor.Core.Domain.MorphFaceMaterialData data)
    {
        var warnings = await Material.MergeMaterialDataAsync(data);
        RefreshDirtyState();
        return warnings;
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
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditMorphFeatures));
        OnPropertyChanged(nameof(CanEditBones));
        OnPropertyChanged(nameof(UsesLiveDeformationPreview));
        OnPropertyChanged(nameof(CanFixMorph));
        OnPropertyChanged(nameof(HasPendingRepair));
        OnPropertyChanged(nameof(AllowsMorphRandomisation));
        OnPropertyChanged(nameof(AllowsCursedRandomisation));
        _setToDefaultsCommand.RaiseCanExecuteChanged();
        RaiseRandomisationCanExecuteChanged();
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

    private IReadOnlyList<MaterialProfileContext> GetMaterialRandomisationProfiles()
    {
        if (_customMaterialWorkspace is null)
        {
            var profile = _randomisationCatalog.GetMaterialProfile(_materialRandomisationProfileKey);
            return profile is not null && _randomisationCatalog.HasMaterialDonors(_materialRandomisationProfileKey)
                ? [new MaterialProfileContext(_materialRandomisationProfileKey, profile, null, null)]
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
            .Select(value => new MaterialProfileContext(
                value.Key, value.Profile!, value.ScopeKey, value.Template))
            .ToArray();
    }

    private (TextureCatalogGame Game, PlayerHairSex Sex, string? ScopeKey)? GetPlayerRandomisationContext()
    {
        if (_playerRandomisationGame is not { } game) return null;
        var catalogGame = game switch
        {
            MorphFaceEditor.LegendaryExplorer.MorphFaceGame.LE1 => TextureCatalogGame.LE1,
            MorphFaceEditor.LegendaryExplorer.MorphFaceGame.LE2 => TextureCatalogGame.LE2,
            MorphFaceEditor.LegendaryExplorer.MorphFaceGame.LE3 => TextureCatalogGame.LE3,
            _ => throw new ArgumentOutOfRangeException(nameof(game))
        };
        if (_customMaterialWorkspace is null)
        {
            if (_profileKey.EndsWith("-human-male", StringComparison.OrdinalIgnoreCase))
                return (catalogGame, PlayerHairSex.Male, null);
            if (_profileKey.EndsWith("-human-female", StringComparison.OrdinalIgnoreCase))
                return (catalogGame, PlayerHairSex.Female, null);
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
        return (catalogGame, sex, playerSkin.Option.EffectiveParameterScopeKey);
    }

    private (TextureCatalogGame Game, PlayerHairSex Sex, string? ScopeKey)? GetHumanScalpContext()
    {
        var player = GetPlayerRandomisationContext();
        if (player is not null) return player;
        if (_customMaterialWorkspace is not null) return null;
        if (!Enum.TryParse<MorphFaceEditor.LegendaryExplorer.MorphFaceGame>(
                _profileKey.Split('-')[0], true, out var game)) return null;
        var catalogGame = game switch
        {
            MorphFaceEditor.LegendaryExplorer.MorphFaceGame.LE1 => TextureCatalogGame.LE1,
            MorphFaceEditor.LegendaryExplorer.MorphFaceGame.LE2 => TextureCatalogGame.LE2,
            MorphFaceEditor.LegendaryExplorer.MorphFaceGame.LE3 => TextureCatalogGame.LE3,
            _ => (TextureCatalogGame)(-1)
        };
        if (!Enum.IsDefined(catalogGame)) return null;
        if (_profileKey.EndsWith("-human-male", StringComparison.OrdinalIgnoreCase))
            return (catalogGame, PlayerHairSex.Male, null);
        return _profileKey.EndsWith("-human-female", StringComparison.OrdinalIgnoreCase)
            ? (catalogGame, PlayerHairSex.Female, null)
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
        var style = PlayerHairStyleCatalog.Resolve(context.Game, context.Sex,
                _registryTextureCandidates, _registryAttachmentCandidates)
            .FirstOrDefault(value =>
                PlayerHairStyleCatalog.MatchesDedicatedScalpPair(value, mesh));
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
        return new PreparedManualHairScalpPair(prepared, style.Id);
    }

    public void ApplyManualHairScalpPair(PreparedManualHairScalpPair prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        using var aggregate = _history.BeginAggregate();
        Material.ApplyRandomisation(prepared.Material);
        aggregate.Commit();
    }

    private async Task<PreparedMaterialRandomisation?> PreparePlayer2DaAsync(
        (TextureCatalogGame Game, PlayerHairSex Sex, string? ScopeKey) context,
        EditorRandomisationScope requested,
        IReadOnlyDictionary<string, DecodedTextureAsset?> playerDetails,
        int seed, bool cursed)
    {
        var allowed = requested.Scalars.Select(value => value.Name)
            .Concat(requested.Vectors.Select(value => value.Name))
            .Concat(requested.Textures.Select(value => value.Name))
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
        foreach (var value in Material.Scalars.Where(value =>
                     string.Equals(Material.ParameterScopeKey(value.Name), context.ScopeKey,
                         StringComparison.OrdinalIgnoreCase) &&
                     Player2DaParameters.ContainsKey(Material.SourceParameterName(value.Name))))
            current[Material.SourceParameterName(value.Name)] = Player2DaValue.FromScalar(value.Value);
        foreach (var value in Material.Vectors.Where(value =>
                     string.Equals(Material.ParameterScopeKey(value.Name), context.ScopeKey,
                         StringComparison.OrdinalIgnoreCase) &&
                     Player2DaParameters.ContainsKey(Material.SourceParameterName(value.Name))))
            current[Material.SourceParameterName(value.Name)] = Player2DaValue.FromVector(value.Value);

        bool HasTexture(string parameter)
        {
            var controlName = Material.FindControlName(context.ScopeKey, parameter,
                MaterialParameterKind.Texture);
            if (controlName is null) return false;
            if (playerDetails.TryGetValue(controlName, out var selected)) return selected is not null;
            return Material.Textures.First(value => value.Name.Equals(controlName,
                    StringComparison.OrdinalIgnoreCase)).SourceName is { } source &&
                   !source.Equals("None", StringComparison.OrdinalIgnoreCase);
        }
        var result = Player2DaRandomisationPolicy.Roll(new Player2DaRandomisationRequest(
                context.Sex == PlayerHairSex.Male ? Player2DaSex.Male : Player2DaSex.Female,
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
            var controlName = Material.FindControlName(context.ScopeKey, parameter, kind);
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
                    var assets = _registryTextureCandidates
                        .Where(candidate => candidate.Game == context.Game &&
                            candidate.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(candidate => candidate.Occurrences
                            .DefaultIfEmpty(candidate.EffectiveOccurrence)
                            .Where(occurrence => occurrence.Origin == TextureCatalogOrigin.BaseGame)
                            .Select(occurrence => new MorphFaceEditor.Core.Domain.AssetIdentity(
                                occurrence.PackagePath, candidate.InstancedPath,
                                occurrence.ExportUIndex, "Texture2D")))
                        .Distinct()
                        .ToArray();
                    if (assets.Length != 1) break;
                    var control = Material.Textures.First(value =>
                        value.Name.Equals(controlName, StringComparison.OrdinalIgnoreCase));
                    try { textures[controlName] = await control.ResolveReferenceAsync(assets[0]); }
                    catch (Exception exception)
                    {
                        AppLog.Warning($"Skipped unresolved Player makeup lash '{objectName}': " +
                                       exception.Message);
                    }
                    break;
                }
            }
        }
        if (cursed && (scalars.Count > 0 || vectors.Count > 0))
        {
            var bounds = requested.Scalars.Where(value => scalars.ContainsKey(value.Name))
                .Select(value => new MaterialRandomisationScalarBounds(
                    value.Name, value.Minimum, value.Maximum))
                .ToArray();
            var variation = CursedMorphRandomiser.CreateExtrasProposal(
                [], scalars, vectors, MaterialRandomisationStrength,
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

    private async Task<IReadOnlyDictionary<string, DecodedTextureAsset?>> PreparePlayerFacialDetailsAsync(
        (TextureCatalogGame Game, PlayerHairSex Sex, string? ScopeKey) context,
        IReadOnlySet<string> requestedTextureNames,
        int seed)
    {
        var controls = Material.Textures
            .Where(value => requestedTextureNames.Contains(value.Name) &&
                            string.Equals(Material.ParameterScopeKey(value.Name), context.ScopeKey,
                                StringComparison.OrdinalIgnoreCase) &&
                            Material.SourceParameterName(value.Name) is "HED_Brow" or "HED_Addn")
            .ToArray();
        var current = controls.ToDictionary(value => Material.SourceParameterName(value.Name),
            value => value.SourceName, StringComparer.OrdinalIgnoreCase);
        var profileKey = $"{context.Game.ToString().ToLowerInvariant()}-human-" +
                         (context.Sex == PlayerHairSex.Male ? "male" : "female");
        var proposal = PlayerFacialDetailTexturePolicy.CreateProposal(
            profileKey, current, _registryTextureCandidates, seed);
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
                AppLog.Warning($"Skipped unresolved Player facial detail '{selection.Asset.InstancedPath}': " +
                               exception.Message);
            }
        }
        return prepared;
    }

    private async Task<PreparedMaterialRandomisation?> PreparePlayerFaceScarAsync(
        (TextureCatalogGame Game, PlayerHairSex Sex, string? ScopeKey) context,
        IReadOnlySet<string> requestedTextureNames,
        IReadOnlySet<string> requestedScalarNames,
        int seed)
    {
        var profileKey = $"{context.Game.ToString().ToLowerInvariant()}-human-" +
                         (context.Sex == PlayerHairSex.Male ? "male" : "female");
        var textures = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var appliedFamilies = 0;

        var diffuseName = Material.FindControlName(context.ScopeKey, "HED_Diff",
            MaterialParameterKind.Texture);
        var normalName = Material.FindControlName(context.ScopeKey, "HED_Norm",
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
                .Where(pair => diffuseControl.CanResolveInstancedPath(pair.DiffusePath) &&
                               normalControl.CanResolveInstancedPath(pair.NormalPath))
                .ToArray();
            var face = PlayerFaceScarTexturePolicy.SelectFaceSet(
                profileKey, npcPairs, _registryTextureCandidates, seed);
            if (face is not null)
            {
                try
                {
                    // Resolve both members before applying either one so a missing asset
                    // cannot leave a face diffuse and normal from different sets.
                    var diffuse = face.PlayerDiffuseAsset is { } playerDiffuse
                        ? await diffuseControl.ResolveReferenceAsync(playerDiffuse)
                        : await diffuseControl.ResolveInstancedPathAsync(face.DiffusePath);
                    var normal = face.PlayerNormalAsset is { } playerNormal
                        ? await normalControl.ResolveReferenceAsync(playerNormal)
                        : await normalControl.ResolveInstancedPathAsync(face.NormalPath);
                    textures[diffuseName] = diffuse;
                    textures[normalName] = normal;
                    appliedFamilies++;
                }
                catch (Exception exception)
                {
                    AppLog.Warning($"Skipped unresolved Player face pair '{face.DiffusePath}' / " +
                                   $"'{face.NormalPath}': {exception.Message}");
                }
            }
        }

        var scarName = Material.FindControlName(context.ScopeKey, "HED_Scar",
            MaterialParameterKind.Texture);
        var customStrengthName = Material.FindControlName(context.ScopeKey,
            "HED_Custom_Scar_Scalar", MaterialParameterKind.Scalar);
        var diffuseStrengthName = Material.FindControlName(context.ScopeKey,
            "HED_Scar_Diffuse_Scalar", MaterialParameterKind.Scalar);
        var unlockedScalarNames = Categories.SelectMany(category => category.SliderGroups)
            .Where(group => group.Inclusion?.IsIncluded != false)
            .SelectMany(group => group.Scalars)
            .Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scarStrengthsUnlocked =
            (customStrengthName is null || unlockedScalarNames.Contains(customStrengthName)) &&
            (diffuseStrengthName is null || unlockedScalarNames.Contains(diffuseStrengthName));
        if (scarName is not null && requestedTextureNames.Contains(scarName) && scarStrengthsUnlocked)
        {
            var scar = PlayerFaceScarTexturePolicy.SelectScar(
                profileKey, _registryTextureCandidates, seed);
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
                        AppLog.Warning($"Skipped unresolved Player scar '{asset.InstancedPath}': " +
                                       exception.Message);
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
        MorphFaceEditor.Core.Domain.AssetIdentity? Mesh)?> PreparePlayerHairAsync(
        (TextureCatalogGame Game, PlayerHairSex Sex, string? ScopeKey) context,
        IReadOnlySet<string> requestedTextureNames,
        int seed,
        bool targetedTextureRoll)
    {
        var diffuseControl = Material.FindControlName(context.ScopeKey, "HED_Scalp_Diff",
            MaterialParameterKind.Texture);
        var normalControl = Material.FindControlName(context.ScopeKey, "HED_Scalp_Norm",
            MaterialParameterKind.Texture);
        if (diffuseControl is null || normalControl is null ||
            !requestedTextureNames.Contains(diffuseControl) ||
            !requestedTextureNames.Contains(normalControl)) return null;

        // Roll the supported scalp components as one family. Some Player materials
        // have no Tang parameter, and a partially locked scalp family must stay put.
        var scalpControls = new[]
            {
                "HED_Scalp_Diff", "HED_Scalp_Norm", "HED_Scalp_Spec", "HED_Scalp_Tang"
            }
            .Select(name => Material.FindControlName(context.ScopeKey, name,
                MaterialParameterKind.Texture))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();
        if (scalpControls.Any(name => !requestedTextureNames.Contains(name))) return null;

        var currentScalpPath = Material.Textures.First(value => value.Name.Equals(
            diffuseControl, StringComparison.OrdinalIgnoreCase)).SourceName;
        if (HairMesh.IsRandomisationLocked && PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                context.Game, context.Sex, _registryTextureCandidates,
                _registryAttachmentCandidates, currentScalpPath, HairMesh.Value))
            return null;

        var options = PlayerHairStyleCatalog.Resolve(context.Game, context.Sex,
            _registryTextureCandidates, _registryAttachmentCandidates,
            HairMesh.IsRandomisationLocked || !CanEditAttachments)
            .Where(style => !targetedTextureRoll ||
                style.MeshAction != PlayerHairMeshAction.Set || style.HasDedicatedScalpPair)
            .ToArray();
        if (options.Length == 0) return null;
        var first = new Random(unchecked(seed ^ 0x504C4159)).Next(options.Length);
        for (var attempt = 0; attempt < options.Length; attempt++)
        {
            var style = options[(first + attempt) % options.Length];
            var supportedScalp = style.ScalpTextures
                .Where(member => Material.FindControlName(context.ScopeKey, member.Key,
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
            var selectedStyle = style;
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
            if (selectedStyle.MeshAction == PlayerHairMeshAction.Clear)
            {
                var hairDiffuse = Material.FindControlName(context.ScopeKey, "HAIR_Diff",
                    MaterialParameterKind.Texture);
                if (hairDiffuse is not null) adjustedTextures[hairDiffuse] = null;
            }
            var adjustedScalars = new Dictionary<string, float>(prepared.Scalars,
                StringComparer.OrdinalIgnoreCase);
            if (context.Sex == PlayerHairSex.Male)
            {
                var scalpMask = Material.FindControlName(context.ScopeKey,
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

    private static PreparedMaterialRandomisation? MergePreparedMaterial(
        PreparedMaterialRandomisation? npc,
        IReadOnlyDictionary<string, DecodedTextureAsset?> playerDetails,
        PreparedMaterialRandomisation? playerHair,
        PreparedMaterialRandomisation? playerFaceScar,
        PreparedMaterialRandomisation? playerColours)
    {
        if (npc is null && playerDetails.Count == 0 && playerHair is null &&
            playerFaceScar is null && playerColours is null)
            return null;
        var textures = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        if (npc is not null)
            foreach (var (name, texture) in npc.Textures) textures[name] = texture;
        foreach (var (name, texture) in playerDetails) textures[name] = texture;
        if (playerHair is not null)
            foreach (var (name, texture) in playerHair.Textures) textures[name] = texture;
        if (playerFaceScar is not null)
            foreach (var (name, texture) in playerFaceScar.Textures) textures[name] = texture;
        if (playerColours is not null)
            foreach (var (name, texture) in playerColours.Textures) textures[name] = texture;
        var scalars = new Dictionary<string, float>(npc?.Scalars ??
            new Dictionary<string, float>(), StringComparer.OrdinalIgnoreCase);
        if (playerHair is not null)
            foreach (var (name, value) in playerHair.Scalars) scalars[name] = value;
        if (playerFaceScar is not null)
            foreach (var (name, value) in playerFaceScar.Scalars) scalars[name] = value;
        if (playerColours is not null)
            foreach (var (name, value) in playerColours.Scalars) scalars[name] = value;
        var vectors = new Dictionary<string, System.Numerics.Vector4>(npc?.Vectors ??
            new Dictionary<string, System.Numerics.Vector4>(), StringComparer.OrdinalIgnoreCase);
        if (playerColours is not null)
            foreach (var (name, value) in playerColours.Vectors) vectors[name] = value;
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (npc is not null) failures.UnionWith(npc.FailedTextureFamilySignatures);
        if (playerHair is not null) failures.UnionWith(playerHair.FailedTextureFamilySignatures);
        return new PreparedMaterialRandomisation(
            scalars,
            vectors,
            textures,
            (npc?.AppliedTextureFamilies ?? 0) + playerDetails.Count +
            (playerHair?.AppliedTextureFamilies ?? 0) +
            (playerFaceScar?.AppliedTextureFamilies ?? 0) +
            (playerColours?.AppliedTextureFamilies ?? 0),
            failures);
    }

    private IReadOnlyDictionary<string, float> WithPlayerHairMorphs(
        IReadOnlyDictionary<string, float> proposed,
        (PreparedMaterialRandomisation Material, PlayerHairStyleOption Style,
            MorphFaceEditor.Core.Domain.AssetIdentity? Mesh)? playerHair,
        int seed)
    {
        if (!RandomiseMorphs || HairMesh.IsRandomisationLocked || playerHair is null)
            return proposed;
        var selected = playerHair.Value;
        if (selected.Style.Sex == PlayerHairSex.Female)
        {
            if (selected.Style.MeshAction != PlayerHairMeshAction.Set) return proposed;
            var includedHair = Categories.SelectMany(category => category.SliderGroups)
                .Where(group => group.Inclusion?.IsIncluded != false)
                .SelectMany(group => group.MorphFeatures)
                .Where(feature => feature.IsEditable &&
                    feature.CategoryKey.Equals("facial-structure", StringComparison.OrdinalIgnoreCase) &&
                    feature.SubcategoryKey.Equals("hair", StringComparison.OrdinalIgnoreCase))
                .Select(feature => feature.Name);
            return ClearFemaleHairMorphsForMesh(proposed, includedHair);
        }
        var diffuse = selected.Material.Textures.FirstOrDefault(value =>
            Material.SourceParameterName(value.Key).Equals("HED_Scalp_Diff", StringComparison.OrdinalIgnoreCase));
        if (diffuse.Value is null) return proposed;
        var current = Material.Textures.FirstOrDefault(value =>
            value.Name.Equals(diffuse.Key, StringComparison.OrdinalIgnoreCase))?.SourceName;
        if (string.Equals(current, diffuse.Value.Source.InstancedPath, StringComparison.OrdinalIgnoreCase))
            return proposed;

        var included = Categories.SelectMany(category => category.SliderGroups)
            .Where(group => group.Inclusion?.IsIncluded != false)
            .SelectMany(group => group.MorphFeatures)
            .Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var editable = Features.Where(value => value.IsEditable && included.Contains(value.Name) &&
                HumanMaleHairScalpPolicy.IsHairMorph(value.Name))
            .Select(value => value.Name).ToArray();
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

    private IReadOnlySet<string> EligibleMaterialTextureParameters()
    {
        var profiles = GetMaterialRandomisationProfiles();
        var player = GetPlayerRandomisationContext();
        return Material.Textures
            .Where(texture => profiles.Any(context =>
                    context.Supports(Material, texture.Name, MaterialParameterKind.Texture) &&
                    _randomisationCatalog.EligibleTextureParameters(context.Key)
                        .Contains(Material.SourceParameterName(texture.Name))) ||
                player is { } context &&
                string.Equals(Material.ParameterScopeKey(texture.Name), context.ScopeKey,
                    StringComparison.OrdinalIgnoreCase) &&
                (IsPlayerExclusiveTexture(Material.SourceParameterName(texture.Name)) ||
                 Player2DaParameters.ContainsKey(Material.SourceParameterName(texture.Name))))
            .Select(texture => texture.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<PreparedMaterialRandomisation> PrepareMaterialRandomisationAsync(
        IReadOnlyList<MaterialProfileContext> profiles,
        IReadOnlySet<string> scalarScope,
        IReadOnlySet<string> vectorScope,
        IReadOnlySet<string> requestedTextureNames,
        int seed,
        string? excludedDonorId)
    {
        var claimedScalars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedVectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedTextureFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resultScalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var resultVectors = new Dictionary<string, System.Numerics.Vector4>(StringComparer.OrdinalIgnoreCase);
        var resultTextures = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appliedFamilies = 0;

        for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            var context = profiles[profileIndex];
            var profile = context.Profile;
            var profileScalarControls = scalarScope
                .Where(name => context.Supports(Material, name, MaterialParameterKind.Scalar))
                .Where(name => profile.Scalars.ContainsKey(Material.SourceParameterName(name)))
                .Where(claimedScalars.Add)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var profileVectorControls = vectorScope
                .Where(name => context.Supports(Material, name, MaterialParameterKind.Vector))
                .Where(name => profile.Vectors.ContainsKey(Material.SourceParameterName(name)))
                .Where(claimedVectors.Add)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var profileScalars = profileScalarControls.Select(Material.SourceParameterName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var profileVectors = profileVectorControls.Select(Material.SourceParameterName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentScalars = Material.Scalars
                .Where(value => context.Supports(Material, value.Name, MaterialParameterKind.Scalar))
                .ToDictionary(value => Material.SourceParameterName(value.Name), value => value.Value,
                    StringComparer.OrdinalIgnoreCase);
            var currentVectors = Material.Vectors
                .Where(value => context.Supports(Material, value.Name, MaterialParameterKind.Vector))
                .ToDictionary(value => Material.SourceParameterName(value.Name), value => value.Value,
                    StringComparer.OrdinalIgnoreCase);
            var currentTextures = Material.Textures
                .Where(value => context.Supports(Material, value.Name, MaterialParameterKind.Texture))
                .ToDictionary(value => Material.SourceParameterName(value.Name), value => value.SourceName,
                    StringComparer.OrdinalIgnoreCase);
            var scalarBounds = Material.Scalars
                .Where(value => context.Supports(Material, value.Name, MaterialParameterKind.Scalar))
                .Select(value => new MaterialRandomisationScalarBounds(
                    Material.SourceParameterName(value.Name), value.Minimum, value.Maximum)).ToArray();
            var requestedProfileTextures = requestedTextureNames
                .Where(name => context.Supports(Material, name, MaterialParameterKind.Texture))
                .Select(Material.SourceParameterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availableTextureNames = Material.Textures
                .Where(value => context.Supports(Material, value.Name, MaterialParameterKind.Texture))
                .Select(value => Material.SourceParameterName(value.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var installedDonors = _randomisationCatalog.ProjectInstalledMaterialDonors(
                context.Key, Material.CanResolveTexturePath);
            var compatibleDonors = excludedDonorId is null
                ? installedDonors
                : installedDonors.Where(value =>
                    !value.Id.Equals(excludedDonorId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (compatibleDonors.Count == 0) compatibleDonors = installedDonors;
            var textureFamilies = compatibleDonors
                .SelectMany(value => value.MaterialTextureFamilies)
                .Where(family => IsTextureFamilyInScope(
                    family.Value, requestedProfileTextures, availableTextureNames))
                .Select(value => value.Key)
                .Where(family => claimedTextureFamilies.Add($"{context.ParameterScopeKey}|{family}"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (profileScalars.Count + profileVectors.Count + textureFamilies.Count == 0) continue;

            var profileSeed = unchecked(seed + profileIndex * 7919);
            var donor = _randomisationCatalog.SelectMaterialDonor(context.Key, profileSeed, excludedDonorId);
            var eligibleSignatureCount = compatibleDonors
                .SelectMany(value => value.MaterialTextureFamilies
                    .Where(family => textureFamilies.Contains(family.Key))
                    .Select(family => family.Value))
                .Select(MaterialRandomiser.TextureFamilySignature)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var excludedSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PreparedMaterialRandomisation? prepared = null;
            for (var attempt = 0; attempt <= eligibleSignatureCount; attempt++)
            {
                var proposal = MaterialRandomiser.CreateProposal(
                    donor, compatibleDonors, profile,
                    currentScalars, currentVectors, scalarBounds,
                    profileScalars, profileVectors, textureFamilies,
                    MaterialRandomisationStrength, profileSeed, currentTextures, excludedSignatures);
                var dependentVectors = MaterialRandomiser.DependentVectorNames(
                    profile.ProfileKey, proposal.TextureFamilies, currentTextures);
                prepared = await Material.PrepareRandomisationAsync(
                    proposal.Scalars.Where(value => profileScalars.Contains(value.Key))
                        .Select(value => new
                        {
                            Name = Material.FindControlName(
                                context.ParameterScopeKey, value.Key, MaterialParameterKind.Scalar),
                            value.Value
                        })
                        .Where(value => value.Name is not null)
                        .ToDictionary(value => value.Name!, value => value.Value, StringComparer.OrdinalIgnoreCase),
                    proposal.Vectors.Where(value => profileVectors.Contains(value.Key) ||
                                                     dependentVectors.Contains(value.Key))
                        .Select(value => new
                        {
                            Name = Material.FindControlName(
                                context.ParameterScopeKey, value.Key, MaterialParameterKind.Vector),
                            value.Value
                        })
                        .Where(value => value.Name is not null)
                        .ToDictionary(value => value.Name!, value => value.Value, StringComparer.OrdinalIgnoreCase),
                    proposal.TextureFamilies,
                    context.ParameterScopeKey);
                if (prepared.FailedTextureFamilySignatures.Count == 0) break;
                var added = false;
                foreach (var failure in prepared.FailedTextureFamilySignatures)
                    added |= excludedSignatures.Add(failure);
                if (!added) break;
            }
            if (prepared is null) continue;
            foreach (var value in prepared.Scalars) resultScalars.TryAdd(value.Key, value.Value);
            foreach (var value in prepared.Vectors) resultVectors.TryAdd(value.Key, value.Value);
            foreach (var value in prepared.Textures) resultTextures.TryAdd(value.Key, value.Value);
            appliedFamilies += prepared.AppliedTextureFamilies;
            failures.UnionWith(prepared.FailedTextureFamilySignatures);
            if (WereAllTextureFamiliesRejected(
                    eligibleSignatureCount, excludedSignatures.Count, prepared.AppliedTextureFamilies))
            {
                const string warning =
                    "No readable texture family was available; numeric material values were still randomised.";
                AppLog.Warning(warning);
                _reportError(warning);
            }
        }

        return new PreparedMaterialRandomisation(
            resultScalars, resultVectors, resultTextures, appliedFamilies, failures);
    }

    private IReadOnlyDictionary<string, float> WithSelectedScalpHairMorphs(
        IReadOnlyDictionary<string, float> proposed,
        PreparedMaterialRandomisation? preparedMaterial,
        int seed)
    {
        if (!RandomiseMorphs || !_profileKey.Contains("human-male", StringComparison.OrdinalIgnoreCase) ||
            preparedMaterial is null) return proposed;
        var selectedScalp = preparedMaterial.Textures.FirstOrDefault(value =>
            Material.SourceParameterName(value.Key).Equals("HED_Scalp_Diff", StringComparison.OrdinalIgnoreCase));
        if (selectedScalp.Value is null) return proposed;
        var selectedPath = selectedScalp.Value.Source.InstancedPath;
        var currentPath = Material.Textures.FirstOrDefault(value =>
            value.Name.Equals(selectedScalp.Key, StringComparison.OrdinalIgnoreCase))?.SourceName;
        if (string.Equals(selectedPath, currentPath, StringComparison.OrdinalIgnoreCase)) return proposed;
        var includedMorphNames = Categories.SelectMany(category => category.SliderGroups)
            .Where(group => group.Inclusion?.IsIncluded != false)
            .SelectMany(group => group.MorphFeatures)
            .Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var editableHair = Features.Where(value => value.IsEditable &&
                HumanMaleHairScalpPolicy.IsHairMorph(value.Name) && includedMorphNames.Contains(value.Name))
            .Select(value => value.Name).ToArray();
        var overrides = HumanMaleHairScalpPolicy.CreateMorphOverrides(selectedPath, editableHair, seed);
        if (overrides is null || overrides.Count == 0) return proposed;
        var result = new Dictionary<string, float>(proposed, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in overrides) result[name] = value;
        return result;
    }

    private void RefreshDirtyState() =>
        IsDirty = _session.HasPendingRepair ||
                  !MaterialAssignmentsMatchCleanState() ||
                  !MorphFaceEditorStateComparer.Equals(_cleanState, CreateDraft());

    private void OnMaterialControlsChanged(object? sender, EventArgs e)
    {
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
        _session.EvaluationChanged -= OnEvaluationChanged;
        _history.HistoryChanged -= OnHistoryChanged;
        Material.PreviewChanged -= OnMaterialPreviewChanged;
        Material.ControlsChanged -= OnMaterialControlsChanged;
        HairMesh.PropertyChanged -= OnHairMeshPropertyChanged;
        foreach (var slot in CustomMaterialSlots) slot.Dispose();
        Material.Dispose();
        HairMesh.Dispose();
        foreach (var otherMesh in OtherMeshes)
        {
            otherMesh.Dispose();
        }
        _history.Dispose();
        _disposed = true;
    }
}
