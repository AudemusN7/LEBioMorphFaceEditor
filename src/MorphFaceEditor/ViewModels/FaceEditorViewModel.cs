using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.ViewModels;

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
        bool previewOnlyAttachments = false)
    {
        _session = session;
        _metadataCatalog = metadataCatalog;
        _customMaterialWorkspace = customMaterialWorkspace;
        _customMaterialOptions = customMaterialOptions ?? [];
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
            () => Randomise(GlobalRandomisationScope(CursedMode), includeCursedBones: true),
            () => CanRandomiseScope(GlobalRandomisationScope(CursedMode)));
        HairMesh = new HairMeshEditorViewModel(
            hairSession, meshCandidates, previewOnlyAttachments ? "Hair mesh (preview only)" : "m_oHairMesh", 0);
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
        bool isRegistryAvailable) =>
        Material.UpdateRegistryCandidates(candidates, profile, isRegistryAvailable);
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
    public bool CanRandomise => CanRandomiseScope(GlobalRandomisationScope(CursedMode));
    public bool AllowsMorphRandomisation => CanEditMorphFeatures && _allowsMorphRandomisation;
    public bool AllowsMaterialRandomisation =>
        Material.Scalars.Count + Material.Vectors.Count + Material.Textures.Count > 0;
    public bool AllowsCursedRandomisation =>
        AllowsMorphRandomisation || CanEditBones || AllowsMaterialRandomisation;
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
            if (CursedMode)
            {
                await RandomiseCursedAsync(requested, includeCursedBones);
                return;
            }

            var morphScope = RandomiseMorphs
                ? requested.MorphFeatures.Where(value => value.IsEditable)
                    .Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var materialProfiles = GetMaterialRandomisationProfiles();
            var scalarScope = RandomiseMaterials
                ? requested.Scalars.Select(value => value.Name)
                    .Where(name => materialProfiles.Any(value =>
                        value.Supports(Material, name, MaterialParameterKind.Scalar) &&
                        value.Profile.Scalars.ContainsKey(Material.SourceParameterName(name))))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vectorScope = RandomiseMaterials
                ? requested.Vectors.Select(value => value.Name)
                    .Where(name => materialProfiles.Any(value =>
                        value.Supports(Material, name, MaterialParameterKind.Vector) &&
                        value.Profile.Vectors.ContainsKey(Material.SourceParameterName(name))))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requestedTextureNames = RandomiseMaterials
                ? requested.Textures.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            PreparedMaterialRandomisation? preparedMaterial = null;
            var materialValueCount = 0;
            if (hasMaterial && materialProfiles.Count > 0)
            {
                preparedMaterial = await PrepareMaterialRandomisationAsync(
                    materialProfiles, scalarScope, vectorScope, requestedTextureNames,
                    materialSeed!.Value, morphDonor?.Id);
                materialValueCount = scalarScope.Count + vectorScope.Count;
            }

            using (var aggregate = _history.BeginAggregate())
            {
                if (morphProposal is not null) _session.SetFeatures(morphProposal.Values);
                if (preparedMaterial is not null) Material.ApplyRandomisation(preparedMaterial);
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
        var features = RandomiseMorphs
            ? requested.MorphFeatures.Where(value => value.IsEditable)
                .ToDictionary(value => value.Name, value => _cursedBaseline.Features[value.Name], StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
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
        if (RandomiseMaterials && requested.Textures.Count > 0)
        {
            var profileIndex = 0;
            foreach (var context in GetMaterialRandomisationProfiles())
            {
                var requestedNames = requested.Textures
                    .Where(value => context.Supports(Material, value.Name, MaterialParameterKind.Texture))
                    .Select(value => Material.SourceParameterName(value.Name))
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
        var preparedMaterial = await Material.PrepareRandomisationAsync(
            materialValues.ScalarValues, materialValues.VectorValues,
            cursedTextureFamilies);
        CursedMorphRandomisationExtras? boneValues = null;
        Dictionary<string, System.Numerics.Vector3>? nextBoneOffsets = null;
        var fuzzedBoneCount = 0;

        using (var aggregate = _history.BeginAggregate())
        {
            if (featureValues.Count > 0) _session.SetFeatures(featureValues);
            if (includeBones && CanEditBones)
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
            if (scalars.Count + vectors.Count + preparedMaterial.Textures.Count > 0)
                Material.ApplyRandomisation(preparedMaterial);
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

    private EditorRandomisationScope GlobalRandomisationScope(bool includeExcluded) => new(
        Categories.SelectMany(category => category.SliderGroups)
            .Where(group => includeExcluded || group.Inclusion?.IsIncluded != false)
            .SelectMany(group => group.MorphFeatures).Distinct().ToArray(),
        Categories.SelectMany(category => category.SliderGroups)
            .Where(group => includeExcluded || group.Inclusion?.IsIncluded != false)
            .SelectMany(group => group.Scalars).Distinct().ToArray(),
        Categories.SelectMany(category => category.ColourGroups)
            .Where(group => includeExcluded || group.Inclusion?.IsIncluded != false)
            .SelectMany(group => group.Values).Distinct().ToArray(),
        Categories.Where(category => includeExcluded || category.TextureInclusion?.IsIncluded != false)
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

    private bool CanRandomiseScope(EditorRandomisationScope scope)
    {
        var hasMorph = CanEditMorphFeatures && RandomiseMorphs && scope.MorphFeatures.Any(value => value.IsEditable);
        var hasRawNumericMaterial = RandomiseMaterials &&
                                    (scope.Scalars.Count + scope.Vectors.Count > 0);
        var materialProfiles = GetMaterialRandomisationProfiles();
        var eligibleTextures = EligibleMaterialTextureParameters();
        var hasEligibleTexture = RandomiseMaterials &&
                                 scope.Textures.Any(value => eligibleTextures.Contains(value.Name)) &&
                                 materialProfiles.Count > 0;
        var hasCursedBones = CursedMode && CanEditBones;
        if (CursedMode) return hasMorph || hasCursedBones || hasRawNumericMaterial || hasEligibleTexture;
        var hasEligibleMorph = hasMorph && _randomisationCatalog.HasDonors(_profileKey);
        var hasEligibleMaterial = RandomiseMaterials && materialProfiles.Count > 0 &&
            (scope.Scalars.Any(value => materialProfiles.Any(profile =>
                 profile.Supports(Material, value.Name, MaterialParameterKind.Scalar) &&
                 profile.Profile.Scalars.ContainsKey(Material.SourceParameterName(value.Name)))) ||
             scope.Vectors.Any(value => materialProfiles.Any(profile =>
                 profile.Supports(Material, value.Name, MaterialParameterKind.Vector) &&
                 profile.Profile.Vectors.ContainsKey(Material.SourceParameterName(value.Name)))) ||
             scope.Textures.Any(value => eligibleTextures.Contains(value.Name)));
        return hasEligibleMorph || hasEligibleMaterial;
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

    private IReadOnlySet<string> EligibleMaterialTextureParameters() =>
        Material.Textures
            .Where(texture => GetMaterialRandomisationProfiles().Any(context =>
                context.Supports(Material, texture.Name, MaterialParameterKind.Texture) &&
                _randomisationCatalog.EligibleTextureParameters(context.Key)
                    .Contains(Material.SourceParameterName(texture.Name))))
            .Select(texture => texture.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
