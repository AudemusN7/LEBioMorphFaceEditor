using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

public static class CpuSkinningEvaluator
{
    public static DeformationResult Skin(
        DeformationResult deformation,
        SkeletalMeshRenderData renderData,
        IReadOnlyList<Matrix4x4> palette)
    {
        ArgumentNullException.ThrowIfNull(deformation);
        ArgumentNullException.ThrowIfNull(renderData);
        ArgumentNullException.ThrowIfNull(palette);
        if (deformation.Positions.Length != renderData.BoneIndices.Count ||
            deformation.Positions.Length != renderData.BoneWeights.Count)
        {
            throw new InvalidDataException("Deformed vertex and skin-influence counts do not match.");
        }

        var positions = new Vector3[deformation.Positions.Length];
        var normals = new Vector3[deformation.Normals.Length];
        for (var vertexIndex = 0; vertexIndex < positions.Length; vertexIndex++)
        {
            var indices = renderData.BoneIndices[vertexIndex];
            var weights = renderData.BoneWeights[vertexIndex];
            var useFallbackInfluence = weights.X <= 0 && weights.Y <= 0 && weights.Z <= 0 && weights.W <= 0;
            var position = Vector3.Zero;
            var normal = Vector3.Zero;
            var totalWeight = 0f;
            for (var influence = 0; influence < 4; influence++)
            {
                var weight = useFallbackInfluence
                    ? influence == 0 ? 1f : 0f
                    : influence switch
                    {
                        0 => weights.X,
                        1 => weights.Y,
                        2 => weights.Z,
                        _ => weights.W
                    };
                if (weight <= 0)
                {
                    continue;
                }
                var boneIndex = indices[influence];
                if ((uint)boneIndex >= (uint)palette.Count)
                {
                    throw new InvalidDataException($"Vertex {vertexIndex} references palette bone {boneIndex}.");
                }
                position += Vector3.Transform(deformation.Positions[vertexIndex], palette[boneIndex]) * weight;
                normal += Vector3.TransformNormal(deformation.Normals[vertexIndex], palette[boneIndex]) * weight;
                totalWeight += weight;
            }
            positions[vertexIndex] = position / totalWeight;
            normals[vertexIndex] = normal.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(normal);
        }
        return new DeformationResult(
            positions,
            normals,
            deformation.AppliedDeltaCount,
            deformation.Notes.Concat(["Final-skeleton palette applied by linear blend skinning."]).ToArray());
    }
}
