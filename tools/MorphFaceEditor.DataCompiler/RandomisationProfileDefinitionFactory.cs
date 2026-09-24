using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.DataCompiler;

/// <summary>Projects runtime profile evidence into the smaller contract required by corpus compilation.</summary>
public static class RandomisationProfileDefinitionFactory
{
    public static IReadOnlyList<RandomisationProfileDefinition> CreateDefault(
        MorphFaceProfileRegistry registry,
        MorphTargetCatalog targetCatalog)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(targetCatalog);
        return registry.Profiles.Select(profile =>
        {
            var targets = profile.IgnoresAuthoredGeometry
                ? []
                : targetCatalog.Load(profile, profile.Game);
            var available = targets.Select(target =>
                {
                    var name = ObjectName(target.Source.InstancedPath);
                    var resolved = new ResolvedMorphFeature(
                        new MorphFeatureValue(name, 0), target,
                        MorphFeatureResolutionKind.DirectTarget, "Compiler target inventory.");
                    return (Name: name, Metadata: profile.UiProfile.Describe(resolved, true));
                })
                .Where(value => value.Metadata.IsVisible && value.Metadata.IsEditable)
                .Select(value => value.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new RandomisationProfileDefinition(
                profile.Key,
                profile.Game,
                MorphRandomisationPoolRouter.Resolve(profile.Key),
                profile.Matches,
                available,
                profile.FeatureAliases)
            {
                IsMaterialOnly = profile.IgnoresAuthoredGeometry
            };
        }).ToArray();
    }

    private static string ObjectName(string instancedPath)
    {
        var separator = instancedPath.LastIndexOf('.');
        return separator < 0 ? instancedPath : instancedPath[(separator + 1)..];
    }
}

/// <summary>Reviewed exact-path exclusions remain visible and attributable in generated audit output.</summary>
public static class ReviewedRandomisationExclusions
{
    public static IReadOnlyList<RandomisationDonorExclusion> All { get; } =
    [
        new(
            LegendaryExplorer.MorphFaceGame.LE3,
            "Human Male.LE3_HMM_Morphs.Broke",
            "Known broken HMM donor requires a stored final-skeleton residual and cannot be reconstructed safely from morph sliders alone."),
        ..Full(LegendaryExplorer.MorphFaceGame.LE1,
            "HMF.Asari", "HMM.Broke", "ASA.hench_asari", "HMF.hench_humanfemale",
            "HMM.hench_humanmale", "HMM.hench_humanmale_combat", "KRO.hench_krogan",
            "HMM.hench_pilot", "HMM.Krogan", "ASA.sta20_avina",
            "HMF.UNC30_ScientistFemale1", "HMM.war50_vi",
            "HMF.PRC2_femalecrew1"),
        ..Full(LegendaryExplorer.MorphFaceGame.LE2,
            "HMF.Asari", "HMF.BioFace_Ashley", "HMM.BioFace_HMM_Guest10",
            "HMM.BioFace_Hologram", "HMM.BioFace_Kaidan", "HMF.BioFace_Wife",
            "HMM.Broke", "ASA.hench_asari", "HMM.hench_humanmale",
            "HMM.hench_joker", "HMM.hench_joker_prologue", "KRO.hench_krogan",
            "HMM.hench_leadingman", "HMM.Krogan", "HMF.Player_Female",
            "ASA.Skel_Asaris_1", "ASA.twrli2_hotel_civilian_female"),
        ..Full(LegendaryExplorer.MorphFaceGame.LE3,
            "HMF.Asari", "ASA.AsariCivilian01_face", "HMM.Broke",
            "HMM.cithub_thane_doc", "ASA.Citsam_commando_02_face",
            "TUR.citwrd_chorban_csec_guard", "HMM.global_joker", "HMM.HMM_Deco_1",
            "SAL.kro001_guard7", "SAL.kro001_guard9", "HMM.Krogan",
            "ASA.OmgHub_Asa_07", "SAL.OmgHub_Sal_02", "SAL.Salarian_Deco_1",
            "HMF.citprs_batmil_csec_officer", "HMF.OmgJck_hurtkid_F"),
        ..ExcludeMorph(LegendaryExplorer.MorphFaceGame.LE1,
            "SAL.jug20_captainkirrahe", "TUR.sta60_amb_buyer02",
            "ASA.war30_thorianasari"),
        ..ExcludeMorph(LegendaryExplorer.MorphFaceGame.LE2,
            "KRO.BioFace_KroganPatron", "ASA.BioFace_tests_shiala"),
        ..ExcludeMorph(LegendaryExplorer.MorphFaceGame.LE3,
            "HMM.proear_SoldierInjured_F", "TUR.N7Rctr_Turian_Nyrek",
            "HMM.OmgJck_StudentLeader_F", "HMM.cit002_consort_fan",
            "HMM.cit_news_announcer", "HMM.end001_alliance_marine",
            "HMM.expak1_m2_guard2"),
        ..ExcludeMaterial(LegendaryExplorer.MorphFaceGame.LE2,
            "HMM.Face_Citasl_Lawyer"),
        ..ExcludeMaterial(LegendaryExplorer.MorphFaceGame.LE3,
            "HMF.HumanCivilian01_face")
    ];

    private static IEnumerable<RandomisationDonorExclusion> Full(
        LegendaryExplorer.MorphFaceGame sourceGame,
        params string[] facePaths) =>
        facePaths.Select(path => new RandomisationDonorExclusion(
            sourceGame, path, "Reviewed as unsuitable for both morph and material randomisation."));

    private static IEnumerable<RandomisationDonorExclusion> ExcludeMorph(
        LegendaryExplorer.MorphFaceGame sourceGame,
        params string[] facePaths) =>
        facePaths.Select(path => new RandomisationDonorExclusion(
            sourceGame, path, "Reviewed as unsuitable for morph randomisation; material seed retained.")
        {
            Scope = RandomisationDonorExclusionScope.Morph
        });

    private static IEnumerable<RandomisationDonorExclusion> ExcludeMaterial(
        LegendaryExplorer.MorphFaceGame sourceGame,
        params string[] facePaths) =>
        facePaths.Select(path => new RandomisationDonorExclusion(
            sourceGame, path, "Reviewed as unsuitable for material randomisation; morph seed retained.")
        {
            Scope = RandomisationDonorExclusionScope.Material
        });
}
