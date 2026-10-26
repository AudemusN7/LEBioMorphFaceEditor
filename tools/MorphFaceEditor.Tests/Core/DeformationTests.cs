using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

public static class DeformationTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("sparse additive target produces expected positions and normals", SparseTargetMatchesExpected),
        new("authored lower-LOD residual survives slider edits", LowerLodResidualSurvivesSliderEdit),
        new("weighted morph bone offsets update existing and absent final-skeleton bones", MorphTargetsAddAbsentBones),
        new("zero skin weights use influence zero", ZeroSkinWeightsUseInfluenceZero),
        new("invalid dormant targets disable editing before slider use", InvalidDormantTargetDisablesEditing),
        new("editing session exposes available targets but saves them sparsely", AvailableTargetsRemainSparse),
        new("mesh sidecar prior selects the original multi-LOD slider branch", () => MeshPriorPreservesLowerLod(1)),
        new("mesh inverse fit preserves a sparse native LOD index", () => MeshPriorPreservesLowerLod(2)),
    ];

    private static void SparseTargetMatchesExpected()
    {
        var mesh = TestFixtures.CreateMesh();
        var target = TestFixtures.CreateTarget(
            new MorphVertexDelta(1, new Vector3(2, -4, 6), new Vector3(1, 0, 0)));
        var result = SparseMorphEvaluator.Evaluate(mesh, [new WeightedMorphTarget("Nose", target, 0.5f)]);
        TestAssert.Near(new Vector3(2, 0, 6), result.Positions[1], 1e-6f);
        TestAssert.Near(Vector3.Normalize(new Vector3(0.5f, 0, 1)), result.Normals[1], 1e-6f);
        TestAssert.Equal(1, result.AppliedDeltaCount);
    }

    private static void LowerLodResidualSurvivesSliderEdit()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Target", "MorphTarget"),
            [
                new MorphTargetLod(0, 3,
                    [new MorphVertexDelta(0, Vector3.UnitX, Vector3.Zero)]),
                new MorphTargetLod(1, 3,
                    [new MorphVertexDelta(0, Vector3.UnitZ, Vector3.Zero)])
            ],
            []);
        var weighted = new[] { new WeightedMorphTarget("Target", target, 0.5f) };
        var bakedLod0 = SparseMorphEvaluator.EvaluatePositions(
            mesh.Lods[0].Positions, weighted, 0);
        var bakedLod1 = SparseMorphEvaluator.EvaluatePositions(
            mesh.Lods[1].Positions, weighted, 1);
        bakedLod1[0] += new Vector3(0, 2, 0);
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [new MorphFeatureValue("Target", 0.5f)],
            [],
            MorphFaceMaterialOverrides.Empty,
            [bakedLod0, bakedLod1],
            []);
        var session = new MorphFaceEditingSession(face, mesh, [target]);

        TestAssert.True(session.CanEditMorphFeatures, "The lower-LOD residual disabled an otherwise exact profile.");
        TestAssert.Near(bakedLod1[0], session.Evaluation.LodGeometry[1].Positions[0], 0.000001f);
        session.SetFeature("Target", 0.6f);
        var expected = bakedLod1[0] + Vector3.UnitZ * 0.1f;
        TestAssert.Near(expected, session.Evaluation.LodGeometry[1].Positions[0], 0.000001f);
        TestAssert.Near(expected,
            session.CreateDraft(null, MorphFaceMaterialOverrides.Empty).BakedLods[1][0],
            0.000001f);
    }

    private static void MorphTargetsAddAbsentBones()
    {
        var reference = new[]
        {
            new ReferenceBone("root", 0, new Vector3(1, 2, 3), Quaternion.Identity),
            new ReferenceBone("jaw", 0, new Vector3(0, 2, 0), Quaternion.Identity)
        };
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("JawTarget", "MorphTarget"),
            [new MorphTargetLod(0, 3, [])],
            [new MorphTargetBoneOffset("root", new Vector3(2, -4, 6)),
             new MorphTargetBoneOffset("jaw", Vector3.UnitY)]);
        var result = MorphBoneOffsetComposer.Compose(
            reference,
            [new BoneTranslation("root", Vector3.Zero)],
            [new WeightedMorphTarget("JawTarget", target, 0.5f)]);

        TestAssert.Equal(2, result.Count);
        TestAssert.Near(new Vector3(2, 0, 6), result.Single(bone => bone.BoneName == "root").Translation, 1e-6f);
        TestAssert.Near(new Vector3(0, 2.5f, 0), result.Single(bone => bone.BoneName == "jaw").Translation, 1e-6f);
    }

    private static void ZeroSkinWeightsUseInfluenceZero()
    {
        var deformation = new DeformationResult([new Vector3(1, 2, 3)], [Vector3.UnitZ], 0, []);
        var renderData = new SkeletalMeshRenderData(
            [new Vector4(1, 0, 0, 1)],
            [Vector2.Zero],
            [new BoneIndex4(0, 0, 0, 0)],
            [Vector4.Zero],
            [],
            []);
        var result = CpuSkinningEvaluator.Skin(
            deformation,
            renderData,
            [Matrix4x4.CreateTranslation(4, 0, 0)]);

        TestAssert.Near(new Vector3(5, 2, 3), result.Positions[0], 1e-6f);
    }

    private static void InvalidDormantTargetDisablesEditing()
    {
        var mesh = TestFixtures.CreateMesh();
        var invalid = TestFixtures.CreateTarget(
            new MorphVertexDelta(99, Vector3.UnitX, Vector3.Zero));
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            null,
            null,
            [new MorphFeatureValue("Target", 0)],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var session = new MorphFaceEditingSession(face, mesh, [invalid]);

        TestAssert.True(!session.CanEditMorphFeatures, "An invalid zero-weight target remained editable.");
        TestAssert.True(session.ValidationErrors.Count > 0, "The target validation failure was not retained for diagnostics.");
    }

    private static void AvailableTargetsRemainSparse()
    {
        var mesh = TestFixtures.CreateMesh();
        var existing = TestFixtures.CreateTarget();
        var available = new MorphTargetAsset(
            TestFixtures.CreateIdentity("nose_BridgeWide", "MorphTarget"),
            [new MorphTargetLod(0, 3, [new MorphVertexDelta(1, Vector3.UnitX, Vector3.Zero)])],
            []);
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            null,
            null,
            [new MorphFeatureValue("Target", 0)],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var session = new MorphFaceEditingSession(face, mesh, [existing, available]);

        TestAssert.True(session.Features.Any(value => value.Name == "nose_BridgeWide" && value.Offset == 0),
            "An available zero-valued target was omitted from the editing inventory.");
        TestAssert.True(!session.CreateDraft(null, MorphFaceMaterialOverrides.Empty).MorphFeatures
                .Any(value => value.Name == "nose_BridgeWide"),
            "An untouched available target was unnecessarily serialized.");

        session.SetFeature("nose_BridgeWide", 0.5f);
        TestAssert.Near(0.5f, session.CreateDraft(null, MorphFaceMaterialOverrides.Empty)
            .MorphFeatures.Single(value => value.Name == "nose_BridgeWide").Offset, 0);
    }

    private static void MeshPriorPreservesLowerLod(int lowerLodIndex)
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh(lowerLodIndex);
        MorphTargetAsset Target(string name, Vector3 lod1Delta) => new(
            TestFixtures.CreateIdentity(name, "MorphTarget"),
            [
                new MorphTargetLod(0, 3,
                    [new MorphVertexDelta(0, Vector3.UnitX, Vector3.Zero)]),
                new MorphTargetLod(lowerLodIndex, 3,
                    [new MorphVertexDelta(0, lod1Delta, Vector3.Zero)])
            ],
            []);
        var first = Target("First", Vector3.UnitX);
        var second = Target("Second", -Vector3.UnitX);
        var resolved = new[]
        {
            new ResolvedMorphFeature(new MorphFeatureValue("First", 1), first,
                MorphFeatureResolutionKind.DirectTarget, null),
            new ResolvedMorphFeature(new MorphFeatureValue("Second", 0), second,
                MorphFeatureResolutionKind.DirectTarget, null)
        };
        var bakedLod0 = SparseMorphEvaluator.EvaluatePositions(
            mesh.Lods[0].Positions,
            [new WeightedMorphTarget("First", first, 1)],
            0);
        var bakedLod1 = SparseMorphEvaluator.EvaluatePositions(
            mesh.Lods[1].Positions,
            [new WeightedMorphTarget("First", first, 1)],
            lowerLodIndex);
        var prior = new MorphMeshFitPrior(
            mesh.Source.InstancedPath,
            [new MorphFeatureValue("First", 1)],
            [],
            [bakedLod0, bakedLod1]);

        var result = MorphMeshInverter.Fit(
            mesh,
            resolved,
            [new MorphMeshPositionCandidate("identity", bakedLod0, prior)],
            [],
            [bakedLod0, bakedLod1]);

        TestAssert.True(result.UsedSourcePrior, "The matching mesh sidecar prior was ignored.");
        TestAssert.Near(1,
            result.MorphData.MorphFeatures.Single(value => value.Name == "First").Offset,
            0.00001f);
        TestAssert.True(result.MorphData.MorphFeatures.All(value => value.Name != "Second"),
            "The LOD0-equivalent alternate target leaked into the recovered branch.");
        for (var vertex = 0; vertex < bakedLod1.Length; vertex++)
        {
            TestAssert.Near(bakedLod1[vertex], result.MorphData.BakedLods[1][vertex], 0.00001f);
        }
    }
}
