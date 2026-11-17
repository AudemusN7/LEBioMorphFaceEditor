using System.Numerics;
using System.Runtime.CompilerServices;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

public sealed record PlayerMorphSourceReconciliation(
    MorphFaceDocument? Document,
    PlayerMorphConversionGeometryReason Reason,
    bool RecoveredWeights,
    int BasisRank)
{
    public bool IsSupported => Document is not null;
    // A well-formed source whose bake has detail beyond its native sliders can still
    // supply its recorded slider values and final pose to an explicit shape transfer.
    public MorphFaceDocument? BakedShapePrior { get; init; }
}

/// <summary>
/// Interprets a Player bake in native target coordinates without treating CC selection
/// names as additional targets. The authored feature prior retains every component
/// which the vertices cannot determine; final bone positions remain authoritative.
/// </summary>
public sealed class PlayerMorphSourceReconciler
{
    private readonly ConditionalWeakTable<MorphFaceProfile, CachedBasis> bases = new();

    public PlayerMorphSourceReconciliation Reconcile(MorphFaceDocument document, SkeletalMeshAsset canonicalBase,
        MorphFaceProfile profile, IReadOnlyList<MorphTargetAsset> targets)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(canonicalBase);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(targets);
        PlayerMorphSourceReconciliation Fail(PlayerMorphConversionGeometryReason reason) => new(null, reason, false, 0);
        if (document.MorphFeatures.Any(value => string.IsNullOrWhiteSpace(value.Name) || !float.IsFinite(value.Offset)) ||
            document.MorphFeatures.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.MorphFeatures.Count)
            return Fail(PlayerMorphConversionGeometryReason.InvalidFeature);
        if (canonicalBase.FindLod(0) is not { } lod || document.BakedLods.Count == 0 ||
            document.BakedLods[0].Length != lod.Positions.Length)
            return Fail(PlayerMorphConversionGeometryReason.MissingOrInconsistentLod);
        if (document.BakedLods[0].Any(value => !IsFinite(value)) || lod.Positions.Any(value => !IsFinite(value)))
            return Fail(PlayerMorphConversionGeometryReason.NonFiniteGeometry);
        var referenceBones = canonicalBase.Topology.ReferenceSkeleton;
        var referenceNames = referenceBones.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (document.FinalSkeleton.Any(value => !referenceNames.Contains(value.BoneName) || !IsFinite(value.Translation)) ||
            document.FinalSkeleton.Select(value => value.BoneName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.FinalSkeleton.Count)
            return Fail(PlayerMorphConversionGeometryReason.InvalidSkeleton);
        var authoredBones = document.FinalSkeleton.ToDictionary(value => value.BoneName, StringComparer.OrdinalIgnoreCase);
        var completeBones = referenceBones.Select(bone => authoredBones.GetValueOrDefault(bone.Name) ??
            new BoneTranslation(bone.Name, bone.Position)).ToArray();

        // Morph-set object names are authoritative. CC node/selection names may coexist
        // with their expanded runtime recipe; guessing aliases double-counts that recipe.
        var eligible = targets.Where(target => !profile.MetadataOnlyFeatures.Contains(Name(target)))
            .ToArray();
        if (eligible.Select(Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != eligible.Length)
            return Fail(PlayerMorphConversionGeometryReason.InvalidTargetLod);
        var indices = eligible.Select((target, index) => (Name: Name(target), Index: index))
            .ToDictionary(value => value.Name, value => value.Index, StringComparer.OrdinalIgnoreCase);
        var weights = new double[eligible.Length];
        foreach (var feature in document.MorphFeatures)
            if (indices.TryGetValue(feature.Name, out var index)) weights[index] = feature.Offset;
        var order = document.MorphFeatures.Where(value => indices.ContainsKey(value.Name)).Select(value => value.Name)
            .Concat(eligible.Select(Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        MorphFaceDocument Capture() => document with
        {
            // An omitted parent-local translation means bind pose. Make that implicit
            // source pose explicit before calculating morph/manual bone contributions.
            FinalSkeleton = completeBones,
            MorphFeatures = order.Select(name => new MorphFeatureValue(name, (float)weights[indices[name]]))
                .Where(value => value.Offset != 0 || document.MorphFeatures.Any(original =>
                    original.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase))).ToArray()
        };
        var normalized = Capture();
        var recorded = normalized;
        var proof = PlayerMorphConversionGeometry.Check(normalized, canonicalBase, eligible,
            profile.MetadataOnlyFeatures, profile.FeatureAliases);
        if (proof.IsSupported) return new(normalized, proof.Reason, false, 0);
        if (proof.Reason != PlayerMorphConversionGeometryReason.GeometryMismatch)
            return Fail(proof.Reason);

        var cached = bases.GetValue(profile, _ => new(eligible, lod.Positions.Length));
        if (cached.VertexCount != lod.Positions.Length)
            return Fail(PlayerMorphConversionGeometryReason.InvalidTargetLod);
        // A correction lies only in observable target space. In particular, bone-only
        // targets and shape-equivalent authored slider branches are never invented.
        for (var refinement = 0; refinement < 3; refinement++)
        {
            var resolution = new MorphFeatureTargetResolver().Resolve(normalized.MorphFeatures, eligible,
                profile.MetadataOnlyFeatures, aliases: profile.FeatureAliases);
            var evaluated = SparseMorphEvaluator.EvaluatePositions(lod.Positions, resolution.WeightedTargets, 0);
            var residual = new Vector3[evaluated.Length];
            for (var vertex = 0; vertex < residual.Length; vertex++)
                residual[vertex] = document.BakedLods[0][vertex] - evaluated[vertex];
            var correction = cached.Basis.SolveCorrection(residual);
            for (var column = 0; column < weights.Length; column++)
            {
                weights[column] += correction[column];
                if (!double.IsFinite(weights[column]) || !float.IsFinite((float)weights[column]))
                    return Fail(PlayerMorphConversionGeometryReason.NonFiniteGeometry);
            }
            normalized = Capture();
            proof = PlayerMorphConversionGeometry.Check(normalized, canonicalBase, eligible,
                profile.MetadataOnlyFeatures, profile.FeatureAliases);
            if (proof.IsSupported) return new(normalized, proof.Reason, true, cached.Basis.Rank);
            if (proof.Reason != PlayerMorphConversionGeometryReason.GeometryMismatch) return Fail(proof.Reason);
        }
        var knownFeatures = document.MorphFeatures.All(f => f.Offset == 0 || indices.ContainsKey(f.Name) ||
            profile.MetadataOnlyFeatures.Contains(f.Name) || profile.FeatureAliases.ContainsKey(f.Name));
        return new(null, proof.Reason, false, cached.Basis.Rank) { BakedShapePrior = knownFeatures ? recorded : null };
    }

    private static string Name(MorphTargetAsset target) => target.Source.InstancedPath.Split('.').Last();
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private sealed class CachedBasis(IReadOnlyList<MorphTargetAsset> targets, int vertexCount)
    {
        public int VertexCount { get; } = vertexCount;
        public MorphTargetCoefficientBasis Basis { get; } = new(targets, vertexCount);
    }
}
