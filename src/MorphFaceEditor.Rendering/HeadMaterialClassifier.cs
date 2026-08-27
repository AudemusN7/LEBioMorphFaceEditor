using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Rendering;

public static class HeadMaterialClassifier
{
    public static HeadMaterialFamily Classify(string? materialName, bool attachment = false)
    {
        var name = materialName ?? string.Empty;
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
        if (ContainsAny(name, "TUR_HED", "TUR_EYE", "Turian"))
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
        if (attachment && ContainsAny(name, "_hat_", "helmet", "visor", "beanie", "_cap_"))
        {
            return HeadMaterialFamily.Accessory;
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

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
