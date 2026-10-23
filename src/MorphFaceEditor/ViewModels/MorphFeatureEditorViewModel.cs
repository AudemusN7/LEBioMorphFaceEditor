using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed class MorphFeatureEditorViewModel : ObservableObject, IContinuousEditViewModel
{
    private readonly MorphFaceEditingSession _session;
    private readonly Action<string> _reportError;
    private readonly IReadOnlySet<int>? _geometryLods;
    private readonly bool _profileAllowsEditing;
    private float _value;
    private bool _isEditable;
    private bool _sessionCanEdit;
    private bool _lodHasMorphGeometry = true;
    private bool _targetWorksAtLod = true;
    private bool _extendedSliders;
    private float _minimum;
    private float _maximum;
    private long _lastPreviewLogTick;

    public MorphFeatureEditorViewModel(
        MorphFaceEditingSession session,
        MorphFeatureMetadata metadata,
        IReadOnlySet<int>? geometryLods,
        Action<string> reportError,
        bool sessionCanEdit = true)
    {
        _session = session;
        _reportError = reportError;
        _geometryLods = geometryLods;
        Metadata = metadata;
        _profileAllowsEditing = metadata.IsEditable;
        _sessionCanEdit = sessionCanEdit;
        _value = session.GetFeature(metadata.Name);
        _minimum = Math.Min(metadata.Minimum, _value);
        _maximum = Math.Max(metadata.Maximum, _value);
        _isEditable = _profileAllowsEditing && _sessionCanEdit;
    }

    public MorphFeatureMetadata Metadata { get; }
    public string Name => Metadata.Name;
    public string Label => Metadata.Label;
    public string CategoryKey => Metadata.CategoryKey;
    public string SubcategoryKey => Metadata.SubcategoryKey;
    public bool IsVisible => Metadata.IsVisible;
    public int SortOrder => Metadata.SortOrder;
    public float Minimum => _extendedSliders ? -RangeExtent : _minimum;
    public float Maximum => _extendedSliders ? RangeExtent : _maximum;
    private float RangeExtent => Math.Max(Math.Abs(_minimum), Math.Abs(_maximum));
    public float Step => Metadata.Step;
    public bool IsEditable
    {
        get => _isEditable;
        private set => SetProperty(ref _isEditable, value);
    }
    public string Description => MetadataTooltipFormatter.Format(Label, Metadata.Description);

    public float Value
    {
        get => _value;
        set
        {
            UpdateRanges(value, expandOnly: true);
            if (SetProperty(ref _value, value))
            {
                try
                {
                    _session.SetFeature(Name, value);
                    var now = Environment.TickCount64;
                    if (now - _lastPreviewLogTick >= 250)
                    {
                        AppLog.Verbose($"FEATURE:PREVIEW {Name}={value:G6}");
                        _lastPreviewLogTick = now;
                    }
                }
                catch (Exception exception)
                {
                    AppLog.Error($"FEATURE:EDIT-FAILED {Name}={value:G6}", exception);
                    SetProperty(ref _value, _session.GetFeature(Name), nameof(Value));
                    _reportError(Loc.Format("loc_VM_morphFeature_changeFailed", Name, AppLog.FilePath));
                }
            }
            UpdateRanges(_value);
        }
    }

    public void BeginEdit()
    {
        AppLog.Verbose($"FEATURE:START {Name}={Value:G6}");
        _session.BeginFeatureEdit(Name);
    }

    public void EndEdit()
    {
        _session.EndFeatureEdit(Name);
        AppLog.Verbose($"FEATURE:END {Name}={Value:G6}");
    }

    public void Refresh()
    {
        var value = _session.GetFeature(Name);
        // Expand before Value, then shrink: a bound Slider must never clamp replayed authored data.
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
        var minimum = Math.Min(Metadata.Minimum, value);
        var maximum = Math.Max(Metadata.Maximum, value);
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

    public void SetPreviewLod(int lodIndex, bool lodHasMorphGeometry)
    {
        _lodHasMorphGeometry = lodHasMorphGeometry;
        _targetWorksAtLod = _geometryLods is null || _geometryLods.Contains(lodIndex);
        RefreshEditability();
    }

    public void SetSessionCanEdit(bool canEdit)
    {
        _sessionCanEdit = canEdit;
        RefreshEditability();
    }

    private void RefreshEditability() =>
        IsEditable = _profileAllowsEditing && _sessionCanEdit && _lodHasMorphGeometry && _targetWorksAtLod;
}
