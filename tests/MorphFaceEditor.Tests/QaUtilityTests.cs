using MorphFaceEditor.Core.Domain;
using System.Text.Json;
using MorphFaceEditor.QA;

namespace MorphFaceEditor.Tests;

internal static class QaUtilityTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("QA coverage streams reproduce filtered cases and vary donors", Selection),
        new("QA BAF filter selects all games and conversion directions without BAT", BafSelection),
        new("QA LE1 HMM conversion donors exclude Hench Human Male", ConversionDonorExclusion),
        new("QA unused Asari morph exclusion preserves neighbouring face identities", UnusedCorpusFaceExclusion),
        new("QA default matrix includes every human material and scalp regression route", HumanMaterialMatrix),
        new("QA 100-roll batch retains failures and continues to the final outcome", () => Batch().GetAwaiter().GetResult()),
        new("QA final roll failure remains explicit", () => FinalFailure().GetAwaiter().GetResult()),
        new("QA performance warnings retain functional failures", Performance),
        new("QA report presents warning category roll average and escaped evidence", Report)
    ];

    private static void UnusedCorpusFaceExclusion()
    {
        string[] candidates = ["HMF.Asari", "hmf.asari", "ASA.Asari", "HMF.AsariVariant", "HMF.cithub_csec_officer_b"];
        var selected = QaCoverageSelection.ExcludePaths(candidates, value => value,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        TestAssert.True(selected.SequenceEqual(candidates.Skip(2)),
            "Known unused morph exclusion either retained HMF.Asari or removed a different face identity.");
    }

    private static void BafSelection()
    {
        foreach (var id in new[] { "load/le1-baf", "clipboard/le2-baf", "npc-randomisation/le3-baf",
                     "conversion/le1-baf/le2", "conversion/le3-baf/le1" })
            TestAssert.True(QaCoverageSelection.MatchesCaseFilter(id, id.Split('/')[0], "BAF"), $"BAF filter missed {id}.");
        foreach (var id in new[] { "load/le1-bat", "clipboard/le2-tuf", "npc-randomisation/le3-hmf-player" })
            TestAssert.True(!QaCoverageSelection.MatchesCaseFilter(id, id.Split('/')[0], "BAF"), $"BAF filter included {id}.");
        TestAssert.True(QaCoverageSelection.MatchesCaseFilter("pcc-audit", "pcc-audit", "pcc-audit"), "Audit selection broke.");
    }

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

    private static void ConversionDonorExclusion()
    {
        var excluded = QaCoverageSelection.ConversionSourceExclusions(MorphFaceGame.LE1, "HMM");
        TestAssert.True(excluded.Contains("HMM.hench_humanmale"),
            "LE1 HMM conversion cases did not exclude Hench Human Male.");
        TestAssert.Equal(0, QaCoverageSelection.ConversionSourceExclusions(
            MorphFaceGame.LE2, "HMM").Count);
        TestAssert.Equal(0, QaCoverageSelection.ConversionSourceExclusions(
            MorphFaceGame.LE1, "HMF").Count);

        var candidates = new[] { "HMM.alternate_a", "HMM.hench_humanmale", "HMM.alternate_b" };
        var eligible = QaCoverageSelection.ExcludePaths(candidates, path => path, excluded);
        TestAssert.True(eligible.SequenceEqual(["HMM.alternate_a", "HMM.alternate_b"]),
            "Conversion donor filtering removed an allowed face or retained the excluded face.");
        for (var seed = 1; seed <= 100; seed++)
        {
            var selected = QaCoverageSelection.Pick(eligible, QaCoverageSelection.Stream(seed, "conversion/le1-hmm", "source"));
            TestAssert.True(!excluded.Contains(selected), "A seeded LE1 HMM conversion selected the excluded face.");
        }
    }

    private static void HumanMaterialMatrix()
    {
        var routes = QaHumanMaterialCoverage.All;
        static QaHumanMaterialRoute[] InGroup(string group) => QaHumanMaterialCoverage.All
            .Where(value => value.Group.Equals(group, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        TestAssert.Equal(6, InGroup("custom-human-materials").Length);
        TestAssert.Equal(7, InGroup("custom-human-hair").Length);
        TestAssert.Equal(7, InGroup("native-human-hair").Length);
        TestAssert.Equal(2, InGroup("cc-scalp-switch").Length);
        TestAssert.Equal(2, InGroup("player-cc-scalp").Length);
        TestAssert.Equal(1, InGroup("kaidan-preview").Length);

        foreach (var game in new[] { "le1", "le2", "le3" })
        foreach (var sex in new[] { "hmm", "hmf" })
            TestAssert.True(InGroup("custom-human-materials").Any(route =>
                    route.Id.Contains($"/{game}-{sex}-npc", StringComparison.OrdinalIgnoreCase)),
                $"The default QA matrix is missing {game} {sex} human material coverage.");

        var expectedHairRoutes = new[]
        {
            "le1-hmm-npc/human-hair", "le1-hmf-npc/human-iconic-femshep-hair",
            "le2-hmm-npc/human-hair", "le2-hmf-npc/human-iconic-femshep-hair",
            "le3-hmm-npc/human-hair", "le3-hmm-npc/human-advanced-hair", "le3-hmm-npc/human-james-hair"
        };
        foreach (var route in expectedHairRoutes)
            TestAssert.True(InGroup("custom-human-hair").Any(actual => actual.Id.EndsWith("/" + route,
                    StringComparison.OrdinalIgnoreCase)),
                $"The default QA matrix is missing Custom hair route '{route}'.");
        TestAssert.Equal(routes.Count, routes.Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
        TestAssert.True(!QaPerformance.Exceeded(QaStageKind.ColdLoad, 10000), "Cold setup was judged against a warm-load limit.");
    }

    private static void Report()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFE-QA-Report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = new QaReport(directory, 123, ["material-ron/le2-asa"], "synthetic") { Complete = true };
            var result = new QaResult("material-ron/le2-asa", "case") { Group = "material-ron", Status = "warning" };
            result.Stages.Add(new QaStage("Fixture export", 5900, 1024, 2048, 0, 0, 0, 0, 0, QaStageKind.FileTransfer)
                { Phase = "Player fixture preparation" });
            result.Stages.Add(new QaStage("Cold catalogue", 3000, 1024, 2048, 0, 0, 0, 0, 0, QaStageKind.Load)
                { Phase = "First-use catalogue" });
            result.Stages.Add(new QaStage("Cached catalogue", 1, 1024, 2048, 0, 0, 0, 0, 0, QaStageKind.Load)
                { Phase = "Session catalogue access" });
            result.Stages.Add(new QaStage("Actual load", 1000, 1024, 2048, 0, 0, 0, 0, 0, QaStageKind.Load)
            {
                BreakdownMs = new Dictionary<string, double> { ["Materials <detail>"] = 900 },
                CacheHits = 1, CacheMisses = 2
            });
            result.PerformanceWarnings.Add("Slow <script>stage</script>");
            result.Evidence["rolls"] = new QaRollSummary(100, 100, 12.5, 1250, true, []);
            report.Results.Add(result); report.Write();
            var html = File.ReadAllText(report.HtmlPath);
            TestAssert.True(System.Net.WebUtility.HtmlDecode(html).Contains("Material RON · LE2 · ASA") && html.Contains("average 12.5 ms") &&
                html.Contains("&lt;script&gt;") && !html.Contains("<script>"), "Report labels, summary or escaping regressed.");
            using var json = JsonDocument.Parse(File.ReadAllText(report.ResultsPath));
            TestAssert.Equal(1, json.RootElement.GetProperty("Warnings").GetInt32());
            TestAssert.Equal(0, json.RootElement.GetProperty("Failed").GetInt32());
            var phases = json.RootElement.GetProperty("PerformanceWarningsByPhase");
            TestAssert.Equal(1, phases.GetProperty("Player fixture preparation").GetInt32());
            TestAssert.Equal(1, phases.GetProperty("First-use catalogue").GetInt32());
            TestAssert.True(!phases.TryGetProperty("Session catalogue access", out _) &&
                !phases.TryGetProperty("Test workflow", out _),
                "Setup/cold warnings were attributed to passing warm workflow stages.");
            TestAssert.True(html.Contains("Materials &lt;detail&gt;") && html.Contains("cache hits 1, misses 2") &&
                html.Contains("not peak or retained memory") && html.Contains("Do not sum"),
                "Load breakdown, scope or allocation interpretation was omitted from the report.");
            result.Status = "failed"; result.Error = "Missing required texture";
            report.Write();
            using var failed = JsonDocument.Parse(File.ReadAllText(report.ResultsPath));
            TestAssert.Equal(1, failed.RootElement.GetProperty("Failed").GetInt32());
            TestAssert.Equal(1, failed.RootElement.GetProperty("PerformanceWarningCount").GetInt32());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
