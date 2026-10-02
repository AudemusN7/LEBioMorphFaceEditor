using System.ComponentModel;
using System.Runtime.CompilerServices;
using MorphFaceEditor.Localisation;

namespace MorphFaceEditor.Localiser;

public sealed class LocaliserRow : INotifyPropertyChanged
{
    private TranslationEntry? _translation;
    public ReviewEntry Source { get; }
    public string Key => Source.Key;
    public string Group => Source.Group;
    public string References => Source.References;
    public bool EnglishEditable => _translation is null;
    public bool LocalisedEditable => _translation is not null && !Excluded;
    public bool CanReview => Problem.Length == 0 && (_translation is null || Excluded || !string.IsNullOrWhiteSpace(_translation.Text));
    public string English { get => Source.Text; set { if (EnglishEditable) Edit(() => Source.Text = value); } }
    public string Localised { get => _translation is null || Excluded ? "" : _translation.Text; set { if (LocalisedEditable) Edit(() => _translation!.Text = value); } }
    public string Notes { get => _translation?.Note ?? Source.Note; set => Edit(() => { if (_translation is null) Source.Note = value; else _translation.Note = value; }); }
    public bool Excluded { get => Source.DoNotTranslate; set => Edit(() => Source.DoNotTranslate = value); }
    public bool Reviewed
    {
        get => _translation is null || Excluded ? Source.Reviewed : _translation.Reviewed;
        set
        {
            if (value && Problem.Length > 0) return;
            Edit(() => { if (_translation is null || Excluded) Source.Reviewed = value; else _translation.Reviewed = value; });
        }
    }
    public string Problem => Source.Problem.Length > 0 ? Source.Problem : Excluded ? "" : _translation?.Problem ?? "";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action<LocaliserRow, LocaliserRowState, LocaliserRowState>? Edited;

    private LocaliserRowState CaptureState() => new(Source.CaptureState(), _translation, _translation?.CaptureState());
    private void Edit(Action change)
    {
        var before = CaptureState();
        change();
        var after = CaptureState();
        if (before != after) Edited?.Invoke(this, before, after);
    }
    internal void RestoreState(LocaliserRowState state)
    {
        Source.RestoreState(state.Source);
        state.Translation?.RestoreState(state.Target!);
    }

    internal LocaliserRow(ReviewEntry source)
    {
        Source = source;
        Source.PropertyChanged += (_, _) => { _translation?.UpdateEnglish(Source.Text); NotifyAll(); };
    }
    internal void SelectLanguage(TranslationEntry? translation)
    {
        if (_translation is not null) _translation.PropertyChanged -= TranslationChanged;
        _translation = translation;
        if (_translation is not null)
        { _translation.UpdateEnglish(Source.Text); _translation.PropertyChanged += TranslationChanged; }
        NotifyAll();
    }
    private void TranslationChanged(object? sender, PropertyChangedEventArgs e) => NotifyAll();
    private void NotifyAll()
    {
        foreach (var name in new[] { nameof(English), nameof(Localised), nameof(Notes), nameof(Excluded), nameof(Reviewed), nameof(Problem), nameof(EnglishEditable), nameof(LocalisedEditable), nameof(CanReview) }) Notify(name);
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed record LocaliserRowState(ReviewEntryState Source, TranslationEntry? Translation, TranslationEntryState? Target);

public enum SectionState { Default, Reviewed, Excluded, Invalid }
public sealed class LocaliserSection(string name, IReadOnlyList<LocaliserRow> rows) : INotifyPropertyChanged
{
    private SectionState _state;
    public string Name { get; } = name;
    public SectionState State => _state;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh()
    {
        var state = rows.Any(row => row.Problem.Length > 0) ? SectionState.Invalid
            : rows.Any(row => row.Excluded) ? SectionState.Excluded
            : rows.Count > 0 && rows.All(row => row.Reviewed) ? SectionState.Reviewed : SectionState.Default;
        if (_state == state) return;
        _state = state; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
    }
}

public sealed class LocaliserLanguage(ReviewViewModel owner, string code, string name) : INotifyPropertyChanged
{
    public string Code { get; } = code;
    public string Name { get; } = name;
    public bool IsSelected { get => owner.LanguageCode == Code; set { if (value) owner.SelectLanguage(Code); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
}
