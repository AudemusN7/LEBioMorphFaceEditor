using System.Numerics;

namespace MorphFaceEditor.Services;

public interface IHdrColorDialogService
{
    bool ExtendedSliders { get; set; }
    Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview);
    Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview);
}
