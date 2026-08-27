using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.ViewModels;

public sealed class BoneAxisEditorViewModel : ObservableObject, IContinuousEditViewModel
{
    private readonly MorphFaceEditingSession _session;
    private float _value;
    private bool _extendedSliders;
    private float _defaultMinimum;
    private float _defaultMaximum;
    private float _extendedExtent;

    public BoneAxisEditorViewModel(MorphFaceEditingSession session, string boneName, int axis)
    {
        _session = session;
        BoneName = boneName;
        Axis = axis;
        _value = session.GetBoneAxis(boneName, axis);
        _defaultMinimum = _value - 2;
        _defaultMaximum = _value + 2;
        _extendedExtent = Math.Max(2, Math.Abs(_value) + 2);
    }

    public string BoneName { get; }
    public int Axis { get; }
    public string AxisName => Axis switch { 0 => "X", 1 => "Y", _ => "Z" };
    public float Minimum => _extendedSliders ? -_extendedExtent : _defaultMinimum;
    public float Maximum => _extendedSliders ? _extendedExtent : _defaultMaximum;
    public float Step => 0.01f;

    public float Value
    {
        get => _value;
        set
        {
            EnsureRangesInclude(value);
            if (SetProperty(ref _value, value))
            {
                _session.SetBoneAxis(BoneName, Axis, value);
            }
        }
    }

    public void BeginEdit() => _session.BeginBoneEdit(BoneName, Axis);
    public void EndEdit() => _session.EndBoneEdit(BoneName, Axis);

    public void Refresh() => SetProperty(ref _value, _session.GetBoneAxis(BoneName, Axis), nameof(Value));

    public void SetExtendedSliders(bool enabled)
    {
        if (_extendedSliders == enabled)
        {
            return;
        }
        _extendedSliders = enabled;
        OnPropertyChanged(nameof(Minimum));
        OnPropertyChanged(nameof(Maximum));
    }

    private void EnsureRangesInclude(float value)
    {
        var changed = false;
        if (value < _defaultMinimum)
        {
            _defaultMinimum = value;
            changed = true;
        }
        if (value > _defaultMaximum)
        {
            _defaultMaximum = value;
            changed = true;
        }
        var extent = Math.Abs(value);
        if (extent > _extendedExtent)
        {
            _extendedExtent = extent;
            changed = true;
        }
        if (changed)
        {
            OnPropertyChanged(nameof(Minimum));
            OnPropertyChanged(nameof(Maximum));
        }
    }
}
