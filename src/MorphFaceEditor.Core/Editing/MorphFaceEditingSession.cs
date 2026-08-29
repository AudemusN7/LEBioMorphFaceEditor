using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Editing;

public sealed record MorphFaceEvaluation(
    DeformationResult Geometry,
    IReadOnlyDictionary<int, DeformationResult> LodGeometry,
    IReadOnlyList<BoneTranslation> FinalSkeleton,
    SkeletalPose Pose,
    MorphTargetResolution Resolution,
    DeformationComparisonReport? OriginalOracleReport);

public sealed class MorphFaceEditingSession : IUndoableEditSource
{
    private const float OracleTolerance = 0.0001f;
    private readonly MorphFaceDocument _document;
    private readonly SkeletalMeshAsset _baseHead;
    private readonly IReadOnlyList<MorphTargetAsset> _targets;
    private readonly IReadOnlySet<string> _metadataOnlyFeatures;
    private readonly string _profileName;
    private readonly IReadOnlyDictionary<string, string> _aliases;
    private readonly Func<DeformationComparisonReport, bool>? _recognizesBaseVariant;
    private readonly string? _geometryEditBlockReason;
    private IReadOnlyList<Vector3[]> _positionCorrections = [];
    private readonly Dictionary<string, float> _features;
    private readonly HashSet<string> _originalFeatureNames;
    private readonly IReadOnlyList<string> _featureOrder;
    private readonly Dictionary<string, Vector3> _boneOverrides;
    private readonly IReadOnlyList<BoneTranslation> _defaultTemplateBones;
    private readonly IReadOnlyDictionary<string, Vector3> _defaultBoneOverrides;
    private IReadOnlyList<BoneTranslation> _templateBones;
    private HashSet<string>? _pastedBoneNames;
    private readonly SemanticEditHistory _history = new();
    private bool _replayingHistory;

