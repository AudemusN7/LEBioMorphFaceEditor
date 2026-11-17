using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

public static class PlayerMorphConversionGeometryTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("player conversion accepts reproducible LOD0 with bone edits", AcceptsCanonicalBake),
        new("player conversion ignores absent or incompatible optional lower LODs", AcceptsOptionalLowerLods),
        new("player conversion rejects LOD0 residual and unresolved active features", RejectsResidualAndUnknown),
        new("player conversion rejects nonfinite or malformed LOD0 data", RejectsInvalidData)
    ];

    private static readonly IReadOnlySet<string> Metadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Alias"] = "Target"
    };

    private static (SkeletalMeshAsset Mesh, MorphTargetAsset Target, MorphFaceDocument Face) Fixture()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var target = new MorphTargetAsset(TestFixtures.CreateIdentity("Target", "MorphTarget"),
        [
            new MorphTargetLod(0, 3, [new MorphVertexDelta(0, Vector3.UnitX, Vector3.Zero)]),
            new MorphTargetLod(1, 3, [new MorphVertexDelta(1, Vector3.UnitY, Vector3.Zero)])
        ], []);
        var weighted = new[] { new WeightedMorphTarget("Alias", target, 0.5f) };
        var baked = mesh.Lods.Select(lod => SparseMorphEvaluator.EvaluatePositions(lod.Positions, weighted, lod.LodIndex)).ToArray();
        var face = new MorphFaceDocument(TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)), mesh.Source, null,
            [new MorphFeatureValue("Alias", 0.5f), new MorphFeatureValue("UnknownZero", 0)],
            [new BoneTranslation("root", new Vector3(10, 20, 30))], MorphFaceMaterialOverrides.Empty, baked, []);
        return (mesh, target, face);
    }

    private static PlayerMorphConversionGeometryResult Check(MorphFaceDocument face, SkeletalMeshAsset mesh, params MorphTargetAsset[] targets) =>
        PlayerMorphConversionGeometry.Check(face, mesh, targets, Metadata, Aliases);

    private static void AcceptsCanonicalBake()
    {
        var (mesh, target, face) = Fixture();
        var lod0Only = face with { BakedLods = [face.BakedLods[0]] };
        var before = lod0Only.BakedLods[0].ToArray();
        TestAssert.True(Check(lod0Only, mesh, target).IsSupported, "Exact LOD0 with independent bone edits was rejected.");
        TestAssert.True(before.SequenceEqual(lod0Only.BakedLods[0]), "The guard modified authored positions.");
    }

    private static void AcceptsOptionalLowerLods()
    {
        var (mesh, target, face) = Fixture();
        var malformedLower = face with { BakedLods = [face.BakedLods[0], [new Vector3(float.PositiveInfinity, 0, 0)]] };
        var malformedTarget = target with { Lods = [target.Lods[0], new MorphTargetLod(1, 3, [new MorphVertexDelta(4, Vector3.One, Vector3.Zero)])] };
        TestAssert.True(Check(malformedLower, mesh, malformedTarget).IsSupported,
            "Optional incompatible lower-LOD data blocked conversion.");
        var residual = face with { BakedLods = [face.BakedLods[0], face.BakedLods[1].ToArray()] };
        residual.BakedLods[1][0] += new Vector3(0, 0.001f, 0);
        TestAssert.True(Check(residual, mesh, target).IsSupported, "An optional lower-LOD residual blocked conversion.");
    }

    private static void RejectsResidualAndUnknown()
    {
        var (mesh, target, face) = Fixture();
        var unknown = face with { MorphFeatures = [new MorphFeatureValue("Unknown", 1)] };
        TestAssert.Equal(PlayerMorphConversionGeometryReason.UnresolvedActiveFeature, Check(unknown, mesh, target).Reason);
        face.BakedLods[0][0] += new Vector3(0, 0.001f, 0);
        var result = Check(face, mesh, target);
        TestAssert.Equal(PlayerMorphConversionGeometryReason.GeometryMismatch, result.Reason);
        TestAssert.Equal<int?>(0, result.LodIndex);
    }

    private static void RejectsInvalidData()
    {
        var (mesh, target, face) = Fixture();
        var invalidWeight = face with { MorphFeatures = [new MorphFeatureValue("Alias", float.NaN)] };
        TestAssert.Equal(PlayerMorphConversionGeometryReason.InvalidFeature, Check(invalidWeight, mesh, target).Reason);
        var invalidTarget = target with { Lods = [new MorphTargetLod(0, 3, [new MorphVertexDelta(4, Vector3.One, Vector3.Zero)])] };
        TestAssert.Equal(PlayerMorphConversionGeometryReason.InvalidTargetLod, Check(face, mesh, invalidTarget).Reason);
        var missingTargetLod = target with { Lods = [target.Lods[1]] };
        TestAssert.Equal(PlayerMorphConversionGeometryReason.InvalidTargetLod, Check(face, mesh, missingTargetLod).Reason);
        var missing = face with { BakedLods = [] };
        TestAssert.Equal(PlayerMorphConversionGeometryReason.MissingOrInconsistentLod, Check(missing, mesh, target).Reason);
        var wrongCount = face with { BakedLods = [[Vector3.Zero]] };
        TestAssert.Equal(PlayerMorphConversionGeometryReason.MissingOrInconsistentLod, Check(wrongCount, mesh, target).Reason);
        face.BakedLods[0][0] = new Vector3(float.PositiveInfinity, 0, 0);
        TestAssert.Equal(PlayerMorphConversionGeometryReason.NonFiniteGeometry, Check(face, mesh, target).Reason);
    }
}
