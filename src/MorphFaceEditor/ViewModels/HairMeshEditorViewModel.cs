using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Models;

namespace MorphFaceEditor.ViewModels;

public sealed record HairMeshOption(string DisplayName, AssetIdentity? Identity)
{
    public override string ToString() => DisplayName;
}

public sealed class HairMeshEditorViewModel : ObservableObject, IDisposable
{
    private readonly AssetReferenceEditingSession _session;
    private HairMeshOption _selected;

    public HairMeshEditorViewModel(
        AssetReferenceEditingSession session,
        IReadOnlyList<PackageAssetListItem> candidates,
        string label,
        int slotIndex)
    {
        _session = session;
        Label = label;
        SlotIndex = slotIndex;
        var options = new List<HairMeshOption> { new("None", null) };
        options.AddRange(candidates.Select(candidate => new HairMeshOption(candidate.DisplayName, candidate.Identity)));
        if (session.Value is { } current && !options.Any(option => Same(option.Identity, current)))
        {
            options.Add(new HairMeshOption($"{current.InstancedPath} (current reference)", current));
        }
        Options = options.ToArray();
        _selected = Find(session.Value);
        session.ValueChanged += OnValueChanged;
    }

    public event EventHandler? SelectionChanged;

    public string Label { get; }
    public int SlotIndex { get; }
    public IReadOnlyList<HairMeshOption> Options { get; }
    public HairMeshOption Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                _session.Set(value.Identity);
            }
        }
    }
    public AssetIdentity? Value => _session.Value;

    private HairMeshOption Find(AssetIdentity? value) => Options.First(option => Same(option.Identity, value));

    private static bool Same(AssetIdentity? left, AssetIdentity? right) =>
        left is null && right is null || left is not null && right is not null &&
        string.Equals(left.InstancedPath, right.InstancedPath, StringComparison.OrdinalIgnoreCase);

    private void OnValueChanged(object? sender, EventArgs e)
    {
        SetProperty(ref _selected, Find(_session.Value), nameof(Selected));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _session.ValueChanged -= OnValueChanged;
}
