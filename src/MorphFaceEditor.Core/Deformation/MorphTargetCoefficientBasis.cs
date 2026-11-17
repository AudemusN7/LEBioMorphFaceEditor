using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

/// <summary>
/// Factorizes canonical LOD0 position deltas once for repeated least-squares corrections.
/// Corrections contain no observable-nullspace component, preserving that part of a prior.
/// </summary>
public sealed class MorphTargetCoefficientBasis
{
    private const double RelativeSingularCutoff = 1e-7;
    private readonly int vertexCount;
    private readonly int columnCount;
    private readonly int reflectorCount;
    private readonly double[][] qrColumns;
    private readonly double[] tau;
    private readonly double[,] singularColumns;
    private readonly double[,] rightVectors;
    private readonly double[] singularSquared;
    private readonly int[] retained;
    private readonly double[][] nullspace;

    public MorphTargetCoefficientBasis(IReadOnlyList<MorphTargetAsset> targets, int vertexCount)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        this.vertexCount = vertexCount;
        columnCount = targets.Count;
        var rows = checked(vertexCount * 3);
        reflectorCount = Math.Min(rows, columnCount);
        qrColumns = new double[columnCount][];
        tau = new double[reflectorCount];

        for (var column = 0; column < columnCount; column++)
        {
            var values = qrColumns[column] = new double[rows];
            var target = targets[column] ?? throw new ArgumentException("A target is null.", nameof(targets));
            var lods = target.Lods.Where(lod => lod.LodIndex == 0).ToArray();
            if (lods.Length > 1)
                throw new ArgumentException("A target has multiple LOD0 entries.", nameof(targets));
            if (lods.Length == 0) continue; // Bone-only target.
            var lod = lods[0];
            if (lod.BaseMeshVertexCount != vertexCount)
                throw new ArgumentException("A target has an incompatible LOD0 vertex count.", nameof(targets));
            foreach (var delta in lod.Vertices)
            {
                if ((uint)delta.SourceIndex >= (uint)vertexCount || !IsFinite(delta.PositionDelta))
                    throw new ArgumentException("A target has an invalid LOD0 position delta.", nameof(targets));
                var row = delta.SourceIndex * 3;
                values[row] += delta.PositionDelta.X;
                values[row + 1] += delta.PositionDelta.Y;
                values[row + 2] += delta.PositionDelta.Z;
            }
        }

        // Householder QR avoids forming A^T A, whose squared condition number would
        // erase small but meaningful morph directions. Store each column contiguously
        // because applying a reflector scans thousands of coordinates in that column.
        for (var k = 0; k < reflectorCount; k++)
        {
            var reflector = qrColumns[k];
            double norm = 0;
            for (var i = k; i < rows; i++) norm = Hypot(norm, reflector[i]);
            if (norm == 0) continue;
            var first = reflector[k];
            var diagonal = first >= 0 ? -norm : norm;
            var denominator = first - diagonal;
            tau[k] = (diagonal - first) / diagonal;
            reflector[k] = diagonal;
            for (var i = k + 1; i < rows; i++) reflector[i] /= denominator;
            for (var j = k + 1; j < columnCount; j++)
            {
                var column = qrColumns[j];
                var dot = column[k];
                for (var i = k + 1; i < rows; i++) dot += reflector[i] * column[i];
                dot *= tau[k];
                column[k] -= dot;
                for (var i = k + 1; i < rows; i++) column[i] -= reflector[i] * dot;
            }
        }

