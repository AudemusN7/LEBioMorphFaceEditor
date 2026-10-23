using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Presentation;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

/// <summary>WPF-hosted regression coverage for the visor lens's time-driven preview lifecycle.</summary>
internal static class VisorLensAnimationLifecycleTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("visor lens animation ticks only while its preview is active", LensAnimationTicksOnlyWhileActive)
    ];

    private static void LensAnimationTicksOnlyWhileActive()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var image = new Image();
                var host = new Grid();
                host.Children.Add(image);
                var window = new Window
                {
                    Width = 128,
                    Height = 128,
                    Left = -2000,
                    Top = -2000,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = host
                };
                using var controller = new HeadPreviewHostController(window, host, image);
                window.Show();
                PumpFor(TimeSpan.FromMilliseconds(200));

                var staticScene = Scene(CreateMaterial(HumanMaterialPreviewModel.Default, HeadMaterialBlendMode.Opaque));
                var lensMaterial = CreateMaterial(HumanMaterialPreviewModel.VisorLens, HeadMaterialBlendMode.Additive);
                var lensScene = Scene(lensMaterial);
                controller.SetScene(staticScene);
                WaitForFrames(controller, 1, "The static scene did not finish its initial presentation.");
                PumpFor(TimeSpan.FromMilliseconds(80));
                var settledStaticFrames = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(140));
                TestAssert.True(settledStaticFrames == controller.Diagnostics.FramesRendered,
                    "A static scene kept rendering after its publication settled.");

                var beforeLens = controller.Diagnostics.FramesRendered;
                controller.SetScene(lensScene);
                WaitForFrameIncrease(controller, beforeLens + 1, "The visor lens did not present its initial frame.");
                var beforeAnimation = controller.Diagnostics.FramesRendered;
                WaitForFrameIncrease(controller, beforeAnimation + 3,
                    "The visible visor lens did not receive time-driven preview frames.");

                controller.SetOptions(new HeadPreviewOptions(RenderMode: HeadPreviewRenderMode.Diagnostic));
                PumpFor(TimeSpan.FromMilliseconds(80));
                var diagnosticFrames = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(diagnosticFrames == controller.Diagnostics.FramesRendered,
                    "Diagnostic mode kept the visor animation timer rendering frames.");

                controller.SetOptions(new HeadPreviewOptions(RenderMode: HeadPreviewRenderMode.Shaded));
                WaitForFrameIncrease(controller, diagnosticFrames + 3,
                    "Returning to the shaded visor preview did not restart animation frames.");

                controller.SetOptions(new HeadPreviewOptions(ShowAttachment: false));
                PumpFor(TimeSpan.FromMilliseconds(80));
                var hiddenAttachmentFrames = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(hiddenAttachmentFrames == controller.Diagnostics.FramesRendered,
                    "Hiding the animated visor attachment left its preview timer rendering frames.");
                controller.SetOptions(new HeadPreviewOptions(ShowAttachment: true));
                WaitForFrameIncrease(controller, hiddenAttachmentFrames + 3,
                    "Showing the visor attachment did not restart animation frames.");

                var staticReplacement = CreateMaterial(HumanMaterialPreviewModel.Default, HeadMaterialBlendMode.Opaque)
                    with { Key = lensMaterial.Key };
                var beforeReplacement = controller.Diagnostics.FramesRendered;
                controller.UpdateMaterials(new HeadPreviewMaterialUpdate(
                    new Dictionary<string, HeadPreviewMaterial> { [lensMaterial.Key] = staticReplacement }));
                WaitForFrameIncrease(controller, beforeReplacement + 1, "The static material replacement was not presented.");
                PumpFor(TimeSpan.FromMilliseconds(80));
                var replacedFrames = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(replacedFrames == controller.Diagnostics.FramesRendered,
                    "A same-key static material replacement left visor animation ticking.");
                controller.UpdateMaterials(new HeadPreviewMaterialUpdate(
                    new Dictionary<string, HeadPreviewMaterial> { [lensMaterial.Key] = lensMaterial }));
                WaitForFrameIncrease(controller, replacedFrames + 3,
                    "A same-key lens material replacement did not restart animation.");

                window.Hide();
                PumpFor(TimeSpan.FromMilliseconds(60));
                var hiddenFrames = controller.Diagnostics.FramesRendered;
                PublishExplicitChanges();
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(hiddenFrames == controller.Diagnostics.FramesRendered,
                    "A hidden host rendered animation or explicit scene/material/options changes.");

                window.Show();
                PumpFor(TimeSpan.FromMilliseconds(100));
                var visibleFrames = controller.Diagnostics.FramesRendered;
                WaitForFrameIncrease(controller, visibleFrames + 3,
                    "Showing the visor host again did not resume animation frames.");

                window.WindowState = WindowState.Minimized;
                PumpFor(TimeSpan.FromMilliseconds(80));
                var minimizedFrames = controller.Diagnostics.FramesRendered;
                PublishExplicitChanges();
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(minimizedFrames == controller.Diagnostics.FramesRendered,
                    "A minimized host rendered animation or explicit scene/material/options changes.");
                window.WindowState = WindowState.Normal;
                window.Activate();
                WaitForFrameIncrease(controller, minimizedFrames + 3, "Restoring the visor host did not resume animation.");

                Exception? modalFailure = null;
                var picker = new Window
                {
                    Owner = window, Width = 96, Height = 96, Left = -2200, Top = -2000,
                    ShowInTaskbar = false, WindowStyle = WindowStyle.None
                };
                picker.Loaded += (_, _) => picker.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    try
                    {
                        PumpFor(TimeSpan.FromMilliseconds(80));
                        TestAssert.True(!window.IsActive && picker.IsActive,
                            "The modal fixture did not deactivate its preview owner.");
                        var inactiveFrames = controller.Diagnostics.FramesRendered;
                        controller.RequestRender();
                        PumpFor(TimeSpan.FromMilliseconds(160));
                        TestAssert.True(inactiveFrames == controller.Diagnostics.FramesRendered,
                            "An inactive modal owner rendered animation or an ordinary render request.");
                        AssertInactivePublication(() => controller.UpdateMaterials(new HeadPreviewMaterialUpdate(
                            new Dictionary<string, HeadPreviewMaterial> { [lensMaterial.Key] = lensMaterial })), "material");
                        AssertInactivePublication(() => controller.SetOptions(new HeadPreviewOptions(), allowInactive: true), "options");
                        AssertInactivePublication(() => controller.SetScene(lensScene), "scene");
                    }
                    catch (Exception exception) { modalFailure = exception; }
                    finally { picker.Close(); }
                });
                _ = picker.ShowDialog();
                if (modalFailure is not null) throw new InvalidOperationException("Modal preview publication failed.", modalFailure);
                window.Activate();
                WaitForFrameIncrease(controller, controller.Diagnostics.FramesRendered + 3,
                    "Closing the modal picker did not resume visor animation.");

                var beforeStaticSwitch = controller.Diagnostics.FramesRendered;
                controller.SetScene(staticScene);
                WaitForFrameIncrease(controller, beforeStaticSwitch + 1,
                    "Switching away from the visor did not present the static scene.");
                PumpFor(TimeSpan.FromMilliseconds(80));
                var afterStaticSwitch = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(afterStaticSwitch == controller.Diagnostics.FramesRendered,
                    "Switching to a static material left visor animation ticking.");

                controller.SetScene(lensScene);
                WaitForFrameIncrease(controller, controller.Diagnostics.FramesRendered + 3,
                    "The visor timer did not restart after switching back to the lens.");
                controller.ClearScene();
                TestAssert.True(image.Source is null, "Clearing the animated scene left its prior frame visible.");
                var clearedFrames = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(clearedFrames == controller.Diagnostics.FramesRendered,
                    "Clearing the visor scene did not stop animation frames.");

                controller.Dispose();
                var disposedFrames = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(80));
                TestAssert.True(disposedFrames == controller.Diagnostics.FramesRendered,
                    "Disposing the host left an animation callback running.");
                window.Close();

                void PublishExplicitChanges()
                {
                    controller.UpdateMaterials(new HeadPreviewMaterialUpdate(
                        new Dictionary<string, HeadPreviewMaterial> { [lensMaterial.Key] = lensMaterial }));
                    controller.SetOptions(new HeadPreviewOptions(), allowInactive: true);
                    controller.SetScene(lensScene);
                }

                void AssertInactivePublication(Action publish, string kind)
                {
                    var before = controller.Diagnostics.FramesRendered;
                    publish();
                    WaitForFrameIncrease(controller, before + 1, $"The modal owner's explicit {kind} update was not presented.");
                    PumpFor(TimeSpan.FromMilliseconds(80));
                    var settled = controller.Diagnostics.FramesRendered;
                    PumpFor(TimeSpan.FromMilliseconds(160));
                    TestAssert.True(settled == controller.Diagnostics.FramesRendered,
                        $"The modal owner's explicit {kind} update restarted inactive animation.");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException($"The visor lens animation lifecycle regression failed: {failure.Message}", failure);
        }
    }

    private static HeadPreviewScene Scene(HeadPreviewMaterial material)
    {
        static HeadPreviewVertex Vertex(float x, float y) => new(
            new Vector3(x, y, 0), Vector3.UnitZ, new Vector4(1, 0, 0, 1), new Vector2(0.5f),
            0, 0, 0, 0, new Vector4(1, 0, 0, 0));

        var mesh = new HeadPreviewMesh(
            "Preview mesh",
            [Vertex(-0.8f, -0.7f), Vertex(0.8f, -0.7f), Vertex(0, 0.8f)],
            [0u, 1u, 2u],
            [new HeadPreviewSection(0, 3, 0, material)],
            IsAttachment: material.PreviewModel == HumanMaterialPreviewModel.VisorLens);
        return new HeadPreviewScene("Lifecycle scene", [mesh],
            new HeadPreviewBounds(new Vector3(-1, -1, -0.1f), new Vector3(1, 1, 0.1f)));
    }

    private static HeadPreviewMaterial CreateMaterial(HumanMaterialPreviewModel model, HeadMaterialBlendMode blendMode)
    {
        var textures = model == HumanMaterialPreviewModel.VisorLens
            ? new[]
            {
                LensTexture("__LensScanline"),
                LensTexture("__LensEmission", TextureColorSpace.Srgb),
                LensTexture("__LensDistortion"),
                LensTexture("__LensFractal")
            }.ToDictionary(texture => texture.ParameterName, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase);
        return new HeadPreviewMaterial(
            "material-" + model,
            "material-" + model,
            HeadMaterialFamily.Accessory,
            blendMode,
            false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            textures)
        {
            PreviewModel = model,
            IsLe2 = true,
            HasCompiledParameterSupport = true
        };
    }

    private static HeadPreviewTexture LensTexture(string name, TextureColorSpace colorSpace = TextureColorSpace.Linear) => new(
        name,
        name,
        1,
        1,
        [100, 140, 180, 255],
        TextureRole.Other,
        colorSpace,
        TextureAlphaPolicy.Ignore,
        false);

    private static void WaitForFrames(HeadPreviewHostController controller, int minimumFrames, string message) =>
        WaitUntil(() => controller.Diagnostics.FramesRendered >= minimumFrames, message);

    private static void WaitForFrameIncrease(HeadPreviewHostController controller, long minimumFrames, string message) =>
        WaitUntil(() => controller.Diagnostics.FramesRendered >= minimumFrames, message);

    private static void WaitUntil(Func<bool> predicate, string message)
    {
        if (predicate()) return;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timeout = Stopwatch.StartNew();
        var poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        poll.Tick += (_, _) =>
        {
            // Initial D3D/shader creation can exceed two seconds on a busy host.
            if (!predicate() && timeout.Elapsed < TimeSpan.FromSeconds(10)) return;
            poll.Stop();
            frame.Continue = false;
        };
        poll.Start();
        Dispatcher.PushFrame(frame);
        TestAssert.True(predicate(), message);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
