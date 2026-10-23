using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiEditorTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("continuous controls complete captured edits on rebind capture focus and unload", ContinuousControlLifetimesKeepCapturedEdits),
        new("bone pointer and wheel gestures retain captured edits through rebind unload and timeout", BoneControlLifetimesKeepHistory),
        new("bound slider ranges preserve Cursed imported and replayed authored values", AuthoredValuesKeepCurrentSliderRanges),
        new("morph controls disable when the selected LOD has no target geometry", MorphControlsDisableForTargetlessLod),
        new("Fix Morph undo restores baked preview and survives a failing preview callback", FixMorphUndoRestoresBakedPreview),
        new("bone controls tolerate bones removed by zeroed morphs", BoneControlsTolerateRemovedMorphBones)
    ];

    private static void AuthoredValuesKeepCurrentSliderRanges() => WpfTestDispatcher.Invoke(() => RunWithDispatcher(async () =>
    {
        using var reader = new MorphFacePackageReader();
        foreach (var extended in new[] { false, true })
        {
            using var editor = CreateRandomisationEditor(reader, randomSeedFactory: () => 771);
            using var clean = CreateRandomisationEditor(reader, randomSeedFactory: () => 771);
            editor.SetExtendedSliders(extended);
            clean.SetExtendedSliders(extended);
            var sliders = BindEditorSliders(editor);
            var original = editor.CreateDraft();
            editor.CursedMode = true;
            editor.MorphRandomisationStrength = editor.MaterialRandomisationStrength = 100;
            await editor.RandomiseAsync();
            var cursed = editor.CreateDraft();
            TestAssert.True(sliders.Any(value => value.Control.Value < value.Minimum || value.Control.Value > value.Maximum),
                "The Cursed fixture did not exercise expanded slider ranges.");
            AssertSliderRanges(sliders, extended);
            editor.UndoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(original, editor.CreateDraft()),
                "Shrinking Undo ranges clamped the restored authored state.");
            AssertSliderRanges(sliders, extended);
            editor.RedoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(cursed, editor.CreateDraft()),
                "Expanding Redo ranges clamped a restored Cursed value.");
            AssertSliderRanges(sliders, extended);

            editor.CursedMode = false;
            clean.RandomiseMaterials = true;
            clean.MorphRandomisationStrength = clean.MaterialRandomisationStrength = 100;
            await clean.RandomiseAsync();
            await editor.RandomiseAsync();
            foreach (var scalar in clean.Material.Scalars)
                TestAssert.Equal(scalar.Value, editor.Material.Scalars.Single(value => value.Name == scalar.Name).Value);
            AssertSliderRanges(sliders, extended);
            editor.UndoCommand.Execute(null);
            editor.ApplyMorphData(new(
                original.MorphFeatures.Select(value => value with { Offset = value.Name == "nose_BridgeIn" ? 25 : -30 }).ToArray(),
                original.FinalSkeleton.Select(value => value.BoneName == "root"
                    ? value with { Translation = new Vector3(-50, 100, -75) } : value).ToArray(),
                original.BakedLods));
            await editor.ApplyMaterialDataAsync(new([new("HED_Norm_Blend", -125), new("Emis_Scalar", 75)], [], []));
            var imported = editor.CreateDraft();
            TestAssert.Equal(25f, editor.Features.Single(value => value.Name == "nose_BridgeIn").Value);
            TestAssert.Equal(-125f, editor.Material.Scalars.Single(value => value.Name == "HED_Norm_Blend").Value);
            TestAssert.Equal(100f, editor.Bones.Single(value => value.BoneName == "root" && value.Axis == 1).Value);
            AssertSliderRanges(sliders, extended);
            editor.UndoCommand.Execute(null);
            editor.UndoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(cursed, editor.CreateDraft()),
                "Undoing imported outliers clamped the restored values.");
            AssertSliderRanges(sliders, extended);
            editor.RedoCommand.Execute(null);
            editor.RedoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(imported, editor.CreateDraft()),
                "Redoing imported outliers clamped the imported values.");
            editor.SetExtendedSliders(!extended);
            AssertSliderRanges(sliders, !extended);
            editor.SetToDefaultsCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(original, editor.CreateDraft()),
                "Shrinking default ranges changed the default authored state.");
            AssertSliderRanges(sliders, !extended);
            editor.UndoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(imported, editor.CreateDraft()),
                "Undo Defaults lost the imported authored state.");
            AssertSliderRanges(sliders, !extended);
            editor.RedoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(original, editor.CreateDraft()),
                "Redo Defaults failed to restore the complete default state.");
            AssertSliderRanges(sliders, !extended);
        }
    }));

    private static IReadOnlyList<(IContinuousEditViewModel Control, Slider Slider, float Minimum, float Maximum)>
        BindEditorSliders(FaceEditorViewModel editor)
    {
        return editor.Features.Select(value => ((IContinuousEditViewModel)value, value.Metadata.Minimum, value.Metadata.Maximum))
            .Concat(editor.Material.Scalars.Select(value => ((IContinuousEditViewModel)value, value.RandomisationMinimum, value.RandomisationMaximum)))
            .Concat(editor.Bones.Select(value => ((IContinuousEditViewModel)value, value.Value - 2, value.Value + 2)))
            .Select(value =>
            {
                var slider = new Slider { DataContext = value.Item1 };
                slider.SetBinding(Slider.MinimumProperty, new Binding("Minimum"));
                slider.SetBinding(Slider.MaximumProperty, new Binding("Maximum"));
                slider.SetBinding(Slider.ValueProperty, new Binding("Value") { Mode = BindingMode.TwoWay });
                return (value.Item1, slider, value.Item2, value.Item3);
            }).ToArray();
    }

    private static void AssertSliderRanges(
        IReadOnlyList<(IContinuousEditViewModel Control, Slider Slider, float Minimum, float Maximum)> sliders, bool extended)
    {
        foreach (var (control, slider, baselineMinimum, baselineMaximum) in sliders)
        {
            var minimum = Math.Min(baselineMinimum, control.Value);
            var maximum = Math.Max(baselineMaximum, control.Value);
            if (extended)
            {
                var extent = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
                (minimum, maximum) = (-extent, extent);
            }
            TestAssert.Near(minimum, (float)slider.Minimum, 0);
            TestAssert.Near(maximum, (float)slider.Maximum, 0);
            TestAssert.Near(control.Value, (float)slider.Value, 0);
        }
    }

    private static void ContinuousControlLifetimesKeepCapturedEdits() => WpfTestDispatcher.Invoke(() =>
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateEditor(reader);
        var original = editor.CreateDraft();
        var feature = editor.Features.Single(value => value.Name == "Target");
        var slider = new Slider { DataContext = feature };
        slider.SetBinding(Slider.ValueProperty, new Binding(nameof(feature.Value)) { Mode = BindingMode.TwoWay });
        var textBox = new TextBox();
        using var gesture = new ContinuousEditGesture();
        void Begin(FrameworkElement source) => gesture.Begin(source, feature.BeginEdit, feature.EndEdit);

        Begin(slider);
        slider.Value = 0.25;
        slider.DataContext = new object();
        TestAssert.True(editor.UndoCommand.CanExecute(null), "Rebinding did not complete the initiating edit.");

        Begin(textBox);
        feature.Value = 0.4f;
        slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.LostMouseCaptureEvent });
        feature.Value = 0.5f;
        textBox.RaiseEvent(new KeyboardFocusChangedEventArgs(
            Keyboard.PrimaryDevice, Environment.TickCount, textBox, slider)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        feature.Value = 0.6f;

        Begin(slider);
        feature.Value = 0.75f;
        slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.LostMouseCaptureEvent });
        gesture.Complete(slider);
        feature.Value = 0.8f;

        Begin(textBox);
        feature.Value = 1f;
        textBox.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        gesture.Complete();
        feature.Value = 0.9f;
        foreach (var expected in new[] { 1f, 0.8f, 0.75f, 0.6f, 0.5f, 0.25f, 0f })
        {
            editor.UndoCommand.Execute(null);
            TestAssert.Equal(expected, feature.Value);
        }
        TestAssert.True(!editor.UndoCommand.CanExecute(null) &&
                        MorphFaceEditorStateComparer.Equals(original, editor.CreateDraft()),
            "Control lifecycle completion split, lost or duplicated an authored action.");
    });

    private static void BoneControlLifetimesKeepHistory() => WpfTestDispatcher.Invoke(() => RunWithDispatcher(async () =>
    {
        using var reader = new MorphFacePackageReader();
        using var first = CreateEditor(reader);
        using var second = CreateEditor(reader);
        var originalFirst = first.CreateDraft();
        var originalSecond = second.CreateDraft();
        var firstBone = first.BoneTransforms.Single(value => value.BoneName == "root");
        var secondBone = second.BoneTransforms.Single(value => value.BoneName == "root");
        var textBox = new TextBox { DataContext = firstBone.X };
        textBox.SetBinding(TextBox.TextProperty, new Binding(nameof(BoneAxisEditorViewModel.Value))
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        textBox.Text = "0.125";
        textBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); // Enter uses this same binding commit.
        TestAssert.Equal(0.125f, firstBone.X.Value);
        TestAssert.True(first.IsDirty, "A committed bone text edit left the clean editor unmarked.");
        first.UndoCommand.Execute(null);
        TestAssert.True(!first.IsDirty && MorphFaceEditorStateComparer.Equals(originalFirst, first.CreateDraft()),
            "Undoing the bone text edit failed to restore clean authored state.");
        var control = new BoneTransformControl { DataContext = firstBone };
        var pad = (Border)control.FindName("Pad");
        void Pointer(RoutedEvent routedEvent) => pad.RaiseEvent(
            new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent });
        void Wheel() => pad.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent });

        Pointer(UIElement.MouseLeftButtonDownEvent);
        firstBone.X.Value = 0.25f;
        firstBone.Y.Value = -0.5f;
        firstBone.Z.Value = 0.75f;
        TestAssert.True(!first.UndoCommand.CanExecute(null), "The puck gesture committed before its lifecycle ended.");
        control.DataContext = secondBone;
        TestAssert.True(first.UndoCommand.CanExecute(null), "Rebinding did not complete the original bone gesture.");
        first.UndoCommand.Execute(null);
        TestAssert.True(!first.UndoCommand.CanExecute(null) &&
                        MorphFaceEditorStateComparer.Equals(originalFirst, first.CreateDraft()),
            "Rebinding lost the original bone gesture or split its axes.");

        Pointer(UIElement.MouseLeftButtonUpEvent);
        TestAssert.True(!second.UndoCommand.CanExecute(null), "A stale pointer release edited the replacement bone.");
        Wheel();
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        TestAssert.True(second.UndoCommand.CanExecute(null), "Unloading did not complete the replacement bone gesture.");
        second.UndoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(originalSecond, second.CreateDraft()),
            "Unloading did not complete the replacement bone gesture.");

        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Wheel();
        Wheel();
        await Task.Delay(550);
        TestAssert.True(second.UndoCommand.CanExecute(null), "The bone wheel idle timer did not complete its edit.");
        second.UndoCommand.Execute(null);
        TestAssert.True(!second.UndoCommand.CanExecute(null) &&
                        MorphFaceEditorStateComparer.Equals(originalSecond, second.CreateDraft()),
            "Wheel timeout did not retain one complete authored action.");
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }));

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
