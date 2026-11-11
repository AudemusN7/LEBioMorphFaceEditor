using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

/// <summary>
/// Experimental LOD0 residual projection between the canonical female LE1 and LE3
/// Player heads. This is a geometric approximation, not a morph conversion or a
/// guarantee that independently authored shapes will match.
/// </summary>
public static class PlayerSurfaceCorrespondence
{
    private const string SourceSignature = "CFFEB46D20ECBEFA6F015BBA0407D7E260C89CE2426F1215AB1AB7695F290D22";
    private const string TargetSignature = "5C1312E2392081BBDCD4762AD416FDEABD5750CB19E8590689BB786F4F79D926";
    private const double UvContainmentTolerance = 1e-7;
    private const double UvTieTolerance = 1e-7;
    private const double UvDegenerateThreshold = 1e-12;
    private const double SurfaceDegenerateThreshold = 1e-16;
    private const double EdgeDegenerateThreshold = 1e-20;
    private const double MaximumUvPositionDistance = 0.5;
    private const double PositionFallbackMargin = 0.3;
    private static readonly int[] SourceSectionMaterials = [0, 1, 2, 3];
    private static readonly int[] TargetSectionMaterials = [3, 1, 0, 2];
    // Target sections: lash, scalp, eyes, face; source: lash, scalp, face, eyes.
    private static readonly int[] TargetToSourceSection = [0, 1, 3, 2];
    private static readonly string[] SourceMaterials = ["HMF_HED_PROLash_Mat_1a", "HMF_HED_PRO_Scalp_Mat", "HMF_HED_PROCustom_Face_Mat_1a", "HMF_EYE_PROAverage_MAT_1a"];
    private static readonly string[] TargetMaterials = ["HMF_EYE_PROAverage_MAT_1a", "HMF_HED_PRO_Scalp_Mat_CC", "HMF_HED_PROCustom_Face_Mat_1a", "HMF_HED_PROLash_Mat_1a"];

    public static Vector3[] Map(SkeletalMeshAsset source, SkeletalMeshAsset target, Vector3[] sourceOffsets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceOffsets);
        Validate(source, SourceSignature, 2232, SourceMaterials, SourceSectionMaterials, "HMF_HED_PROCustom_MDL");
        Validate(target, TargetSignature, 2390, TargetMaterials, TargetSectionMaterials, "HMF_HED_PROCustom_MDL_CC");
        if (sourceOffsets.Length != source.Positions.Length || sourceOffsets.Any(v => !Finite(v)))
            throw new ArgumentException("Source offsets must contain one finite vector per source vertex.", nameof(sourceOffsets));

