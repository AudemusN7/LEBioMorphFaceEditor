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
        new("paired scalp lock lookup requires the resolved matching mesh", PairedScalpLockLookupUsesInstalledPair),
        new("HMF Mom scalp uses its own diffuse and normal with shared mask", HmfMomScalpUsesDedicatedMaps),
        new("official DLC hair styles are eligible but mod-only styles are not", OfficialDlcIsEligibleAndModsAreNot),
        new("locked Player hair mesh retains independent scalp styles only", MeshLockKeepsIndependentScalps),
        new("Ssk slot exposes one style with two meshes and a no-mesh outcome", SskSlotHasThreeEqualOutcomes),
        new("Player hair catalogue uses base-game occurrence identity under mod override", BaseGameIdentitySurvivesOverride)
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
                13, 1, TextureCatalogOrigin.BaseGame, 31)]);
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
        TestAssert.Equal("GBL_ARM_ALL_Norm", ObjectName(mhk.ScalpTextures["HED_Scalp_Tang"]));
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
                occurrence, [occurrence]);
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
            return (Candidate: new AttachmentMeshCandidate(canonicalPath, overrideOccurrence,
                    [overrideOccurrence, baseOccurrence]),
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
        TestAssert.Equal("GBL_ARM_ALL_Norm", ObjectName(mom.ScalpTextures["HED_Scalp_Tang"]));
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
        TestAssert.True(!PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk], mhkPath, null),
            "Mhk scalp alone must not be lock-protected when no mesh is selected.");
        TestAssert.True(!PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk], mhkPath,
                new AssetIdentity("other.pcc", "Other.Mohawk.HMM_HIR_Mhk_MDL", 7, "SkeletalMesh")),
            "A same-named mesh from another package must not be treated as the installed paired attachment.");
        TestAssert.True(!PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk],
                "BIOG_HMM_HIR_PRO_R.CrewCut.HMM_HIR_Cru_Diff", mhkMeshIdentity),
            "Independent scalp styles must remain randomisable under a mesh lock.");
        TestAssert.True(!PlayerHairStyleCatalog.IsCurrentScalpPairedStyle(
                TextureCatalogGame.LE1, PlayerHairSex.Male, textures, [mhk],
                "DLC_MOD_ThirdParty.Hair.HMM_HIR_Mhk_Scalp_Diff", mhkMeshIdentity),
            "A third-party asset with a matching leaf must not be mistaken for the installed paired scalp.");

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

    private static void BaseGameIdentitySurvivesOverride()
    {
        var textures = ScalpFamily(TextureCatalogGame.LE3, "HMM", "Cru");
        var baseOccurrence = new AttachmentMeshOccurrence("BIOG_HMM_HIR_PRO.pcc", "HMM_HIR_Fsk_MDL",
            41, 1, TextureCatalogOrigin.BaseGame, 28);
        var modOccurrence = new AttachmentMeshOccurrence("DLC_MOD_Fsk.pcc", "HMM_HIR_Fsk_MDL",
            9, 20, TextureCatalogOrigin.Mod, 28);
        var canonicalPath = TextureCatalogPicker.CanonicalPath(
            baseOccurrence.InstancedPath, baseOccurrence.PackagePath);
        var mesh = new AttachmentMeshCandidate(canonicalPath,
            modOccurrence, [baseOccurrence, modOccurrence]);
        var options = PlayerHairStyleCatalog.Resolve(TextureCatalogGame.LE3, PlayerHairSex.Male,
            textures, [mesh]);
        var identity = options.Single(value => value.Id == "hmm-fsk").MeshVariants.Single();
        TestAssert.Equal("BIOG_HMM_HIR_PRO.pcc", identity.PackagePath);
        TestAssert.Equal(canonicalPath, identity.InstancedPath);
        TestAssert.Equal(41, identity.UIndex);
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
            return new TextureCatalogCandidate(game, path, occurrence, [occurrence]);
        }).ToArray();
    }

    private static TextureCatalogCandidate[] ScalpTexture(TextureCatalogGame game, string name)
    {
        var occurrence = new TextureCatalogOccurrence($"BIOG_{name.Split('_')[0]}_HIR_PRO.pcc", 22,
            0, TextureCatalogOrigin.BaseGame, 256, 256, "PF_DXT5", "Character", false, null);
        return [new TextureCatalogCandidate(game, $"BIOG_{name.Split('_')[0]}_HIR_PRO.Hair.{name}", occurrence, [occurrence])];
    }

    private static AttachmentMeshCandidate Mesh(string objectName, TextureCatalogOrigin origin,
        string sex = "HMM")
    {
        var occurrence = new AttachmentMeshOccurrence($"BIOG_{sex}_HIR_PRO.pcc", objectName,
            7, 1, origin, 28);
        return new AttachmentMeshCandidate($"BIOG_{sex}_HIR_PRO.Hair.{objectName}", occurrence, [occurrence]);
    }

    private static string ObjectName(string path) => path[(path.LastIndexOf('.') + 1)..];
}
