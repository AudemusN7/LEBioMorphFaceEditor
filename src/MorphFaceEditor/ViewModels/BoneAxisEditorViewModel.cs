using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.ViewModels;

public sealed class BoneAxisEditorViewModel : ObservableObject, IContinuousEditViewModel
{
    private readonly MorphFaceEditingSession _session;
    private float _value;
    private bool _extendedSliders;
    private readonly float _baselineMinimum;
    private readonly float _baselineMaximum;
    private float _minimum;
    private float _maximum;
    private bool _isAvailable = true;

    public BoneAxisEditorViewModel(MorphFaceEditingSession session, string boneName, int axis)
    {
        _session = session;
        BoneName = boneName;
        Axis = axis;
        _value = session.GetBoneAxis(boneName, axis);
        _minimum = _baselineMinimum = _value - 2;
        _maximum = _baselineMaximum = _value + 2;
    }

    public string BoneName { get; }
    public int Axis { get; }
    public string AxisName => Axis switch { 0 => "X", 1 => "Y", _ => "Z" };
    public float Minimum => _extendedSliders ? -RangeExtent : _minimum;
    public float Maximum => _extendedSliders ? RangeExtent : _maximum;
    private float RangeExtent => Math.Max(Math.Abs(_minimum), Math.Abs(_maximum));
    public float Step => 0.01f;
    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public float Value
    {
        get => _value;
        set
        {
            if (!float.IsFinite(value))
            {
                OnPropertyChanged(nameof(Value));
                return;
            }
            UpdateRanges(value, expandOnly: true);
            if (SetProperty(ref _value, value))
            {
                _session.SetBoneAxis(BoneName, Axis, value);
            }
            UpdateRanges(_value);
        }
    }

    public void BeginEdit()
    {
        if (IsAvailable) _session.BeginBoneEdit(BoneName, Axis);
    }
    public void EndEdit()
    {
        if (IsAvailable) _session.EndBoneEdit(BoneName, Axis);
    }

    public void Refresh()
    {
        if (!_session.TryGetBoneAxis(BoneName, Axis, out var value))
        {
            IsAvailable = false;
            return;
        }
        IsAvailable = true;
        // Restore the value before shrinking its bound Slider's range.
        UpdateRanges(value, expandOnly: true);
        SetProperty(ref _value, value, nameof(Value));
        UpdateRanges(value);
    }

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

    private void UpdateRanges(float value, bool expandOnly = false)
    {
        var minimum = Math.Min(_baselineMinimum, value);
        var maximum = Math.Max(_baselineMaximum, value);
        if (expandOnly)
            (minimum, maximum) = (Math.Min(_minimum, minimum), Math.Max(_maximum, maximum));
        if (_minimum != minimum || _maximum != maximum)
        {
            _minimum = minimum;
            _maximum = maximum;
            OnPropertyChanged(nameof(Minimum));
            OnPropertyChanged(nameof(Maximum));
        }
    }
}
