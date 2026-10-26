using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MorphFaceEditor.Localisation;
using MorphFaceEditor.Localiser;

namespace MorphFaceEditor.Tests;

internal static class LocaliserTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("localiser: a wording edit preserves master and translation file layout", WordingPreservesLayout),
        new("localiser: notes and protection survive XML-sensitive characters", AnnotationRoundTrip),
        new("localiser: excluded strings cannot be translated and are omitted from saved languages", ProtectedStrings),
        new("localiser: explicitly retired translations load and are pruned on save", RetiredTranslations),
        new("localiser: cached wording validation and review hashes invalidate on edits", CachedValidationAndReviewHashes),
        new("localiser: excluded tab column and rows are confined to the UK master", MasterOnlyExclusions),
        new("localiser: invalid wording, concurrent edits and locked files preserve the master and draft", SafeSave),
        new("localiser: language drafts persist Unicode notes review stamps and missing strings", TranslationRoundTrip),
        new("localiser: combined language and master save rolls back if either file fails", TranslationSafeSave),
        new("localiser: undo groups typing restores reviews and survives language changes and saving", HistoryLifecycle),
        new("localiser: undo reverses a 200-row bulk toggle in one step", BulkHistory),
        new("localiser: inline typing retains editors caret and selection until filtered edits finish", InlineTyping)
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

    private static void RetiredTranslations() => Scratch(root =>
    {
        var masterPath = Path.Combine(root, "Strings.en-GB.xaml");
        File.WriteAllText(masterPath, Xml("\n<!--localiser-retired: [\"loc_retired\"]-->\n" +
            "<system:String x:Key=\"loc_active\">Active</system:String>\n"));
        var master = ReviewWorkspace.Open(masterPath);
        TestAssert.True(master.RetiredKeys.SetEquals(["loc_retired"]), "The explicit retirement list was not parsed.");

        var languagePath = Path.Combine(root, "Strings.fr.xaml");
        File.WriteAllText(languagePath, Xml("\n<!-- Keep this language section -->\n" +
            "<!-- localiser: {\"Key\":\"loc_active\",\"Note\":\"active context\"} -->\n" +
            "<system:String x:Key=\"loc_active\">Actif</system:String>\n" +
            "<!-- localiser: {\"Key\":\"loc_retired\",\"Note\":\"retired context\"} -->\n" +
            "<system:String x:Key=\"loc_retired\">Ancienne chaîne</system:String>\n"));
        var locale = Read(languagePath);
        StringCatalogue.ValidateOverlay(master.Entries.ToDictionary(entry => entry.Key, entry => entry.Text), locale.Strings,
            locale.Annotations, master.RetiredKeys);
        RejectFormat(() => StringCatalogue.ValidateOverlay(master.Entries.ToDictionary(entry => entry.Key, entry => entry.Text),
            new Dictionary<string, string> { ["loc_unknown"] = "Unknown" }, retiredKeys: master.RetiredKeys));

        var language = TranslationWorkspace.Open(master, languagePath);
        TestAssert.Equal(1, language.Entries.Count);
        TestAssert.True(!language.Entries.ContainsKey("loc_retired"), "A retired key appeared as a translation row.");
        TestAssert.True(language.IsDirty, "Pending retired-string cleanup did not mark the language dirty.");
        language.SaveAs(languagePath, master);
        TestAssert.True(!language.IsDirty, "Saving retired-string cleanup left the language dirty.");
        var saved = Read(languagePath);
        TestAssert.Equal("Actif", saved.Strings["loc_active"]);
        TestAssert.True(!saved.Strings.ContainsKey("loc_retired") && !saved.Annotations.ContainsKey("loc_retired"),
            "Saving did not prune retired strings and their annotations.");
        TestAssert.Equal("active context", saved.Annotations["loc_active"].Note);
        TestAssert.True(File.ReadAllText(languagePath).Contains("Keep this language section", StringComparison.Ordinal),
            "Saving removed the existing section heading.");

        var unknownPath = Path.Combine(root, "Strings.unknown.xaml");
        File.WriteAllText(unknownPath, Xml("<system:String x:Key=\"loc_unknown\">Unknown</system:String>"));
        RejectFormat(() => TranslationWorkspace.Open(master, unknownPath));
        RejectFormat(() => ReadText(Xml("<!--localiser-retired: [\"loc_active\"]-->" +
            "<system:String x:Key=\"loc_active\">Active</system:String>")));
        RejectFormat(() => ReadText(Xml("<!--localiser-retired: [\"loc_retired\",\"loc_retired\"]-->" +
            "<system:String x:Key=\"loc_active\">Active</system:String>")));
    });

    private static void CachedValidationAndReviewHashes() => Scratch(root =>
    {
        var master = ReviewWorkspace.Open(CreateMaster(root));
        var source = master.Entries.Single(entry => entry.Key == "loc_one");
        TestAssert.Equal("", source.Problem);
        source.Text = "Missing placeholder";
        TestAssert.True(source.Problem.Length > 0, "A source wording edit did not refresh cached validation.");
        source.Text = "Changed {0}";
        TestAssert.Equal("", source.Problem);
        source.Reviewed = true;
        TestAssert.True(source.Reviewed, "A valid source wording could not be reviewed after a cached problem.");
        source.Text = "Changed again {0}";
        TestAssert.True(!source.Reviewed, "A source text hash cache retained a stale review.");

        var translation = TranslationWorkspace.Empty(master).Entries["loc_one"];
        translation.Text = "Un {0}";
        TestAssert.Equal("", translation.Problem);
        translation.Reviewed = true;
        TestAssert.True(translation.Reviewed, "A valid translation could not be reviewed.");
        translation.Text = "Un";
        TestAssert.True(translation.Problem.Length > 0 && !translation.Reviewed,
            "A translation wording edit did not invalidate its cached validation and review hash.");
        translation.Text = "Une {0}";
        TestAssert.Equal("", translation.Problem);
        translation.UpdateEnglish("Changed {1}");
        TestAssert.True(translation.Problem.Length > 0 && !translation.Reviewed,
            "An English source update did not invalidate the cached translation validation and review hash.");
        translation.Text = "Une {1}";
        TestAssert.Equal("", translation.Problem);
        translation.Reviewed = true;
        TestAssert.True(translation.Reviewed, "A repaired translation did not accept a fresh review stamp.");
    });

    private static void MasterOnlyExclusions() => Scratch(root => WpfTestDispatcher.Invoke(() =>
    {
        var path = CreateMaster(root);
        var window = new MorphFaceEditor.Localiser.MainWindow();
        var review = window.Review;
        review.Open(path);
        var tabs = (TabControl)window.FindName("FilterTabs");
        var grid = (DataGrid)window.FindName("StringList");
        var excludedColumn = grid.Columns.Single(column => Equals(column.Header, "Excluded"));
        TabItem ExcludedTab()
        {
            Layout(window);
            return (TabItem)tabs.ItemContainerGenerator.ContainerFromItem("Excluded");
        }
        var row = review.Rows.Single(item => item.Key == "loc_one");
        row.Notes = "Master-only wording";
        review.SetFlags([row], excludedColumn: true, value: true);
        review.SelectedFilter = "Needs review";
        TestAssert.True(review.Entries!.Cast<LocaliserRow>().Contains(row),
            "English master review hid an unreviewed excluded row.");
        TestAssert.Equal(Visibility.Visible, ExcludedTab().Visibility);
        TestAssert.Equal(Visibility.Visible, excludedColumn.Visibility);

        foreach (var code in new[] { "en-US", "fr" })
        {
            review.SelectedFilter = "Excluded";
            review.SelectLanguage(code);
            TestAssert.Equal("All strings", review.SelectedFilter);
            TestAssert.Equal(Visibility.Collapsed, ExcludedTab().Visibility);
            TestAssert.Equal(Visibility.Collapsed, excludedColumn.Visibility);
            TestAssert.Equal("All strings", (string)tabs.SelectedItem);
            TestAssert.True(grid.Items.Cast<LocaliserRow>().Any(item => item.Key == "loc_two"),
                "Switching from Excluded left the translation table empty.");
            foreach (var filter in review.Filters.Where(filter => filter != "Excluded"))
            {
                review.SelectedFilter = filter;
                Layout(window);
                TestAssert.True(!grid.Items.Cast<LocaliserRow>().Contains(row),
                    $"{code} showed an excluded row under {filter}.");
            }
            review.SelectedFilter = "All strings";
            review.Search = row.Key;
            Layout(window);
            TestAssert.Equal(0, grid.Items.Count);
            review.Search = "";
            review.SelectLanguage("en-GB");
            TestAssert.Equal(Visibility.Visible, ExcludedTab().Visibility);
            TestAssert.Equal(Visibility.Visible, excludedColumn.Visibility);
            review.SelectedFilter = "Excluded";
            Layout(window);
            TestAssert.True(grid.Items.Cast<LocaliserRow>().Contains(row),
                "Returning to the master lost the excluded row.");
        }
        review.Workspace!.Save();
        window.Close();
    }));

    private static void SafeSave() => Scratch(root =>
    {
        var path = CreateMaster(root);
        var workspace = ReviewWorkspace.Open(path);
        var original = File.ReadAllBytes(path);
        foreach (var invalid in new[] { "Missing placeholder", "Malformed {0" })
        {
            workspace.Entries[0].Text = invalid;
            RejectFormat(workspace.Save);
            TestAssert.True(File.ReadAllBytes(path).SequenceEqual(original) && workspace.IsDirty,
                "Invalid master wording overwrote the source or discarded the draft.");
        }

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

    private static void HistoryLifecycle() => Scratch(root => WpfTestDispatcher.Invoke(() =>
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

    private static void BulkHistory() => Scratch(root => WpfTestDispatcher.Invoke(() =>
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

    private static void InlineTyping() => Scratch(root => WpfTestDispatcher.Invoke(() =>
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
