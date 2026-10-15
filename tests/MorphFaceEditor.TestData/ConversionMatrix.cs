using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.TestData;

// The curated positive D1 corpus matrix is shared by the regression suite and retained-output QA.
internal sealed record ConversionSource(MorphFaceGame SourceGame, string Family, string ObjectName)
{
    public string FacePath => Family + "." + ObjectName;
}

internal static class ConversionMatrix
{
    public const int ExpectedCount = 68;
    public static IEnumerable<MorphFaceGame> TargetGames(MorphFaceGame source, string family) => family == "ALN"
        ? source == MorphFaceGame.LE2 ? [MorphFaceGame.LE3] : source == MorphFaceGame.LE3 ? [MorphFaceGame.LE2] : []
        : new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 }.Where(game => game != source);

    public static IReadOnlyList<ConversionSource> Sources { get; } =
    [
        new(MorphFaceGame.LE1, "HMM", "rp115_admiral"),
        new(MorphFaceGame.LE1, "HMM", "BIOA_PRC2_YoungWarrior"),
        new(MorphFaceGame.LE1, "HMM", "sta20_human_ambassador"),
        new(MorphFaceGame.LE1, "HMF", "sta70_control02"),
        new(MorphFaceGame.LE1, "HMF", "rp105a_emilywong"),
        new(MorphFaceGame.LE1, "HMF", "ice_ercs_guard3"),
        new(MorphFaceGame.LE1, "ASA", "Psyrana"),
        new(MorphFaceGame.LE1, "SAL", "sta60_schells"),
        new(MorphFaceGame.LE1, "TUR", "sta60_assassin02"),
        new(MorphFaceGame.LE1, "KRO", "jug70_docdroyas"),
        new(MorphFaceGame.LE1, "BAT", "prc1_balak"),
        new(MorphFaceGame.LE2, "HMM", "BioFace_Security2"),
        new(MorphFaceGame.LE2, "HMM", "PtyMtl_Partner"),
        new(MorphFaceGame.LE2, "HMM", "zyavtl_vido"),
        new(MorphFaceGame.LE2, "HMF", "cithub_csec_officer_b"),
        new(MorphFaceGame.LE2, "HMF", "BioFace_HMF_Guest04"),
        new(MorphFaceGame.LE2, "HMF", "zyavtl_corpse_1"),
        new(MorphFaceGame.LE2, "ALN", "BioFace_LyingVorcha1"),
        new(MorphFaceGame.LE2, "ASA", "BioFace_Asari_Spectre_Head"),
        new(MorphFaceGame.LE2, "SAL", "BioFace_male_extra2"),
        new(MorphFaceGame.LE2, "TUR", "face_gangster_1"),
        new(MorphFaceGame.LE2, "KRO", "KroPrl_weyrloc_clanspeaker"),
        new(MorphFaceGame.LE2, "BAT", "face_anto"),
        new(MorphFaceGame.LE3, "HMM", "BioFace_End001_CommRoomTech"),
        new(MorphFaceGame.LE3, "HMM", "nor_ken"),
        new(MorphFaceGame.LE3, "HMM", "global_udina"),
        new(MorphFaceGame.LE3, "HMF", "nor_warroom_guard2"),
        new(MorphFaceGame.LE3, "HMF", "cermir_Orianna"),
        new(MorphFaceGame.LE3, "HMF", "citprs_bounty_hunter01"),
        new(MorphFaceGame.LE3, "ALN", "citwrd_gryll"),
        new(MorphFaceGame.LE3, "ASA", "global_aria"),
        new(MorphFaceGame.LE3, "SAL", "GenericSalarian01_Face"),
        new(MorphFaceGame.LE3, "TUR", "Turian_Primarchson_face"),
        new(MorphFaceGame.LE3, "KRO", "norkro_wreav"),
        new(MorphFaceGame.LE3, "BAT", "Batarian_Crew4_Face")
    ];
}
