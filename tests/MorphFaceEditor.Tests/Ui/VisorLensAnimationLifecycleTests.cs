using System.Diagnostics;
using System.Numerics;
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

    private static void LensAnimationTicksOnlyWhileActive() => WpfTestDispatcher.Invoke(() =>
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
        var trace = new List<string>();
        string? previewFailure = null;
        void Trace(string kind) => trace.Add($"VISOR {kind}: active={window.IsActive}; state={window.WindowState}; visible={window.IsVisible}/{host.IsVisible}; size={host.ActualWidth}x{host.ActualHeight}; frames={controller.Diagnostics.FramesRendered}");
        window.Activated += (_, _) => Trace("Activated");
        window.Deactivated += (_, _) => Trace("Deactivated");
        window.StateChanged += (_, _) => Trace("StateChanged");
        controller.PreviewFailed += message =>
        {
            previewFailure ??= message;
            trace.Add("VISOR PreviewFailed: " + message);
        };
        try
        {
            window.Show();
            WaitForActivation(window);
            PumpFor(TimeSpan.FromMilliseconds(200));
            var staticScene = Scene(CreateMaterial(HumanMaterialPreviewModel.Default, HeadMaterialBlendMode.Opaque));
            var lensMaterial = CreateMaterial(HumanMaterialPreviewModel.VisorLens, HeadMaterialBlendMode.Additive);
            var staticReplacement = CreateMaterial(HumanMaterialPreviewModel.Default, HeadMaterialBlendMode.Opaque)
                with { Key = lensMaterial.Key };
            var lensScene = Scene(lensMaterial);
            controller.SetScene(lensScene);
            WaitForFrames(controller, 1, "The visor lens did not present its initial frame.", window);
            // Losing activation after startup must not make the active-animation fixture time out.
            var competingWindow = new Window
            {
                Owner = window, Width = 96, Height = 96, Left = -2200, Top = -2000,
                ShowInTaskbar = false, WindowStyle = WindowStyle.None
            };
            try
            {
                competingWindow.Show();
                WaitForActivation(competingWindow);
                TestAssert.True(!window.IsActive, "The startup fixture did not deactivate its preview owner.");
                WaitForFrames(controller, controller.Diagnostics.FramesRendered + 3,
                    "The visible visor lens did not receive time-driven preview frames.", window);
            }
            finally { competingWindow.Close(); }

            foreach (var (label, pause, resume) in new (string, Action, Action)[]
            {
                ("Diagnostic mode",
                    () => controller.SetOptions(new HeadPreviewOptions(RenderMode: HeadPreviewRenderMode.Diagnostic)),
                    () => controller.SetOptions(new HeadPreviewOptions())),
                ("hidden attachment",
                    () => controller.SetOptions(new HeadPreviewOptions(ShowAttachment: false)),
                    () => controller.SetOptions(new HeadPreviewOptions())),
                ("same-key static material replacement",
                    () => ApplyMaterial(staticReplacement), () => ApplyMaterial(lensMaterial)),
                ("static scene",
                    () => controller.SetScene(staticScene), () => controller.SetScene(lensScene))
            })
            {
                var pausedFrames = AssertPublicationThenQuiet(pause, label);
                Trace("Resuming " + label);
                resume();
                WaitForFrames(controller, pausedFrames + 3,
                    $"Leaving {label} did not restart visor animation.", window);
            }

            foreach (var (label, enter, restore) in new (string, Action, Action)[]
            {
                ("hidden", window.Hide, window.Show),
                ("minimized", () => window.WindowState = WindowState.Minimized,
                    () => window.WindowState = WindowState.Normal)
            })
            {
                enter();
                PumpFor(TimeSpan.FromMilliseconds(80));
                var gatedFrames = controller.Diagnostics.FramesRendered;
                PublishExplicitChanges();
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(gatedFrames == controller.Diagnostics.FramesRendered,
                    $"A {label} host rendered animation or explicit scene/material/options changes.");
                restore();
                WaitForFrames(controller, gatedFrames + 3,
                    $"Restoring the {label} visor host did not resume animation.", window);
            }

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
                    WaitForActivation(picker);
                    TestAssert.True(!window.IsActive && picker.IsActive,
                        "The modal fixture did not deactivate its preview owner.");
                    var inactiveFrames = controller.Diagnostics.FramesRendered;
                    controller.RequestRender();
                    PumpFor(TimeSpan.FromMilliseconds(160));
                    TestAssert.True(inactiveFrames == controller.Diagnostics.FramesRendered,
                        "An inactive modal owner rendered animation or an ordinary render request.");
                    AssertPublicationThenQuiet(() => ApplyMaterial(lensMaterial), "modal owner's explicit material");
                    AssertPublicationThenQuiet(() => controller.SetOptions(new HeadPreviewOptions(), allowInactive: true),
                        "modal owner's explicit options");
                    AssertPublicationThenQuiet(() => controller.SetScene(lensScene), "modal owner's explicit scene");
                }
                catch (Exception exception) { modalFailure = exception; }
                finally { picker.Close(); }
            });
            _ = picker.ShowDialog();
            if (modalFailure is not null) throw new InvalidOperationException("Modal preview publication failed.", modalFailure);
            WaitForFrames(controller, controller.Diagnostics.FramesRendered + 3,
                "Closing the modal picker did not resume visor animation.", window);

            controller.ClearScene();
            var clearedFrames = controller.Diagnostics.FramesRendered;
            PumpFor(TimeSpan.FromMilliseconds(160));
            TestAssert.True(clearedFrames == controller.Diagnostics.FramesRendered,
                "Clearing the visor scene did not stop animation frames.");
            controller.SetScene(lensScene);
            WaitForFrames(controller, clearedFrames + 3, "Reloading the cleared visor did not restart animation.", window);
            controller.Dispose();
            var disposedFrames = controller.Diagnostics.FramesRendered;
            PumpFor(TimeSpan.FromMilliseconds(80));
            TestAssert.True(disposedFrames == controller.Diagnostics.FramesRendered,
                "Disposing the host left an animation callback running.");
            TestAssert.True(previewFailure is null, $"The lifecycle preview failed: {previewFailure}");

            void ApplyMaterial(HeadPreviewMaterial material) => controller.UpdateMaterials(new HeadPreviewMaterialUpdate(
                new Dictionary<string, HeadPreviewMaterial> { [material.Key] = material }));

            void PublishExplicitChanges()
            {
                ApplyMaterial(lensMaterial);
                controller.SetOptions(new HeadPreviewOptions(), allowInactive: true);
                controller.SetScene(lensScene);
            }

            int AssertPublicationThenQuiet(Action publish, string kind)
            {
                var before = controller.Diagnostics.FramesRendered;
                publish();
                WaitForFrames(controller, before + 1, $"The {kind} update was not presented.");
                PumpFor(TimeSpan.FromMilliseconds(80));
                var settled = controller.Diagnostics.FramesRendered;
                PumpFor(TimeSpan.FromMilliseconds(160));
                TestAssert.True(settled == controller.Diagnostics.FramesRendered,
                    $"The {kind} update left visor animation ticking.");
                return settled;
            }
        }
        catch
        {
            Trace("FAILED");
            Console.WriteLine(string.Join(Environment.NewLine, trace));
            throw;
        }
        finally
        {
            window.Close();
        }
    });

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

    private static void WaitForFrames(HeadPreviewHostController controller, int minimumFrames, string message,
        Window? activateWindow = null)
    {
        // Startup, restore and ShowDialog do not guarantee that Windows keeps/grants activation.
        // Retry that fixture prerequisite while waiting, without relaxing the host's activity gate.
        bool Ready() => (activateWindow is null || TryActivate(activateWindow)) &&
                        controller.Diagnostics.FramesRendered >= minimumFrames;
        WaitFor(Ready, () => activateWindow is null ? message :
            $"{message} Window active={activateWindow.IsActive}, state={activateWindow.WindowState}.");
    }

    private static void WaitForActivation(Window window) => WaitFor(() => TryActivate(window),
        () => $"The lifecycle fixture window did not activate (state={window.WindowState}, visible={window.IsVisible}).");

    private static bool TryActivate(Window window)
    {
        if (!window.IsActive) _ = window.Activate();
        return window.IsActive;
    }

    private static void WaitFor(Func<bool> ready, Func<string> failure)
    {
        if (ready()) return;
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
            if (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(10)) return;
            poll.Stop();
            frame.Continue = false;
        };
        poll.Start();
        Dispatcher.PushFrame(frame);
        TestAssert.True(ready(), failure());
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
