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
        new("paired scalp lock classification follows reviewed mesh across games and sexes", PairedMeshClassificationIsGameAndSexScoped),
        new("Player official DLC hair styles also require runtime backing", OfficialDlcIsEligibleAndModsAreNot),
        new("locked Player hair mesh retains independent scalp styles only", MeshLockKeepsIndependentScalps),
    ];

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
        TestAssert.True(!options.Any(value => value.Id == "hmf-jack"),
            "A mod-only occurrence must not introduce a style into the core pool.");
        var unavailable = new PlayerRuntimeAssetAvailability(MorphFaceGame.LE3,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var playerMeshes = PlayerWorkspaceReferencePolicy.SelectRegistryMeshes([ftl, modOnly], unavailable);
        var playerOptions = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE3, PlayerHairSex.Female, textures, playerMeshes);
        TestAssert.True(!playerOptions.Any(value => value.Id == "hmf-ftl-long-hair"),
            "Official origin alone admitted level-only FTL hair into the Player pool.");

    }

    private static void PairedMeshClassificationIsGameAndSexScoped()
    {
        var cases = new[]
        {
            (PlayerHairSex.Male, "Mhk", "hmm-mhk"),
            (PlayerHairSex.Female, "PROCustomBraids01", "hmf-braids")
        };

        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        foreach (var (sex, stem, id) in cases)
        {
            var prefix = sex == PlayerHairSex.Male ? "HMM" : "HMF";
            var meshName = $"{prefix}_HIR_{stem}_MDL";
            var candidate = Mesh(meshName, TextureCatalogOrigin.BaseGame, prefix);
            var selected = new AssetIdentity(candidate.EffectiveOccurrence.PackagePath,
                TextureCatalogPicker.CanonicalPath(candidate.EffectiveOccurrence.InstancedPath,
                    candidate.EffectiveOccurrence.PackagePath),
                candidate.EffectiveOccurrence.ExportUIndex, "SkeletalMesh");

            // Copies and package overrides preserve the canonical reviewed instance path while
            // changing package/export identity. Lock classification must follow that path.
            var copiedIdentity = selected with { PackagePath = "copied-package.pcc", UIndex = 987 };
            TestAssert.True(PlayerHairStyleCatalog.IsCurrentHairMeshPairedStyle(
                    game, sex, [candidate], copiedIdentity),
                $"{game} {sex} did not classify the copied {id} mesh as scalp-paired.");

            var foreignMesh = new AssetIdentity("other.pcc", $"Unrelated.Hair.{prefix}_HIR_{stem}_MDL",
                987, "SkeletalMesh");
            TestAssert.True(!PlayerHairStyleCatalog.IsCurrentHairMeshPairedStyle(
                    game, sex, [candidate], foreignMesh),
                $"{game} {sex} treated an unrelated same-named {id} mesh as the reviewed pair.");
        }

        var hmmCruMesh = Mesh("HMM_HIR_Fsk_MDL", TextureCatalogOrigin.BaseGame);
        var hmfCruMesh = Mesh("HMF_HIR_Afr_MDL", TextureCatalogOrigin.BaseGame, "HMF");
        foreach (var (sex, candidate) in new[]
                 {
                     (PlayerHairSex.Male, hmmCruMesh),
                     (PlayerHairSex.Female, hmfCruMesh)
                 })
        {
            var package = candidate.EffectiveOccurrence.PackagePath;
            var selected = new AssetIdentity(package,
                TextureCatalogPicker.CanonicalPath(candidate.EffectiveOccurrence.InstancedPath, package),
                candidate.EffectiveOccurrence.ExportUIndex, "SkeletalMesh");
            var copied = selected with { PackagePath = "copied-package.pcc", UIndex = 987 };
            TestAssert.True(!PlayerHairStyleCatalog.IsCurrentHairMeshPairedStyle(
                    MorphFaceGame.LE1, sex, [candidate], copied),
                $"The {sex} Cru-paired mesh was incorrectly classified as requiring its scalp.");
        }
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
