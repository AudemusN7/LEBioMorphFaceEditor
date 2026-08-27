using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

public sealed record MorphMeshFitPrior(
    string BaseHeadPath,
    IReadOnlyList<MorphFeatureValue> MorphFeatures,
    IReadOnlyList<BoneTranslation> FinalSkeleton,
    IReadOnlyList<Vector3[]> BakedLods);

public sealed record MorphMeshPositionCandidate(
    string CoordinateSystem,
    IReadOnlyList<Vector3> Positions,
    MorphMeshFitPrior? Prior = null);

public sealed record MorphMeshFitResult(
    MorphFaceMorphData MorphData,
    string CoordinateSystem,
    float RootMeanSquareError,
    float MaximumError,
    int RecoveredFeatureCount,
    bool UsedSourcePrior);

/// <summary>
/// Projects a baked LOD0 mesh back into the linear span of a resolved morph-target set.
/// A matching export sidecar preserves the source slider branch, final skeleton, and baked
/// lower LODs; arbitrary mesh files without provenance recover geometry-authoring controls only.
/// </summary>
public static class MorphMeshInverter
{
    public static MorphMeshFitResult Fit(
        SkeletalMeshAsset baseHead,
        IReadOnlyList<ResolvedMorphFeature> resolvedFeatures,
        IReadOnlyList<MorphMeshPositionCandidate> candidates,
        IReadOnlyList<Vector3[]> positionCorrections,
        IReadOnlyList<Vector3[]> templateBakedLods)
    {
        ArgumentNullException.ThrowIfNull(baseHead);
        ArgumentNullException.ThrowIfNull(resolvedFeatures);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(templateBakedLods);
        if (candidates.Count == 0)
        {
            throw new InvalidDataException("The imported mesh did not contain any vertex positions.");
        }

        FitCandidate? exactPrior = null;
        foreach (var candidate in candidates.Where(value =>
                     value.Prior is { BakedLods.Count: > 0 } prior &&
                     prior.BaseHeadPath.Equals(
                         baseHead.Source.InstancedPath,
                         StringComparison.OrdinalIgnoreCase) &&
                     prior.BakedLods.Count == templateBakedLods.Count &&
                     prior.BakedLods.Select(lod => lod.Length)
                         .SequenceEqual(templateBakedLods.Select(lod => lod.Length)) &&
                     value.Positions.Count == prior.BakedLods[0].Length))
        {
            var measured = MeasureDistance(candidate.Positions, candidate.Prior!.BakedLods[0]);
            if (measured.Maximum > 0.0001f)
            {
                continue;
            }
            var fitted = new FitCandidate(
                candidate.CoordinateSystem,
                [],
                (float)Math.Sqrt(measured.SumSquared / candidate.Positions.Count),
                measured.Maximum,
                candidate.Prior);
            if (exactPrior is null || fitted.RootMeanSquareError < exactPrior.RootMeanSquareError)
            {
                exactPrior = fitted;
            }
        }
        if (exactPrior is { Prior: { } source })
        {
            var sourceData = new MorphFaceMorphData(
                source.MorphFeatures.ToArray(),
                source.FinalSkeleton.ToArray(),
                source.BakedLods.Select(lod => lod.ToArray()).ToArray());
            return new MorphMeshFitResult(
                sourceData,
                exactPrior.CoordinateSystem,
                exactPrior.RootMeanSquareError,
                exactPrior.MaximumError,
                source.MorphFeatures.Count(value => Math.Abs(value.Offset) >= 0.000001f),
                true);
        }

        var columns = resolvedFeatures
            .Where(feature => feature.Target?.Lods.FirstOrDefault(lod => lod.LodIndex == 0) is { Vertices.Count: > 0 })
            .GroupBy(feature => feature.Target!.Source.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (columns.Length == 0)
        {
            throw new InvalidOperationException("The selected face profile has no invertible LOD0 morph targets.");
        }

        var basePositions = baseHead.FindLod(0)?.Positions ?? baseHead.Positions;
        var correctedBase = basePositions.ToArray();
        if (positionCorrections.Count > 0)
        {
            var correction = positionCorrections[0];
            if (correction.Length != correctedBase.Length)
            {
                throw new InvalidDataException("The selected base-variant correction has incompatible LOD0 topology.");
            }
            for (var vertex = 0; vertex < correctedBase.Length; vertex++)
            {
                correctedBase[vertex] += correction[vertex];
            }
        }

        var deltas = new Vector3[columns.Length][];
        for (var column = 0; column < columns.Length; column++)
        {
            deltas[column] = new Vector3[correctedBase.Length];
            var lod = columns[column].Target!.Lods.Single(value => value.LodIndex == 0);
            if (lod.BaseMeshVertexCount != correctedBase.Length)
            {
                throw new InvalidDataException(
                    $"Target '{columns[column].Target!.Source.InstancedPath}' expects {lod.BaseMeshVertexCount} LOD0 vertices; the selected base has {correctedBase.Length}.");
            }
            foreach (var delta in lod.Vertices)
            {
                if ((uint)delta.SourceIndex >= (uint)correctedBase.Length)
                {
                    throw new InvalidDataException(
                        $"Target '{columns[column].Target!.Source.InstancedPath}' references invalid vertex {delta.SourceIndex}.");
                }
                deltas[column][delta.SourceIndex] = delta.PositionDelta;
            }
        }

        FitCandidate? best = null;
        foreach (var candidate in candidates.Where(value => value.Positions.Count == correctedBase.Length))
        {
            var prior = candidate.Prior is { } candidatePrior &&
                        candidatePrior.BaseHeadPath.Equals(
                            baseHead.Source.InstancedPath,
                            StringComparison.OrdinalIgnoreCase)
                ? candidatePrior
                : null;
            var priorByName = prior?.MorphFeatures.ToDictionary(
                value => value.Name,
                value => value.Offset,
                StringComparer.OrdinalIgnoreCase);
            var priorWeights = priorByName is null
                ? null
                : columns.Select(feature => (double)priorByName.GetValueOrDefault(feature.Feature.Name)).ToArray();
            var fitted = SolveCandidate(candidate, correctedBase, deltas, priorWeights, prior);
            if (best is null || fitted.RootMeanSquareError < best.RootMeanSquareError)
            {
                best = fitted;
            }
        }
        if (best is null)
        {
            var counts = string.Join(", ", candidates.Select(value =>
                $"{value.CoordinateSystem}: {value.Positions.Count}"));
            throw new InvalidDataException(
                $"The imported mesh vertex count does not match the selected profile ({correctedBase.Length}). Found {counts}.");
        }

        var weightedTargets = columns
            .Select((feature, index) => new WeightedMorphTarget(
                feature.Feature.Name,
                feature.Target!,
                (float)best.Weights[index]))
            .Where(weighted => Math.Abs(weighted.Weight) >= 0.000001f)
            .ToArray();
        var morphFeaturesByName = best.Prior?.MorphFeatures.ToDictionary(
                                      value => value.Name,
                                      value => value.Offset,
                                      StringComparer.OrdinalIgnoreCase)
                                  ?? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < columns.Length; index++)
        {
            morphFeaturesByName[columns[index].Feature.Name] = (float)best.Weights[index];
        }
        var morphFeatures = morphFeaturesByName
            .Where(value => Math.Abs(value.Value) >= 0.000001f)
            .Select(value => new MorphFeatureValue(value.Key, value.Value))
            .ToArray();
        var finalSkeleton = ComposeFinalSkeleton(
            baseHead,
            columns,
            weightedTargets,
            best.Prior);

        var baseLods = baseHead.AvailableLodPositions;
        var bakedLods = templateBakedLods
            .Select((template, lodIndex) =>
            {
                if (lodIndex >= baseLods.Count || baseLods[lodIndex].Length != template.Length)
                {
                    // Some faces retain a lower stored LOD which the canonical
                    // target set cannot rebuild. It remains template-owned.
                    return template.ToArray();
                }
                var positions = baseLods[lodIndex];
                var compatible = weightedTargets
                    .Where(weighted => weighted.Target.Lods.Any(lod => lod.LodIndex == lodIndex))
                    .ToArray();
                var baked = compatible.Length == 0
                    ? positions.ToArray()
                    : SparseMorphEvaluator.EvaluatePositions(positions, compatible, lodIndex);
                if (lodIndex < positionCorrections.Count)
                {
                    var correction = positionCorrections[lodIndex];
                    if (correction.Length == baked.Length)
                    {
                        for (var vertex = 0; vertex < baked.Length; vertex++)
                        {
                            baked[vertex] += correction[vertex];
                        }
                    }
                }
                return baked;
            })
            .ToArray();

        return new MorphMeshFitResult(
            new MorphFaceMorphData(morphFeatures, finalSkeleton, bakedLods),
            best.CoordinateSystem,
            best.RootMeanSquareError,
            best.MaximumError,
            morphFeatures.Length,
            best.Prior is not null);
    }

