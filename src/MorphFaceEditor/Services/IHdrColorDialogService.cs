using System.Numerics;

namespace MorphFaceEditor.Services;

public sealed record BackgroundColourChoice(Vector4 Colour, bool FollowTheme = false);

public interface IHdrColorDialogService
{
    bool ExtendedSliders { get; set; }
    Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview);
    Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview);
    BackgroundColourChoice? EditBackground(string title, Vector4 value, Vector4 themeDefault, Action<Vector4> livePreview)
        => EditStandard(title, value, livePreview) is { } colour ? new(colour) : null;
}