        // One-sided Jacobi SVD of the small, zero-padded R produces both the
        // pseudoinverse directions and an orthonormal coefficient nullspace.
        singularColumns = new double[columnCount, columnCount];
        rightVectors = new double[columnCount, columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            rightVectors[i, i] = 1;
            if (i < reflectorCount)
                for (var j = i; j < columnCount; j++) singularColumns[i, j] = qrColumns[j][i];
        }
        OrthogonalizeColumns();
        singularSquared = new double[columnCount];
        for (var j = 0; j < columnCount; j++)
            for (var i = 0; i < columnCount; i++)
                singularSquared[j] += singularColumns[i, j] * singularColumns[i, j];
        var largest = singularSquared.Length == 0 ? 0 : singularSquared.Max();
        var cutoff = largest * RelativeSingularCutoff * RelativeSingularCutoff;
        retained = Enumerable.Range(0, columnCount).Where(j => singularSquared[j] > cutoff).ToArray();
        nullspace = Enumerable.Range(0, columnCount).Where(j => singularSquared[j] <= cutoff)
            .Select(j => Enumerable.Range(0, columnCount).Select(i => rightVectors[i, j]).ToArray()).ToArray();
    }

    public int Rank => retained.Length;
    public IReadOnlyList<double[]> Nullspace => nullspace;

    public double[] SolveCorrection(IReadOnlyList<Vector3> positionResidual)
    {
        ArgumentNullException.ThrowIfNull(positionResidual);
        if (positionResidual.Count != vertexCount)
            throw new ArgumentException("Residual vertex count does not match the basis.", nameof(positionResidual));
        var transformed = new double[checked(vertexCount * 3)];
        for (var i = 0; i < vertexCount; i++)
        {
            var value = positionResidual[i];
            if (!IsFinite(value)) throw new ArgumentException("Residual contains a non-finite position.", nameof(positionResidual));
            transformed[3 * i] = value.X;
            transformed[3 * i + 1] = value.Y;
            transformed[3 * i + 2] = value.Z;
        }
        for (var k = 0; k < reflectorCount; k++)
        {
            var reflector = qrColumns[k];
            var dot = transformed[k];
            for (var i = k + 1; i < transformed.Length; i++) dot += reflector[i] * transformed[i];
            dot *= tau[k];
            transformed[k] -= dot;
            for (var i = k + 1; i < transformed.Length; i++) transformed[i] -= reflector[i] * dot;
        }
        var result = new double[columnCount];
        foreach (var j in retained)
        {
            double dot = 0;
            for (var i = 0; i < reflectorCount; i++) dot += singularColumns[i, j] * transformed[i];
            var scale = dot / singularSquared[j];
            for (var i = 0; i < columnCount; i++) result[i] += rightVectors[i, j] * scale;
        }
        // Explicitly strip rounding leakage into every unobservable direction.
        foreach (var direction in nullspace)
        {
            double dot = 0;
            for (var i = 0; i < columnCount; i++) dot += result[i] * direction[i];
            for (var i = 0; i < columnCount; i++) result[i] -= dot * direction[i];
        }
        return result;
    }

    private void OrthogonalizeColumns()
    {
        for (var sweep = 0; sweep < 100; sweep++)
        {
            var changed = false;
            for (var p = 0; p < columnCount; p++)
            for (var q = p + 1; q < columnCount; q++)
            {
                double alpha = 0, beta = 0, gamma = 0;
                for (var i = 0; i < columnCount; i++)
                {
                    var x = singularColumns[i, p]; var y = singularColumns[i, q];
                    alpha += x * x; beta += y * y; gamma += x * y;
                }
                if (alpha == 0 || beta == 0 || Math.Abs(gamma) <= 1e-14 * Math.Sqrt(alpha) * Math.Sqrt(beta)) continue;
                var zeta = (beta - alpha) / (2 * gamma);
                var t = Math.CopySign(1.0, zeta) / (Math.Abs(zeta) + Hypot(1, zeta));
                var c = 1 / Math.Sqrt(1 + t * t);
                var s = c * t;
                for (var i = 0; i < columnCount; i++)
                {
                    Rotate(singularColumns, i, p, q, c, s);
                    Rotate(rightVectors, i, p, q, c, s);
                }
                changed = true;
            }
            if (!changed) return;
        }
        throw new InvalidOperationException("Morph target basis SVD did not converge.");
    }

    private static void Rotate(double[,] matrix, int row, int p, int q, double c, double s)
    {
        var x = matrix[row, p]; var y = matrix[row, q];
        matrix[row, p] = c * x - s * y;
        matrix[row, q] = s * x + c * y;
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static double Hypot(double a, double b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        var maximum = Math.Max(a, b);
        if (maximum == 0) return 0;
        var ratio = Math.Min(a, b) / maximum;
        return maximum * Math.Sqrt(1 + ratio * ratio);
    }
}
