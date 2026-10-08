using MorphFaceEditor.Core.Randomisation;
using System.IO;

namespace MorphFaceEditor.Services;

/// <summary>Loads the embedded donor resource once and routes editor profiles into core sampling.</summary>
public sealed class MorphRandomisationCatalog
{
    private const string ResourceName =
        "MorphFaceEditor.Assets.Randomisation.GlobalMorphs.mfr.br";

    public MorphRandomisationCatalog(MorphRandomisationCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        Corpus = WithHumanHairColours(corpus);
    }

    public static MorphRandomisationCatalog Empty { get; } = new(
        new MorphRandomisationCorpus(
            MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>()));

    public MorphRandomisationCorpus Corpus { get; }

    // The supported hair graphs express the same three colours under different names.
    // Project the existing population at runtime; retain native evidence when it exists.
    private static readonly string[][] HairColourNames =
    [
        ["HED_Hair_Colour_Vector", "Hair_Color"],
        ["Highlight1Color", "Highlight1Colour_Vector", "HED_Aniso_Colour01_Vector", "Aniso_Color_1"],
        ["Highlight2Color", "Highlight2Colour_Vector", "HED_Aniso_Colour02_Vector", "Aniso_Color_2"]
    ];

    private static MorphRandomisationCorpus WithHumanHairColours(MorphRandomisationCorpus corpus)
    {
        static bool IsHuman(string profile) =>
            profile.EndsWith("-human-male", StringComparison.OrdinalIgnoreCase) ||
            profile.EndsWith("-human-female", StringComparison.OrdinalIgnoreCase);

        static IReadOnlyDictionary<string, T> Project<T>(IReadOnlyDictionary<string, T> source,
            Func<string, T, T> rename)
        {
            var result = new Dictionary<string, T>(source, StringComparer.OrdinalIgnoreCase);
            foreach (var aliases in HairColourNames)
            {
                var origin = aliases.FirstOrDefault(source.ContainsKey);
                if (origin is null) continue;
                foreach (var alias in aliases)
                    result.TryAdd(alias, rename(alias, source[origin]));
            }
            return result;
        }

        return corpus with
        {
            MaterialProfiles = corpus.MaterialProfiles.ToDictionary(entry => entry.Key,
                entry => IsHuman(entry.Key)
                    ? entry.Value with { Vectors = Project(entry.Value.Vectors,
                        (name, statistics) => statistics with { Name = name }) }
                    : entry.Value, StringComparer.OrdinalIgnoreCase),
            Pools = corpus.Pools.ToDictionary(entry => entry.Key,
                entry => (IReadOnlyList<MorphRandomisationDonor>)entry.Value.Select(donor =>
                    IsHuman(donor.SourceProfileKey)
                        ? donor with { MaterialVectors = Project(donor.MaterialVectors, (_, value) => value) }
                        : donor).ToArray())
        };
    }

