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
    /// Classifies a MIC-to-master chain from its effective root. Child MIC names are useful
    /// labels, but the final recognized parent determines which compiled material family runs.
    /// Unknown intermediate nodes do not turn a valid hair chain into an accessory.
    /// </summary>
    public static HeadMaterialFamily ClassifyChain(IEnumerable<string> identities, bool attachment = false)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var recognizedRoot = identities
            .Reverse()
            .Select(identity => (Identity: identity, Family: Classify(identity)))
            .FirstOrDefault(value => value.Family != HeadMaterialFamily.Unknown);
        return recognizedRoot.Identity is not null
            ? recognizedRoot.Family
            : attachment ? HeadMaterialFamily.Accessory : HeadMaterialFamily.Unknown;
    }

    public static string? EffectiveRootIdentity(IEnumerable<string> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return identities.Reverse().FirstOrDefault(identity => Classify(identity) != HeadMaterialFamily.Unknown);
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

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
