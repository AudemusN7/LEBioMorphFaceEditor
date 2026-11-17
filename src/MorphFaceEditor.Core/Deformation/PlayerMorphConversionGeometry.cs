using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

public enum PlayerMorphConversionGeometryReason
{
    None,
    MissingOrInconsistentLod,
    InvalidFeature,
    UnresolvedActiveFeature,
    InvalidTargetLod,
    InvalidSkeleton,
    NonFiniteGeometry,
    GeometryMismatch
}

public sealed record PlayerMorphConversionGeometryResult(
    bool IsSupported, int? LodIndex, PlayerMorphConversionGeometryReason Reason);

/// <summary>Proves that serialized features reproduce the authored model-space LOD0 without corrections.</summary>
public static class PlayerMorphConversionGeometry
{
    // Centimetres, per coordinate: allows float accumulation noise but not a fitted residual.
    private const float PositionTolerance = 0.0001f;

    public static PlayerMorphConversionGeometryResult Check(
        MorphFaceDocument document,
        SkeletalMeshAsset baseHead,
        IReadOnlyList<MorphTargetAsset> targets,
        IReadOnlySet<string> metadataOnlyFeatures,
        IReadOnlyDictionary<string, string> aliases)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(baseHead);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(metadataOnlyFeatures);
        ArgumentNullException.ThrowIfNull(aliases);

        static PlayerMorphConversionGeometryResult Fail(PlayerMorphConversionGeometryReason reason, int? lod = null) =>
            new(false, lod, reason);

        if (baseHead.Lods.Count == 0 || document.BakedLods.Count == 0)
            return Fail(PlayerMorphConversionGeometryReason.MissingOrInconsistentLod, 0);
        if (document.MorphFeatures.Any(f => string.IsNullOrWhiteSpace(f.Name) || !float.IsFinite(f.Offset)) ||
            document.MorphFeatures.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            return Fail(PlayerMorphConversionGeometryReason.InvalidFeature);

        var resolution = new MorphFeatureTargetResolver().Resolve(document.MorphFeatures, targets, metadataOnlyFeatures, aliases: aliases);
        if (resolution.Features.Any(f => !f.IsResolved && f.Feature.Offset != 0))
            return Fail(PlayerMorphConversionGeometryReason.UnresolvedActiveFeature);
        var active = resolution.WeightedTargets;

        // Player payloads commonly contain only LOD0. Lower LODs are regenerated
        // for the destination, so neither their presence nor their contents are proof inputs.
        const int index = 0;
        var lod = baseHead.Lods[0];
        var baked = document.BakedLods[0];
        if (lod.LodIndex != index || lod.Topology.LodIndex != index ||
            lod.Positions.Length == 0 || lod.Topology.VertexCount != lod.Positions.Length ||
            baked is null || baked.Length != lod.Positions.Length)
            return Fail(PlayerMorphConversionGeometryReason.MissingOrInconsistentLod, index);
        if (lod.Positions.Any(v => !IsFinite(v)) || baked.Any(v => !IsFinite(v)))
            return Fail(PlayerMorphConversionGeometryReason.NonFiniteGeometry, index);

        foreach (var weighted in active)
        {
            var matches = weighted.Target.Lods.Where(value => value.LodIndex == index).ToArray();
            if (matches.Length != 1 || matches[0].BaseMeshVertexCount != lod.Positions.Length ||
                matches[0].Vertices.Any(v => (uint)v.SourceIndex >= (uint)lod.Positions.Length || !IsFinite(v.PositionDelta)))
                return Fail(PlayerMorphConversionGeometryReason.InvalidTargetLod, index);
        }

        var evaluated = SparseMorphEvaluator.EvaluatePositions(lod.Positions, active, index);
        for (var vertex = 0; vertex < baked.Length; vertex++)
        {
            var actual = evaluated[vertex];
            if (!IsFinite(actual)) return Fail(PlayerMorphConversionGeometryReason.NonFiniteGeometry, index);
            var difference = Vector3.Abs(actual - baked[vertex]);
            if (difference.X > PositionTolerance || difference.Y > PositionTolerance || difference.Z > PositionTolerance)
                return Fail(PlayerMorphConversionGeometryReason.GeometryMismatch, index);
        }
        return new(true, null, PlayerMorphConversionGeometryReason.None);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