    public MorphFaceEditingSession(
        MorphFaceDocument document,
        SkeletalMeshAsset baseHead,
        IReadOnlyList<MorphTargetAsset> targets,
        IReadOnlySet<string>? metadataOnlyFeatures = null,
        string profileName = "Human Male",
        IReadOnlyDictionary<string, string>? aliases = null,
        Func<DeformationComparisonReport, bool>? recognizesBaseVariant = null,
        string? geometryEditBlockReason = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _baseHead = baseHead ?? throw new ArgumentNullException(nameof(baseHead));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _metadataOnlyFeatures = metadataOnlyFeatures ?? MorphFeatureTargetResolver.HumanMaleMetadataOnlyFeatures;
        _profileName = profileName;
        _aliases = aliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _recognizesBaseVariant = recognizesBaseVariant;
        _geometryEditBlockReason = string.IsNullOrWhiteSpace(geometryEditBlockReason)
            ? null
            : geometryEditBlockReason;
        _features = document.MorphFeatures.ToDictionary(
            feature => feature.Name,
            feature => feature.Offset,
            StringComparer.OrdinalIgnoreCase);
        _originalFeatureNames = document.MorphFeatures
            .Select(feature => feature.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The editing surface represents the capabilities of the loaded head profile,
        // not merely the sparse list that this particular BioMorphFace happens to use.
        // Newly exposed entries remain sparse on save until the user gives them a value.
        var availableNames = targets
            .Select(target => GetObjectName(target.Source.InstancedPath))
            .Concat(_aliases.Keys)
            .Concat(_metadataOnlyFeatures)
            .Concat(document.MorphFeatures.Select(feature => feature.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var name in availableNames)
        {
            _features.TryAdd(name, 0);
        }
        _featureOrder = availableNames;

        _templateBones = document.FinalSkeleton.ToArray();
        var resolution = Resolve();
        var composed = MorphBoneOffsetComposer.Compose(
            baseHead.Topology.ReferenceSkeleton,
            _templateBones,
            resolution.WeightedTargets);
        var composedByName = composed.ToDictionary(
            bone => bone.BoneName,
            StringComparer.OrdinalIgnoreCase);
        _boneOverrides = document.FinalSkeleton
            .ToDictionary(
                bone => bone.BoneName,
                bone => bone.Translation - composedByName[bone.BoneName].Translation,
                StringComparer.OrdinalIgnoreCase);
        _defaultTemplateBones = _templateBones.ToArray();
        _defaultBoneOverrides = new Dictionary<string, Vector3>(
            _boneOverrides, StringComparer.OrdinalIgnoreCase);
        _positionCorrections = DetectPositionCorrections(resolution);
        Evaluation = Evaluate(includeOracle: true);
        ValidationErrors = ValidateEditableTargets(Evaluation.Resolution);
        CanEdit = _geometryEditBlockReason is null &&
                  Evaluation.Resolution.UnresolvedFeatureNames.Count == 0 &&
                  Evaluation.OriginalOracleReport is { IsWithinTolerance: true } &&
                  ValidationErrors.Count == 0;
    }

    public event EventHandler? EvaluationChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? EditCommitted;

    public MorphFaceEvaluation Evaluation { get; private set; }
    public bool CanEdit { get; }
    public IReadOnlyList<string> ValidationErrors { get; }
    public string? EditBlockReason
    {
        get
        {
            if (CanEdit)
            {
                return null;
            }
            if (_geometryEditBlockReason is not null)
            {
                return _geometryEditBlockReason;
            }
            if (Evaluation.Resolution.UnresolvedFeatureNames.Count > 0)
            {
                return $"Unresolved features: {string.Join(", ", Evaluation.Resolution.UnresolvedFeatureNames.Take(5))}.";
            }
            var report = Evaluation.OriginalOracleReport;
            if (report is not { IsWithinTolerance: true })
            {
                return report is null
                    ? "The baked deformation oracle was unavailable."
                    : $"The baked deformation oracle missed by {report.MaximumError:G6}.";
            }
            return ValidationErrors.FirstOrDefault() ?? "Profile validation failed.";
        }
    }
    public bool UsesBaseVariantCorrection => _positionCorrections.Count > 0;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public IReadOnlyList<MorphFeatureValue> Features => _featureOrder
        .Select(name => new MorphFeatureValue(name, _features[name]))
        .ToArray();
    public IReadOnlyList<BoneTranslation> FinalSkeleton => Evaluation.FinalSkeleton;
    public IReadOnlyList<int> AvailableLodIndices => _baseHead.AvailableLodPositions
        .Select((_, index) => index)
        .ToArray();

    public MorphMeshFitResult FitMeshPositions(IReadOnlyList<MorphMeshPositionCandidate> candidates)
    {
        if (!CanEdit)
        {
            throw new InvalidOperationException(
                "Mesh import requires a selected template whose profile passes live geometry validation.");
        }
        return MorphMeshInverter.Fit(
            _baseHead,
            Evaluation.Resolution.Features,
            candidates,
            _positionCorrections,
            _document.BakedLods);
    }

    public MorphFaceDocument CreateDraft(
        AssetIdentity? hairMeshReference,
        IReadOnlyList<AssetIdentity?> otherMeshReferences,
        MorphFaceMaterialOverrides materialOverrides)
    {
        if (!CanEdit)
        {
            // Material-only fallback saves must never rewrite geometry from a
            // target set which failed validation or was explicitly blocked.
            // Preserve morph features, final bones, and every baked LOD exactly.
            return _document with
            {
                HairMeshReference = hairMeshReference,
                OtherMeshReferences = otherMeshReferences,
                MaterialOverrides = materialOverrides
            };
        }

        var lods = _document.BakedLods.Select(lod => lod.ToArray()).ToArray();
        if (lods.Length > 0)
        {
            lods[0] = Evaluation.Geometry.Positions.ToArray();
        }
        var weightedTargets = Evaluation.Resolution.WeightedTargets;
        var baseLods = _baseHead.AvailableLodPositions;
        for (var lodIndex = 1; lodIndex < Math.Min(lods.Length, baseLods.Count); lodIndex++)
        {
            var basePositions = baseLods[lodIndex];
            var lodTargets = weightedTargets
                .Where(weighted => weighted.Target.Lods.Any(lod => lod.LodIndex == lodIndex))
                .ToArray();
            var canBake = lodTargets.Length > 0 &&
                          basePositions.Length == lods[lodIndex].Length &&
                          lodTargets.All(weighted =>
                              weighted.Target.Lods.First(lod => lod.LodIndex == lodIndex).BaseMeshVertexCount ==
                              basePositions.Length);
            if (canBake)
            {
                lods[lodIndex] = SparseMorphEvaluator.EvaluatePositions(basePositions, lodTargets, lodIndex);
                ApplyCorrection(lods[lodIndex], lodIndex);
            }
        }
        return _document with
        {
            HairMeshReference = hairMeshReference,
            OtherMeshReferences = otherMeshReferences,
            MorphFeatures = Features
                .Where(feature => _originalFeatureNames.Contains(feature.Name) || feature.Offset != 0)
                .ToArray(),
            FinalSkeleton = _pastedBoneNames is null
                ? FinalSkeleton
                : FinalSkeleton.Where(value => _pastedBoneNames.Contains(value.BoneName)).ToArray(),
            MaterialOverrides = materialOverrides,
            BakedLods = lods
        };
    }

    public MorphFaceDocument CreateDraft(
        AssetIdentity? hairMeshReference,
        MorphFaceMaterialOverrides materialOverrides) =>
        CreateDraft(hairMeshReference, _document.OtherMeshReferences, materialOverrides);

    public float GetFeature(string name) => _features[name];

    public float GetBoneAxis(string boneName, int axis)
    {
        if (TryGetBoneAxis(boneName, axis, out var value)) return value;
        throw new KeyNotFoundException($"Bone '{boneName}' is not present in the current evaluated skeleton.");
    }

    public bool TryGetBoneAxis(string boneName, int axis, out float value)
    {
        var bone = Evaluation.FinalSkeleton.FirstOrDefault(candidate =>
            string.Equals(candidate.BoneName, boneName, StringComparison.OrdinalIgnoreCase));
        if (bone is null)
        {
            value = 0;
            return false;
        }
        value = axis switch
        {
            0 => bone.Translation.X,
            1 => bone.Translation.Y,
            2 => bone.Translation.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
        return true;
    }

    public void BeginFeatureEdit(string name) => BeginEdit(FeatureKey(name), GetFeature(name));
    public void EndFeatureEdit(string name) => EndEdit(FeatureKey(name), GetFeature(name));
    public void BeginBoneEdit(string boneName, int axis) => BeginEdit(BoneKey(boneName, axis), GetBoneAxis(boneName, axis));
    public void EndBoneEdit(string boneName, int axis) => EndEdit(BoneKey(boneName, axis), GetBoneAxis(boneName, axis));

    public void SetFeature(string name, float value)
    {
        EnsureEditable();
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        var before = GetFeature(name);
        if (before == value)
        {
            return;
        }
        _features[name] = value;
        if (!_replayingHistory)
        {
            if (_history.Record(FeatureKey(name), before, value))
            {
                EditCommitted?.Invoke(this, EventArgs.Empty);
            }
        }
        Refresh();
    }

    /// <summary>
    /// Applies a complete logical randomisation as one validated mutation, evaluation and history entry.
    /// </summary>
    public void SetFeatures(IReadOnlyDictionary<string, float> values)
    {
        EnsureEditable();
        ArgumentNullException.ThrowIfNull(values);
        var desired = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(values),
                    $"Feature '{name}' has a non-finite value.");
            }
            if (!desired.TryAdd(name, value))
            {
                throw new ArgumentException($"Feature '{name}' occurs more than once.", nameof(values));
            }
        }

        var unknown = desired.Keys.Where(name => !_features.ContainsKey(name)).ToArray();
        if (unknown.Length > 0)
        {
            throw new KeyNotFoundException(
                $"Unsupported morph features: {string.Join(", ", unknown.Take(5))}.");
        }

        var before = desired
            .Where(value => _features[value.Key] != value.Value)
            .ToDictionary(value => value.Key, value => _features[value.Key], StringComparer.OrdinalIgnoreCase);
        if (before.Count == 0)
        {
            return;
        }
        var after = before.Keys.ToDictionary(name => name, name => desired[name], StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in after)
        {
            _features[name] = value;
        }
        if (!_replayingHistory && _history.Record(new SemanticFeatureBatchEdit(before, after)))
        {
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }
        Refresh();
    }

