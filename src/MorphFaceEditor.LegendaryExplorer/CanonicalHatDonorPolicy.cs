using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Reviewed official hat identities, independent of the face's Hair/Other storage slot.
/// LE1/2 share paths; LE3 donors can have different geometry and material parents (Moto).
/// Null denotes an absent native destination, never permission to port the source mesh.
/// </summary>
internal static class CanonicalHatDonorPolicy
{
    private static readonly DonorPair[] Donors =
    [
        new("BIOG_HMF_HIR_PRO.Beanie.HMF_HAT_Bn1_MDL", "BIOG_HMF_HIR_PRO.Hat_Beanie.HMF_HAT_Bn1_MDL"),
        new("BIOG_HMF_HIR_PRO.Beanie02.HMF_HAT_Bn2_MDL", "BIOG_HMF_HIR_PRO.Hat_Beanie02.HMF_HAT_Bn2_MDL"),
        new("BIOG_HMF_HIR_PRO.Beret.HMF_HAT_Brt_MDL", "BIOG_HMF_HIR_PRO.Hat_Beret.HMF_HAT_Brt_MDL"),
        new("BIOG_HMF_HIR_PRO.Cap.HMF_HAT_Cap_MDL", "BIOG_HMF_HIR_PRO.Hat_Cap.HMF_HAT_Cap_MDL"),
        new("BIOG_HMF_HIR_PRO.Cosmo.HMF_HAT_Cos_MDL", null),
        new("BIOG_HMF_HIR_PRO.Cuban.HMF_HAT_Cub_MDL", "BIOG_HMF_HIR_PRO.Hat_Cuban.HMF_HAT_Cub_MDL"),
        new("BIOG_HMF_HIR_PRO.Cyclops.HMF_HAT_CYC_MDL", null),
        new("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er1_MDL", null),
        new("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er2_MDL", null),
        new("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er3_MDL", null),
        new("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er4_MDL", null),
        new("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er5_MDL", null),
        new("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er6_MDL", null),
        new("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er7_MDL", null),
        new("BIOG_HMF_HIR_PRO.GlassesFull.HMF_HAT_GLA_MDL", null),
        new("BIOG_HMF_HIR_PRO.GlassesHalf.HMF_HAT_GLH_MDL", null),
        new("BIOG_HMF_HIR_PRO.HairClip.HMF_HAT_Hcp_MDL", "BIOG_HMF_HIR_PRO.Hat_HairClip.HMF_HAT_Hcp_MDL"),
        new("BIOG_HMF_HIR_PRO.Moto.HMF_HAT_MTR_MDL", "BIOG_HMF_HIR_PRO.Hat_Moto.HMF_HAT_MTR_MDL"),
        new("BIOG_HMF_HIR_PRO.MotoGlasses.HMF_HAT_MTG_MDL", null),
        new("BIOG_HMF_HIR_PRO.Officer.HMF_HAT_Off_MDL", "BIOG_HMF_HIR_PRO.Hat_Officer.HMF_HAT_Off_MDL"),
        new("BIOG_HMM_HIR_PRO_R.Beanie01.HMM_HAT_Bn1_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Beanie01.HMM_HAT_Bn1_MDL"),
        new("BIOG_HMM_HIR_PRO_R.Beanie02.HMM_HAT_Bn2_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Beanie02.HMM_HAT_Bn2_MDL"),
        new("BIOG_HMM_HIR_PRO_R.Beret.HMM_HAT_Brt_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Beret.HMM_HAT_Brt_MDL"),
        new("BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Cap.HMM_HAT_Cap_MDL"),
        new("BIOG_HMM_HIR_PRO_R.Cosmo.HMM_HAT_Cos_MDL", null),
        new("BIOG_HMM_HIR_PRO_R.Cuban.HMM_HAT_Cub_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Cuban.HMM_HAT_Cub_MDL"),
        new("BIOG_HMM_HIR_PRO_R.Cyclops.HMM_HAT_CYC_MDL", null),
        new("BIOG_HMM_HIR_PRO_R.GlassesFull.HMM_HAT_Glf_MDL", null),
        new("BIOG_HMM_HIR_PRO_R.GlassesHalf.HMM_HAT_GLH_MDL", null),
        new("BIOG_HMM_HIR_PRO_R.Moto.HMM_HAT_MTR_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Moto.HMM_HAT_MTR_MDL"),
        new("BIOG_HMM_HIR_PRO_R.MotoGlasses.HMM_HAT_MTG_MDL", null),
        new("BIOG_HMM_HIR_PRO_R.Officer.HMM_HAT_Off_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Officer.HMM_HAT_Off_MDL"),
        new("BIOG_HMM_HIR_PRO_R.Visor.HMM_HAT_VSR_MDL", null),
        new(null, "BIOG_HMM_HIR_PRO_R.Hat_Hackett.HMM_HAT_Hac_MDL"),
    ];

    internal static bool TryResolve(
        MEGame sourceGame, MEGame targetGame, string sourcePath, out string? targetPath)
    {
        targetPath = null;
        if (sourceGame == targetGame || !IsLegendary(sourceGame) || !IsLegendary(targetGame))
        {
            return false;
        }
        foreach (var donor in Donors)
        {
            var nativeSource = sourceGame == MEGame.LE3 ? donor.LE3 : donor.LE12;
            if (nativeSource is not null && nativeSource.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                targetPath = targetGame == MEGame.LE3 ? donor.LE3 : donor.LE12;
                return true;
            }
        }
        return false;
    }

    private static bool IsLegendary(MEGame game) => game is MEGame.LE1 or MEGame.LE2 or MEGame.LE3;

    private sealed record DonorPair(string? LE12, string? LE3);
}
