using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MorphFaceEditor.QA;

internal sealed class QaResult(string id, string description)
{
    public string Id { get; } = id;
    public string Description { get; } = description;
    public string Group { get; set; } = "";
    public string Status { get; set; } = "running";
    public double DurationMs { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> PerformanceWarnings { get; } = [];
    public List<string> PerformanceFailures { get; } = [];
    public List<string> Notes { get; } = [];
    public Dictionary<string, object?> Evidence { get; } = [];
    public List<string> Artifacts { get; } = [];
    public List<QaStage> Stages { get; } = [];
}

internal sealed record QaStage(string Name, double DurationMs, long ManagedAllocatedBytes,
    long WorkingSetBytes, long PrivateBytes, int Handles, int Gen0Collections, int Gen1Collections, int Gen2Collections,
    QaStageKind Kind = QaStageKind.Diagnostic)
{
    public double? LimitMs { get; init; } = QaPerformance.Limit(Kind);
    public double? FailureLimitMs { get; init; } = QaPerformance.FailureLimit(Kind);
    public string? TimingKey { get; init; }
    public string LimitSource { get; init; } = QaPerformance.Limit(Kind) is null
        ? "Excluded" : "Global kind policy";
    public bool ExceededLimit => LimitMs is { } limit && DurationMs > limit;
    public bool ExceededFailureLimit => FailureLimitMs is { } limit && DurationMs > limit;
    public string TimingSeverity => LimitMs is null ? "excluded"
        : ExceededFailureLimit ? "failure" : ExceededLimit ? "warning" : "pass";
    public int SampleCount { get; init; } = 1;
    public string Phase { get; init; } = "Test workflow";
    public IReadOnlyDictionary<string, double>? BreakdownMs { get; init; }
    public int? CacheHits { get; init; }
    public int? CacheMisses { get; init; }
    public double? TotalDurationMs { get; init; }
}

internal sealed class QaReport(string directory, int seed, string[] planned, string runToken)
{
    public string ResultsPath => Path.Combine(directory, $"qa-{runToken}-results.json");
    public string HtmlPath => Path.Combine(directory, $"qa-{runToken}-report.html");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, IncludeFields = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public List<QaResult> Results { get; } = [];
    public bool Complete { get; set; }
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    public void Write()
    {
        Directory.CreateDirectory(directory);
        var data = new
        {
            Scope = "Expanded per-game archetype, conversion, player randomisation, and HMM mesh interchange coverage.",
            StartedUtc = _started, UpdatedUtc = DateTimeOffset.UtcNow, Complete, Seed = seed,
            Build = typeof(QaReport).Assembly.GetName().Version?.ToString(),
            Machine = Environment.MachineName, Runtime = Environment.Version.ToString(),
            Planned = planned, Passed = Count("passed"), Warnings = Count("warning"),
            PerformanceWarningCases = Results.Count(item => item.PerformanceWarnings.Count > 0),
            PerformanceWarningCount = Results.Sum(item => item.PerformanceWarnings.Count),
            PerformanceWarningsByPhase = Results.SelectMany(item => item.Stages)
                .Where(stage => stage.ExceededLimit && !stage.ExceededFailureLimit).GroupBy(stage => stage.Phase)
                .ToDictionary(group => group.Key, group => group.Count()),
            PerformanceFailureCases = Results.Count(item => item.PerformanceFailures.Count > 0),
            PerformanceFailureCount = Results.Sum(item => item.PerformanceFailures.Count),
            PerformanceFailuresByPhase = Results.SelectMany(item => item.Stages)
                .Where(stage => stage.ExceededFailureLimit).GroupBy(stage => stage.Phase)
                .ToDictionary(group => group.Key, group => group.Count()),
            TimingPolicy = QaTimingBaseline.Description + " Serial stages; Custom catalogue/reader reused for the run with normal invalidation. First-use catalogue costs remain limited and reported separately. Preview loads and saved-candidate verification use fresh readers. OS caches are not flushed. Allocation is process-wide volume, not peak heap.",
            Failed = Count("failed"), Blocked = Count("blocked"), Results
        };
        var json = JsonSerializer.Serialize(data, JsonOptions);
        AtomicWrite(ResultsPath, json);

        string H(string? value) => WebUtility.HtmlEncode(value) ?? "";
        var passed = Count("passed");
        var warnings = Count("warning");
        var failedCount = Count("failed");
        var blocked = Count("blocked");
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>MFE QA Report</title><style>
            :root{color-scheme:dark;--bg:#11151d;--panel:#1b2230;--line:#354155;--text:#e7eaf2;--muted:#aab4c5;--green:#8ae2aa;--amber:#ffd27a;--red:#ff9a9a;--blue:#9dc6ff}
            *{box-sizing:border-box}body{font:15px/1.5 system-ui,sans-serif;background:var(--bg);color:var(--text);max-width:1160px;margin:0 auto;padding:28px 20px 60px}
            a{color:var(--blue)}header,article,.summary{background:var(--panel);border:1px solid var(--line);border-radius:10px}header{padding:24px;margin-bottom:18px}
            h1,h2{line-height:1.2;margin-top:0}h1{margin-bottom:5px;font-size:1.8rem}h2{font-size:1.15rem;overflow-wrap:anywhere}.muted,small{color:var(--muted)}
            .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(125px,1fr));gap:10px;margin:16px 0}.summary{padding:12px 15px}.summary strong{display:block;font-size:1.5rem}
            article{padding:18px;margin:14px 0;scroll-margin-top:16px}.passed{color:var(--green)}.warning{color:var(--amber)}.failed,.blocked{color:var(--red)}
            .callout{border-left:3px solid var(--amber);padding:7px 11px;background:#282719;margin:8px 0;overflow-wrap:anywhere}.diagnostic{color:var(--muted)}
            .callout.failed{border-color:var(--red);background:#2b1f25}
            pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#10141b;padding:12px;border-radius:6px}img{max-width:min(280px,100%);height:auto}
            table{border-collapse:collapse;width:100%;margin:12px 0}td,th{text-align:left;padding:8px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted)}
            .scroll{overflow-x:auto}.timing-key{overflow-wrap:anywhere}details{margin-top:12px}summary{cursor:pointer;color:var(--blue)}
            </style></head><body><header><h1>MFE QA Report</h1>
            """);
        html.Append($"<p class='muted'>Seed {seed} · {Results.Count}/{planned.Length} cases completed · {(Complete ? "Run finished" : "Run in progress")}</p>");
        html.Append($"<div class='cards'><div class='summary passed'><strong>{passed}</strong>Passed</div><div class='summary warning'><strong>{warnings}</strong>Warnings</div><div class='summary failed'><strong>{failedCount}</strong>Failed</div><div class='summary blocked'><strong>{blocked}</strong>Blocked</div></div>");
        html.Append($"<p class='warning'>Performance: {Results.Sum(item => item.PerformanceWarnings.Count)} timing warnings across {Results.Count(item => item.PerformanceWarnings.Count > 0)} cases, including functional failures.</p>");
        html.Append($"<p class='failed'>Performance failures: {Results.Sum(item => item.PerformanceFailures.Count)} stages across {Results.Count(item => item.PerformanceFailures.Count > 0)} cases.</p>");
        foreach (var phase in Results.SelectMany(item => item.Stages)
                     .Where(stage => stage.ExceededLimit && !stage.ExceededFailureLimit)
                     .GroupBy(stage => stage.Phase))
            html.Append($"<p class='warning'>{H(phase.Key)}: {phase.Count()} slow stages.</p>");
        foreach (var phase in Results.SelectMany(item => item.Stages).Where(stage => stage.ExceededFailureLimit)
                     .GroupBy(stage => stage.Phase))
            html.Append($"<p class='failed'>{H(phase.Key)}: {phase.Count()} stages exceeded the failure limit.</p>");
        html.Append("<p class='muted'>Catalogue first use and session cache access are measured separately from Custom geometry/workspace construction. Fixture setup warnings describe setup operations, not the later test workflow. Readers for face loads and saved-output verification remain fresh. Stages run serially; Windows file caches are not flushed. Managed allocation is cumulative process-wide allocation during a stage, not peak or retained memory. Working set is a whole-process snapshot at stage end.</p>");
        html.Append($"<p><a href='{H(Path.GetFileName(ResultsPath))}'>Full JSON evidence and stage metrics</a></p><p class='muted'>{H(QaTimingBaseline.Description)} Actual warning/failure limits and their sources are recorded per stage. Exceeding a warning limit adds a warning; exceeding a failure limit fails the case after evidence is collected.</p></header>");

        html.Append("<article><h2>Results by utility</h2><div class='scroll'><table><thead><tr><th>Utility</th><th>Passed</th><th>Warnings</th><th>Failed</th><th>Blocked</th></tr></thead><tbody>");
        foreach (var group in Results.GroupBy(item => item.Group))
            html.Append($"<tr><td>{H(QaDisplay.Utility(group.Key))}</td><td>{group.Count(item => item.Status == "passed")}</td><td>{group.Count(item => item.Status == "warning")}</td><td>{group.Count(item => item.Status == "failed")}</td><td>{group.Count(item => item.Status == "blocked")}</td></tr>");
        html.Append("</tbody></table></div></article>");

        var attention = Results.Where(item => item.Status is "warning" or "failed" or "blocked").ToArray();
        if (attention.Length > 0)
        {
            html.Append("<article><h2>Needs attention</h2>");
            foreach (var result in attention)
            {
                html.Append($"<p class='{H(result.Status)}'><a class='{H(result.Status)}' href='#{H(result.Id)}'>{H(Label(result.Status))}: {H(QaDisplay.Case(result.Id))}</a></p>");
                var slow = result.Stages.Where(stage => stage.ExceededLimit).ToArray();
                if (slow.Length > 0)
                {
                    html.Append("<ul>");
                    foreach (var stage in slow)
                        html.Append($"<li class='{(stage.ExceededFailureLimit ? "failed" : "warning")}'>{H(stage.Name)} — {stage.DurationMs:F1} ms / warning {stage.LimitMs:F0} ms / failure {stage.FailureLimitMs:F0} ms</li>");
                    html.Append("</ul>");
                }
            }
            html.Append("</article>");
        }

        foreach (var result in Results)
        {
            html.Append($"<article id='{H(result.Id)}'><h2 class='{H(result.Status)}'>{H(Label(result.Status))} · {H(QaDisplay.Case(result.Id))}</h2><p class='muted'>{result.DurationMs / 1000:F2} s</p>");
            foreach (var value in result.Evidence.Values.OfType<QaRollSummary>())
                html.Append($"<p><strong>Randomisation:</strong> {value.Successful}/{value.Attempts} successful · average {value.AverageMs:F1} ms · {(value.Failures.Count == 0 ? "No failures" : $"{value.Attempts - value.Successful} failed rolls")}</p>");
            foreach (var warning in result.Warnings) html.Append($"<p class='callout'><strong>Warning:</strong> {H(warning)}</p>");
            foreach (var warning in result.PerformanceWarnings) html.Append($"<p class='callout'><strong>Performance warning:</strong> {H(warning)}</p>");
            foreach (var failure in result.PerformanceFailures) html.Append($"<p class='callout failed'><strong>Performance failure:</strong> {H(failure)}</p>");
            foreach (var note in result.Notes) html.Append($"<p class='diagnostic'><strong>Diagnostic:</strong> {H(note)}</p>");
            html.Append($"<details><summary>Evidence and asset donors</summary><pre>{H(JsonSerializer.Serialize(result.Evidence, JsonOptions))}</pre></details>");
            if (result.Stages.Count > 0)
            {
                html.Append("<div class='scroll'><table><thead><tr><th>Phase / stage</th><th>Time</th><th>Warning / failure limits</th><th>Allocation volume</th><th>Process working set</th></tr></thead><tbody>");
                foreach (var stage in result.Stages)
                {
                    var key = stage.TimingKey is { } timingKey
                        ? $"<br><small class='timing-key'>Key: {H(timingKey)}</small>" : "";
                    var limits = stage.LimitMs is { } limit
                        ? $"Warning {limit:F0} ms<br>Failure {stage.FailureLimitMs:F0} ms" : "Excluded";
                    html.Append($"<tr class='{(stage.ExceededFailureLimit ? "failed" : stage.ExceededLimit ? "warning" : "")}'><td><small>{H(stage.Phase)}</small><br>{H(stage.Name)}</td><td>{stage.DurationMs:F1} ms{(stage.SampleCount > 1 ? $" average ({stage.SampleCount} rolls)" : "")}</td><td>{limits}<br><small>{H(stage.LimitSource)}</small>{key}</td><td>{stage.ManagedAllocatedBytes / 1048576.0:F2} MiB{(stage.SampleCount > 1 ? " total" : "")}</td><td>{stage.WorkingSetBytes / 1048576.0:F1} MiB</td></tr>");
                    if (stage.BreakdownMs is { } breakdown)
                        html.Append($"<tr><td colspan='5'><details><summary>Load breakdown · cache hits {stage.CacheHits}, misses {stage.CacheMisses}</summary><p>Reader total includes its component stages; material substeps are within Materials. Do not sum these overlapping timings.</p><ul>{string.Concat(breakdown.Select(pair => $"<li>{H(pair.Key)}: {pair.Value:F1} ms</li>"))}</ul></details></td></tr>");
                }
                html.Append("</tbody></table></div>");
            }
            foreach (var artifact in result.Artifacts.Distinct())
            {
                var relative = Path.GetRelativePath(directory, artifact).Replace('\\', '/');
                var link = string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
                html.Append($"<p><a href='{H(link)}'>{H(relative)}</a></p>");
                if (Path.GetExtension(artifact).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    html.Append($"<a href='{H(link)}'><img src='{H(link)}' alt='{H(result.Id)}'></a>");
            }
            if (result.Error is not null) html.Append($"<pre class='failed'>{H(result.Error)}</pre>");
            html.Append("</article>");
        }
        html.Append("</body></html>");
        AtomicWrite(HtmlPath, html.ToString());

        int Count(string status) => Results.Count(item => item.Status == status);
    }

    private static string Label(string status) => status switch
    {
        "passed" => "Passed",
        "warning" => "Warning",
        "failed" => "Failed",
        "blocked" => "Blocked",
        _ => "Running"
    };

    private static void AtomicWrite(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
