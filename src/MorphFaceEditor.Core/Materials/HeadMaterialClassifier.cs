namespace MorphFaceEditor.Core.Materials;

/// <summary>
/// Classifies head-material identities without depending on package or rendering types.
/// The package inventory and preview both use this policy so safety decisions cannot drift.
/// </summary>
public static class HeadMaterialClassifier
{
    public static HeadMaterialFamily Classify(string? materialName, bool attachment = false)
    {
        var name = materialName ?? string.Empty;
        if (ContainsAny(name,
                "_ARM_", "_CTH_", "_HGR_", "_BDY_", "Body", "Armor", "Armour", "Clothing",
                "_hat_", "helmet", "visor", "beanie", "_cap_", "headgear"))
        {
            return HeadMaterialFamily.Accessory;
        }
        if (ContainsAny(name, "ALN_HED", "ALN_EYE", "Vorcha"))
        {
            return ContainsAny(name, "eye_", "_eye")
                ? HeadMaterialFamily.VorchaEyes
                : HeadMaterialFamily.VorchaSkin;
        }
        if (ContainsAny(name, "BAT_HED", "Batarian"))
        {
            return HeadMaterialFamily.BatarianSkin;
        }
        if (ContainsAny(name, "KRO_HED", "KRO_EYE", "Krogan"))
        {
            return ContainsAny(name, "eye_", "_eye")
                ? HeadMaterialFamily.KroganEyes
                : HeadMaterialFamily.KroganSkin;
        }
        if (ContainsAny(name, "TUR_HED", "TUR_EYE", "TUF_HED", "TUF_EYE", "Turian"))
        {
            return ContainsAny(name, "eye_", "_eye")
                ? HeadMaterialFamily.TurianEyes
                : HeadMaterialFamily.TurianSkin;
        }
        if (ContainsAny(name, "ASA_HED", "Asari"))
        {
            return ContainsAny(name, "eye_") ? HeadMaterialFamily.Eyes
                : ContainsAny(name, "lash") ? HeadMaterialFamily.Lashes
                : HeadMaterialFamily.AsariSkin;
        }
        if (ContainsAny(name, "SAL_HED", "Salarian"))
        {
            return ContainsAny(name, "eye_", "_eye")
                ? HeadMaterialFamily.SalarianEyes
                : HeadMaterialFamily.SalarianSkin;
        }
        if (ContainsAny(name, "lash", "eyelash"))
        {
            return HeadMaterialFamily.Lashes;
        }
        if (ContainsAny(name, "eye_", "_eye", "eyes"))
        {
            return HeadMaterialFamily.Eyes;
        }
        if (ContainsAny(name, "teeth", "mouth", "gum"))
        {
            return HeadMaterialFamily.Teeth;
        }
        if (ContainsAny(name, "PROShort01", "PROShort_01"))
        {
            return HeadMaterialFamily.MaskedHair;
        }
        if (ContainsAny(name, "hair", "_hir_"))
        {
            return HeadMaterialFamily.Hair;
        }
        if (ContainsAny(name, "scalp"))
        {
            return HeadMaterialFamily.Scalp;
        }
        if (ContainsAny(name, "face", "head", "_hed_"))
        {
            return HeadMaterialFamily.Skin;
        }
        return attachment ? HeadMaterialFamily.Accessory : HeadMaterialFamily.Unknown;
    }

    /// <summary>
    /// Classifies a complete MIC-to-master chain. Conflicting identities are unsafe rather
    /// than being resolved from whichever export happens to be visited first.
    /// </summary>
    public static HeadMaterialFamily ClassifyChain(IEnumerable<string> identities, bool attachment = false)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var families = identities
            .Select(identity => Classify(identity, attachment))
            .Where(family => family != HeadMaterialFamily.Unknown)
            .Distinct()
            .ToArray();
        if (families.Contains(HeadMaterialFamily.Accessory))
        {
            return HeadMaterialFamily.Accessory;
        }
        if (families.Length == 0)
        {
            return attachment ? HeadMaterialFamily.Accessory : HeadMaterialFamily.Unknown;
        }

        var concrete = families.Where(family => !IsGenericFamily(family)).Distinct().ToArray();
        if (concrete.Length > 1)
        {
            return HeadMaterialFamily.Unknown;
        }
        if (concrete.Length == 1)
        {
            return families.Where(IsGenericFamily).All(generic => IsCompatibleGeneric(concrete[0], generic))
                ? concrete[0]
                : HeadMaterialFamily.Unknown;
        }
        return families.Length == 1 ? families[0] : HeadMaterialFamily.Unknown;
    }

    public static bool IsAssignableHeadFamily(HeadMaterialFamily family) => family is
        HeadMaterialFamily.Skin or
        HeadMaterialFamily.AsariSkin or
        HeadMaterialFamily.SalarianSkin or
        HeadMaterialFamily.SalarianEyes or
        HeadMaterialFamily.TurianSkin or
        HeadMaterialFamily.TurianEyes or
        HeadMaterialFamily.BatarianSkin or
        HeadMaterialFamily.KroganSkin or
        HeadMaterialFamily.KroganEyes or
        HeadMaterialFamily.VorchaSkin or
        HeadMaterialFamily.VorchaEyes or
        HeadMaterialFamily.Scalp or
        HeadMaterialFamily.Eyes or
        HeadMaterialFamily.Teeth or
        HeadMaterialFamily.Lashes or
        HeadMaterialFamily.Hair or
        HeadMaterialFamily.MaskedHair;

    private static bool IsGenericFamily(HeadMaterialFamily family) => family is
        HeadMaterialFamily.Skin or HeadMaterialFamily.Scalp or HeadMaterialFamily.Eyes or
        HeadMaterialFamily.Teeth or HeadMaterialFamily.Lashes or HeadMaterialFamily.Hair or
        HeadMaterialFamily.MaskedHair;

    private static bool IsCompatibleGeneric(HeadMaterialFamily concrete, HeadMaterialFamily generic) => concrete switch
    {
        HeadMaterialFamily.SalarianEyes or HeadMaterialFamily.TurianEyes or
            HeadMaterialFamily.KroganEyes or HeadMaterialFamily.VorchaEyes => generic == HeadMaterialFamily.Eyes,
        _ => generic == HeadMaterialFamily.Skin
    };

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
