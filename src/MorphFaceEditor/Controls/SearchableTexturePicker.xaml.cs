using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Controls;

public partial class SearchableTexturePicker : UserControl
{
    public SearchableTexturePicker() => InitializeComponent();

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        PickerButton.IsChecked = false;
        if (DataContext is MaterialTextureEditorViewModel { SearchText.Length: > 0 } editor)
        {
            editor.SearchText = string.Empty;
        }
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
