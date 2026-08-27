namespace MorphFaceEditor.Core.Randomisation;

/// <summary>Creates bounded feature proposals from one real donor without mutating editor state.</summary>
public static class MorphRandomiser
{
    public static MorphRandomisationDonor SelectDonor(
        MorphRandomisationCorpus corpus,
        MorphRandomisationPoolKey poolKey,
        int randomSeed,
        Func<MorphRandomisationDonor, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        if (corpus.FormatVersion != MorphRandomisationCorpus.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported morph-randomisation corpus format {corpus.FormatVersion}.");
        }
        if (!corpus.Pools.TryGetValue(poolKey, out var pool))
        {
            throw new InvalidOperationException($"Randomisation pool '{poolKey}' has no eligible donors.");
        }
        var donors = predicate is null ? pool : pool.Where(predicate).ToArray();
        if (donors.Count == 0)
        {
            throw new InvalidOperationException($"Randomisation pool '{poolKey}' has no compatible donors.");
        }
        var random = new StableRandom(unchecked((ulong)(uint)randomSeed));
        return donors[random.NextIndex(donors.Count)];
    }

    public static MorphRandomisationProposal CreateProposal(
        MorphRandomisationCorpus corpus,
        MorphRandomisationPoolKey poolKey,
        IReadOnlyDictionary<string, float> currentValues,
        IReadOnlyList<MorphRandomisationFeatureBounds> featureBounds,
        IReadOnlySet<string> scope,
        int strengthPercent,
        int randomSeed)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(currentValues);
        ArgumentNullException.ThrowIfNull(featureBounds);
        ArgumentNullException.ThrowIfNull(scope);
        if (strengthPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(strengthPercent));
        }
        var boundsByName = new Dictionary<string, MorphRandomisationFeatureBounds>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var bounds in featureBounds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bounds.Name);
            if (!float.IsFinite(bounds.Minimum) || !float.IsFinite(bounds.Maximum) ||
                bounds.Minimum > bounds.Maximum)
            {
                throw new ArgumentException(
                    $"Feature '{bounds.Name}' has invalid randomisation bounds.", nameof(featureBounds));
            }
            if (!boundsByName.TryAdd(bounds.Name, bounds))
            {
                throw new ArgumentException(
                    $"Feature bounds contain duplicate name '{bounds.Name}'.", nameof(featureBounds));
            }
        }

        foreach (var (name, value) in currentValues)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!float.IsFinite(value))
            {
                throw new ArgumentException($"Current feature '{name}' is not finite.", nameof(currentValues));
            }
        }

        var donor = SelectDonor(corpus, poolKey, randomSeed);
        return CreateProposal(donor, currentValues, featureBounds, scope, strengthPercent, randomSeed);
    }

    public static MorphRandomisationProposal CreateProposal(
        MorphRandomisationDonor donor,
        IReadOnlyDictionary<string, float> currentValues,
        IReadOnlyList<MorphRandomisationFeatureBounds> featureBounds,
        IReadOnlySet<string> scope,
        int strengthPercent,
        int randomSeed)
    {
        ArgumentNullException.ThrowIfNull(donor);
        ArgumentNullException.ThrowIfNull(currentValues);
        ArgumentNullException.ThrowIfNull(featureBounds);
        ArgumentNullException.ThrowIfNull(scope);
        if (strengthPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(strengthPercent));
        }
        var boundsByName = new Dictionary<string, MorphRandomisationFeatureBounds>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var bounds in featureBounds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bounds.Name);
            if (!float.IsFinite(bounds.Minimum) || !float.IsFinite(bounds.Maximum) ||
                bounds.Minimum > bounds.Maximum)
            {
                throw new ArgumentException(
                    $"Feature '{bounds.Name}' has invalid randomisation bounds.", nameof(featureBounds));
            }
            if (!boundsByName.TryAdd(bounds.Name, bounds))
            {
                throw new ArgumentException(
                    $"Feature bounds contain duplicate name '{bounds.Name}'.", nameof(featureBounds));
            }
        }
        foreach (var (name, value) in currentValues)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!float.IsFinite(value))
            {
                throw new ArgumentException($"Current feature '{name}' is not finite.", nameof(currentValues));
            }
        }

        var random = new StableRandom(unchecked((ulong)(uint)randomSeed) ^ 0xD1B54A32D192ED03UL);
        var values = new Dictionary<string, float>(currentValues, StringComparer.OrdinalIgnoreCase);
        var strength = strengthPercent / 100d;
        foreach (var name in scope.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (!values.ContainsKey(name))
            {
                continue;
            }
            if (!boundsByName.TryGetValue(name, out var bounds))
            {
                throw new ArgumentException(
                    $"Feature '{name}' has no randomisation bounds.", nameof(featureBounds));
            }

            if (!donor.NonZeroValues.TryGetValue(name, out var donorValue) || donorValue == 0)
            {
                values[name] = 0;
                continue;
            }
            if (!float.IsFinite(donorValue))
            {
                throw new InvalidDataException(
                    $"Donor '{donor.Id}' feature '{name}' is not finite.");
            }

            // Interpolating both endpoints keeps 0% exact and exposes the complete
            // normal range at 100% without ever waking a donor-zero feature.
            var lower = donorValue + ((bounds.Minimum - donorValue) * strength);
            var upper = donorValue + ((bounds.Maximum - donorValue) * strength);
            if (lower > upper)
            {
                (lower, upper) = (upper, lower);
            }
            var sampled = lower + ((upper - lower) * random.NextUnitDouble());
            values[name] = Math.Clamp((float)sampled, bounds.Minimum, bounds.Maximum);
        }

        return new MorphRandomisationProposal(donor.Id, randomSeed, values);
    }

    /// <summary>Small stable generator so a logged seed remains reproducible across framework updates.</summary>
    internal sealed class StableRandom(ulong seed)
    {
        private ulong _state = seed;

        public int NextIndex(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            return (int)(NextUnitDouble() * count);
        }

        public double NextUnitDouble() => (NextUInt64() >> 11) * (1d / 9007199254740992d);

        private ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
