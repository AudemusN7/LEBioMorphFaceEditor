using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;

namespace MorphFaceEditor.Tests;

public static class PlayerHairStyleCatalogTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("manual LE3 CC scalp pairs retain the automatic mesh pool", ManualCcPairsKeepAutomaticPool),
        new("Player official DLC hair styles also require runtime backing", OfficialDlcIsEligibleAndModsAreNot),
        new("locked Player hair mesh retains independent scalp styles only", MeshLockKeepsIndependentScalps),
    ];

    private static void ManualCcPairsKeepAutomaticPool()
    {
        var cases = new[]
        {
            (PlayerHairSex.Male, "Mhk", "HMM_HIR_Mhk_MDL", "HMM_HIR_Mhk_MDL_CC", "hmm-mhk"),
            (PlayerHairSex.Female, "Mhk", "HMF_HIR_Mhk_MDL", "HMF_HIR_Mhk_MDL_CC", "hmf-mhk"),
            (PlayerHairSex.Male, "Sar", "HMM_HIR_Ssk_01_MDL", "HMM_HIR_Ssk_01_MDL_CC", "hmm-sar-ssk"),
            (PlayerHairSex.Male, "Sar", "HMM_HIR_Ssk_02_MDL", "HMM_HIR_Ssk_02_MDL_CC", "hmm-sar-ssk")
        };
        foreach (var (sex, stem, baseName, ccName, id) in cases)
        {
            var prefix = sex == PlayerHairSex.Male ? "HMM" : "HMF";
            var textures = ScalpFamily(MorphFaceGame.LE3, prefix, stem);
            var native = Mesh(baseName, TextureCatalogOrigin.BaseGame, prefix);
            var cc = Mesh(ccName, TextureCatalogOrigin.BaseGame, prefix);
            var before = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE3, sex, textures, [native]);
            foreach (var ccOnly in new[] { false, true })
            {
                var meshes = ccOnly ? new[] { cc } : new[] { native, cc };
                var automatic = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE3, sex, textures, meshes);
                if (ccOnly)
                    TestAssert.True(automatic.All(value => value.Id != id),
                        $"The manual-only {ccName} added an automatic hairstyle outcome.");
                else
                {
                    TestAssert.True(before.Select(value => value.Id).SequenceEqual(automatic.Select(value => value.Id)),
                        $"Adding {ccName} changed the automatic style pool or its weighting.");
                    TestAssert.True(before.Single(value => value.Id == id).MeshVariants.SequenceEqual(
                            automatic.Single(value => value.Id == id).MeshVariants),
                        $"Adding {ccName} changed automatic mesh selection.");
                }
                var manual = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE3, sex, textures, meshes,
                    includeManualPairedVariants: true).Single(value => value.Id == id);
                var copied = new AssetIdentity("copied-package.pcc", cc.CanonicalPath, 987, "SkeletalMesh");
                TestAssert.True(PlayerHairStyleCatalog.MatchesDedicatedScalpPair(manual, copied),
                    $"The {(ccOnly ? "CC-only" : "combined")} catalogue did not pair the canonical {ccName} selection.");
                TestAssert.True(!PlayerHairStyleCatalog.MatchesDedicatedScalpPair(manual,
                        copied with { InstancedPath = $"Unrelated.Hair.{ccName}" }),
                    $"An unrelated same-named {ccName} was treated as the reviewed pair.");
            }
            foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2 })
                TestAssert.True(PlayerHairStyleCatalog.Resolve(game, sex, ScalpFamily(game, prefix, stem), [cc],
                        includeManualPairedVariants: true).All(value => value.Id != id),
                    $"The LE3-only {ccName} introduced a manual pair in {game}.");
        }
    }

    private static void OfficialDlcIsEligibleAndModsAreNot()
    {
        var textures = ScalpFamily(MorphFaceGame.LE3, "HMF", "Cru");
        var ftl = Mesh("HMF_HIR_FTL_LongHair_MDL", TextureCatalogOrigin.OfficialDlc);
        var modOnly = Mesh("HMF_HIR_PROJack_MDL", TextureCatalogOrigin.Mod);
        var options = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE3, PlayerHairSex.Female,
            textures, [ftl, modOnly]);
        TestAssert.True(options.Any(value => value.Id == "hmf-ftl-long-hair" &&
                                            value.MeshAction == PlayerHairMeshAction.Set),
            "The broader NPC/PCC catalogue lost a reviewed official style.");
        var meshOnly = options.Single(value => value.Id == "hmf-ftl-long-hair");
        TestAssert.True(!PlayerHairStyleCatalog.MatchesDedicatedScalpPair(meshOnly, meshOnly.MeshVariants.Single()),
            "A mesh-only style using the shared Cru scalp was incorrectly classified as a dedicated pair.");
        TestAssert.True(!options.Any(value => value.Id == "hmf-jack"),
            "A mod-only occurrence must not introduce a style into the core pool.");
        var unavailable = new PlayerRuntimeAssetAvailability(MorphFaceGame.LE3,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var playerMeshes = PlayerWorkspaceReferencePolicy.SelectRegistryMeshes([ftl, modOnly], unavailable);
        var playerOptions = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE3, PlayerHairSex.Female, textures, playerMeshes);
        TestAssert.True(!playerOptions.Any(value => value.Id == "hmf-ftl-long-hair"),
            "Official origin alone admitted level-only FTL hair into the Player pool.");

    }

    private static void MeshLockKeepsIndependentScalps()
    {
        var textures = ScalpFamily(MorphFaceGame.LE1, "HMM", "Cru")
            .Concat(ScalpFamily(MorphFaceGame.LE1, "HMM", "Afr")).ToArray();
        var mesh = Mesh("HMM_HIR_Fsk_MDL", TextureCatalogOrigin.BaseGame);
        var options = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE1, PlayerHairSex.Male,
            textures, [mesh], meshLocked: true);
        TestAssert.True(options.Any(value => value.Id == "hmm-cru" && value.MeshAction == PlayerHairMeshAction.Keep),
            "An independent scalp option must preserve the locked attachment.");
        TestAssert.True(options.Any(value => value.Id == "hmm-afr" && value.MeshAction == PlayerHairMeshAction.Keep),
            "An independent morph-paired scalp remains available under a mesh lock.");
        TestAssert.True(!options.Any(value => value.Id == "hmm-fsk" || value.Id == "hmm-mhk"),
            "Styles that select an attachment or require a paired attachment must be excluded while locked.");
    }

    private static TextureCatalogCandidate[] ScalpFamily(MorphFaceGame game, string sex, string stem)
    {
        var prefix = $"{sex}_HIR_{stem}";
        if (stem == "Mhk") prefix += "_Scalp";
        var names = new[] { $"{prefix}_Diff", $"{prefix}_Norm", $"{prefix}_Mask", "GBL_ARM_ALL_Norm" };
        if (sex == "HMM" && stem == "Cru") names = names.Append("HMM_HED_PROBase_Scalp_Norm_Stack").ToArray();
        names = names.Append("GBL_ARM_ALL_Black").ToArray();
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Select((name, index) =>
        {
            var path = $"BIOG_{sex}_HIR_PRO_R.Hair.{name}";
            var occurrence = new TextureCatalogOccurrence($"BIOG_{sex}_HIR_PRO_R.pcc", index + 1,
                0, TextureCatalogOrigin.BaseGame, 256, 256, "PF_DXT5", "Character", false, null);
            return new TextureCatalogCandidate(game, path, occurrence, [occurrence])
            { IsEligibleOfficialDefault = true };
        }).ToArray();
    }

    private static AttachmentMeshCandidate Mesh(string objectName, TextureCatalogOrigin origin,
        string sex = "HMM")
    {
        var occurrence = new AttachmentMeshOccurrence($"BIOG_{sex}_HIR_PRO.pcc", $"Hair.{objectName}",
            7, 1, origin, 28);
        return new AttachmentMeshCandidate($"BIOG_{sex}_HIR_PRO.Hair.{objectName}", occurrence, [occurrence])
        { IsEligibleOfficialDefault = origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc };
    }

}
