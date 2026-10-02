using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MorphFaceEditor.Controls;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Views;
using ThemeMode = MorphFaceEditor.Infrastructure.ThemeMode;

namespace MorphFaceEditor.Tests;

internal static class ThemeRuntimeTests
{
    internal sealed class SystemTheme : IWindowsThemeSource
    {
        public ThemeMode Mode { get; private set; } = ThemeMode.Dark;
        public event EventHandler? Changed;
        public void Set(ThemeMode mode) { Mode = mode; Changed?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly ResourceDictionary _resources;
        private readonly Dictionary<string, Color> _original;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"MFE-ThemeRuntime-{Guid.NewGuid():N}");
        public string FilePath => Path.Combine(Root, "theme-preferences.json");
        public SystemTheme System { get; } = new();
        public ThemeManager Manager { get; }
        public Fixture(ResourceDictionary resources)
        {
            _resources = ThemeResources.Palette(resources);
            _original = _resources.Keys.Cast<object>().OfType<string>()
                .Where(k => k.StartsWith("Palette."))
                .ToDictionary(k => k, k => ((ThemeColour)_resources[k]).Value);
            Manager = new(resources, new ThemePreferencesStore(FilePath), System);
        }
        public void Dispose()
        {
            Manager.Dispose();
            foreach (var (key, colour) in _original) ((ThemeColour)_resources[key]).Value = colour;
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    internal static void Verify(Application app, Window main, MainWindowViewModel viewModel, Fixture fixture)
    {
        var manager = fixture.Manager;
        using var viewportBinding = new ThemeViewportBinding(manager, viewModel);
        var palette = ThemeResources.Palette(app.Resources);
        Color Colour(string key) => ((ThemeColour)palette[key]).Value;
        void Defaults(ThemeMode mode)
        {
            foreach (var (key, colour) in ThemeManager.ReadColours(mode.ToString()))
                TestAssert.Equal(colour, Colour(key));
            var background = manager.ViewportBackground;
            TestAssert.Equal(new Vector4(background.R / 255f, background.G / 255f, background.B / 255f, 1), viewModel.BackgroundColor);
            TestAssert.Equal(new Vector3(background.R / 255f, background.G / 255f, background.B / 255f), viewModel.CreatePreviewOptions().BackgroundColor);
        }
        try
        {
            TestAssert.Equal(ThemeMode.System, manager.Mode);
            Defaults(ThemeMode.Dark);
            var settings = new SettingsWindow(viewModel, manager);
            var selector = (ComboBox)settings.FindName("ThemeSelector");
            selector.SelectedIndex = 1;
            Pump();
            TestAssert.Equal(ThemeMode.Light, manager.Mode);
            Defaults(ThemeMode.Light);
            TestAssert.Equal(Colors.White, ((SolidColorBrush)main.Background).Color);
            TestAssert.Equal(Colors.Black, ((SolidColorBrush)main.Foreground).Color);
            TestAssert.Equal((byte)0x0C, Colour("Palette.SelectedRow").A);
            fixture.System.Set(ThemeMode.Dark);
            Pump();
            TestAssert.Equal(ThemeMode.Light, manager.EffectiveMode);
            selector.SelectedIndex = 0;
            Pump();
            Defaults(ThemeMode.Dark);
            fixture.System.Set(ThemeMode.Light);
            Pump();
            Defaults(ThemeMode.Light);
            TestAssert.Equal(0, selector.SelectedIndex);
            TestAssert.Equal(ThemeMode.System, new ThemePreferencesStore(fixture.FilePath).Current.Mode);
            settings.Close();

            foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
            {
                manager.SetMode(mode);
                manager.PreviewAccentHue(285);
                Pump();
                var defaults = ThemeManager.ReadColours(mode.ToString());
                var authored = ThemeManager.Hsv(defaults["Palette.Accent"]);
                var accent = ThemeManager.Hsv(Colour("Palette.Accent"));
                TestAssert.Near(285f, (float)accent.Hue, .6f);
                TestAssert.Near((float)authored.Saturation, (float)accent.Saturation, .005f);
                TestAssert.Near((float)authored.Value, (float)accent.Value, .005f);
                var selection = ThemeManager.Hsv(Colour("Palette.SelectedRow"));
                var originalSelection = ThemeManager.Hsv(defaults["Palette.SelectedRow"]);
                var expectedHue = ThemeManager.NormalizeHue(originalSelection.Hue + 285 - authored.Hue);
                TestAssert.Near((float)expectedHue, (float)selection.Hue, .6f);
                TestAssert.Near((float)originalSelection.Saturation, (float)selection.Saturation, .005f);
                TestAssert.Near((float)originalSelection.Value, (float)selection.Value, .005f);
                TestAssert.Equal(defaults["Palette.SelectedRow"].A, Colour("Palette.SelectedRow").A);
                foreach (var (key, colour) in defaults.Where(p => p.Key is not ("Palette.Accent" or "Palette.SelectedRow")))
                    TestAssert.Equal(colour, Colour(key));
                var handle = new WindowInteropHelper(main).Handle;
                if (DwmGetWindowAttribute(handle, 20, out var darkChrome, sizeof(int)) == 0)
                    TestAssert.Equal(mode == ThemeMode.Dark ? 1 : 0, darkChrome);
                var background = defaults["Palette.PreviewHost"];
                TestAssert.Equal(new Vector4(background.R / 255f, background.G / 255f, background.B / 255f, 1), viewModel.BackgroundColor);
            }

            manager.CommitAccentHue();
            var saved = new ThemePreferencesStore(fixture.FilePath);
            TestAssert.Equal(ThemeMode.Light, saved.Current.Mode);
            TestAssert.Equal(285d, saved.Current.AccentHue);
            var lockedSettings = new SettingsWindow(viewModel, manager);
            var beforeFailedSave = File.ReadAllBytes(fixture.FilePath);
            using (var locked = File.Open(fixture.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ((ComboBox)lockedSettings.FindName("ThemeSelector")).SelectedIndex = 2;
                TestAssert.Equal(ThemeMode.Light, manager.Mode);
                TestAssert.Equal(1, ((ComboBox)lockedSettings.FindName("ThemeSelector")).SelectedIndex);
                TestAssert.True(((TextBlock)lockedSettings.FindName("OperationStatus")).Text.Contains("Could not save"),
                    "A failed preference write was silently accepted.");
            }
            TestAssert.True(beforeFailedSave.SequenceEqual(File.ReadAllBytes(fixture.FilePath)) &&
                            !File.Exists(fixture.FilePath + ".tmp"), "Failed preference save modified data or left a candidate.");
            lockedSettings.Close();
            using (var reopened = new ThemeManager(app.Resources, saved, new SystemTheme()))
            {
                TestAssert.Equal(ThemeMode.Light, reopened.EffectiveMode);
                TestAssert.Equal(285d, reopened.AccentHue);
            }
            VerifyPicker(manager);
            // A live System change also works when the notification is raised off the UI thread.
            manager.SetMode(ThemeMode.System);
            Task.Run(() => fixture.System.Set(ThemeMode.Dark)).GetAwaiter().GetResult();
            Pump();
            TestAssert.Equal(ThemeMode.Dark, manager.EffectiveMode);
            manager.PreviewAccentHue(null);
            manager.CommitAccentHue();
            Defaults(ThemeMode.Dark);
            File.WriteAllText(fixture.FilePath, """{"Mode":"invalid","AccentHue":285}""");
            TestAssert.Equal(new ThemePreferences(), new ThemePreferencesStore(fixture.FilePath).Current);
            File.WriteAllText(fixture.FilePath, """{"Mode":"Light","AccentHue":999}""");
            TestAssert.Equal(new ThemePreferences(), new ThemePreferencesStore(fixture.FilePath).Current);
        }
        finally
        {
            manager.PreviewAccentHue(null);
            manager.SetMode(ThemeMode.Dark);
            Pump();
        }
    }

    private static void VerifyPicker(ThemeManager manager)
    {
        var before = manager.AccentHue;
        var picker = new AccentColorPickerWindow(manager) { Opacity = 0 };
        var wheel = (ColorWheelControl)picker.FindName("Wheel");
        var hsv = ThemeManager.Hsv(manager.AuthoredAccent);
        picker.Show();
        picker.UpdateLayout();
        TestAssert.True(wheel.HueOnly && !Descendants<Slider>(picker).Any(), "Accent picker exposes non-hue sliders.");
        wheel.Hue = 80;
        TestAssert.Equal(80d, manager.AccentHue);
        TestAssert.Near((float)hsv.Value, (float)wheel.Brightness, .0001f);
        TestAssert.Near((float)hsv.Saturation, (float)wheel.Saturation, .0001f);
        typeof(ColorWheelControl).GetMethod("SetFromPoint",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(wheel, [new Point(wheel.ActualWidth / 2 + 5, wheel.ActualHeight / 2 + 5)]);
        TestAssert.Near(45f, (float)manager.AccentHue!.Value, .001f);
        TestAssert.Near((float)hsv.Saturation, (float)wheel.Saturation, .0001f);
        picker.Close();
        TestAssert.Equal(before, manager.AccentHue);

        var accepted = new AccentColorPickerWindow(manager) { Opacity = 0 };
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            ((ColorWheelControl)accepted.FindName("Wheel")).Hue = 120;
            ((Button)accepted.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        TestAssert.Equal(true, accepted.ShowDialog());
        TestAssert.Equal(120d, manager.AccentHue);

        var reset = new AccentColorPickerWindow(manager) { Opacity = 0 };
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            ((Button)reset.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)reset.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        TestAssert.Equal(true, reset.ShowDialog());
        TestAssert.Equal<double?>(null, manager.AccentHue);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var matchChild in Descendants<T>(child)) yield return matchChild;
        }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
