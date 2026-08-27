using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.ViewModels;

public sealed class MaterialScalarEditorViewModel(
    MaterialEditingSession session,
    MaterialParameterDefinition definition) : ObservableObject, IContinuousEditViewModel
{
    private bool _extendedSliders;
    private float _defaultMinimum = Math.Min(definition.Minimum, session.GetScalar(definition.Name));
    private float _defaultMaximum = Math.Max(definition.Maximum, session.GetScalar(definition.Name));
    private float _extendedExtent = Math.Max(
        Math.Abs(Math.Min(definition.Minimum, session.GetScalar(definition.Name))),
        Math.Abs(Math.Max(definition.Maximum, session.GetScalar(definition.Name))));

    public string Name => definition.Name;
    public string Label => definition.Label;
    public string Group => definition.Group;
    public string CategoryKey => definition.Group;
    public string Description => definition.Description;
    public float Minimum => _extendedSliders ? -RangeExtent : _defaultMinimum;
    public float Maximum => _extendedSliders ? RangeExtent : _defaultMaximum;
    public float Step => definition.Step;
    private float RangeExtent => _extendedExtent;

    public float Value
    {
        get => session.GetScalar(Name);
        set
        {
            EnsureRangesInclude(value);
            if (value != Value)
            {
                session.SetScalar(Name, value);
                OnPropertyChanged();
            }
        }
    }

    public void BeginEdit() => session.BeginScalarEdit(Name);
    public void EndEdit() => session.EndScalarEdit(Name);
    public void Refresh()
    {
        EnsureRangesInclude(Value);
        OnPropertyChanged(nameof(Value));
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
