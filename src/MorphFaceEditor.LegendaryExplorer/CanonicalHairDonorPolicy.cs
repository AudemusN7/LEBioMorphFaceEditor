using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Exact, reviewed cross-game hair donor routes. Routes are directional; no leaf-name inference
/// or source-mesh fallback is permitted.
/// </summary>
internal static class CanonicalHairDonorPolicy
{
    private static readonly DonorRoute[] Routes =
    [
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.Iconbun.HMF_HIR_IBun_MDL", "BIOG_HMF_HIR_PRO.Hair_Iconbun.HMF_HIR_IBun_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.LowBun.HMF_HIR_Lbn_MDL", "biog_hmf_hir_pro.Hair_LowBun.HMF_HIR_Lbn_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.MidBun.HMF_HIR_Mbn_MDL", "biog_hmf_hir_pro.Hair_MidBun.HMF_HIR_Mbn_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.Mohawk.HMF_HIR_Mhk_MDL", "BIOG_HMF_HIR_PRO.Hair_Mohawk.HMF_HIR_Mhk_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.PonyTail.HMF_HIR_Ptl_MDL", "biog_hmf_hir_pro.Hair_PonyTail.HMF_HIR_Ptl_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomAfro.HMF_HIR_PROCustom_Afr_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomAfro.HMF_HIR_PROCustom_Afr_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomBraids01.HMF_HIR_PROCustomBraids01_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomBraids01.HMF_HIR_PROCustomBraids01_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomCute.HMF_HIR_PROCustom_Cute_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomCute.HMF_HIR_PROCustom_Cute_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomShort01.HMF_HIR_PROCustomShort01_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomShort01.HMF_HIR_PROCustomShort01_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomShort02.HMF_HIR_PROCustomShort02_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomShort02.HMF_HIR_PROCustomShort02_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMF_HIR_PRO.Pulled.HMF_HIR_Pulled_MDL", "BIOG_HMF_HIR_PRO.Hair_Pulled.HMF_HIR_Pulled_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.FauxHawk.HMM_HIR_Fhwk_01_MDL", "BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_01_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.FormalSpikes.HMM_HIR_Fsk_MDL", "BIOG_HMM_HIR_PRO_R.Hair_FormalSpikes.HMM_HIR_Fsk_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.Kaiden.HMM_HIR_Kai_MDL", "BIOG_HMM_HIR_PRO_R.Hair_Kaiden.HMM_HIR_Kai_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.Mohawk.HMM_HIR_Mhk_MDL", "BIOG_HMM_HIR_PRO_R.Hair_Mohawk.HMM_HIR_Mhk_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.SargeSpike.HMM_HIR_Ssk_01_MDL", "BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_01_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.SargeSpike.HMM_HIR_Ssk_02_MDL", "BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_02_MDL"),
        new(MEGame.LE1, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.Short03.HMM_HIR_PROShort03_MDL", "BIOG_HMM_HIR_PRO_R.Hair_Short03.HMM_HIR_PROShort03_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.Iconbun.HMF_HIR_IBun_MDL", "BIOG_HMF_HIR_PRO.Hair_Iconbun.HMF_HIR_IBun_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.Mohawk.HMF_HIR_Mhk_MDL", "BIOG_HMF_HIR_PRO.Hair_Mohawk.HMF_HIR_Mhk_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.PonyTail.HMF_HIR_Ptl_MDL", "biog_hmf_hir_pro.Hair_PonyTail.HMF_HIR_Ptl_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomAfro.HMF_HIR_PROCustom_Afr_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomAfro.HMF_HIR_PROCustom_Afr_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomBraids01.HMF_HIR_PROCustomBraids01_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomBraids01.HMF_HIR_PROCustomBraids01_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomCute.HMF_HIR_PROCustom_Cute_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomCute.HMF_HIR_PROCustom_Cute_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomShort01.HMF_HIR_PROCustomShort01_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomShort01.HMF_HIR_PROCustomShort01_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomShort02.HMF_HIR_PROCustomShort02_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomShort02.HMF_HIR_PROCustomShort02_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL", "BIOG_HMF_HIR_PRO.Hair_PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.Pulled.HMF_HIR_Pulled_MDL", "BIOG_HMF_HIR_PRO.Hair_Pulled.HMF_HIR_Pulled_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.FauxHawk.HMM_HIR_Fhwk_01_MDL", "BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_01_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.FormalSpikes.HMM_HIR_Fsk_MDL", "BIOG_HMM_HIR_PRO_R.Hair_FormalSpikes.HMM_HIR_Fsk_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.Mohawk.HMM_HIR_Mhk_MDL", "BIOG_HMM_HIR_PRO_R.Hair_Mohawk.HMM_HIR_Mhk_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.RollinsSpike.HMM_HIR_Rsk_MDL", "BIOG_HMM_HIR_PRO_R.Hair_RollinsSpike.HMM_HIR_Rsk_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.SargeSpike.HMM_HIR_Ssk_01_MDL", "BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_01_MDL"),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMM_HIR_PRO_R.SargeSpike.HMM_HIR_Ssk_02_MDL", "BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_02_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_Mohawk.HMF_HIR_Mhk_MDL", "BIOG_HMF_HIR_PRO.Mohawk.HMF_HIR_Mhk_MDL"),
        new(MEGame.LE3, MEGame.LE1, "biog_hmf_hir_pro.Hair_PROAshley.HMF_HIR_PROAshley_MDL", "BIOG_HMF_HIR_PRO.Ashley.HMF_HIR_PROAshley_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_PROCustomAfro.HMF_HIR_PROCustom_Afr_MDL", "BIOG_HMF_HIR_PRO.PROCustomAfro.HMF_HIR_PROCustom_Afr_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_PROCustomCute.HMF_HIR_PROCustom_Cute_MDL", "BIOG_HMF_HIR_PRO.PROCustomCute.HMF_HIR_PROCustom_Cute_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_PROCustomSexy.HMF_HIR_PROCustom_Sexy_MDL", "BIOG_HMF_HIR_PRO.PROCustomSexy.HMF_HIR_PROCustom_Sexy_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_PROCustomShort01.HMF_HIR_PROCustomShort01_MDL", "BIOG_HMF_HIR_PRO.PROCustomShort01.HMF_HIR_PROCustomShort01_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_PROCustomShort02.HMF_HIR_PROCustomShort02_MDL", "BIOG_HMF_HIR_PRO.PROCustomShort02.HMF_HIR_PROCustomShort02_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_Pulled.HMF_HIR_Pulled_MDL", "BIOG_HMF_HIR_PRO.Pulled.HMF_HIR_Pulled_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_Short02.HMF_HIR_PROShort_MDL", "BIOG_HMF_HIR_PRO.Short02.HMF_HIR_PROShort_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMM_HIR_PRO_R.Hair_Mohawk.HMM_HIR_Mhk_MDL", "BIOG_HMM_HIR_PRO_R.Mohawk.HMM_HIR_Mhk_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_02_MDL", "BIOG_HMM_HIR_PRO_R.SargeSpike.HMM_HIR_Ssk_02_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_Mohawk.HMF_HIR_Mhk_MDL", "BIOG_HMF_HIR_PRO.Mohawk.HMF_HIR_Mhk_MDL"),
        new(MEGame.LE3, MEGame.LE2, "biog_hmf_hir_pro.Hair_PROAshley.HMF_HIR_PROAshley_MDL", "BIOG_HMF_HIR_PRO.Ashley.HMF_HIR_PROAshley_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_PROCustomAfro.HMF_HIR_PROCustom_Afr_MDL", "BIOG_HMF_HIR_PRO.PROCustomAfro.HMF_HIR_PROCustom_Afr_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_PROCustomCute.HMF_HIR_PROCustom_Cute_MDL", "BIOG_HMF_HIR_PRO.PROCustomCute.HMF_HIR_PROCustom_Cute_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_PROCustomSexy.HMF_HIR_PROCustom_Sexy_MDL", "BIOG_HMF_HIR_PRO.PROCustomSexy.HMF_HIR_PROCustom_Sexy_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_PROCustomShort01.HMF_HIR_PROCustomShort01_MDL", "BIOG_HMF_HIR_PRO.PROCustomShort01.HMF_HIR_PROCustomShort01_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_PROCustomShort02.HMF_HIR_PROCustomShort02_MDL", "BIOG_HMF_HIR_PRO.PROCustomShort02.HMF_HIR_PROCustomShort02_MDL"),
        new(MEGame.LE3, MEGame.LE2, "biog_hmf_hir_pro.Hair_PROMiranda.HMF_HIR_PROMiranda_MDL", "BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_MIR_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_Pulled.HMF_HIR_Pulled_MDL", "BIOG_HMF_HIR_PRO.Pulled.HMF_HIR_Pulled_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_Short02.HMF_HIR_PROShort_MDL", "BIOG_HMF_HIR_PRO.Short02.HMF_HIR_PROShort_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMM_HIR_PRO_R.Hair_Mohawk.HMM_HIR_Mhk_MDL", "BIOG_HMM_HIR_PRO_R.Mohawk.HMM_HIR_Mhk_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_02_MDL", "BIOG_HMM_HIR_PRO_R.SargeSpike.HMM_HIR_Ssk_02_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_PROCustomBraids01.HMF_HIR_PROCustomBraids01_MDL", "BIOG_HMF_HIR_PRO.PROCustomBraids01.HMF_HIR_PROCustomBraids01_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_PROCustomBraids01.HMF_HIR_PROCustomBraids01_MDL", "BIOG_HMF_HIR_PRO.PROCustomBraids01.HMF_HIR_PROCustomBraids01_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL", "BIOG_HMF_HIR_PRO.PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL", "BIOG_HMF_HIR_PRO.PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL"),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMM_HIR_PRO_R.Hair_James.HMM_HIR_PROJames_MDL", null),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMM_HIR_PRO_R.Hair_James.HMM_HIR_PROJames_MDL", null),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMM_HIR_PRO_R.Hair_Kaiden.HMM_HIR_Kai_MDL", "BIOG_HMM_HIR_PRO_R.Kaiden.HMM_HIR_Kai_MDL"),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMM_HIR_PRO_R.Hair_Kaiden.HMM_HIR_Kai_MDL", "BIOG_HMM_HIR_PRO_R.Kaiden.HMM_HIR_Kai_MDL"),
        new(MEGame.LE2, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_MIR_MDL", null),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_MIR_MDL", null),
        new(MEGame.LE2, MEGame.LE1, "BIOG_HMF_HIR_PRO.LowBun.HMF_HIR_Mbu_MDL", null),
        new(MEGame.LE2, MEGame.LE3, "BIOG_HMF_HIR_PRO.LowBun.HMF_HIR_Mbu_MDL", null),
        new(MEGame.LE3, MEGame.LE1, "biog_hmf_hir_pro.Hair_PROEva.HMF_HIR_PROEva_MDL", null),
        new(MEGame.LE3, MEGame.LE2, "biog_hmf_hir_pro.Hair_PROEva.HMF_HIR_PROEva_MDL", null),
        new(MEGame.LE3, MEGame.LE1, "biog_hmf_hir_pro.Hair_PROJack.HMF_HIR_PROJack_MDL", null),
        new(MEGame.LE3, MEGame.LE2, "biog_hmf_hir_pro.Hair_PROJack.HMF_HIR_PROJack_MDL", null),
        new(MEGame.LE3, MEGame.LE1, "biog_hmf_hir_pro.Hair_PROJessica.HMF_HIR_PROJessica_MDL", null),
        new(MEGame.LE3, MEGame.LE2, "biog_hmf_hir_pro.Hair_PROJessica.HMF_HIR_PROJessica_MDL", null),
        new(MEGame.LE3, MEGame.LE1, "biog_hmf_hir_pro.Hair_PROMiranda.HMF_HIR_PROMiranda_MDL", null),
        new(MEGame.LE3, MEGame.LE1, "BIOG_HMF_HIR_PRO.Hair_VI.HMF_HIR_VI_MDL", null),
        new(MEGame.LE3, MEGame.LE2, "BIOG_HMF_HIR_PRO.Hair_VI.HMF_HIR_VI_MDL", null),
    ];

    internal static bool TryResolve(
        MEGame sourceGame, MEGame targetGame, string sourcePath, out string? targetPath)
    {
        targetPath = null;
        foreach (var route in Routes)
        {
            if (route.SourceGame == sourceGame &&
                route.TargetGame == targetGame &&
                route.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                targetPath = route.TargetPath;
                return true;
            }
        }

        return false;
    }

    private sealed record DonorRoute(
        MEGame SourceGame, MEGame TargetGame, string SourcePath, string? TargetPath);
}
