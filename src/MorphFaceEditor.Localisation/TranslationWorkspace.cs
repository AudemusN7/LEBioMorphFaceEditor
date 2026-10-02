using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MorphFaceEditor.Localisation;

internal sealed record TranslationEntryState(string Text, string Note, string? ReviewedHash, string? SourceHash);

public sealed class TranslationEntry : INotifyPropertyChanged
{
    private string _text;
    private string _note;
    private string? _reviewedHash;
    private string? _sourceHash;
    private (string Text, string Note, string? Reviewed, string? Source) _saved;
    public string Key { get; }
    public string Text { get => _text; set { if (Set(ref _text, value)) Notify(nameof(Reviewed)); } }
    public string Note { get => _note; set => Set(ref _note, value); }
    public string English { get; private set; }
    public bool Reviewed
    {
        get => Text.Length > 0 && _reviewedHash == StringCatalogue.TextHash(Text) && _sourceHash == StringCatalogue.TextHash(English);
        set
        {
            if (value && (string.IsNullOrWhiteSpace(Text) || Problem.Length > 0)) return;
            _reviewedHash = value ? StringCatalogue.TextHash(Text) : null;
            _sourceHash = value ? StringCatalogue.TextHash(English) : null;
            Notify(); Notify(nameof(IsDirty));
        }
    }
    public string Problem
    {
        get
        {
            if (Text.Length == 0) return "";
            try { StringCatalogue.ValidateValue(Key, English, Text); return ""; }
            catch (FormatException exception) { return exception.Message; }
        }
    }
    public bool IsDirty => _saved != (Text, Note, _reviewedHash, _sourceHash);
    internal EntryAnnotation Annotation => new(Key, _reviewedHash, Note: Note, SourceHash: _sourceHash);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal TranslationEntryState CaptureState() => new(Text, Note, _reviewedHash, _sourceHash);
    internal void RestoreState(TranslationEntryState state)
    {
        if (CaptureState() == state) return;
        _text = state.Text; _note = state.Note; _reviewedHash = state.ReviewedHash; _sourceHash = state.SourceHash;
        foreach (var name in new[] { nameof(Text), nameof(Note), nameof(Reviewed), nameof(Problem), nameof(IsDirty) }) Notify(name);
    }

    internal TranslationEntry(ReviewEntry master, string text, EntryAnnotation? annotation)
    {
        Key = master.Key; English = master.Text; _text = text;
        _note = annotation?.Note ?? master.Note;
        _reviewedHash = annotation?.ReviewedHash; _sourceHash = annotation?.SourceHash;
        AcceptSaved();
    }
    public void UpdateEnglish(string text)
    { if (English == text) return; English = text; Notify(nameof(Reviewed)); Notify(nameof(Problem)); }
    internal void AcceptSaved() => _saved = (Text, Note, _reviewedHash, _sourceHash);
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); Notify(nameof(Problem)); Notify(nameof(IsDirty)); return true;
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TranslationWorkspace
{
    private byte[]? _fingerprint;
    private readonly IReadOnlyDictionary<string, ReviewEntry> _sources;
    private HashSet<string> _savedExclusions;
    public string? Path { get; private set; }
    public IReadOnlyDictionary<string, TranslationEntry> Entries { get; }
    public bool IsDirty => Entries.Values.Any(entry => entry.IsDirty ||
        (entry.Text.Length > 0 && _sources[entry.Key].DoNotTranslate != _savedExclusions.Contains(entry.Key)));

    private TranslationWorkspace(ReviewWorkspace master, string? path, CatalogueData? data, byte[]? fingerprint)
    {
        Path = path; _fingerprint = fingerprint;
        Entries = master.Entries.ToDictionary(entry => entry.Key,
            entry => new TranslationEntry(entry, data?.Strings.GetValueOrDefault(entry.Key) ?? "", data?.Annotations.GetValueOrDefault(entry.Key)), StringComparer.Ordinal);
        _sources = master.Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        _savedExclusions = master.Entries.Where(entry => entry.DoNotTranslate).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        // An older file containing newly excluded strings must be cleaned on its next save.
        if (data is not null) _savedExclusions.ExceptWith(data.Strings.Keys);
    }
    public static TranslationWorkspace Empty(ReviewWorkspace master) => new(master, null, null, null);
    public static TranslationWorkspace Open(ReviewWorkspace master, string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (new FileInfo(path).Length > 16_000_000) throw new FormatException("The localisation is too large.");
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);
        var data = StringCatalogue.Read(stream);
        var keys = master.Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in data.Strings.Keys.Concat(data.Annotations.Keys)) if (!keys.Contains(key)) throw new FormatException($"Unknown localisation key: {key}");
        // Invalid wording is retained in the table so it can be repaired before saving.
        return new TranslationWorkspace(master, path, data, SHA256.HashData(bytes));
    }

    public void SaveAs(string path, ReviewWorkspace master)
    {
        path = System.IO.Path.GetFullPath(path);
        if (string.Equals(path, master.Path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a separate language file; this path is the English master.");
        var bytes = Serialize(master);
        var expected = string.Equals(path, Path, StringComparison.OrdinalIgnoreCase) ? _fingerprint : CatalogueFileWriter.Fingerprint(path);
        var writes = new List<CatalogueWrite> { new(path, bytes, expected) };
        var masterWrite = master.PrepareWrite(master.Path);
        if (master.IsDirty) writes.Add(masterWrite);
        CatalogueFileWriter.Write(writes.ToArray());
        if (master.IsDirty) master.AcceptSaved(masterWrite);
        Path = path; _fingerprint = SHA256.HashData(bytes);
        foreach (var entry in Entries.Values) entry.AcceptSaved();
        _savedExclusions = _sources.Values.Where(entry => entry.DoNotTranslate).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    }

    private byte[] Serialize(ReviewWorkspace master)
    {
        var system = (XNamespace)"clr-namespace:System;assembly=mscorlib";
        var root = new XElement(StringCatalogue.Presentation + "ResourceDictionary",
            new XAttribute("xmlns", StringCatalogue.Presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", StringCatalogue.Xaml.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "system", system.NamespaceName));
        foreach (var source in master.Entries)
        {
            var entry = Entries[source.Key]; entry.UpdateEnglish(source.Text);
            if (source.DoNotTranslate || entry.Text.Length == 0)
            {
                if (entry.Note.Length > 0) root.Add(StringCatalogue.AnnotationComment(new EntryAnnotation(entry.Key, Note: entry.Note)));
                continue;
            }
            StringCatalogue.ValidateValue(entry.Key, source.Text, entry.Text);
            if (entry.Annotation.ReviewedHash is not null || entry.Note.Length > 0) root.Add(StringCatalogue.AnnotationComment(entry.Annotation));
            root.Add(new XElement(system + "String", new XAttribute(StringCatalogue.Xaml + "Key", entry.Key),
                new XAttribute(XNamespace.Xml + "space", "preserve"), entry.Text));
        }
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.None }))
            new XDocument(root).Save(writer);
        return stream.ToArray();
    }
}
