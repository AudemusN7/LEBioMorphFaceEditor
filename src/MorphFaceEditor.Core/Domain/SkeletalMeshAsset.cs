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

/// <summary>Complete renderer-facing data for one skeletal-mesh LOD.</summary>
public sealed record SkeletalMeshLod(
    int LodIndex,
    Vector3[] Positions,
    Vector3[] Normals,
    SkeletalMeshTopology Topology,
    SkeletalMeshRenderData RenderData);

public sealed record SkeletalMeshAsset(
    AssetIdentity Source,
    Vector3[] Positions,
    Vector3[] Normals,
    SkeletalMeshTopology Topology,
    SkeletalMeshRenderData? RenderData = null,
    IReadOnlyList<Vector3[]>? LodPositions = null,
    IReadOnlyList<SkeletalMeshLod>? Lods = null)
{
    public IReadOnlyList<Vector3[]> AvailableLodPositions => Lods is { Count: > 0 }
        ? Lods.OrderBy(lod => lod.LodIndex).Select(lod => lod.Positions).ToArray()
        : LodPositions ?? [Positions];

    public IReadOnlyList<SkeletalMeshLod> AvailableLods => Lods is { Count: > 0 }
        ? Lods
        : RenderData is null
            ? []
            : [new SkeletalMeshLod(0, Positions, Normals, Topology, RenderData)];

    public SkeletalMeshLod? FindLod(int lodIndex) =>
        AvailableLods.FirstOrDefault(lod => lod.LodIndex == lodIndex);
}
