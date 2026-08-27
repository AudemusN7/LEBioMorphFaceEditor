using System.Numerics;

namespace MorphFaceEditor.Core.Randomisation;

/// <summary>Creates corpus-bounded scalar, perceptual-colour, selector and texture-family proposals.</summary>
public static class MaterialRandomiser
{
    public static MaterialRandomisationProposal CreateProposal(
        MorphRandomisationDonor donor,
        IReadOnlyList<MorphRandomisationDonor> compatibleDonors,
        MaterialRandomisationProfile profile,
        IReadOnlyDictionary<string, float> currentScalars,
        IReadOnlyDictionary<string, Vector4> currentVectors,
        IReadOnlyList<MaterialRandomisationScalarBounds> scalarBounds,
        IReadOnlySet<string> scalarScope,
        IReadOnlySet<string> vectorScope,
        IReadOnlySet<string> textureFamilyScope,
        int strengthPercent,
        int randomSeed,
        IReadOnlyDictionary<string, string>? currentTextures = null)
    {
        ArgumentNullException.ThrowIfNull(donor);
        ArgumentNullException.ThrowIfNull(compatibleDonors);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(currentScalars);
        ArgumentNullException.ThrowIfNull(currentVectors);
        ArgumentNullException.ThrowIfNull(scalarBounds);
        ArgumentNullException.ThrowIfNull(scalarScope);
        ArgumentNullException.ThrowIfNull(vectorScope);
        ArgumentNullException.ThrowIfNull(textureFamilyScope);
        if (strengthPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(strengthPercent));
        }

        var bounds = scalarBounds.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        if (bounds.Values.Any(value => !float.IsFinite(value.Minimum) || !float.IsFinite(value.Maximum) ||
                                       value.Minimum > value.Maximum))
        {
            throw new ArgumentException("Material scalar bounds must be finite and ordered.", nameof(scalarBounds));
        }

        var random = new MorphRandomiser.StableRandom(
            unchecked((ulong)(uint)randomSeed) ^ 0x8CB92BA72F3D8DD7UL);
        var scalars = new Dictionary<string, float>(currentScalars, StringComparer.OrdinalIgnoreCase);
        foreach (var name in scalarScope.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (IsExcludedScalar(profile.ProfileKey, name)) continue;
            if (!scalars.ContainsKey(name) || !donor.MaterialScalars.TryGetValue(name, out var seedValue) ||
                !profile.Scalars.TryGetValue(name, out var statistics) || !bounds.TryGetValue(name, out var limit))
            {
                continue;
            }
            if (IsSeedExactScalar(profile.ProfileKey, name))
            {
                scalars[name] = seedValue;
                continue;
            }
            if (IsSafetyConstrainedScalar(profile.ProfileKey, name))
            {
                limit = new MaterialRandomisationScalarBounds(name, statistics.P10, statistics.P90);
            }
            scalars[name] = SampleScalar(seedValue, statistics, limit, strengthPercent, random);
        }

        var textures = SelectTextureFamilies(
            donor, compatibleDonors, profile.ProfileKey, textureFamilyScope, strengthPercent, random);
        var vectors = new Dictionary<string, Vector4>(currentVectors, StringComparer.OrdinalIgnoreCase);
        foreach (var name in vectorScope.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (IsExcludedVector(profile.ProfileKey, name)) continue;
            if (!vectors.ContainsKey(name) || !donor.MaterialVectors.TryGetValue(name, out var seedValue) ||
                !profile.Vectors.TryGetValue(name, out var statistics))
            {
                continue;
            }
            vectors[name] = statistics.Kind == MaterialVectorRandomisationKind.Selector
                ? SampleSelector(seedValue, statistics.SelectorStates, strengthPercent, random)
                : SampleColour(seedValue, FindColourTarget(name, seedValue, compatibleDonors, random),
                    statistics, strengthPercent, random);
        }
        ApplyTextureDependencies(profile.ProfileKey, textures, currentTextures, vectors, random);

