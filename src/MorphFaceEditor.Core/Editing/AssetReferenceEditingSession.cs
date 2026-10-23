using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Editing;

public sealed class AssetReferenceEditingSession(AssetIdentity? initial)
{
    public event EventHandler? ValueChanged;

    public EditorHistory History { get; set; } = new();
    public AssetIdentity? Value { get; private set; } = initial;
    public bool CanUndo => History.CanUndo;
    public bool CanRedo => History.CanRedo;

    public void Set(AssetIdentity? value)
    {
        if (SameReference(Value, value))
        {
            return;
        }
        var before = Value;
        Value = value;
        History.Record(this, before, value, Restore);
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo() => History.Undo();
    public void Redo() => History.Redo();

    private void Restore(AssetIdentity? value)
    {
        Value = value;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameReference(AssetIdentity? left, AssetIdentity? right) =>
        ReferenceEquals(left, right) ||
        left is not null && right is not null &&
        string.Equals(left.PackagePath, right.PackagePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.InstancedPath, right.InstancedPath, StringComparison.OrdinalIgnoreCase);
}
