using System.Numerics;

namespace MorphFaceEditor.Core.Deformation;

public sealed record VertexError(int SourceIndex, float Error, Vector3 Predicted, Vector3 Expected);

public sealed record DeformationComparisonReport(
    int PredictedVertexCount,
    int ExpectedVertexCount,
    int ComparedVertexCount,
    int VerticesAboveTolerance,
    float Tolerance,
    float MaximumError,
    double RootMeanSquareError,
    double MeanError,
    IReadOnlyList<VertexError> LargestErrors,
    int AppliedDeltaCount,
    IReadOnlyList<string> Notes)
{
    public bool VertexCountsMatch => PredictedVertexCount == ExpectedVertexCount;

    public bool IsWithinTolerance => VertexCountsMatch && VerticesAboveTolerance == 0;
}

public static class DeformationComparison
{
    public static DeformationComparisonReport Compare(
        DeformationResult deformation,
        IReadOnlyList<Vector3> expected,
        float tolerance = 0.0001f,
        int largestErrorCount = 20)
    {
        ArgumentNullException.ThrowIfNull(deformation);
        ArgumentNullException.ThrowIfNull(expected);
        if (!float.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }

        if (largestErrorCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(largestErrorCount));
        }

        var count = Math.Min(deformation.Positions.Length, expected.Count);
        var errors = new VertexError[count];
        double sum = 0;
        double squaredSum = 0;
        var maximum = 0f;
        var aboveTolerance = 0;

        for (var index = 0; index < count; index++)
        {
            var predicted = deformation.Positions[index];
            var actual = expected[index];
            var error = Vector3.Distance(predicted, actual);
            if (!float.IsFinite(error))
            {
                error = float.PositiveInfinity;
            }

            errors[index] = new VertexError(index, error, predicted, actual);
            maximum = Math.Max(maximum, error);
            if (error > tolerance)
            {
                aboveTolerance++;
            }

            sum += error;
            squaredSum += (double)error * error;
        }

        var notes = deformation.Notes.ToList();
        if (deformation.Positions.Length != expected.Count)
        {
            notes.Add($"Vertex counts differ: predicted {deformation.Positions.Length}, expected {expected.Count}.");
        }

        var largest = errors
            .OrderByDescending(item => item.Error)
            .ThenBy(item => item.SourceIndex)
            .Take(largestErrorCount)
            .ToArray();

        return new DeformationComparisonReport(
            deformation.Positions.Length,
            expected.Count,
            count,
            aboveTolerance,
            tolerance,
            maximum,
            count == 0 ? 0 : Math.Sqrt(squaredSum / count),
            count == 0 ? 0 : sum / count,
            largest,
            deformation.AppliedDeltaCount,
            notes);
    }
}