    private static IReadOnlyList<BoneTranslation> ComposeFinalSkeleton(
        SkeletalMeshAsset baseHead,
        IReadOnlyList<ResolvedMorphFeature> columns,
        IReadOnlyList<WeightedMorphTarget> weightedTargets,
        MorphMeshFitPrior? prior)
    {
        if (prior is null)
        {
            return MorphBoneOffsetComposer.Compose(
                baseHead.Topology.ReferenceSkeleton,
                [],
                weightedTargets);
        }

        var priorByName = prior.MorphFeatures.ToDictionary(
            value => value.Name,
            value => value.Offset,
            StringComparer.OrdinalIgnoreCase);
        var priorTargets = columns
            .Select(feature => new WeightedMorphTarget(
                feature.Feature.Name,
                feature.Target!,
                priorByName.GetValueOrDefault(feature.Feature.Name)))
            .Where(weighted => Math.Abs(weighted.Weight) >= 0.000001f)
            .ToArray();
        var priorComposed = MorphBoneOffsetComposer.Compose(
            baseHead.Topology.ReferenceSkeleton,
            prior.FinalSkeleton,
            priorTargets);
        var priorComposedByName = priorComposed.ToDictionary(
            value => value.BoneName,
            StringComparer.OrdinalIgnoreCase);
        var manualOffsets = prior.FinalSkeleton.ToDictionary(
            value => value.BoneName,
            value => value.Translation - priorComposedByName[value.BoneName].Translation,
            StringComparer.OrdinalIgnoreCase);
        return MorphBoneOffsetComposer.Compose(
            baseHead.Topology.ReferenceSkeleton,
            prior.FinalSkeleton,
            weightedTargets,
            manualOffsets);
    }