    public void SetBoneAxis(string boneName, int axis, float value)
    {
        EnsureEditable();
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        var before = GetBoneAxis(boneName, axis);
        if (before == value)
        {
            return;
        }
        var overrideValue = _boneOverrides.GetValueOrDefault(boneName);
        var delta = value - before;
        overrideValue = axis switch
        {
            0 => overrideValue with { X = overrideValue.X + delta },
            1 => overrideValue with { Y = overrideValue.Y + delta },
            2 => overrideValue with { Z = overrideValue.Z + delta },
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
        _boneOverrides[boneName] = overrideValue;
        _pastedBoneNames?.Add(boneName);
        if (!_replayingHistory)
        {
            if (_history.Record(BoneKey(boneName, axis), before, value))
            {
                EditCommitted?.Invoke(this, EventArgs.Empty);
            }
        }
        Refresh();
    }

    /// <summary>Restores the construction-time morph and final-skeleton state as one undoable edit.</summary>
    public void ResetToDefaults()
    {
        EnsureEditable();
        var before = CaptureAuthoringState();
        var after = new MorphFaceAuthoringState(
            _featureOrder.ToDictionary(name => name, _ => 0f, StringComparer.OrdinalIgnoreCase),
            _defaultTemplateBones.ToArray(),
            new Dictionary<string, Vector3>(_defaultBoneOverrides, StringComparer.OrdinalIgnoreCase),
            null);
        if (AuthoringStatesEqual(before, after))
        {
            return;
        }

        RestoreAuthoringState(after);
        if (!_replayingHistory && _history.Record(new SemanticMorphStateEdit(before, after)))
        {
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }
        Refresh();
    }

    /// <summary>
    /// Replaces the complete live geometry-authoring state without flushing it
    /// to the package workspace. The owning view model treats this like a
    /// multi-control edit and supplies the normal dirty/abandon workflow.
    /// </summary>
    public void ApplyMorphData(MorphFaceMorphData data)
    {
        EnsureEditable();
        ArgumentNullException.ThrowIfNull(data);
        var desiredFeatures = data.MorphFeatures.ToDictionary(
            value => value.Name,
            value => value.Offset,
            StringComparer.OrdinalIgnoreCase);
        if (desiredFeatures.Count != data.MorphFeatures.Count ||
            desiredFeatures.Any(value => string.IsNullOrWhiteSpace(value.Key) || !float.IsFinite(value.Value)))
        {
            throw new InvalidDataException("Pasted morph features must have unique names and finite values.");
        }
        var unknownFeatures = desiredFeatures.Keys.Where(name => !_features.ContainsKey(name)).ToArray();
        if (unknownFeatures.Length > 0)
        {
            throw new InvalidOperationException(
                $"The pasted profile contains unsupported morph features: {string.Join(", ", unknownFeatures.Take(5))}.");
        }
        if (data.FinalSkeleton.Any(value =>
                string.IsNullOrWhiteSpace(value.BoneName) || !IsFinite(value.Translation)) ||
            data.FinalSkeleton.Select(value => value.BoneName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.FinalSkeleton.Count)
        {
            throw new InvalidDataException("Pasted final-skeleton entries must have unique names and finite values.");
        }
        var referenceBones = _baseHead.Topology.ReferenceSkeleton
            .Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownBones = data.FinalSkeleton
            .Select(value => value.BoneName)
            .Where(name => !referenceBones.Contains(name))
            .ToArray();
        if (unknownBones.Length > 0)
        {
            throw new InvalidOperationException(
                $"The pasted profile contains unsupported final-skeleton bones: {string.Join(", ", unknownBones.Take(5))}.");
        }

        var previouslyExposedBones = Evaluation.FinalSkeleton;
        foreach (var name in _featureOrder)
        {
            _features[name] = desiredFeatures.GetValueOrDefault(name);
        }
        _pastedBoneNames = data.FinalSkeleton
            .Select(value => value.BoneName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _templateBones = data.FinalSkeleton
            .Concat(previouslyExposedBones.Where(value => !_pastedBoneNames.Contains(value.BoneName)))
            .ToArray();
        _boneOverrides.Clear();
        var composed = MorphBoneOffsetComposer.Compose(
            _baseHead.Topology.ReferenceSkeleton,
            _templateBones,
            Resolve().WeightedTargets);
        var composedByName = composed.ToDictionary(value => value.BoneName, StringComparer.OrdinalIgnoreCase);
        foreach (var desired in data.FinalSkeleton)
        {
            _boneOverrides[desired.BoneName] =
                desired.Translation - composedByName[desired.BoneName].Translation;
        }
        Refresh();
    }

    /// <summary>
    /// Captures game-independent slider values and manual final-skeleton
    /// residuals. Baked vertices and bind-pose coordinates are profile-owned.
    /// </summary>
    public MorphFaceSemanticTransferData CaptureSemanticTransferData() => new(
        Features,
        _boneOverrides
            .OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .Select(value => new BoneTranslation(value.Key, value.Value))
            .ToArray());

    /// <summary>
    /// Projects semantic authoring state onto this session's profile. Exact
    /// feature/bone names transfer; destination gaps retain neutral values.
    /// </summary>
    public void ApplySemanticTransferData(MorphFaceSemanticTransferData data)
    {
        EnsureEditable();
        ArgumentNullException.ThrowIfNull(data);
        var desiredFeatures = data.MorphFeatures.ToDictionary(
            value => value.Name,
            value => value.Offset,
            StringComparer.OrdinalIgnoreCase);
        if (desiredFeatures.Count != data.MorphFeatures.Count ||
            desiredFeatures.Any(value => string.IsNullOrWhiteSpace(value.Key) || !float.IsFinite(value.Value)))
        {
            throw new InvalidDataException("Transferred morph features must have unique names and finite values.");
        }
        if (data.AdditiveBoneOffsets.Any(value =>
                string.IsNullOrWhiteSpace(value.BoneName) || !IsFinite(value.Translation)) ||
            data.AdditiveBoneOffsets.Select(value => value.BoneName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.AdditiveBoneOffsets.Count)
        {
            throw new InvalidDataException("Transferred bone residuals must have unique names and finite values.");
        }

        foreach (var name in _featureOrder)
        {
            _features[name] = desiredFeatures.GetValueOrDefault(name);
        }
        var destinationBones = _baseHead.Topology.ReferenceSkeleton
            .Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _boneOverrides.Clear();
        foreach (var value in data.AdditiveBoneOffsets.Where(value => destinationBones.Contains(value.BoneName)))
        {
            _boneOverrides[value.BoneName] = value.Translation;
        }
        _pastedBoneNames = null;
        Refresh();
    }

    public void Undo() => Replay(_history.PopUndo(), useAfter: false);
    public void Redo() => Replay(_history.PopRedo(), useAfter: true);
    public void ClearRedo() => _history.ClearRedo();

    private MorphTargetResolution Resolve() => new MorphFeatureTargetResolver().Resolve(
        Features, _targets, _metadataOnlyFeatures, _profileName, _aliases);

    private MorphFaceEvaluation Evaluate(bool includeOracle)
    {
        var resolution = Resolve();
        var geometry = SparseMorphEvaluator.Evaluate(_baseHead, resolution.WeightedTargets);
        ApplyCorrection(geometry.Positions, 0);
        var lodGeometry = new Dictionary<int, DeformationResult> { [0] = geometry };
        foreach (var lod in _baseHead.AvailableLods.Where(value => value.LodIndex > 0))
        {
            var evaluated = SparseMorphEvaluator.Evaluate(_baseHead, resolution.WeightedTargets, lod.LodIndex);
            ApplyCorrection(evaluated.Positions, lod.LodIndex);
            lodGeometry[lod.LodIndex] = evaluated;
        }
        var finalSkeleton = MorphBoneOffsetComposer.Compose(
            _baseHead.Topology.ReferenceSkeleton,
            _templateBones,
            resolution.WeightedTargets,
            _boneOverrides);
        var pose = SkeletalPoseComposer.Compose(_baseHead.Topology.ReferenceSkeleton, finalSkeleton);
        var report = includeOracle && _document.BakedLods.Count > 0
            ? DeformationComparison.Compare(geometry, _document.BakedLods[0], OracleTolerance, 10)
            : null;
        return new MorphFaceEvaluation(geometry, lodGeometry, finalSkeleton, pose, resolution, report);
    }

    private IReadOnlyList<Vector3[]> DetectPositionCorrections(MorphTargetResolution resolution)
    {
        if (_document.BakedLods.Count == 0)
        {
            return [];
        }
        var raw = SparseMorphEvaluator.Evaluate(_baseHead, resolution.WeightedTargets);
        var report = DeformationComparison.Compare(raw, _document.BakedLods[0], OracleTolerance, 10);
        var correctLod0 = !report.IsWithinTolerance &&
                          _recognizesBaseVariant is not null &&
                          _recognizesBaseVariant(report);
        if (!report.IsWithinTolerance && !correctLod0)
        {
            return [];
        }

        var corrections = new List<Vector3[]>();
        var lodCount = Math.Min(_document.BakedLods.Count, _baseHead.AvailableLodPositions.Count);
        for (var lodIndex = 0; lodIndex < lodCount; lodIndex++)
        {
            var basePositions = _baseHead.AvailableLodPositions[lodIndex];
            var baked = _document.BakedLods[lodIndex];
            var canEvaluate = basePositions.Length == baked.Length &&
                              resolution.WeightedTargets.All(weighted =>
                                  weighted.Target.Lods.FirstOrDefault(lod => lod.LodIndex == lodIndex) is not
                                      { } targetLod ||
                                  targetLod.BaseMeshVertexCount == basePositions.Length);
            if (!canEvaluate)
            {
                break;
            }
            var evaluated = lodIndex == 0
                ? raw.Positions
                : SparseMorphEvaluator.EvaluatePositions(
                    basePositions,
                    resolution.WeightedTargets,
                    lodIndex);
            corrections.Add(lodIndex == 0 && !correctLod0
                ? new Vector3[baked.Length]
                : evaluated.Zip(baked, (source, target) => target - source).ToArray());
        }
        return corrections;
    }

    private IReadOnlyList<string> ValidateEditableTargets(MorphTargetResolution resolution)
    {
        var errors = new List<string>();
        var targets = resolution.Features
            .Where(feature => feature.Target is not null)
            .Select(feature => feature.Target!)
            .DistinctBy(target => $"{target.Source.PackagePath}|{target.Source.InstancedPath}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var commonLodCount = Math.Min(_document.BakedLods.Count, _baseHead.AvailableLodPositions.Count);

        for (var lodIndex = 0; lodIndex < commonLodCount; lodIndex++)
        {
            var basePositions = _baseHead.AvailableLodPositions[lodIndex];
            var bakedPositions = _document.BakedLods[lodIndex];
            var lodTargets = targets
                .Where(target => target.Lods.Any(lod => lod.LodIndex == lodIndex))
                .ToArray();
            // UE3 targets are allowed to stop before the mesh's final LODs.
            // Absence means zero contribution at that LOD; if the whole set
            // stops, that stored baked LOD is deliberately preserved.
            if (basePositions.Length == 0 || lodTargets.Length == 0)
            {
                continue;
            }
            if (basePositions.Length != bakedPositions.Length)
            {
                errors.Add(
                    $"Stored LOD {lodIndex} has {bakedPositions.Length} vertices; the profile base has {basePositions.Length}.");
                continue;
            }

            foreach (var target in lodTargets)
            {
                var lod = target.Lods.First(value => value.LodIndex == lodIndex);
                if (lod.BaseMeshVertexCount != basePositions.Length)
                {
                    errors.Add(
                        $"Target '{target.Source.InstancedPath}' LOD {lodIndex} expects {lod.BaseMeshVertexCount} vertices; the profile base has {basePositions.Length}.");
                    continue;
                }
                var invalid = lod.Vertices.FirstOrDefault(vertex =>
                    vertex.SourceIndex < 0 || vertex.SourceIndex >= basePositions.Length ||
                    !IsFinite(vertex.PositionDelta) || !IsFinite(vertex.NormalDelta));
                if (invalid.SourceIndex < 0 || invalid.SourceIndex >= basePositions.Length ||
                    !IsFinite(invalid.PositionDelta) || !IsFinite(invalid.NormalDelta))
                {
                    errors.Add(
                        $"Target '{target.Source.InstancedPath}' LOD {lodIndex} contains an invalid sparse vertex delta.");
                }
            }
        }

        foreach (var target in targets)
        foreach (var boneOffset in target.BoneOffsets)
        {
            if (string.IsNullOrWhiteSpace(boneOffset.BoneName) ||
                !IsFinite(boneOffset.Offset))
            {
                errors.Add(
                    $"Target '{target.Source.InstancedPath}' contains an invalid offset for bone '{boneOffset.BoneName}'.");
            }
        }

        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private void ApplyCorrection(Vector3[] positions, int lodIndex)
    {
        if (lodIndex >= _positionCorrections.Count)
        {
            return;
        }
        var correction = _positionCorrections[lodIndex];
        for (var index = 0; index < positions.Length; index++)
        {
            positions[index] += correction[index];
        }
    }

    private void Refresh()
    {
        Evaluation = Evaluate(includeOracle: false);
        EvaluationChanged?.Invoke(this, EventArgs.Empty);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EndEdit(string key, float value)
    {
        if (_history.End(key, value))
        {
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BeginEdit(string key, float value)
    {
        if (_history.Begin(key, value))
        {
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Replay(SemanticEdit edit, bool useAfter)
    {
        _replayingHistory = true;
        try
        {
            if (edit is SemanticFeatureBatchEdit batch)
            {
                SetFeatures(useAfter ? batch.After : batch.Before);
            }
            else if (edit is SemanticMorphStateEdit stateEdit)
            {
                RestoreAuthoringState(useAfter ? stateEdit.After : stateEdit.Before);
                Refresh();
            }
            else if (edit is SemanticValueEdit valueEdit)
            {
                var value = useAfter ? valueEdit.After : valueEdit.Before;
                if (valueEdit.Key.StartsWith("feature:", StringComparison.Ordinal))
                {
                    SetFeature(valueEdit.Key[8..], value);
                }
                else
                {
                    var parts = valueEdit.Key.Split(':');
                    SetBoneAxis(parts[1],
                        int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), value);
                }
            }
        }
        finally
        {
            _replayingHistory = false;
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EnsureEditable()
    {
        if (!CanEdit)
        {
            throw new InvalidOperationException(
                "Live editing is disabled because the original face did not pass the deformation oracle.");
        }
    }

    private MorphFaceAuthoringState CaptureAuthoringState() => new(
        new Dictionary<string, float>(_features, StringComparer.OrdinalIgnoreCase),
        _templateBones.ToArray(),
        new Dictionary<string, Vector3>(_boneOverrides, StringComparer.OrdinalIgnoreCase),
        _pastedBoneNames is null
            ? null
            : new HashSet<string>(_pastedBoneNames, StringComparer.OrdinalIgnoreCase));

    private void RestoreAuthoringState(MorphFaceAuthoringState state)
    {
        _features.Clear();
        foreach (var value in state.Features) _features[value.Key] = value.Value;
        _templateBones = state.TemplateBones.ToArray();
        _boneOverrides.Clear();
        foreach (var value in state.BoneOverrides) _boneOverrides[value.Key] = value.Value;
        _pastedBoneNames = state.PastedBoneNames is null
            ? null
            : new HashSet<string>(state.PastedBoneNames, StringComparer.OrdinalIgnoreCase);
    }

    private static bool AuthoringStatesEqual(MorphFaceAuthoringState left, MorphFaceAuthoringState right) =>
        left.Features.Count == right.Features.Count &&
        left.Features.All(value => right.Features.TryGetValue(value.Key, out var other) && other == value.Value) &&
        left.TemplateBones.SequenceEqual(right.TemplateBones) &&
        left.BoneOverrides.Count == right.BoneOverrides.Count &&
        left.BoneOverrides.All(value =>
            right.BoneOverrides.TryGetValue(value.Key, out var other) && other == value.Value) &&
        (left.PastedBoneNames is null
            ? right.PastedBoneNames is null
            : right.PastedBoneNames is not null && left.PastedBoneNames.SetEquals(right.PastedBoneNames));

    private static string FeatureKey(string name) => $"feature:{name}";
    private static string BoneKey(string name, int axis) => $"bone:{name}:{axis}";

    private static string GetObjectName(string instancedPath)
    {
        var separator = instancedPath.LastIndexOf('.');
        return separator < 0 ? instancedPath : instancedPath[(separator + 1)..];
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

internal sealed record MorphFaceAuthoringState(
    IReadOnlyDictionary<string, float> Features,
    IReadOnlyList<BoneTranslation> TemplateBones,
    IReadOnlyDictionary<string, Vector3> BoneOverrides,
    IReadOnlySet<string>? PastedBoneNames);
