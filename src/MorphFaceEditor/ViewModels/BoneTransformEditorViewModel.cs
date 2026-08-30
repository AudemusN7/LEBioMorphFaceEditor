using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.ViewModels;

public sealed class BoneTransformEditorViewModel : ObservableObject
{
    private readonly MorphFaceEditingSession _session;
    private bool _isAvailable;

    public BoneTransformEditorViewModel(
        MorphFaceEditingSession session,
        BoneAxisEditorViewModel x,
        BoneAxisEditorViewModel y,
        BoneAxisEditorViewModel z)
    {
        _session = session;
        if (!string.Equals(x.BoneName, y.BoneName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(x.BoneName, z.BoneName, StringComparison.OrdinalIgnoreCase) ||
            x.Axis != 0 || y.Axis != 1 || z.Axis != 2)
        {
            throw new ArgumentException("A bone transform requires matching X, Y and Z axis editors.");
        }

        BoneName = x.BoneName;
        X = x;
        Y = y;
        Z = z;
        _isAvailable = x.IsAvailable || y.IsAvailable || z.IsAvailable;
    }

    public string BoneName { get; }
    public BoneAxisEditorViewModel X { get; }
    public BoneAxisEditorViewModel Y { get; }
    public BoneAxisEditorViewModel Z { get; }
    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public void BeginPuckEdit()
    {
        if (IsAvailable)
        {
            _session.BeginBoneTranslationEdit(BoneName);
        }
    }

    public void EndPuckEdit()
    {
        _session.EndBoneTranslationEdit(BoneName);
    }

    public void RefreshAvailability() =>
        IsAvailable = X.IsAvailable || Y.IsAvailable || Z.IsAvailable;
}
