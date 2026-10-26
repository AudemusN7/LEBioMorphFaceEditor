using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using MorphFaceEditor.Localisation;

namespace MorphFaceEditor.Localiser;

public partial class MainWindow : Window
{
    internal ReviewViewModel Review { get; } = new();
    public MainWindow()
    {
        InitializeComponent(); DataContext = Review;
        BindingOperations.SetBinding(ExcludedColumn, DataGridColumn.VisibilityProperty,
            new Binding(nameof(ReviewViewModel.IsEnglish)) { Source = Review, Converter = new BooleanToVisibilityConverter() });
        ApplyDarkCaption(this);
    }

    internal static void ApplyDarkCaption(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var dark = 1; _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            SetCaptionColour(handle, 35, ((SolidColorBrush)window.FindResource("PanelBackground")).Color);
            SetCaptionColour(handle, 36, ((SolidColorBrush)window.FindResource("PrimaryText")).Color);
            SetCaptionColour(handle, 34, ((SolidColorBrush)window.FindResource("BorderBrush")).Color);
        };
    }

    internal void OpenInitial(string? path)
    {
        var repository = SourceReferenceIndex.FindRepository(AppContext.BaseDirectory);
        if (path is null && repository is not null)
            path = Path.Combine(repository, "src", "MorphFaceEditor", "Localisation", "Strings.en-GB.xaml");
        if (path is not null && File.Exists(path)) TryAction("Could not load localisation", () => Review.Open(path));
    }

    private void OnLoadCommand(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Load localisation", Filter = "XAML localisation (*.xaml)|*.xaml", CheckFileExists = true,
            FileName = $"Strings.{Review.LanguageCode}.xaml" };
        if (dialog.ShowDialog(this) != true || !ConfirmLoss(Review.IsEnglish)) return;
        TryAction("Could not load localisation", () => Review.LoadLocalisation(dialog.FileName));
    }
    private void OnSaveCommand(object sender, ExecutedRoutedEventArgs e) => SaveAs();
    private void CanSave(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Review.HasWorkspace;
    private void CanUndo(object sender, CanExecuteRoutedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, SearchBox)) return; e.CanExecute = Review.CanUndo; e.Handled = true; }
    private void CanRedo(object sender, CanExecuteRoutedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, SearchBox)) return; e.CanExecute = Review.CanRedo; e.Handled = true; }
    private void OnUndoCommand(object sender, ExecutedRoutedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, SearchBox)) return; Review.Undo(); e.Handled = true; }
    private void OnRedoCommand(object sender, ExecutedRoutedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, SearchBox)) return; Review.Redo(); e.Handled = true; }
    private void OnHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SearchBox)) return;
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Z && modifiers == ModifierKeys.Control) { Review.Undo(); e.Handled = true; }
        else if ((e.Key == Key.Y && modifiers == ModifierKeys.Control) ||
                 (e.Key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        { Review.Redo(); e.Handled = true; }
    }
    private bool SaveAs()
    {
        var path = Review.CurrentPath;
        var dialog = new SaveFileDialog { Title = "Save localisation as", Filter = "XAML localisation (*.xaml)|*.xaml", DefaultExt = ".xaml", AddExtension = true,
            FileName = path.Length > 0 ? Path.GetFileName(path) : $"Strings.{Review.LanguageCode}.xaml", OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(path.Length > 0 ? path : Review.Workspace!.Path) };
        return dialog.ShowDialog(this) == true && TryAction("Could not save localisation", () => Review.SaveAs(dialog.FileName));
    }

    private void OnLanguageChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string code } || Review.LanguageCode == code) return;
        if (!TryAction("Could not load language", () => Review.SelectLanguage(code)))
            foreach (var language in Review.Languages) language.Refresh();
    }
    private void OnFlagClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkbox) return;
        ApplyCheckbox(checkbox); e.Handled = true;
    }
    private void OnFlagKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || sender is not CheckBox checkbox) return;
        ApplyCheckbox(checkbox); e.Handled = true;
    }
    private void ApplyCheckbox(CheckBox checkbox)
    {
        if (checkbox.DataContext is not LocaliserRow row) return;
        var selected = StringList.SelectedItems.Cast<LocaliserRow>().ToArray();
        var rows = selected.Contains(row) ? selected : [row];
        Review.SetFlags(rows, Equals(checkbox.Tag, "Excluded"), checkbox.IsChecked != true);
    }
    private void OnTableKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not TextBox)
        { StringList.SelectAll(); e.Handled = true; }
    }
    private void OnCellContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement target ||
            target.DataContext is not LocaliserRow row)
            return;

        var field = target.Tag as string;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = item.Tag switch
            {
                "CopyCell" or "CopyKey" => true,
                "PasteCell" => CanPasteCell(row, field) && ClipboardContainsUnicodeText(),
                _ => item.Command == ApplicationCommands.Copy
                    ? target is TextBox
                    : item.Command == ApplicationCommands.Paste && target is TextBox &&
                      CanPasteCell(row, field) && ClipboardContainsUnicodeText()
            };
        }
    }

    private void OnCellContextMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu ||
            menu.PlacementTarget is not FrameworkElement target || target.DataContext is not LocaliserRow row)
            return;

        var field = target.Tag as string;
        switch (item.Tag as string)
        {
            case "CopyCell":
                if (ReadCell(row, field) is { } value) TrySetClipboardText(value);
                break;
            case "CopyKey":
                TrySetClipboardText(row.Key);
                break;
            case "PasteCell" when CanPasteCell(row, field) && TryGetClipboardText(out var text):
                Review.CommitPendingCellEdit();
                WriteCell(row, field!, text);
                Review.CommitPendingCellEdit();
                break;
        }
    }

    private static bool ClipboardContainsUnicodeText()
    {
        try { return Clipboard.ContainsText(TextDataFormat.UnicodeText); }
        catch (ExternalException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void TrySetClipboardText(string value)
    {
        try { Clipboard.SetText(value, TextDataFormat.UnicodeText); }
        catch (ExternalException) { }
        catch (InvalidOperationException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryGetClipboardText(out string value)
    {
        try
        {
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                value = Clipboard.GetText(TextDataFormat.UnicodeText);
                return true;
            }
        }
        catch (ExternalException) { }
        catch (InvalidOperationException) { }
        catch (UnauthorizedAccessException) { }
        value = "";
        return false;
    }

    private static string? ReadCell(LocaliserRow row, string? field) => field switch
    {
        "Key" => row.Key,
        "English" => row.English,
        "Localised" => row.Localised,
        "Notes" => row.Notes,
        _ => null
    };

    private static bool CanPasteCell(LocaliserRow row, string? field) => field switch
    {
        "English" => row.EnglishEditable,
        "Localised" => row.LocalisedEditable,
        "Notes" => true,
        _ => false
    };

    private static void WriteCell(LocaliserRow row, string field, string value)
    {
        switch (field)
        {
            case "English": row.English = value; break;
            case "Localised": row.Localised = value; break;
            case "Notes": row.Notes = value; break;
        }
    }

    private void OnCellEditStarted(object sender, KeyboardFocusChangedEventArgs e)
    {
        // TextBox has its own class-level Undo/Redo commands. Override those on each
        // inline editor so keyboard shortcuts and context menus share the table history.
        if (sender is TextBox editor && editor.CommandBindings.Count == 0)
        {
            editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, OnUndoCommand, CanUndo));
            editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, OnRedoCommand, CanRedo));
        }
        Review.SetCellEditing(true);
    }
    private void OnCellEditEnded(object sender, KeyboardFocusChangedEventArgs e) => Review.SetCellEditing(false);

    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = !ConfirmLoss(allLanguages: true);
    private bool ConfirmLoss(bool allLanguages)
    {
        if (allLanguages ? !Review.IsDirty : !Review.CurrentTranslationDirty) return true;
        var answer = LocaliserDialog.Show(this, "Unsaved localisation", "Save edited localisations before continuing?", confirmation: true);
        if (answer == MessageBoxResult.No) return true;
        if (answer != MessageBoxResult.Yes) return false;
        var original = Review.LanguageCode;
        try
        {
            foreach (var code in allLanguages ? Review.DirtyLanguageCodes.ToArray() : new[] { original })
            { Review.SelectLanguage(code); if (!SaveAs()) return false; }
            return true;
        }
        finally { Review.SelectLanguage(original); }
    }
    private bool TryAction(string title, Action action)
    {
        try { action(); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException or InvalidOperationException)
        { LocaliserDialog.Show(this, title, exception.Message); return false; }
    }
    private static void SetCaptionColour(IntPtr handle, int attribute, Color colour)
    { var value = colour.R | colour.G << 8 | colour.B << 16; _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)); }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}
