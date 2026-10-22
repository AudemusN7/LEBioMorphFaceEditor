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
                controller.SetScene(staticScene);
                WaitForFrames(controller, 2, "The static scene did not finish its initial presentation.");
                var settledStaticFrames = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(140));
                TestAssert.True(settledStaticFrames == controller.Diagnostics.FramesRendered,
                    "A static scene kept rendering after its publication settled.");

                controller.SetScene(Scene(CreateMaterial(HumanMaterialPreviewModel.VisorLens, HeadMaterialBlendMode.Additive)));
                WaitForFrames(controller, 4, "The visor lens did not present its initial frames.");
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

                window.Hide();
                PumpFor(TimeSpan.FromMilliseconds(60));
                var hiddenFrames = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(hiddenFrames == controller.Diagnostics.FramesRendered,
                    "A hidden host kept rendering visor animation frames.");

                window.Show();
                PumpFor(TimeSpan.FromMilliseconds(100));
                var visibleFrames = controller.Diagnostics.FramesRendered;
                WaitForFrameIncrease(controller, visibleFrames + 3,
                    "Showing the visor host again did not resume animation frames.");

                controller.SetScene(staticScene);
                WaitForFrameIncrease(controller, controller.Diagnostics.FramesRendered + 2,
                    "Switching away from the visor did not present the static scene.");
                var afterStaticSwitch = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(afterStaticSwitch == controller.Diagnostics.FramesRendered,
                    "Switching to a static material left visor animation ticking.");

                controller.SetScene(Scene(CreateMaterial(HumanMaterialPreviewModel.VisorLens, HeadMaterialBlendMode.Additive)));
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
