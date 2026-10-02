using System.Collections;
using System.Numerics;
using System.Resources;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Views;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using System.Windows.Controls.Primitives;
using System.Text.Json;
using MorphFaceEditor.Controls;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

// Runs inside the existing WPF smoke test's STA/Application lifetime.
internal static class ThemePaletteTests
{
    internal static void Verify(Application application, Window main, MainWindowViewModel viewModel)
    {
        Color Colour(string key) => ((SolidColorBrush)application.FindResource(key)).Color;
        var links = new Dictionary<string, string[]>
        {
            ["WindowBackground"] = ["InputBackground", "TextureWellBackground", "PreviewHintBackground", "BusyOverlayBackground"],
            ["PanelBackground"] = ["SectionBackground", "NavyCaption"],
            ["RaisedBackground"] = ["BoneSelectionBackground"],
            ["Accent"] = ["BadgeBorder", "TextSelectionBrush"],
            ["PrimaryText"] = ["OnColourText", "ErrorBannerText", "LightText"],
            ["SecondaryText"] = ["SwatchBorder", "PuckOutline"],
            ["MutedText"] = ["LocalDetailText", "PlaceholderText", "BonePadBorder", "BoneGridLine", "BoneRail"],
            ["SelectionBackground"] = ["BadgeBackground"],
            ["StatusFailed"] = ["ValidationForeground", "WarningText", "ErrorBannerBorder", "ActorRemoveBackground", "ErrorBannerBackground"],
            ["StatusReady"] = ["ActorIncludeBackground"],
            ["BorderBrush"] = ["NavyBorder", "HintBorder"]
        };
        foreach (var (canonical, roles) in links)
            foreach (var role in roles)
            {
                TestAssert.Equal(Colour(canonical), Colour(role));
                TestAssert.True(!ReferenceEquals(application.FindResource(canonical), application.FindResource(role)),
                    $"Semantic brushes {canonical} and {role} were merged.");
            }
        TestAssert.Equal(Color.FromRgb(0x10, 0x14, 0x1B), Colour("WindowBackground"));
        TestAssert.Equal(Color.FromArgb(0x40, 0x1F, 0x96, 0xBF), Colour("BadgeBackground"));
        TestAssert.Equal(Color.FromRgb(0xDE, 0x4E, 0x4E), Colour("ValidationForeground"));
        TestAssert.Equal(.75, ((SolidColorBrush)application.FindResource("TextSelectionBrush")).Opacity);
        TestAssert.Equal(.25, ((SolidColorBrush)application.FindResource("ErrorBannerBackground")).Opacity);
        TestAssert.True(Colour("HoverBackground") != Colour("RaisedBackground") &&
                        Colour("PressedBackground") != Colour("RaisedBackground") &&
                        Colour("HoverBackground") != Colour("PressedBackground"),
            "Button hover/pressed colours are not distinct.");

        var originalBackground = viewModel.BackgroundColor;
        var existingBrush = (SolidColorBrush)main.Background;
        var linkedBrush = (SolidColorBrush)application.FindResource("InputBackground");
        var captionBrush = (SolidColorBrush)application.FindResource("NavyCaption");
        var captionNotifications = 0;
        var paletteResources = ThemeResources.Palette(application.Resources);
        var windowColour = (ThemeColour)paletteResources["Palette.Window"];
        var panelColour = (ThemeColour)paletteResources["Palette.Panel"];
        var originalWindow = windowColour.Value;
        var originalPanel = panelColour.Value;
        void Changed(object? sender, EventArgs args) => captionNotifications++;
        captionBrush.Changed += Changed;
        try
        {
            var colour = Color.FromRgb(0x25, 0x30, 0x40);
            windowColour.Value = colour;
            panelColour.Value = colour;
            Pump();
            TestAssert.Equal(colour, existingBrush.Color);
            TestAssert.Equal(colour, linkedBrush.Color);
            TestAssert.Equal(colour, Colour("TextureWellBackground"));
            TestAssert.Equal(colour, Colour("BusyOverlayBackground"));
            TestAssert.Equal(colour, captionBrush.Color);
            TestAssert.True(captionNotifications > 0, "Live caption colour changes did not notify native window chrome.");
            var handle = new WindowInteropHelper(main).Handle;
            if (DwmGetWindowAttribute(handle, 35, out var caption, sizeof(int)) == 0)
                TestAssert.Equal(DarkTitleBar.ToColorRef(colour), caption);
            TestAssert.Equal(originalBackground, viewModel.BackgroundColor);
        }
        finally
        {
            windowColour.Value = originalWindow;
            panelColour.Value = originalPanel;
            Pump();
            captionBrush.Changed -= Changed;
        }

        var translucent = new HdrColorPickerWindow("Theme test", new Vector4(.2f, .4f, .6f, .25f),
            allowHdr: false, allowAlpha: true);
        TestAssert.Near(.25f, translucent.Value.W, .0001f);
        TestAssert.True(((RowDefinition)translucent.FindName("AlphaRow")).Height.Value > 0,
            "Theme picker hides opacity.");
        TestAssert.Equal("#33669940", ((TextBox)translucent.FindName("HexTextBox")).Text);
        ((Slider)translucent.FindName("AlphaSlider")).Value = .5;
        TestAssert.Near(.5f, translucent.Value.W, .0001f);
        translucent.Close();
        VerifyArchetypeTags(application, main);
        VerifyAssetTable(application);

#if DEBUG
        VerifyDebugPersistence(application, main);
#else
        var assembly = typeof(App).Assembly;
        TestAssert.True(assembly.GetType("MorphFaceEditor.Views.ThemeDebugWindow") is null &&
                        assembly.GetType("MorphFaceEditor.Infrastructure.DebugThemePalette") is null,
            "Release includes debug theme code.");
        using var stream = assembly.GetManifestResourceStream("MorphFaceEditor.g.resources")!;
        using var resources = new ResourceReader(stream);
        TestAssert.True(!resources.Cast<DictionaryEntry>().Any(entry =>
            entry.Key.ToString()!.Contains("themedebugwindow", StringComparison.OrdinalIgnoreCase)),
            "Release includes debug theme XAML.");
#endif
    }

