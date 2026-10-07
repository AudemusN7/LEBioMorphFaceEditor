using System.Numerics;
using MorphFaceEditor.Core.Editing;

namespace MorphFaceEditor.ViewModels;

// The bookkeeping must travel with bone edits through Undo and Redo.
internal sealed class CursedBoneOffsetHistory(Dictionary<string, Vector3> offsets) : IUndoableEditSource
{
    private readonly Stack<(Dictionary<string, Vector3> Before, Dictionary<string, Vector3> After)> _undo = new();
    private readonly Stack<(Dictionary<string, Vector3> Before, Dictionary<string, Vector3> After)> _redo = new();
    public event EventHandler? EditCommitted;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    internal void Set(IReadOnlyDictionary<string, Vector3> values)
    {
        if (offsets.Count == values.Count && values.All(value =>
            offsets.TryGetValue(value.Key, out var current) && current == value.Value)) return;
        var before = new Dictionary<string, Vector3>(offsets, StringComparer.OrdinalIgnoreCase);
        var after = new Dictionary<string, Vector3>(values, StringComparer.OrdinalIgnoreCase);
        Replace(after);
        _undo.Push((before, after));
        _redo.Clear();
        EditCommitted?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        var edit = _undo.Pop();
        Replace(edit.Before);
        _redo.Push(edit);
    }

    public void Redo()
    {
        var edit = _redo.Pop();
        Replace(edit.After);
        _undo.Push(edit);
    }

    public void ClearRedo() => _redo.Clear();
    private void Replace(IReadOnlyDictionary<string, Vector3> values)
    {
        offsets.Clear();
        foreach (var value in values) offsets.Add(value.Key, value.Value);
    }
}
