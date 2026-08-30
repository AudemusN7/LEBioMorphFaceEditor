namespace MorphFaceEditor.Core.Editing;

public abstract record SemanticEdit;

public sealed record SemanticValueEdit(string Key, float Before, float After) : SemanticEdit;

public sealed record SemanticFeatureBatchEdit(
    IReadOnlyDictionary<string, float> Before,
    IReadOnlyDictionary<string, float> After) : SemanticEdit;

public sealed record SemanticBoneTranslationEdit(
    string BoneName,
    System.Numerics.Vector3 Before,
    System.Numerics.Vector3 After) : SemanticEdit;

internal sealed record SemanticMorphStateEdit(
    MorphFaceAuthoringState Before,
    MorphFaceAuthoringState After) : SemanticEdit;

public sealed class SemanticEditHistory
{
    private readonly Stack<SemanticEdit> _undo = new();
    private readonly Stack<SemanticEdit> _redo = new();
    private string? _activeKey;
    private float _activeBefore;
    private float _activeAfter;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public bool Begin(string key, float currentValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (string.Equals(_activeKey, key, StringComparison.Ordinal))
        {
            return false;
        }

        var committed = CommitActive();
        _activeKey = key;
        _activeBefore = currentValue;
        _activeAfter = currentValue;
        return committed;
    }

    public bool Record(string key, float before, float after)
    {
        if (_activeKey is not null)
        {
            if (string.Equals(_activeKey, key, StringComparison.Ordinal))
            {
                _activeAfter = after;
                return false;
            }

            var committed = CommitActive();
            return Push(new SemanticValueEdit(key, before, after)) || committed;
        }
        return Push(new SemanticValueEdit(key, before, after));
    }

    public bool Record(SemanticEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var committed = CommitActive();
        return Push(edit) || committed;
    }

    public bool End(string key, float currentValue)
    {
        if (_activeKey is null || !string.Equals(_activeKey, key, StringComparison.Ordinal))
        {
            // WPF can deliver a stale pointer-up after a virtualized row has changed
            // data context. It must not close whichever slider is active now.
            return false;
        }
        _activeAfter = currentValue;
        return CommitActive();
    }

    public SemanticEdit PopUndo()
    {
        EnsureIdle();
        var edit = _undo.Pop();
        _redo.Push(edit);
        return edit;
    }

    public SemanticEdit PopRedo()
    {
        EnsureIdle();
        var edit = _redo.Pop();
        _undo.Push(edit);
        return edit;
    }

    public void ClearRedo() => _redo.Clear();

    private bool Push(SemanticEdit edit)
    {
        if (edit is SemanticValueEdit valueEdit && valueEdit.Before == valueEdit.After)
        {
            return false;
        }
        if (edit is SemanticFeatureBatchEdit batchEdit &&
            batchEdit.Before.Count == batchEdit.After.Count &&
            batchEdit.Before.All(value =>
                batchEdit.After.TryGetValue(value.Key, out var after) && value.Value == after))
        {
            return false;
        }
        if (edit is SemanticBoneTranslationEdit boneEdit && boneEdit.Before == boneEdit.After)
        {
            return false;
        }
        _undo.Push(edit);
        _redo.Clear();
        return true;
    }

    private void EnsureIdle()
    {
        _ = CommitActive();
    }

    private bool CommitActive()
    {
        if (_activeKey is null)
        {
            return false;
        }

        var edit = new SemanticValueEdit(_activeKey, _activeBefore, _activeAfter);
        _activeKey = null;
        return Push(edit);
    }
}
