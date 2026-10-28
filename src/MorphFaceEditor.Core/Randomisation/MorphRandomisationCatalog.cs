using MorphFaceEditor.Core.Deformation;

namespace MorphFaceEditor.Core.Randomisation;

/// <summary>Loads the embedded donor resource once and routes editor profiles into core sampling.</summary>
public sealed class MorphRandomisationCatalog
{
    private const string ResourceName =
        "MorphFaceEditor.Core.Assets.Randomisation.GlobalMorphs.mfr.br";

    public MorphRandomisationCatalog(MorphRandomisationCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        Corpus = corpus;
    }

    public static MorphRandomisationCatalog Empty { get; } = new(
        new MorphRandomisationCorpus(
            MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>()));

    public MorphRandomisationCorpus Corpus { get; }

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
            return Corpus.Pools.TryGetValue(key, out var donors) &&
                   donors.Any(donor => IsMorphDonor(key, donor));
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
        int randomSeed)
    {
        var pool = MorphRandomisationPoolRouter.Resolve(profileKey);
        var donor = MorphRandomiser.SelectDonor(Corpus, pool, randomSeed,
            donor => IsMorphDonor(pool, donor));
        if (!profileKey.EndsWith("-female-turian", StringComparison.OrdinalIgnoreCase)) return donor;

        // Shared TUR seeds retain their identity and zero pattern. Only the
        // female set's authored channels participate; material selection uses
        // the separate, unchanged female material population.
        var names = MorphFaceProfileFeatures.FemaleTurianTargetNames;
        return donor with
        {
            AvailableFeatures = donor.AvailableFeatures.Where(names.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            NonZeroValues = donor.NonZeroValues.Where(row => names.Contains(row.Key))
                .ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool IsMorphDonor(MorphRandomisationPoolKey pool, MorphRandomisationDonor donor) =>
        donor.AvailableFeatures.Count > 0 &&
        (pool != MorphRandomisationPoolKey.Turian ||
         !donor.SourceProfileKey.EndsWith("-female-turian", StringComparison.OrdinalIgnoreCase));

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
        // Reuse checks only within this projection; later rolls must see changed sources.
        var availability = new Dictionary<(string Parameter, string Path), bool>();
        bool CanResolve(string parameter, string path)
        {
            var key = (parameter, path);
            if (!availability.TryGetValue(key, out var available))
                availability.Add(key, available = canResolveParameterPath(parameter, path));
            return available;
        }
        return CompatibleMaterialDonors(profileKey)
            .Select(donor => donor with
            {
                MaterialTextureFamilies = donor.MaterialTextureFamilies
                    .Select(family => new
                    {
                        family.Key,
                        Available = family.Value
                            .Where(member => CanResolve(member.Key, member.Value))
                            .ToDictionary(member => member.Key, member => member.Value,
                                StringComparer.OrdinalIgnoreCase),
                        Required = family.Value
                            .Where(member => MaterialTextureFamilyPolicy.IsRequiredMember(family.Key, member.Key))
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

    public MaterialRandomisationProfile? GetMaterialProfile(string profileKey) =>
        ResolveMaterialProfileKey(profileKey) is { } key ? Corpus.MaterialProfiles.GetValueOrDefault(key) : null;

    private string? ResolveMaterialProfileKey(string profileKey)
    {
        // BAF uses BAT's complete material population, including the LE1 fallback.
        profileKey = profileKey.Replace("-female-batarian", "-batarian", StringComparison.OrdinalIgnoreCase);
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

}
