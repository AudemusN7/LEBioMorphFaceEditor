using System.Numerics;
using System.Windows.Input;
using System.Windows.Media;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed class MaterialVectorEditorViewModel : ObservableObject
{
    private readonly MaterialEditingSession _session;
    private readonly MaterialParameterDefinition _definition;
    private readonly IHdrColorDialogService _dialog;

    public MaterialVectorEditorViewModel(
        MaterialEditingSession session,
        MaterialParameterDefinition definition,
        IHdrColorDialogService dialog)
    {
        _session = session;
        _definition = definition;
        _dialog = dialog;
        EditCommand = new RelayCommand(Edit);
    }

    public string Name => _definition.Name;
    public string Label => _definition.Label;
    public string Group => _definition.Group;
    public string CategoryKey => _definition.Group;
    public string Description => _definition.Description;
    public ICommand EditCommand { get; }
    public Vector4 Value => _session.GetVector(Name);
    public string ValueSummary => $"R {Value.X:F3}  G {Value.Y:F3}  B {Value.Z:F3}  A {Value.W:F3}";
    // Parameter alpha is preserved in ValueSummary/the editor, but colour swatches
    // remain opaque so sentinel and mask alpha values cannot hide the RGB preview.
    public Brush PreviewBrush => new SolidColorBrush(Color.FromArgb(byte.MaxValue, ToByte(Value.X), ToByte(Value.Y), ToByte(Value.Z)));

    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueSummary));
        OnPropertyChanged(nameof(PreviewBrush));
    }

    private void Edit()
    {
        var before = Value;
        var applied = _dialog.Edit(Label, before, value => _session.PreviewVector(Name, value));
        _session.CompleteVectorPreview(Name, before, applied ?? before, applied is not null);
    }

    private static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);
}
