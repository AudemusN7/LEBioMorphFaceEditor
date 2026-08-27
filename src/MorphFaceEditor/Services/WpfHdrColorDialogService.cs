using System.Numerics;
using System.Windows;
using MorphFaceEditor.Views;

namespace MorphFaceEditor.Services;

public sealed class WpfHdrColorDialogService : IHdrColorDialogService
{
    public bool ExtendedSliders { get; set; }

    public Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview)
        => EditWindow(title, value, livePreview, allowHdr: true);

    public Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview)
        => EditWindow(title, value with { W = 1 }, livePreview, allowHdr: false);

    private Vector4? EditWindow(
        string title,
        Vector4 value,
        Action<Vector4> livePreview,
        bool allowHdr)
    {
        var window = new HdrColorPickerWindow(title, value, allowHdr, ExtendedSliders)
        {
            Owner = Application.Current.MainWindow
        };
        window.ValueChanged += (_, _) => livePreview(window.Value);
        return window.ShowDialog() == true ? window.Value : null;
    }
}
