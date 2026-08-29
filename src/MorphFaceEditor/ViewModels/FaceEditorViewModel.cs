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
    private readonly Func<int> _randomSeedFactory;
    private readonly Action<string> _reportError;
    private MorphFaceEditor.Core.Domain.MorphFaceDocument _cleanState;
    private EditorFeatureCategoryViewModel? _selectedCategory;
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
        bool isTextureRegistryAvailable = false)
    {
        _session = session;
        _profileKey = profileKey;
        _randomisationCatalog = randomisationCatalog ?? MorphRandomisationCatalog.Empty;
        _randomSeedFactory = randomSeedFactory ?? Random.Shared.Next;
        _reportError = reportError;
        _randomisationInclusionState = randomisationInclusionState ?? new RandomisationInclusionState();
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
                    metadataCatalog.Describe(feature, session.CanEdit),
                    feature,
                    session.AvailableLodIndices),
                feature.Target is null || feature.Target.BoneOffsets.Count > 0
                    ? null
                    : feature.Target.Lods
                        .Where(lod => lod.Vertices.Count > 0)
                        .Select(lod => lod.LodIndex)
                        .ToHashSet(),
                reportError))
            .Where(feature => feature.IsVisible)
            .ToArray();
        Bones = session.FinalSkeleton
            .SelectMany(bone => Enumerable.Range(0, 3)
                .Select(axis => new BoneAxisEditorViewModel(session, bone.BoneName, axis)))
            .ToArray();
        var hairSession = new AssetReferenceEditingSession(hairMeshReference);
        var otherMeshSessions = Enumerable.Range(0, 1)
            .Select(index => new AssetReferenceEditingSession(otherMeshReferences.ElementAtOrDefault(index)))
            .ToArray();
        _preservedOtherMeshes = otherMeshReferences.Skip(1).ToArray();
        _history = new EditorUndoCoordinator(
            [session, materialSession, hairSession, .. otherMeshSessions]);
        _undoCommand = new RelayCommand(_history.Undo, () => _history.CanUndo);
        _redoCommand = new RelayCommand(_history.Redo, () => _history.CanRedo);
        _setToDefaultsCommand = new RelayCommand(SetToDefaults, () => CanEdit);
        _randomiseCommand = new RelayCommand(
            () => Randomise(GlobalRandomisationScope(CursedMode), includeCursedBones: true),
            () => CanRandomiseScope(GlobalRandomisationScope(CursedMode)));
        HairMesh = new HairMeshEditorViewModel(hairSession, meshCandidates, "m_oHairMesh", 0);
        OtherMeshes = otherMeshSessions
            .Select((otherSession, index) => new HairMeshEditorViewModel(
                otherSession,
                meshCandidates,
                "m_oOtherMeshes",
                index + 1))
            .ToArray();
        AttachmentMeshes = [HairMesh, .. OtherMeshes];
        Material = new MaterialEditorViewModel(
            materialSession, colorDialog, references, packagePath, textureCandidates, reportError, metadataCatalog,
            registryTextureCandidates, textureCatalogProfile, isTextureRegistryAvailable);
        Categories = metadataCatalog.Categories
            .Select(category => new EditorFeatureCategoryViewModel(
                category,
                Features.Where(value => value.CategoryKey == category.Key).ToArray(),
                Material.Scalars.Where(value => value.CategoryKey == category.Key).ToArray(),
                Material.Vectors.Where(value => value.CategoryKey == category.Key).ToArray(),
                Material.Textures.Where(value => value.CategoryKey == category.Key).ToArray(),
                metadataCatalog,
                scope =>
                {
                    var eligibleTextures = _randomisationCatalog.EligibleTextureParameters(_profileKey);
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
        _selectedCategory = Categories.FirstOrDefault();
        session.EvaluationChanged += OnEvaluationChanged;
        _history.HistoryChanged += OnHistoryChanged;
        Material.PreviewChanged += OnMaterialPreviewChanged;
        _cleanState = CreateDraft();
    }

    public event EventHandler? PreviewChanged;
    public event EventHandler? MaterialPreviewChanged;
    public event EventHandler? DirtyStateChanged;

    public IReadOnlyList<MorphFeatureEditorViewModel> Features { get; }
    public IReadOnlyList<EditorFeatureCategoryViewModel> Categories { get; }
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
    public bool CanEdit => _session.CanEdit;
    public bool CanRandomise => CanRandomiseScope(GlobalRandomisationScope(CursedMode));
    public bool CursedMode
    {
        get => _cursedMode;
        set
        {
            if (SetProperty(ref _cursedMode, value))
            {
                if (value)
                {
                    RandomiseMorphs = true;
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
            if (SetProperty(ref _randomiseMorphs, value)) RaiseRandomisationCanExecuteChanged();
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
    public void ApplyMorphData(MorphFaceEditor.Core.Domain.MorphFaceMorphData data)
    {
        _session.ApplyMorphData(data);
        RefreshDirtyState();
    }
    private void SetToDefaults()
    {
        using var aggregate = _history.BeginAggregate();
        _session.ResetToDefaults();
        Material.ResetToDefaults();
        aggregate.Commit();
        _cursedBaseline = null;
        _cursedBoneOffsets.Clear();
    }
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
            var materialProfile = _randomisationCatalog.GetMaterialProfile(_profileKey);
            var scalarScope = RandomiseMaterials && materialProfile is not null
                ? requested.Scalars.Select(value => value.Name)
                    .Where(materialProfile.Scalars.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vectorScope = RandomiseMaterials && materialProfile is not null
                ? requested.Vectors.Select(value => value.Name)
                    .Where(materialProfile.Vectors.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
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
            if (hasMaterial && materialProfile is not null)
            {
                var materialDonor = _randomisationCatalog.SelectMaterialDonor(
                    _profileKey, materialSeed!.Value, morphDonor?.Id);
                var installedMaterialDonors = _randomisationCatalog.ProjectInstalledMaterialDonors(
                    _profileKey, Material.CanResolveTexturePath);
                var donorsFromOtherHeads = morphDonor is null
                    ? installedMaterialDonors
                    : installedMaterialDonors.Where(value =>
                        !value.Id.Equals(morphDonor.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
                var compatibleMaterialDonors = donorsFromOtherHeads.Count > 0
                    ? donorsFromOtherHeads
                    : installedMaterialDonors;
                var availableTextureNames = Material.Textures.Select(value => value.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var textureFamilies = compatibleMaterialDonors
                    .SelectMany(value => value.MaterialTextureFamilies)
                    .Where(family => IsTextureFamilyInScope(
                        family.Value,
                        requestedTextureNames,
                        availableTextureNames))
                    .Select(value => value.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var currentScalars = Material.Scalars.ToDictionary(
                    value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
                var currentVectors = Material.Vectors.ToDictionary(
                    value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
                var currentTextures = Material.Textures.ToDictionary(
                    value => value.Name, value => value.SourceName, StringComparer.OrdinalIgnoreCase);
                var scalarBounds = Material.Scalars.Select(value => new MaterialRandomisationScalarBounds(
                    value.Name, value.Minimum, value.Maximum)).ToArray();
                var eligibleSignatureCount = compatibleMaterialDonors
                    .SelectMany(value => value.MaterialTextureFamilies
                        .Where(family => textureFamilies.Contains(family.Key))
                        .Select(family => family.Value))
                    .Select(MaterialRandomiser.TextureFamilySignature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var excludedSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var attempt = 0; attempt <= eligibleSignatureCount; attempt++)
                {
                    var materialProposal = MaterialRandomiser.CreateProposal(
                        materialDonor, compatibleMaterialDonors, materialProfile,
                        currentScalars, currentVectors, scalarBounds,
                        scalarScope, vectorScope, textureFamilies, MaterialRandomisationStrength, materialSeed.Value,
                        currentTextures, excludedSignatures);
                    var dependentVectors = MaterialRandomiser.DependentVectorNames(
                        materialProfile.ProfileKey, materialProposal.TextureFamilies, currentTextures);
                    preparedMaterial = await Material.PrepareRandomisationAsync(
                        materialProposal.Scalars.Where(value => scalarScope.Contains(value.Key))
                            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase),
                        materialProposal.Vectors.Where(value => vectorScope.Contains(value.Key) ||
                                                                 dependentVectors.Contains(value.Key))
                            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase),
                        materialProposal.TextureFamilies);
                    if (preparedMaterial.FailedTextureFamilySignatures.Count == 0) break;
                    var added = false;
                    foreach (var failedSignature in preparedMaterial.FailedTextureFamilySignatures)
                        added |= excludedSignatures.Add(failedSignature);
                    if (!added) break;
                }
                if (WereAllTextureFamiliesRejected(
                        eligibleSignatureCount,
                        excludedSignatures.Count,
                        preparedMaterial?.AppliedTextureFamilies ?? 0))
                {
                    const string warning =
                        "No readable texture family was available; numeric material values were still randomised.";
                    AppLog.Warning(warning);
                    _reportError(warning);
                }
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
        if (RandomiseMaterials && requested.Textures.Count > 0 &&
            _randomisationCatalog.HasMaterialDonors(_profileKey))
        {
            var donor = _randomisationCatalog.SelectDonor(_profileKey, seed, requireMaterial: true);
            var requestedNames = requested.Textures.Select(value => value.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availableTextureNames = Material.Textures.Select(value => value.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            cursedTextureFamilies = donor.MaterialTextureFamilies
                .Where(family => IsTextureFamilyInScope(
                    family.Value,
                    requestedNames,
                    availableTextureNames))
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
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
            if (includeBones && RandomiseMorphs)
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
        if (!CanEdit) return false;
        var hasMorph = RandomiseMorphs && scope.MorphFeatures.Any(value => value.IsEditable);
        var hasRawNumericMaterial = RandomiseMaterials &&
                                    (scope.Scalars.Count + scope.Vectors.Count > 0);
        var eligibleTextures = _randomisationCatalog.EligibleTextureParameters(_profileKey);
        var hasEligibleTexture = RandomiseMaterials &&
                                 scope.Textures.Any(value => eligibleTextures.Contains(value.Name)) &&
                                 _randomisationCatalog.HasMaterialDonors(_profileKey);
        if (CursedMode) return hasMorph || hasRawNumericMaterial || hasEligibleTexture;
        var hasEligibleMorph = hasMorph && _randomisationCatalog.HasDonors(_profileKey);
        var profile = _randomisationCatalog.GetMaterialProfile(_profileKey);
        var hasEligibleMaterial = RandomiseMaterials && profile is not null &&
            (scope.Scalars.Any(value => profile.Scalars.ContainsKey(value.Name)) ||
             scope.Vectors.Any(value => profile.Vectors.ContainsKey(value.Name)) ||
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
    public async Task ApplyMaterialDataAsync(MorphFaceEditor.Core.Domain.MorphFaceMaterialData data)
    {
        await Material.ApplyDataAsync(data);
        RefreshDirtyState();
    }
    private void OnEvaluationChanged(object? sender, EventArgs e)
    {
        foreach (var feature in Features)
        {
            feature.Refresh();
        }
        foreach (var bone in Bones)
        {
            bone.Refresh();
        }
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        _undoCommand.RaiseCanExecuteChanged();
        _redoCommand.RaiseCanExecuteChanged();
        RefreshDirtyState();
    }

    private void RefreshDirtyState() =>
        IsDirty = !MorphFaceEditorStateComparer.Equals(_cleanState, CreateDraft());

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
