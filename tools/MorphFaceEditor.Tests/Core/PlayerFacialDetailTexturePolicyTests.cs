using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

public static class PlayerFacialDetailTexturePolicyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player detail selection uses the highest eligible official identity under mod override", OfficialOccurrenceWins),
        new("Nested Player detail paths retain exact package-local identities", NestedPathsRemainExactIdentities),
        new("Player detail parameters no-op independently when a pool is unavailable", ParametersNoOpIndependently),
    ];

    private static void OfficialOccurrenceWins()
    {
        var baseGame = Occurrence("BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 17);
        var officialDlc = new TextureCatalogOccurrence(
            "DLC_CON_HMM_HED_PROMorph\\CookedPCConsole\\BIOG_HMM_HED_PROMorph.pcc",
            29, 450, TextureCatalogOrigin.OfficialDlc, 1024, 1024, "PF_DXT5",
            "TEXTUREGROUP_Character", false, null);
        var mod = Occurrence("AThirdPartyHairMod.pcc", TextureCatalogOrigin.Mod, 99);
        var candidate = new TextureCatalogCandidate(
            MorphFaceGame.LE1,
            "BIOG_HMM_HED_PROMorph.Beard.Custom.HMM_HED_PROCustom_5oclock",
            officialDlc,
            [mod, baseGame, officialDlc])
        { IsEligibleOfficialDefault = true };
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Addn"] = "existing"
        };

        var proposal = PlayerFacialDetailTexturePolicy.CreateProposal(
            "le1-human-male", current, [candidate], 789);

        var selected = proposal["HED_Addn"];
        TestAssert.Equal(officialDlc.PackagePath, selected.Asset.PackagePath);
        TestAssert.Equal(officialDlc.ExportUIndex, selected.Asset.UIndex);
        TestAssert.Equal("Texture2D", selected.Asset.ClassName);
        TestAssert.Equal(TextureCatalogOrigin.OfficialDlc, selected.Candidate.EffectiveOccurrence.Origin);

        var unprojected = PlayerFacialDetailTexturePolicy.CreateProposal(
            "le1-human-male", current, [candidate with { IsEligibleOfficialDefault = false }], 789);
        TestAssert.Equal(0, unprojected.Count);
    }

    private static void ParametersNoOpIndependently()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HED_Addn"] = "existing beard",
            ["HED_Brow"] = "existing brow"
        };
        var onlyBrow = Candidate(MorphFaceGame.LE2,
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
                new TextureCatalogCandidate(MorphFaceGame.LE2, beardPath, beardOccurrence, [beardOccurrence])
                    { IsEligibleOfficialDefault = true },
                new TextureCatalogCandidate(MorphFaceGame.LE2, browPath, browOccurrence, [browOccurrence])
                    { IsEligibleOfficialDefault = true }
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

    private static TextureCatalogCandidate Candidate(
        MorphFaceGame game,
        string path,
        string sex = "HMM")
    {
        var occurrence = Occurrence(sex == "HMF"
            ? "BIOG_HMF_HED_PROMorph_R.pcc"
            : "BIOG_HMM_HED_PROMorph.pcc", TextureCatalogOrigin.BaseGame, 7);
        return new TextureCatalogCandidate(game, path, occurrence, [occurrence])
        {
            IsEligibleOfficialDefault = true
        };
    }

    private static TextureCatalogOccurrence Occurrence(
        string package,
        TextureCatalogOrigin origin,
        int exportUIndex) =>
        new(package, exportUIndex, 0, origin, 1024, 1024, "PF_DXT5", "TEXTUREGROUP_Character",
            false, null);
}
