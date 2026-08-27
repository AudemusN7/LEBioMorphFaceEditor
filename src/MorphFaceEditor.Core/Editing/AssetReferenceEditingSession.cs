using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Editing;

public sealed class AssetReferenceEditingSession(AssetIdentity? initial) : IUndoableEditSource
{
    private readonly Stack<(AssetIdentity? Before, AssetIdentity? After)> _undo = new();
    private readonly Stack<(AssetIdentity? Before, AssetIdentity? After)> _redo = new();
    private bool _replaying;

    public event EventHandler? ValueChanged;
    public event EventHandler? EditCommitted;

    public AssetIdentity? Value { get; private set; } = initial;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Set(AssetIdentity? value)
    {
        if (SameReference(Value, value))
        {
            return;
        }
        var before = Value;
        Value = value;
        if (!_replaying)
        {
            _undo.Push((before, value));
            _redo.Clear();
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        var edit = _undo.Pop();
        _redo.Push(edit);
        Replay(edit.Before);
    }

    public void Redo()
    {
        var edit = _redo.Pop();
        _undo.Push(edit);
        Replay(edit.After);
    }

    public void ClearRedo() => _redo.Clear();

    private void Replay(AssetIdentity? value)
    {
        _replaying = true;
        try
        {
            Set(value);
        }
        finally
        {
            _replaying = false;
        }
    }

    private static bool SameReference(AssetIdentity? left, AssetIdentity? right) =>
        ReferenceEquals(left, right) ||
        left is not null && right is not null &&
        string.Equals(left.PackagePath, right.PackagePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.InstancedPath, right.InstancedPath, StringComparison.OrdinalIgnoreCase);
}
