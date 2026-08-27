using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

public sealed record WeightedMorphTarget(
    string FeatureName,
    MorphTargetAsset Target,
    float Weight);

public sealed record DeformationResult(
    Vector3[] Positions,
    Vector3[] Normals,
    int AppliedDeltaCount,
    IReadOnlyList<string> Notes);

/// <summary>
/// Applies the verified LE1 additive sparse-target rule to positions and TangentZ normals.
/// </summary>
public static class SparseMorphEvaluator
{
    public static DeformationResult Evaluate(
        SkeletalMeshAsset baseMesh,
        IReadOnlyList<WeightedMorphTarget> weightedTargets,
        int lodIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(baseMesh);
        ArgumentNullException.ThrowIfNull(weightedTargets);
        ArgumentOutOfRangeException.ThrowIfNegative(lodIndex);
        var baseLod = lodIndex == 0 && baseMesh.FindLod(0) is null
            ? new { baseMesh.Positions, baseMesh.Normals }
            : baseMesh.FindLod(lodIndex) is { } renderLod
                ? new { renderLod.Positions, renderLod.Normals }
                : throw new InvalidDataException(
                    $"Base mesh '{baseMesh.Source.InstancedPath}' has no renderable LOD {lodIndex}.");
        var positions = (Vector3[])baseLod.Positions.Clone();
        var normals = (Vector3[])baseLod.Normals.Clone();
        var appliedDeltaCount = 0;
        var notes = new List<string>
        {
            "Direct feature weight multiplied by sparse position and TangentZ deltas."
        };

        foreach (var weightedTarget in weightedTargets)
        {
            if (!float.IsFinite(weightedTarget.Weight))
            {
                throw new InvalidDataException($"Weight for '{weightedTarget.FeatureName}' is not finite.");
            }

            var lod = weightedTarget.Target.Lods.FirstOrDefault(value => value.LodIndex == lodIndex);
            if (lod is null)
            {
                continue;
            }
            if (lod.BaseMeshVertexCount != positions.Length)
            {
                throw new InvalidDataException(
                    $"Target '{weightedTarget.Target.Source.InstancedPath}' expects {lod.BaseMeshVertexCount} vertices; base mesh has {positions.Length}.");
            }

            foreach (var vertex in lod.Vertices)
            {
                if ((uint)vertex.SourceIndex >= (uint)positions.Length)
                {
                    throw new InvalidDataException(
                        $"Target '{weightedTarget.Target.Source.InstancedPath}' references vertex {vertex.SourceIndex}; base mesh has {positions.Length} vertices.");
                }

                positions[vertex.SourceIndex] += vertex.PositionDelta * weightedTarget.Weight;
                normals[vertex.SourceIndex] += vertex.NormalDelta * weightedTarget.Weight;
                appliedDeltaCount++;
            }
        }

        for (var index = 0; index < normals.Length; index++)
        {
            var normal = normals[index];
            if (!IsFinite(normal) || normal.LengthSquared() < 1e-12f)
            {
                notes.Add($"Normal {index} could not be normalised and was replaced with +Z.");
                normals[index] = Vector3.UnitZ;
                continue;
            }

            normals[index] = Vector3.Normalize(normal);
        }

        return new DeformationResult(positions, normals, appliedDeltaCount, notes);
    }

    public static Vector3[] EvaluatePositions(
        IReadOnlyList<Vector3> basePositions,
        IReadOnlyList<WeightedMorphTarget> weightedTargets,
        int lodIndex)
    {
        ArgumentNullException.ThrowIfNull(basePositions);
        ArgumentNullException.ThrowIfNull(weightedTargets);
        ArgumentOutOfRangeException.ThrowIfNegative(lodIndex);
        var positions = basePositions.ToArray();
        foreach (var weightedTarget in weightedTargets)
        {
            if (!float.IsFinite(weightedTarget.Weight))
            {
                throw new InvalidDataException($"Weight for '{weightedTarget.FeatureName}' is not finite.");
            }
            var lod = weightedTarget.Target.Lods.FirstOrDefault(value => value.LodIndex == lodIndex);
            if (lod is null)
            {
                continue;
            }
            if (lod.BaseMeshVertexCount != positions.Length)
            {
                throw new InvalidDataException(
                    $"Target '{weightedTarget.Target.Source.InstancedPath}' LOD {lodIndex} expects {lod.BaseMeshVertexCount} vertices; base mesh has {positions.Length}.");
            }
            foreach (var vertex in lod.Vertices)
            {
                if ((uint)vertex.SourceIndex >= (uint)positions.Length)
                {
                    throw new InvalidDataException(
                        $"Target '{weightedTarget.Target.Source.InstancedPath}' LOD {lodIndex} references vertex {vertex.SourceIndex}; base mesh has {positions.Length} vertices.");
                }
                positions[vertex.SourceIndex] += vertex.PositionDelta * weightedTarget.Weight;
            }
        }
        return positions;
    }

    private static bool IsFinite(Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
