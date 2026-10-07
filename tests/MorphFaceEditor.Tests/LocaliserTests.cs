using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MorphFaceEditor.Localisation;
using MorphFaceEditor.Localiser;

namespace MorphFaceEditor.Tests;

internal static class LocaliserTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("localiser: full master review round-trip preserves every key and string", CorpusRoundTrip),
        new("localiser: a wording edit preserves master and translation file layout", WordingPreservesLayout),
        new("localiser: editing a real English label changes only its wording bytes", RealLabelPreservesLayout),
        new("localiser: wording changes invalidate reviews and revert restores them", ReviewLifecycle),
        new("localiser: notes and protection survive XML-sensitive characters", AnnotationRoundTrip),
        new("localiser: excluded strings cannot be translated and are omitted from saved languages", ProtectedStrings),
        new("localiser: invalid wording cannot overwrite the master", InvalidSave),
        new("localiser: concurrent edits and locked files preserve the master and draft", SafeSave),
        new("localiser: source references retain exact keys and line numbers", SourceReferences),
        new("localiser: language drafts persist Unicode notes review stamps and missing strings", TranslationRoundTrip),
        new("localiser: combined language and master save rolls back if either file fails", TranslationSafeSave),
        new("localiser: undo groups typing restores reviews and survives language changes and saving", HistoryLifecycle),
        new("localiser: undo reverses a 200-row bulk toggle in one step", BulkHistory),
        new("localiser: inline typing retains editors caret and selection until filtered edits finish", InlineTyping),
        new("localiser: dark table bulk edits 200 rows and retains all language drafts", WindowSmoke)
    ];

    private static void WordingPreservesLayout() => Scratch(root =>
    {
        var original = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:system=\"clr-namespace:System;assembly=mscorlib\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\r\n" +
            "    <!-- Keep this section comment -->\r\n" +
            "    <system:String xml:space='preserve' x:Key='loc_First'>First &amp; original</system:String>\r\n" +
            "    <system:String x:Key=\"loc_Second\" xml:space=\"preserve\"><![CDATA[Second < untouched]]></system:String>\r\n" +
            "</ResourceDictionary>\r\n";
        var masterPath = Path.Combine(root, "master.xaml");
        var languagePath = Path.Combine(root, "language.xaml");
        var encoding = new UTF8Encoding(true);
        File.WriteAllText(masterPath, original, encoding);
        File.WriteAllText(languagePath, original, encoding);
        var master = ReviewWorkspace.Open(masterPath);
        var language = TranslationWorkspace.Open(master, languagePath);
        master.Entries.Single(entry => entry.Key == "loc_First").Text = "Master & changed";
        master.Save();
        TestAssert.True(File.ReadAllBytes(masterPath).SequenceEqual(encoding.GetPreamble().Concat(
            encoding.GetBytes(original.Replace("First &amp; original", "Master &amp; changed", StringComparison.Ordinal)))),
            "Master save changed untouched bytes, comments, CDATA, BOM or line endings.");
        language.Entries["loc_First"].Text = "Translated & changed";
        language.SaveAs(languagePath, master);
        TestAssert.True(File.ReadAllBytes(languagePath).SequenceEqual(encoding.GetPreamble().Concat(
            encoding.GetBytes(original.Replace("First &amp; original", "Translated &amp; changed", StringComparison.Ordinal)))),
            "Translation save reformatted untouched content.");
        var saved = File.ReadAllBytes(languagePath);
        language.SaveAs(languagePath, master);
        TestAssert.True(saved.SequenceEqual(File.ReadAllBytes(languagePath)), "Repeated translation save changed bytes.");
    });

    private static void RealLabelPreservesLayout() => Scratch(root =>
    {
        var source = FindMaster();
        var masterPath = Path.Combine(root, "Strings.en-GB.xaml");
        var languagePath = Path.Combine(root, "Strings.en-US.xaml");
        File.Copy(source, masterPath);
        File.Copy(Path.Combine(Path.GetDirectoryName(source)!, "Strings.en-US.xaml"), languagePath);
        var master = ReviewWorkspace.Open(masterPath);
        var language = TranslationWorkspace.Open(master, languagePath);
        const string key = "loc_Services_MetadataText_Archetype_HumanFemale_hed_eyeshadow_tint_scalar_32e85d_Label";
        foreach (var path in new[] { masterPath, languagePath })
        {
            var original = File.ReadAllText(path);
            if (path == masterPath)
            {
                master.Entries.Single(entry => entry.Key == key).Text = "Eyeliner Strength QA";
                master.Save();
            }
            else
            {
                language.Entries[key].Text = "Eyeliner Strength QA";
                language.SaveAs(path, master);
            }
            TestAssert.Equal(original.Replace(
                ">Eyeliner Strength</system:String>", ">Eyeliner Strength QA</system:String>", StringComparison.Ordinal),
                File.ReadAllText(path));
        }
    });

    private static void CorpusRoundTrip() => Scratch(root =>
    {
        var path = Path.Combine(root, "Strings.en-GB.xaml");
        File.Copy(FindMaster(), path);
        var original = Read(path);
        var bytes = File.ReadAllBytes(path);
        var workspace = ReviewWorkspace.Open(path);
        TestAssert.Equal(original.Strings.Count, workspace.Entries.Count);
        TestAssert.Equal("ArchetypeTag", workspace.Entries.Single(entry => entry.Key == "loc_ArchetypeTag_Asari").Group);
        foreach (var entry in workspace.Entries) entry.Reviewed = true;
        workspace.SaveAs(path); // Exercise writing even when the master is already fully reviewed.
        TestAssert.True(!workspace.IsDirty, "Saved workspace is still dirty.");
        TestAssert.True(File.ReadAllBytes(workspace.BackupPath).SequenceEqual(bytes), "Backup does not contain the prior master.");
        var saved = Read(path);
        TestAssert.Equal(original.Strings.Count, saved.Strings.Count);
        foreach (var (key, value) in original.Strings) TestAssert.Equal(value, saved.Strings[key]);
        TestAssert.True(saved.Document.ToString().Contains("English (UK) master"), "Master comments were lost.");
        var reopened = ReviewWorkspace.Open(path);
        TestAssert.True(reopened.Entries.All(entry => entry.Reviewed), "Review marks did not persist.");
        var savedBytes = File.ReadAllBytes(path);
        reopened.Save();
        TestAssert.True(File.ReadAllBytes(path).SequenceEqual(savedBytes), "No-op save changed the file.");
        reopened.Entries[0].Note = "One note";
        reopened.Save();
        TestAssert.Equal(original.Strings.Count, Read(path).Annotations.Count);
    });

    private static void ReviewLifecycle() => Scratch(root =>
    {
        var path = CreateMaster(root);
        var workspace = ReviewWorkspace.Open(path);
        var entry = workspace.Entries[0];
        entry.Reviewed = true;
        workspace.Save();
        entry.Text = "New wording {0}";
        TestAssert.True(!entry.Reviewed && entry.IsDirty, "A wording edit retained its reviewed state.");
        entry.Revert();
        TestAssert.True(entry.Reviewed && !entry.IsDirty, "Revert lost the saved review.");
        entry.Text = "New wording {0}";
        workspace.Save();
        TestAssert.True(!ReviewWorkspace.Open(path).Entries[0].Reviewed, "The old review accepted new wording.");
        entry.Reviewed = true;
        workspace.Save();
        var document = File.ReadAllText(path).Replace("New wording {0}", "External wording {0}", StringComparison.Ordinal);
        File.WriteAllText(path, document);
        TestAssert.True(!ReviewWorkspace.Open(path).Entries[0].Reviewed, "An external wording edit retained a stale review stamp.");
    });

    private static void AnnotationRoundTrip() => Scratch(root =>
    {
        var path = CreateMaster(root);
        var workspace = ReviewWorkspace.Open(path);
        var entry = workspace.Entries[0];
        entry.Note = "Keep BIOG_HMM -- \"quoted\" <xml> & français 日本語\nSecond line -->";
        entry.DoNotTranslate = true;
        entry.Reviewed = true;
        workspace.Save();
        var reopened = ReviewWorkspace.Open(path).Entries[0];
        TestAssert.Equal(entry.Note, reopened.Note);
        TestAssert.True(reopened.DoNotTranslate && reopened.Reviewed, "Annotation flags changed.");
        var master = Read(path);
        StringCatalogue.ValidateOverlay(master.Strings, master.Strings, master.Annotations);
        var invalid = Xml("<!-- localiser: {bad json} --><system:String x:Key=\"loc_one\">One</system:String>");
        RejectFormat(() => ReadText(invalid));
        var duplicate = Xml("<!-- localiser: {\"Key\":\"loc_one\"} --><!-- localiser: {\"Key\":\"loc_one\"} --><system:String x:Key=\"loc_one\">One</system:String>");
        RejectFormat(() => ReadText(duplicate));
    });

    private static void ProtectedStrings() => Scratch(root =>
    {
        var workspace = ReviewWorkspace.Open(CreateMaster(root));
        workspace.Entries[0].DoNotTranslate = true;
        workspace.Entries[1].Note = "Translator context";
        workspace.Save();
        var master = Read(workspace.Path);
        RejectFormat(() => StringCatalogue.ValidateOverlay(master.Strings,
            new Dictionary<string, string> { ["loc_one"] = "Traduit {0}" }, master.Annotations));
        StringCatalogue.ValidateOverlay(master.Strings, new Dictionary<string, string> { ["loc_one"] = "One {0}" }, master.Annotations);
        StringCatalogue.ValidateOverlay(master.Strings, new Dictionary<string, string> { ["loc_two"] = "Deux" }, master.Annotations);
        var language = TranslationWorkspace.Empty(workspace);
        language.Entries["loc_one"].Text = "Traduit {0}";
        language.Entries["loc_two"].Text = "Deux";
        var languagePath = Path.Combine(root, "Strings.fr.xaml");
        language.SaveAs(languagePath, workspace);
        var template = Read(languagePath);
        TestAssert.True(!template.Strings.ContainsKey("loc_one"), "Protected key leaked into translator template.");
        TestAssert.Equal("Deux", template.Strings["loc_two"]);
        TestAssert.Equal("Translator context", template.Annotations["loc_two"].Note);
        workspace.Entries[0].DoNotTranslate = false;
        workspace.Save();
        TestAssert.True(!Read(workspace.Path).Annotations.ContainsKey("loc_one"), "Cleared protection was retained in the master.");
    });

    private static void InvalidSave() => Scratch(root =>
    {
        var path = CreateMaster(root);
        var original = File.ReadAllBytes(path);
        var workspace = ReviewWorkspace.Open(path);
        workspace.Entries[0].Text = "Missing placeholder";
        RejectFormat(workspace.Save);
        TestAssert.True(File.ReadAllBytes(path).SequenceEqual(original) && workspace.IsDirty, "Invalid save lost original or draft.");
        workspace.Entries[0].Text = "Malformed {0";
        RejectFormat(workspace.Save);
        TestAssert.Equal(0, Directory.GetFiles(root, "*.tmp").Length);
        StringCatalogue.ValidateValue("loc_one", "X {0:N2} and {1}", "{1}: {0:N2}");
        RejectFormat(() => StringCatalogue.ValidateValue("loc_EditorFileDrop_AllFilesLabel", "All files", "Bad|label"));
    });

    private static void SafeSave() => Scratch(root =>
    {
        var path = CreateMaster(root);
        var workspace = ReviewWorkspace.Open(path);
        workspace.Entries[0].Text = "Edited {0}";
        File.AppendAllText(path, "\n<!-- external change -->");
        var external = File.ReadAllBytes(path);
        RejectIo(workspace.Save);
        TestAssert.True(File.ReadAllBytes(path).SequenceEqual(external) && workspace.IsDirty, "Concurrent edit was overwritten.");
        workspace = ReviewWorkspace.Open(path);
        workspace.Entries[0].Text = "Edited {0}";
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) RejectIo(workspace.Save);
        TestAssert.True(File.ReadAllBytes(path).SequenceEqual(external) && workspace.IsDirty, "Locked save lost data.");
        TestAssert.Equal(0, Directory.GetFiles(root, "*.tmp").Length);
        workspace.Save();
        TestAssert.Equal("Edited {0}", Read(path).Strings["loc_one"]);
        var savedBytes = File.ReadAllBytes(path);
        workspace.Entries[0].Note = "Another edit";
        using (File.Open(workspace.BackupPath, FileMode.Open, FileAccess.Read, FileShare.None)) RejectIo(workspace.Save);
        TestAssert.True(File.ReadAllBytes(path).SequenceEqual(savedBytes) && workspace.IsDirty, "A backup failure lost the master or draft.");
        TestAssert.Equal(0, Directory.GetFiles(root, "*.tmp").Length);
    });

    private static void SourceReferences() => Scratch(root =>
    {
        var source = Path.Combine(root, "src", "Demo");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(root, "MorphFaceEditor.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(source, "View.cs"), "// source\nLoc.Text(\"loc_one\"); Loc.Text(\"loc_one\");\nLoc.Text(\"loc_one_extra\");");
        var index = SourceReferenceIndex.Build(root, ["loc_one", "loc_one_extra"]);
        TestAssert.Equal("src" + Path.DirectorySeparatorChar + "Demo" + Path.DirectorySeparatorChar + "View.cs:2", index["loc_one"]);
        TestAssert.True(index["loc_one_extra"].EndsWith(":3"), "A key matched another key's prefix.");
        TestAssert.Equal(root, SourceReferenceIndex.FindRepository(source));
    });

    private static void TranslationRoundTrip() => Scratch(root =>
    {
        var master = ReviewWorkspace.Open(CreateMaster(root));
        var language = TranslationWorkspace.Empty(master);
        var first = language.Entries["loc_one"];
        first.Text = "Français 日本語 {0}\nDeuxième ligne";
        first.Note = "Context -- <xml> & \"quotes\"";
        first.Reviewed = true;
        language.Entries["loc_three"].Note = "A note before translation exists";
        var path = Path.Combine(root, "Strings.fr.xaml");
        language.SaveAs(path, master);
        var reopened = TranslationWorkspace.Open(master, path);
        TestAssert.Equal(first.Text, reopened.Entries["loc_one"].Text);
        TestAssert.True(reopened.Entries["loc_one"].Reviewed, "Translation review was lost.");
        TestAssert.Equal(first.Note, reopened.Entries["loc_one"].Note);
        TestAssert.Equal("", reopened.Entries["loc_three"].Text);
        TestAssert.Equal("A note before translation exists", reopened.Entries["loc_three"].Note);
        TestAssert.True(!Read(path).Strings.ContainsKey("loc_three"), "Missing translation overwrote runtime fallback.");
        master.Entries[0].Text = "One revised {0}";
        reopened.Entries["loc_one"].UpdateEnglish(master.Entries[0].Text);
        TestAssert.True(!reopened.Entries["loc_one"].Reviewed, "Master changes retained a stale translation review.");
        first.Text = "Missing placeholder";
        var original = File.ReadAllBytes(path);
        RejectFormat(() => language.SaveAs(path, master));
        TestAssert.True(File.ReadAllBytes(path).SequenceEqual(original), "Invalid translation overwrote the prior file.");
        var invalidPath = Path.Combine(root, "invalid.xaml");
        File.WriteAllText(invalidPath, Xml("<system:String x:Key=\"loc_one\">Broken {0</system:String>"));
        var invalid = TranslationWorkspace.Open(master, invalidPath);
        TestAssert.True(invalid.Entries["loc_one"].Problem.Length > 0, "Invalid imported wording was not flagged.");
        invalid.Entries["loc_one"].Text = "Repaired {0}";
        invalid.SaveAs(invalidPath, master);
    });

    private static void TranslationSafeSave() => Scratch(root =>
    {
        var master = ReviewWorkspace.Open(CreateMaster(root));
        master.Entries[0].Reviewed = true; master.Save();
        var language = TranslationWorkspace.Empty(master);
        language.Entries["loc_one"].Text = "Un {0}";
        var path = Path.Combine(root, "Strings.fr.xaml");
        language.SaveAs(path, master);
        master.Entries[0].DoNotTranslate = true;
        TestAssert.True(language.IsDirty, "A new exclusion did not mark the affected localisation as needing save.");
        master.Entries[0].DoNotTranslate = false;
        var originalMaster = File.ReadAllBytes(master.Path);
        var originalLanguage = File.ReadAllBytes(path);
        master.Entries[1].DoNotTranslate = true;
        language.Entries["loc_one"].Text = "Un changé {0}";
        using (File.Open(master.BackupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            RejectIo(() => language.SaveAs(path, master));
        TestAssert.True(File.ReadAllBytes(master.Path).SequenceEqual(originalMaster) &&
            File.ReadAllBytes(path).SequenceEqual(originalLanguage), "A combined save partially installed files.");
        TestAssert.True(master.IsDirty && language.IsDirty, "A failed combined save lost the drafts.");
        TestAssert.Equal(0, Directory.GetFiles(root, "*.tmp").Length);
        language.SaveAs(path, master);
        TestAssert.True(ReviewWorkspace.Open(master.Path).Entries[1].DoNotTranslate, "Global exclusions were not saved with the language.");
        RejectIo(() => language.SaveAs(master.Path, master));
        language.Entries["loc_one"].Text = "Another {0}";
        File.AppendAllText(path, "\n<!-- external change -->");
        var external = File.ReadAllBytes(path);
        RejectIo(() => language.SaveAs(path, master));
        TestAssert.True(File.ReadAllBytes(path).SequenceEqual(external), "A concurrent locale edit was overwritten.");
        language.SaveAs(Path.Combine(root, "copy.xaml"), master);
    });

    private static void HistoryLifecycle() => Scratch(root => OnSta(() =>
    {
        var path = CreateMaster(root);
        var review = new ReviewViewModel();
        review.Open(path);
        var row = review.Rows[0];
        review.SetFlags([row], excludedColumn: false, value: true);
        review.SaveAs(path);
        review.Open(path);
        row = review.Rows[0];
        TestAssert.True(!review.CanUndo && !review.CanRedo, "Loading a master retained an old undo stack.");
        review.SetCellEditing(true);
        row.English = "Edited {0}";
        row.English += " over several keystrokes";
        TestAssert.True(review.CanUndo && !row.Reviewed, "An active text edit was not undoable.");
        review.Undo();
        TestAssert.Equal("One {0}", row.English);
        TestAssert.True(row.Reviewed && !review.Workspace!.IsDirty, "Undo did not restore the original review and clean state.");
        review.Redo();
        TestAssert.Equal("Edited {0} over several keystrokes", row.English);
        TestAssert.True(!row.Reviewed, "Redo incorrectly reviewed the edited wording.");
        review.SetCellEditing(false);
        review.Undo();
        TestAssert.True(!review.CanUndo && review.CanRedo, "Typing was split into multiple undo steps.");
        row.English = row.English;
        TestAssert.True(review.CanRedo, "A no-op edit cleared redo.");

        review.SelectLanguage("fr");
        row.Localised = "Un {0}";
        TestAssert.True(!review.CanRedo, "A new edit retained the abandoned redo branch.");
        row.Reviewed = true;
        review.SelectLanguage("ja");
        row.Notes = "Japanese context";
        review.Undo();
        TestAssert.Equal("", row.Notes);
        review.Undo();
        TestAssert.Equal("fr", review.LanguageCode);
        TestAssert.Equal("Un {0}", row.Localised);
        TestAssert.True(!row.Reviewed, "Undoing a translation review affected its wording.");
        review.Undo();
        TestAssert.Equal("", row.Localised);
        review.Redo(); review.Redo();
        TestAssert.True(row.Reviewed, "Redo lost the translation review stamp.");
        review.SelectLanguage("en-GB");
        row.English = "Revised source {0}";
        review.SelectLanguage("fr");
        TestAssert.True(!row.Reviewed, "Changing English left a translation reviewed.");
        review.Undo();
        TestAssert.Equal("en-GB", review.LanguageCode);
        TestAssert.True(row.Reviewed, "Undo lost the master review stamp.");
        review.SelectLanguage("fr");
        TestAssert.True(row.Reviewed, "Undo did not restore translation review validity.");
        review.SelectLanguage("en-GB");
        row.Notes = "Saved context";
        review.SaveAs(path);
        TestAssert.True(!review.Workspace!.IsDirty && review.CanUndo, "Saving cleared history or retained a dirty master.");
        review.Undo();
        TestAssert.Equal("", row.Notes);
        TestAssert.True(review.Workspace.IsDirty, "Undo after saving did not mark the draft dirty.");
        TestAssert.Equal("Saved context", ReviewWorkspace.Open(path).Entries[0].Note);
        review.Redo();
        TestAssert.True(!review.Workspace.IsDirty, "Redo to the saved state remained dirty.");
        review.SelectLanguage("fr");
        review.SaveAs(Path.Combine(root, "Strings.fr.xaml"));
        review.LoadLocalisation(Path.Combine(root, "Strings.fr.xaml"));
        TestAssert.True(!review.CanUndo && !review.CanRedo, "Loading another file retained history tied to the old draft.");
    }));

    private static void BulkHistory() => Scratch(root => OnSta(() =>
    {
        var path = Path.Combine(root, "Strings.en-GB.xaml");
        File.Copy(FindMaster(), path);
        var review = new ReviewViewModel();
        review.Open(path);
        review.SetFlags(review.Rows.Take(200), excludedColumn: true, value: false);
        review.SetFlags(review.Rows.Take(200), excludedColumn: false, value: false);
        review.SaveAs(path); review.Open(path);
        var rows = review.Rows.Take(200).ToArray();
        review.SetFlags(rows, excludedColumn: true, value: true);
        TestAssert.True(rows.All(row => row.Excluded) && review.CanUndo, "Bulk exclusions were not recorded.");
        review.SelectedFilter = "Excluded";
        review.Undo();
        TestAssert.True(rows.All(row => !row.Excluded) && !review.CanUndo, "Undo did not reverse all 200 exclusions in one step.");
        TestAssert.True(!review.Workspace!.IsDirty, "Bulk undo did not restore the saved state.");
        review.Redo();
        TestAssert.True(rows.All(row => row.Excluded), "Redo did not restore all 200 exclusions.");
        review.Undo();
        review.SetFlags(rows, excludedColumn: false, value: true);
        TestAssert.True(rows.All(row => row.Reviewed) && !review.CanRedo, "Bulk reviews failed or retained stale redo.");
        review.Undo();
        TestAssert.True(rows.All(row => !row.Reviewed) && !review.CanUndo, "Bulk reviews required more than one undo.");
    }));

    private static void InlineTyping() => Scratch(root => OnSta(() =>
    {
        var path = CreateMaster(root);
        var window = new MorphFaceEditor.Localiser.MainWindow();
        var review = window.Review;
        review.Open(path);
        var grid = (DataGrid)window.FindName("StringList");
        var row = review.Rows.Single(item => item.Key == "loc_two");
        var resets = 0;
        review.Entries!.CollectionChanged += (_, args) =>
        { if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };

        TextBox Editor(string property)
        {
            Layout(window);
            return Visuals(grid).OfType<TextBox>().Single(box => ReferenceEquals(box.DataContext, row) &&
                box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == property);
        }
        void Focus(TextBox editor, bool entering) => editor.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice, 0, entering ? grid : editor, entering ? editor : grid)
            { RoutedEvent = entering ? System.Windows.Input.Keyboard.GotKeyboardFocusEvent : System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
        void Type(TextBox editor, string text, Func<string> stored)
        {
            grid.SelectedItem = row;
            Focus(editor, entering: true);
            var before = resets;
            var expected = editor.Text;
            editor.CaretIndex = expected.Length;
            foreach (var character in text)
            {
                var position = editor.CaretIndex;
                editor.SelectedText = character.ToString();
                editor.CaretIndex = position + 1;
                expected += character;
                Layout(window); // Drain the same queued view refresh that used to end each keystroke.
                TestAssert.Equal(expected, stored());
                TestAssert.Equal(expected.Length, editor.CaretIndex);
                TestAssert.Equal(before, resets);
                TestAssert.True(Visuals(grid).Contains(editor), "Typing replaced the active cell editor.");
                TestAssert.True(grid.SelectedItems.Contains(row), "Typing cleared the row selection.");
            }
            TestAssert.True(review.IsDirty, "Inline edits were not tracked immediately.");
        }

        // A reviewed row must stay visible while its new wording invalidates the review.
        review.SetFlags([row], excludedColumn: false, value: true);
        review.SelectedFilter = "Reviewed";
        var english = Editor(nameof(LocaliserRow.English));
        Type(english, " revised\n日本語", () => row.English);
        TestAssert.True(!english.IsUndoEnabled, "Native cell history competes with the application history.");
        var editedEnglish = row.English;
        System.Windows.Input.ApplicationCommands.Undo.Execute(null, english);
        Layout(window);
        TestAssert.Equal("Two", row.English);
        TestAssert.True(row.Reviewed && Visuals(grid).Contains(english), "Undo lost the review or active editor.");
        System.Windows.Input.ApplicationCommands.Redo.Execute(null, english);
        Layout(window);
        TestAssert.Equal(editedEnglish, row.English);
        TestAssert.True(!row.Reviewed, "Typing did not invalidate the old review.");
        TestAssert.Equal(1, review.Entries.Cast<object>().Count());
        Focus(english, entering: false); Layout(window);
        TestAssert.Equal(0, review.Entries.Cast<object>().Count());

        review.SelectedFilter = "All strings";
        row.Notes = "Context";
        review.SelectedFilter = "Has notes";
        var notes = Editor(nameof(LocaliserRow.Notes));
        Type(notes, " — multiline\nnotes", () => row.Notes);
        var editedNotes = row.Notes;
        System.Windows.Input.ApplicationCommands.Undo.Execute(null, notes);
        Layout(window);
        TestAssert.Equal("Context", row.Notes);
        System.Windows.Input.ApplicationCommands.Redo.Execute(null, notes);
        Layout(window);
        TestAssert.Equal(editedNotes, row.Notes);
        notes.SelectAll(); notes.SelectedText = ""; Layout(window);
        TestAssert.Equal("", row.Notes);
        TestAssert.Equal(1, review.Entries.Cast<object>().Count());
        Focus(notes, entering: false); Layout(window);
        TestAssert.Equal(0, review.Entries.Cast<object>().Count());

        review.SelectedFilter = "All strings";
        review.SelectLanguage("fr");
        row.Localised = "Deux";
        review.SetFlags([row], excludedColumn: false, value: true);
        review.SelectedFilter = "Reviewed";
        var localised = Editor(nameof(LocaliserRow.Localised));
        Type(localised, " modifié\n中文", () => row.Localised);
        var editedTranslation = row.Localised;
        System.Windows.Input.ApplicationCommands.Undo.Execute(null, localised);
        Layout(window);
        TestAssert.Equal("Deux", row.Localised);
        TestAssert.True(row.Reviewed, "Cell Undo lost the translated wording's review.");
        System.Windows.Input.ApplicationCommands.Redo.Execute(null, localised);
        Layout(window);
        TestAssert.Equal(editedTranslation, row.Localised);
        TestAssert.True(!row.Reviewed, "Typing retained the stale translation review.");
        Focus(localised, entering: false); Layout(window);
        TestAssert.Equal(0, review.Entries.Cast<object>().Count());
        review.SaveAs(Path.Combine(root, "Strings.fr.xaml"));
        var saved = TranslationWorkspace.Open(ReviewWorkspace.Open(path), Path.Combine(root, "Strings.fr.xaml"));
        TestAssert.Equal(row.Localised, saved.Entries[row.Key].Text);
        window.Close();
    }));

    private static void WindowSmoke() => Scratch(root => OnSta(() =>
    {
        var path = Path.Combine(root, "Strings.en-GB.xaml");
        File.Copy(FindMaster(), path);
        // Bulk-edit assertions start from a neutral copy, regardless of the author's saved review policy.
        var scratchMaster = ReviewWorkspace.Open(path);
        foreach (var entry in scratchMaster.Entries) { entry.DoNotTranslate = false; entry.Reviewed = false; }
        scratchMaster.Save();
        var window = new MorphFaceEditor.Localiser.MainWindow();
        var review = window.Review;
        review.Open(path);
        Layout(window);
        var grid = (DataGrid)window.FindName("StringList");
        TestAssert.Equal(6, grid.Columns.Count);
        TestAssert.Equal(6, ((TabControl)window.FindName("FilterTabs")).Items.Count);
        TestAssert.Equal(10, ((ItemsControl)window.FindName("LanguageOptions")).Items.Count);
        var undo = (Button)window.FindName("UndoButton");
        var redo = (Button)window.FindName("RedoButton");
        var search = (TextBox)window.FindName("SearchBox");
        TestAssert.True(undo.TranslatePoint(new Point(), search).X < 0 && redo.TranslatePoint(new Point(), search).X < 0 &&
            Math.Abs(undo.TranslatePoint(new Point(), search).Y) < 2, "Undo/Redo are not immediately left of search.");
        TestAssert.True(!System.Windows.Input.ApplicationCommands.Undo.CanExecute(null, undo) &&
            !System.Windows.Input.ApplicationCommands.Redo.CanExecute(null, redo), "Empty history buttons are enabled.");
        TestAssert.True(window.FindName("WordingEditor") is null && window.FindName("ReviewNextButton") is null, "Old panels remain.");
        var colours = MorphFaceEditor.Infrastructure.ThemeManager.ReadColours("Dark");
        TestAssert.Equal(colours["Palette.Window"], ((SolidColorBrush)window.FindResource("WindowBackground")).Color);
        TestAssert.Equal(colours["Palette.Ready"], ((SolidColorBrush)window.FindResource("StatusReady")).Color);
        TestAssert.Equal(colours["Palette.Pending"], ((SolidColorBrush)window.FindResource("StatusPending")).Color);
        TestAssert.Equal(colours["Palette.Failed"], ((SolidColorBrush)window.FindResource("StatusFailed")).Color);
        Layout(window);
        foreach (var row in review.Rows.Take(200)) grid.SelectedItems.Add(row);
        var checkbox = Visuals(grid).OfType<CheckBox>().First(box => Equals(box.Tag, "Excluded") && box.DataContext is LocaliserRow);
        checkbox.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        TestAssert.Equal(200, review.Rows.Count(row => row.Excluded));
        TestAssert.Equal(SectionState.Excluded, review.Sections[0].State);
        var excluded = review.Rows.Select(row => row.Excluded).ToArray();
        System.Windows.Input.ApplicationCommands.Undo.Execute(null, undo);
        System.Windows.Input.ApplicationCommands.Redo.Execute(null, redo);
        TestAssert.True(review.Rows.Select(row => row.Excluded).SequenceEqual(excluded), "Toolbar undo/redo lost a bulk edit.");
        review.SetFlags(review.Rows.Take(200), excludedColumn: false, value: true);
        TestAssert.Equal(200, review.Rows.Count(row => row.Reviewed));
        var first = review.Rows[0];
        first.English = "Missing the placeholder";
        review.Refresh();
        TestAssert.Equal(SectionState.Invalid, review.Sections[0].State);
        first.Source.Revert();
        review.SetFlags(review.Rows.Take(200), excludedColumn: true, value: true);
        review.SaveAs(path);
        TestAssert.Equal(200, ReviewWorkspace.Open(path).Entries.Count(entry => entry.DoNotTranslate));
        review.SelectedFilter = "Excluded";
        TestAssert.Equal(200, review.Entries!.Cast<object>().Count());
        review.SelectedFilter = "All strings";
        var target = review.Rows[210];
        var originalEnglish = target.English;
        review.SelectLanguage("fr");
        TestAssert.True(!target.EnglishEditable && target.LocalisedEditable, "Language edit permissions are wrong.");
        target.English = "This must not edit the master";
        TestAssert.Equal(originalEnglish, target.English);
        target.Localised = "FR — " + originalEnglish;
        target.Notes = "French context";
        target.Reviewed = true;
        TestAssert.True(target.Reviewed, "Translation could not be marked reviewed.");
        review.SelectLanguage("ja");
        target.Localised = "JA — " + originalEnglish;
        review.SelectLanguage("fr");
        TestAssert.Equal("FR — " + originalEnglish, target.Localised);
        TestAssert.Equal("French context", target.Notes);
        TestAssert.True(target.Reviewed, "Switching languages lost a review.");
        review.SaveAs(Path.Combine(root, "Strings.fr.xaml"));
        review.SelectLanguage("en-GB");
        TestAssert.Equal("", target.Localised);
        TestAssert.True(target.EnglishEditable && !target.LocalisedEditable, "UK mode did not blank and disable Localised.");
        review.SelectLanguage("ja");
        TestAssert.Equal("JA — " + originalEnglish, target.Localised);
        review.SaveAs(Path.Combine(root, "Strings.ja.xaml"));
        review.SelectLanguage("en-GB");
        review.SelectedSection = review.Sections.Single(section => section.Name == target.Group);
        review.SetFlags(review.Rows.Where(row => row.Group == target.Group), excludedColumn: true, value: false);
        review.SetFlags(review.Rows.Where(row => row.Group == target.Group), excludedColumn: false, value: true);
        TestAssert.Equal(SectionState.Reviewed, review.SelectedSection.State);
        review.SaveAs(path);
        review.Open(FindMaster());
        review.Search = "loc_Settings_";
        Render(window, "review-window.png");
        review.SelectLanguage("fr");
        var title = review.Rows.Single(row => row.Key == "loc_Settings_Title");
        title.Localised = "Paramètres";
        review.Rows.Single(row => row.Key == "loc_Settings_Cancel").Localised = "Annuler";
        review.Rows.Single(row => row.Key == "loc_Settings_AccentColour").Localised = "Couleur d’accent";
        Render(window, "translation-window.png");
        // Discard synthetic screenshot drafts without writing the real source corpus.
        review.Open(FindMaster());
        window.Close();
    }));

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Visuals(child)) yield return descendant;
        }
    }
    private static void Layout(MorphFaceEditor.Localiser.MainWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1540, 840));
        content.Arrange(new Rect(0, 0, 1540, 840));
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
    private static void Render(MorphFaceEditor.Localiser.MainWindow window, string filename)
    {
        Layout(window);
        var bitmap = new RenderTargetBitmap(1540, 840, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((FrameworkElement)window.Content);
        var output = Path.Combine(FindRepository(), "artifacts", "localiser", filename);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = File.Create(output);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new Exception("Localiser window smoke test timed out.");
        if (error is not null) throw new Exception("Localiser window smoke test failed: " + error, error);
    }
    private static string CreateMaster(string root)
    {
        var path = Path.Combine(root, "Strings.en-GB.xaml");
        File.WriteAllText(path, Xml("\n<!-- First section -->\n<system:String x:Key=\"loc_one\" xml:space=\"preserve\">One {0}</system:String>\n<system:String x:Key=\"loc_two\">Two</system:String>\n<!-- Second section -->\n<system:String x:Key=\"loc_three\">Three</system:String>\n"));
        return path;
    }
    private static string Xml(string content) => "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:system=\"clr-namespace:System;assembly=mscorlib\">" + content + "</ResourceDictionary>";
    private static CatalogueData Read(string path) { using var stream = File.OpenRead(path); return StringCatalogue.Read(stream); }
    private static CatalogueData ReadText(string text) { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)); return StringCatalogue.Read(stream); }
    private static string FindRepository() => SourceReferenceIndex.FindRepository(AppContext.BaseDirectory) ?? throw new DirectoryNotFoundException("Source checkout not found.");
    private static string FindMaster() => Path.Combine(FindRepository(), "src", "MorphFaceEditor", "Localisation", "Strings.en-GB.xaml");
    private static void RejectFormat(Action action) { try { action(); } catch (FormatException) { return; } throw new Exception("Invalid catalogue edit was accepted."); }
    private static void RejectIo(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Unsafe catalogue save was accepted."); }
    private static void Scratch(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "MFE-Localiser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
