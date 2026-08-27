namespace MorphFaceEditor.Benchmarks;

public static class BenchmarkStatistics
{
    public static double Percentile(double[] values, double percentile)
    {
        var sortedValues = values.Order().ToArray();
        var index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }
}
