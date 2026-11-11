using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;

namespace MorphFaceEditor.Core.Deformation;

public sealed record PlayerMorphConversionBaseline(string Id, string ProfileKey, MorphFaceMorphData MorphData);

/// <summary>A trusted, profile-specific baked reference expressed in native target coordinates.</summary>
public sealed class PlayerMorphBaselineFrame
{
    private readonly MorphFaceSemanticTransferData semantic;
    public PlayerMorphConversionBaseline Baseline { get; }
    public SkeletalMeshAsset EvaluationBase { get; }
    public MorphFaceDocument Document { get; }

    private PlayerMorphBaselineFrame(PlayerMorphConversionBaseline baseline, SkeletalMeshAsset evaluationBase,
        MorphFaceDocument document, MorphFaceSemanticTransferData semantic)
    {
        Baseline = baseline;
        EvaluationBase = evaluationBase;
        Document = document;
        this.semantic = semantic;
    }

    public static PlayerMorphBaselineFrame? TryCreate(PlayerMorphConversionBaseline baseline, MorphFaceDocument template,
        SkeletalMeshAsset canonicalBase, MorphFaceProfile profile, IReadOnlyList<MorphTargetAsset> targets)
    {
        try { return Create(baseline, template, canonicalBase, profile, targets); }
        catch (InvalidDataException) { return null; } // A local RON can belong to a different mesh or rig.
    }

