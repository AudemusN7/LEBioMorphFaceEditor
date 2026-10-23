using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MorphFaceEditor.QA;

/// <summary>Frozen, accepted timing evidence. A judged run never updates its own limits.</summary>
internal static class QaTimingBaseline
{
    private const string ResourceName = "MorphFaceEditor.QA.TimingBaselines.accepted-0.10.0.39.json";
    private const string AuditPrefix = "PCC read/import audit ";
    private static readonly Regex ArtifactPrefix = new(@"\b[0-9a-fA-F]{12,32}-\d{4,}-", RegexOptions.CultureInvariant);
    private static readonly Regex GuidName = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant);
    private static readonly Regex WorkspaceGuid = new(@"(?<=\.)[0-9a-fA-F]{32}(?=\.)", RegexOptions.CultureInvariant);
    private static readonly Regex BatchAverage = new(@" · \d+ rolls · average$", RegexOptions.CultureInvariant);
    private static readonly Lazy<Baseline> Accepted = new(ReadAccepted);

    internal static string Description
    {
        get
        {
            var source = Accepted.Value.Document.Source;
            return $"Frozen accepted QA baseline {source.Build}, seed {source.Seed}, SHA256 {source.Sha256}; " +
                   "25% headroom for fixed/native inputs, 50% for seeded variable/derived inputs; " +
                   "at least 100 ms absolute headroom, rounded upward to 100 ms. " +
                   "Severe failure above 2x the fixed-input cap or 3x the variable-input cap. " +
                   "Unknown stages retain global limits with a 3x severe threshold; " +
                   "ColdLoad, Render and Diagnostic remain excluded. No calibration from the run being judged.";
        }
    }

    internal static void Apply(QaResult result, IReadOnlyList<QaResult>? allResults = null)
    {
        var keys = KeysFor(result, allResults);
        for (var index = 0; index < result.Stages.Count; index++)
        {
            var stage = result.Stages[index];
            var limit = QaPerformance.Limit(stage.Kind);
            var failureLimit = limit * Accepted.Value.Document.Policy.VariableFailureMultiplier;
            var source = limit is null ? "Excluded" : "Global kind policy";
            if (limit is not null && Accepted.Value.Entries.TryGetValue(keys[index], out var entry))
            {
                limit = CalculateLimit(entry, stage.Kind, Accepted.Value.Document.Policy);
                failureLimit = limit * (entry.MarginPercent == Accepted.Value.Document.Policy.FixedMarginPercent
                    ? Accepted.Value.Document.Policy.FixedFailureMultiplier
                    : Accepted.Value.Document.Policy.VariableFailureMultiplier);
                source = $"Accepted {Accepted.Value.Document.Source.Build} baseline (+{entry.MarginPercent}%)";
            }
            result.Stages[index] = stage with
            {
                LimitMs = limit, FailureLimitMs = failureLimit, TimingKey = keys[index], LimitSource = source
            };
        }
    }

    /// <summary>Stable keys in stage order, independent of selected face names and display sequence.</summary>
    internal static IReadOnlyList<string> KeysFor(QaResult result, IReadOnlyList<QaResult>? allResults = null)
    {
        var origins = ArtifactOrigins(allResults is null ? [result] : [.. allResults, result]);
        var loadOccurrences = new Dictionary<(string Phase, QaStageKind Kind), int>();
        var customWorkspaceReady = false;
        var keys = new List<string>(result.Stages.Count);
        foreach (var stage in result.Stages)
        {
            var raw = Purpose(stage.Name, stage.Phase);
            var purpose = NormalizeFilename(raw);
            if (raw.StartsWith(AuditPrefix, StringComparison.Ordinal))
            {
                var filename = raw[AuditPrefix.Length..];
                if (origins.TryGetValue(filename, out var origin))
                {
                    purpose = $"PCC read/import audit · {origin.CaseId} · {origin.Role}";
                    if (origin.RoleOccurrence > 1)
                        purpose += $" · artifact-role-occurrence-{origin.RoleOccurrence}";
                }
                else
                {
                    purpose = $"PCC read/import audit · unlinked · {NormalizeFilename(filename)}";
                }
            }
            if (purpose.StartsWith("Load · ", StringComparison.Ordinal))
            {
                var occurrenceKey = (stage.Phase, stage.Kind);
                var occurrence = loadOccurrences.GetValueOrDefault(occurrenceKey) + 1;
                loadOccurrences[occurrenceKey] = occurrence;
                purpose = $"Load · <face> · load-occurrence-{occurrence}";
            }
            var batch = stage.TotalDurationMs is not null ||
                        stage.Kind == QaStageKind.Randomisation && raw.EndsWith(" · average", StringComparison.Ordinal);
            if (batch) purpose = BatchAverage.Replace(purpose, " · batch-average-per-roll");
            if (purpose.StartsWith("Custom workspace construction", StringComparison.Ordinal))
                customWorkspaceReady = true;
            if (purpose == "Editor asset catalogue")
                purpose += customWorkspaceReady ? " · detached-custom" : " · package-catalogue";
            var statistic = batch ? "batch-average-per-roll" : "single-stage-duration";
            keys.Add($"{result.Id} | {stage.Phase} | {purpose} | {stage.Kind} | {statistic}");
        }
        return keys;
    }

    private static string Purpose(string name, string phase)
    {
        var parts = name.Split(" · ", 3, StringSplitOptions.None);
        return parts.Length == 3 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
               parts[1] == phase ? parts[2] : name;
    }

    private static string NormalizeFilename(string value) =>
        WorkspaceGuid.Replace(GuidName.Replace(ArtifactPrefix.Replace(value, ""), "<guid>"), "<guid>");

    private static Dictionary<string, ArtifactOrigin> ArtifactOrigins(IReadOnlyList<QaResult> results)
    {
        var origins = new Dictionary<string, ArtifactOrigin>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var path in result.Artifacts)
            {
                var filename = Path.GetFileName(path);
                var role = NormalizeFilename(filename);
                var occurrence = occurrences.GetValueOrDefault(role) + 1;
                occurrences[role] = occurrence;
                origins[filename] = new(result.Id, role, occurrence);
            }
        }
        return origins;
    }

    private static double CalculateLimit(BaselineEntry entry, QaStageKind kind, MarginPolicy policy)
    {
        var floor = kind == QaStageKind.FileTransfer ? policy.FileTransferFloorMs : policy.LoadRandomisationFloorMs;
        var value = Math.Max(Math.Max(entry.EnvelopeMs * (1 + entry.MarginPercent / 100d),
            entry.EnvelopeMs + policy.AbsoluteHeadroomMs), floor);
        return Math.Ceiling(value / policy.RoundingMs) * policy.RoundingMs;
    }

    private static Baseline ReadAccepted()
    {
        using var stream = typeof(QaTimingBaseline).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidDataException($"Missing accepted QA timing baseline '{ResourceName}'.");
        var document = JsonSerializer.Deserialize<BaselineDocument>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                       ?? throw new InvalidDataException("The accepted QA timing baseline is empty.");
        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported QA timing baseline schema {document.SchemaVersion}.");
        var entries = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            if (!double.IsFinite(entry.EnvelopeMs) || entry.EnvelopeMs < 0 || entry.MarginPercent is not (25 or 50) ||
                !entries.TryAdd(entry.Key, entry))
                throw new InvalidDataException($"Invalid or duplicate accepted QA timing key '{entry.Key}'.");
        }
        return new(document, entries);
    }

    private sealed record ArtifactOrigin(string CaseId, string Role, int RoleOccurrence);
    private sealed record Baseline(BaselineDocument Document, IReadOnlyDictionary<string, BaselineEntry> Entries);
    private sealed record BaselineDocument(int SchemaVersion, BaselineSource Source, MarginPolicy Policy, IReadOnlyList<BaselineEntry> Entries);
    private sealed record BaselineSource(string Build, int Seed, string Sha256, string Report);
    private sealed record MarginPolicy(int FixedMarginPercent, int VariableMarginPercent, double AbsoluteHeadroomMs,
        double LoadRandomisationFloorMs, double FileTransferFloorMs, double RoundingMs,
        double FixedFailureMultiplier, double VariableFailureMultiplier);
    private sealed record BaselineEntry(string Key, double EnvelopeMs, int MarginPercent);
}
