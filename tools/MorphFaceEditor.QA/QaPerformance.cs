namespace MorphFaceEditor.QA;

internal enum QaStageKind { Diagnostic, Load, Randomisation, FileTransfer, Render }

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

    public static void ApplyWarnings(QaResult result)
    {
        var slow = result.Stages.Where(stage => stage.ExceededLimit).ToArray();
        if (slow.Length == 0) return;
        foreach (var stage in slow)
            result.PerformanceWarnings.Add($"{stage.Name}: {stage.DurationMs:F1} ms (limit {stage.LimitMs:F0} ms).");
        if (result.Status == "passed") result.Status = "warning";
    }
}
