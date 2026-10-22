using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiEditorTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("slider and bone puck gestures retain order after stale pointer completion", ControlGesturesKeepSemanticHistory),
        new("morph controls disable when the selected LOD has no target geometry", MorphControlsDisableForTargetlessLod),
        new("Fix Morph undo restores baked preview and survives a failing preview callback", FixMorphUndoRestoresBakedPreview),
        new("bone controls tolerate bones removed by zeroed morphs", BoneControlsTolerateRemovedMorphBones)
    ];

    private static void ControlGesturesKeepSemanticHistory()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        var original = editor.CreateDraft();
        var feature = editor.Features.Single(value => value.Name == "nose_BridgeIn");
        var bone = editor.BoneTransforms.Single(value => value.BoneName == "root");
        feature.BeginEdit();
        feature.Value = 0.25f;
        feature.Value = 0.5f;
        TestAssert.True(!editor.UndoCommand.CanExecute(null), "A slider drag committed before release.");

        bone.BeginPuckEdit();
        bone.X.Value = 0.25f;
        bone.Y.Value = -0.5f;
        bone.Z.Value = 0.75f;
        feature.EndEdit(); // A virtualized slider's old pointer-up arrives during the puck drag.
        bone.X.Value = 0.4f;
        bone.EndPuckEdit();
        bone.EndPuckEdit();

        editor.UndoCommand.Execute(null);
        TestAssert.Equal(0.5f, feature.Value);
        TestAssert.Equal(Vector3.Zero, new Vector3(bone.X.Value, bone.Y.Value, bone.Z.Value));
        editor.UndoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(original, editor.CreateDraft()) &&
                        !editor.UndoCommand.CanExecute(null),
            "Stale completion split a gesture or lost the preceding edit.");
    }

    private static void MorphControlsDisableForTargetlessLod()
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
        var session = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(face, mesh, [target]);
        var resolved = session.Evaluation.Resolution.Features.Single(feature => feature.Feature.Name == "Target");
        var metadata = new HumanMaleFeatureMetadataCatalog().Describe(resolved, session.CanEditMorphFeatures);
        var control = new MorphFeatureEditorViewModel(session, metadata, new HashSet<int> { 0, 1 }, _ => { });
        TestAssert.Near(0, control.Minimum, 0);
        TestAssert.Near(1, control.Maximum, 0);
        control.SetExtendedSliders(true);
        TestAssert.Near(-control.Maximum, control.Minimum, 0);
        TestAssert.True(control.Minimum < 0 && control.Maximum > 0,
            "Extended mode did not centre the morph slider on zero with a signed range.");

        control.SetPreviewLod(1, lodHasMorphGeometry: true);
        TestAssert.True(control.IsEditable, "A supported LOD disabled its morph control.");
        control.SetPreviewLod(2, lodHasMorphGeometry: false);
        TestAssert.True(!control.IsEditable, "A targetless LOD left its morph control enabled.");
    }

    private static void FixMorphUndoRestoresBakedPreview()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh(lowerLodIndex: 2);
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Shape", "MorphTarget"),
            [
                new MorphTargetLod(0, 3, [new MorphVertexDelta(1, new Vector3(2, 0, 0), Vector3.Zero)]),
                new MorphTargetLod(2, 3, [new MorphVertexDelta(1, new Vector3(0, 0, 2), Vector3.Zero)])
            ],
            []);
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [new MorphFeatureValue("Shape", 0.5f)],
            [],
            MorphFaceMaterialOverrides.Empty,
            [
                [Vector3.Zero, new Vector3(3, 0, 0), Vector3.UnitY],
                [new Vector3(10, 0, 0), new Vector3(12.5f, 0, 0), new Vector3(10, 2, 0)]
            ],
            []);
        var session = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(document, mesh, [target]);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            null,
            ResolvedHeadMaterialSet.Empty,
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document));
        using var reader = new MorphFacePackageReader();
        var editor = new FaceEditorViewModel(
            session,
            new HumanMaleFeatureMetadataCatalog(),
            new MorphFaceEditor.Core.Editing.MaterialEditingSession(
                MorphFaceMaterialOverrides.Empty,
                ResolvedHeadMaterialSet.Empty),
            new StubColorDialog(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "fixture.pcc",
            [],
            [],
            null,
            [],
            _ => { });
        using var viewModel = CreateMainWindowViewModel(reader);
        var setEditor = typeof(MainWindowViewModel).GetMethod(
            "SetEditor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("MainWindowViewModel.SetEditor was not found.");
        setEditor.Invoke(viewModel, [editor, loaded]);
        viewModel.PreviewLod = 2;
        var feature = editor.Features.Single(value => value.Name == "Shape");

        TestAssert.True(viewModel.FixMorphCommand.CanExecute(null),
            "The repairable face did not expose the Fix Morph command.");
        TestAssert.True(!feature.IsEditable, "The blocked face exposed an editable slider before repair.");

        viewModel.FixMorphCommand.Execute(null);

        TestAssert.True(editor.IsDirty, "Repair did not mark the face dirty.");
        TestAssert.True(feature.IsEditable, "Repair did not enable the morph slider.");
        TestAssert.True(!viewModel.FixMorphCommand.CanExecute(null),
            "Fix Morph remained available after repair.");
        MorphFaceEditor.Rendering.HeadPreviewScene? undoScene = null;
        var undoDeformationCount = 0;
        viewModel.PreviewSceneReady += (scene, _) => undoScene = scene;
        viewModel.PreviewDeformationReady += _ => undoDeformationCount++;
        editor.UndoCommand.Execute(null);

        TestAssert.True(undoScene is not null, "Undo did not rebuild the fallback preview scene.");
        TestAssert.Equal(0, undoDeformationCount);
        TestAssert.Near(new Vector3(0, 0, 12.5f), undoScene!.Meshes[0].Vertices[1].Position, 0.0001f);
        TestAssert.True(!editor.IsDirty, "Undo left the repaired face dirty.");
        TestAssert.True(!feature.IsEditable, "Undo left the morph slider enabled.");
        TestAssert.True(viewModel.FixMorphCommand.CanExecute(null),
            "Undo did not restore Fix Morph availability.");
        MorphFaceEditor.Rendering.HeadPreviewDeformationUpdate? redoUpdate = null;
        viewModel.PreviewDeformationReady += update => redoUpdate = update;
        editor.RedoCommand.Execute(null);

        TestAssert.True(redoUpdate is not null, "Redo did not restore the editable deformation preview.");
        TestAssert.Near(new Vector3(0, 1, 12), redoUpdate!.Vertices[1].Position, 0.0001f);
        TestAssert.True(editor.IsDirty && feature.IsEditable,
            "Redo did not restore the repaired editor state.");
        editor.UndoCommand.Execute(null);
        viewModel.PreviewDeformationReady += _ => throw new InvalidOperationException("preview callback failed");
        viewModel.FixMorphCommand.Execute(null);

        TestAssert.True(editor.IsDirty && feature.IsEditable &&
                        !viewModel.FixMorphCommand.CanExecute(null),
            "A throwing preview subscriber rolled back the committed repair state.");
        TestAssert.True(viewModel.HasError, "The post-commit preview failure was not reported.");
    }

    private static void BoneControlsTolerateRemovedMorphBones()
    {
        var source = TestFixtures.CreateMesh();
        var mesh = source with
        {
            Lods = [source.Lods[0] with { Topology = source.Topology with
            {
                ReferenceSkeleton =
                [
                    new ReferenceBone("root", 0, Vector3.Zero, Quaternion.Identity),
                    new ReferenceBone("eye_transient", 0, new Vector3(1, 2, 3), Quaternion.Identity)
                ],
                ActiveBones = [0, 1],
                RequiredBones = [0, 1]
            }}]
        };
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.eyes_wide", "MorphTarget"),
            [new MorphTargetLod(0, mesh.Positions.Length, [])],
            [new MorphTargetBoneOffset("eye_transient", Vector3.One)]);
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("SalarianFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [new MorphFeatureValue("eyes_wide", 1)],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var session = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(
            face, mesh, [target], profileName: "Salarian");
        var control = new BoneAxisEditorViewModel(session, "eye_transient", 0);

        session.SetFeatures(new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["eyes_wide"] = 0
        });
        control.Refresh();

        TestAssert.True(!control.IsAvailable,
            "A bone control remained active after its morph-introduced bone left the evaluated skeleton.");
        session.Undo();
        control.Refresh();
        TestAssert.True(control.IsAvailable,
            "Undo did not reactivate a bone control when its morph-introduced bone returned.");
    }
}
