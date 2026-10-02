using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using MorphFaceEditor.Localisation;

namespace MorphFaceEditor.Localiser;

public sealed partial class ReviewViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, TranslationWorkspace> _translations = new(StringComparer.Ordinal);
    private string _search = "";
    private string _selectedFilter = "All strings";
    private LocaliserSection? _selectedSection;
    private bool _refreshPending;
    private bool _switching;
    private bool _cellEditing;
    public ReviewWorkspace? Workspace { get; private set; }
    public IReadOnlyList<LocaliserRow> Rows { get; private set; } = [];
    public ICollectionView? Entries { get; private set; }
    public IReadOnlyList<LocaliserSection> Sections { get; private set; } = [];
    public IReadOnlyList<string> Filters { get; } = ["All strings", "Needs review", "Reviewed", "Excluded", "Has notes", "Invalid wording"];
    public IReadOnlyList<LocaliserLanguage> Languages { get; }
    public string LanguageCode { get; private set; } = "en-GB";
    public bool IsEnglish => LanguageCode == "en-GB";
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }
    public string SelectedFilter { get => _selectedFilter; set { if (Set(ref _selectedFilter, value)) Refresh(); } }
    public LocaliserSection? SelectedSection { get => _selectedSection; set { if (Set(ref _selectedSection, value)) Refresh(); } }
    public bool HasWorkspace => Workspace is not null;
    public bool IsDirty => Workspace?.IsDirty == true || _translations.Values.Any(translation => translation.IsDirty);
    public bool CurrentTranslationDirty => !IsEnglish && _translations.GetValueOrDefault(LanguageCode)?.IsDirty == true;
    public IEnumerable<string> DirtyLanguageCodes => (Workspace?.IsDirty == true ? new[] { "en-GB" } : Array.Empty<string>())
        .Concat(_translations.Where(pair => pair.Value.IsDirty).Select(pair => pair.Key));
    public string CurrentPath => IsEnglish ? Workspace?.Path ?? "" : _translations.GetValueOrDefault(LanguageCode)?.Path ?? "";
    public string WindowTitle => $"MFE Localiser · {Languages.First(language => language.Code == LanguageCode).Name}" + (IsDirty ? " •" : "");
    public event PropertyChangedEventHandler? PropertyChanged;

    public ReviewViewModel()
    {
        Languages = new (string Code, string Name)[]
        {
            ("en-GB", "English (UK)"), ("en-US", "English (US)"), ("fr", "Français"), ("es", "Español"), ("de", "Deutsch"),
            ("it", "Italiano"), ("pl", "Polski"), ("ru", "Русский"), ("zh-Hans", "简体中文"), ("ja", "日本語")
        }.Select(language => new LocaliserLanguage(this, language.Code, language.Name)).ToArray();
    }

    public void Open(string path)
    {
        var workspace = ReviewWorkspace.Open(path);
        var repo = SourceReferenceIndex.FindRepository(Path.GetDirectoryName(workspace.Path)!);
        if (repo is not null)
        {
            var references = SourceReferenceIndex.Build(repo, workspace.Entries.Select(entry => entry.Key));
            foreach (var entry in workspace.Entries) entry.References = references[entry.Key];
        }
        foreach (var row in Rows) { row.PropertyChanged -= RowChanged; row.Edited -= RowEdited; }
        ResetHistory(); _cellEditing = false;
        Workspace = workspace; _translations.Clear(); LanguageCode = "en-GB";
        Rows = workspace.Entries.Select(entry => new LocaliserRow(entry)).ToArray();
        Sections = new[] { new LocaliserSection("All sections", Rows) }.Concat(Rows.GroupBy(row => row.Group)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new LocaliserSection(group.Key, group.ToArray()))).ToArray();
        _selectedSection = Sections[0]; _selectedFilter = "All strings"; _search = "";
        Entries = new ListCollectionView(Rows.ToList()) { Filter = Matches };
        foreach (var row in Rows) { row.PropertyChanged += RowChanged; row.Edited += RowEdited; }
        foreach (var language in Languages) language.Refresh();
        foreach (var name in new[] { nameof(Workspace), nameof(Rows), nameof(Entries), nameof(Sections), nameof(SelectedSection), nameof(SelectedFilter), nameof(Search), nameof(HasWorkspace), nameof(LanguageCode), nameof(IsEnglish), nameof(CurrentPath) }) Notify(name);
        Refresh();
    }

    public void SelectLanguage(string code)
    {
        if (LanguageCode == code) return;
        if (!Languages.Any(language => language.Code == code)) throw new ArgumentException("Unknown language.", nameof(code));
        CommitPendingHistory();
        TranslationWorkspace? translation = null;
        if (code != "en-GB" && Workspace is not null)
        {
            if (!_translations.TryGetValue(code, out translation))
            {
                var path = Path.Combine(Path.GetDirectoryName(Workspace.Path)!, $"Strings.{code}.xaml");
                translation = File.Exists(path) ? TranslationWorkspace.Open(Workspace, path) : TranslationWorkspace.Empty(Workspace);
                _translations[code] = translation;
            }
        }
        _switching = true;
        try
        {
            LanguageCode = code;
            foreach (var row in Rows) row.SelectLanguage(translation?.Entries[row.Key]);
            foreach (var language in Languages) language.Refresh();
        }
        finally { _switching = false; }
        Notify(nameof(LanguageCode)); Notify(nameof(IsEnglish)); Notify(nameof(CurrentPath)); Refresh();
    }

    public void LoadLocalisation(string path)
    {
        if (IsEnglish) { Open(path); return; }
        if (Workspace is null) throw new InvalidOperationException("Load an English master first.");
        if (string.Equals(Path.GetFullPath(path), Workspace.Path, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("This is the English master. Select English (UK) to load it.");
        var loaded = TranslationWorkspace.Open(Workspace, path);
        ResetHistory();
        _translations[LanguageCode] = loaded;
        _switching = true;
        try { foreach (var row in Rows) row.SelectLanguage(loaded.Entries[row.Key]); }
        finally { _switching = false; }
        Notify(nameof(CurrentPath)); Refresh();
    }

    public void SaveAs(string path)
    {
        if (Workspace is null) return;
        CommitPendingHistory();
        path = Path.GetFullPath(path);
        if (_translations.Any(pair => pair.Key != LanguageCode && string.Equals(pair.Value.Path, path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("That file is loaded for another language. Choose a separate file.");
        if (IsEnglish) Workspace.SaveAs(path);
        else _translations[LanguageCode].SaveAs(path, Workspace);
        Notify(nameof(CurrentPath)); Refresh();
    }

    public void SetFlags(IEnumerable<LocaliserRow> rows, bool excludedColumn, bool value)
    {
        CommitPendingHistory();
        _switching = true; _historyBatch = true;
        try { foreach (var row in rows.ToArray()) { if (excludedColumn) row.Excluded = value; else row.Reviewed = value; } }
        finally { _switching = false; _historyBatch = false; CommitPendingHistory(); }
        Refresh();
    }

    private bool Matches(object item)
    {
        var row = (LocaliserRow)item;
        if (SelectedSection?.Name is { } section && section != "All sections" && row.Group != section) return false;
        if (SelectedFilter switch
            {
                "Needs review" => row.Reviewed, "Reviewed" => !row.Reviewed, "Excluded" => !row.Excluded,
                "Has notes" => row.Notes.Length == 0, "Invalid wording" => row.Problem.Length == 0, _ => false
            }) return false;
        return string.IsNullOrWhiteSpace(Search) || new[] { row.Key, row.English, row.Localised, row.Notes, row.References }
            .Any(value => value.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_switching) return;
        QueueRefresh();
        Notify(nameof(IsDirty)); Notify(nameof(WindowTitle));
    }
    internal void SetCellEditing(bool editing)
    {
        _cellEditing = editing;
        if (!editing) { CommitPendingHistory(); QueueRefresh(); }
    }
    private void QueueRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => { _refreshPending = false; Refresh(); }, DispatcherPriority.Background);
    }
    internal void Refresh()
    {
        foreach (var section in Sections) section.Refresh();
        // Refresh recreates the grid's inline editors. Keep the active editor (and its
        // caret/undo history) alive until focus leaves it, even if it stops matching a filter.
        if (!_cellEditing && Entries is not null)
        {
            var visible = Entries.Cast<LocaliserRow>().ToHashSet();
            if (Entries.SortDescriptions.Count > 0 || !visible.SetEquals(Rows.Where(row => Matches(row))))
                Entries.Refresh();
        }
        Notify(nameof(IsDirty)); Notify(nameof(WindowTitle));
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
