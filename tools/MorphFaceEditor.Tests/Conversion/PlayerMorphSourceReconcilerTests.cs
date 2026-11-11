using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

internal static class PlayerMorphSourceReconcilerTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("player source recovers independent native shapes and preserves authored pose", RecoversShapesAndPose),
        new("player source preserves ambiguous and bone-only prior weights", PreservesUnobservableWeights),
        new("player source rejects unexplained and malformed bakes", RejectsUnexplainedBake),
        new("player source preserves omitted bone bind pose during recovery", PreservesSparseSkeleton),
        new("player source recovers native geometry without double-counting legacy aliases", RecoversLegacyAliases)
    ];

    private static readonly MorphFaceProfile Profile = new("LE1_HMF", MorphFaceGame.LE1,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "HMF", _ => true, (_, _) => true);

    private static MorphTargetAsset Target(string name, int vertex, Vector3 delta, params MorphTargetBoneOffset[] bones) =>
        new(TestFixtures.CreateIdentity(name, "MorphTarget"),
            [new MorphTargetLod(0, 3, delta == Vector3.Zero ? [] : [new MorphVertexDelta(vertex, delta, Vector3.Zero)])], bones);

    private static MorphFaceDocument Face(SkeletalMeshAsset mesh, MorphTargetAsset[] targets, float[] weights,
        MorphFeatureValue[] prior, BoneTranslation[] bones)
    {
        var weighted = targets.Select((target, i) => new WeightedMorphTarget(target.Source.InstancedPath, target, weights[i])).ToArray();
        var bake = SparseMorphEvaluator.EvaluatePositions(mesh.Positions, weighted, 0);
        return new(TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)), mesh.Source, null,
            prior, bones, MorphFaceMaterialOverrides.Empty, [bake], []);
    }

    private static float Weight(MorphFaceDocument document, string name) => document.GetFeatureOffset(name);
    private static Vector3 Bone(IReadOnlyList<BoneTranslation> bones, string name) =>
        bones.Single(b => b.BoneName.Equals(name, StringComparison.OrdinalIgnoreCase)).Translation;

    private static void RecoversShapesAndPose()
    {
        var mesh = PlayerConversionTestFixtures.MeshWithFaceBones();
        MorphTargetAsset[] targets =
        [
            Target("eyes_Big", 0, new Vector3(0.4f, 0, 0), new MorphTargetBoneOffset("eye", new Vector3(0, 0.2f, 0))),
            Target("nose_BridgeIn", 1, new Vector3(0, 0.3f, 0)),
            Target("cheeks_Wide", 2, new Vector3(0, 0, 0.5f), new MorphTargetBoneOffset("cheek", new Vector3(0.1f, 0, 0)))
        ];
        BoneTranslation[] pose = [new("root", Vector3.Zero), new("eye", new Vector3(1.013f, 2.027f, 2.982f)), new("cheek", new Vector3(-1.018f, 2.011f, 3.023f))];
        var face = Face(mesh, targets, [0.6f, -0.4f, 0.3f], [new("eyes_Big", 0.1f)], pose);
        var originalBake = face.BakedLods[0].ToArray();
        var result = new PlayerMorphSourceReconciler().Reconcile(face, mesh, Profile, targets);
        TestAssert.True(result.IsSupported && result.RecoveredWeights, "Native shape residuals should be recovered.");
        TestAssert.Equal(3, result.BasisRank);
        var normalized = result.Document!;
        TestAssert.Near(0.6f, Weight(normalized, "eyes_Big"), 0.00001f);
        TestAssert.Near(-0.4f, Weight(normalized, "nose_BridgeIn"), 0.00001f);
        TestAssert.Near(0.3f, Weight(normalized, "cheeks_Wide"), 0.00001f);
        TestAssert.True(originalBake.SequenceEqual(face.BakedLods[0]) && originalBake.SequenceEqual(normalized.BakedLods[0]), "Authored bake changed.");
        TestAssert.Equal(1, face.MorphFeatures.Count);
        var session = new MorphFaceEditingSession(normalized, mesh, targets, Profile.MetadataOnlyFeatures, aliases: Profile.FeatureAliases);
        foreach (var bone in pose) TestAssert.Near(bone.Translation, Bone(session.Evaluation.FinalSkeleton, bone.BoneName), 0.00001f);
        TestAssert.True(originalBake.Zip(session.Evaluation.Geometry.Positions).All(p => Vector3.Distance(p.First, p.Second) < 0.0001f), "Recovered sliders did not reproduce the source bake.");
    }

    private static void PreservesUnobservableWeights()
    {
        var mesh = PlayerConversionTestFixtures.MeshWithFaceBones();
        MorphTargetAsset[] targets = [Target("eyes_A", 0, Vector3.UnitX), Target("eyes_B", 0, Vector3.UnitX), Target("bone_only", 0, Vector3.Zero, new MorphTargetBoneOffset("eye", Vector3.UnitY))];
        var face = Face(mesh, targets, [1.5f, 2.5f, 0.7f], [new("eyes_A", 0.5f), new("eyes_B", 1.5f), new("bone_only", 0.7f)], []);
        var result = new PlayerMorphSourceReconciler().Reconcile(face, mesh, Profile, targets);
        TestAssert.True(result.IsSupported && result.RecoveredWeights, "Ambiguous shape should remain recoverable.");
        TestAssert.Equal(1, result.BasisRank);
        TestAssert.Near(-1, Weight(result.Document!, "eyes_A") - Weight(result.Document!, "eyes_B"), 0.00001f);
        TestAssert.Near(4, Weight(result.Document!, "eyes_A") + Weight(result.Document!, "eyes_B"), 0.00001f);
        TestAssert.Near(0.7f, Weight(result.Document!, "bone_only"), 0.00001f);
    }

    private static void RejectsUnexplainedBake()
    {
        var mesh = PlayerConversionTestFixtures.MeshWithFaceBones();
        MorphTargetAsset[] targets = [Target("eyes_Big", 0, Vector3.UnitX)];
        var face = Face(mesh, targets, [0.5f], [], []);
        face.BakedLods[0][1] += new Vector3(0, 0.001f, 0);
        var reconciler = new PlayerMorphSourceReconciler();
        var result = reconciler.Reconcile(face, mesh, Profile, targets);
        TestAssert.True(!result.IsSupported, "A sculpt outside target space must be rejected.");
        TestAssert.Equal(PlayerMorphConversionGeometryReason.GeometryMismatch, result.Reason);
        var malformed = face with { BakedLods = [[Vector3.Zero]] };
        TestAssert.Equal(PlayerMorphConversionGeometryReason.MissingOrInconsistentLod, reconciler.Reconcile(malformed, mesh, Profile, targets).Reason);
        var invalid = face with { MorphFeatures = [new("eyes_Big", float.NaN)] };
        TestAssert.Equal(PlayerMorphConversionGeometryReason.InvalidFeature, reconciler.Reconcile(invalid, mesh, Profile, targets).Reason);
        var badTarget = targets[0] with { Lods = [new MorphTargetLod(0, 3, [new MorphVertexDelta(3, Vector3.UnitX, Vector3.Zero)])] };
        var active = face with { MorphFeatures = [new("eyes_Big", 0.5f)] };
        TestAssert.Equal(PlayerMorphConversionGeometryReason.InvalidTargetLod, reconciler.Reconcile(active, mesh, Profile, [badTarget]).Reason);
    }

    private static void RecoversLegacyAliases()
    {
        var mesh = PlayerConversionTestFixtures.MeshWithFaceBones();
        MorphTargetAsset[] targets = [Target("jawNative", 0, Vector3.UnitX), Target("mouthNative", 1, Vector3.UnitY)];
        var profile = new MorphFaceProfile("legacy-test", MorphFaceGame.LE1,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Jaw_Width"] = "jawNative", ["mouthShape_thick"] = "mouthNative"
            }, "HMM", _ => true, (_, _) => true);
        var face = Face(mesh, targets, [0.4f, -0.3f],
            [new("Jaw_Width", 0.4f), new("mouthShape_thick", -0.3f)], []);
        var result = new PlayerMorphSourceReconciler().Reconcile(face, mesh, profile, targets);
        TestAssert.True(result.IsSupported, "Legacy alias bake should resolve in native target space.");
        TestAssert.Near(0.4f, Weight(result.Document!, "jawNative"), 0.00001f);
        TestAssert.Near(-0.3f, Weight(result.Document!, "mouthNative"), 0.00001f);
        TestAssert.True(result.Document!.MorphFeatures.All(f => f.Name != "Jaw_Width" && f.Name != "mouthShape_thick"),
            "Legacy aliases were retained and could double-count the native recipe.");
    }

    private static void PreservesSparseSkeleton()
    {
        var mesh = PlayerConversionTestFixtures.MeshWithFaceBones();
        MorphTargetAsset[] targets = [Target("eyes_Big", 0, Vector3.UnitX, new MorphTargetBoneOffset("eye", new Vector3(0, 0.4f, 0)))];
        var face = Face(mesh, targets, [0.5f], [], [new("cheek", new Vector3(-0.97f, 2.01f, 3.02f))]);
        var result = new PlayerMorphSourceReconciler().Reconcile(face, mesh, Profile, targets);
        TestAssert.True(result.IsSupported && result.RecoveredWeights, "Sparse pose with a recoverable shape should be supported.");
        var normalized = result.Document!;
        TestAssert.Equal(1, face.FinalSkeleton.Count);
        TestAssert.Near(new Vector3(1, 2, 3), Bone(normalized.FinalSkeleton, "eye"), 0.00001f);
        var session = new MorphFaceEditingSession(normalized, mesh, targets, Profile.MetadataOnlyFeatures, aliases: Profile.FeatureAliases);
        TestAssert.Near(new Vector3(0, -0.2f, 0), Bone(session.CaptureSemanticTransferData().AdditiveBoneOffsets, "eye"), 0.00001f);
        TestAssert.Near(new Vector3(1, 2, 3), Bone(session.Evaluation.FinalSkeleton, "eye"), 0.00001f);
        TestAssert.Near(face.FinalSkeleton[0].Translation, Bone(session.Evaluation.FinalSkeleton, "cheek"), 0.00001f);
        TestAssert.Near(Vector3.Zero, Bone(session.Evaluation.FinalSkeleton, "root"), 0.00001f);
    }
}
