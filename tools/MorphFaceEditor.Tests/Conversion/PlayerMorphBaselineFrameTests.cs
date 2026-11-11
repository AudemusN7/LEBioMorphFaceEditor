using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

internal static class PlayerMorphBaselineFrameTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("player baseline frame transfers shape and multiple manual bone edits", TransfersEdits),
        new("player baseline frame validates profile and malformed geometry", RejectsInvalid)
    ];

    private static readonly MorphFaceProfile Profile = new("test", MorphFaceGame.LE1,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata" },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["alias"] = "shape" },
        "test", _ => true, (_, _) => true);

    private static MorphTargetAsset Target(float eye, float cheek) => new(TestFixtures.CreateIdentity("shape", "MorphTarget"),
        [new MorphTargetLod(0, 3, [new MorphVertexDelta(0, Vector3.UnitX, Vector3.Zero)])],
        [new MorphTargetBoneOffset("eye", Vector3.UnitY * eye), new MorphTargetBoneOffset("cheek", Vector3.UnitZ * cheek)]);
    private static MorphFaceDocument Template(SkeletalMeshAsset mesh) => new(TestFixtures.CreateIdentity("Face", "BioMorphFace"),
        new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)), mesh.Source, null,
        [], [], MorphFaceMaterialOverrides.Empty, [mesh.Positions.ToArray()], []);
    private static PlayerMorphBaselineFrame Frame(SkeletalMeshAsset mesh, MorphTargetAsset target, float weight, Vector3 baked,
        BoneTranslation[] bones) => PlayerMorphBaselineFrame.Create(new("approved", Profile.Key,
            new MorphFaceMorphData([new("shape", weight), new("alias", 9), new("metadata", 8)], bones,
                [new[] { baked, mesh.Positions[1], mesh.Positions[2] }])), Template(mesh), mesh, Profile, [target]);
    private static Vector3 Bone(IReadOnlyList<BoneTranslation> bones, string name) => bones.Single(b => b.BoneName == name).Translation;
    private static float Feature(IReadOnlyList<MorphFeatureValue> values, string name) => values.Single(f => f.Name == name).Offset;

    private static void TransfersEdits()
    {
        var mesh = PlayerConversionTestFixtures.MeshWithFaceBones();
        var sourceTarget = Target(2, 3);
        var targetTarget = Target(4, 5);
        var source = Frame(mesh, sourceTarget, 0.25f, new Vector3(10, 0, 0),
            [new("eye", new Vector3(1, 4, 3))]); // cheek deliberately omitted: bind is authoritative
        var destination = Frame(mesh, targetTarget, 0.75f, new Vector3(20, 0, 0),
            [new("eye", new Vector3(1, 7, 3)), new("cheek", new Vector3(-1, 2, 8))]);
        TestAssert.Equal(1, source.Document.MorphFeatures.Count);
        TestAssert.Near(new Vector3(-1, 2, 3), Bone(source.Document.FinalSkeleton, "cheek"), 0.00001f);
        var sourceSession = new MorphFaceEditingSession(source.Document, source.EvaluationBase, [sourceTarget], Profile.MetadataOnlyFeatures, aliases: Profile.FeatureAliases);
        TestAssert.Near(new Vector3(10, 0, 0), sourceSession.Evaluation.Geometry.Positions[0], 0.00001f);
        sourceSession.SetFeature("shape", 0.5f);
        sourceSession.SetBoneAxis("eye", 0, sourceSession.GetBoneAxis("eye", 0) + 0.3f);
        sourceSession.SetBoneAxis("cheek", 1, sourceSession.GetBoneAxis("cheek", 1) - 0.4f);
        var edits = source.CaptureEdits(sourceSession.CaptureSemanticTransferData());
        var projection = destination.ApplyEdits(edits);
        TestAssert.Near(1f, Feature(projection.MorphFeatures, "shape"), 0.00001f);
        var targetSession = new MorphFaceEditingSession(destination.Document, destination.EvaluationBase, [targetTarget], Profile.MetadataOnlyFeatures, aliases: Profile.FeatureAliases);
        targetSession.ApplySemanticTransferData(projection);
        TestAssert.Near(new Vector3(20.25f, 0, 0), targetSession.Evaluation.Geometry.Positions[0], 0.00001f);
        TestAssert.Near(new Vector3(1.3f, 8, 3), Bone(targetSession.Evaluation.FinalSkeleton, "eye"), 0.00001f);
        TestAssert.Near(new Vector3(-1, 1.6f, 9.25f), Bone(targetSession.Evaluation.FinalSkeleton, "cheek"), 0.00001f);
        var extra = source.CaptureEdits(new MorphFaceSemanticTransferData([new("foreign", 2)], [new("foreignBone", Vector3.One)]));
        var retained = destination.ApplyEdits(extra);
        TestAssert.Near(2, Feature(retained.MorphFeatures, "foreign"), 0.00001f);
        TestAssert.Near(Vector3.One, Bone(retained.AdditiveBoneOffsets, "foreignBone"), 0.00001f);
    }

    private static void RejectsInvalid()
    {
        var mesh = PlayerConversionTestFixtures.MeshWithFaceBones();
        var baseline = new PlayerMorphConversionBaseline("approved", "wrong", new MorphFaceMorphData([], [], [mesh.Positions]));
        var failed = false;
        try { PlayerMorphBaselineFrame.Create(baseline, Template(mesh), mesh, Profile, [Target(1, 1)]); }
        catch (InvalidDataException) { failed = true; }
        TestAssert.True(failed, "Wrong profile must be rejected.");
        baseline = baseline with { ProfileKey = Profile.Key, MorphData = baseline.MorphData with { BakedLods = [[Vector3.Zero]] } };
        failed = false;
        try { PlayerMorphBaselineFrame.Create(baseline, Template(mesh), mesh, Profile, [Target(1, 1)]); }
        catch (InvalidDataException) { failed = true; }
        TestAssert.True(failed, "Wrong baked vertex count must be rejected.");
    }
}
