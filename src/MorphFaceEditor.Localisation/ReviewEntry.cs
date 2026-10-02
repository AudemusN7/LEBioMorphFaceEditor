using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MorphFaceEditor.Localisation;

internal sealed record ReviewEntryState(string Text, string Note, bool Excluded, string? ReviewedHash);

public sealed class ReviewEntry : INotifyPropertyChanged
{
    private string _text;
    private string _textHash;
    private string _note;
    private string? _reviewedHash;
    private bool _doNotTranslate;
    internal string SavedText { get; private set; }
    private EntryAnnotation _savedAnnotation;

    internal ReviewEntry(CatalogueItem item, EntryAnnotation? annotation)
    {
        Key = item.Key;
        Group = item.Group;
        SavedText = _text = item.Text;
        _textHash = StringCatalogue.TextHash(item.Text);
        _savedAnnotation = annotation ?? new EntryAnnotation(Key);
        _note = _savedAnnotation.Note;
        _reviewedHash = _savedAnnotation.ReviewedHash;
        _doNotTranslate = _savedAnnotation.DoNotTranslate;
    }

    public string Key { get; }
    public string Group { get; }
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value; _textHash = StringCatalogue.TextHash(value);
            Notify(); Notify(nameof(Reviewed)); Notify(nameof(Problem)); Notify(nameof(IsDirty));
        }
    }
    public string Note { get => _note; set => Set(ref _note, value); }
    public bool DoNotTranslate { get => _doNotTranslate; set => Set(ref _doNotTranslate, value); }
    public bool Reviewed
    {
        get => _reviewedHash == _textHash;
        set
        {
            var hash = value ? _textHash : null;
            Set(ref _reviewedHash, hash);
        }
    }
    public string Problem
    {
        get
        {
            try { StringCatalogue.ValidateValue(Key, SavedText, Text); return ""; }
            catch (FormatException exception) { return exception.Message; }
        }
    }
    public bool IsDirty => Text != SavedText || Annotation != _savedAnnotation;
    internal EntryAnnotation Annotation => new(Key, _reviewedHash, DoNotTranslate, Note);
    public string References { get; set; } = "No source checkout found. The key is still preserved.";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal ReviewEntryState CaptureState() => new(Text, Note, DoNotTranslate, _reviewedHash);
    internal void RestoreState(ReviewEntryState state)
    {
        if (CaptureState() == state) return;
        _text = state.Text; _textHash = StringCatalogue.TextHash(_text);
        _note = state.Note; _doNotTranslate = state.Excluded; _reviewedHash = state.ReviewedHash;
        foreach (var name in new[] { nameof(Text), nameof(Note), nameof(DoNotTranslate), nameof(Reviewed), nameof(Problem), nameof(IsDirty) }) Notify(name);
    }

    public void Revert()
    {
        Text = SavedText;
        Note = _savedAnnotation.Note;
        DoNotTranslate = _savedAnnotation.DoNotTranslate;
        _reviewedHash = _savedAnnotation.ReviewedHash;
        Notify(nameof(Reviewed)); Notify(nameof(IsDirty));
    }
    internal void AcceptSaved()
    {
        SavedText = Text;
        _savedAnnotation = Annotation;
        Notify(nameof(IsDirty));
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(property); Notify(nameof(IsDirty)); return true;
    }
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
