using System.Numerics;

namespace MorphFaceEditor.Core.Domain;

public sealed record MeshSectionTopology(
    int MaterialIndex,
    int ChunkIndex,
    int BaseIndex,
    int TriangleCount);

public sealed record MeshChunkTopology(
    int BaseVertexIndex,
    int RigidVertexCount,
    int SoftVertexCount,
    int MaxBoneInfluences,
    IReadOnlyList<int> BoneMap);

public sealed record ReferenceBone(
    string Name,
    int ParentIndex,
    Vector3 Position,
    Quaternion Orientation);

public sealed record SkeletalMeshTopology(
    int LodIndex,
    int VertexCount,
    int IndexCount,
    int MaterialCount,
    IReadOnlyList<MeshSectionTopology> Sections,
    IReadOnlyList<MeshChunkTopology> Chunks,
    IReadOnlyList<ReferenceBone> ReferenceSkeleton,
    IReadOnlyList<int> ActiveBones,
    IReadOnlyList<int> RequiredBones,
    string SignatureSha256);

public readonly record struct BoneIndex4(int X, int Y, int Z, int W)
{
    public int this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        3 => W,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

/// <summary>
/// LOD data needed by a renderer. Bone indices are reference-skeleton indices,
/// not chunk-local indices.
/// </summary>
public sealed record SkeletalMeshRenderData(
    IReadOnlyList<Vector4> Tangents,
    IReadOnlyList<Vector2> TextureCoordinates,
    IReadOnlyList<BoneIndex4> BoneIndices,
    IReadOnlyList<Vector4> BoneWeights,
    IReadOnlyList<int> Indices,
    IReadOnlyList<AssetIdentity?> MaterialSlots);

/// <summary>Geometry for one LOD, with optional streams needed for rendering and skinning.</summary>
public sealed record SkeletalMeshLod(
    int LodIndex,
    Vector3[] Positions,
    Vector3[] Normals,
    SkeletalMeshTopology Topology,
    SkeletalMeshRenderData? RenderData = null);

public sealed record SkeletalMeshAsset(
    AssetIdentity Source,
    IReadOnlyList<SkeletalMeshLod> Lods)
{
    // Producers supply LOD0 first, then lower LODs in ascending native index order.
    public Vector3[] Positions => Lods[0].Positions;
    public Vector3[] Normals => Lods[0].Normals;
    public SkeletalMeshTopology Topology => Lods[0].Topology;
    public SkeletalMeshRenderData? RenderData => Lods[0].RenderData;

    public SkeletalMeshLod? FindLod(int lodIndex) =>
        Lods.FirstOrDefault(lod => lod.LodIndex == lodIndex);

    /// <summary>Maps a native LOD index to its position in the ordered mesh/baked-LOD collection.</summary>
    public int FindLodOrdinal(int lodIndex)
    {
        for (var ordinal = 0; ordinal < Lods.Count; ordinal++)
        {
            if (Lods[ordinal].LodIndex == lodIndex) return ordinal;
        }
        return -1;
    }
}