    private static FitCandidate SolveCandidate(
        MorphMeshPositionCandidate candidate,
        IReadOnlyList<Vector3> basePositions,
        IReadOnlyList<Vector3[]> deltas,
        IReadOnlyList<double>? priorWeights,
        MorphMeshFitPrior? prior)
    {
        var count = deltas.Count;
        var normal = new double[count, count];
        var right = new double[count];
        for (var vertex = 0; vertex < basePositions.Count; vertex++)
        {
            var displacement = candidate.Positions[vertex] - basePositions[vertex];
            for (var left = 0; left < count; left++)
            {
                var leftDelta = deltas[left][vertex];
                right[left] += Vector3.Dot(leftDelta, displacement);
                for (var column = left; column < count; column++)
                {
                    normal[left, column] += Vector3.Dot(leftDelta, deltas[column][vertex]);
                }
            }
        }
        for (var row = 0; row < count; row++)
        {
            for (var column = 0; column < row; column++)
            {
                normal[row, column] = normal[column, row];
            }
        }

        if (priorWeights is not null)
        {
            var priorError = MeasureError(candidate.Positions, basePositions, deltas, priorWeights);
            if (priorError.Maximum <= 0.0001f)
            {
                return new FitCandidate(
                    candidate.CoordinateSystem,
                    priorWeights.ToArray(),
                    (float)Math.Sqrt(priorError.SumSquared / basePositions.Count),
                    priorError.Maximum,
                    prior);
            }
        }

        // A tiny ridge stabilises correlated opposite/character targets without materially
        // changing a representable face. Pivoted elimination then handles the remaining rank.
        var largestDiagonal = Enumerable.Range(0, count).Max(index => normal[index, index]);
        // Without provenance, use only enough ridge to make a rank-deficient
        // target basis solvable. A matching export sidecar is stronger evidence:
        // keep its known slider branch stable across package/UModel float noise,
        // while mesh edits of meaningful size still dominate the fit.
        var ridgeScale = priorWeights is null ? 1e-10 : 1e-6;
        var ridge = Math.Max(largestDiagonal * ridgeScale, priorWeights is null ? 1e-12 : 1e-8);
        for (var index = 0; index < count; index++)
        {
            normal[index, index] += ridge;
            if (priorWeights is not null)
            {
                right[index] += ridge * priorWeights[index];
            }
        }
        var weights = Solve(normal, right);
        var measured = MeasureError(candidate.Positions, basePositions, deltas, weights);
        return new FitCandidate(
            candidate.CoordinateSystem,
            weights,
            (float)Math.Sqrt(measured.SumSquared / basePositions.Count),
            measured.Maximum,
            prior);
    }

