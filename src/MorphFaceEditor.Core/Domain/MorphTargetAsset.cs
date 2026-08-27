using System.Numerics;

namespace MorphFaceEditor.Core.Domain;

public readonly record struct MorphVertexDelta(
    int SourceIndex,
    Vector3 PositionDelta,
    Vector3 NormalDelta);

public sealed record MorphTargetLod(
    int LodIndex,
    int BaseMeshVertexCount,
    IReadOnlyList<MorphVertexDelta> Vertices);

public sealed record MorphTargetBoneOffset(string BoneName, Vector3 Offset);

public sealed record MorphTargetAsset(
    AssetIdentity Source,
    IReadOnlyList<MorphTargetLod> Lods,
    IReadOnlyList<MorphTargetBoneOffset> BoneOffsets);
