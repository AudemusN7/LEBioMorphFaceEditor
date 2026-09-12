using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.ViewModels;

public sealed record CustomMaterialSlotOptionViewModel(
    string DisplayName,
    CustomMaterialAssignmentOption? Assignment);

/// <summary>Projects one used imported material slot into an explicit assignment selector.</summary>
public sealed class CustomMaterialSlotEditorViewModel : ObservableObject, IDisposable
{
    private readonly CustomMaterialWorkspace _workspace;
    private readonly CustomMaterialSlot _slot;
    private readonly Action<string> _reportError;
    private CustomMaterialSlotOptionViewModel _selected;
    private bool _refreshing;
    private bool _disposed;

    public CustomMaterialSlotEditorViewModel(
        CustomMaterialWorkspace workspace,
        CustomMaterialSlot slot,
        IReadOnlyList<CustomMaterialAssignmentOption> assignments,
        Action<string> reportError)
    {
        _workspace = workspace;
        _slot = slot;
        _reportError = reportError;
        Options = [
            new CustomMaterialSlotOptionViewModel("Unassigned", null),
            .. assignments.Select(option => new CustomMaterialSlotOptionViewModel(option.Label, option))
        ];
        _selected = Options[0];
        RefreshSelection();
        workspace.ActiveMaterialsChanged += OnWorkspaceChanged;
    }

    public string Label => $"Slot {_slot.MaterialIndex}: {_slot.MaterialName}";
    public string Detail => _slot.Sections.Count == 1
        ? "1 used section"
        : $"{_slot.Sections.Count} used sections";
    public IReadOnlyList<CustomMaterialSlotOptionViewModel> Options { get; }
    public CustomMaterialSlotOptionViewModel Selected
    {
        get => _selected;
        set
        {
            if (value is null || ReferenceEquals(value, _selected)) return;
            var previous = _selected;
            if (!SetProperty(ref _selected, value) || _refreshing) return;
            try
            {
                if (value.Assignment is null) _workspace.Unassign(_slot.MaterialIndex);
                else _workspace.Assign(_slot, value.Assignment);
            }
            catch (Exception exception)
            {
                _refreshing = true;
                _selected = previous;
                OnPropertyChanged();
                _refreshing = false;
                _reportError(exception.Message);
            }
        }
    }

    private void OnWorkspaceChanged(object? sender, CustomMaterialWorkspaceChangedEventArgs e) =>
        RefreshSelection();

    private void RefreshSelection()
    {
        var assignment = _workspace.Assignments
            .FirstOrDefault(value => value.Slot.MaterialIndex == _slot.MaterialIndex)?.Option;
        var selected = assignment is null
            ? Options[0]
            : Options.FirstOrDefault(value => value.Assignment?.Id.Equals(
                assignment.Id, StringComparison.OrdinalIgnoreCase) == true) ?? Options[0];
        if (ReferenceEquals(selected, _selected)) return;
        _refreshing = true;
        SetProperty(ref _selected, selected, nameof(Selected));
        _refreshing = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _workspace.ActiveMaterialsChanged -= OnWorkspaceChanged;
        _disposed = true;
    }
}
