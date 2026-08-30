using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class MorphTargetCatalogTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("default profiles load morph targets from embedded bundles", DefaultProfilesLoadEmbeddedBundles),
        new("bundled profiles expose authored morph LOD coverage", BundledProfilesExposeAuthoredLodCoverage),
        new("HMM inert droop metadata remains distinct from the authored droop target", HmmDroopMetadataRemainsDistinct)
    ];

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

    private static void BundledProfilesExposeAuthoredLodCoverage()
    {
        var catalog = new MorphTargetCatalog();
        foreach (var profile in MorphFaceProfileRegistry.CreateDefault().Profiles)
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

        foreach (var profile in MorphFaceProfileRegistry.CreateDefault().Profiles)
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
