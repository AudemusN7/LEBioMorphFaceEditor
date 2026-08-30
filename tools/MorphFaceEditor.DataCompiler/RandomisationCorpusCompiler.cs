using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Numerics;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.DataCompiler;

public enum RandomisationFeatureState
{
    Unavailable,
    Zero,
    NonZero
}

public sealed record RandomisationProfileDefinition(
    string ProfileKey,
    MorphFaceGame Game,
    MorphRandomisationPoolKey PoolKey,
    Func<string, string?, bool> Matches,
    IReadOnlySet<string> AvailableFeatures,
    IReadOnlyDictionary<string, string> Aliases)
{
    public bool IsMaterialOnly { get; init; }
}

public sealed record RandomisationDonorExclusion(
    MorphFaceGame Game,
    string FacePath,
    string Reason);

public sealed record RandomisationAuditRow(
    MorphRandomisationPoolKey PoolKey,
    MorphFaceGame Game,
    string ProfileKey,
    string PackagePath,
    string FacePath,
    string FeatureName,
    RandomisationFeatureState State,
    float? Value,
    bool DonorEligible,
    string? ExclusionReason);

public sealed record RandomisationAuditFace(
    MorphFaceGame Game,
    string PackagePath,
    string FacePath,
    string? ProfileKey,
    MorphRandomisationPoolKey? PoolKey,
    bool DonorEligible,
    string? ExclusionReason);

public sealed record RandomisationMaterialAuditRow(
    MorphRandomisationPoolKey PoolKey,
    MorphFaceGame Game,
    string ProfileKey,
    string PackagePath,
    string FacePath,
    MaterialParameterKind Kind,
    string ParameterName,
    float? ScalarValue,
    Vector4? VectorValue,
    string? TexturePath,
    string? TextureFamily,
    bool DonorEligible,
    string? EvidenceError);

public sealed record RandomisationCompilation(
    MorphRandomisationCorpus Corpus,
    IReadOnlyList<RandomisationAuditFace> Faces,
    IReadOnlyList<RandomisationAuditRow> Rows,
    IReadOnlyList<RandomisationMaterialAuditRow> MaterialRows,
    string Markdown,
    string Csv);