    private static (double SumSquared, float Maximum) MeasureError(
        IReadOnlyList<Vector3> targetPositions,
        IReadOnlyList<Vector3> basePositions,
        IReadOnlyList<Vector3[]> deltas,
        IReadOnlyList<double> weights)
    {
        double sumSquared = 0;
        var maximum = 0f;
        for (var vertex = 0; vertex < basePositions.Count; vertex++)
        {
            var reconstructed = basePositions[vertex];
            for (var column = 0; column < deltas.Count; column++)
            {
                reconstructed += deltas[column][vertex] * (float)weights[column];
            }
            var error = Vector3.Distance(reconstructed, targetPositions[vertex]);
            sumSquared += error * error;
            maximum = Math.Max(maximum, error);
        }
        return (sumSquared, maximum);
    }

    private static (double SumSquared, float Maximum) MeasureDistance(
        IReadOnlyList<Vector3> left,
        IReadOnlyList<Vector3> right)
    {
        double sumSquared = 0;
        var maximum = 0f;
        for (var index = 0; index < left.Count; index++)
        {
            var error = Vector3.Distance(left[index], right[index]);
            sumSquared += error * error;
            maximum = Math.Max(maximum, error);
        }
        return (sumSquared, maximum);
    }

    private static double[] Solve(double[,] matrix, double[] vector)
    {
        var size = vector.Length;
        for (var pivot = 0; pivot < size; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < size; row++)
            {
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot]))
                {
                    best = row;
                }
            }
            if (best != pivot)
            {
                for (var column = pivot; column < size; column++)
                {
                    (matrix[pivot, column], matrix[best, column]) =
                        (matrix[best, column], matrix[pivot, column]);
                }
                (vector[pivot], vector[best]) = (vector[best], vector[pivot]);
            }

            var diagonal = matrix[pivot, pivot];
            if (Math.Abs(diagonal) < 1e-20)
            {
                continue;
            }
            for (var row = pivot + 1; row < size; row++)
            {
                var factor = matrix[row, pivot] / diagonal;
                if (factor == 0)
                {
                    continue;
                }
                for (var column = pivot; column < size; column++)
                {
                    matrix[row, column] -= factor * matrix[pivot, column];
                }
                vector[row] -= factor * vector[pivot];
            }
        }

        var result = new double[size];
        for (var row = size - 1; row >= 0; row--)
        {
            var value = vector[row];
            for (var column = row + 1; column < size; column++)
            {
                value -= matrix[row, column] * result[column];
            }
            result[row] = Math.Abs(matrix[row, row]) < 1e-20 ? 0 : value / matrix[row, row];
        }
        return result;
    }

    private sealed record FitCandidate(
        string CoordinateSystem,
        double[] Weights,
        float RootMeanSquareError,
        float MaximumError,
        MorphMeshFitPrior? Prior);
}
