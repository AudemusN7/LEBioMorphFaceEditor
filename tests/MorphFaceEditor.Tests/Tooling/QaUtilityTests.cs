using MorphFaceEditor.Core.Domain;
using System.Text.Json;
using MorphFaceEditor.QA;

namespace MorphFaceEditor.Tests;

internal static class QaUtilityTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("QA roll batches retain early and final failures", () => BatchResults().GetAwaiter().GetResult()),
        new("QA coverage streams reproduce filtered cases and vary donors", Selection),
        new("QA native save values compare separately from preserved source ordering", NativeSaveOrdering),
        new("QA material comparison preserves duplicate counts and exact texture identities", MaterialNamesAndDuplicates),
        new("QA reports escape source text and retain functional failures beside timing warnings", Report)
    ];

    private static void MaterialNamesAndDuplicates()
    {
        var before = new[] { (Name: "nA", Value: "None"), (Name: "Colour", Value: "1"), (Name: "colour", Value: "2") };
        var same = new[] { (Name: "COLOUR", Value: "2"), (Name: "NA", Value: "None"), (Name: "colour", Value: "1") };
        TestAssert.True(QaMaterialValues.Normalise(before, row => row.Name, row => row.Value)
            .SequenceEqual(QaMaterialValues.Normalise(same, row => row.Name, row => row.Value)), "Equivalent material rows differ.");
        TestAssert.True(!QaMaterialValues.Differences(before, same, row => row.Name, row => row.Value, "Texture").Any(),
            "Material diagnostics report a case-only difference.");
        foreach (var changed in new[] { same[..2], same.Append(same[0]).ToArray(), new[] { (Name: "NA", Value: "Changed") } })
            TestAssert.True(QaMaterialValues.Differences(before, changed, row => row.Name, row => row.Value, "Texture").Any(),
                "Material comparison concealed a change or duplicate count.");

        var donor = new AssetIdentity("BIOG_HMM_HED_PROMorph.pcc", "Eye.EYE_Iris_Norm", 1, "Texture2D");
        var saved = new AssetIdentity("CustomHead.pcc", "BIOG_HMM_HED_PROMorph.Eye.EYE_Iris_Norm", 42, "Texture2D");
        TestAssert.True(QaTextureIdentity.SameTexture(donor, saved), "Seek-free and qualified paths falsely differ.");
        TestAssert.True(!QaTextureIdentity.SameTexture(donor, saved with
            { InstancedPath = "OtherPackage.Eye.EYE_Iris_Norm" }), "Same leaf in another package was accepted.");
        TestAssert.True(!QaTextureIdentity.SameTexture(donor, saved with
            { InstancedPath = "BIOG_HMM_HED_PROMorph.Other.EYE_Iris_Norm" }), "Same leaf in another group was accepted.");
        TestAssert.True(!QaTextureIdentity.SameTexture(donor, saved with
            { ClassName = "TextureCube" }), "A different texture class was accepted.");
    }

    private static void NativeSaveOrdering()
    {
        var head = new MorphFaceMorphData([new("A", 0.25f), new("B", 0)],
            [new("boneA", new(1, 2, 3)), new("boneB", new(4, 5, 6))],
            [new System.Numerics.Vector3[] { new(1, 2, 3), new(4, 5, 6) }]);
        var reordered = head with { MorphFeatures = head.MorphFeatures.Reverse().ToArray(),
            FinalSkeleton = head.FinalSkeleton.Reverse().ToArray() };
        TestAssert.True(QaHeadPayload.GeometryDifferences(head, reordered, compareNameOrder: false).Count == 0,
            "Native semantic comparison rejected unchanged values in source order.");
        TestAssert.True(QaHeadPayload.GeometryDifferences(head, reordered).Count > 0,
            "Raw payload comparison stopped enforcing authored ordering.");
        TestAssert.True(QaHeadPayload.RetainsOrder(new[] { "B", "A" }, new[] { "B", "A", "new" }), "Native append lost original ordering.");
        TestAssert.True(!QaHeadPayload.RetainsOrder(new[] { "B", "A" }, new[] { "A", "B" }), "Native reorder was accepted.");
        foreach (var damaged in new[]
        {
            reordered with { MorphFeatures = [new("B", 0), new("A", 0.5f)] },
            reordered with { MorphFeatures = [new("B", 0), new("A", 0.25f), new("A", 0.25f)] },
            reordered with { BakedLods = [head.BakedLods[0].Reverse().ToArray()] }
        })
            TestAssert.True(QaHeadPayload.GeometryDifferences(head, damaged, compareNameOrder: false).Count > 0,
                "Native comparison concealed a changed value, duplicate or vertex order.");
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
        foreach (var optional in new[] { "benchmark", "donor-sweep" })
        {
            var id = optional + "/le1-baf";
            TestAssert.True(!QaCoverageSelection.SelectCase(id, optional, null, false) &&
                !QaCoverageSelection.SelectCase(id, optional, ["BAF"], false) &&
                QaCoverageSelection.SelectCase(id, optional, [optional], false), "Optional work selection changed.");
        }
        TestAssert.True(QaCoverageSelection.SelectCase("donor-sweep/le1-baf", "donor-sweep", ["BAF"], true) &&
            !QaCoverageSelection.MatchesCaseFilter("load/le1-bat", "load", "BAF"), "Exhaustive/filter boundary changed.");
    }

    private static async Task BatchResults()
    {
        foreach (var (count, finalFailure) in new[] { (1, true), (3, false), (100, true) })
        {
            var calls = 0;
            var result = await QaRandomisationBatch.Run(roll =>
            {
                calls++;
                if (roll == 1) throw new InvalidDataException("Expected early failure");
                return Task.FromResult<IReadOnlyList<string>>(finalFailure && roll == count ? ["Expected final failure"] : []);
            }, attempts: count);
            TestAssert.Equal(count, calls);
            TestAssert.Equal(count, result.Attempts);
            TestAssert.Equal(finalFailure && count > 1 ? 2 : 1, result.Failures.Count);
            TestAssert.Equal(1, result.Failures.First().Roll);
            TestAssert.True(result.FinalRollSucceeded == !finalFailure &&
                (!finalFailure || result.Failures.Last().Roll == count),
                "The batch lost a failure or stopped before its final outcome.");
        }
    }

    private static void Report()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFE-QA-Report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = new QaReport(directory, 123, [], "synthetic") { Complete = true };
            var result = new QaResult("load/test", "source text") { Status = "failed", Error = "<script>test</script>" };
            result.Stages.Add(new("Player fixture preparation", 1567, 0, 0, 0, 0, 0, 0, 0, QaStageKind.Load));
            result.Stages.Add(new("Render preview", 20000, 0, 0, 0, 0, 0, 0, 0, QaStageKind.Render));
            QaPerformance.ApplyWarnings(result);
            report.Results.Add(result);
            report.Write();
            var html = File.ReadAllText(report.HtmlPath);
            TestAssert.True(html.Contains("&lt;script&gt;test&lt;/script&gt;") && !html.Contains(result.Error),
                "Report source text became executable markup.");
            var attention = html[(html.IndexOf("<h2>Needs attention</h2>", StringComparison.Ordinal))..];
            attention = attention[..attention.IndexOf("</article>", StringComparison.Ordinal)];
            TestAssert.True(attention.Contains("<li>Player fixture preparation — 1567.0 ms / 1500 ms</li>") &&
                !attention.Contains("Render preview"), "Attention summary lost timing limits or included an exempt stage.");
            using var json = JsonDocument.Parse(File.ReadAllText(report.ResultsPath));
            TestAssert.Equal(1, json.RootElement.GetProperty("Failed").GetInt32());
            TestAssert.Equal(1, json.RootElement.GetProperty("PerformanceWarningCount").GetInt32());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