    public static MorphRandomisationCatalog LoadEmbedded()
    {
        using var stream = typeof(MorphRandomisationCatalog).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                $"Embedded morph-randomisation resource '{ResourceName}' is missing.");
        return new MorphRandomisationCatalog(MorphRandomisationBundleSerializer.Read(stream));
    }

    public bool HasDonors(string profileKey)
    {
        try
        {
            var key = MorphRandomisationPoolRouter.Resolve(profileKey);
            return Corpus.Pools.TryGetValue(key, out var donors) && donors.Count > 0;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public bool HasMaterialDonors(string profileKey) =>
        ResolveMaterialProfileKey(profileKey) is not null && CompatibleMaterialDonors(profileKey).Count > 0;

    public MorphRandomisationDonor SelectDonor(
        string profileKey,
        int randomSeed,
        bool requireMaterial)
    {
        var pool = MorphRandomisationPoolRouter.Resolve(profileKey);
        return MorphRandomiser.SelectDonor(Corpus, pool, randomSeed,
            requireMaterial
                ? donor => IsCompatibleMaterialDonorProfile(profileKey, donor.SourceProfileKey) &&
                           donor.HasMaterialEvidence
                : donor => donor.AvailableFeatures.Count > 0);
    }

    public MorphRandomisationDonor SelectMaterialDonor(
        string profileKey,
        int randomSeed,
        string? excludedDonorId = null)
    {
        var pool = MorphRandomisationPoolRouter.Resolve(profileKey);
        bool IsCompatible(MorphRandomisationDonor donor) =>
            IsCompatibleMaterialDonorProfile(profileKey, donor.SourceProfileKey) && donor.HasMaterialEvidence;
        var candidates = Corpus.Pools.GetValueOrDefault(pool)?.Where(IsCompatible).ToArray() ?? [];
        var hasDistinctCandidate = excludedDonorId is not null && candidates.Any(donor =>
            !donor.Id.Equals(excludedDonorId, StringComparison.OrdinalIgnoreCase));
        return MorphRandomiser.SelectDonor(
            Corpus, pool, randomSeed,
            donor => IsCompatible(donor) &&
                     (!hasDistinctCandidate ||
                      !donor.Id.Equals(excludedDonorId, StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyList<MorphRandomisationDonor> CompatibleMaterialDonors(string profileKey)
    {
        try
        {
            var pool = MorphRandomisationPoolRouter.Resolve(profileKey);
            if (ResolveMaterialProfileKey(profileKey) is null) return [];
            return Corpus.Pools.GetValueOrDefault(pool)?
                .Where(value => IsCompatibleMaterialDonorProfile(profileKey, value.SourceProfileKey) &&
                                value.HasMaterialEvidence).ToArray() ?? [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    public IReadOnlyList<MorphRandomisationDonor> ProjectInstalledMaterialDonors(
        string profileKey,
        Func<string, string, bool> canResolveParameterPath)
    {
        ArgumentNullException.ThrowIfNull(canResolveParameterPath);
        return CompatibleMaterialDonors(profileKey)
            .Select(donor => donor with
            {
                MaterialTextureFamilies = donor.MaterialTextureFamilies
                    .Select(family => new
                    {
                        family.Key,
                        Available = family.Value
                            .Where(member => canResolveParameterPath(member.Key, member.Value))
                            .ToDictionary(member => member.Key, member => member.Value,
                                StringComparer.OrdinalIgnoreCase),
                        Required = family.Value
                            .Where(member => IsRequiredTextureMember(family.Key, member.Key))
                            .Select(member => member.Key)
                            .ToArray()
                    })
                    .Where(family => family.Available.Count > 0 &&
                                     family.Required.All(family.Available.ContainsKey))
                    .ToDictionary(family => family.Key,
                        family => (IReadOnlyDictionary<string, string>)family.Available,
                        StringComparer.OrdinalIgnoreCase)
            })
            .ToArray();
    }

    private static bool IsRequiredTextureMember(string family, string parameterName) =>
        family.Equals("human-face", StringComparison.OrdinalIgnoreCase)
            ? parameterName is "HED_Diff" or "HED_Norm"
            : family.Equals("human-scalp", StringComparison.OrdinalIgnoreCase)
                ? parameterName is "HED_Scalp_Diff" or "HED_Scalp_Norm"
                : family.EndsWith("-face", StringComparison.OrdinalIgnoreCase)
                    ? parameterName.EndsWith("_HED_Diff", StringComparison.OrdinalIgnoreCase) ||
                      parameterName.EndsWith("_HED_Norm", StringComparison.OrdinalIgnoreCase)
                : true;

    public MaterialRandomisationProfile? GetMaterialProfile(string profileKey) =>
        ResolveMaterialProfileKey(profileKey) is { } key ? Corpus.MaterialProfiles.GetValueOrDefault(key) : null;

    private string? ResolveMaterialProfileKey(string profileKey)
    {
        if (Corpus.MaterialProfiles.ContainsKey(profileKey)) return profileKey;
        // LE1 contains no stock Batarian donor faces. Their LE1 editor material schema is
        // compatible with the pooled alien system, so LE2 is the nearest audited source.
        if (profileKey.Equals("le1-batarian", StringComparison.OrdinalIgnoreCase))
        {
            if (Corpus.MaterialProfiles.ContainsKey("le2-batarian")) return "le2-batarian";
            if (Corpus.MaterialProfiles.ContainsKey("le3-batarian")) return "le3-batarian";
        }
        return null;
    }

    private static bool IsCompatibleMaterialDonorProfile(string targetProfileKey, string donorProfileKey)
    {
        var target = targetProfileKey.ToLowerInvariant();
        var donor = donorProfileKey.ToLowerInvariant();
        if (target.EndsWith("-vorcha", StringComparison.Ordinal))
        {
            // The head schema is shared, but LE2 uses ALN_EYE_MASTER_MAT while
            // LE3 uses TUR_HED_EYE_MASTER_MAT. Keep their atomic eye families
            // and numeric eye parameters inside the matching game profile.
            return donor == target;
        }
        if (target.EndsWith("-female-turian", StringComparison.Ordinal))
        {
            // TUF shares the Turian material population, but uses TUF-equivalent
            // texture objects for that population.
            return donor.EndsWith("-female-turian", StringComparison.Ordinal);
        }
        if (target.EndsWith("-turian", StringComparison.Ordinal))
        {
            return !donor.EndsWith("-female-turian", StringComparison.Ordinal);
        }
        if (!target.Contains("human-", StringComparison.Ordinal))
        {
            // Alien pools are already species-isolated and their material schemas are intentionally
            // shared across the trilogy. Missing per-game parameters are filtered at proposal time.
            return true;
        }
        if (target.StartsWith("le3-", StringComparison.Ordinal)) return donor == target;
        var targetSex = target[(target.IndexOf("human-", StringComparison.Ordinal))..];
        var donorHuman = donor.IndexOf("human-", StringComparison.Ordinal);
        return donorHuman >= 0 && donor[donorHuman..] == targetSex &&
               (donor.StartsWith("le1-", StringComparison.Ordinal) ||
                donor.StartsWith("le2-", StringComparison.Ordinal));
    }

    public IReadOnlySet<string> EligibleTextureParameters(string profileKey) =>
        CompatibleMaterialDonors(profileKey)
            .SelectMany(value => value.MaterialTextureFamilies.Values)
            .SelectMany(value => value.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public MorphRandomisationProposal CreateProposal(
        string profileKey,
        IReadOnlyDictionary<string, float> currentValues,
        IReadOnlyList<MorphRandomisationFeatureBounds> bounds,
        IReadOnlySet<string> scope,
        int strengthPercent,
        int randomSeed) =>
        MorphRandomiser.CreateProposal(
            Corpus,
            MorphRandomisationPoolRouter.Resolve(profileKey),
            currentValues,
            bounds,
            scope,
            strengthPercent,
            randomSeed);

    public MorphRandomisationProposal CreateMorphProposal(
        MorphRandomisationDonor donor,
        IReadOnlyDictionary<string, float> currentValues,
        IReadOnlyList<MorphRandomisationFeatureBounds> bounds,
        IReadOnlySet<string> scope,
        int strengthPercent,
        int randomSeed) => MorphRandomiser.CreateProposal(
        donor, currentValues, bounds, scope, strengthPercent, randomSeed);
}
