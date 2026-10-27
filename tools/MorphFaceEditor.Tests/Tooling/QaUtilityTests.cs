using MorphFaceEditor.Core.Domain;
using System.Text.Json;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.LegendaryExplorer;
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
        new("QA morph clipboard refusals accept localised errors but reject silent or changed targets", ClipboardRefusal),
        new("QA load oracle misses remain diagnostic without accepting invalid topology", () => LoadOracleDiagnostics().GetAwaiter().GetResult()),
        new("QA reports retain frozen timing limits and functional failures beside timing outcomes", Report)
    ];

    private static void ClipboardRefusal() => WpfTestDispatcher.Invoke(() =>
    {
        using var reader = new MorphFacePackageReader();
        using var editor = UiTestFixtures.CreateEditor(reader, ignoresAuthoredGeometry: true);
        var before = editor.CreateDraft();
        string? error = null;
        try { editor.ApplyMorphData(new(before.MorphFeatures, before.FinalSkeleton, before.BakedLods)); }
        catch (InvalidOperationException exception)
        {
            error = MorphFaceEditor.Infrastructure.UserFacingMessageFormatter.Format(exception);
        }
        TestAssert.True(!string.IsNullOrWhiteSpace(error), "The non-editable profile did not report a paste refusal.");
        QaCases.RequireRejectedMorphPaste(error, before, editor.CreateDraft());
        TestAssert.True(!editor.IsDirty, "The refused paste dirtied the real editor.");

        void MustFail(string? message, MorphFaceDocument after)
        {
            try { QaCases.RequireRejectedMorphPaste(message, before, after); }
            catch (InvalidDataException) { return; }
            throw new Exception("QA accepted a silent or mutating morph paste refusal.");
        }
        MustFail(null, before);
        MustFail(" ", before);
        MustFail(error, before with { MorphFeatures = [new("Target", 0.5f)] });
        MustFail(error, before with { FinalSkeleton = [new("root", System.Numerics.Vector3.One)] });
        MustFail(error, before with { BakedLods = [before.BakedLods[0].Reverse().ToArray()] });
    });

    private static async Task LoadOracleDiagnostics()
    {
        var source = PackageTestFixtures.FixturePath("LE2 GlobalMorphs.pcc");
        if (!File.Exists(source)) throw new TestSkippedException("The LE2 GlobalMorphs corpus is unavailable.");
        var directory = Path.Combine(Path.GetTempPath(), "MFE-QA-Oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var context = new QaContext(Environment.CurrentDirectory, Path.GetDirectoryName(source)!, directory, 123)
            {
                Face = new QaFaceSpec(MorphFaceGame.LE2, "HMF"),
                Current = new QaResult("load/oracle", "Existing authored deformation mismatch")
            };
            context.TrackInput(source);
            var original = await context.Load(source, "HMF.BioFace_Ashley");
            TestAssert.True(context.Current.Evidence["oracle"] is DeformationComparisonReport { IsWithinTolerance: false } &&
                context.Current.Notes.Any(note => note.Contains("HMF.BioFace_Ashley", StringComparison.Ordinal)),
                "The native oracle miss was lost or did not produce an informational note.");
            TestAssert.True(!original.EditingSession.CanEditMorphFeatures,
                "QA changed the production oracle gate for unsafe morph editing.");

            // A different face identity and an unchanged copy must not need a named exception.
            var copy = Path.Combine(directory, "PreservedSource.pcc");
            File.Copy(source, copy);
            const string renamed = "HMF.BioFace_OracleFixture";
            using (var package = MEPackageHandler.OpenMEPackage(copy))
            {
                package.FindExport("HMF.BioFace_Ashley", "BioMorphFace")!.ObjectName = "BioFace_OracleFixture";
                package.Save();
            }
            var reopened = await context.Load(copy, renamed);
            TestAssert.True(context.Current.Evidence["oracle"] is DeformationComparisonReport { IsWithinTolerance: false } &&
                context.Current.Notes.Any(note => note.Contains(renamed, StringComparison.Ordinal)),
                "A preserved mismatch was accepted only for the original corpus identity.");
            var before = original.Loaded.Document;
            var after = reopened.Loaded.Document;
            PackageTestFixtures.AssertMorphEqual(new(before.MorphFeatures, before.FinalSkeleton, before.BakedLods),
                new(after.MorphFeatures, after.FinalSkeleton, after.BakedLods));

            using (var package = MEPackageHandler.OpenMEPackage(copy))
            {
                var face = package.FindExport(renamed, "BioMorphFace")!;
                var binary = face.GetBinaryData<BioMorphFace>();
                binary.LODs[0] = binary.LODs[0][..^1];
                face.WriteBinary(binary);
                package.Save();
            }
            string? failure = null;
            try { await context.Load(copy, renamed); }
            catch (InvalidDataException exception) { failure = exception.Message; }
            TestAssert.True(failure?.Contains("topology", StringComparison.OrdinalIgnoreCase) == true,
                $"An invalid vertex count was not rejected by topology validation: {failure ?? "no exception"}.");
            context.VerifyInputs();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

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
        TimingPolicy();
        var directory = Path.Combine(Path.GetTempPath(), "MFE-QA-Report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = new QaReport(directory, 123, [], "synthetic") { Complete = true };
            var result = new QaResult("load/test", "source text") { Status = "failed", Error = "<script>test</script>" };
            result.Stages.Add(TimingStage("Player fixture preparation", 500.1, QaStageKind.Load) with
            {
                LimitMs = 500, FailureLimitMs = 1500, TimingKey = "load/test | <source>",
                LimitSource = "Accepted .39 <baseline>"
            });
            result.Stages.Add(TimingStage("Render preview", 20000, QaStageKind.Render));
            result.Stages.Add(TimingStage("Randomisation", 10, QaStageKind.Randomisation) with
            {
                LimitMs = 200, FailureLimitMs = 600, SampleCount = 100, TotalDurationMs = 1000,
                LimitSource = "Accepted .39 baseline"
            });
            QaPerformance.ApplyOutcomes(result);
            TestAssert.True(result.Status == "failed" && result.Error == "<script>test</script>",
                "A timing warning changed the functional failure.");
            report.Results.Add(result);
            var severe = new QaResult("save/test", "severe timing") { Status = "passed" };
            severe.Stages.Add(TimingStage("Save PCC", 900.1, QaStageKind.FileTransfer) with
            {
                LimitMs = 300, FailureLimitMs = 900, TimingKey = "save/test | Save PCC",
                LimitSource = "Accepted .39 baseline"
            });
            QaPerformance.ApplyOutcomes(severe);
            TestAssert.True(severe.Status == "failed" && severe.Error is null &&
                            severe.PerformanceWarnings.Count == 0 && severe.PerformanceFailures.Count == 1,
                "A severe timing breach was not a separate failure.");
            report.Results.Add(severe);
            report.Write();
            var html = File.ReadAllText(report.HtmlPath);
            TestAssert.True(html.Contains("&lt;script&gt;test&lt;/script&gt;") && !html.Contains(result.Error),
                "Report source text became executable markup.");
            var attention = html[(html.IndexOf("<h2>Needs attention</h2>", StringComparison.Ordinal))..];
            attention = attention[..attention.IndexOf("</article>", StringComparison.Ordinal)];
            TestAssert.True(attention.Contains("Player fixture preparation — 500.1 ms / warning 500 ms / failure 1500 ms") &&
                attention.Contains("class='failed'>Save PCC") &&
                !attention.Contains("Render preview"), "Attention summary lost timing limits or included an exempt stage.");
            TestAssert.True(html.Contains("Accepted .39 &lt;baseline&gt;") &&
                            html.Contains("Key: load/test | &lt;source&gt;") &&
                            html.Contains("Warning 300 ms<br>Failure 900 ms") &&
                            html.Contains("10.0 ms average (100 rolls)") &&
                            html.Contains(QaTimingBaseline.Description),
                "Report lost limit provenance, save thresholds, batch units or the shared frozen policy.");
            using var json = JsonDocument.Parse(File.ReadAllText(report.ResultsPath));
            TestAssert.Equal(2, json.RootElement.GetProperty("Failed").GetInt32());
            TestAssert.Equal(1, json.RootElement.GetProperty("PerformanceWarningCount").GetInt32());
            TestAssert.Equal(1, json.RootElement.GetProperty("PerformanceFailureCount").GetInt32());
            TestAssert.True(json.RootElement.GetProperty("TimingPolicy").GetString()!.Contains(QaTimingBaseline.Description),
                "JSON omitted the frozen baseline metadata.");
            var stages = json.RootElement.GetProperty("Results")[0].GetProperty("Stages");
            var restored = JsonSerializer.Deserialize<QaStage>(stages[0].GetRawText(), new JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            })!;
            TestAssert.True(restored.LimitMs == 500 && restored.FailureLimitMs == 1500 &&
                            restored.TimingKey == result.Stages[0].TimingKey &&
                            restored.LimitSource == result.Stages[0].LimitSource && restored.TimingSeverity == "warning",
                "Reading retained stage evidence recomputed its limits or lost provenance.");
            TestAssert.True(stages[1].GetProperty("LimitMs").ValueKind == JsonValueKind.Null &&
                            stages[1].GetProperty("FailureLimitMs").ValueKind == JsonValueKind.Null &&
                            stages[1].GetProperty("TimingSeverity").GetString() == "excluded" &&
                            stages[1].GetProperty("LimitSource").GetString() == "Excluded",
                "Render evidence acquired a limit or lost its exclusion source.");
            var saved = json.RootElement.GetProperty("Results")[1].GetProperty("Stages")[0];
            TestAssert.True(saved.GetProperty("LimitMs").GetDouble() == 300 &&
                            saved.GetProperty("FailureLimitMs").GetDouble() == 900 &&
                            saved.GetProperty("TimingSeverity").GetString() == "failure" &&
                            saved.GetProperty("LimitSource").GetString() == "Accepted .39 baseline",
                "Saved-output timing evidence lost its actual caps, severity or limit source.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void TimingPolicy()
    {
        var boundary = TimingStage("explicit limits", 500, QaStageKind.Load) with
            { LimitMs = 500, FailureLimitMs = 1500 };
        TestAssert.True(boundary.TimingSeverity == "pass" && !boundary.ExceededLimit &&
                        (boundary with { DurationMs = 500.01 }).TimingSeverity == "warning" &&
                        (boundary with { DurationMs = 1500 }).TimingSeverity == "warning" &&
                        !(boundary with { DurationMs = 1500 }).ExceededFailureLimit &&
                        (boundary with { DurationMs = 1500.01 }).TimingSeverity == "failure",
            "Stored warning/failure caps lost their strict boundaries.");
        var warning = new QaResult("warning/test", "warning is not fatal") { Status = "passed" };
        warning.Stages.Add(boundary with { DurationMs = 500.01 });
        QaPerformance.ApplyOutcomes(warning);
        TestAssert.True(warning.Status == "warning" && warning.Error is null &&
                        warning.PerformanceWarnings.Count == 1 && warning.PerformanceFailures.Count == 0,
            "An ordinary timing warning failed the case.");
        foreach (var status in new[] { "failed", "blocked" })
        {
            var functional = new QaResult("functional/test", "functional outcome")
                { Status = status, Error = "Original functional error" };
            functional.Stages.Add(boundary with { DurationMs = 1500.01 });
            QaPerformance.ApplyOutcomes(functional);
            TestAssert.True(functional.Status == status && functional.Error == "Original functional error" &&
                            functional.PerformanceFailures.Count == 1,
                "A timing failure overwrote the functional outcome.");
        }

        var known = new QaResult("load/le1-kro", "accepted variable source") { Group = "load" };
        known.Stages.Add(TimingStage("041 · Test workflow · Load · KRO.FirstFace", 300, QaStageKind.Load));
        QaTimingBaseline.Apply(known);
        TestAssert.True(known.Stages[0].LimitMs == 500 && known.Stages[0].FailureLimitMs == 1500 &&
                        known.Stages[0].TimingKey is not null,
            "The accepted 300 ms source did not retain its calibrated 500/1500 ms limits.");
        var changed = new QaResult(known.Id, "different seed and measured duration") { Group = known.Group };
        changed.Stages.Add(TimingStage("901 · Test workflow · Load · KRO.OtherFace", 20000, QaStageKind.Load));
        QaTimingBaseline.Apply(changed);
        TestAssert.True(changed.Stages[0].TimingKey == known.Stages[0].TimingKey &&
                        changed.Stages[0].LimitMs == 500 && changed.Stages[0].FailureLimitMs == 1500,
            "Selected face, display sequence or a slow judged run rebased the frozen limit.");

        var native = new QaResult("pcsav/le2-male-custom", "fixed native source") { Group = "pcsav" };
        native.Stages.Add(TimingStage("001 · Test workflow · Load · BIOG_MORPH_FACE.QaCareer", 1535, QaStageKind.Load));
        native.Stages.Add(TimingStage("020 · Test workflow · Load · BIOG_MORPH_FACE.Reopened", 1535, QaStageKind.Load));
        QaTimingBaseline.Apply(native);
        TestAssert.True(native.Stages[0].LimitMs == 2000 && native.Stages[0].FailureLimitMs == 4000 &&
                        native.Stages[0].TimingKey != native.Stages[1].TimingKey,
            "Fixed native limits or original/reopened load grouping changed.");

        foreach (var (id, loadLimit, renderLimit, auditLimit) in new[]
                 {
                     ("le1-hmf-player", 1200d, 300d, 400d), ("le1-hmm-player", 1700d, 400d, 300d),
                     ("le2-hmf-player", 1500d, 300d, 300d), ("le2-hmm-player", 2000d, 300d, 300d),
                     ("le3-hmf-player", 1400d, 300d, 400d), ("le3-hmm-player", 1300d, 300d, 600d)
                 })
        {
            var player = new QaResult("load/" + id, "seeded Player fixture");
            player.Stages.Add(TimingStage("001 · Test workflow · Load · BIOG_MORPH_FACE.Player", 0, QaStageKind.Load));
            player.Stages.Add(TimingStage("002 · Test workflow · Preview renderer initialisation · loaded.png", 0, QaStageKind.Load));
            const string fixture = "abcdef012345-0010-seeded-player.pcc";
            player.Artifacts.Add(fixture);
            QaTimingBaseline.Apply(player);
            var playerAudit = new QaResult("pcc-audit", "seeded Player package audit");
            playerAudit.Stages.Add(TimingStage("003 · Test workflow · PCC read/import audit " + fixture, 0, QaStageKind.FileTransfer));
            QaTimingBaseline.Apply(playerAudit, [player]);
            TestAssert.True(player.Stages[0].LimitMs == loadLimit && player.Stages[1].LimitMs == renderLimit &&
                            playerAudit.Stages[0].LimitMs == auditLimit &&
                            player.Stages.Concat(playerAudit.Stages).All(stage =>
                                stage.FailureLimitMs == stage.LimitMs * 3 &&
                                stage.LimitSource == "Accepted 0.10.0.39 baseline (+50%)"),
                "Seeded Player descendants inherited fixed-input limits: " + id);
        }

        var batch = new QaResult("npc-randomisation/le1-kro", "mean per roll") { Group = "npc-randomisation" };
        batch.Stages.Add(TimingStage("013 · Test workflow · Production randomisation · 100 rolls · average", 150,
            QaStageKind.Randomisation) with { SampleCount = 100, TotalDurationMs = 15000 });
        QaTimingBaseline.Apply(batch);
        TestAssert.True(batch.Stages[0].LimitMs == 200 && batch.Stages[0].FailureLimitMs == 600 &&
                        !batch.Stages[0].ExceededLimit && batch.Stages[0].DurationMs == 150 &&
                        batch.Stages[0].TotalDurationMs == 15000,
            "Randomisation timing judged the batch total instead of its mean per roll.");

        var audit = new QaResult("pcc-audit", "current-case control artifact");
        const string control = "abcdef012345-0009-audit-control.pcc";
        audit.Artifacts.Add(Path.Combine("pcc-audit", control));
        audit.Stages.Add(TimingStage("020 · Test workflow · PCC read/import audit " + control,
            100, QaStageKind.FileTransfer));
        QaTimingBaseline.Apply(audit, [known]);
        TestAssert.True(audit.Stages[0].TimingKey!.Contains("PCC read/import audit · pcc-audit · audit-control.pcc") &&
                        audit.Stages[0].LimitSource.StartsWith("Accepted ", StringComparison.Ordinal),
            "Earlier case owners concealed the current audit's own control artifact.");

        var ron = new QaResult("ron-full/le2-hmf-npc", "distinct RON import routes");
        foreach (var purpose in new[] { "Import RON command", "Import RON command · package replacement",
                     "Import RON command · route unconfirmed" })
            ron.Stages.Add(TimingStage("007 · Test workflow · " + purpose, 3754, QaStageKind.FileTransfer));
        QaTimingBaseline.Apply(ron);
        TestAssert.True(ron.Stages[0].LimitMs == 600 && ron.Stages[0].FailureLimitMs == 1800 &&
                        ron.Stages.Skip(1).All(stage => stage.LimitMs == 5000 && stage.FailureLimitMs == 15000 &&
                                                      stage.LimitSource == "Global kind policy") &&
                        ron.Stages.Select(stage => stage.TimingKey).Distinct().Count() == 3,
            "RON replacement or unconfirmed routes inherited a matching live-edit timing cap.");

        var unknown = new QaResult("new-case/test", "uncalibrated operations");
        foreach (var kind in new[] { QaStageKind.Load, QaStageKind.Randomisation, QaStageKind.FileTransfer,
                     QaStageKind.ColdLoad, QaStageKind.Render, QaStageKind.Diagnostic })
            unknown.Stages.Add(TimingStage("New operation", 100000, kind));
        QaTimingBaseline.Apply(unknown);
        TestAssert.True(unknown.Stages.All(stage => stage.LimitMs == QaPerformance.Limit(stage.Kind) &&
                                                  stage.FailureLimitMs == QaPerformance.FailureLimit(stage.Kind)) &&
                        unknown.Stages.Where(stage => stage.LimitMs is null)
                            .All(stage => stage.TimingSeverity == "excluded" && !stage.ExceededLimit &&
                                          !stage.ExceededFailureLimit && stage.LimitSource == "Excluded") &&
                        unknown.Stages[0].LimitSource != known.Stages[0].LimitSource,
            "Unknown operations lost fallback limits or excluded stages acquired timing outcomes.");
    }

    private static QaStage TimingStage(string name, double duration, QaStageKind kind) =>
        new(name, duration, 0, 0, 0, 0, 0, 0, 0, kind);
}
