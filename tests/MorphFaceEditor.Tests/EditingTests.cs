using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using System.Numerics;

namespace MorphFaceEditor.Tests;

/// <summary>Checks cross-domain history, dirty-state ownership and semantic profile transfer.</summary>
public static class EditingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("unified history preserves interleaved editor chronology", UnifiedHistoryIsChronological),
        new("dirty comparison tracks authored state and ignores derived LODs", DirtyComparisonTracksAuthoredState),
        new("semantic transfer rebakes destination geometry and preserves bone residuals", SemanticTransferRebakesDestinationProfile)
    ];

    private static void SemanticTransferRebakesDestinationProfile()
    {
        var sourceMesh = TestFixtures.CreateMesh();
        var sourceCommon = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Common", "MorphTarget"),
            [new MorphTargetLod(0, 3, [new MorphVertexDelta(1, new Vector3(2, 0, 0), Vector3.Zero)])],
            [new MorphTargetBoneOffset("root", new Vector3(0, 2, 0))]);
        var sourceDocument = new MorphFaceDocument(
            TestFixtures.CreateIdentity("SourceFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, "source"),
            sourceMesh.Source,
            null,
            [new MorphFeatureValue("Common", 0.5f), new MorphFeatureValue("SourceOnly", 0.75f)],
            [new BoneTranslation("root", new Vector3(0, 1, 3))],
            MorphFaceMaterialOverrides.Empty,
            [[Vector3.Zero, new Vector3(2, 2, 3), new Vector3(-1, -2, -3)]],
            []);
        var source = new MorphFaceEditingSession(
            sourceDocument,
            sourceMesh,
            [sourceCommon],
            new HashSet<string>(["SourceOnly"], StringComparer.OrdinalIgnoreCase),
            "Source");
        TestAssert.True(source.CanEdit, source.EditBlockReason ?? "Source session was not editable.");

        var destinationBase = new[]
        {
            new Vector3(10, 0, 0),
            new Vector3(11, 2, 3),
            new Vector3(9, -2, -3)
        };
        var destinationMesh = sourceMesh with
        {
            Source = TestFixtures.CreateIdentity("DestinationBase", "SkeletalMesh"),
            Positions = destinationBase,
            Topology = sourceMesh.Topology with
            {
                ReferenceSkeleton = [new ReferenceBone("root", 0, new Vector3(5, 0, 0), Quaternion.Identity)]
            }
        };
        var destinationCommon = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Common", "MorphTarget"),
            [new MorphTargetLod(0, 3, [new MorphVertexDelta(1, new Vector3(0, 4, 0), Vector3.Zero)])],
            [new MorphTargetBoneOffset("root", new Vector3(0, 6, 0))]);
        var destinationOnly = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.DestinationOnly", "MorphTarget"),
            [new MorphTargetLod(0, 3, [new MorphVertexDelta(2, Vector3.One, Vector3.Zero)])],
            []);
        var destinationDocument = new MorphFaceDocument(
            TestFixtures.CreateIdentity("DestinationFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, "destination"),
            destinationMesh.Source,
            null,
            [],
            [new BoneTranslation("root", new Vector3(5, 0, 0))],
            MorphFaceMaterialOverrides.Empty,
            [destinationBase.ToArray()],
            []);
        var destination = new MorphFaceEditingSession(
            destinationDocument,
            destinationMesh,
            [destinationCommon, destinationOnly],
            profileName: "Destination");
        TestAssert.True(destination.CanEdit, destination.EditBlockReason ?? "Destination session was not editable.");

        destination.ApplySemanticTransferData(source.CaptureSemanticTransferData());
        var draft = destination.CreateDraft(null, [], MorphFaceMaterialOverrides.Empty);

        TestAssert.Near(0.5f, draft.GetFeatureOffset("Common"), 0.000001f);
        TestAssert.True(draft.MorphFeatures.All(value => value.Name != "SourceOnly"),
            "A source-only slider leaked into the destination profile.");
        TestAssert.True(draft.MorphFeatures.All(value => value.Name != "DestinationOnly" || value.Offset == 0),
            "A destination-only slider did not retain its neutral value.");
        TestAssert.Near(new Vector3(11, 4, 3), draft.BakedLods[0][1], 0.000001f);
        TestAssert.Near(new Vector3(5, 3, 3), draft.FinalSkeleton.Single().Translation, 0.000001f);
    }

    private static void UnifiedHistoryIsChronological()
    {
        var geometry = new FakeSource("geometry");
        var material = new FakeSource("material");
        var hair = new FakeSource("hair");
        using var history = new EditorUndoCoordinator(geometry, material, hair);
        geometry.Commit("feature");
        material.Commit("colour");
        hair.Commit("mesh");

        history.Undo();
        history.Undo();
        history.Undo();
        TestAssert.Equal("undo:mesh,undo:colour,undo:feature", string.Join(',',
            hair.Log.Concat(material.Log).Concat(geometry.Log)));

        history.Redo();
        history.Redo();
        history.Redo();
        TestAssert.True(geometry.Log.Contains("redo:feature"), "Geometry redo was not replayed.");
        TestAssert.True(material.Log.Contains("redo:colour"), "Material redo was not replayed.");
        TestAssert.True(hair.Log.Contains("redo:mesh"), "Hair redo was not replayed.");
    }

    private static void DirtyComparisonTracksAuthoredState()
    {
        var source = new AssetIdentity("fixture.pcc", "Package.Face", 1, "BioMorphFace");
        var hair = new AssetIdentity("fixture.pcc", "Package.Hair", 2, "SkeletalMesh");
        var material = new MorphFaceMaterialOverrides(
            null,
            [new ScalarMaterialOverride("Scalar", 0.5f)],
            [new VectorMaterialOverride("Colour", Vector4.One)],
            []);
        var baseline = new MorphFaceDocument(
            source,
            new PackageFingerprint(1, DateTime.UnixEpoch, "hash"),
            null,
            hair,
            [new MorphFeatureValue("Feature", 0.25f)],
            [new BoneTranslation("root", Vector3.Zero)],
            material,
            [new[] { Vector3.Zero }],
            []);

        TestAssert.True(MorphFaceEditorStateComparer.Equals(
            baseline,
            baseline with { BakedLods = [new[] { Vector3.One }] }),
            "Derived baked geometry incorrectly marked the document dirty.");
        TestAssert.True(!MorphFaceEditorStateComparer.Equals(
            baseline,
            baseline with { MorphFeatures = [new MorphFeatureValue("Feature", 0.75f)] }),
            "A feature edit was not detected.");
        TestAssert.True(!MorphFaceEditorStateComparer.Equals(
            baseline,
            baseline with { OtherMeshReferences = [new AssetIdentity("fixture.pcc", "Package.Hat", 3, "SkeletalMesh")] }),
            "An m_oOtherMeshes edit was not detected.");
        TestAssert.True(!MorphFaceEditorStateComparer.Equals(
            baseline,
            baseline with
            {
                MaterialOverrides = material with
                {
                    Scalars = [new ScalarMaterialOverride("Scalar", 0.75f)]
                }
            }),
            "A material edit was not detected.");
    }

    private sealed class FakeSource(string name) : IUndoableEditSource
    {
        private readonly Stack<string> _undo = new();
        private readonly Stack<string> _redo = new();
        public event EventHandler? EditCommitted;
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public List<string> Log { get; } = [];

        public void Commit(string value)
        {
            _undo.Push(value);
            _redo.Clear();
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }

        public void Undo()
        {
            var value = _undo.Pop();
            _redo.Push(value);
            Log.Add($"undo:{value}");
        }

        public void Redo()
        {
            var value = _redo.Pop();
            _undo.Push(value);
            Log.Add($"redo:{value}");
        }

        public void ClearRedo() => _redo.Clear();
        public override string ToString() => name;
    }
}
