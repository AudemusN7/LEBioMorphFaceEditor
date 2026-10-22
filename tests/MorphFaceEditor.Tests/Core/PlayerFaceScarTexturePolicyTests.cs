using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;

namespace MorphFaceEditor.Tests;

public static class PlayerFaceScarTexturePolicyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player face pool combines distinct NPC and base-game Custom Diff/Norm sets", FacePoolIsDistinctAndGameSexScoped),
        new("Player scar assets use the highest eligible official identity under mod override", ScarSelectionUsesOfficialDefault),
        new("Nested Player face and scar paths retain exact package-local identities", NestedPathsRemainExactIdentities),
        new("HMF Player scars are available in LE1 only", HmfScarSupportIsLe1Only),
    ];

    private static void FacePoolIsDistinctAndGameSexScoped()
    {
        var npc = new[]
        {
            new PlayerFaceTextureSet("npc.FaceA_Diff", "npc.FaceA_Norm"),
            new PlayerFaceTextureSet("npc.FaceA_Diff", "npc.FaceA_Norm"),
            new PlayerFaceTextureSet("npc.FaceB_Diff", "npc.FaceB_Norm"),
            new PlayerFaceTextureSet("npc.Incomplete_Diff", "")
        };
        var candidates = new[]
        {
            Candidate(MorphFaceGame.LE2, "BIOG_HMM_HED_PROMorph.Diffuse.HMM_HED_PROCustom_Military_Diff", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 10),
            Candidate(MorphFaceGame.LE2, "BIOG_HMM_HED_PROMorph.Normal.HMM_HED_PROCustom_Military_Norm", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 11),
            Candidate(MorphFaceGame.LE2, "BIOG_HMF_HED_PROMorph_R.Diffuse.HMF_HED_PROCustom_Frek_Diff", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 12),
            Candidate(MorphFaceGame.LE2, "BIOG_HMF_HED_PROMorph_R.Normal.HMF_HED_PROCustom_Military_Norm", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 13),
            Candidate(MorphFaceGame.LE2, "BIOG_HMM_HED_PROMorph.Diffuse.HMM_HED_PROCustom_ModFace_Diff", "ThirdParty.pcc", TextureCatalogOrigin.Mod, 14),
            Candidate(MorphFaceGame.LE3, "BIOG_HMM_HED_PROMorph.Diffuse.HMM_HED_PROCustom_OtherGame_Diff", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 15),
            Candidate(MorphFaceGame.LE2, "BIOG_HMM_HED_PROMorph.Normal.HMM_HED_PROCustom_OtherGame_Norm", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 16)
        };

        var pool = PlayerFaceScarTexturePolicy.CreateFacePool("le2-human-male", npc, candidates);

        TestAssert.Equal(3, pool.Count);
        TestAssert.True(pool.Any(value => value.DiffusePath == "npc.FaceA_Diff" && value.NormalPath == "npc.FaceA_Norm"),
            "The NPC face set was not preserved as an atomic pair.");
        TestAssert.True(pool.Any(value => value.PlayerDiffuseAsset?.InstancedPath.EndsWith(
                "HMM_HED_PROCustom_Military_Diff", StringComparison.OrdinalIgnoreCase) == true &&
            value.PlayerNormalAsset?.InstancedPath.EndsWith(
                "HMM_HED_PROCustom_Military_Norm", StringComparison.OrdinalIgnoreCase) == true),
            "The compatible HMM Player Custom set was not included.");
        TestAssert.True(!pool.Any(value => value.DiffusePath.Contains("HMF_", StringComparison.OrdinalIgnoreCase) ||
                                          value.DiffusePath.Contains("OtherGame", StringComparison.OrdinalIgnoreCase) ||
                                          value.DiffusePath.Contains("ModFace", StringComparison.OrdinalIgnoreCase)),
            "The Player face pool admitted an incompatible sex, game, or non-base-game set.");
    }

    private static void ScarSelectionUsesOfficialDefault()
    {
        var baseGame = Occurrence("BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 31, 1);
        var officialDlc = Occurrence("DLC_CON_HMM_HED_PROMorph/BIOG_HMM_HED_PROMorph.pcc",
            TextureCatalogOrigin.OfficialDlc, 42, 420);
        var mod = Occurrence("ThirdParty.pcc", TextureCatalogOrigin.Mod, 99, 99);
        var candidates = new[]
        {
            new TextureCatalogCandidate(MorphFaceGame.LE2,
                "BIOG_HMM_HED_PROMorph.Scars.HMM_HED_PROCustom_Scr7", officialDlc,
                [mod, baseGame, officialDlc]) { IsEligibleOfficialDefault = true },
            Candidate(MorphFaceGame.LE2, "BIOG_HMM_HED_PROMorph.Scars.HMM_HED_PROCustom_Scr8", "ThirdParty.pcc", TextureCatalogOrigin.Mod, 32),
            Candidate(MorphFaceGame.LE2, "BIOG_HMM_HED_PROMorph.Scars.HMM_HED_PROCustom_Scr9_extra", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 33)
        };
        var selected = Enumerable.Range(0, 64)
            .Select(seed => PlayerFaceScarTexturePolicy.SelectScar("le2-human-male", candidates, seed))
            .Where(value => value?.HasScar == true)
            .Select(value => value!)
            .ToArray();

        TestAssert.True(selected.Length > 0, "No base-game scar candidate was selectable.");
        TestAssert.True(selected.All(value =>
                value.TextureAsset!.PackagePath == officialDlc.PackagePath &&
                value.TextureAsset.UIndex == officialDlc.ExportUIndex &&
                value.TextureAsset.InstancedPath.EndsWith("HMM_HED_PROCustom_Scr7", StringComparison.OrdinalIgnoreCase)),
            "The scar selection did not retain the highest eligible official asset identity.");

        var unprojected = candidates[0] with { IsEligibleOfficialDefault = false };
        var noScar = PlayerFaceScarTexturePolicy.SelectScar("le2-human-male", [unprojected], 1);
        TestAssert.True(noScar is { HasScar: false },
            "A physical occurrence without eligible official-default provenance was selected as a scar.");
    }

    private static void HmfScarSupportIsLe1Only()
    {
        var le1 = new[]
        {
            Candidate(MorphFaceGame.LE1, "BIOG_HMF_HED_PROMorph_R.Scars.HMF_HED_PROCustom_Scr1", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 41)
        };
        var le2 = new[]
        {
            Candidate(MorphFaceGame.LE2, "BIOG_HMF_HED_PROMorph_R.Scars.HMF_HED_PROCustom_Scr1", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 42)
        };

        var hmfScar = Enumerable.Range(0, 64)
            .Select(seed => PlayerFaceScarTexturePolicy.SelectScar("le1-human-female", le1, seed))
            .FirstOrDefault(value => value?.HasScar == true);
        TestAssert.True(hmfScar is not null,
            "HMF LE1 scar selection should be available.");
        TestAssert.Equal(1f, hmfScar!.CustomScarScalar);
        TestAssert.Equal(0f, hmfScar.ScarDiffuseScalar);
        TestAssert.True(PlayerFaceScarTexturePolicy.SelectScar("le2-human-female", le2, 2) is null,
            "HMF LE2 does not support the Player scar slot.");
        TestAssert.True(PlayerFaceScarTexturePolicy.SelectScar("le3-human-female", [], 2) is null,
            "HMF LE3 does not support the Player scar slot.");
    }

    private static void NestedPathsRemainExactIdentities()
    {
        const string hmmPackage = "BIOG_HMM_HED_PROMorph.pcc";
        const string diffusePath = "Diffuse.HMM_HED_PROCustom_Military_Diff";
        const string normalPath = "Normal.HMM_HED_PROCustom_Military_Norm";
        const string scarPath = "Scars.HMM_HED_PROCustom_Scr7";
        var diffuseOccurrence = Occurrence(hmmPackage, TextureCatalogOrigin.BaseGame, 91, 1);
        var normalOccurrence = Occurrence(hmmPackage, TextureCatalogOrigin.BaseGame, 92, 1);
        var scarOccurrence = Occurrence(hmmPackage, TextureCatalogOrigin.BaseGame, 93, 1);
        var candidates = new[]
        {
            new TextureCatalogCandidate(MorphFaceGame.LE2, diffusePath,
                diffuseOccurrence, [diffuseOccurrence]) { IsEligibleOfficialDefault = true },
            new TextureCatalogCandidate(MorphFaceGame.LE2, normalPath,
                normalOccurrence, [normalOccurrence]) { IsEligibleOfficialDefault = true },
            new TextureCatalogCandidate(MorphFaceGame.LE2, scarPath,
                scarOccurrence, [scarOccurrence]) { IsEligibleOfficialDefault = true }
        };

        var face = PlayerFaceScarTexturePolicy.CreateFacePool("le2-human-male", [], candidates)
            .Single();
        TestAssert.Equal(diffusePath, face.DiffusePath);
        TestAssert.Equal(normalPath, face.NormalPath);
        TestAssert.Equal(diffusePath, face.PlayerDiffuseAsset!.InstancedPath);
        TestAssert.Equal(hmmPackage, face.PlayerDiffuseAsset.PackagePath);
        TestAssert.Equal(diffuseOccurrence.ExportUIndex, face.PlayerDiffuseAsset.UIndex);
        TestAssert.Equal(normalPath, face.PlayerNormalAsset!.InstancedPath);
        TestAssert.Equal(hmmPackage, face.PlayerNormalAsset.PackagePath);
        TestAssert.Equal(normalOccurrence.ExportUIndex, face.PlayerNormalAsset.UIndex);

        var scar = Enumerable.Range(0, 64)
            .Select(seed => PlayerFaceScarTexturePolicy.SelectScar("le2-human-male", candidates, seed))
            .First(value => value?.HasScar == true)!;
        TestAssert.Equal(scarPath, scar.TextureAsset!.InstancedPath);
        TestAssert.Equal(hmmPackage, scar.TextureAsset.PackagePath);
        TestAssert.Equal(scarOccurrence.ExportUIndex, scar.TextureAsset.UIndex);
    }

    private static TextureCatalogCandidate Candidate(
        MorphFaceGame game,
        string path,
        string package,
        TextureCatalogOrigin origin,
        int exportUIndex)
    {
        var occurrence = Occurrence(package, origin, exportUIndex, exportUIndex);
        return new TextureCatalogCandidate(game, path, occurrence, [occurrence])
        {
            IsEligibleOfficialDefault = origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc
        };
    }

    private static TextureCatalogOccurrence Occurrence(
        string package,
        TextureCatalogOrigin origin,
        int exportUIndex,
        int mountPriority) =>
        new(package, exportUIndex, mountPriority, origin, 1024, 1024, "PF_DXT5", "TEXTUREGROUP_Character",
            false, null);
}
