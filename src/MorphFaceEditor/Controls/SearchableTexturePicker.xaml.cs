using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Controls;

public partial class SearchableTexturePicker : UserControl
{
    private bool _suppressPickerClick;

    public SearchableTexturePicker() => InitializeComponent();

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        _suppressPickerClick = PickerButton.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed;
        PickerButton.IsChecked = false;
        if (DataContext is MaterialTextureEditorViewModel { SearchText.Length: > 0 } editor)
        {
            editor.SearchText = string.Empty;
        }
    }

    private void OnPickerPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_suppressPickerClick || PickerPopup.IsOpen)
        {
            _suppressPickerClick = false;
            PickerPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnPickerPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PickerPopup.IsOpen)
        {
            return;
        }
        var offset = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            _ => 0
        };
        if (offset != 0 && MoveSelection(offset))
        {
            e.Handled = true;
        }
    }

    private void OnPickerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!PickerPopup.IsOpen && MoveSelection(e.Delta > 0 ? -1 : 1))
        {
            PickerButton.Focus();
            e.Handled = true;
        }
    }

    private bool MoveSelection(int offset)
    {
        if (DataContext is not MaterialTextureEditorViewModel editor || editor.Candidates.Count == 0)
        {
            return false;
        }
        var index = -1;
        if (editor.SelectedTexture is not null)
        {
            for (var candidateIndex = 0; candidateIndex < editor.Candidates.Count; candidateIndex++)
            {
                if (ReferenceEquals(editor.Candidates[candidateIndex], editor.SelectedTexture))
                {
                    index = candidateIndex;
                    break;
                }
            }
        }
        var next = Math.Clamp(index + offset, 0, editor.Candidates.Count - 1);
        if (next == index)
        {
            return false;
        }
        editor.SelectedTexture = editor.Candidates[next];
        return true;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PickerPopup.IsOpen && e.AddedItems.Count > 0)
        {
            PickerPopup.IsOpen = false;
        }
    }

    private void OnCandidateMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaterialTextureOption item })
        {
            _ = item.EnsureThumbnailAsync();
        }
    }
}
