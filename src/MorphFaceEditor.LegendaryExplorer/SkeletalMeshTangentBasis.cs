using System.Numerics;

namespace MorphFaceEditor.LegendaryExplorer;

public static class SkeletalMeshTangentBasis
{
    public static Vector4 ToRenderTangent(Vector4 tangentX, Vector4 tangentZ)
    {
        // UE3 stores the bitangent handedness in TangentZ.w. TangentX.w is a
        // separate packed channel and cannot be used to reconstruct the TBN.
        var bitangentSign = tangentZ.W < 0 ? -1f : 1f;
        return new Vector4(tangentX.X, tangentX.Y, tangentX.Z, bitangentSign);
    }

    public static Vector4 Orthogonalize(Vector4 tangent, Vector3 normal)
    {
        var unitNormal = Vector3.Normalize(normal);
        var direction = new Vector3(tangent.X, tangent.Y, tangent.Z);
        direction -= unitNormal * Vector3.Dot(direction, unitNormal);
        if (direction.LengthSquared() < 1e-10f)
        {
            var axis = Math.Abs(unitNormal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            direction = Vector3.Cross(axis, unitNormal);
        }
        direction = Vector3.Normalize(direction);
        return new Vector4(direction, tangent.W < 0 ? -1 : 1);
    }
}
