using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class PlayerWorkspaceReferencePolicyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player texture projection admits only the two global HIR fallback maps", GlobalHairFallbacksAreAllowed),
        new("Player texture projection admits LE3 HMM Short Scalp maps only from their base package", Le3ShortScalpMapsAreNarrowlyAllowed)
    ];

    private static void GlobalHairFallbacksAreAllowed()
    {
        const string black = "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black";
        const string normal = "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Norm";
        const string unrelatedTwoPart = "BIOG_HMM_HED_PROMorph_R.HMM_HED_Norm";
        const string unrelatedNonBiog = "HMM_HED_PROMorph_R.Brow.HMM_HED_PROCustom_ArchedBrow";
        const string nonBiogFallback = "Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black";
        TestAssert.True(!PlayerWorkspaceReferencePolicy.IsSeekFreeQualified(black) &&
                        !PlayerWorkspaceReferencePolicy.IsSeekFreeQualified(normal),
            "The fallback exception must not broaden the general seek-free package-path rule.");

        var projected = PlayerWorkspaceReferencePolicy.SelectRegistryTextures(
        [
            Candidate(black),
            Candidate(normal),
            Candidate(unrelatedTwoPart),
            Candidate(unrelatedNonBiog),
            Candidate(nonBiogFallback)
        ]);

        TestAssert.Equal(2, projected.Count);
        TestAssert.True(projected.Any(candidate => candidate.InstancedPath == black),
            "The global black hair-mask fallback was excluded from Player texture projection.");
        TestAssert.True(projected.Any(candidate => candidate.InstancedPath == normal),
            "The global normal-map hair fallback was excluded from Player texture projection.");
    }

    private static void Le3ShortScalpMapsAreNarrowlyAllowed()
    {
        const string diffuse = "Hair_Short03.HMM_HIR_Short_Scalp_Diff";
        const string normal = "Hair_Short03.HMM_HIR_Short_Scalp_Norm";
        const string mask = "Hair_Short03.HMM_HIR_Short_Scalp_Mask";
        var baseGame = Occurrence("BIOG_HMM_HIR_PRO_R.pcc", TextureCatalogOrigin.BaseGame, 4);
        var mod = Occurrence("ThirdPartyHair.pcc", TextureCatalogOrigin.Mod, 50);
        var candidates = new[]
        {
            Candidate(TextureCatalogGame.LE3, diffuse, mod, [mod, baseGame]),
            Candidate(TextureCatalogGame.LE3, normal, baseGame, [baseGame]),
            Candidate(TextureCatalogGame.LE3, mask, baseGame, [baseGame]),
            Candidate(TextureCatalogGame.LE2, diffuse, baseGame, [baseGame]),
            Candidate(TextureCatalogGame.LE3, diffuse,
                Occurrence("BIOG_HMM_HIR_PRO.pcc", TextureCatalogOrigin.BaseGame, 8),
                [Occurrence("BIOG_HMM_HIR_PRO.pcc", TextureCatalogOrigin.BaseGame, 8)]),
            Candidate(TextureCatalogGame.LE3, diffuse, mod, [mod]),
            Candidate(TextureCatalogGame.LE3,
                "Hair_Short03.HMM_HIR_Short_Scalp_Tang", baseGame, [baseGame]),
            Candidate(TextureCatalogGame.LE3,
                "Hair_Short04.HMM_HIR_Short_Scalp_Diff", baseGame, [baseGame])
        };

        var projected = PlayerWorkspaceReferencePolicy.SelectRegistryTextures(candidates);

        TestAssert.Equal(3, projected.Count);
        TestAssert.True(projected.Any(candidate => candidate.InstancedPath == diffuse),
            "The LE3 Short Scalp diffuse map was excluded despite its base-game source occurrence.");
        TestAssert.True(projected.Any(candidate => candidate.InstancedPath == normal),
            "The LE3 Short Scalp normal map was excluded despite its base-game source occurrence.");
        TestAssert.True(projected.Any(candidate => candidate.InstancedPath == mask),
            "The LE3 Short Scalp mask was excluded despite its base-game source occurrence.");
    }

    private static TextureCatalogCandidate Candidate(string path)
    {
        var occurrence = Occurrence("BIOG_TestPackage.pcc", TextureCatalogOrigin.BaseGame, 1);
        return Candidate(TextureCatalogGame.LE2, path, occurrence, [occurrence]);
    }

    private static TextureCatalogCandidate Candidate(
        TextureCatalogGame game,
        string path,
        TextureCatalogOccurrence effective,
        IReadOnlyList<TextureCatalogOccurrence> occurrences) =>
        new(game, path, effective, occurrences);

    private static TextureCatalogOccurrence Occurrence(
        string package,
        TextureCatalogOrigin origin,
        int exportUIndex) =>
        new(package, exportUIndex, 0, origin, 1024, 1024, "PF_DXT5", "TEXTUREGROUP_Character", false, null);
}
