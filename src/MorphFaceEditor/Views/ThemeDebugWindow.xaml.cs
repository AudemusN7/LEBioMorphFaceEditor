using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Views;

public partial class ThemeDebugWindow : Window
{
    private readonly DebugThemePalette _palette;
    private readonly DebugThemeColour[] _rows;
    private HwndSource? _source;
    private HdrColorPickerWindow? _picker;
    private bool _enablePending;

    internal ThemeDebugWindow(DebugThemePalette palette, string? loadStatus = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _palette = palette;
        _rows = DebugThemePalette.Definitions.Select(d => new DebugThemeColour(palette, d)).ToArray();
        ColourRows.ItemsSource = _rows;
        SavePath.Text = palette.FilePath;
        SaveStatus.Text = loadStatus ?? Loc.Text("loc_ThemeDebugWindow_ColoursSavedOnlyOnSave");
        palette.Changed += OnPaletteChanged;
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(OnNativeMessage);
        };
        Closed += (_, _) =>
        {
            palette.Changed -= OnPaletteChanged;
            _source?.RemoveHook(OnNativeMessage);
            _source = null;
        };
    }

    private IntPtr OnNativeMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // ShowDialog disables every other window on this UI thread. This Debug-only
        // authoring window is deliberately exempt; ordinary dialogs stay modal.
        const int wmEnable = 0x000A;
        if (message == wmEnable && wParam == IntPtr.Zero && !_enablePending)
        {
            _enablePending = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                _enablePending = false;
                if (_source is { IsDisposed: false } && IsVisible)
                    _ = EnableWindow(_source.Handle, true);
            });
        }
        return IntPtr.Zero;
    }

    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        foreach (var row in _rows) row.Refresh();
        SaveStatus.Text = Loc.Text("loc_ThemeDebugWindow_UnsavedPaletteChanges");
    }

    private void OnEditColour(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not DebugThemeColour row) return;
        if (_picker is not null)
        {
            _picker.Activate();
            return;
        }
        var original = row.Colour;
        var picker = new HdrColorPickerWindow(row.Name,
            new Vector4(original.R / 255f, original.G / 255f, original.B / 255f, original.A / 255f),
            allowHdr: false, allowAlpha: true) { Owner = this };
        void Preview(object? source, EventArgs args)
        {
            var value = picker.Value;
            static byte Channel(float channel) => (byte)Math.Clamp((int)Math.Round(channel * 255), 0, 255);
            _palette.Set(row.Key, Color.FromArgb(Channel(value.W), Channel(value.X), Channel(value.Y), Channel(value.Z)));
        }
        picker.ValueChanged += Preview;
        _picker = picker;
        var accepted = false;
        try { accepted = picker.ShowDialog() == true; }
        finally
        {
            picker.ValueChanged -= Preview;
            _picker = null;
            if (!accepted) _palette.Set(row.Key, original);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            _palette.Save();
            SaveStatus.Text = Loc.Text("loc_ThemeDebugWindow_SaveCompleteInstructions");
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            SaveStatus.Text = Loc.Format("loc_ThemeDebugWindow_CouldNotSave", exception.Message);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
}