        var sourceRender = source.RenderData!;
        var targetRender = target.RenderData!;
        var triangles = new Triangle[4][];
        for (var section = 0; section < 4; section++)
        {
            var part = source.Topology.Sections[section];
            triangles[section] = new Triangle[part.TriangleCount];
            for (var i = 0; i < part.TriangleCount; i++)
            {
                var start = part.BaseIndex + i * 3;
                triangles[section][i] = new Triangle(sourceRender.Indices[start], sourceRender.Indices[start + 1], sourceRender.Indices[start + 2]);
            }
        }
        var assigned = new bool[target.Positions.Length];
        var result = new Vector3[target.Positions.Length];
        for (var targetSection = 0; targetSection < 4; targetSection++)
        {
            var section = target.Topology.Sections[targetSection];
            var primary = TargetToSourceSection[targetSection];
            // Face/scalp boundaries moved between games; only the 3D fallback may cross them.
            var secondary = primary switch { 1 => 2, 2 => 1, _ => -1 };
            for (var n = section.BaseIndex; n < section.BaseIndex + section.TriangleCount * 3; n++)
            {
                var vertex = targetRender.Indices[n];
                if (assigned[vertex]) continue;
                assigned[vertex] = true;
                var q = Vector2d.From(targetRender.TextureCoordinates[vertex]);
                var p = Vector3d.From(target.Positions[vertex]);
                // UV islands can overlap, so ties are resolved using 3D distance.
                var bestUv = double.PositiveInfinity;
                foreach (var tri in triangles[primary])
                    if (TryUv(tri, sourceRender.TextureCoordinates, q, out _, out var distance))
                        bestUv = Math.Min(bestUv, distance);
                if (!double.IsFinite(bestUv)) throw new InvalidDataException("No nondegenerate source UV triangle.");
                var bestPosition = double.PositiveInfinity;
                var chosen = default(Triangle);
                var weights = default(Vector3d);
                foreach (var tri in triangles[primary])
                {
                    if (!TryUv(tri, sourceRender.TextureCoordinates, q, out var candidate, out var distance) || distance > bestUv + UvTieTolerance) continue;
                    var positionDistance = (Interpolate(tri, candidate, source.Positions) - p).Length;
                    if (positionDistance < bestPosition)
                    {
                        bestPosition = positionDistance;
                        chosen = tri;
                        weights = candidate;
                    }
                }
                var closest = double.PositiveInfinity;
                var closestTri = default(Triangle);
                var closestWeights = default(Vector3d);
                FindClosest(triangles[primary]);
                if (secondary >= 0) FindClosest(triangles[secondary]);
                if (!double.IsFinite(closest) || !double.IsFinite(bestPosition))
                    throw new InvalidDataException("Could not map a target vertex to a source triangle.");
                if (bestPosition > Math.Max(MaximumUvPositionDistance, closest + PositionFallbackMargin))
                {
                    chosen = closestTri;
                    weights = closestWeights;
                }
                var offset = Interpolate(chosen, weights, sourceOffsets);
                result[vertex] = new Vector3((float)offset.X, (float)offset.Y, (float)offset.Z);
                if (!Finite(result[vertex])) throw new InvalidDataException("Mapped offset is nonfinite.");

                void FindClosest(Triangle[] candidates)
                {
                    foreach (var tri in candidates)
                    {
                        var w = Closest3D(tri, source.Positions, p);
                        var distance = (Interpolate(tri, w, source.Positions) - p).Length;
                        if (distance < closest)
                        {
                            closest = distance;
                            closestTri = tri;
                            closestWeights = w;
                        }
                    }
                }
            }
        }
        if (assigned.Any(value => !value)) throw new InvalidDataException("Target mesh has unreferenced vertices.");
        return result;
    }

    private static void Validate(SkeletalMeshAsset mesh, string signature, int count, string[] materials, int[] sectionMaterials, string meshName)
    {
        if (mesh.Lods.Count == 0 || mesh.Lods[0].LodIndex != 0 || mesh.Positions.Length != count ||
            !string.Equals(mesh.Topology.SignatureSha256, signature, StringComparison.OrdinalIgnoreCase) ||
            !mesh.Source.InstancedPath.EndsWith("." + meshName, StringComparison.OrdinalIgnoreCase) ||
            mesh.RenderData is not { } render || mesh.Topology.Sections.Count != 4 || render.MaterialSlots.Count != 4 ||
            render.TextureCoordinates.Count != count || mesh.Positions.Any(v => !Finite(v)) ||
            render.TextureCoordinates.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y)))
            throw new InvalidDataException("Mesh is not the expected canonical Player LOD0.");
        for (var i = 0; i < 4; i++)
        {
            if (render.MaterialSlots[i] is not { } material ||
                !material.InstancedPath.EndsWith("." + materials[i], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Canonical Player material slots do not match.");
            var section = mesh.Topology.Sections[i];
            if (section.MaterialIndex != sectionMaterials[i])
                throw new InvalidDataException("Canonical Player section layout does not match.");
            if (section.BaseIndex < 0 || section.TriangleCount <= 0 ||
                (long)section.BaseIndex + 3L * section.TriangleCount > render.Indices.Count)
                throw new InvalidDataException("Invalid section index range.");
            for (var j = section.BaseIndex; j < section.BaseIndex + section.TriangleCount * 3; j++)
                if (render.Indices[j] < 0 || render.Indices[j] >= count) throw new InvalidDataException("Invalid mesh index.");
        }
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private readonly record struct Triangle(int A, int B, int C);
    private readonly record struct Vector2d(double X, double Y)
    {
        public static Vector2d From(Vector2 v) => new(v.X, v.Y);
        public static Vector2d operator -(Vector2d a, Vector2d b) => new(a.X - b.X, a.Y - b.Y);
        public static Vector2d operator +(Vector2d a, Vector2d b) => new(a.X + b.X, a.Y + b.Y);
        public static Vector2d operator *(Vector2d a, double b) => new(a.X * b, a.Y * b);
        public double Dot(Vector2d b) => X * b.X + Y * b.Y;
        public double Cross(Vector2d b) => X * b.Y - Y * b.X;
        public double Length => Math.Sqrt(Dot(this));
    }
    private readonly record struct Vector3d(double X, double Y, double Z)
    {
        public static Vector3d From(Vector3 v) => new(v.X, v.Y, v.Z);
        public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3d operator *(Vector3d a, double b) => new(a.X * b, a.Y * b, a.Z * b);
        public double Dot(Vector3d b) => X * b.X + Y * b.Y + Z * b.Z;
        public double Length => Math.Sqrt(Dot(this));
    }
    private static Vector3d Interpolate(Triangle t, Vector3d w, IReadOnlyList<Vector3> values) => Vector3d.From(values[t.A]) * w.X + Vector3d.From(values[t.B]) * w.Y + Vector3d.From(values[t.C]) * w.Z;

    private static bool TryUv(Triangle t, IReadOnlyList<Vector2> uv, Vector2d q, out Vector3d weights, out double distance)
    {
        var a = Vector2d.From(uv[t.A]);
        var b = Vector2d.From(uv[t.B]);
        var c = Vector2d.From(uv[t.C]);
        var e0 = b - a;
        var e1 = c - a;
        var d = q - a;
        var den = e0.Cross(e1);
        if (Math.Abs(den) <= UvDegenerateThreshold)
        {
            weights = default;
            distance = double.PositiveInfinity;
            return false;
        }
        var v = d.Cross(e1) / den;
        var w = e0.Cross(d) / den;
        var u = 1 - v - w;
        if (u >= -UvContainmentTolerance && v >= -UvContainmentTolerance && w >= -UvContainmentTolerance) weights = new Vector3d(u, v, w);
        else
        {
            var best = double.PositiveInfinity;
            var candidate = default(Vector3d);
            Edge(a, b, 0);
            Edge(b, c, 1);
            Edge(c, a, 2);
            weights = candidate;
            void Edge(Vector2d x, Vector2d y, int edge)
            {
                var delta = y - x;
                var length = delta.Dot(delta);
                var f = length > EdgeDegenerateThreshold ? Math.Clamp((q - x).Dot(delta) / length, 0, 1) : 0;
                var dist = (x + delta * f - q).Length;
                if (dist >= best) return;
                best = dist;
                candidate = edge switch
                {
                    0 => new Vector3d(1 - f, f, 0),
                    1 => new Vector3d(0, 1 - f, f),
                    _ => new Vector3d(f, 0, 1 - f)
                };
            }
        }
        distance = (a * weights.X + b * weights.Y + c * weights.Z - q).Length;
        return true;
    }

    private static Vector3d Closest3D(Triangle t, IReadOnlyList<Vector3> positions, Vector3d p)
    {
        var a = Vector3d.From(positions[t.A]);
        var b = Vector3d.From(positions[t.B]);
        var c = Vector3d.From(positions[t.C]);
        var v0 = b - a;
        var v1 = c - a;
        var v2 = p - a;
        var d00 = v0.Dot(v0);
        var d01 = v0.Dot(v1);
        var d11 = v1.Dot(v1);
        var d20 = v2.Dot(v0);
        var d21 = v2.Dot(v1);
        var denominator = d00 * d11 - d01 * d01;
        var best = double.PositiveInfinity;
        var weights = default(Vector3d);
        if (Math.Abs(denominator) > SurfaceDegenerateThreshold)
        {
            var v = (d11 * d20 - d01 * d21) / denominator;
            var w = (d00 * d21 - d01 * d20) / denominator;
            var u = 1 - v - w;
            if (u >= 0 && v >= 0 && w >= 0)
            {
                weights = new Vector3d(u, v, w);
                best = (a * u + b * v + c * w - p).Length;
            }
        }
        Edge(a, b, 0);
        Edge(b, c, 1);
        Edge(c, a, 2);
        return weights;
        void Edge(Vector3d x, Vector3d y, int edge)
        {
            var delta = y - x;
            var length = delta.Dot(delta);
            var f = length > EdgeDegenerateThreshold ? Math.Clamp((p - x).Dot(delta) / length, 0, 1) : 0;
            var distance = (x + delta * f - p).Length;
            if (distance >= best) return;
            best = distance;
            weights = edge switch
            {
                0 => new Vector3d(1 - f, f, 0),
                1 => new Vector3d(0, 1 - f, f),
                _ => new Vector3d(f, 0, 1 - f)
            };
        }
    }
}
