using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>Maps the active morph profile to non-exclusive texture discovery and ranking signals.</summary>
public static class TextureCatalogProfiles
{
    public static TextureCatalogProfile For(MorphFaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Key switch
        {
            var key when key.EndsWith("human-male", StringComparison.OrdinalIgnoreCase) =>
                new TextureCatalogProfile(key, ["HMM_HED", "HMN_HED"], ["HMM_EYE", "HED_EYE"]),
            var key when key.EndsWith("human-female", StringComparison.OrdinalIgnoreCase) =>
                new TextureCatalogProfile(key, ["HMF_HED"], ["HMF_EYE", "HED_EYE"]),
            var key when key.EndsWith("asari", StringComparison.OrdinalIgnoreCase) =>
                new TextureCatalogProfile(key, ["ASA_HED"], ["ASA_EYE"]),
            var key when key.EndsWith("salarian", StringComparison.OrdinalIgnoreCase) =>
                new TextureCatalogProfile(key, ["SAL_HED"], ["SAL_EYE"]),
            var key when key.EndsWith("female-turian", StringComparison.OrdinalIgnoreCase) =>
                new TextureCatalogProfile(key, ["TUF_HED"], ["TUF_EYE", "TUR_EYE", "HED_EYE"]),
            var key when key.EndsWith("turian", StringComparison.OrdinalIgnoreCase) =>
                new TextureCatalogProfile(key, ["TUR_HED"], ["TUR_EYE"]),
            var key when key.EndsWith("krogan", StringComparison.OrdinalIgnoreCase) =>
                new TextureCatalogProfile(key, ["KRO_HED"], ["KRO_EYE"]),
            var key when key.EndsWith("batarian", StringComparison.OrdinalIgnoreCase) =>
                new TextureCatalogProfile(key, ["BAT_HED"], ["BAT_EYE"]),
            var key when key.EndsWith("vorcha", StringComparison.OrdinalIgnoreCase) =>
                new TextureCatalogProfile(key, ["ALN_HED", "TUR_HED_Diff"], ["ALN_EYE", "Eye_Norm"]),
            _ => TextureCatalogProfile.Empty
        };
    }
}
