namespace MorphFaceEditor.Core.Editing;

public interface IUndoableEditSource
{
    event EventHandler? EditCommitted;

    bool CanUndo { get; }
    bool CanRedo { get; }

    void Undo();
    void Redo();
    void ClearRedo();
}

public interface IEditorHistoryAggregate : IDisposable
{
    void Commit();
}

/// <summary>
/// Orders semantic edits from independent geometry and material sessions into one editor history.
/// Each source retains its compact payload; this class only records chronological ownership.
/// </summary>
public sealed class EditorUndoCoordinator : IDisposable
{
    private readonly IUndoableEditSource[] _sources;
    private readonly Stack<UndoStep> _undo = new();
    private readonly Stack<UndoStep> _redo = new();
    private bool _replaying;
    private List<IUndoableEditSource>? _aggregateSources;

    public EditorUndoCoordinator(params IUndoableEditSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.Distinct().ToArray();
        foreach (var source in _sources)
        {
            source.EditCommitted += OnEditCommitted;
        }
    }

    public event EventHandler? HistoryChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Groups edits from multiple sources into one chronological Undo step.</summary>
    public IEditorHistoryAggregate BeginAggregate()
    {
        if (_aggregateSources is not null)
        {
            throw new InvalidOperationException("An editor history aggregate is already active.");
        }
        _aggregateSources = [];
        return new AggregateScope(this);
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        var step = _undo.Pop();
        _replaying = true;
        try
        {
            for (var index = step.Sources.Count - 1; index >= 0; index--)
            {
                step.Sources[index].Undo();
            }
        }
        finally
        {
            _replaying = false;
        }
        _redo.Push(step);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }

        var step = _redo.Pop();
        _replaying = true;
        try
        {
            foreach (var source in step.Sources)
            {
                source.Redo();
            }
        }
        finally
        {
            _replaying = false;
        }
        _undo.Push(step);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditCommitted(object? sender, EventArgs e)
    {
        if (_replaying || sender is not IUndoableEditSource source)
        {
            return;
        }

        if (_aggregateSources is not null)
        {
            _aggregateSources.Add(source);
            return;
        }
        Push(new UndoStep([source]));
    }

    private void CompleteAggregate()
    {
        var sources = _aggregateSources
            ?? throw new InvalidOperationException("No editor history aggregate is active.");
        _aggregateSources = null;
        if (sources.Count > 0)
        {
            Push(new UndoStep(sources.ToArray()));
        }
    }

    private void RollBackAggregate()
    {
        var sources = _aggregateSources
            ?? throw new InvalidOperationException("No editor history aggregate is active.");
        _aggregateSources = null;
        _replaying = true;
        try
        {
            for (var index = sources.Count - 1; index >= 0; index--)
            {
                sources[index].Undo();
            }
        }
        finally
        {
            _replaying = false;
        }
        foreach (var source in _sources)
        {
            source.ClearRedo();
        }
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Push(UndoStep step)
    {
        _undo.Push(step);
        _redo.Clear();
        foreach (var candidate in _sources)
        {
            candidate.ClearRedo();
        }
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.EditCommitted -= OnEditCommitted;
        }
    }

    private sealed record UndoStep(IReadOnlyList<IUndoableEditSource> Sources);

    private sealed class AggregateScope(EditorUndoCoordinator owner) : IEditorHistoryAggregate
    {
        private EditorUndoCoordinator? _owner = owner;
        private bool _committed;

        public void Commit() => _committed = true;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
            {
                return;
            }
            if (_committed)
            {
                current.CompleteAggregate();
            }
            else
            {
                current.RollBackAggregate();
            }
        }
    }
}
