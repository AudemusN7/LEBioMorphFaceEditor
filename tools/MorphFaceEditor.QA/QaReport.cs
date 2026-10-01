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
    public List<string> Notes { get; } = [];
    public Dictionary<string, object?> Evidence { get; } = [];
    public List<string> Artifacts { get; } = [];
    public List<QaStage> Stages { get; } = [];
}

internal sealed record QaStage(string Name, double DurationMs, long ManagedAllocatedBytes,
    long WorkingSetBytes, long PrivateBytes, int Handles, int Gen0Collections, int Gen1Collections, int Gen2Collections,
    QaStageKind Kind = QaStageKind.Diagnostic)
{
    public double? LimitMs => QaPerformance.Limit(Kind);
    public bool ExceededLimit => QaPerformance.Exceeded(Kind, DurationMs);
    public int SampleCount { get; init; } = 1;
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
            pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#10141b;padding:12px;border-radius:6px}img{max-width:min(280px,100%);height:auto}
            table{border-collapse:collapse;width:100%;margin:12px 0}td,th{text-align:left;padding:8px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted)}
            .scroll{overflow-x:auto}details{margin-top:12px}summary{cursor:pointer;color:var(--blue)}
            </style></head><body><header><h1>MFE QA Report</h1>
            """);
        html.Append($"<p class='muted'>Seed {seed} · {Results.Count}/{planned.Length} cases completed · {(Complete ? "Run finished" : "Run in progress")}</p>");
        html.Append($"<div class='cards'><div class='summary passed'><strong>{passed}</strong>Passed</div><div class='summary warning'><strong>{warnings}</strong>Warnings</div><div class='summary failed'><strong>{failedCount}</strong>Failed</div><div class='summary blocked'><strong>{blocked}</strong>Blocked</div></div>");
        html.Append($"<p class='warning'>Performance: {Results.Sum(item => item.PerformanceWarnings.Count)} timing warnings across {Results.Count(item => item.PerformanceWarnings.Count > 0)} cases, including functional failures.</p>");
        html.Append($"<p><a href='{H(Path.GetFileName(ResultsPath))}'>Full JSON evidence and stage metrics</a></p><p class='muted'>Timing limits: Load and Randomisation 1,500 ms; file transfer 5,000 ms. Render and PNG readback are excluded. Limit breaches appear as warnings after evidence is collected.</p></header>");

        html.Append("<article><h2>Results by utility</h2><div class='scroll'><table><thead><tr><th>Utility</th><th>Passed</th><th>Warnings</th><th>Failed</th><th>Blocked</th></tr></thead><tbody>");
        foreach (var group in Results.GroupBy(item => item.Group))
            html.Append($"<tr><td>{H(QaDisplay.Utility(group.Key))}</td><td>{group.Count(item => item.Status == "passed")}</td><td>{group.Count(item => item.Status == "warning")}</td><td>{group.Count(item => item.Status == "failed")}</td><td>{group.Count(item => item.Status == "blocked")}</td></tr>");
        html.Append("</tbody></table></div></article>");

        var attention = Results.Where(item => item.Status is "warning" or "failed" or "blocked").ToArray();
        if (attention.Length > 0)
        {
            html.Append("<article><h2>Needs attention</h2>");
            foreach (var result in attention)
                html.Append($"<p class='{H(result.Status)}'><a class='{H(result.Status)}' href='#{H(result.Id)}'>{H(Label(result.Status))}: {H(QaDisplay.Case(result.Id))}</a></p>");
            html.Append("</article>");
        }

        foreach (var result in Results)
        {
            html.Append($"<article id='{H(result.Id)}'><h2 class='{H(result.Status)}'>{H(Label(result.Status))} · {H(QaDisplay.Case(result.Id))}</h2><p class='muted'>{result.DurationMs / 1000:F2} s</p>");
            foreach (var value in result.Evidence.Values.OfType<QaRollSummary>())
                html.Append($"<p><strong>Randomisation:</strong> {value.Successful}/{value.Attempts} successful · average {value.AverageMs:F1} ms · {(value.Failures.Count == 0 ? "No failures" : $"{value.Attempts - value.Successful} failed rolls")}</p>");
            foreach (var warning in result.Warnings) html.Append($"<p class='callout'><strong>Warning:</strong> {H(warning)}</p>");
            foreach (var warning in result.PerformanceWarnings) html.Append($"<p class='callout'><strong>Performance warning:</strong> {H(warning)}</p>");
            foreach (var note in result.Notes) html.Append($"<p class='diagnostic'><strong>Diagnostic:</strong> {H(note)}</p>");
            html.Append($"<details><summary>Evidence and asset donors</summary><pre>{H(JsonSerializer.Serialize(result.Evidence, JsonOptions))}</pre></details>");
            if (result.Stages.Count > 0)
            {
                html.Append("<div class='scroll'><table><thead><tr><th>Stage</th><th>Time</th><th>Limit</th><th>Managed allocation</th><th>Working set</th></tr></thead><tbody>");
                foreach (var stage in result.Stages)
                    html.Append($"<tr class='{(stage.ExceededLimit ? "warning" : "")}'><td>{H(stage.Name)}</td><td>{stage.DurationMs:F1} ms{(stage.SampleCount > 1 ? $" average ({stage.SampleCount} rolls)" : "")}</td><td>{(stage.LimitMs is { } limit ? $"{limit:F0} ms" : "Excluded")}</td><td>{stage.ManagedAllocatedBytes / 1048576.0:F2} MiB{(stage.SampleCount > 1 ? " total" : "")}</td><td>{stage.WorkingSetBytes / 1048576.0:F1} MiB</td></tr>");
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
