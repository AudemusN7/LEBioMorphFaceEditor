using System.Text.Json;
using MorphFaceEditor.QA;

namespace MorphFaceEditor.Tests;

internal static class QaUtilityTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("QA coverage streams reproduce filtered cases and vary donors", Selection),
        new("QA 100-roll batch retains failures and continues to the final outcome", () => Batch().GetAwaiter().GetResult()),
        new("QA final roll failure remains explicit", () => FinalFailure().GetAwaiter().GetResult()),
        new("QA performance warnings retain functional failures", Performance),
        new("QA report presents warning category roll average and escaped evidence", Report)
    ];

    private static void Selection()
    {
        var whole = QaCoverageSelection.Stream(123, "load/le2-hmm-npc", "source");
        _ = QaCoverageSelection.Stream(123, "another-case", "source").Next();
        var filtered = QaCoverageSelection.Stream(123, "load/le2-hmm-npc", "source");
        for (var index = 0; index < 100; index++) TestAssert.Equal(whole.Next(), filtered.Next());
        var donors = Enumerable.Range(1, 100).Select(seed => QaCoverageSelection.Pick(
            new[] { "A", "B", "C" }, QaCoverageSelection.Stream(seed, "case", "source"))).ToHashSet();
        TestAssert.Equal(3, donors.Count);
        TestAssert.True(QaCoverageSelection.Stream(123, "case", "source").Next() !=
            QaCoverageSelection.Stream(123, "case", "editor").Next(), "Donor and operation streams overlap.");
    }

    private static async Task Batch()
    {
        var state = 0;
        var progress = new List<int>();
        var summary = await QaRandomisationBatch.Run(roll =>
        {
            state = roll;
            if (roll == 7) throw new InvalidDataException("Missing texture");
            return Task.FromResult<IReadOnlyList<string>>(roll == 19 ? ["Skipped unresolved family"] : []);
        }, progress.Add);
        TestAssert.Equal(100, state);
        TestAssert.Equal(100, summary.Attempts);
        TestAssert.Equal(98, summary.Successful);
        TestAssert.True(summary.FinalRollSucceeded && summary.Failures.Select(value => value.Roll).SequenceEqual(new[] { 7, 19 }),
            "Earlier roll failures were lost or stopped the batch.");
        TestAssert.Equal(summary.TotalMs / 100, summary.AverageMs);
        TestAssert.True(progress.SequenceEqual(Enumerable.Range(1, 10).Select(value => value * 10)), "Batch progress is incomplete.");
    }

    private static async Task FinalFailure()
    {
        var summary = await QaRandomisationBatch.Run(roll => Task.FromResult<IReadOnlyList<string>>(
            roll == 100 ? ["Final texture unresolved"] : []));
        TestAssert.True(!summary.FinalRollSucceeded && summary.Successful == 99 && summary.Failures.Single().Roll == 100,
            "The final failure was treated as a successful retained outcome.");
    }

    private static void Performance()
    {
        var result = new QaResult("case", "case") { Status = "passed" };
        result.Stages.Add(new("Slow Load", 1501, 0, 0, 0, 0, 0, 0, 0, QaStageKind.Load));
        result.Stages.Add(new("Render", 100000, 0, 0, 0, 0, 0, 0, 0, QaStageKind.Render));
        QaPerformance.ApplyWarnings(result);
        TestAssert.Equal("warning", result.Status);
        TestAssert.Equal(1, result.PerformanceWarnings.Count);
        TestAssert.True(result.Error is null, "Performance breach became an error.");
        result.Status = "failed"; result.Error = "Texture failure";
        QaPerformance.ApplyWarnings(result);
        TestAssert.Equal("failed", result.Status);
        TestAssert.Equal("Texture failure", result.Error);
        TestAssert.True(!QaPerformance.Exceeded(QaStageKind.Randomisation, 1500), "Limit boundary is inclusive.");
    }

    private static void Report()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFE-QA-Report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = new QaReport(directory, 123, ["material-ron/le2-asa"], "synthetic") { Complete = true };
            var result = new QaResult("material-ron/le2-asa", "case") { Group = "material-ron", Status = "warning" };
            result.PerformanceWarnings.Add("Slow <script>stage</script>");
            result.Evidence["rolls"] = new QaRollSummary(100, 100, 12.5, 1250, true, []);
            report.Results.Add(result); report.Write();
            var html = File.ReadAllText(report.HtmlPath);
            TestAssert.True(System.Net.WebUtility.HtmlDecode(html).Contains("Material RON · LE2 · ASA") && html.Contains("average 12.5 ms") &&
                html.Contains("&lt;script&gt;") && !html.Contains("<script>"), "Report labels, summary or escaping regressed.");
            using var json = JsonDocument.Parse(File.ReadAllText(report.ResultsPath));
            TestAssert.Equal(1, json.RootElement.GetProperty("Warnings").GetInt32());
            TestAssert.Equal(0, json.RootElement.GetProperty("Failed").GetInt32());
            result.Status = "failed"; result.Error = "Missing required texture";
            report.Write();
            using var failed = JsonDocument.Parse(File.ReadAllText(report.ResultsPath));
            TestAssert.Equal(1, failed.RootElement.GetProperty("Failed").GetInt32());
            TestAssert.Equal(1, failed.RootElement.GetProperty("PerformanceWarningCount").GetInt32());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
