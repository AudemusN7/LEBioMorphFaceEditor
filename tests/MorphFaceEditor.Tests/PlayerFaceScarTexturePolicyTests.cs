using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;

namespace MorphFaceEditor.Tests;

public static class PlayerFaceScarTexturePolicyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player face pool combines distinct NPC and base-game Custom Diff/Norm sets", FacePoolIsDistinctAndGameSexScoped),
        new("HMF Player Rough face Diff/Norm set is included across all games", HmfRoughSetIsIncluded),
        new("Player face selection always keeps its diffuse and normal paired", FaceSelectionRemainsAtomic),
        new("Player scars include no-scar and use explicit scalar strengths", ScarSelectionIncludesNoScar),
        new("Player scar assets require exact base-game HMM or HMF identities", ScarSelectionUsesBaseGameOccurrences),
        new("Nested Player face and scar paths retain exact package-local identities", NestedPathsRemainExactIdentities),
        new("HMF Player scars are available in LE1 only", HmfScarSupportIsLe1Only),
        new("Unsupported scar parameter suppresses the scar proposal", UnsupportedScarParameterIsNoOp)
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
            Candidate(TextureCatalogGame.LE2, "BIOG_HMM_HED_PROMorph.Diffuse.HMM_HED_PROCustom_Military_Diff", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 10),
            Candidate(TextureCatalogGame.LE2, "BIOG_HMM_HED_PROMorph.Normal.HMM_HED_PROCustom_Military_Norm", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 11),
            Candidate(TextureCatalogGame.LE2, "BIOG_HMF_HED_PROMorph_R.Diffuse.HMF_HED_PROCustom_Frek_Diff", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 12),
            Candidate(TextureCatalogGame.LE2, "BIOG_HMF_HED_PROMorph_R.Normal.HMF_HED_PROCustom_Military_Norm", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 13),
            Candidate(TextureCatalogGame.LE2, "BIOG_HMM_HED_PROMorph.Diffuse.HMM_HED_PROCustom_ModFace_Diff", "ThirdParty.pcc", TextureCatalogOrigin.Mod, 14),
            Candidate(TextureCatalogGame.LE3, "BIOG_HMM_HED_PROMorph.Diffuse.HMM_HED_PROCustom_OtherGame_Diff", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 15),
            Candidate(TextureCatalogGame.LE2, "BIOG_HMM_HED_PROMorph.Normal.HMM_HED_PROCustom_OtherGame_Norm", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 16)
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

    private static void FaceSelectionRemainsAtomic()
    {
        var npc = new[]
        {
            new PlayerFaceTextureSet("npc.FaceA_Diff", "npc.FaceA_Norm"),
            new PlayerFaceTextureSet("npc.FaceB_Diff", "npc.FaceB_Norm")
        };
        var candidates = new[]
        {
            Candidate(TextureCatalogGame.LE1, "BIOG_HMF_HED_PROMorph_R.Diffuse.HMF_HED_PROCustom_Frek_Diff", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 1),
            Candidate(TextureCatalogGame.LE1, "BIOG_HMF_HED_PROMorph_R.Normal.HMF_HED_PROCustom_Military_Norm", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 2)
        };
        var pool = PlayerFaceScarTexturePolicy.CreateFacePool("le1-human-female", npc, candidates);
        var validPairs = pool.Select(value => (value.DiffusePath, value.NormalPath)).ToHashSet();

        for (var seed = 0; seed < 64; seed++)
        {
            var selected = PlayerFaceScarTexturePolicy.SelectFaceSet("le1-human-female", npc, candidates, seed);
            TestAssert.True(selected is not null && validPairs.Contains((selected.DiffusePath, selected.NormalPath)),
                "Face selection produced a diffuse/normal combination outside the compatible pool.");
        }
    }

    private static void HmfRoughSetIsIncluded()
    {
        foreach (var game in Enum.GetValues<TextureCatalogGame>())
        {
            const string package = "BIOG_HMF_HED_PROMorph_R.pcc";
            var candidates = new[]
            {
                Candidate(game, "BIOG_HMF_HED_PROMorph_R.Diffuse.HMF_HED_PROCustom_Rough_Diff", package,
                    TextureCatalogOrigin.BaseGame, 51),
                Candidate(game, "BIOG_HMF_HED_PROMorph_R.Normal.HMF_HED_PROCustom_Rough_Norm", package,
                    TextureCatalogOrigin.BaseGame, 52)
            };

            var pool = PlayerFaceScarTexturePolicy.CreateFacePool(
                $"{game.ToString().ToLowerInvariant()}-human-female", [], candidates);

            TestAssert.Equal(1, pool.Count);
            TestAssert.True(pool[0].PlayerDiffuseAsset?.InstancedPath.EndsWith(
                    "HMF_HED_PROCustom_Rough_Diff", StringComparison.OrdinalIgnoreCase) == true &&
                pool[0].PlayerNormalAsset?.InstancedPath.EndsWith(
                    "HMF_HED_PROCustom_Rough_Norm", StringComparison.OrdinalIgnoreCase) == true,
                $"The {game} HMF Rough set was not retained as its exact paired assets.");
        }
    }

    private static void ScarSelectionIncludesNoScar()
    {
        var candidates = new[]
        {
            Candidate(TextureCatalogGame.LE3, "BIOG_HMM_HED_PROMorph.Scars.HMM_HED_PROCustom_Scr1", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 21),
            Candidate(TextureCatalogGame.LE3, "BIOG_HMM_HED_PROMorph.Scars.HMM_HED_PROCustom_Scr2", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 22)
        };
        var seenNone = false;
        var seenScar = false;
        for (var seed = 0; seed < 64; seed++)
        {
            var selected = PlayerFaceScarTexturePolicy.SelectScar("le3-human-male", candidates, seed);
            TestAssert.True(selected is not null, "HMM scar selection unexpectedly returned no proposal.");
            if (!selected!.HasScar)
            {
                seenNone = true;
                TestAssert.Equal(0f, selected.CustomScarScalar);
                TestAssert.Equal(0f, selected.ScarDiffuseScalar);
            }
            else
            {
                seenScar = true;
                TestAssert.Equal(1f, selected.CustomScarScalar);
                TestAssert.Equal(1f, selected.ScarDiffuseScalar);
                TestAssert.True(selected.TextureAsset!.InstancedPath.Contains("_Scr", StringComparison.OrdinalIgnoreCase),
                    "The active scar selection did not preserve the chosen texture identity.");
            }
        }
        TestAssert.True(seenNone && seenScar,
            "The scar pool should make both no-scar and active scar outcomes reachable.");
    }

    private static void ScarSelectionUsesBaseGameOccurrences()
    {
        var baseGame = Occurrence("BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 31, 1);
        var mod = Occurrence("ThirdParty.pcc", TextureCatalogOrigin.Mod, 99, 99);
        var candidates = new[]
        {
            new TextureCatalogCandidate(TextureCatalogGame.LE2,
                "BIOG_HMM_HED_PROMorph.Scars.HMM_HED_PROCustom_Scr7", mod, [mod, baseGame]),
            Candidate(TextureCatalogGame.LE2, "BIOG_HMM_HED_PROMorph.Scars.HMM_HED_PROCustom_Scr8", "ThirdParty.pcc", TextureCatalogOrigin.Mod, 32),
            Candidate(TextureCatalogGame.LE2, "BIOG_HMM_HED_PROMorph.Scars.HMM_HED_PROCustom_Scr9_extra", "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 33)
        };
        var selected = Enumerable.Range(0, 64)
            .Select(seed => PlayerFaceScarTexturePolicy.SelectScar("le2-human-male", candidates, seed))
            .Where(value => value?.HasScar == true)
            .Select(value => value!)
            .ToArray();

        TestAssert.True(selected.Length > 0, "No base-game scar candidate was selectable.");
        TestAssert.True(selected.All(value =>
                value.TextureAsset!.PackagePath == baseGame.PackagePath &&
                value.TextureAsset.UIndex == baseGame.ExportUIndex &&
                value.TextureAsset.InstancedPath.EndsWith("HMM_HED_PROCustom_Scr7", StringComparison.OrdinalIgnoreCase)),
            "The scar selection did not retain the exact base-game asset identity.");
    }

    private static void HmfScarSupportIsLe1Only()
    {
        var le1 = new[]
        {
            Candidate(TextureCatalogGame.LE1, "BIOG_HMF_HED_PROMorph_R.Scars.HMF_HED_PROCustom_Scr1", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 41)
        };
        var le2 = new[]
        {
            Candidate(TextureCatalogGame.LE2, "BIOG_HMF_HED_PROMorph_R.Scars.HMF_HED_PROCustom_Scr1", "BIOG_HMF_HED_PROMorph_R.pcc", TextureCatalogOrigin.BaseGame, 42)
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
            new TextureCatalogCandidate(TextureCatalogGame.LE2, diffusePath,
                diffuseOccurrence, [diffuseOccurrence]),
            new TextureCatalogCandidate(TextureCatalogGame.LE2, normalPath,
                normalOccurrence, [normalOccurrence]),
            new TextureCatalogCandidate(TextureCatalogGame.LE2, scarPath,
                scarOccurrence, [scarOccurrence])
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

    private static void UnsupportedScarParameterIsNoOp()
    {
        var result = PlayerFaceScarTexturePolicy.SelectScar(
            "le1-human-male", [], 5, scarParameterSupported: false);
        TestAssert.True(result is null,
            "The scar policy should leave a material unchanged when its scar parameter is unsupported.");
    }

    private static TextureCatalogCandidate Candidate(
        TextureCatalogGame game,
        string path,
        string package,
        TextureCatalogOrigin origin,
        int exportUIndex)
    {
        var occurrence = Occurrence(package, origin, exportUIndex, exportUIndex);
        return new TextureCatalogCandidate(game, path, occurrence, [occurrence]);
    }

    private static TextureCatalogOccurrence Occurrence(
        string package,
        TextureCatalogOrigin origin,
        int exportUIndex,
        int mountPriority) =>
        new(package, exportUIndex, mountPriority, origin, 1024, 1024, "PF_DXT5", "TEXTUREGROUP_Character",
            false, null);
}
