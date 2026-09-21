using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Controls;

public partial class SearchableMeshPicker : UserControl
{
    private Window? _ownerWindow;

    public SearchableMeshPicker() => InitializeComponent();

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown += OnOwnerPreviewMouseDown;
            _ownerWindow.Deactivated += OnOwnerDeactivated;
        }
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown -= OnOwnerPreviewMouseDown;
            _ownerWindow.Deactivated -= OnOwnerDeactivated;
            _ownerWindow = null;
        }
        PickerButton.IsChecked = false;
        if (DataContext is HairMeshEditorViewModel editor && editor.SearchText.Length > 0)
        {
            editor.SearchText = string.Empty;
        }
    }

    private void OnOwnerPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!PickerButton.IsMouseOver &&
            PickerPopup.Child is not UIElement { IsMouseOver: true })
        {
            PickerPopup.IsOpen = false;
        }
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e) => PickerPopup.IsOpen = false;

    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PickerPopup.IsOpen = false;
            PickerButton.Focus();
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
        // Keep the closed picker consistent with focused sliders: merely hovering it
        // must not consume the wheel or change the selected attachment. Once the
        // picker has explicit keyboard focus, wheel selection is useful and safe.
        if (!PickerPopup.IsOpen && PickerButton.IsKeyboardFocusWithin &&
            MoveSelection(e.Delta > 0 ? -1 : 1))
        {
            e.Handled = true;
        }
    }

    private bool MoveSelection(int offset)
    {
        if (DataContext is not HairMeshEditorViewModel editor || editor.Candidates.Count == 0)
        {
            return false;
        }
        var index = -1;
        if (editor.Selected is not null)
        {
            for (var candidateIndex = 0; candidateIndex < editor.Candidates.Count; candidateIndex++)
            {
                if (ReferenceEquals(editor.Candidates[candidateIndex], editor.Selected))
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
        editor.Selected = editor.Candidates[next];
        return true;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PickerPopup.IsOpen && e.AddedItems.Count > 0)
        {
            PickerPopup.IsOpen = false;
        }
    }
}
