using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.Tests;

/// <summary>Protects the detached catalogue ordering rules from package and WPF concerns.</summary>
public static class TextureCatalogTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("texture catalogue: active texture ranks before profile matches", ActiveTextureRanksFirst),
        new("texture catalogue: profile matches precede shared and general textures", ProfileMatchesRankBeforeSharedAndGeneral),
        new("texture catalogue: search matches path package and origin", SearchMatchesUserFacingProvenance),
        new("texture catalogue: duplicate paths keep the highest mounted occurrence", DuplicatePathsKeepEffectiveOccurrence),
        new("texture catalogue: projector filters paths and verifies exact Texture2D exports", ProjectorFiltersAndVerifies)
    ];

    private static void ActiveTextureRanksFirst()
    {
        var profile = new TextureCatalogProfile("le3-asari", ["ASA_HED"], ["ASA_EYE"]);
        var active = Candidate("BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add2", "base.pcc");
        var preferred = Candidate("BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1", "base.pcc");

        var ranked = TextureCatalogSearch.FilterAndRank([preferred, active], profile, active.InstancedPath, string.Empty);

        TestAssert.Equal(active.InstancedPath, ranked[0].InstancedPath);
    }

    private static void ProfileMatchesRankBeforeSharedAndGeneral()
    {
        var profile = new TextureCatalogProfile("le3-asari", ["ASA_HED"], ["ASA_EYE"]);
        var general = Candidate("BIOG_TUR_HED_PROMorph_R.Adds.TUR_HED_PRO_Add1", "turian.pcc");
        var shared = Candidate("BIOG_ASA_EYE.ASA_EYE_Diff", "eyes.pcc");
        var preferred = Candidate("BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1", "asari.pcc");

        var ranked = TextureCatalogSearch.FilterAndRank([general, shared, preferred], profile, null, string.Empty);

        TestAssert.True(ranked.Select(candidate => candidate.InstancedPath).SequenceEqual(
            [preferred.InstancedPath, shared.InstancedPath, general.InstancedPath]),
            "Profile ranking did not keep preferred, shared, and general texture candidates in that order.");
    }

    private static void SearchMatchesUserFacingProvenance()
    {
        var profile = new TextureCatalogProfile("le3-asari", ["ASA_HED"], []);
        var modded = Candidate(
            "BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Custom",
            "DLC_MOD_Custom\\CookedPCConsole\\ModdedTextures.pcc",
            TextureCatalogOrigin.Mod);
        var vanilla = Candidate("BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1", "BioG_Asa.pcc");

        var byObjectName = TextureCatalogSearch.FilterAndRank([vanilla, modded], profile, null, "custom");
        var byPackage = TextureCatalogSearch.FilterAndRank([vanilla, modded], profile, null, "moddedtextures");
        var byOrigin = TextureCatalogSearch.FilterAndRank([vanilla, modded], profile, null, "mod");

        TestAssert.Equal(modded.InstancedPath, byObjectName.Single().InstancedPath);
        TestAssert.Equal(modded.InstancedPath, byPackage.Single().InstancedPath);
        TestAssert.Equal(modded.InstancedPath, byOrigin.Single().InstancedPath);
    }

    private static void DuplicatePathsKeepEffectiveOccurrence()
    {
        var path = "BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1";
        var baseOccurrence = Occurrence("base.pcc", mountPriority: 0, TextureCatalogOrigin.BaseGame);
        var modOccurrence = Occurrence("DLC_MOD_Custom\\CookedPCConsole\\mod.pcc", mountPriority: 9021, TextureCatalogOrigin.Mod);
        var candidate = new TextureCatalogCandidate(
            TextureCatalogGame.LE3,
            path,
            modOccurrence,
            [baseOccurrence, modOccurrence]);

        TestAssert.Equal(modOccurrence, candidate.EffectiveOccurrence);
        TestAssert.Equal(2, candidate.Occurrences.Count);
    }

    private static void ProjectorFiltersAndVerifies()
    {
        const string preferredPath = "BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add1";
        const string sharedEyePath = "BIOG_ASA_EYE.ASA_EYE_Diff";
        var profile = new TextureCatalogProfile("le3-asari", ["ASA_HED"], ["ASA_EYE"]);
        var resolver = new FakeOccurrenceResolver(new Dictionary<(string Package, string Path), TextureCatalogOccurrence>
        {
            [("base.pcc", preferredPath)] = Occurrence("base.pcc", 0, TextureCatalogOrigin.BaseGame),
            [("mod.pcc", preferredPath)] = Occurrence("mod.pcc", 9000, TextureCatalogOrigin.Mod),
            [("eye.pcc", sharedEyePath)] = Occurrence("eye.pcc", 0, TextureCatalogOrigin.BaseGame)
        });
        var entries = new[]
        {
            new TextureCatalogIndexEntry(preferredPath, ["base.pcc", "mod.pcc"]),
            new TextureCatalogIndexEntry(sharedEyePath, ["eye.pcc"]),
            new TextureCatalogIndexEntry("BIOG_ASA_HED_PROMorph_R.Adds.NotATexture", ["invalid.pcc"]),
            new TextureCatalogIndexEntry("Materials.Anamorphic", ["irrelevant.pcc"])
        };

        var candidates = TextureCatalogProjector.Project(MorphFaceGame.LE3, profile, entries, resolver);

        TestAssert.Equal(2, candidates.Count);
        var preferred = candidates.Single(candidate => candidate.InstancedPath == preferredPath);
        TestAssert.Equal("mod.pcc", preferred.EffectiveOccurrence.PackagePath);
        TestAssert.Equal(2, preferred.Occurrences.Count);
        TestAssert.True(candidates.Any(candidate => candidate.InstancedPath == sharedEyePath),
            "A profile-declared shared eye texture was not admitted to the compact catalogue.");
    }

    private static TextureCatalogCandidate Candidate(
        string path,
        string packagePath,
        TextureCatalogOrigin origin = TextureCatalogOrigin.BaseGame) => new(
        TextureCatalogGame.LE3,
        path,
        Occurrence(packagePath, 0, origin),
        [Occurrence(packagePath, 0, origin)]);

    private static TextureCatalogOccurrence Occurrence(
        string packagePath,
        int mountPriority,
        TextureCatalogOrigin origin) => new(
        packagePath,
        ExportUIndex: 42,
        MountPriority: mountPriority,
        Origin: origin,
        Width: 1024,
        Height: 1024,
        PixelFormat: "PF_DXT1",
        TextureGroup: "TEXTUREGROUP_Character",
        HasExternalMips: false,
        TextureFileCacheName: null);

    private sealed class FakeOccurrenceResolver(
        IReadOnlyDictionary<(string Package, string Path), TextureCatalogOccurrence> occurrences) : ITextureCatalogOccurrenceResolver
    {
        public bool TryResolve(
            MorphFaceGame game,
            string packagePath,
            string instancedPath,
            out TextureCatalogOccurrence occurrence) =>
            occurrences.TryGetValue((packagePath, instancedPath), out occurrence!);
    }
}
