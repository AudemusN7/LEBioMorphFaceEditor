using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Tests;

public static class MorphTargetCoefficientBasisTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("coefficient basis solves independent directions", Independent),
        new("coefficient basis preserves prior nullspace for duplicates", DuplicatePrior),
        new("coefficient basis leaves bone-only columns untouched", ZeroColumn),
        new("coefficient basis leaves out-of-span geometry as residual", OutOfSpan),
        new("coefficient basis retains small real direction", SmallDirection),
        new("coefficient basis rejects malformed and nonfinite input", InvalidInput)
    ];

    private static MorphTargetAsset Target(string name, params MorphVertexDelta[] deltas) =>
        new(TestFixtures.CreateIdentity(name, "MorphTarget"), [new MorphTargetLod(0, 1, deltas)], []);

    private static MorphVertexDelta Delta(Vector3 value) => new(0, value, Vector3.Zero);
    private static void Near(double expected, double actual, double tolerance = 1e-9) =>
        TestAssert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}; got {actual}.");

    private static void Independent()
    {
        var basis = new MorphTargetCoefficientBasis([Target("x", Delta(Vector3.UnitX)), Target("y", Delta(Vector3.UnitY))], 1);
        var result = basis.SolveCorrection([new Vector3(2, -3, 4)]);
        TestAssert.Equal(2, basis.Rank);
        Near(2, result[0]); Near(-3, result[1]);
    }

    private static void DuplicatePrior()
    {
        var basis = new MorphTargetCoefficientBasis([Target("a", Delta(Vector3.UnitX)), Target("b", Delta(Vector3.UnitX))], 1);
        var correction = basis.SolveCorrection([new Vector3(4, 0, 0)]);
        TestAssert.Equal(1, basis.Rank);
        TestAssert.Equal(1, basis.Nullspace.Count);
        Near(2, correction[0]); Near(2, correction[1]);
        var nullDirection = basis.Nullspace[0];
        Near(0, correction[0] * nullDirection[0] + correction[1] * nullDirection[1]);
        // Adding the correction to unequal prior weights keeps their difference.
        Near(-3, (1 + correction[0]) - (4 + correction[1]));
    }

    private static void ZeroColumn()
    {
        var boneOnly = new MorphTargetAsset(TestFixtures.CreateIdentity("bone", "MorphTarget"), [], []);
        var basis = new MorphTargetCoefficientBasis([Target("x", Delta(Vector3.UnitX)), boneOnly], 1);
        var result = basis.SolveCorrection([new Vector3(3, 0, 0)]);
        TestAssert.Equal(1, basis.Rank); Near(3, result[0]); Near(0, result[1]);
    }

    private static void OutOfSpan()
    {
        var basis = new MorphTargetCoefficientBasis([Target("x", Delta(Vector3.UnitX))], 1);
        var result = basis.SolveCorrection([new Vector3(2, 5, 0)]);
        Near(2, result[0]);
        Near(5, new Vector3(2, 5, 0).Y - (float)result[0] * Vector3.UnitX.Y);
    }

    private static void SmallDirection()
    {
        var basis = new MorphTargetCoefficientBasis([Target("large", Delta(new Vector3(50, 0, 0))), Target("small", Delta(new Vector3(0, 0.000085f, 0)))], 1);
        TestAssert.Equal(2, basis.Rank);
        var correction = basis.SolveCorrection([new Vector3(0, 0.00017f, 0)]);
        Near(2, correction[1], 1e-6);
    }

    private static void InvalidInput()
    {
        Throws(() => new MorphTargetCoefficientBasis([Target("bad", new MorphVertexDelta(1, Vector3.One, Vector3.Zero))], 1));
        Throws(() => new MorphTargetCoefficientBasis([Target("bad", Delta(new Vector3(float.NaN, 0, 0)))], 1));
        var basis = new MorphTargetCoefficientBasis([Target("x", Delta(Vector3.UnitX))], 1);
        Throws(() => basis.SolveCorrection([new Vector3(float.PositiveInfinity, 0, 0)]));
        Throws(() => basis.SolveCorrection([]));
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Expected invalid input to be rejected.");
    }
}
