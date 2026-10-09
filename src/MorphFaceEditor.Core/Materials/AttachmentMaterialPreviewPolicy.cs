namespace MorphFaceEditor.Core.Materials;

/// <summary>Reviewed game-specific attachment roots with dedicated preview shaders.</summary>
public static class AttachmentMaterialPreviewPolicy
{
    public static bool TryGetModel(string game, string canonicalPath, out HumanMaterialPreviewModel model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);

        var gameName = game.Trim();
        var path = canonicalPath.Trim();
        model = (gameName.ToUpperInvariant(), path.ToUpperInvariant()) switch
        {
            ("LE1" or "LE2" or "LE3", "BIOG_HUMANOID_MASTER_MTR_R.HUMAN.HMN_HAT_MASTER_MAT") =>
                HumanMaterialPreviewModel.Hat,
            ("LE1" or "LE2", "BIOG_HUMANOID_MASTER_MTR_R.HUMAN.HMF_VSR_MASTER_MAT") =>
                HumanMaterialPreviewModel.Visor,
            ("LE1" or "LE2", "BIOG_HMM_HIR_PRO_R.CAP.HMM_HAT_CAP_DECAL_MASTER_MAT") =>
                HumanMaterialPreviewModel.CapDecal,
            ("LE3", "BIOG_HMM_HIR_PRO_R.HAT_CAP.HMM_HAT_CAP_DECAL_MASTER_MAT") =>
                HumanMaterialPreviewModel.CapDecal,
            ("LE2", "BIOG_HMM_HIR_PRO_R.CAP.HMM_HAT_CAP_MAT_2A") =>
                HumanMaterialPreviewModel.CapLe2,
            ("LE3", "BIOG_HMM_ARM_AND_R.MASTER.ANDERSONB_MASTER_MAT_USER") =>
                HumanMaterialPreviewModel.AndersonHat,
            ("LE3", "BIOG_HMM_HIR_PRO_R.HAT_MOTO.HMM_HAT_MTR_MASTER") =>
                HumanMaterialPreviewModel.MotoHat,
            ("LE1" or "LE2", "BIOG_HMM_HIR_PRO_R.VISOR.HMM_HAT_VSR_MAT_VISOR") =>
                HumanMaterialPreviewModel.VisorLens,
            _ => HumanMaterialPreviewModel.Default
        };
        return model != HumanMaterialPreviewModel.Default;
    }
}
