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
    private IReadOnlyList<HairMeshOption> _options;
    private string _searchText = string.Empty;
    private HairMeshOption? _previewSelection;

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
        _options = options.ToArray();
        Candidates = [];
        _selected = Find(session.Value);
        ApplySearch();
        session.ValueChanged += OnValueChanged;
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? PreviewChanged;

    public string Label { get; }
    public int SlotIndex { get; }
    public IReadOnlyList<HairMeshOption> Options => _options;
    public IReadOnlyList<HairMeshOption> Candidates { get; private set; }
    /// <summary>Candidate currently shown as a transient preview, without changing the authored reference.</summary>
    public HairMeshOption? PreviewSelection => _previewSelection;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                CancelPreview();
                ApplySearch();
            }
        }
    }
    public HairMeshOption Selected
    {
        get => _selected;
        set
        {
            // Filtering can temporarily remove the current item from the ListBox;
            // WPF then attempts to write a null SelectedItem back through the
            // binding. Keep the authored selection until the user picks a candidate.
            if (value is null)
            {
                return;
            }
            CancelPreview();
            if (SetProperty(ref _selected, value))
            {
                _session.Set(value.Identity);
            }
        }
    }
    public AssetIdentity? Value => _session.Value;

    public void SetImportedPreview(AssetIdentity? value)
    {
        if (value is not null && !_options.Any(option => Same(option.Identity, value)))
        {
            _options = [.. _options, new HairMeshOption($"{value.InstancedPath} (imported preview)", value)];
            OnPropertyChanged(nameof(Options));
            ApplySearch();
        }
        _session.Set(value);
    }

    /// <summary>Requests a non-authored attachment preview for the picker highlight.</summary>
    public void Preview(HairMeshOption candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Same(candidate.Identity, _selected.Identity))
        {
            CancelPreview();
            return;
        }
        if (ReferenceEquals(_previewSelection, candidate))
        {
            return;
        }
        _previewSelection = candidate;
        OnPropertyChanged(nameof(PreviewSelection));
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restores the committed attachment after a cancelled picker preview.</summary>
    public void CancelPreview()
    {
        if (_previewSelection is null)
        {
            return;
        }
        _previewSelection = null;
        OnPropertyChanged(nameof(PreviewSelection));
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Commits an explicit picker choice through the normal authored session.</summary>
    public void Commit(HairMeshOption candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        CancelPreview();
        Selected = candidate;
    }

    private HairMeshOption Find(AssetIdentity? value) => Options.First(option => Same(option.Identity, value));

    private void ApplySearch()
    {
        var none = _options[0];
        var filtered = _options.Skip(1)
            .Where(option => MatchesSearch(option, SearchText))
            .ToArray();
        Candidates = [none, .. filtered];
        OnPropertyChanged(nameof(Candidates));
    }

    private static bool MatchesSearch(HairMeshOption option, string searchText)
    {
        var query = searchText.Trim();
        return query.Length == 0 ||
               option.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (option.Identity?.InstancedPath.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (option.Identity?.PackagePath.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool Same(AssetIdentity? left, AssetIdentity? right) =>
        left is null && right is null || left is not null && right is not null &&
        string.Equals(left.InstancedPath, right.InstancedPath, StringComparison.OrdinalIgnoreCase);

    private void OnValueChanged(object? sender, EventArgs e)
    {
        CancelPreview();
        SetProperty(ref _selected, Find(_session.Value), nameof(Selected));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _session.ValueChanged -= OnValueChanged;
}
