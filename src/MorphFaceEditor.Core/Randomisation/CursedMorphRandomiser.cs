using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Randomisation;

public sealed record CursedMorphRandomisationProposal(
    int RandomSeed,
    IReadOnlyDictionary<string, float> FeatureValues,
    IReadOnlyList<BoneTranslation> BoneValues,
    IReadOnlyDictionary<string, float> ScalarValues,
    IReadOnlyDictionary<string, Vector4> VectorValues);

public sealed record CursedMorphRandomisationExtras(
    IReadOnlyList<BoneTranslation> BoneValues,
    IReadOnlyDictionary<string, float> ScalarValues,
    IReadOnlyDictionary<string, Vector4> VectorValues);

/// <summary>
/// Produces deliberately extreme but finite face values using the ranges and
/// facial-bone filtering from ME Randomizer's shared morph randomisers.
/// </summary>
public static class CursedMorphRandomiser
{
    public static CursedMorphRandomisationProposal CreateProposal(
        IReadOnlyDictionary<string, float> currentFeatures,
        IReadOnlyList<BoneTranslation> currentBones,
        IReadOnlyDictionary<string, float> currentScalars,
        IReadOnlyDictionary<string, Vector4> currentVectors,
        int strengthPercent,
        int randomSeed)
    {
        var features = CreateFeatureValues(currentFeatures, strengthPercent, randomSeed);
        var extras = CreateExtrasProposal(
            currentBones, currentScalars, currentVectors, strengthPercent, randomSeed);
        return new CursedMorphRandomisationProposal(
            randomSeed, features, extras.BoneValues, extras.ScalarValues, extras.VectorValues);
    }

    public static IReadOnlyDictionary<string, float> CreateFeatureValues(
        IReadOnlyDictionary<string, float> currentFeatures,
        int strengthPercent,
        int randomSeed)
    {
        ArgumentNullException.ThrowIfNull(currentFeatures);
        ValidateStrength(strengthPercent);
        ValidateFinite(currentFeatures, nameof(currentFeatures));

        var random = CreateRandom(randomSeed, 0xA0761D6478BD642FUL);
        var strength = strengthPercent / 100f;
        var directionGroups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var features = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, current) in currentFeatures.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
        {
            var (minimum, maximum) = MorphRange(name, directionGroups, random);
            var target = random.NextFloat(minimum, maximum);
            features[name] = Lerp(current, target, strength);
        }
        return features;
    }

    public static CursedMorphRandomisationExtras CreateExtrasProposal(
        IReadOnlyList<BoneTranslation> currentBones,
        IReadOnlyDictionary<string, float> currentScalars,
        IReadOnlyDictionary<string, Vector4> currentVectors,
        int strengthPercent,
        int randomSeed)
    {
        ArgumentNullException.ThrowIfNull(currentBones);
        ArgumentNullException.ThrowIfNull(currentScalars);
        ArgumentNullException.ThrowIfNull(currentVectors);
        ValidateStrength(strengthPercent);
        ValidateFinite(currentScalars, nameof(currentScalars));
        if (currentBones.Any(value => string.IsNullOrWhiteSpace(value.BoneName) || !IsFinite(value.Translation)) ||
            currentBones.Select(value => value.BoneName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != currentBones.Count)
        {
            throw new ArgumentException("Bone values must have unique names and finite translations.", nameof(currentBones));
        }
        if (currentVectors.Any(value => string.IsNullOrWhiteSpace(value.Key) || !IsFinite(value.Value)))
        {
            throw new ArgumentException("Material vectors must have names and finite components.", nameof(currentVectors));
        }

        var random = CreateRandom(randomSeed, 0xE7037ED1A0B428DBUL);
        var strength = strengthPercent / 100f;
        var bones = currentBones.Select(value => IsFacialBone(value.BoneName)
                ? value with { Translation = Multiply(value.Translation, strength, random) }
                : value)
            .ToArray();
        var scalars = currentScalars.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value.Key, value => Multiply(value.Value, strength, random),
                StringComparer.OrdinalIgnoreCase);
        var vectors = currentVectors.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value.Key, value => Multiply(value.Value, strength, random),
                StringComparer.OrdinalIgnoreCase);

        return new CursedMorphRandomisationExtras(bones, scalars, vectors);
    }

    private static StableRandom CreateRandom(int seed, ulong domain) =>
        new(unchecked((ulong)(uint)seed) ^ domain);

    private static void ValidateStrength(int strengthPercent)
    {
        if (strengthPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(strengthPercent));
        }
    }

    private static (float Minimum, float Maximum) MorphRange(
        string name,
        Dictionary<string, int> directions,
        StableRandom random)
    {
        if (name.Contains("eye", StringComparison.OrdinalIgnoreCase))
        {
            _ = Direction("eye", 3, directions, random);
            return (-1, 5);
        }
        if (name.Contains("jaw", StringComparison.OrdinalIgnoreCase))
        {
            return Direction("jaw", 3, directions, random) == 0 ? (-5, 20) : (-1, 20);
        }
        if (name.Contains("mouth", StringComparison.OrdinalIgnoreCase))
        {
            return Direction("mouth", 3, directions, random) == 0 ? (-5, 20) : (-5, 5);
        }
        if (name.Contains("nose", StringComparison.OrdinalIgnoreCase))
        {
            return Direction("nose", 2, directions, random) == 0 ? (-15, 15) : (-2, 10);
        }
        return (-7, 7);
    }

    private static int Direction(
        string group,
        int choices,
        Dictionary<string, int> directions,
        StableRandom random)
    {
        if (!directions.TryGetValue(group, out var direction))
        {
            direction = random.NextIndex(choices);
            directions[group] = direction;
        }
        return group.Equals("nose", StringComparison.OrdinalIgnoreCase) && direction == 1 ? 2 : direction;
    }

    private static bool IsFacialBone(string name) =>
        name.Contains("eye", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("sneer", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("nose", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("brow", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("jaw", StringComparison.OrdinalIgnoreCase);

    private static float Multiply(float value, float strength, StableRandom random) =>
        ToFiniteFloat((double)value * random.NextFloat(1 - (strength / 2), 1 + strength));

    private static Vector3 Multiply(Vector3 value, float strength, StableRandom random) => new(
        Multiply(value.X, strength, random),
        Multiply(value.Y, strength, random),
        Multiply(value.Z, strength, random));

    private static Vector4 Multiply(Vector4 value, float strength, StableRandom random) => new(
        Multiply(value.X, strength, random),
        Multiply(value.Y, strength, random),
        Multiply(value.Z, strength, random),
        Multiply(value.W, strength, random));

    private static float Lerp(float from, float to, float amount) =>
        ToFiniteFloat(from + (((double)to - from) * amount));
    private static float ToFiniteFloat(double value) => value switch
    {
        > float.MaxValue => float.MaxValue,
        < -float.MaxValue => -float.MaxValue,
        _ => (float)value
    };
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static void ValidateFinite(IReadOnlyDictionary<string, float> values, string parameterName)
    {
        if (values.Any(value => string.IsNullOrWhiteSpace(value.Key) || !float.IsFinite(value.Value)))
        {
            throw new ArgumentException("Values must have names and finite numbers.", parameterName);
        }
    }

    private sealed class StableRandom(ulong seed)
    {
        private ulong _state = seed;

        public int NextIndex(int count) => (int)(NextUnitDouble() * count);
        public float NextFloat(float minimum, float maximum) =>
            minimum + ((maximum - minimum) * (float)NextUnitDouble());
        private double NextUnitDouble() => (NextUInt64() >> 11) * (1d / 9007199254740992d);

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