    private static void VerifyArchetypeTags(Application application, Window main)
    {
        foreach (var profile in MorphFaceProfileRegistry.CreateDefault().Profiles.DistinctBy(p => p.ExportTag))
        {
            var code = profile.ExportTag.Trim('[', ']');
            var definition = ArchetypeTag.Definitions.Single(tag => tag.Code == code);
            TestAssert.Equal((Color)ColorConverter.ConvertFromString(profile.ExportTagColor),
                ((SolidColorBrush)application.FindResource(definition.BrushKey)).Color);
        }
        var list = (ListBox)main.FindName("FaceList");
        var row = (Grid)list.ItemTemplate.LoadContent();
        var border = row.Children.OfType<Border>().Single();
        row.DataContext = new BioMorphFaceListItem(1, "Test.Face", "Face", "[HMM]", "#FFFFFF");
        Pump();
        var source = (ThemeColour)ThemeResources.Palette(application.Resources)["Palette.Tag.HMM"];
        var original = source.Value;
        try
        {
            var colour = Color.FromRgb(0xD0, 0xE0, 0xF0);
            source.Value = colour;
            Pump();
            TestAssert.Equal(colour, ((SolidColorBrush)border.Background).Color);
            // Recycled rows must switch their resource link, not retain the previous archetype.
            row.DataContext = new BioMorphFaceListItem(2, "Test.Other", "Other", "[HMF]", "#FFFFFF");
            Pump();
            TestAssert.Equal(((SolidColorBrush)application.FindResource("ArchetypeTag.HMF")).Color,
                ((SolidColorBrush)border.Background).Color);
            row.DataContext = new BioMorphFaceListItem(3, "Test.Custom", "Custom", "[UNKNOWN]");
            Pump();
            TestAssert.Equal(((SolidColorBrush)application.FindResource("ArchetypeTag.CUSTOM")).Color,
                ((SolidColorBrush)border.Background).Color);
        }
        finally { source.Value = original; Pump(); }
    }

#if DEBUG
    private static void VerifyDebugPersistence(Application application, Window main)
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-Theme-{Guid.NewGuid():N}");
        var palette = new DebugThemePalette(application.Resources, Path.Combine(root, "theme-palette.json"));
        var original = DebugThemePalette.Definitions.ToDictionary(d => d.Key, d => palette.Get(d.Key));
        var defaults = new ResourceDictionary
        {
            Source = new Uri("/MorphFaceEditor;component/Themes/EditorPalette.xaml", UriKind.Relative)
        };
        var canonicalKeys = defaults.Keys.Cast<object>().OfType<string>().Where(k => k.StartsWith("Palette.")).ToArray();
        TestAssert.Equal(canonicalKeys.Length, DebugThemePalette.Definitions.Count);
        TestAssert.True(canonicalKeys.All(key => DebugThemePalette.Definitions.Any(d => d.Key == key)),
            "The debug UI omitted a canonical colour.");
        var window = new ThemeDebugWindow(palette);
        TestAssert.Equal(canonicalKeys.Length, ((ItemsControl)window.FindName("ColourRows")).Items.Count);
        window.Close();
        try
        {
            VerifyPaletteDuringModalDialogs(palette, main);
            var expected = DebugThemePalette.Definitions.Select((d, index) =>
                (d.Key, Colour: Color.FromArgb((byte)(index * 11), (byte)(index * 7), (byte)(index * 9), (byte)(index * 13))))
                .ToDictionary(v => v.Key, v => v.Colour);
            foreach (var (key, colour) in expected) palette.Set(key, colour);
            Pump();
            foreach (var definition in DebugThemePalette.Definitions)
                foreach (var role in definition.Roles)
                    TestAssert.Equal(expected[definition.Key], ((SolidColorBrush)application.FindResource(role)).Color);
            palette.Save();
            using (var saved = JsonDocument.Parse(File.ReadAllText(palette.FilePath)))
            {
                TestAssert.Equal(2, saved.RootElement.GetProperty("schemaVersion").GetInt32());
                TestAssert.Equal(.75, saved.RootElement.GetProperty("roleOpacities").GetProperty("TextSelectionBrush").GetDouble());
                TestAssert.Equal(.25, saved.RootElement.GetProperty("roleOpacities").GetProperty("ErrorBannerBackground").GetDouble());
            }
            foreach (var (key, colour) in original) palette.Set(key, colour);
            TestAssert.Equal("Loaded saved debug colours.", palette.Load());
            foreach (var (key, colour) in expected) TestAssert.Equal(colour, palette.Get(key));
            File.WriteAllText(palette.FilePath,
                """{"schemaVersion":1,"colours":{"Palette.Accent":"#FF010203","Palette.SelectedText":"#12345678","Palette.BoneSelection":"#FF098765"}}""");
            TestAssert.Equal("Loaded saved debug colours.", palette.Load());
            Pump();
            TestAssert.Equal(Color.FromRgb(1, 2, 3), palette.Get("Palette.Accent"));
            TestAssert.Equal(palette.Get("Palette.Accent"), ((SolidColorBrush)application.FindResource("TextSelectionBrush")).Color);
            TestAssert.Equal(palette.Get("Palette.Raised"), ((SolidColorBrush)application.FindResource("BoneSelectionBackground")).Color);
            foreach (var (key, colour) in expected) palette.Set(key, colour);
            // A valid first entry followed by invalid data must not partially apply.
            File.WriteAllText(palette.FilePath,
                """{"schemaVersion":1,"colours":{"Palette.Window":"#FF010203","Palette.Panel":"bad"}}""");
            TestAssert.True(palette.Load()!.Contains("not loaded"), "Malformed palette was accepted.");
            foreach (var (key, colour) in expected) TestAssert.Equal(colour, palette.Get(key));
        }
        finally
        {
            foreach (var (key, colour) in original) palette.Set(key, colour);
            Pump();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyPaletteDuringModalDialogs(DebugThemePalette palette, Window main)
    {
        var window = new ThemeDebugWindow(palette) { Owner = main, Opacity = 0 };
        window.Show();
        var paletteHandle = new WindowInteropHelper(window).Handle;
        var mainHandle = new WindowInteropHelper(main).Handle;
        var dialog = new Window { Owner = main, Width = 100, Height = 100, Opacity = 0, ShowInTaskbar = false };
        Exception? failure = null;
        _ = main.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            try
            {
                TestAssert.True(IsWindowEnabled(paletteHandle) && window.IsEnabled,
                    "A modal dialog disabled the palette.");
                TestAssert.True(!IsWindowEnabled(mainHandle), "The palette exception broke MainWindow's modality.");
                var nested = new Window { Owner = dialog, Width = 100, Height = 100, Opacity = 0, ShowInTaskbar = false };
                _ = main.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                {
                    try
                    {
                        TestAssert.True(IsWindowEnabled(paletteHandle) && window.IsEnabled,
                            "A nested modal dialog disabled the palette.");
                        TestAssert.True(!IsWindowEnabled(new WindowInteropHelper(dialog).Handle),
                            "The outer dialog was incorrectly enabled.");
                        // The picker remains usable from the palette during the nested dialog.
                        var rows = (ItemsControl)window.FindName("ColourRows");
                        window.UpdateLayout();
                        var swatch = Descendants<Button>(rows).First();
                        var row = (DebugThemeColour)swatch.DataContext;
                        var before = palette.Get(row.Key);
                        _ = main.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                        {
                            var picker = Application.Current.Windows.OfType<HdrColorPickerWindow>().Single(w => w.IsVisible);
                            picker.Opacity = 0;
                            try
                            {
                                TestAssert.True(IsWindowEnabled(paletteHandle), "The palette's picker disabled its window.");
                                ((Slider)picker.FindName("RedSlider")).Value = .85;
                                TestAssert.True(palette.Get(row.Key) != before, "Picker preview did not reach the palette.");
                            }
                            catch (Exception exception) { failure = exception; }
                            finally { picker.DialogResult = false; }
                        });
                        swatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        TestAssert.Equal(before, palette.Get(row.Key));
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { nested.DialogResult = false; }
                });
                nested.ShowDialog();
                TestAssert.True(IsWindowEnabled(paletteHandle) && !IsWindowEnabled(mainHandle),
                    "Closing a nested dialog broke the palette/MainWindow enabled state.");
            }
            catch (Exception exception) { failure = exception; }
            finally { dialog.DialogResult = false; }
        });
        try
        {
            dialog.ShowDialog();
            TestAssert.True(IsWindowEnabled(mainHandle), "MainWindow stayed disabled after the modal dialog closed.");
            if (failure is not null) throw failure;
        }
        finally { window.Close(); }
    }