/// <summary>Classifies raw corpus faces and emits deterministic audit and runtime representations.</summary>
public static class RandomisationCorpusCompiler
{
    public static RandomisationCompilation Compile(
        IReadOnlyList<RawMorphRandomisationFace> rawFaces,
        IReadOnlyList<RandomisationProfileDefinition> profiles,
        IReadOnlyList<RandomisationDonorExclusion> reviewedExclusions)
    {
        ArgumentNullException.ThrowIfNull(rawFaces);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(reviewedExclusions);
        var unionByPool = profiles
            .GroupBy(value => value.PoolKey)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(value => value.AvailableFeatures)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        var donors = Enum.GetValues<MorphRandomisationPoolKey>()
            .ToDictionary(value => value, _ => new List<MorphRandomisationDonor>());
        var faces = new List<RandomisationAuditFace>();
        var rows = new List<RandomisationAuditRow>();
        var materialRows = new List<RandomisationMaterialAuditRow>();

        foreach (var raw in rawFaces.OrderBy(value => value.Game).ThenBy(value => value.FacePath, StringComparer.Ordinal))
        {
            var matches = profiles.Where(profile =>
                    profile.Game == raw.Game && profile.Matches(raw.FacePath, raw.BaseHeadPath))
                .ToArray();
            if (matches.Length != 1)
            {
                var reason = matches.Length == 0
                    ? "No supported morph profile matched the face."
                    : $"The face matched {matches.Length} morph profiles.";
                faces.Add(new RandomisationAuditFace(
                    raw.Game, raw.PackagePath, raw.FacePath, null, null, false, reason));
                continue;
            }

            var profile = matches[0];
            var availableLookup = profile.AvailableFeatures
                .ToDictionary(value => value, value => value, StringComparer.OrdinalIgnoreCase);
            var canonicalValues = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            string? exclusionReason = null;
            foreach (var feature in raw.Features)
            {
                if (string.IsNullOrWhiteSpace(feature.Name) || !float.IsFinite(feature.Offset))
                {
                    exclusionReason = "The face contains a blank feature name or non-finite offset.";
                    break;
                }
                var aliased = profile.Aliases.GetValueOrDefault(feature.Name) ?? feature.Name;
                if (!availableLookup.TryGetValue(aliased, out var canonicalName))
                {
                    continue;
                }
                if (!canonicalValues.TryAdd(canonicalName, feature.Offset))
                {
                    exclusionReason = $"Feature '{canonicalName}' occurs more than once.";
                    break;
                }
            }

            var reviewed = reviewedExclusions.FirstOrDefault(value =>
                value.Game == raw.Game &&
                string.Equals(value.FacePath, raw.FacePath, StringComparison.OrdinalIgnoreCase));
            if (reviewed is not null)
            {
                exclusionReason = reviewed.Reason;
            }
            var materialOnlyProfile = profile.IsMaterialOnly ||
                                      profile.PoolKey == MorphRandomisationPoolKey.Vorcha;
            if (exclusionReason is null && canonicalValues.Values.All(value => value == 0))
            {
                exclusionReason = materialOnlyProfile
                    ? "Morph sliders are intentionally unavailable for this profile; this face is a material donor only."
                    : "The face has no non-zero visible editable morph sliders.";
            }
            var eligible = exclusionReason is null;
            var materialEligible = materialOnlyProfile
                ? string.IsNullOrWhiteSpace(raw.MaterialEvidenceError)
                : eligible;
            var sourceFile = Path.GetFileName(raw.PackagePath);
            faces.Add(new RandomisationAuditFace(
                raw.Game, sourceFile, raw.FacePath, profile.ProfileKey, profile.PoolKey,
                eligible, exclusionReason));

            var nonZero = canonicalValues
                .Where(value => value.Value != 0)
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
            if (eligible || materialEligible)
            {
                var textureFamilies = BuildTextureFamilies(profile.ProfileKey, raw.MaterialTextures);
                donors[profile.PoolKey].Add(new MorphRandomisationDonor(
                    $"{raw.Game}:{raw.FacePath}",
                    profile.ProfileKey,
                    new HashSet<string>(profile.AvailableFeatures, StringComparer.OrdinalIgnoreCase),
                    nonZero)
                {
                    MaterialScalars = new Dictionary<string, float>(raw.MaterialScalars, StringComparer.OrdinalIgnoreCase),
                    MaterialVectors = new Dictionary<string, Vector4>(raw.MaterialVectors, StringComparer.OrdinalIgnoreCase),
                    MaterialTextureFamilies = textureFamilies
                });
            }

            foreach (var value in raw.MaterialScalars.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
            {
                materialRows.Add(new RandomisationMaterialAuditRow(
                    profile.PoolKey, raw.Game, profile.ProfileKey, sourceFile, raw.FacePath,
                    MaterialParameterKind.Scalar, value.Key, value.Value, null, null, null,
                    materialEligible, raw.MaterialEvidenceError));
            }
            foreach (var value in raw.MaterialVectors.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
            {
                materialRows.Add(new RandomisationMaterialAuditRow(
                    profile.PoolKey, raw.Game, profile.ProfileKey, sourceFile, raw.FacePath,
                    MaterialParameterKind.Vector, value.Key, null, value.Value, null, null,
                    materialEligible, raw.MaterialEvidenceError));
            }
            var familyLookup = BuildTextureFamilies(profile.ProfileKey, raw.MaterialTextures)
                .SelectMany(family => family.Value.Keys.Select(parameter => (parameter, family.Key)))
                .ToDictionary(value => value.parameter, value => value.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var value in raw.MaterialTextures.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
            {
                materialRows.Add(new RandomisationMaterialAuditRow(
                    profile.PoolKey, raw.Game, profile.ProfileKey, sourceFile, raw.FacePath,
                    MaterialParameterKind.Texture, value.Key, null, null, value.Value,
                    familyLookup.GetValueOrDefault(value.Key), materialEligible, raw.MaterialEvidenceError));
            }

            foreach (var featureName in unionByPool[profile.PoolKey])
            {
                var available = availableLookup.ContainsKey(featureName);
                var value = available ? canonicalValues.GetValueOrDefault(featureName) : 0;
                var state = !available
                    ? RandomisationFeatureState.Unavailable
                    : value == 0 ? RandomisationFeatureState.Zero : RandomisationFeatureState.NonZero;
                rows.Add(new RandomisationAuditRow(
                    profile.PoolKey, raw.Game, profile.ProfileKey, sourceFile, raw.FacePath,
                    featureName, state, state == RandomisationFeatureState.NonZero ? value : null,
                    eligible, exclusionReason));
            }
        }

        var stablePools = donors.ToDictionary(
            value => value.Key,
            value => (IReadOnlyList<MorphRandomisationDonor>)value.Value
                .OrderBy(donor => donor.Id, StringComparer.Ordinal).ToArray());
        var stableFaces = faces.OrderBy(value => value.Game).ThenBy(value => value.FacePath, StringComparer.Ordinal).ToArray();
        var stableRows = rows.OrderBy(value => value.PoolKey).ThenBy(value => value.Game)
            .ThenBy(value => value.FacePath, StringComparer.Ordinal)
            .ThenBy(value => value.FeatureName, StringComparer.OrdinalIgnoreCase).ToArray();
        var stableMaterialRows = materialRows.OrderBy(value => value.ProfileKey, StringComparer.Ordinal)
            .ThenBy(value => value.FacePath, StringComparer.Ordinal)
            .ThenBy(value => value.Kind)
            .ThenBy(value => value.ParameterName, StringComparer.OrdinalIgnoreCase).ToArray();
        var materialProfiles = BuildMaterialProfiles(stablePools);
        var corpus = new MorphRandomisationCorpus(MorphRandomisationCorpus.CurrentFormatVersion, stablePools)
        {
            MaterialProfiles = materialProfiles
        };
        return new RandomisationCompilation(
            corpus, stableFaces, stableRows, stableMaterialRows,
            BuildMarkdown(rawFaces, stableFaces, stableRows, stableMaterialRows, stablePools, materialProfiles),
            BuildCsv(stableRows, stableMaterialRows));
    }

    private static string BuildMarkdown(
        IReadOnlyList<RawMorphRandomisationFace> rawFaces,
        IReadOnlyList<RandomisationAuditFace> faces,
        IReadOnlyList<RandomisationAuditRow> rows,
        IReadOnlyList<RandomisationMaterialAuditRow> materialRows,
        IReadOnlyDictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>> pools,
        IReadOnlyDictionary<string, MaterialRandomisationProfile> materialProfiles)
    {
        var text = new StringBuilder();
        text.AppendLine("# Morph Randomiser GlobalMorphs Corpus Audit");
        text.AppendLine();
        text.AppendLine($"Faces accounted for: {faces.Count.ToString(CultureInfo.InvariantCulture)}. Eligible donors: {faces.Count(value => value.DonorEligible).ToString(CultureInfo.InvariantCulture)}. Excluded: {faces.Count(value => !value.DonorEligible).ToString(CultureInfo.InvariantCulture)}.");
        text.AppendLine();
        text.AppendLine("## Sources");
        text.AppendLine();
        text.AppendLine("| Game | File | Bytes | SHA-256 |");
        text.AppendLine("|---|---|---:|---|");
        foreach (var source in rawFaces.GroupBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                     .Select(group => (Path: group.Key, Game: group.First().Game))
                     .OrderBy(value => value.Game))
        {
            var file = new FileInfo(source.Path);
            var hash = file.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source.Path))) : "unavailable";
            text.AppendLine($"| {source.Game} | {Path.GetFileName(source.Path)} | {(file.Exists ? file.Length : 0).ToString(CultureInfo.InvariantCulture)} | `{hash}` |");
        }
        text.AppendLine();
        text.AppendLine("## Pools");
        text.AppendLine();
        text.AppendLine("| Pool | Eligible donors |");
        text.AppendLine("|---|---:|");
        foreach (var key in Enum.GetValues<MorphRandomisationPoolKey>())
        {
            text.AppendLine($"| {key} | {pools[key].Count.ToString(CultureInfo.InvariantCulture)} |");
        }
        text.AppendLine();
        text.AppendLine("## Exclusions");
        text.AppendLine();
        foreach (var face in faces.Where(value => !value.DonorEligible))
        {
            text.AppendLine($"- `{face.Game}:{face.FacePath}` — {face.ExclusionReason}");
        }
        text.AppendLine();
        text.AppendLine("## Feature statistics");
        text.AppendLine();
        text.AppendLine("| Pool | Feature | Available | Non-zero | Rate | Min | Max | Mean | Median | StdDev | P10 | P90 |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var group in rows.Where(value => value.DonorEligible)
                     .GroupBy(value => (value.PoolKey, value.FeatureName)))
        {
            var available = group.Where(value => value.State != RandomisationFeatureState.Unavailable).ToArray();
            var values = available.Where(value => value.State == RandomisationFeatureState.NonZero)
                .Select(value => value.Value!.Value).Order().ToArray();
            var mean = values.Length == 0 ? 0 : values.Average();
            var variance = values.Length == 0 ? 0 : values.Select(value => Math.Pow(value - mean, 2)).Average();
            text.AppendLine($"| {group.Key.PoolKey} | {group.Key.FeatureName} | {available.Length} | {values.Length} | {Format(available.Length == 0 ? 0 : (double)values.Length / available.Length)} | {Format(ValueAt(values, 0))} | {Format(ValueAt(values, 1))} | {Format(mean)} | {Format(Percentile(values, 0.5))} | {Format(Math.Sqrt(variance))} | {Format(Percentile(values, 0.1))} | {Format(Percentile(values, 0.9))} |");
        }
        text.AppendLine();
        text.AppendLine("## Strongest slider relationships");
        text.AppendLine();
        text.AppendLine("Pearson correlation includes zero values but only compares faces where both sliders existed. Co-activation is the number of faces where both were non-zero; Jaccard measures overlap between their active masks.");
        text.AppendLine();
        text.AppendLine("| Pool | Feature A | Feature B | Compared faces | Co-active | Active Jaccard | Pearson r |");
        text.AppendLine("|---|---|---|---:|---:|---:|---:|");
        foreach (var relationship in FindRelationships(rows))
        {
            text.AppendLine($"| {relationship.PoolKey} | {relationship.First} | {relationship.Second} | {relationship.SampleCount} | {relationship.CoActive} | {Format(relationship.Jaccard)} | {Format(relationship.Correlation)} |");
        }
        text.AppendLine();
        text.AppendLine("## Material scalar statistics");
        text.AppendLine();
        text.AppendLine("| Profile | Parameter | Samples | Min | P10 | P90 | Max |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var profile in materialProfiles.Values.OrderBy(value => value.ProfileKey, StringComparer.Ordinal))
        {
            foreach (var value in profile.Scalars.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var count = materialRows.Count(row => row.DonorEligible && row.ProfileKey == profile.ProfileKey &&
                    row.Kind == MaterialParameterKind.Scalar && row.ParameterName.Equals(value.Name, StringComparison.OrdinalIgnoreCase));
                text.AppendLine($"| {profile.ProfileKey} | {value.Name} | {count} | {Format(value.Minimum)} | {Format(value.P10)} | {Format(value.P90)} | {Format(value.Maximum)} |");
            }
        }
        text.AppendLine();
        text.AppendLine("## Material vector classifications");
        text.AppendLine();
        text.AppendLine("| Profile | Parameter | Behaviour | Samples | Selector states | Component min | Component max |");
        text.AppendLine("|---|---|---|---:|---:|---|---|");
        foreach (var profile in materialProfiles.Values.OrderBy(value => value.ProfileKey, StringComparer.Ordinal))
        {
            foreach (var value in profile.Vectors.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var count = materialRows.Count(row => row.DonorEligible && row.ProfileKey == profile.ProfileKey &&
                    row.Kind == MaterialParameterKind.Vector && row.ParameterName.Equals(value.Name, StringComparison.OrdinalIgnoreCase));
                text.AppendLine($"| {profile.ProfileKey} | {value.Name} | {value.Kind} | {count} | {value.SelectorStates.Count} | {FormatVector(value.Minimum)} | {FormatVector(value.Maximum)} |");
            }
        }
        text.AppendLine();
        text.AppendLine("## Eligible texture families");
        text.AppendLine();
        text.AppendLine("| Profile | Family | Donors | Variants | Members |");
        text.AppendLine("|---|---|---:|---:|---|");
        foreach (var profile in materialProfiles.Values.OrderBy(value => value.ProfileKey, StringComparer.Ordinal))
        {
            var donors = pools.Values.SelectMany(value => value)
                .Where(value => value.SourceProfileKey.Equals(profile.ProfileKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var family in profile.TextureFamilies.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                var variants = donors.Where(value => value.MaterialTextureFamilies.ContainsKey(family))
                    .Select(value => string.Join(";", value.MaterialTextureFamilies[family]
                        .OrderBy(member => member.Key).Select(member => $"{member.Key}={member.Value}")))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var members = donors.SelectMany(value => value.MaterialTextureFamilies.GetValueOrDefault(family)?.Keys ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray();
                text.AppendLine($"| {profile.ProfileKey} | {family} | {donors.Count(value => value.MaterialTextureFamilies.ContainsKey(family))} | {variants.Length} | {string.Join(", ", members)} |");
            }
        }
        var evidenceFailures = rawFaces.Where(value => value.MaterialEvidenceError is not null).ToArray();
        text.AppendLine();
        text.AppendLine("## Material evidence failures");
        text.AppendLine();
        if (evidenceFailures.Length == 0)
        {
            text.AppendLine("None.");
        }
        else
        {
            foreach (var face in evidenceFailures)
            {
                text.AppendLine($"- `{face.Game}:{face.FacePath}` — {face.MaterialEvidenceError}");
            }
        }
        return text.ToString();
    }

    private sealed record FeatureRelationship(
        MorphRandomisationPoolKey PoolKey,
        string First,
        string Second,
        int SampleCount,
        int CoActive,
        double Jaccard,
        double Correlation);

    private static IReadOnlyList<FeatureRelationship> FindRelationships(
        IReadOnlyList<RandomisationAuditRow> rows)
    {
        var result = new List<FeatureRelationship>();
        foreach (var pool in rows.Where(value => value.DonorEligible).GroupBy(value => value.PoolKey))
        {
            var faces = pool.GroupBy(value => (value.Game, value.FacePath))
                .Select(group => group.ToDictionary(value => value.FeatureName, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var features = pool.Select(value => value.FeatureName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            var relationships = new List<FeatureRelationship>();
            for (var firstIndex = 0; firstIndex < features.Length; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < features.Length; secondIndex++)
                {
                    var first = features[firstIndex];
                    var second = features[secondIndex];
                    var samples = faces.Where(face =>
                            face.TryGetValue(first, out var firstRow) &&
                            face.TryGetValue(second, out var secondRow) &&
                            firstRow.State != RandomisationFeatureState.Unavailable &&
                            secondRow.State != RandomisationFeatureState.Unavailable)
                        .Select(face => (
                            First: face[first].Value ?? 0,
                            Second: face[second].Value ?? 0))
                        .ToArray();
                    if (samples.Length < 5)
                    {
                        continue;
                    }
                    var coActive = samples.Count(value => value.First != 0 && value.Second != 0);
                    if (coActive < 3)
                    {
                        continue;
                    }
                    var eitherActive = samples.Count(value => value.First != 0 || value.Second != 0);
                    var firstMean = samples.Average(value => value.First);
                    var secondMean = samples.Average(value => value.Second);
                    var numerator = samples.Sum(value =>
                        (value.First - firstMean) * (value.Second - secondMean));
                    var firstSquares = samples.Sum(value => Math.Pow(value.First - firstMean, 2));
                    var secondSquares = samples.Sum(value => Math.Pow(value.Second - secondMean, 2));
                    var denominator = Math.Sqrt(firstSquares * secondSquares);
                    if (denominator == 0)
                    {
                        continue;
                    }
                    relationships.Add(new FeatureRelationship(
                        pool.Key, first, second, samples.Length, coActive,
                        eitherActive == 0 ? 0 : (double)coActive / eitherActive,
                        numerator / denominator));
                }
            }
            result.AddRange(relationships.OrderByDescending(value => Math.Abs(value.Correlation))
                .ThenByDescending(value => value.CoActive)
                .ThenBy(value => value.First, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Second, StringComparer.OrdinalIgnoreCase)
                .Take(20));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildTextureFamilies(
        string profileKey,
        IReadOnlyDictionary<string, string> textures)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string family, IEnumerable<KeyValuePair<string, string>> members, params string[] required)
        {
            var values = members.ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
            if (values.Count > 0 && required.All(values.ContainsKey)) result[family] = values;
        }

        foreach (var value in textures.Where(value =>
                     value.Key.Contains("addn", StringComparison.OrdinalIgnoreCase)))
        {
            Add($"addition:{value.Key}", [value]);
        }
        foreach (var value in textures.Where(value =>
                     value.Key.Contains("tatt", StringComparison.OrdinalIgnoreCase)))
        {
            Add($"tattoo:{value.Key}", [value]);
        }
        if (profileKey.Contains("human", StringComparison.OrdinalIgnoreCase))
        {
            Add("human-face", textures.Where(value => value.Key is "HED_Diff" or "HED_Norm"),
                "HED_Diff", "HED_Norm");
            Add("human-eyes", textures.Where(value =>
                value.Key.Contains("EYE", StringComparison.OrdinalIgnoreCase)));
            Add("human-face-mask", textures.Where(value =>
                value.Key.Equals("HED_Mask", StringComparison.OrdinalIgnoreCase)), "HED_Mask");
            var scalp = textures.Where(value =>
                value.Key.Contains("scalp", StringComparison.OrdinalIgnoreCase) ||
                value.Key.Equals("HED_Tang", StringComparison.OrdinalIgnoreCase));
            Add("human-scalp", scalp, "HED_Scalp_Diff", "HED_Scalp_Norm");
        }
        else if (profileKey.EndsWith("-vorcha", StringComparison.OrdinalIgnoreCase))
        {
            Add("vorcha-face", textures.Where(value => value.Key is
                    "TUR_HED_Diff" or "ALN_HED_Norm" or "ALN_HED_Tint"),
                "TUR_HED_Diff", "ALN_HED_Norm");
            Add("vorcha-eyes", textures.Where(value => value.Key is "ALN_HED_Diff" or "Eye_Norm"),
                "ALN_HED_Diff", "Eye_Norm");
            Add("vorcha-eyes", textures.Where(value => value.Key is "EYE_Diff" or "Eye_Norm"),
                "EYE_Diff", "Eye_Norm");
        }
        else if (TryGetSpeciesTexturePrefix(profileKey, out var species, out var prefix))
        {
            Add($"{species}-face", textures.Where(value =>
                    value.Key.StartsWith($"{prefix}_HED_", StringComparison.OrdinalIgnoreCase) &&
                    !value.Key.Contains("EYE", StringComparison.OrdinalIgnoreCase) &&
                    !value.Key.Contains("Addn", StringComparison.OrdinalIgnoreCase) &&
                    !value.Key.Contains("Tatt", StringComparison.OrdinalIgnoreCase)),
                $"{prefix}_HED_Diff", $"{prefix}_HED_Norm");
            Add($"{species}-eyes", textures.Where(value =>
                value.Key.Contains("EYE", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    private static bool TryGetSpeciesTexturePrefix(
        string profileKey,
        out string species,
        out string prefix)
    {
        (species, prefix) = profileKey.ToLowerInvariant() switch
        {
            var key when key.EndsWith("asari", StringComparison.Ordinal) => ("asari", "ASA"),
            var key when key.EndsWith("salarian", StringComparison.Ordinal) => ("salarian", "SAL"),
            var key when key.EndsWith("female-turian", StringComparison.Ordinal) => ("female-turian", "TUR"),
            var key when key.EndsWith("turian", StringComparison.Ordinal) => ("turian", "TUR"),
            var key when key.EndsWith("krogan", StringComparison.Ordinal) => ("krogan", "KRO"),
            var key when key.EndsWith("batarian", StringComparison.Ordinal) => ("batarian", "BAT"),
            _ => (string.Empty, string.Empty)
        };
        return prefix.Length > 0;
    }

    private static IReadOnlyDictionary<string, MaterialRandomisationProfile> BuildMaterialProfiles(
        IReadOnlyDictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>> pools)
    {
        return pools.Values.SelectMany(value => value)
            .GroupBy(value => value.SourceProfileKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var scalars = group.SelectMany(value => value.MaterialScalars)
                    .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(values => values.Key, values =>
                    {
                        var samples = values.Select(value => value.Value).Where(float.IsFinite).Order().ToArray();
                        return new MaterialScalarStatistics(
                            values.Key, samples.FirstOrDefault(), samples.LastOrDefault(),
                            (float)Percentile(samples, 0.1), (float)Percentile(samples, 0.9));
                    }, StringComparer.OrdinalIgnoreCase);
                var vectors = group.SelectMany(value => value.MaterialVectors)
                    .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(values => values.Key, values =>
                    {
                        var samples = values.Select(value => value.Value).Where(IsFinite).ToArray();
                        var kind = InferVectorKind(values.Key, samples);
                        var minimum = ComponentExtrema(samples, Math.Min);
                        var maximum = ComponentExtrema(samples, Math.Max);
                        var states = kind == MaterialVectorRandomisationKind.Selector
                            ? samples.Select(CanonicaliseSelector).GroupBy(value => value)
                                .Select(state => new MaterialSelectorState(state.Key, state.Count()))
                                .OrderByDescending(value => value.Count).ThenBy(value => value.Value.X)
                                .ThenBy(value => value.Value.Y).ThenBy(value => value.Value.Z)
                                .ThenBy(value => value.Value.W).ToArray()
                            : [];
                        return new MaterialVectorStatistics(values.Key, kind, minimum, maximum, states);
                    }, StringComparer.OrdinalIgnoreCase);
                var families = group.SelectMany(value => value.MaterialTextureFamilies.Keys)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new MaterialRandomisationProfile(group.Key, scalars, vectors, families);
            }, StringComparer.OrdinalIgnoreCase);
    }

    private static MaterialVectorRandomisationKind InferVectorKind(string name, IReadOnlyList<Vector4> values)
    {
        if (name.Contains("mask_vector", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_01_Vector", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_02_Vector", StringComparison.OrdinalIgnoreCase))
        {
            return MaterialVectorRandomisationKind.Selector;
        }
        if (name.Contains("colour", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("color", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("tone", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("tint", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("blonde", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("scattering", StringComparison.OrdinalIgnoreCase))
        {
            return MaterialVectorRandomisationKind.PerceptualColour;
        }
        var canonical = values.Count > 0 && values.Count(value => IsCanonical(value)) >= values.Count * 0.8;
        return canonical ? MaterialVectorRandomisationKind.Selector : MaterialVectorRandomisationKind.PerceptualColour;
    }

    private static bool IsCanonical(Vector4 value) =>
        new[] { value.X, value.Y, value.Z }.All(component =>
            Math.Abs(component) <= 0.001f || Math.Abs(component - 1) <= 0.001f);

    private static Vector4 CanonicaliseSelector(Vector4 value) => new(
        Canonical(value.X), Canonical(value.Y), Canonical(value.Z), Canonical(value.W));

    private static float Canonical(float value) => Math.Abs(value) <= 0.001f ? 0 :
        Math.Abs(value - 1) <= 0.001f ? 1 : value;

    private static Vector4 ComponentExtrema(IReadOnlyList<Vector4> values, Func<float, float, float> select) =>
        values.Count == 0 ? Vector4.Zero : values.Skip(1).Aggregate(values[0], (current, value) => new Vector4(
            select(current.X, value.X), select(current.Y, value.Y),
            select(current.Z, value.Z), select(current.W, value.W)));

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static string BuildCsv(
        IReadOnlyList<RandomisationAuditRow> rows,
        IReadOnlyList<RandomisationMaterialAuditRow> materialRows)
    {
        var text = new StringBuilder("RecordType,Pool,Game,Profile,Package,Face,Name,StateOrKind,X,Y,Z,W,TexturePath,TextureFamily,DonorEligible,ExclusionReason\r\n");
        foreach (var row in rows)
        {
            text.AppendJoin(',',
                "Morph", Csv(row.PoolKey.ToString()), Csv(row.Game.ToString()), Csv(row.ProfileKey),
                Csv(row.PackagePath), Csv(row.FacePath), Csv(row.FeatureName), Csv(row.State.ToString()),
                row.Value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                row.DonorEligible ? "true" : "false", Csv(row.ExclusionReason ?? string.Empty));
            text.Append("\r\n");
        }
        foreach (var row in materialRows)
        {
            var vector = row.VectorValue;
            text.AppendJoin(',',
                "Material", Csv(row.PoolKey.ToString()), Csv(row.Game.ToString()), Csv(row.ProfileKey),
                Csv(row.PackagePath), Csv(row.FacePath), Csv(row.ParameterName), Csv(row.Kind.ToString()),
                row.ScalarValue?.ToString("R", CultureInfo.InvariantCulture) ??
                    vector?.X.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                vector?.Y.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                vector?.Z.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                vector?.W.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(row.TexturePath ?? string.Empty), Csv(row.TextureFamily ?? string.Empty),
                row.DonorEligible ? "true" : "false", Csv(row.EvidenceError ?? string.Empty));
            text.Append("\r\n");
        }
        return text.ToString();
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string FormatVector(Vector4 value) =>
        $"({Format(value.X)}, {Format(value.Y)}, {Format(value.Z)}, {Format(value.W)})";
    private static double ValueAt(float[] values, int endpoint) => values.Length == 0 ? 0 : values[endpoint == 0 ? 0 : ^1];
    private static double Percentile(float[] values, double percentile)
    {
        if (values.Length == 0) return 0;
        var position = (values.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return values[lower] + ((values[upper] - values[lower]) * (position - lower));
    }
}
