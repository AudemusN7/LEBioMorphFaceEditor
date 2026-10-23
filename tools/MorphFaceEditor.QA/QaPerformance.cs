namespace MorphFaceEditor.QA;

internal enum QaStageKind { Diagnostic, ColdLoad, Load, Randomisation, FileTransfer, Render }

internal static class QaPerformance
{
    public static double? Limit(QaStageKind kind) => kind switch
    {
        QaStageKind.Load or QaStageKind.Randomisation => 1500,
        QaStageKind.FileTransfer => 5000,
        _ => null
    };

    public static bool Exceeded(QaStageKind kind, double milliseconds) =>
        Limit(kind) is { } limit && milliseconds > limit;

    public static double? FailureLimit(QaStageKind kind) => Limit(kind) * 3;

    public static void ApplyOutcomes(QaResult result)
    {
        result.PerformanceWarnings.Clear();
        result.PerformanceFailures.Clear();
        foreach (var stage in result.Stages.Where(stage => stage.ExceededLimit && !stage.ExceededFailureLimit))
            result.PerformanceWarnings.Add($"{stage.Name}: {stage.DurationMs:F1} ms (limit {stage.LimitMs:F0} ms).");
        foreach (var stage in result.Stages.Where(stage => stage.ExceededFailureLimit))
            result.PerformanceFailures.Add($"{stage.Name}: {stage.DurationMs:F1} ms " +
                $"(failure limit {stage.FailureLimitMs:F0} ms; warning limit {stage.LimitMs:F0} ms).");
        if (result.PerformanceFailures.Count > 0 && result.Status is ("passed" or "warning"))
            result.Status = "failed";
        else if (result.PerformanceWarnings.Count > 0 && result.Status == "passed")
            result.Status = "warning";
    }
}
