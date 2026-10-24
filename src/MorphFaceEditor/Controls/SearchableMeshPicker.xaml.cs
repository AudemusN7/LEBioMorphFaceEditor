using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Controls;

public partial class SearchableMeshPicker : UserControl
{
    private readonly PickerInteraction<HairMeshEditorViewModel, HairMeshOption> _interaction;

    public SearchableMeshPicker()
    {
        InitializeComponent();
        _interaction = new(this, PickerButton, PickerPopup, SearchBox, CandidateList,
            editor => editor.Candidates,
            editor => editor.Selected,
            (editor, option) => editor.Preview(option),
            editor => editor.CancelPreview(),
            (editor, option) => editor.Commit(option),
            editor => { if (editor.SearchText.Length > 0) editor.SearchText = string.Empty; });
    }

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e) =>
        CandidateList.Height = Math.Clamp(CandidateList.Height + e.VerticalChange, 80, 900);

    private void OnPopupOpened(object? sender, EventArgs e) => _interaction.Open();
    private void OnPopupClosed(object? sender, EventArgs e) => _interaction.Close();
    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e) => _interaction.PopupKeyDown(e);
    private void OnPickerPreviewKeyDown(object sender, KeyEventArgs e) => _interaction.PickerKeyDown(e);
    private void OnPickerPreviewMouseWheel(object sender, MouseWheelEventArgs e) => _interaction.PickerMouseWheel(e);
    private void OnCandidateMouseEnter(object sender, MouseEventArgs e) => _interaction.CandidateMouseEnter(sender);
    private void OnCandidateMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _interaction.CandidateMouseLeftButtonUp(sender, e);
    private void OnCandidateListMouseLeave(object sender, MouseEventArgs e) => _interaction.CancelPreviewAndRestore();
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => _interaction.CancelPreviewAndRestore();
}
