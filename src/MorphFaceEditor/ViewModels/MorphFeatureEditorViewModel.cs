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
    private float _defaultMinimum;
    private float _defaultMaximum;
    private float _extendedExtent;
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
        _defaultMinimum = Math.Min(metadata.Minimum, _value);
        _defaultMaximum = Math.Max(metadata.Maximum, _value);
        _extendedExtent = Math.Max(Math.Abs(_defaultMinimum), Math.Abs(_defaultMaximum));
        _isEditable = _profileAllowsEditing && _sessionCanEdit;
    }

    public MorphFeatureMetadata Metadata { get; }
    public string Name => Metadata.Name;
    public string Label => Metadata.Label;
    public string CategoryKey => Metadata.CategoryKey;
    public string SubcategoryKey => Metadata.SubcategoryKey;
    public bool IsVisible => Metadata.IsVisible;
    public int SortOrder => Metadata.SortOrder;
    public float Minimum => _extendedSliders ? -_extendedExtent : _defaultMinimum;
    public float Maximum => _extendedSliders ? _extendedExtent : _defaultMaximum;
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
            EnsureRangesInclude(value);
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
        EnsureRangesInclude(value);
        SetProperty(ref _value, value, nameof(Value));
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
