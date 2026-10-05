using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Controls;

public partial class SearchableTexturePicker : UserControl
{
    private Window? _ownerWindow;
    private MaterialTextureOption? _highlighted;
    private readonly SemaphoreSlim _thumbnailSlots = new(2, 2);
    private readonly Dictionary<PackageAssetListItem, CancellationTokenSource> _thumbnailLoads = [];
    private CancellationTokenSource? _thumbnailSession;

    public SearchableTexturePicker() => InitializeComponent();

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e) =>
        CandidateList.Height = Math.Clamp(CandidateList.Height - e.VerticalChange, 80, 900);

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown += OnOwnerPreviewMouseDown;
            _ownerWindow.Deactivated += OnOwnerDeactivated;
        }
        if (DataContext is MaterialTextureEditorViewModel editor)
        {
            editor.CancelTexturePreview();
            if (editor.SearchText.Length > 0)
            {
                editor.SearchText = string.Empty;
            }
            _highlighted = editor.SelectedTexture;
            CandidateList.SelectedItem = _highlighted;
            CandidateList.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() => CandidateList.ScrollIntoView(_highlighted)));
        }
        _thumbnailSession = new CancellationTokenSource();
        CandidateList.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(UpdateVisibleThumbnails));
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
        _thumbnailSession?.Cancel();
        _thumbnailSession?.Dispose();
        _thumbnailSession = null;
        foreach (var cancellation in _thumbnailLoads.Values)
        {
            cancellation.Cancel();
        }
        if (DataContext is MaterialTextureEditorViewModel editor)
        {
            editor.CancelTexturePreview();
            if (editor.SearchText.Length > 0)
            {
                editor.SearchText = string.Empty;
            }
            _highlighted = editor.SelectedTexture;
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

    private void OnOwnerDeactivated(object? sender, EventArgs e)
    {
        CancelPreviewAndRestore();
        PickerPopup.IsOpen = false;
    }

    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is CheckBox)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            CancelPreviewAndRestore();
            PickerPopup.IsOpen = false;
            PickerButton.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            CommitHighlighted();
            e.Handled = true;
            return;
        }
        var offset = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            _ => 0
        };
        if (offset != 0 && MoveHighlight(offset))
        {
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
        if (offset != 0 && MoveCommittedSelection(offset))
        {
            e.Handled = true;
        }
    }

    private void OnPickerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!PickerPopup.IsOpen && PickerButton.IsKeyboardFocusWithin &&
            MoveCommittedSelection(e.Delta > 0 ? -1 : 1))
        {
            e.Handled = true;
        }
    }

    private bool MoveHighlight(int offset)
    {
        if (DataContext is not MaterialTextureEditorViewModel editor || editor.Candidates.Count == 0)
        {
            return false;
        }
        var index = -1;
        if (_highlighted is not null)
        {
            for (var candidateIndex = 0; candidateIndex < editor.Candidates.Count; candidateIndex++)
            {
                if (ReferenceEquals(editor.Candidates[candidateIndex], _highlighted))
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
        SetHighlight(editor, editor.Candidates[next]);
        return true;
    }

    private bool MoveCommittedSelection(int offset)
    {
        if (DataContext is not MaterialTextureEditorViewModel editor || editor.Candidates.Count == 0)
        {
            return false;
        }
        var index = -1;
        for (var candidateIndex = 0; candidateIndex < editor.Candidates.Count; candidateIndex++)
        {
            if (ReferenceEquals(editor.Candidates[candidateIndex], editor.SelectedTexture))
            {
                index = candidateIndex;
                break;
            }
        }
        var next = Math.Clamp(index + offset, 0, editor.Candidates.Count - 1);
        if (next == index)
        {
            return false;
        }
        editor.CommitTexture(editor.Candidates[next]);
        return true;
    }

    private void SetHighlight(MaterialTextureEditorViewModel editor, MaterialTextureOption candidate)
    {
        if (ReferenceEquals(_highlighted, candidate))
        {
            return;
        }
        _highlighted = candidate;
        CandidateList.SelectedItem = candidate;
        CandidateList.ScrollIntoView(candidate);
        _ = editor.PreviewTextureAsync(candidate);
    }

    private void CommitHighlighted()
    {
        if (DataContext is not MaterialTextureEditorViewModel editor || _highlighted is null)
        {
            return;
        }
        editor.CommitTexture(_highlighted);
        PickerPopup.IsOpen = false;
        PickerButton.Focus();
    }

    private void CancelPreviewAndRestore()
    {
        if (DataContext is MaterialTextureEditorViewModel editor)
        {
            editor.CancelTexturePreview();
            _highlighted = editor.SelectedTexture;
            CandidateList.SelectedItem = _highlighted;
        }
    }

    private void OnCandidateMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaterialTextureOption item } &&
            DataContext is MaterialTextureEditorViewModel editor)
        {
            SetHighlight(editor, item);
        }
    }

    private void OnCandidateMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaterialTextureOption item })
        {
            _highlighted = item;
            CommitHighlighted();
            e.Handled = true;
        }
    }

    private void OnCandidateListMouseLeave(object sender, MouseEventArgs e) => CancelPreviewAndRestore();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        CancelPreviewAndRestore();
        CandidateList.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(UpdateVisibleThumbnails));
    }

    private void OnCandidateListScrollChanged(object sender, ScrollChangedEventArgs e) =>
        UpdateVisibleThumbnails();

    private void UpdateVisibleThumbnails()
    {
        if (_thumbnailSession is not { IsCancellationRequested: false } session ||
            !PickerPopup.IsOpen ||
            FindVisualChild<VirtualizingStackPanel>(CandidateList) is not { Children.Count: > 0 } panel)
        {
            return;
        }

        var firstIndex = CandidateList.ItemContainerGenerator.IndexFromContainer(panel.Children[0]);
        if (firstIndex < 0)
        {
            return;
        }

        var start = Math.Max(0, firstIndex - 2);
        var end = Math.Min(CandidateList.Items.Count - 1, firstIndex + panel.Children.Count + 1);
        var visibleAssets = new HashSet<PackageAssetListItem>();
        for (var index = start; index <= end; index++)
        {
            if (CandidateList.Items[index] is MaterialTextureOption { Asset: { CanLoadThumbnail: true } asset })
            {
                visibleAssets.Add(asset);
            }
        }

        foreach (var (asset, cancellation) in _thumbnailLoads.ToArray())
        {
            if (!visibleAssets.Contains(asset))
            {
                cancellation.Cancel();
            }
        }

        foreach (var asset in visibleAssets)
        {
            if (_thumbnailLoads.ContainsKey(asset))
            {
                continue;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            _thumbnailLoads.Add(asset, cancellation);
            _ = LoadThumbnailAsync(asset, cancellation);
        }
    }

    private async Task LoadThumbnailAsync(
        PackageAssetListItem asset,
        CancellationTokenSource cancellation)
    {
        var acquired = false;
        try
        {
            await _thumbnailSlots.WaitAsync(cancellation.Token);
            acquired = true;
            await asset.EnsureThumbnailAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (acquired)
            {
                _thumbnailSlots.Release();
            }
            if (_thumbnailLoads.TryGetValue(asset, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                _thumbnailLoads.Remove(asset);
            }
            cancellation.Dispose();
            if (PickerPopup.IsOpen)
            {
                _ = CandidateList.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(UpdateVisibleThumbnails));
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }
        return null;
    }
}
