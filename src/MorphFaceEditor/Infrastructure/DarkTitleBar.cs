using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MorphFaceEditor.Infrastructure;

public static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    // DWM uses COLORREF (0x00BBGGRR), not the usual XAML RGB ordering.
    private const int NavyCaption = 0x0029211B; // #1B2129, PanelBackground
    private const int NavyBorder = 0x004A3E34;  // #343E4A, BorderBrush
    private const int LightText = 0x00F8F5F2;   // #F2F5F8, PrimaryText

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var enabled = 1;
            if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
            }

            SetColor(handle, CaptionColor, NavyCaption);
            SetColor(handle, BorderColor, NavyBorder);
            SetColor(handle, TextColor, LightText);
        };
    }

    private static void SetColor(IntPtr handle, int attribute, int color) =>
        _ = DwmSetWindowAttribute(handle, attribute, ref color, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
