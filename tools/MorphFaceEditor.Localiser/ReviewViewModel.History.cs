using System.Windows.Input;

namespace MorphFaceEditor.Localiser;

public sealed partial class ReviewViewModel
{
    private sealed record RowEdit(LocaliserRow Row, LocaliserRowState Before, LocaliserRowState After);
    private sealed record HistoryItem(string Language, RowEdit[] Edits);
    private readonly Stack<HistoryItem> _undo = new();
    private readonly Stack<HistoryItem> _redo = new();
    private readonly Dictionary<LocaliserRow, RowEdit> _pendingEdits = new();
    private string _pendingLanguage = "en-GB";
    private bool _historyBatch;
    private bool _replayingHistory;

    public bool CanUndo => _undo.Count > 0 || _pendingEdits.Values.Any(edit => edit.Before != edit.After);
    public bool CanRedo => _redo.Count > 0;
    public void Undo() => MoveHistory(_undo, _redo, undo: true);
    public void Redo() => MoveHistory(_redo, _undo, undo: false);
    internal void CommitPendingCellEdit() => CommitPendingHistory();

    private void RowEdited(LocaliserRow row, LocaliserRowState before, LocaliserRowState after)
    {
        if (_replayingHistory) return;
        if (_pendingLanguage != LanguageCode) CommitPendingHistory();
        _pendingLanguage = LanguageCode;
        var edit = new RowEdit(row, before, after);
        _redo.Clear();
        if (_cellEditing || _historyBatch)
            _pendingEdits[row] = _pendingEdits.TryGetValue(row, out var pending) ? edit with { Before = pending.Before } : edit;
        else
            _undo.Push(new HistoryItem(LanguageCode, [edit]));
        NotifyHistory();
    }

    private void CommitPendingHistory()
    {
        var edits = _pendingEdits.Values.Where(edit => edit.Before != edit.After).ToArray();
        if (edits.Length > 0) _undo.Push(new HistoryItem(_pendingLanguage, edits));
        _pendingEdits.Clear();
        NotifyHistory();
    }

    private void MoveHistory(Stack<HistoryItem> from, Stack<HistoryItem> to, bool undo)
    {
        CommitPendingHistory();
        if (!from.TryPeek(out var item)) return;
        // Bring the affected language into view so an undo never silently edits a hidden draft.
        SelectLanguage(item.Language);
        _replayingHistory = true; _switching = true;
        try
        {
            foreach (var edit in item.Edits) edit.Row.RestoreState(undo ? edit.Before : edit.After);
            foreach (var edit in item.Edits)
                foreach (var translation in _translations.Values)
                    translation.Entries[edit.Row.Key].UpdateEnglish(edit.Row.English);
            from.Pop(); to.Push(item);
        }
        finally { _replayingHistory = false; _switching = false; }
        Refresh(updateSections: true, forceView: true); NotifyHistory();
    }

    private void ResetHistory()
    {
        _undo.Clear(); _redo.Clear(); _pendingEdits.Clear();
        NotifyHistory();
    }
    private void NotifyHistory()
    {
        Notify(nameof(CanUndo)); Notify(nameof(CanRedo));
        CommandManager.InvalidateRequerySuggested();
    }
}
