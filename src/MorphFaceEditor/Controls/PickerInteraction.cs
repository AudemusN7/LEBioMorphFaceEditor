using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace MorphFaceEditor.Controls;

/// <summary>Owns a picker's temporary highlight and popup input; the editor owns authored selection.</summary>
internal sealed class PickerInteraction<TEditor, TOption>(
    FrameworkElement control,
    ToggleButton button,
    Popup popup,
    TextBox search,
    ListBox list,
    Func<TEditor, IReadOnlyList<TOption>> candidates,
    Func<TEditor, TOption?> selected,
    Action<TEditor, TOption> preview,
    Action<TEditor> cancelPreview,
    Action<TEditor, TOption> commit,
    Action<TEditor> clearSearch,
    bool ignoreRepeatedHighlight = false)
    where TEditor : class
    where TOption : class
{
    private Window? _ownerWindow;
    private TOption? _highlighted;
    private bool _synchronisingHighlight;

    public void Open()
    {
        _ownerWindow = Window.GetWindow(control);
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown += OnOwnerPreviewMouseDown;
            _ownerWindow.Deactivated += OnOwnerDeactivated;
        }
        if (control.DataContext is TEditor editor)
        {
            cancelPreview(editor);
            clearSearch(editor);
            RestoreHighlight(editor);
            list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => list.ScrollIntoView(_highlighted)));
        }
        search.Focus();
        search.SelectAll();
    }

    public void Close()
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown -= OnOwnerPreviewMouseDown;
            _ownerWindow.Deactivated -= OnOwnerDeactivated;
            _ownerWindow = null;
        }
        button.IsChecked = false;
        if (control.DataContext is TEditor editor)
        {
            cancelPreview(editor);
            clearSearch(editor);
            _highlighted = selected(editor);
        }
    }

    public void PopupKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelPreviewAndRestore();
            popup.IsOpen = false;
            button.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitHighlighted();
            e.Handled = true;
        }
        else if (KeyOffset(e.Key) is var offset && offset != 0 && MoveSelection(offset, previewOnly: true))
        {
            e.Handled = true;
        }
    }

    public void PickerKeyDown(KeyEventArgs e)
    {
        if (!popup.IsOpen && KeyOffset(e.Key) is var offset && offset != 0 &&
            MoveSelection(offset, previewOnly: false))
            e.Handled = true;
    }

    public void PickerMouseWheel(MouseWheelEventArgs e)
    {
        if (!popup.IsOpen && button.IsKeyboardFocusWithin &&
            MoveSelection(e.Delta > 0 ? -1 : 1, previewOnly: false))
            e.Handled = true;
    }

    public void CandidateMouseEnter(object sender)
    {
        if (!_synchronisingHighlight && sender is FrameworkElement { DataContext: TOption option } &&
            control.DataContext is TEditor editor)
            SetHighlight(editor, option);
    }

    public void CandidateMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TOption option })
        {
            _highlighted = option;
            CommitHighlighted();
            e.Handled = true;
        }
    }

    public void CancelPreviewAndRestore()
    {
        if (control.DataContext is TEditor editor)
        {
            cancelPreview(editor);
            RestoreHighlight(editor);
        }
    }

    private bool MoveSelection(int offset, bool previewOnly)
    {
        if (control.DataContext is not TEditor editor || candidates(editor) is not { Count: > 0 } options)
            return false;
        var current = previewOnly ? _highlighted : selected(editor);
        var index = -1;
        for (var candidateIndex = 0; candidateIndex < options.Count; candidateIndex++)
        {
            if (ReferenceEquals(options[candidateIndex], current))
            {
                index = candidateIndex;
                break;
            }
        }
        var next = Math.Clamp(index + offset, 0, options.Count - 1);
        if (next == index) return false;
        if (previewOnly) SetHighlight(editor, options[next]);
        else commit(editor, options[next]);
        return true;
    }

    private void SetHighlight(TEditor editor, TOption option)
    {
        if (ignoreRepeatedHighlight && ReferenceEquals(_highlighted, option)) return;
        _highlighted = option;
        SetListSelection();
        list.ScrollIntoView(option);
        preview(editor, option);
    }

    private void RestoreHighlight(TEditor editor)
    {
        _highlighted = selected(editor);
        SetListSelection();
    }

    private void SetListSelection()
    {
        _synchronisingHighlight = true;
        try { list.SelectedItem = _highlighted; }
        finally { _synchronisingHighlight = false; }
    }

    private void CommitHighlighted()
    {
        if (control.DataContext is not TEditor editor || _highlighted is null) return;
        commit(editor, _highlighted);
        popup.IsOpen = false;
        button.Focus();
    }

    private void OnOwnerPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!button.IsMouseOver && popup.Child is not UIElement { IsMouseOver: true })
            popup.IsOpen = false;
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e)
    {
        CancelPreviewAndRestore();
        popup.IsOpen = false;
    }

    private static int KeyOffset(Key key) => key switch { Key.Up => -1, Key.Down => 1, _ => 0 };
}