        return new MaterialRandomisationProposal(donor.Id, randomSeed, scalars, vectors, textures);
    }

    public static IReadOnlySet<string> DependentVectorNames(
        string profileKey,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> textureFamilies,
        IReadOnlyDictionary<string, string>? currentTextures = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profileKey.Contains("human-male", StringComparison.OrdinalIgnoreCase) &&
            ContainsTexture(textureFamilies, currentTextures, "HED_Mask", "Mask3"))
        {
            result.Add("HED_Mask_Vector");
        }
        if (profileKey.Contains("turian", StringComparison.OrdinalIgnoreCase) &&
            ContainsTexture(textureFamilies, currentTextures, "TUR_HED_Addn", "GBL_ARM_ALL_Norm"))
        {
            result.Add("TUR_HED_Addn_Mask_Vector");
        }
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SelectTextureFamilies(
        MorphRandomisationDonor donor,
        IReadOnlyList<MorphRandomisationDonor> compatibleDonors,
        string profileKey,
        IReadOnlySet<string> scope,
        int strength,
        MorphRandomiser.StableRandom random)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in scope.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = compatibleDonors
                .Select(value => value.MaterialTextureFamilies.GetValueOrDefault(family))
                .Where(value => value is not null && !IsBlacklistedTextureFamily(profileKey, family, value))
                .Cast<IReadOnlyDictionary<string, string>>()
                .GroupBy(TextureSignature, StringComparer.OrdinalIgnoreCase)
                .Select(value => value.First())
                .OrderBy(TextureSignature, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 0) continue;

            IReadOnlyDictionary<string, string>? selected = null;
            if (strength == 0 && donor.MaterialTextureFamilies.TryGetValue(family, out var seedFamily) &&
                !IsBlacklistedTextureFamily(profileKey, family, seedFamily))
            {
                selected = seedFamily;
            }
            else if (!ShouldBalanceTextureFamily(family) &&
                     donor.MaterialTextureFamilies.TryGetValue(family, out seedFamily) &&
                     !IsBlacklistedTextureFamily(profileKey, family, seedFamily))
            {
                selected = seedFamily;
            }
            selected ??= candidates[random.NextIndex(candidates.Length)];
            result[family] = selected;
        }
        return result;
    }

    private static bool ShouldBalanceTextureFamily(string family) =>
        family.StartsWith("addition:", StringComparison.OrdinalIgnoreCase) ||
        family.StartsWith("tattoo:", StringComparison.OrdinalIgnoreCase) ||
        family.Equals("human-face-mask", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlacklistedTextureFamily(
        string profileKey,
        string family,
        IReadOnlyDictionary<string, string> values)
    {
        if (profileKey.Contains("human-female", StringComparison.OrdinalIgnoreCase) &&
            family.Equals("human-scalp", StringComparison.OrdinalIgnoreCase) &&
            values.Values.Any(value => value.Contains("Ashley", StringComparison.OrdinalIgnoreCase))) return true;
        if (profileKey.Contains("human-male", StringComparison.OrdinalIgnoreCase) &&
            (family.Equals("human-face", StringComparison.OrdinalIgnoreCase) ||
             family.Equals("human-scalp", StringComparison.OrdinalIgnoreCase)) &&
            values.Values.Any(value => value.Contains("Joker", StringComparison.OrdinalIgnoreCase))) return true;
        return profileKey.Contains("asari", StringComparison.OrdinalIgnoreCase) &&
               family.StartsWith("addition:", StringComparison.OrdinalIgnoreCase) &&
               values.Values.Any(value => value.Contains("GBL_ARM_ALL_Norm", StringComparison.OrdinalIgnoreCase));
    }

    private static string TextureSignature(IReadOnlyDictionary<string, string> values) =>
        string.Join("|", values.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .Select(value => $"{value.Key}={value.Value}"));

    private static void ApplyTextureDependencies(
        string profileKey,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> textures,
        IReadOnlyDictionary<string, string>? currentTextures,
        IDictionary<string, Vector4> vectors,
        MorphRandomiser.StableRandom random)
    {
        var dependencies = DependentVectorNames(profileKey, textures, currentTextures);
        if (dependencies.Contains("TUR_HED_Addn_Mask_Vector") && vectors.ContainsKey("TUR_HED_Addn_Mask_Vector"))
        {
            vectors["TUR_HED_Addn_Mask_Vector"] = new Vector4(0, 0, 0, 1);
        }
        if (dependencies.Contains("HED_Mask_Vector") && vectors.ContainsKey("HED_Mask_Vector"))
        {
            Vector4[] validMask3States =
            [
                new(0, 0, 0, 1),
                new(0, 0, 1, 1),
                new(1, 1, 0, 1),
                new(1, 1, 1, 1)
            ];
            vectors["HED_Mask_Vector"] = validMask3States[random.NextIndex(validMask3States.Length)];
        }
    }

    private static bool ContainsTexture(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> textureFamilies,
        IReadOnlyDictionary<string, string>? currentTextures,
        string parameterName,
        string pathFragment)
    {
        var proposed = textureFamilies.Values.SelectMany(value => value)
            .FirstOrDefault(value => value.Key.Equals(parameterName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(proposed.Key))
        {
            return proposed.Value.Contains(pathFragment, StringComparison.OrdinalIgnoreCase);
        }
        return currentTextures?.TryGetValue(parameterName, out var current) == true &&
               current.Contains(pathFragment, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedScalar(string profileKey, string name)
    {
        if (name.Equals("Mask", StringComparison.OrdinalIgnoreCase) &&
            (profileKey.Contains("human-female", StringComparison.OrdinalIgnoreCase) ||
             profileKey.Contains("asari", StringComparison.OrdinalIgnoreCase) ||
             profileKey.Contains("turian", StringComparison.OrdinalIgnoreCase))) return true;
        if (profileKey.Contains("krogan", StringComparison.OrdinalIgnoreCase) &&
            name.Equals("Wrex_Spec_Scalar", StringComparison.OrdinalIgnoreCase)) return true;
        return IsHumanOrAsari(profileKey) &&
               (name.Equals("Emis_Scalar", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("HED_EYE_FX_Scalar", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExcludedVector(string profileKey, string name) =>
        IsHumanOrAsari(profileKey) &&
        (name.Equals("Emis_Color", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("HED_EYE_FX_Vector", StringComparison.OrdinalIgnoreCase));

    private static bool IsSeedExactScalar(string profileKey, string name) =>
        profileKey.Contains("human-male", StringComparison.OrdinalIgnoreCase) &&
        name.StartsWith("HED_Addn_", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafetyConstrainedScalar(string profileKey, string name)
    {
        if (IsHumanOrAsari(profileKey) &&
            name is "U_Offset" or "V_Offset" or "X_Tile" or "Y_Tile" or "Sclera_Darken") return true;
        if (profileKey.Contains("salarian", StringComparison.OrdinalIgnoreCase) &&
            name is "SAL_HED_EYE_Emis" or "SAL_HED_Spec_Scalar") return true;
        return profileKey.Contains("turian", StringComparison.OrdinalIgnoreCase) &&
               name is "TUR_HED_Spwr_Skin_Scalar" or "TUR_HED_Spwr_Bone_Scalar" or
                   "TUR_EYE_Lens_SPwr_Scalar";
    }

    private static bool IsHumanOrAsari(string profileKey) =>
        profileKey.Contains("human-", StringComparison.OrdinalIgnoreCase) ||
        profileKey.Contains("asari", StringComparison.OrdinalIgnoreCase);

    private static float SampleScalar(
        float seed,
        MaterialScalarStatistics statistics,
        MaterialRandomisationScalarBounds bounds,
        int strength,
        MorphRandomiser.StableRandom random)
    {
        var amount = strength / 100f;
        var safeMinimum = seed + ((statistics.P10 - seed) * Math.Min(amount * 2, 1));
        var safeMaximum = seed + ((statistics.P90 - seed) * Math.Min(amount * 2, 1));
        var experimental = Math.Max(0, (amount - 0.5f) * 2);
        var minimum = safeMinimum + ((bounds.Minimum - safeMinimum) * experimental);
        var maximum = safeMaximum + ((bounds.Maximum - safeMaximum) * experimental);
        if (minimum > maximum) (minimum, maximum) = (maximum, minimum);
        return minimum + ((maximum - minimum) * (float)random.NextUnitDouble());
    }

    private static Vector4 FindColourTarget(
        string name,
        Vector4 seed,
        IReadOnlyList<MorphRandomisationDonor> donors,
        MorphRandomiser.StableRandom random)
    {
        var candidates = donors.Where(value => value.MaterialVectors.ContainsKey(name)).ToArray();
        return candidates.Length == 0
            ? seed
            : candidates[random.NextIndex(candidates.Length)].MaterialVectors[name];
    }

    private static Vector4 SampleColour(
        Vector4 seed,
        Vector4 target,
        MaterialVectorStatistics statistics,
        int strength,
        MorphRandomiser.StableRandom random)
    {
        if (strength == 0) return seed;
        var safeAmount = Math.Min(strength / 50f, 1) * (float)random.NextUnitDouble();
        var from = LinearRgbToOklab(seed);
        var to = LinearRgbToOklab(target);
        var lab = Vector3.Lerp(from, to, safeAmount);
        if (strength > 50)
        {
            var expansion = ((strength - 50) / 50f) * (float)random.NextUnitDouble() * 0.5f;
            lab += (to - from) * expansion;
        }
        var rgb = OklabToLinearRgb(lab);
        var alphaAmount = strength / 100f * (float)random.NextUnitDouble();
        var alpha = seed.W + ((target.W - seed.W) * alphaAmount);
        return Clamp(new Vector4(rgb, alpha), statistics.Minimum, statistics.Maximum, strength);
    }

    private static Vector4 SampleSelector(
        Vector4 seed,
        IReadOnlyList<MaterialSelectorState> states,
        int strength,
        MorphRandomiser.StableRandom random)
    {
        if (strength == 0 || states.Count == 0 || random.NextUnitDouble() > strength / 100d)
        {
            return seed;
        }
        var candidates = states.Where(value => value.Value != seed).ToArray();
        if (candidates.Length == 0) return seed;
        var flatten = strength / 100d;
        var weights = candidates.Select(value => ((1 - flatten) * value.Count) + flatten).ToArray();
        var roll = random.NextUnitDouble() * weights.Sum();
        for (var index = 0; index < candidates.Length; index++)
        {
            roll -= weights[index];
            if (roll <= 0) return candidates[index].Value;
        }
        return candidates[^1].Value;
    }

    private static Vector4 Clamp(Vector4 value, Vector4 observedMinimum, Vector4 observedMaximum, int strength)
    {
        var expansion = strength <= 50 ? 0 : (strength - 50) / 50f * 0.25f;
        static float Component(float value, float minimum, float maximum, float expansion)
        {
            var span = Math.Max(maximum - minimum, 0.001f);
            return Math.Clamp(value, minimum - (span * expansion), maximum + (span * expansion));
        }
        return new Vector4(
            Component(value.X, observedMinimum.X, observedMaximum.X, expansion),
            Component(value.Y, observedMinimum.Y, observedMaximum.Y, expansion),
            Component(value.Z, observedMinimum.Z, observedMaximum.Z, expansion),
            Component(value.W, observedMinimum.W, observedMaximum.W, expansion));
    }

    // Values are stored as linear material colours. OKLab makes interpolation perceptually coherent.
    private static Vector3 LinearRgbToOklab(Vector4 value)
    {
        var l = (0.4122214708f * value.X) + (0.5363325363f * value.Y) + (0.0514459929f * value.Z);
        var m = (0.2119034982f * value.X) + (0.6806995451f * value.Y) + (0.1073969566f * value.Z);
        var s = (0.0883024619f * value.X) + (0.2817188376f * value.Y) + (0.6299787005f * value.Z);
        l = MathF.Cbrt(l); m = MathF.Cbrt(m); s = MathF.Cbrt(s);
        return new Vector3(
            (0.2104542553f * l) + (0.793617785f * m) - (0.0040720468f * s),
            (1.9779984951f * l) - (2.428592205f * m) + (0.4505937099f * s),
            (0.0259040371f * l) + (0.7827717662f * m) - (0.808675766f * s));
    }

    private static Vector3 OklabToLinearRgb(Vector3 value)
    {
        var l = value.X + (0.3963377774f * value.Y) + (0.2158037573f * value.Z);
        var m = value.X - (0.1055613458f * value.Y) - (0.0638541728f * value.Z);
        var s = value.X - (0.0894841775f * value.Y) - (1.291485548f * value.Z);
        l *= l * l; m *= m * m; s *= s * s;
        return new Vector3(
            (4.0767416621f * l) - (3.3077115913f * m) + (0.2309699292f * s),
            (-1.2684380046f * l) + (2.6097574011f * m) - (0.3413193965f * s),
            (-0.0041960863f * l) - (0.7034186147f * m) + (1.707614701f * s));
    }
}
