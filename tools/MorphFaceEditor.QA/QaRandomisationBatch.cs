using System.Diagnostics;

namespace MorphFaceEditor.QA;

internal sealed record QaRollFailure(int Roll, string Error);
internal sealed record QaRollSummary(int Attempts, int Successful, double AverageMs,
    double TotalMs, bool FinalRollSucceeded, IReadOnlyList<QaRollFailure> Failures);

internal static class QaRandomisationBatch
{
    internal const int RollCount = 100;

    internal static async Task<QaRollSummary> Run(Func<int, Task<IReadOnlyList<string>>> roll,
        Action<int>? progress = null)
    {
        var failures = new List<QaRollFailure>();
        var total = 0d;
        var successful = 0;
        var finalSucceeded = false;
        for (var index = 1; index <= RollCount; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            var errors = new List<string>();
            try { errors.AddRange(await roll(index)); }
            catch (Exception error) { errors.Add(error.ToString()); }
            finally { stopwatch.Stop(); total += stopwatch.Elapsed.TotalMilliseconds; }
            finalSucceeded = errors.Count == 0;
            if (finalSucceeded) successful++;
            else failures.AddRange(errors.Distinct().Select(error => new QaRollFailure(index, error)));
            if (index % 10 == 0) progress?.Invoke(index);
        }
        return new(RollCount, successful, total / RollCount, total, finalSucceeded, failures);
    }
}
