using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

public static class PlayerFacialDetailTexturePolicyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player detail pools separate HMM brows from beards and exclude NPC additions", HmmPoolsAreSeparated),
        new("Player detail pool uses HMF brows and never invents HED_Addn", HmfSupportsOnlyBrow),
        new("Player detail selection requires base-game candidates and uses their exact identity", BaseGameOccurrenceWins),
        new("Nested Player detail paths retain exact package-local identities", NestedPathsRemainExactIdentities),
        new("Player detail parameters no-op independently when a pool is unavailable", ParametersNoOpIndependently),
        new("Player detail selection no-ops for unsupported profiles", UnsupportedProfileIsNoOp)
    ];

    private static void HmmPoolsAreSeparated()
    {
        var candidates = new[]
        {
            Candidate(TextureCatalogGame.LE2,
                "BIOG_HMM_HED_PROMorph.Beard.Custom.HMM_HED_PROCustom_Add1"),
            Candidate(TextureCatalogGame.LE2,
                "BIOG_HMM_HED_PROMorph.Beard.Custom.HMM_HED_PROCustom_Stubble"),
            Candidate(TextureCatalogGame.LE2,
                "BIOG_HMM_HED_PROMorph.Brow.HMM_HED_PROCustom_ArchedBrow"),
            Candidate(TextureCatalogGame.LE2,
                "BIOG_HMM_HED_PROMorph.Brow.HMM_HED_PROCustom_Bushybrow"),
            // NPC combined addition texture: same general face category, but not a Player beard.
            Candidate(TextureCatalogGame.LE2,
                "BIOG_HMM_HED_PROMorph.Addn.HMM_HED_Addn_012"),
            // A Player brow texture must not leak into the beard slot.
            Candidate(TextureCatalogGame.LE2,
                "BIOG_HMM_HED_PROMorph.Brow.HMM_HED_PROCustom_ArchedBrow", "HMF"),
            // Likewise, a correctly named beard in another folder is not eligible.
            Candidate(TextureCatalogGame.LE2,
                "BIOG_HMM_HED_PROMorph.Addn.HMM_HED_PROCustom_Add1")
        };
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Addn"] = "existing beard",
            ["HED_Brow"] = "existing brow"
        };

        var proposal = PlayerFacialDetailTexturePolicy.CreateProposal(
            "le2-human-male", current, candidates, 123);

        TestAssert.Equal(2, proposal.Count);
        TestAssert.Equal("HED_Addn", proposal["HED_Addn"].ParameterName);
        TestAssert.True(proposal["HED_Addn"].Candidate.ObjectName.StartsWith(
            "HMM_HED_PROCustom_", StringComparison.OrdinalIgnoreCase),
            "The HMM beard roll did not select a Player beard texture.");
        TestAssert.True(proposal["HED_Addn"].Candidate.InstancedPath.Contains(
            ".Beard.", StringComparison.OrdinalIgnoreCase),
            "The HMM addition slot selected a texture outside the Player beard category.");
        TestAssert.True(proposal["HED_Brow"].Candidate.InstancedPath.Contains(
            ".Brow.", StringComparison.OrdinalIgnoreCase),
            "The HMM brow slot selected a texture outside the Player brow category.");
        TestAssert.True(!proposal.Values.Any(value => value.Candidate.ObjectName.Contains(
            "HED_Addn_", StringComparison.OrdinalIgnoreCase)),
            "The Player detail pools included an NPC combined addition texture.");
    }

    private static void HmfSupportsOnlyBrow()
    {
        var candidates = new[]
        {
            Candidate(TextureCatalogGame.LE3,
                "BIOG_HMF_HED_PROMorph_R.Brow.HMF_HED_PROCustom_AngularBrow", "HMF"),
            Candidate(TextureCatalogGame.LE3,
                "BIOG_HMF_HED_PROMorph_R.Beard.HMF_HED_PROCustom_Add1", "HMF"),
            Candidate(TextureCatalogGame.LE3,
                "BIOG_HMF_HED_PROMorph_R.Brow.HMM_HED_PROCustom_ArchedBrow", "HMF")
        };
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Brow"] = "existing makeup/brow",
            // This isn't a parameter on the HMF Player material and must be ignored.
            ["HED_Addn"] = "should remain outside the proposal"
        };

        var proposal = PlayerFacialDetailTexturePolicy.CreateProposal(
            "le3-human-female", current, candidates, 456);

        TestAssert.Equal(1, proposal.Count);
        TestAssert.True(proposal.ContainsKey("HED_Brow"),
            "The HMF Player brow parameter was not randomized.");
        TestAssert.Equal("HMF_HED_PROCustom_AngularBrow", proposal["HED_Brow"].Candidate.ObjectName);
    }

    private static void BaseGameOccurrenceWins()
    {
        var baseGame = Occurrence("BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 17);
        var mod = Occurrence("AThirdPartyHairMod.pcc", TextureCatalogOrigin.Mod, 99);
        var candidate = new TextureCatalogCandidate(
            TextureCatalogGame.LE1,
            "BIOG_HMM_HED_PROMorph.Beard.Custom.HMM_HED_PROCustom_5oclock",
            mod,
            [mod, baseGame]);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Addn"] = "existing"
        };

        var proposal = PlayerFacialDetailTexturePolicy.CreateProposal(
            "le1-human-male", current, [candidate], 789);

        var selected = proposal["HED_Addn"];
        TestAssert.Equal(baseGame.PackagePath, selected.Asset.PackagePath);
        TestAssert.Equal(baseGame.ExportUIndex, selected.Asset.UIndex);
        TestAssert.Equal("Texture2D", selected.Asset.ClassName);
        TestAssert.Equal(TextureCatalogOrigin.BaseGame, selected.Candidate.EffectiveOccurrence.Origin);
    }

    private static void ParametersNoOpIndependently()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Addn"] = "existing beard",
            ["HED_Brow"] = "existing brow"
        };
        var onlyBrow = Candidate(TextureCatalogGame.LE2,
            "BIOG_HMM_HED_PROMorph.Brow.HMM_HED_PROCustom_ThickBrow");

        var proposal = PlayerFacialDetailTexturePolicy.CreateProposal(
            "le2-human-male", current, [onlyBrow], 321);

        TestAssert.Equal(1, proposal.Count);
        TestAssert.True(proposal.ContainsKey("HED_Brow"),
            "A brow candidate should still be rolled when the beard pool is empty.");
        TestAssert.True(!proposal.ContainsKey("HED_Addn"),
            "An empty beard pool should leave HED_Addn unchanged.");
    }

    private static void NestedPathsRemainExactIdentities()
    {
        const string package = "BIOG_HMM_HED_PROMorph.pcc";
        const string beardPath = "Beard.Custom.HMM_HED_PROCustom_ChinStrap";
        const string browPath = "Brow.HMM_HED_PROCustom_FatBrow";
        var beardOccurrence = Occurrence(package, TextureCatalogOrigin.BaseGame, 81);
        var browOccurrence = Occurrence(package, TextureCatalogOrigin.BaseGame, 82);
        var proposal = PlayerFacialDetailTexturePolicy.CreateProposal(
            "le2-human-male",
            new Dictionary<string, string> { ["HED_Addn"] = "current beard", ["HED_Brow"] = "current brow" },
            [
                new TextureCatalogCandidate(TextureCatalogGame.LE2, beardPath, beardOccurrence, [beardOccurrence]),
                new TextureCatalogCandidate(TextureCatalogGame.LE2, browPath, browOccurrence, [browOccurrence])
            ],
            1337);

        foreach (var (parameter, expectedPath, expectedOccurrence) in new[]
                 {
                     ("HED_Addn", beardPath, beardOccurrence),
                     ("HED_Brow", browPath, browOccurrence)
                 })
        {
            var selected = proposal[parameter];
            TestAssert.Equal(expectedPath, selected.Asset.InstancedPath);
            TestAssert.Equal(expectedOccurrence.PackagePath, selected.Asset.PackagePath);
            TestAssert.Equal(expectedOccurrence.ExportUIndex, selected.Asset.UIndex);
            TestAssert.Equal(expectedPath, selected.Candidate.InstancedPath);
            TestAssert.Equal(expectedOccurrence.PackagePath, selected.Candidate.EffectiveOccurrence.PackagePath);
            TestAssert.Equal(expectedOccurrence.ExportUIndex, selected.Candidate.EffectiveOccurrence.ExportUIndex);
            var registryIdentity = new MorphFaceEditor.Core.Domain.AssetIdentity(
                expectedOccurrence.PackagePath, expectedPath, expectedOccurrence.ExportUIndex, "Texture2D");
            var option = new MaterialTextureOption(
                new PackageAssetListItem(registryIdentity), RegistryCandidate: selected.Candidate);
            TestAssert.True(option.MatchesIdentity(selected.Asset),
                $"The {parameter} selection did not match the same registry option used by ResolveReferenceAsync.");
        }
    }

    private static void UnsupportedProfileIsNoOp()
    {
        var proposal = PlayerFacialDetailTexturePolicy.CreateProposal(
            "le2-asari",
            new Dictionary<string, string> { ["HED_Brow"] = "existing" },
            [Candidate(TextureCatalogGame.LE2,
                "BIOG_HMF_HED_PROMorph_R.Brow.HMF_HED_PROCustom_AngularBrow", "HMF")],
            5);

        TestAssert.Equal(0, proposal.Count);
    }

    private static TextureCatalogCandidate Candidate(
        TextureCatalogGame game,
        string path,
        string sex = "HMM")
    {
        var occurrence = Occurrence(sex == "HMF"
            ? "BIOG_HMF_HED_PROMorph_R.pcc"
            : "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 7);
        return new TextureCatalogCandidate(game, path, occurrence, [occurrence]);
    }

    private static TextureCatalogOccurrence Occurrence(
        string package,
        TextureCatalogOrigin origin,
        int exportUIndex) =>
        new(package, exportUIndex, 0, origin, 1024, 1024, "PF_DXT5", "TEXTUREGROUP_Character",
            false, null);
}
