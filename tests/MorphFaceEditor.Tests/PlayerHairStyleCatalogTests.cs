using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;

namespace MorphFaceEditor.Tests;

public static class PlayerHairStyleCatalogTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player hair style pool pairs mesh-only entries with Cru scalp", MeshOnlyStylesPairWithCru),
        new("HMF mesh-only styles also pair with Cru scalp", HmfMeshOnlyStylesPairWithCru),
        new("HMF LE2 Cru scalp uses the shared player normal fallback", HmfLe2CruUsesSharedNormalFallback),
        new("Mhk and braided scalp use their scalp maps, not the mesh maps", PairedScalpsPreferDedicatedMaps),
        new("manual HMF scalp prompts recognize installed mesh overrides", ManualHmfPairRecognizesPackageOverride),
        new("paired scalp lock lookup follows the reviewed selected mesh", PairedScalpLockLookupUsesInstalledPair),
        new("paired scalp lock classification follows reviewed mesh across games and sexes", PairedMeshClassificationIsGameAndSexScoped),
        new("NPC HMF Afr prefers the native mesh when custom and native variants exist", NpcHmfAfrPrefersNativeMesh),
        new("HMF Mom scalp uses its own diffuse and normal with shared mask", HmfMomScalpUsesDedicatedMaps),
        new("official DLC hair styles are eligible but mod-only styles are not", OfficialDlcIsEligibleAndModsAreNot),
        new("locked Player hair mesh retains independent scalp styles only", MeshLockKeepsIndependentScalps),
        new("Ssk slot exposes one style with two meshes and a no-mesh outcome", SskSlotHasThreeEqualOutcomes),
        new("Player hair catalogue chooses the highest eligible official occurrence under mod override", OfficialIdentitySurvivesOverride)
    ];

    private static void MeshOnlyStylesPairWithCru()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE1, "HMM", "Cru");
        var mesh = Mesh("HMM_HIR_Fsk_MDL", TextureCatalogOrigin.BaseGame);
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1, PlayerHairSex.Male,
            textures, [mesh]);
        var option = options.Single(value => value.Id == "hmm-fsk");
        TestAssert.Equal("HMM_HIR_Cru_Diff", ObjectName(option.ScalpTextures["HED_Scalp_Diff"]));
        TestAssert.Equal(PlayerHairMeshAction.Set, option.MeshAction);
        TestAssert.True(!option.HasDedicatedScalpPair && !option.RequiresMeshForScalp,
            "A mesh-only Cru style was treated as requiring a new mesh during a scalp-only roll.");
        TestAssert.Equal("HMM_HIR_Fsk_MDL", ObjectName(option.MeshVariants.Single().InstancedPath));
        TestAssert.True(options.Any(value => value.Id == "hmm-cru" && value.MeshAction == PlayerHairMeshAction.Clear),
            "Cru must remain an independent scalp-only style as well as the mesh-only pairing scalp.");
        TestAssert.True(options.All(value => value.ScalpTextures.Count == 4),
            "Every HMM style in the pool must resolve a complete scalp family.");
    }

    private static void HmfMeshOnlyStylesPairWithCru()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE1, "HMF", "Cru");
        var mesh = new AttachmentMeshCandidate("BIOG_HMF_HIR_PRO.Classy.HMF_HIR_Cls_MDL",
            new AttachmentMeshOccurrence("BIOG_HMF_HIR_PRO.pcc", "HMF_HIR_Cls_MDL",
                13, 1, TextureCatalogOrigin.BaseGame, 31),
            [new AttachmentMeshOccurrence("BIOG_HMF_HIR_PRO.pcc", "HMF_HIR_Cls_MDL",
                13, 1, TextureCatalogOrigin.BaseGame, 31)])
        { IsEligibleOfficialDefault = true };
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1, PlayerHairSex.Female,
            textures, [mesh]);
        var option = options.Single(value => value.Id == "hmf-cls");
        TestAssert.Equal("HMF_HIR_Cru_Diff", ObjectName(option.ScalpTextures["HED_Scalp_Diff"]));
        TestAssert.True(options.Any(value => value.Id == "hmf-cru" && value.MeshAction == PlayerHairMeshAction.Clear),
            "HMF Cru must remain an independent scalp-only style.");
    }

    private static void OfficialDlcIsEligibleAndModsAreNot()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE3, "HMF", "Cru");
        var ftl = Mesh("HMF_HIR_FTL_LongHair_MDL", TextureCatalogOrigin.OfficialDlc);
        var modOnly = Mesh("HMF_HIR_PROJack_MDL", TextureCatalogOrigin.Mod);
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE3, PlayerHairSex.Female,
            textures, [ftl, modOnly]);
        TestAssert.True(options.Any(value => value.Id == "hmf-ftl-long-hair" &&
                                            value.MeshAction == PlayerHairMeshAction.Set),
            "A reviewed official DLC style must remain eligible.");
        TestAssert.True(!options.Any(value => value.Id == "hmf-jack"),
            "A mod-only occurrence must not introduce a style into the core pool.");
    }

    private static void PairedScalpsPreferDedicatedMaps()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE1, "HMM", "Cru")
            .Concat(new[]
            {
                "HMM_HIR_Mhk_Scalp_Diff", "HMM_HIR_Mhk_Scalp_Norm", "HMM_HIR_Mhk_Scalp_Mask",
                "HMM_HIR_Mhk_Diff", "HMM_HIR_Mhk_Norm", "HMM_HIR_Mhk_Mask",
                "HMM_HIR_PROCustomBraids01_Scalp_Diff", "HMM_HIR_PROCustomBraids01_Scalp_Norm",
                "HMM_HIR_PROCustomBraids01_Scalp_Mask", "HMM_HIR_PROCustomBraids01_Diff",
                "HMM_HIR_PROCustomBraids01_Norm", "HMM_HIR_PROCustomBraids01_Mask"
            }.SelectMany(name => ScalpTexture(TextureCatalogGame.LE1, name)))
            .ToArray();
        var meshes = new[]
        {
            Mesh("HMM_HIR_Mhk_MDL", TextureCatalogOrigin.BaseGame),
            Mesh("HMM_HIR_PROCustomBraids01_MDL", TextureCatalogOrigin.BaseGame)
        };
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1, PlayerHairSex.Male,
            textures, meshes);
        var mhk = options.Single(value => value.Id == "hmm-mhk");
        var braids = options.Single(value => value.Id == "hmm-braids");
        TestAssert.True(mhk.HasDedicatedScalpPair && mhk.RequiresMeshForScalp &&
                        braids.HasDedicatedScalpPair && braids.RequiresMeshForScalp,
            "A mandatory paired scalp could be selected without its mesh.");
        TestAssert.Equal("HMM_HIR_Mhk_Scalp_Diff", ObjectName(mhk.ScalpTextures["HED_Scalp_Diff"]));
        TestAssert.Equal("HMM_HIR_Mhk_Scalp_Norm", ObjectName(mhk.ScalpTextures["HED_Scalp_Norm"]));
        TestAssert.Equal("HMM_HIR_Mhk_Scalp_Mask", ObjectName(mhk.ScalpTextures["HED_Scalp_Spec"]));
        TestAssert.Equal("HMM_HIR_PROCustomBraids01_Scalp_Diff", ObjectName(braids.ScalpTextures["HED_Scalp_Diff"]));
        TestAssert.Equal("GBL_ARM_ALL_Norm", ObjectName(mhk.ScalpTextures["HED_Tang"]));
    }

    private static void ManualHmfPairRecognizesPackageOverride()
    {
        const string basePackage = @"D:\EA Games\Mass Effect Legendary Edition\Game\ME2\BioGame\CookedPCConsole\BIOG_HMF_HIR_PRO.pcc";
        const string overridePackage = @"D:\EA Games\Mass Effect Legendary Edition\Game\ME2\BioGame\DLC\DLC_MOD_LE2PATCH\CookedPCConsole\BIOG_HMF_HIR_PRO.pcc";
        var textureNames = new[]
        {
            "HMF_HIR_PROCustomBraids01_Scalp_Diff", "HMF_HIR_PROCustomBraids01_Scalp_Norm",
            "HMF_HIR_PROCustomBraids01_Scalp_Mask", "HMF_HIR_PROCustomShortAfro_Diff",
            "HMF_HIR_PROCustomShortAfro_Norm", "HMF_HIR_PROCustomShortAfro_Mask",
            "GBL_ARM_ALL_Norm", "GBL_ARM_ALL_Black"
        };
        var textures = textureNames.Select(name =>
        {
            var outer = name.Contains("Braids01", StringComparison.OrdinalIgnoreCase)
                ? "PROCustomBraids01"
                : name.Contains("ShortAfro", StringComparison.OrdinalIgnoreCase)
                    ? "PROCustomShortAfro"
                    : "Global";
            var package = outer == "Global" ? "BIOG_Humanoid_MASTER_MTR_R.pcc" : basePackage;
            var instancedPath = outer == "Global"
                ? $"BIOG_Humanoid_MASTER_MTR_R.Global.{name}"
                : $"BIOG_HMF_HIR_PRO.{outer}.{name}";
            var occurrence = new TextureCatalogOccurrence(package, 1, 0, TextureCatalogOrigin.BaseGame,
                256, 256, "PF_DXT5", "Character", false, null);
            return new TextureCatalogCandidate(TextureCatalogGame.LE2, instancedPath,
                occurrence, [occurrence]) { IsEligibleOfficialDefault = true };
        }).ToArray();
        var meshes = new[] { "Braids01", "ShortAfro" }.Select((stem, index) =>
        {
            var meshName = $"HMF_HIR_PROCustom{stem}_MDL";
            var outer = $"PROCustom{stem}";
            var instancedPath = $"{outer}.{meshName}";
            var canonicalPath = $"BIOG_HMF_HIR_PRO.{instancedPath}";
            var baseOccurrence = new AttachmentMeshOccurrence(basePackage, instancedPath,
                index == 0 ? 1270 : 1280, 0, TextureCatalogOrigin.BaseGame, 42);
            var overrideOccurrence = new AttachmentMeshOccurrence(overridePackage, instancedPath,
                index == 0 ? 1270 : 1280, 2499, TextureCatalogOrigin.Mod, 42);
            return (Candidate: new AttachmentMeshCandidate(canonicalPath, baseOccurrence,
                    [overrideOccurrence, baseOccurrence]) { IsEligibleOfficialDefault = true },
                Selected: new AssetIdentity(overridePackage, canonicalPath,
                    overrideOccurrence.ExportUIndex, "SkeletalMesh"),
                Id: index == 0 ? "hmf-braids" : "hmf-short-afro");
        }).ToArray();

        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE2, PlayerHairSex.Female,
            textures, meshes.Select(value => value.Candidate).ToArray());

        foreach (var mesh in meshes)
        {
            var option = options.Single(value => value.Id == mesh.Id);
            TestAssert.True(option.HasDedicatedScalpPair,
                $"The {mesh.Id} style lost its dedicated scalp pair.");
            TestAssert.True(!option.MeshVariants.Any(value => value.PackagePath.Equals(
                    overridePackage, StringComparison.OrdinalIgnoreCase)),
                "A mod override leaked into the randomisation donor variants.");
            TestAssert.True(PlayerHairStyleCatalog.MatchesDedicatedScalpPair(option, mesh.Selected),
                $"The actual installed override identity did not match {mesh.Id} for manual prompt classification.");
        }
    }

    private static void HmfMomScalpUsesDedicatedMaps()
    {
        var textures = ScalpTexture(TextureCatalogGame.LE1, "HMF_HIR_Mom_Scalp_Diff")
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "HMF_HIR_Mom_Scalp_Norm"))
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "HMF_HIR_Mom_Diff"))
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "HMF_HIR_Mom_Norm"))
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "HMF_HIR_Mom_Mask"))
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "HMF_HIR_Mom_Tang"))
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "GBL_ARM_ALL_Black"))
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "GBL_ARM_ALL_Norm"))
            .ToArray();
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1, PlayerHairSex.Female,
            textures, []);
        var mom = options.Single(value => value.Id == "hmf-mom-scalp");
        TestAssert.Equal("HMF_HIR_Mom_Scalp_Diff", ObjectName(mom.ScalpTextures["HED_Scalp_Diff"]));
        TestAssert.Equal("HMF_HIR_Mom_Scalp_Norm", ObjectName(mom.ScalpTextures["HED_Scalp_Norm"]));
        TestAssert.Equal("GBL_ARM_ALL_Black", ObjectName(mom.ScalpTextures["HED_Scalp_Spec"]));
        TestAssert.Equal("GBL_ARM_ALL_Norm", ObjectName(mom.ScalpTextures["HED_Tang"]));
    }

    private static void HmfLe2CruUsesSharedNormalFallback()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE2, "HMF", "Cru")
            .Where(value => value.ObjectName != "HMF_HIR_Cru_Norm")
            .Concat(ScalpTexture(TextureCatalogGame.LE2, "HMF_HED_PROBase_Scalp_Norm"))
            .ToArray();
        var cls = Mesh("HMF_HIR_Cls_MDL", TextureCatalogOrigin.BaseGame);
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE2, PlayerHairSex.Female,
            textures, [cls]);
        var option = options.Single(value => value.Id == "hmf-cls");
        TestAssert.Equal("HMF_HED_PROBase_Scalp_Norm", ObjectName(option.ScalpTextures["HED_Scalp_Norm"]));
    }

    private static void PairedScalpLockLookupUsesInstalledPair()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE1, "HMM", "Cru")
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "HMM_HIR_Mhk_Scalp_Diff"))
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "HMM_HIR_Mhk_Scalp_Norm"))
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "HMM_HIR_Mhk_Scalp_Mask"))
            .ToArray();
        var mhkPath = textures.Single(value => value.ObjectName == "HMM_HIR_Mhk_Scalp_Diff").InstancedPath;
        var mhk = Mesh("HMM_HIR_Mhk_MDL", TextureCatalogOrigin.BaseGame);
        var mhkMeshIdentity = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1, PlayerHairSex.Male,
            textures, [mhk]).Single(value => value.Id == "hmm-mhk").MeshVariants.Single();
        TestAssert.True(PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk], mhkPath, mhkMeshIdentity),
            "Mhk scalp must be considered paired when the matching reviewed mesh is selected.");
        TestAssert.True(PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk], mhkPath,
                mhkMeshIdentity with { PackagePath = "patched-copy.pcc", UIndex = 99 }),
            "A copied or patched instance of the reviewed paired mesh was not recognized by its canonical path.");
        TestAssert.True(!PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk], mhkPath, null),
            "Mhk scalp alone must not be lock-protected when no mesh is selected.");
        TestAssert.True(!PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk], mhkPath,
                new AssetIdentity("other.pcc", "Other.Mohawk.HMM_HIR_Mhk_MDL", 7, "SkeletalMesh")),
            "A same-named mesh from another package must not be treated as the installed paired attachment.");
        TestAssert.True(PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk],
                "BIOG_HMM_HIR_PRO_R.CrewCut.HMM_HIR_Cru_Diff", mhkMeshIdentity),
            "A reviewed paired mesh must protect its scalp despite a different native diffuse path.");
        TestAssert.True(PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk],
                "DLC_MOD_ThirdParty.Hair.HMM_HIR_Mhk_Scalp_Diff", mhkMeshIdentity),
            "Paired-mesh protection must not depend on the current diffuse resolving in the core texture registry.");

        var sarTextures = ScalpFamily(TextureCatalogGame.LE1, "HMM", "Sar")
            .Concat(ScalpTexture(TextureCatalogGame.LE1, "HMM_HIR_Sar_Tang"))
            .ToArray();
        var ssk01 = Mesh("HMM_HIR_Ssk_01_MDL", TextureCatalogOrigin.BaseGame);
        var sarPath = sarTextures.Single(value => value.ObjectName == "HMM_HIR_Sar_Diff").InstancedPath;
        var ssk01Identity = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1, PlayerHairSex.Male,
            sarTextures, [ssk01]).Single(value => value.Id == "hmm-sar-ssk").MeshVariants.Single();
        TestAssert.True(PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, sarTextures, [ssk01], sarPath, ssk01Identity),
            "Sar is paired under the Ssk slot only when an Ssk mesh is selected.");
        TestAssert.True(!PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, sarTextures, [ssk01], sarPath, null),
            "The same Sar scalp is independent while no Ssk mesh is selected.");
    }

    private static void PairedMeshClassificationIsGameAndSexScoped()
    {
        var cases = new[]
        {
            (PlayerHairSex.Male, "Mhk", "hmm-mhk"),
            (PlayerHairSex.Male, "PROCustomBraids01", "hmm-braids"),
            (PlayerHairSex.Male, "PROCustomShortAfro", "hmm-short-afro"),
            (PlayerHairSex.Female, "Mhk", "hmf-mhk"),
            (PlayerHairSex.Female, "PROCustomBraids01", "hmf-braids"),
            (PlayerHairSex.Female, "PROCustomShortAfro", "hmf-short-afro")
        };

        foreach (var game in new[] { TextureCatalogGame.LE1, TextureCatalogGame.LE2, TextureCatalogGame.LE3 })
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
                    TextureCatalogGame.LE1, sex, [candidate], copied),
                $"The {sex} Cru-paired mesh was incorrectly classified as requiring its scalp.");
        }
    }

    private static void NpcHmfAfrPrefersNativeMesh()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE1, "HMF", "Cru");
        var variants = new[]
        {
            (Id: "hmf-afr-mesh", Native: "HMF_HIR_Afr_MDL", Custom: "HMF_HIR_PROCustom_Afr_MDL"),
            (Id: "hmf-cte-mesh", Native: "HMF_HIR_Cte_MDL", Custom: "HMF_HIR_PROCustom_Cute_MDL"),
            (Id: "hmf-sxy-mesh", Native: "HMF_HIR_Sxy_MDL", Custom: "HMF_HIR_PROCustom_Sexy_MDL")
        };
        foreach (var variant in variants)
        {
            var native = Mesh(variant.Native, TextureCatalogOrigin.BaseGame, "HMF");
            var custom = Mesh(variant.Custom, TextureCatalogOrigin.BaseGame, "HMF");
            var playerOptions = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1,
                PlayerHairSex.Female, textures, [custom, native]);
            var playerStyle = playerOptions.Single(value => value.Id == variant.Id);
            TestAssert.Equal(variant.Custom, ObjectName(playerStyle.MeshVariants.Single().InstancedPath));

            var npcOptions = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1,
                PlayerHairSex.Female, textures, [custom, native], preferNativeNpcMeshVariants: true);
            var npcStyle = npcOptions.Single(value => value.Id == variant.Id);
            TestAssert.Equal(variant.Native, ObjectName(npcStyle.MeshVariants.Single().InstancedPath));

            var fallbackOptions = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1,
                PlayerHairSex.Female, textures, [custom], preferNativeNpcMeshVariants: true);
            var fallbackStyle = fallbackOptions.Single(value => value.Id == variant.Id);
            TestAssert.Equal(variant.Custom, ObjectName(fallbackStyle.MeshVariants.Single().InstancedPath));
        }
    }

    private static void MeshLockKeepsIndependentScalps()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE1, "HMM", "Cru")
            .Concat(ScalpFamily(TextureCatalogGame.LE1, "HMM", "Afr")).ToArray();
        var mesh = Mesh("HMM_HIR_Fsk_MDL", TextureCatalogOrigin.BaseGame);
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE1, PlayerHairSex.Male,
            textures, [mesh], meshLocked: true);
        TestAssert.True(options.Any(value => value.Id == "hmm-cru" && value.MeshAction == PlayerHairMeshAction.Keep),
            "An independent scalp option must preserve the locked attachment.");
        TestAssert.True(options.Any(value => value.Id == "hmm-afr" && value.MeshAction == PlayerHairMeshAction.Keep),
            "An independent morph-paired scalp remains available under a mesh lock.");
        TestAssert.True(!options.Any(value => value.Id == "hmm-fsk" || value.Id == "hmm-mhk"),
            "Styles that select an attachment or require a paired attachment must be excluded while locked.");
    }

    private static void SskSlotHasThreeEqualOutcomes()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE2, "HMM", "Sar");
        var meshes = new[] { Mesh("HMM_HIR_Ssk_01_MDL", TextureCatalogOrigin.BaseGame),
            Mesh("HMM_HIR_Ssk_02_MDL", TextureCatalogOrigin.BaseGame) };
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE2, PlayerHairSex.Male,
            textures, meshes);
        var slotOptions = options.Where(value => value.Id == "hmm-sar-ssk").ToArray();
        TestAssert.Equal(1, slotOptions.Length);
        var ssk = slotOptions.Single();
        TestAssert.Equal(2, ssk.MeshVariants.Count);
        TestAssert.True(ssk.MeshVariants.Any(value => value.InstancedPath.EndsWith("HMM_HIR_Ssk_01_MDL")),
            "The Ssk style must expose its first mesh variant.");
        TestAssert.True(ssk.MeshVariants.Any(value => value.InstancedPath.EndsWith("HMM_HIR_Ssk_02_MDL")),
            "The Ssk style must expose its second mesh variant.");
        TestAssert.True(ssk.IncludesNoMeshOutcome && ssk.HairMorphNames.Contains("flatTop"),
            "The same weighted slot must expose a no-mesh outcome while retaining Flat Top.");
        TestAssert.True(ssk.HasDedicatedScalpPair && !ssk.RequiresMeshForScalp,
            "Ssk should offer its Sar scalp manually without forcing a mesh in a targeted roll.");
        TestAssert.Equal(PlayerHairMeshAction.Set, ssk.MeshAction);
        TestAssert.True(options.Any(value => value.Id == "hmm-sar" && value.HairMorphNames.Count == 0 &&
                                             value.MeshAction == PlayerHairMeshAction.Clear),
            "The independent Sar-only scalp outcome must have no Flat Top morph.");
    }

    private static void OfficialIdentitySurvivesOverride()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE3, "HMM", "Cru");
        var baseOccurrence = new AttachmentMeshOccurrence("BIOG_HMM_HIR_PRO.pcc", "HMM_HIR_Fsk_MDL",
            41, 1, TextureCatalogOrigin.BaseGame, 28);
        var officialDlcOccurrence = new AttachmentMeshOccurrence("DLC_CON_Hair/BIOG_HMM_HIR_PRO.pcc",
            "HMM_HIR_Fsk_MDL", 43, 300, TextureCatalogOrigin.OfficialDlc, 28);
        var modOccurrence = new AttachmentMeshOccurrence("DLC_MOD_Fsk.pcc", "HMM_HIR_Fsk_MDL",
            9, 9000, TextureCatalogOrigin.Mod, 28);
        var canonicalPath = TextureCatalogPicker.CanonicalPath(
            baseOccurrence.InstancedPath, baseOccurrence.PackagePath);
        var mesh = new AttachmentMeshCandidate(canonicalPath,
            officialDlcOccurrence, [baseOccurrence, officialDlcOccurrence, modOccurrence])
        { IsEligibleOfficialDefault = true };
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE3, PlayerHairSex.Male,
            textures, [mesh]);
        var identity = options.Single(value => value.Id == "hmm-fsk").MeshVariants.Single();
        TestAssert.Equal(officialDlcOccurrence.PackagePath, identity.PackagePath);
        TestAssert.Equal(canonicalPath, identity.InstancedPath);
        TestAssert.Equal(43, identity.UIndex);

        var unprojectedOptions = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE3,
            PlayerHairSex.Male, textures, [mesh with { IsEligibleOfficialDefault = false }]);
        TestAssert.True(unprojectedOptions.All(value => value.Id != "hmm-fsk"),
            "A physical mesh without eligible official-default provenance became an automatic hair variant.");
    }

    private static TextureCatalogCandidate[] ScalpFamily(TextureCatalogGame game, string sex, string stem)
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

    private static TextureCatalogCandidate[] ScalpTexture(TextureCatalogGame game, string name)
    {
        var occurrence = new TextureCatalogOccurrence($"BIOG_{name.Split('_')[0]}_HIR_PRO.pcc", 22,
            0, TextureCatalogOrigin.BaseGame, 256, 256, "PF_DXT5", "Character", false, null);
        return [new TextureCatalogCandidate(game, $"BIOG_{name.Split('_')[0]}_HIR_PRO.Hair.{name}", occurrence, [occurrence])
        { IsEligibleOfficialDefault = true }];
    }

    private static AttachmentMeshCandidate Mesh(string objectName, TextureCatalogOrigin origin,
        string sex = "HMM")
    {
        var occurrence = new AttachmentMeshOccurrence($"BIOG_{sex}_HIR_PRO.pcc", $"Hair.{objectName}",
            7, 1, origin, 28);
        return new AttachmentMeshCandidate($"BIOG_{sex}_HIR_PRO.Hair.{objectName}", occurrence, [occurrence])
        { IsEligibleOfficialDefault = origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc };
    }

    private static string ObjectName(string path) => path[(path.LastIndexOf('.') + 1)..];
}
