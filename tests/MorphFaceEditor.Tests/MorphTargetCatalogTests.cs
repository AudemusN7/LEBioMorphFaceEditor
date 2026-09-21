using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Services;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

public static class MorphTargetCatalogTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("default profiles load morph targets from embedded bundles", DefaultProfilesLoadEmbeddedBundles),
        new("bundled profiles expose authored morph LOD coverage", BundledProfilesExposeAuthoredLodCoverage),
        new("player Custom and Custom CC heads resolve explicit human profiles", PlayerHeadsResolveHumanProfiles),
        new("HMM inert droop metadata remains distinct from the authored droop target", HmmDroopMetadataRemainsDistinct),
        new("Krogan retained race metadata stays hidden and sortable", KroganRaceMetadataStaysHidden)
    ];

    private static void PlayerHeadsResolveHumanProfiles()
    {
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        foreach (var sex in new[] { "HMM", "HMF" })
        {
            var root = sex == "HMM" ? "BIOG_HMM_HED_PROMorph" : "BIOG_HMF_HED_PROMorph_R";
            var suffix = sex == "HMM" ? "human-male" : "human-female";
            var regular = profiles.Find(game, "Player.Imported", $"{root}.Custom.{sex}_HED_PROCustom_MDL");
            TestAssert.Equal($"{game.ToString().ToLowerInvariant()}-{suffix}", regular?.Key);
            var cc = profiles.Find(game, "Player.Imported", $"{root}.Custom.{sex}_HED_PROCustom_MDL_CC");
            if (game == MorphFaceGame.LE3)
            {
                TestAssert.Equal($"le3-{suffix}", cc?.Key);
            }
            else
            {
                TestAssert.Equal<MorphFaceProfile?>(null, cc);
            }
        }
    }

    private static void HmmDroopMetadataRemainsDistinct()
    {
        var catalog = new MorphTargetCatalog();
        foreach (var profile in MorphFaceProfileRegistry.CreateDefault().Profiles
                     .Where(profile => profile.Key.EndsWith("-human-male", StringComparison.Ordinal)))
        {
            TestAssert.True(profile.MetadataOnlyFeatures.Contains("eyeShape_droop"),
                $"{profile.Key} no longer preserves the proven-inert eyeShape_droop creator value as metadata.");

            var authoredTarget = catalog.Load(profile, profile.Game, @"Z:\definitely-not-a-package.pcc")
                .Single(target => target.Source.InstancedPath.Split('.').Last()
                    .Equals("eye_Shape_droop", StringComparison.Ordinal));
            TestAssert.True(authoredTarget.Lods.SelectMany(lod => lod.Vertices)
                    .Any(vertex => vertex.PositionDelta.LengthSquared() > 0),
                $"{profile.Key} eye_Shape_droop unexpectedly lost its authored geometry.");
        }
    }

    private static void KroganRaceMetadataStaysHidden()
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "race_asnOld", "race_asnYoung", "race_blackOld", "race_Blackyng",
            "race_cauOld", "race_cauYng"
        };
        var profiles = MorphFaceProfileRegistry.CreateDefault().Profiles
            .Where(profile => profile.Key is "le1-krogan" or "le2-krogan")
            .ToArray();
        foreach (var profile in profiles)
        {
            TestAssert.True(profile.MetadataOnlyFeatures.SetEquals(expected),
                $"{profile.Key} no longer treats the retained race selectors as metadata-only.");
            foreach (var name in expected)
            {
                var metadata = profile.UiProfile.Describe(
                    new ResolvedMorphFeature(
                        new MorphFeatureValue(name, 0), null,
                        MorphFeatureResolutionKind.MetadataOnly,
                        "stored character-creator metadata"),
                    sessionCanEdit: true);
                TestAssert.True(!metadata.IsVisible && !metadata.IsEditable,
                    $"{profile.Key} exposed retained race selector '{name}' as an editor control.");
                TestAssert.Equal(int.MaxValue, metadata.SortOrder);
            }
        }
    }

    private static void BundledProfilesExposeAuthoredLodCoverage()
    {
        var catalog = new MorphTargetCatalog();
        foreach (var profile in MorphFaceProfileRegistry.CreateDefault().Profiles
                     .Where(profile => !profile.IgnoresAuthoredGeometry))
        {
            var targets = catalog.Load(profile, profile.Game, @"Z:\definitely-not-a-package.pcc");
            TestAssert.True(targets.SelectMany(target => target.Lods)
                    .All(lod => lod.LodIndex < 2 || lod.Vertices.Count == 0),
                $"{profile.Key} unexpectedly acquired authored LOD2 target geometry; revisit the LOD2 editor lockout.");
        }

        var hmf = MorphFaceProfileRegistry.CreateDefault().Profiles.Single(profile => profile.Key == "le3-human-female");
        var lod1Only = catalog.Load(hmf, hmf.Game, @"Z:\definitely-not-a-package.pcc")
            .Where(target => target.BoneOffsets.Count == 0 &&
                             target.Lods.Count(lod => lod.Vertices.Count > 0) == 1 &&
                             target.Lods.Any(lod => lod.LodIndex == 1 && lod.Vertices.Count > 0))
            .ToArray();
        TestAssert.Equal(7, lod1Only.Length);
        var hair = lod1Only.Where(target => target.Source.InstancedPath.Split('.').Last()
            .StartsWith("HAIR_", StringComparison.OrdinalIgnoreCase)).ToArray();
        TestAssert.Equal(6, hair.Length);
        var baseline = hair[0].Lods.Single(lod => lod.LodIndex == 1).Vertices;
        foreach (var target in hair.Skip(1))
        {
            var vertices = target.Lods.Single(lod => lod.LodIndex == 1).Vertices;
            TestAssert.Equal(baseline.Count, vertices.Count);
            var positionDifference = baseline.Zip(vertices, (left, right) =>
                System.Numerics.Vector3.Distance(left.PositionDelta, right.PositionDelta)).Max();
            var normalDifference = baseline.Zip(vertices, (left, right) =>
                System.Numerics.Vector3.Distance(left.NormalDelta, right.NormalDelta)).Max();
            TestAssert.True(baseline.Zip(vertices).All(pair => pair.First.SourceIndex == pair.Second.SourceIndex),
                $"{target.Source.InstancedPath} no longer shares the malformed HAIR source-index set.");
            TestAssert.Near(0, normalDifference, 0);
            TestAssert.True(positionDifference < 0.0002f,
                $"{target.Source.InstancedPath} is no longer equivalent to the malformed HAIR sag target; reassess whether it should remain hidden.");
        }

    }

    private static void DefaultProfilesLoadEmbeddedBundles()
    {
        var expectedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["HMM_BaseMorphSet"] = 155,
            ["HMF_BaseMorphSet"] = 133,
            ["ASA_BaseMorphSet"] = 54,
            ["SAL_BaseMorphSet"] = 35,
            ["TUR_BaseMorphSet"] = 37,
            ["KRO_baseMorphSet"] = 25,
            ["BAT_BaseMorphSet"] = 43,
            ["ALN_ReconstructedMorphSet"] = 17
        };
        var gameSpecificCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["LE2.HMM_BaseMorphSet"] = 156,
            ["LE3.HMM_BaseMorphSet"] = 153,
            ["LE3.HMF_BaseMorphSet"] = 136
        };
        var catalog = new MorphTargetCatalog();

        foreach (var profile in MorphFaceProfileRegistry.CreateDefault().Profiles
                     .Where(profile => !profile.IgnoresAuthoredGeometry))
        {
            var targets = catalog.Load(profile, profile.Game, @"Z:\definitely-not-a-package.pcc");
            var expected = gameSpecificCounts.GetValueOrDefault(
                $"{profile.Game}.{profile.TargetSetName}", expectedCounts[profile.TargetSetName!]);
            TestAssert.Equal(expected, targets.Count);
            TestAssert.True(targets.All(target => target.Source.PackagePath.StartsWith(
                    "embedded://", StringComparison.Ordinal)),
                $"{profile.Key} loaded one or more targets from an external package.");
        }
    }
}
