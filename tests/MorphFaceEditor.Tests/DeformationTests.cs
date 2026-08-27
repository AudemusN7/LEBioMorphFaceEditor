using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Diagnostics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

public static class DeformationTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("zero targets preserve the base", ZeroTargetsPreserveBase),
        new("sparse additive target produces expected positions and normals", SparseTargetMatchesExpected),
        new("sparse evaluator applies the requested lower LOD", SparseTargetUsesRequestedLod),
        new("authored lower-LOD residual survives slider edits", LowerLodResidualSurvivesSliderEdit),
        new("comparison reports exact worst vertex", ComparisonReportsWorstVertex),
        new("topology detects baked vertex mismatch", TopologyDetectsBakedMismatch),
        new("out-of-range morph index is rejected", OutOfRangeTargetIsRejected),
        new("Human Male metadata-only features resolve without geometry", MetadataOnlyFeaturesResolve),
        new("morph bone offsets reconstruct local final positions", BoneOffsetsReconstructFinalPositions),
        new("morph targets add previously absent final-skeleton bones", MorphTargetsAddAbsentBones),
        new("reference pose produces an identity skinning palette", ReferencePoseProducesIdentityPalette),
        new("zero skin weights use influence zero", ZeroSkinWeightsUseInfluenceZero),
        new("invalid dormant targets disable editing before slider use", InvalidDormantTargetDisablesEditing),
        new("slider drag history coalesces into one semantic edit", SliderHistoryCoalesces),
        new("editing session exposes available targets but saves them sparsely", AvailableTargetsRemainSparse),
        new("baked mesh inversion recovers exact morph slider weights", MeshInversionRecoversWeights),
        new("mesh sidecar prior selects the original multi-LOD slider branch", MeshPriorPreservesLowerLod)
    ];

    private static void ZeroTargetsPreserveBase()
    {
        var mesh = TestFixtures.CreateMesh();
        var result = SparseMorphEvaluator.Evaluate(mesh, []);
        TestAssert.Equal(mesh.Positions.Length, result.Positions.Length);
        for (var index = 0; index < mesh.Positions.Length; index++)
        {
            TestAssert.Near(mesh.Positions[index], result.Positions[index], 0);
        }
    }

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

    private static void SparseTargetUsesRequestedLod()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Target", "MorphTarget"),
            [
                new MorphTargetLod(0, 3, []),
                new MorphTargetLod(1, 3, [new MorphVertexDelta(1, Vector3.UnitZ, Vector3.UnitY)])
            ],
            []);

        var result = SparseMorphEvaluator.Evaluate(
            mesh,
            [new WeightedMorphTarget("lower", target, 0.5f)],
            1);

        TestAssert.Near(new Vector3(12, 0, 0.5f), result.Positions[1], 1e-6f);
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
            mesh.AvailableLodPositions[0], weighted, 0);
        var bakedLod1 = SparseMorphEvaluator.EvaluatePositions(
            mesh.AvailableLodPositions[1], weighted, 1);
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

        TestAssert.True(session.CanEdit, "The lower-LOD residual disabled an otherwise exact profile.");
        TestAssert.Near(bakedLod1[0], session.Evaluation.LodGeometry[1].Positions[0], 0.000001f);
        session.SetFeature("Target", 0.6f);
        var expected = bakedLod1[0] + Vector3.UnitZ * 0.1f;
        TestAssert.Near(expected, session.Evaluation.LodGeometry[1].Positions[0], 0.000001f);
        TestAssert.Near(expected,
            session.CreateDraft(null, MorphFaceMaterialOverrides.Empty).BakedLods[1][0],
            0.000001f);
    }

    private static void ComparisonReportsWorstVertex()
    {
        var result = new DeformationResult(
            [Vector3.Zero, new Vector3(2, 0, 0), new Vector3(0, 3, 0)],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            0,
            []);
        var report = DeformationComparison.Compare(
            result,
            [Vector3.Zero, new Vector3(1, 0, 0), Vector3.Zero],
            tolerance: 0.5f,
            largestErrorCount: 1);
        TestAssert.Near(3f, report.MaximumError, 1e-6f);
        TestAssert.Equal(2, report.VerticesAboveTolerance);
        TestAssert.Equal(2, report.LargestErrors[0].SourceIndex);
    }

    private static void TopologyDetectsBakedMismatch()
    {
        var mesh = TestFixtures.CreateMesh();
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            null,
            null,
            [],
            [],
            MorphFaceMaterialOverrides.Empty,
            [[Vector3.Zero]],
            []);
        var report = TopologyDiagnostics.Analyze(mesh, face);
        TestAssert.True(!report.IsValid, "Expected invalid topology.");
        TestAssert.True(report.Issues.Any(issue => issue.Code == "FACE_VERTEX_COUNT"), "Expected FACE_VERTEX_COUNT issue.");
    }

    private static void OutOfRangeTargetIsRejected()
    {
        var mesh = TestFixtures.CreateMesh();
        var target = TestFixtures.CreateTarget(new MorphVertexDelta(50, Vector3.One, Vector3.Zero));
        try
        {
            SparseMorphEvaluator.Evaluate(mesh, [new WeightedMorphTarget("Bad", target, 1)]);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new Exception("Expected InvalidDataException.");
    }

    private static void MetadataOnlyFeaturesResolve()
    {
        var target = TestFixtures.CreateTarget();
        var metadataNames = new[]
        {
            "eyes_bagOut", "eyeShape_liara", "cheeks_gaunt",
            "Formal", "None", "Sarge", "Slick",
            "nose_BottomThin", "nose_BottomWide",
            "race_asnOld", "race_asnYoung", "race_blackOld", "race_Blackyng", "race_cauOld", "race_cauYng",
            "teeth_canineExtend", "teeth_Narrow", "teeth_Wide"
        };
        var resolution = new MorphFeatureTargetResolver().Resolve(
            [new MorphFeatureValue("Target", 0.5f), .. metadataNames.Select(name => new MorphFeatureValue(name, 0.65f))],
            [target]);
        TestAssert.Equal(0, resolution.UnresolvedFeatureNames.Count);
        TestAssert.Equal(1, resolution.WeightedTargets.Count);
        TestAssert.True(
            resolution.Features.Skip(1).All(feature => feature.Kind == MorphFeatureResolutionKind.MetadataOnly),
            "A proven Human Male front-end value was treated as a geometry target.");
    }

    private static void BoneOffsetsReconstructFinalPositions()
    {
        var reference = new[] { new ReferenceBone("root", 0, new Vector3(1, 2, 3), Quaternion.Identity) };
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Target", "MorphTarget"),
            [new MorphTargetLod(0, 3, [])],
            [new MorphTargetBoneOffset("root", new Vector3(2, -4, 6))]);
        var result = MorphBoneOffsetComposer.Compose(
            reference,
            [new BoneTranslation("root", Vector3.Zero)],
            [new WeightedMorphTarget("Target", target, 0.5f)]);
        TestAssert.Near(new Vector3(2, 0, 6), result[0].Translation, 1e-6f);
    }

    private static void ReferencePoseProducesIdentityPalette()
    {
        var skeleton = new[]
        {
            new ReferenceBone("root", 0, new Vector3(1, 2, 3), Quaternion.Identity),
            new ReferenceBone("child", 0, new Vector3(0, 4, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f))
        };
        var pose = SkeletalPoseComposer.Compose(
            skeleton,
            skeleton.Select(bone => new BoneTranslation(bone.Name, bone.Position)).ToArray());
        var point = new Vector3(3, -2, 5);
        foreach (var matrix in pose.SkinningMatrices)
        {
            TestAssert.Near(point, Vector3.Transform(point, matrix), 1e-5f);
        }
    }

    private static void MorphTargetsAddAbsentBones()
    {
        var reference = new[]
        {
            new ReferenceBone("root", 0, Vector3.Zero, Quaternion.Identity),
            new ReferenceBone("jaw", 0, new Vector3(0, 2, 0), Quaternion.Identity)
        };
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("JawTarget", "MorphTarget"),
            [new MorphTargetLod(0, 3, [])],
            [new MorphTargetBoneOffset("jaw", new Vector3(0, 0.5f, 0))]);
        var result = MorphBoneOffsetComposer.Compose(
            reference,
            [new BoneTranslation("root", Vector3.Zero)],
            [new WeightedMorphTarget("JawTarget", target, 1)]);

        TestAssert.Equal(2, result.Count);
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

        TestAssert.True(!session.CanEdit, "An invalid zero-weight target remained editable.");
        TestAssert.True(session.ValidationErrors.Count > 0, "The target validation failure was not retained for diagnostics.");
    }

    private static void SliderHistoryCoalesces()
    {
        var mesh = TestFixtures.CreateMesh();
        var target = TestFixtures.CreateTarget(new MorphVertexDelta(1, Vector3.UnitX, Vector3.Zero));
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
        var session = new MorphFaceEditingSession(face, mesh, [target]);
        TestAssert.True(session.CanEdit, "Synthetic session did not pass its zero-weight oracle.");

        session.BeginFeatureEdit("Target");
        session.SetFeature("Target", 0.25f);
        session.SetFeature("Target", 0.5f);
        TestAssert.True(!session.CanUndo, "A drag was committed before it ended.");
        session.EndFeatureEdit("Target");
        TestAssert.True(session.CanUndo, "Completed drag did not produce an undo entry.");
        session.Undo();
        TestAssert.Near(0, session.GetFeature("Target"), 0);
        TestAssert.True(!session.CanUndo && session.CanRedo, "One drag created more than one undo entry.");
        session.Redo();
        TestAssert.Near(0.5f, session.GetFeature("Target"), 0);

        // A virtualized WPF slider can report the old row's pointer-up after a
        // different row has started. The stale completion must be ignored and
        // both drags must remain single, correctly ordered undo entries.
        session.BeginFeatureEdit("Target");
        session.SetFeature("Target", 0.25f);
        session.BeginBoneEdit("root", 0);
        session.SetBoneAxis("root", 0, 0.75f);
        session.EndFeatureEdit("Target");
        session.EndBoneEdit("root", 0);
        session.Undo();
        TestAssert.Near(0, session.GetBoneAxis("root", 0), 0);
        session.Undo();
        TestAssert.Near(0.5f, session.GetFeature("Target"), 0);
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

    private static void MeshInversionRecoversWeights()
    {
        var mesh = TestFixtures.CreateMesh();
        var wide = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Wide", "MorphTarget"),
            [new MorphTargetLod(0, 3,
            [
                new MorphVertexDelta(0, new Vector3(2, 0, 0), Vector3.Zero),
                new MorphVertexDelta(2, new Vector3(0.5f, 0, 0), Vector3.Zero)
            ])],
            []);
        var tall = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Tall", "MorphTarget"),
            [new MorphTargetLod(0, 3,
            [
                new MorphVertexDelta(1, new Vector3(0, 3, 0), Vector3.Zero),
                new MorphVertexDelta(2, new Vector3(0, 1, 0), Vector3.Zero)
            ])],
            []);
        var resolved = new[]
        {
            new ResolvedMorphFeature(new MorphFeatureValue("Wide", 0), wide,
                MorphFeatureResolutionKind.DirectTarget, null),
            new ResolvedMorphFeature(new MorphFeatureValue("Tall", 0), tall,
                MorphFeatureResolutionKind.DirectTarget, null)
        };
        var baked = mesh.Positions.ToArray();
        baked[0] += new Vector3(0.5f, 0, 0);
        baked[1] += new Vector3(0, -1.5f, 0);
        baked[2] += new Vector3(0.125f, -0.5f, 0);

        var result = MorphMeshInverter.Fit(
            mesh,
            resolved,
            [
                new MorphMeshPositionCandidate("wrong", baked.Select(value =>
                    new Vector3(value.X, -value.Y, value.Z)).ToArray()),
                new MorphMeshPositionCandidate("identity", baked)
            ],
            [],
            [mesh.Positions]);

        TestAssert.Equal("identity", result.CoordinateSystem);
        TestAssert.Near(0.25f,
            result.MorphData.MorphFeatures.Single(value => value.Name == "Wide").Offset,
            0.00001f);
        TestAssert.Near(-0.5f,
            result.MorphData.MorphFeatures.Single(value => value.Name == "Tall").Offset,
            0.00001f);
        TestAssert.Near(0, result.MaximumError, 0.00001f);
    }

    private static void MeshPriorPreservesLowerLod()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        MorphTargetAsset Target(string name, Vector3 lod1Delta) => new(
            TestFixtures.CreateIdentity(name, "MorphTarget"),
            [
                new MorphTargetLod(0, 3,
                    [new MorphVertexDelta(0, Vector3.UnitX, Vector3.Zero)]),
                new MorphTargetLod(1, 3,
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
            mesh.AvailableLodPositions[0],
            [new WeightedMorphTarget("First", first, 1)],
            0);
        var bakedLod1 = SparseMorphEvaluator.EvaluatePositions(
            mesh.AvailableLodPositions[1],
            [new WeightedMorphTarget("First", first, 1)],
            1);
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
