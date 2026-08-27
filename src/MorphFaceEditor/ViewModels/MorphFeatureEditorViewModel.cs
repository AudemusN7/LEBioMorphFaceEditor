using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed class MorphFeatureEditorViewModel : ObservableObject, IContinuousEditViewModel
{
    private readonly MorphFaceEditingSession _session;
    private readonly Action<string> _reportError;
    private readonly IReadOnlySet<int>? _geometryLods;
    private float _value;
    private bool _isEditable;
    private bool _extendedSliders;
    private float _defaultMinimum;
    private float _defaultMaximum;
    private float _extendedExtent;
    private long _lastPreviewLogTick;

    public MorphFeatureEditorViewModel(
        MorphFaceEditingSession session,
        MorphFeatureMetadata metadata,
        IReadOnlySet<int>? geometryLods,
        Action<string> reportError)
    {
        _session = session;
        _reportError = reportError;
        _geometryLods = geometryLods;
        Metadata = metadata;
        _value = session.GetFeature(metadata.Name);
        _defaultMinimum = Math.Min(metadata.Minimum, _value);
        _defaultMaximum = Math.Max(metadata.Maximum, _value);
        _extendedExtent = Math.Max(Math.Abs(_defaultMinimum), Math.Abs(_defaultMaximum));
        _isEditable = metadata.IsEditable;
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
    public string Description => Metadata.Description;

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
                        AppLog.Information($"Feature preview {Name}={value:G9}.");
                        _lastPreviewLogTick = now;
                    }
                }
                catch (Exception exception)
                {
                    AppLog.Error($"Feature edit failed for {Name}={value:G9}.", exception);
                    SetProperty(ref _value, _session.GetFeature(Name), nameof(Value));
                    _reportError($"Feature '{Name}' could not be changed. Details were written to {AppLog.FilePath}");
                }
            }
        }
    }

    public void BeginEdit()
    {
        AppLog.Information($"Feature drag started: {Name}={Value:G9}.");
        _session.BeginFeatureEdit(Name);
    }

    public void EndEdit()
    {
        _session.EndFeatureEdit(Name);
        AppLog.Information($"Feature drag completed: {Name}={Value:G9}.");
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
        var targetWorksAtLod = _geometryLods is null || _geometryLods.Contains(lodIndex);
        IsEditable = Metadata.IsEditable && lodHasMorphGeometry && targetWorksAtLod;
    }
}