#endif
    private static void VerifyAssetTable(Application application)
    {
        var inventory = new PackageInventory(@"C:\Fixture\Assets.pcc", "LE1",
            new PackageFingerprint(1, DateTime.UnixEpoch, "fixture"),
            Enumerable.Range(1, 80).Select(i => new PackageEntrySummary(i, $"Assets.Mesh_{i}", $"Mesh_{i}", "SkeletalMesh", false)).ToArray());
        var window = new ManualAssetSelectionWindow(MorphFaceGame.LE1, inventory, [])
            { Width = 620, Height = 420, Opacity = 0, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var list = (ListView)window.FindName("AssetList");
            TestAssert.Equal(1d, ((TextBox)window.FindName("SearchBox")).SelectionOpacity);
            TestAssert.Equal(0, list.Template.Triggers.Count);
            var chrome = (Border)list.Template.FindName("AssetListChrome", list);
            TestAssert.Equal(((SolidColorBrush)application.FindResource("BorderBrush")).Color,
                ((SolidColorBrush)chrome.BorderBrush).Color);
            var viewer = (ScrollViewer)list.Template.FindName("PART_ScrollViewer", list);
            TestAssert.True(viewer.ScrollableWidth > 0 && viewer.ScrollableHeight > 0,
                "The asset table no longer scrolls in both directions.");
            var headers = Descendants<GridViewColumnHeader>(viewer).Where(h => h.Role == GridViewColumnHeaderRole.Normal).ToArray();
            TestAssert.Equal(3, headers.Length);
            foreach (var header in headers)
            {
                TestAssert.Equal(((SolidColorBrush)application.FindResource("PanelBackground")).Color,
                    ((SolidColorBrush)header.Background).Color);
                TestAssert.Equal(((SolidColorBrush)application.FindResource("PrimaryText")).Color,
                    ((SolidColorBrush)header.Foreground).Color);
            }
            var junction = (Border)viewer.Template.FindName("ScrollBarJunction", viewer);
            TestAssert.Equal(((SolidColorBrush)application.FindResource("InputBackground")).Color,
                ((SolidColorBrush)junction.Background).Color);
            viewer.ScrollToHorizontalOffset(50);
            window.UpdateLayout();
            Pump();
            var headerScroll = Descendants<ScrollViewer>(viewer).First(v => !ReferenceEquals(v, viewer));
            TestAssert.Near((float)viewer.HorizontalOffset, (float)headerScroll.HorizontalOffset, .01f);
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
