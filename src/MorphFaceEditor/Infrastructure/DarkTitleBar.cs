using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MorphFaceEditor.Infrastructure;

public static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;

            var roles = new[] { (CaptionColor, "NavyCaption"), (BorderColor, "NavyBorder"), (TextColor, "LightText") };
            var subscriptions = new List<(SolidColorBrush Brush, EventHandler Handler)>();
            foreach (var (attribute, key) in roles)
            {
                if (window.TryFindResource(key) is not SolidColorBrush brush) continue;
                void Update(object? sender, EventArgs args)
                {
                    SetColor(handle, attribute, ToColorRef(brush.Color));
                    if (attribute == TextColor)
                    {
                        var enabled = brush.Color.R + brush.Color.G + brush.Color.B > 384 ? 1 : 0;
                        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
                    }
                }
                Update(null, EventArgs.Empty);
                brush.Changed += Update;
                subscriptions.Add((brush, Update));
            }
            window.Closed += (_, _) =>
            {
                foreach (var (brush, handler) in subscriptions) brush.Changed -= handler;
            };
        };
    }

    // Windows captions ignore alpha and use COLORREF (0x00BBGGRR).
    internal static int ToColorRef(Color colour) => colour.R | (colour.G << 8) | (colour.B << 16);

    private static void SetColor(IntPtr handle, int attribute, int color) =>
        _ = DwmSetWindowAttribute(handle, attribute, ref color, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
