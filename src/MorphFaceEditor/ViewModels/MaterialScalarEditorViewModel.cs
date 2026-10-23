using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed class MaterialScalarEditorViewModel(
    MaterialEditingSession session,
    MaterialParameterDefinition definition) : ObservableObject, IContinuousEditViewModel
{
    private bool _extendedSliders;
    private float _minimum = Math.Min(definition.Minimum, session.GetScalar(definition.Name));
    private float _maximum = Math.Max(definition.Maximum, session.GetScalar(definition.Name));

    public string Name => definition.Name;
    public string Label => definition.Label;
    public string Group => definition.Group;
    public string CategoryKey => definition.Group;
    public string Description => MetadataTooltipFormatter.Format(Label, definition.Description);
    public float Minimum => _extendedSliders ? -RangeExtent : _minimum;
    public float Maximum => _extendedSliders ? RangeExtent : _maximum;
    public float RandomisationMinimum => definition.Minimum;
    public float RandomisationMaximum => definition.Maximum;
    public float Step => definition.Step;
    private float RangeExtent => Math.Max(Math.Abs(_minimum), Math.Abs(_maximum));

    public float Value
    {
        get => session.GetScalar(Name);
        set
        {
            if (value != Value)
            {
                UpdateRanges(value, expandOnly: true);
                try { session.SetScalar(Name, value); }
                finally { Refresh(); }
            }
        }
    }

    public void BeginEdit() => session.BeginScalarEdit(Name);
    public void EndEdit() => session.EndScalarEdit(Name);
    public void Refresh()
    {
        // Restore the value before shrinking its bound Slider's range.
        UpdateRanges(Value, expandOnly: true);
        OnPropertyChanged(nameof(Value));
        UpdateRanges(Value);
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
        var minimum = Math.Min(definition.Minimum, value);
        var maximum = Math.Max(definition.Maximum, value);
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
