using System.Numerics;

namespace MorphFaceEditor.LegendaryExplorer.Interchange;

/// <summary>Recognises the compensating basis in verified older MFE exports.</summary>
internal static class GltfTangentCompatibility
{
    // This is supporting evidence for a validated legacy MFE sidecar, not a
    // general tangent repair rule: authored normal bakes can use other bases.
    internal static void RestoreLegacyNativeBasis(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IList<Vector4> tangents,
        IReadOnlyList<Vector2>? uvs,
        IReadOnlyList<int> indices,
        IReadOnlyList<(int Offset, int Count)> streams,
        IReadOnlyList<bool> suppliedTangents,
        IReadOnlyList<bool> suppliedNormals,
        IReadOnlyList<bool> suppliedUvs)
    {
        if (uvs is null) return;
        var referenced = new bool[positions.Count];
        var aligned = new int[positions.Count];
        var opposed = new int[positions.Count];
        for (var face = 0; face + 2 < indices.Count; face += 3)
        {
            var a = indices[face]; var b = indices[face + 1]; var c = indices[face + 2];
            Observe(a); Observe(b); Observe(c);
            if (!suppliedUvs[a] || !suppliedUvs[b] || !suppliedUvs[c]) continue;
            var edge1 = positions[b] - positions[a];
            var edge2 = positions[c] - positions[a];
            var uv1 = uvs[b] - uvs[a]; var uv2 = uvs[c] - uvs[a];
            var determinant = uv1.X * uv2.Y - uv1.Y * uv2.X;
            if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 1e-8f) continue;
            if (!TryDirection((edge1 * uv2.Y - edge2 * uv1.Y) / determinant, out var uvTangent) ||
                !TryDirection((edge2 * uv1.X - edge1 * uv2.X) / determinant, out var uvBitangent)) continue;
            Vote(a); Vote(b); Vote(c);

            void Observe(int vertex)
            {
                if (suppliedTangents[vertex] && suppliedNormals[vertex] && suppliedUvs[vertex])
                    referenced[vertex] = true;
            }

            void Vote(int vertex)
            {
                if (!referenced[vertex]) return;
                var value = tangents[vertex];
                if (value.W != -1f && value.W != 1f) return;
                if (!TryDirection(normals[vertex], out var normal)) return;
                var direction = new Vector3(value.X, value.Y, value.Z);
                if (!TryDirection(direction - normal * Vector3.Dot(normal, direction), out var tangent) ||
                    Vector3.Dot(tangent, uvTangent) < 0.5f ||
                    !TryDirection(Vector3.Cross(normal, tangent), out var bitangent)) return;
                var agreement = Vector3.Dot(bitangent * value.W, uvBitangent);
                if (agreement >= 0.5f) aligned[vertex]++;
                else if (agreement <= -0.5f) opposed[vertex]++;
            }
        }

        foreach (var (offset, count) in streams)
        {
            var referenceCount = 0;
            var reliableCount = 0;
            var opposedCount = 0;
            var validSigns = true;
            for (var vertex = offset; vertex < offset + count; vertex++)
            {
                if (suppliedTangents[vertex] && tangents[vertex].W != -1f && tangents[vertex].W != 1f)
                    validSigns = false;
                if (!referenced[vertex]) continue;
                referenceCount++;
                var votes = aligned[vertex] + opposed[vertex];
                if (votes == 0 || Math.Max(aligned[vertex], opposed[vertex]) < votes * 0.9f) continue;
                reliableCount++;
                if (opposed[vertex] > aligned[vertex]) opposedCount++;
            }
            // Each referenced vertex gets one vote rather than being weighted
            // by the number of triangle corners that share it.
            // Mirrored UV islands are compared against their own UV derivatives.
            if (!validSigns || reliableCount < 3 || reliableCount < referenceCount * 0.75f ||
                opposedCount < reliableCount * 0.95f) continue;
            for (var vertex = offset; vertex < offset + count; vertex++)
                if (suppliedTangents[vertex]) tangents[vertex] = tangents[vertex] with { W = -tangents[vertex].W };
        }
    }

    private static bool TryDirection(Vector3 value, out Vector3 direction)
    {
        var lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
        {
            direction = default;
            return false;
        }
        direction = value / MathF.Sqrt(lengthSquared);
        return true;
    }
}
