namespace MorphFaceEditor.Core.Editing;

public interface IEditorHistoryAggregate : IDisposable
{
    void Commit();
}

/// <summary>Owns the editor's chronological edits, including gestures and atomic composites.</summary>
public sealed class EditorHistory
{
    public EditorHistory() { }
    internal EditorHistory(TextureHistoryStore textureStorage) => _textureStorage = textureStorage;
    private TextureHistoryStore? _textureStorage;
    private readonly Stack<IReadOnlyList<Edit>> _undo = new();
    private readonly Stack<IReadOnlyList<Edit>> _redo = new();
    private object? _gestureKey;
    private Edit? _gesture;
    private List<Edit>? _aggregate;
    private bool _replaying;

    public event EventHandler? HistoryChanged;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    internal TextureHistoryStore.HistoryTextureSnapshot? CaptureTexture(MorphFaceEditor.Core.Materials.DecodedTextureAsset? texture) =>
        texture is null ? null : (_textureStorage ??= new()).Capture(texture);

    public TextureHistoryStatistics TextureStorageStatistics => _textureStorage?.Statistics ?? new(0, 0, 0, 0, 0, 0, 0);

    public Task WaitForTextureWritesAsync() => _textureStorage?.WhenWritesComplete ?? Task.CompletedTask;

    /// <summary>Records typed authored state; matching gesture updates retain its original before state.</summary>
    public bool Record<T>(object key, T before, T after, Action<T> restore)
    {
        if (_replaying || EqualityComparer<T>.Default.Equals(before, after)) return false;
        var edit = new ValueEdit<T>(key, before, after, restore);
        if (Equals(_gestureKey, key))
        {
            _gesture = _gesture is ValueEdit<T> previous ? previous with { After = after } : edit;
        }
        else
        {
            CommitGesture();
            Add(edit);
        }
        return true;
    }

    public void BeginGesture(object key)
    {
        if (Equals(_gestureKey, key)) return;
        CommitGesture();
        _gestureKey = key;
    }

    public void EndGesture(object key)
    {
        // A recycled control can finish after another control has begun editing.
        if (Equals(_gestureKey, key)) CommitGesture();
    }

    public void CommitGesture()
    {
        var edit = _gesture;
        _gestureKey = null;
        _gesture = null;
        if (edit is { IsNoOp: false }) Add(edit);
    }

    public IEditorHistoryAggregate BeginAggregate()
    {
        if (_aggregate is not null) throw new InvalidOperationException("An editor history aggregate is already active.");
        CommitGesture();
        _aggregate = [];
        return new AggregateScope(this);
    }

    public void Undo()
    {
        CommitGesture();
        if (_undo.Count == 0) return;
        var step = _undo.Pop();
        _redo.Push(step);
        Replay(step, after: false);
    }

    public void Redo()
    {
        CommitGesture();
        if (_redo.Count == 0) return;
        var step = _redo.Pop();
        _undo.Push(step);
        Replay(step, after: true);
    }

    private void Add(Edit edit)
    {
        if (_aggregate is not null) _aggregate.Add(edit);
        else Push([edit]);
    }

    private void Push(IReadOnlyList<Edit> edits)
    {
        if (edits.Count == 0) return;
        _undo.Push(edits);
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteAggregate(bool commit)
    {
        CommitGesture();
        var edits = _aggregate ?? throw new InvalidOperationException("No editor history aggregate is active.");
        _aggregate = null;
        if (commit) Push(edits.ToArray());
        else Replay(edits, after: false);
    }

    private void Replay(IReadOnlyList<Edit> edits, bool after)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        _replaying = true;
        try
        {
            if (after) foreach (var edit in edits) Restore(edit);
            else for (var index = edits.Count - 1; index >= 0; index--) Restore(edits[index]);
        }
        finally { _replaying = false; }
        try { HistoryChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception)
        {
            failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
        }
        failure?.Throw();

        void Restore(Edit edit)
        {
            // Stored payloads restore authored values before notifying the preview.
            // A failing listener must not strand the remainder of a composite.
            try { edit.Restore(after); }
            catch (Exception exception)
            {
                failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    private abstract record Edit
    {
        public abstract bool IsNoOp { get; }
        public abstract void Restore(bool after);
    }

    private sealed record ValueEdit<T>(object Key, T Before, T After, Action<T> Apply) : Edit
    {
        public override bool IsNoOp => EqualityComparer<T>.Default.Equals(Before, After);
        public override void Restore(bool after) => Apply(after ? After : Before);
    }

    private sealed class AggregateScope(EditorHistory owner) : IEditorHistoryAggregate
    {
        private EditorHistory? _owner = owner;
        private bool _committed;
        public void Commit() => _committed = true;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.CompleteAggregate(_committed);
    }
}
