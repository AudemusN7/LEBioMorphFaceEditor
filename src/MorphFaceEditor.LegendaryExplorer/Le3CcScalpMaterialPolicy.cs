namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Exact reviewed LE3 Player CC scalp MIC identities shared by selection and save.</summary>
public static class Le3CcScalpMaterialPolicy
{
    public const string MaleDonorPath =
        "BIOG_HMM_HED_PROMORPH.AVERAGE.HMM_HED_PRO_SCALP_MAT_CC";
    public const string FemaleDonorPath =
        "BIOG_HMF_HED_PROMORPH_R.AVERAGE.HMF_HED_PRO_SCALP_MAT_CC";
    public const string MasterMaterialPath =
        "BIOG_HUMANOID_MASTER_MTR_R.HUMAN.HMM_HED_PRO_MASTER_SCALP_MAT_CC";

    public static string CanonicalDonorPath(bool female) => female ? FemaleDonorPath : MaleDonorPath;

    public static bool IsCanonicalDonorPath(string path) =>
        path.Equals(MaleDonorPath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(FemaleDonorPath, StringComparison.OrdinalIgnoreCase);
}