    public static PlayerMorphBaselineFrame Create(PlayerMorphConversionBaseline baseline, MorphFaceDocument template,
        SkeletalMeshAsset canonicalBase, MorphFaceProfile profile, IReadOnlyList<MorphTargetAsset> targets)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(canonicalBase);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(targets);
        if (string.IsNullOrWhiteSpace(baseline.Id) || !string.Equals(baseline.ProfileKey, profile.Key, StringComparison.OrdinalIgnoreCase) || baseline.MorphData is null)
            throw new InvalidDataException("Baseline identity or profile is invalid.");
        var data = baseline.MorphData;
        if (data.MorphFeatures.Any(f => string.IsNullOrWhiteSpace(f.Name) || !float.IsFinite(f.Offset)) ||
            data.MorphFeatures.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.MorphFeatures.Count)
            throw new InvalidDataException("Baseline features are invalid.");
        if (canonicalBase.Lods.Count == 0 || canonicalBase.Lods[0].LodIndex != 0 || data.BakedLods.Count == 0 ||
            data.BakedLods[0] is null || data.BakedLods[0].Length != canonicalBase.Lods[0].Positions.Length ||
            data.BakedLods[0].Length == 0 || data.BakedLods[0].Any(v => !Finite(v)))
            throw new InvalidDataException("Baseline LOD0 is invalid.");
        var bones = canonicalBase.Topology.ReferenceSkeleton;
        if (bones.Count == 0 || bones.Any(b => string.IsNullOrWhiteSpace(b.Name) || !Finite(b.Position)) ||
            bones.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bones.Count)
            throw new InvalidDataException("Baseline reference skeleton is invalid.");
        var boneNames = bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (data.FinalSkeleton.Any(b => !boneNames.Contains(b.BoneName) || !Finite(b.Translation)) ||
            data.FinalSkeleton.Select(b => b.BoneName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.FinalSkeleton.Count)
            throw new InvalidDataException("Baseline skeleton is invalid.");
        var authoredBones = data.FinalSkeleton.ToDictionary(b => b.BoneName, StringComparer.OrdinalIgnoreCase);
        var completeBones = bones.Select(b => authoredBones.GetValueOrDefault(b.Name) ?? new BoneTranslation(b.Name, b.Position)).ToArray();
        var eligible = targets.Where(t => !profile.MetadataOnlyFeatures.Contains(Name(t))).ToArray();
        if (eligible.Any(t => string.IsNullOrWhiteSpace(Name(t))) || eligible.Select(Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != eligible.Length)
            throw new InvalidDataException("Baseline target names are invalid.");
        var names = eligible.Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var features = data.MorphFeatures.Where(f => names.Contains(f.Name)).ToArray();
        var weights = features.ToDictionary(f => f.Name, f => f.Offset, StringComparer.OrdinalIgnoreCase);
        var weighted = eligible.Where(t => weights.GetValueOrDefault(Name(t)) != 0)
            .Select(t => new WeightedMorphTarget(Name(t), t, weights[Name(t)])).ToArray();
        foreach (var lod in canonicalBase.Lods)
        {
            if (lod.Topology.LodIndex != lod.LodIndex || lod.Topology.VertexCount != lod.Positions.Length || lod.Positions.Any(v => !Finite(v)))
                throw new InvalidDataException("Baseline mesh LOD is invalid.");
            foreach (var target in weighted)
            {
                var matches = target.Target.Lods.Where(l => l.LodIndex == lod.LodIndex).ToArray();
                if (matches.Length > 1 || (lod.LodIndex == 0 && matches.Length != 1) || matches.Any(l =>
                    l.BaseMeshVertexCount != lod.Positions.Length || l.Vertices.Any(v => (uint)v.SourceIndex >= (uint)lod.Positions.Length || !Finite(v.PositionDelta))))
                    throw new InvalidDataException("Baseline target LOD is invalid.");
            }
        }
        if (weighted.Any(t => t.Target.BoneOffsets.Any(b => string.IsNullOrWhiteSpace(b.BoneName) || !Finite(b.Offset))))
            throw new InvalidDataException("Baseline target bones are invalid.");
        // Sum deltas at zero to avoid cancellation against large canonical coordinates.
        var deltas = SparseMorphEvaluator.EvaluatePositions(new Vector3[data.BakedLods[0].Length], weighted, 0);
        var zero = data.BakedLods[0].Select((p, i) => p - deltas[i]).ToArray();
        if (zero.Any(v => !Finite(v))) throw new InvalidDataException("Baseline geometry is invalid.");
        var evaluationBase = canonicalBase with { Lods = canonicalBase.Lods.Select((l, i) => i == 0 ? l with { Positions = zero } : l).ToArray() };
        var baked = evaluationBase.Lods.Select((l, i) => i == 0 ? data.BakedLods[0].ToArray() :
            SparseMorphEvaluator.EvaluatePositions(l.Positions, weighted, l.LodIndex)).ToArray();
        var document = template with { MorphFeatures = features, FinalSkeleton = completeBones, BakedLods = baked };
        var session = new MorphFaceEditingSession(document, evaluationBase, eligible, profile.MetadataOnlyFeatures, aliases: profile.FeatureAliases);
        return new PlayerMorphBaselineFrame(baseline, evaluationBase, document, session.CaptureSemanticTransferData());
    }

    public MorphFaceSemanticTransferData CaptureEdits(MorphFaceSemanticTransferData authored) => Combine(authored, semantic, -1);
    public MorphFaceSemanticTransferData ApplyEdits(MorphFaceSemanticTransferData edits) => Combine(semantic, edits, 1);

    private static MorphFaceSemanticTransferData Combine(MorphFaceSemanticTransferData left, MorphFaceSemanticTransferData right, int sign)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var features = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var bones = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        void Add(MorphFaceSemanticTransferData value, int multiplier)
        {
            if (value.MorphFeatures.Any(f => string.IsNullOrWhiteSpace(f.Name) || !float.IsFinite(f.Offset)) ||
                value.MorphFeatures.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.MorphFeatures.Count ||
                value.AdditiveBoneOffsets.Any(b => string.IsNullOrWhiteSpace(b.BoneName) || !Finite(b.Translation)) ||
                value.AdditiveBoneOffsets.Select(b => b.BoneName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.AdditiveBoneOffsets.Count)
                throw new InvalidDataException("Semantic transfer values are invalid.");
            foreach (var f in value.MorphFeatures) features[f.Name] = features.GetValueOrDefault(f.Name) + multiplier * f.Offset;
            foreach (var b in value.AdditiveBoneOffsets) bones[b.BoneName] = bones.GetValueOrDefault(b.BoneName) + multiplier * b.Translation;
        }
        Add(left, 1); Add(right, sign);
        if (features.Values.Any(v => !float.IsFinite(v)) || bones.Values.Any(v => !Finite(v)))
            throw new InvalidDataException("Semantic transfer values overflowed.");
        return new(features.Select(f => new MorphFeatureValue(f.Key, f.Value)).ToArray(),
            bones.Select(b => new BoneTranslation(b.Key, b.Value)).ToArray());
    }
    private static string Name(MorphTargetAsset target) => target.Source.InstancedPath.Split('.').Last();
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
