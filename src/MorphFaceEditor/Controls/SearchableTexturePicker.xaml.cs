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
    private readonly PickerInteraction<MaterialTextureEditorViewModel, MaterialTextureOption> _interaction;
    private readonly SemaphoreSlim _thumbnailSlots = new(2, 2);
    private readonly Dictionary<PackageAssetListItem, CancellationTokenSource> _thumbnailLoads = [];
    private CancellationTokenSource? _thumbnailSession;

    public SearchableTexturePicker()
    {
        InitializeComponent();
        _interaction = new(this, PickerButton, PickerPopup, SearchBox, CandidateList,
            editor => editor.Candidates,
            editor => editor.SelectedTexture,
            (editor, option) => _ = editor.PreviewTextureAsync(option),
            editor => editor.CancelTexturePreview(),
            (editor, option) => editor.CommitTexture(option),
            editor => { if (editor.SearchText.Length > 0) editor.SearchText = string.Empty; },
            ignoreRepeatedHighlight: true);
    }

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e) =>
        CandidateList.Height = Math.Clamp(CandidateList.Height - e.VerticalChange, 80, 900);

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        _interaction.Open();
        _thumbnailSession = new CancellationTokenSource();
        CandidateList.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(UpdateVisibleThumbnails));
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        _thumbnailSession?.Cancel();
        _thumbnailSession?.Dispose();
        _thumbnailSession = null;
        foreach (var cancellation in _thumbnailLoads.Values)
        {
            cancellation.Cancel();
        }
        _interaction.Close();
    }

    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is CheckBox) return;
        _interaction.PopupKeyDown(e);
    }

    private void OnPickerPreviewKeyDown(object sender, KeyEventArgs e) => _interaction.PickerKeyDown(e);
    private void OnPickerPreviewMouseWheel(object sender, MouseWheelEventArgs e) => _interaction.PickerMouseWheel(e);
    private void OnCandidateMouseEnter(object sender, MouseEventArgs e) => _interaction.CandidateMouseEnter(sender);
    private void OnCandidateMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _interaction.CandidateMouseLeftButtonUp(sender, e);
    private void OnCandidateListMouseLeave(object sender, MouseEventArgs e) => _interaction.CancelPreviewAndRestore();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _interaction.CancelPreviewAndRestore();
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
