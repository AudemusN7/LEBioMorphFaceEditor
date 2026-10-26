using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Windows.Threading;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var appLogSuppression = AppLog.SuppressPersistentWrites();
        if (args.Contains("--help"))
        {
            Console.WriteLine("MFE QA: game and archetype workflows, cross-game conversion, randomisation and export checks.");
            Console.WriteLine("Options: --output <new-directory> --corpus <directory> --seed <integer> --case <id> --exhaustive --list --audit-output <completed-run-directory> --self-check");
            return 0;
        }
        var cases = QaCases.All;
        if (args.Contains("--list"))
        {
            foreach (var item in cases) Console.WriteLine($"{item.Id}: {QaDisplay.Case(item.Id)}");
            return 0;
        }
        var exitCode = 2;
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { exitCode = await RunAsync(args); }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }
        var recognized = new HashSet<string> { "--output", "--corpus", "--seed", "--case", "--audit-output", "--self-check", "--exhaustive" };
        for (var index = 0; index < args.Length; index++)
        {
            if (!recognized.Contains(args[index])) throw new ArgumentException($"Unknown option '{args[index]}'.");
            if (args[index] is not "--self-check" and not "--exhaustive") index++;
        }
        var root = QaContext.FindWorkspace();
        var output = Path.GetFullPath(Option("--output") ?? Path.Combine(root, "artifacts", "qa",
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Choose a new or empty output directory; QA runs never overwrite previous evidence.");
        var seed = Option("--seed") is { } seedText
            ? int.Parse(seedText, System.Globalization.CultureInfo.InvariantCulture)
            : RandomNumberGenerator.GetInt32(1, int.MaxValue);
        using var context = new QaContext(root, Option("--corpus") ?? Path.Combine(root, "fixtures", "Global Morphs"),
            output, seed);
        context.AuditOutputDirectory = Option("--audit-output") is { } auditDirectory ? Path.GetFullPath(auditDirectory) : null;
        if (context.AuditOutputDirectory is not null && !Directory.Exists(context.AuditOutputDirectory))
            throw new DirectoryNotFoundException("Audit output directory does not exist: " + context.AuditOutputDirectory);
        var priorArtifactOwners = context.AuditOutputDirectory is null
            ? Array.Empty<QaResult>() : QaOutputFiles.ReadArtifactOwners(context.AuditOutputDirectory);
        var filter = Option("--case") ?? (context.AuditOutputDirectory is null ? null : "pcc-audit");
        var filters = filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = args.Contains("--self-check")
            ? new[] { new QaCase("runner-self-check", "Naming, flattened companions and saved-output evidence", QaRunnerCheck.Run, "runner-self-check") }
            : QaCases.All.Where(item => QaCoverageSelection.SelectCase(item.Id, item.Group, filters, args.Contains("--exhaustive"))).ToArray();
        if (selected.Length == 0) throw new ArgumentException($"Unknown case '{filter}'. Use --list.");
        var report = new QaReport(output, context.Seed, selected.Select(item => item.Id).ToArray(), context.RunToken);
        Console.WriteLine($"MFE QA | {selected.Length} cases | Seed {context.Seed}\nEvidence: {output}");
        Console.WriteLine($"Randomisation budget: {selected.Count(item => QaCoverageSelection.PlannedRolls(item.Group) == 100)} dedicated 100-roll cases; " +
            $"up to {selected.Sum(item => QaCoverageSelection.PlannedRolls(item.Group))} rolls total, plus at most six shared fixture setup rolls.");
        report.Write();
        foreach (var item in selected)
        {
            Console.WriteLine($"START {QaDisplay.Case(item.Id)}");
            var result = new QaResult(item.Id, item.Description) { Group = item.Group };
            context.Current = result;
            result.Evidence["plannedRollsUpperBound"] = QaCoverageSelection.PlannedRolls(item.Group);
            var materialComparisons = new ConcurrentQueue<MaterialInstanceComparisonDiagnostic>();
            using var comparisonObserver = MaterialInstanceSourcePolicy.ObserveComparisons(materialComparisons.Enqueue);
            var clock = Stopwatch.StartNew();
            try
            {
                await item.Run(context);
                context.ThrowRecordedFailures();
                result.Status = "passed";
            }
            catch (QaPrerequisiteException error)
            {
                var failures = context.RecordedFailureSummary;
                result.Status = failures is null ? "blocked" : "failed";
                result.Error = failures is null ? error.ToString() : failures + Environment.NewLine + error;
            }
            catch (Exception error)
            {
                result.Status = "failed";
                var recordedFailures = context.RecordedFailureSummary;
                var details = error.ToString();
                result.Error = string.IsNullOrWhiteSpace(recordedFailures) || details.Contains(recordedFailures, StringComparison.Ordinal)
                    ? details
                    : recordedFailures + Environment.NewLine + details;
            }
            finally
            {
                if (!materialComparisons.IsEmpty)
                    result.Evidence["materialInstanceComparisons"] = materialComparisons.ToArray();
                try { context.VerifyInputs(); }
                catch (Exception error)
                {
                    result.Status = "failed";
                    result.Error = string.Join(Environment.NewLine, new[] { result.Error, error.ToString() }.OfType<string>());
                }
            }
            QaTimingBaseline.Apply(result, [.. priorArtifactOwners, .. report.Results]);
            QaPerformance.ApplyOutcomes(result);
            result.DurationMs = clock.Elapsed.TotalMilliseconds;
            report.Results.Add(result);
            report.Write();
            Console.WriteLine($"{result.Status.ToUpperInvariant(),-7} {QaDisplay.Case(item.Id)} ({result.DurationMs / 1000:F1}s)");
            foreach (var warning in result.PerformanceWarnings)
                Console.WriteLine($"  Warning: {warning}");
            foreach (var failure in result.PerformanceFailures)
                Console.Error.WriteLine($"  Timing failure: {failure}");
            if (result.Error is not null) Console.Error.WriteLine(result.Error);
        }
        report.Complete = true;
        report.Write();
        var passed = report.Results.Count(item => item.Status == "passed");
        var warnings = report.Results.Count(item => item.Status == "warning");
        var failed = report.Results.Count(item => item.Status == "failed");
        var blocked = report.Results.Count(item => item.Status == "blocked");
        Console.WriteLine($"Complete: {passed} Passed, {warnings} Warning, {failed} Failed, {blocked} Blocked. Report: {report.HtmlPath}");
        var performanceWarnings = report.Results.Sum(item => item.PerformanceWarnings.Count);
        if (performanceWarnings > 0) Console.WriteLine($"Performance: {performanceWarnings} timing warnings across " +
            $"{report.Results.Count(item => item.PerformanceWarnings.Count > 0)} cases (including failed cases).");
        var performanceFailures = report.Results.Sum(item => item.PerformanceFailures.Count);
        if (performanceFailures > 0) Console.WriteLine($"Performance: {performanceFailures} severe timing failures across " +
            $"{report.Results.Count(item => item.PerformanceFailures.Count > 0)} cases.");
        return failed == 0 && blocked == 0 ? 0 : 1;
    }
}

internal sealed record QaCase(string Id, string Description, Func<QaContext, Task> Run, string Group);

internal sealed class QaPrerequisiteException(string message) : Exception(message);
