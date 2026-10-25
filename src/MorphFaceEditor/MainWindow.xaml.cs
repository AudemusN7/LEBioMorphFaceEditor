using System.Windows;
using MorphFaceEditor.Presentation;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Models;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using MorphFaceEditor.Views;

namespace MorphFaceEditor;

/// <summary>Thin WPF event bridge between bindings, orbit input and the preview host.</summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly HeadPreviewHostController _previewHost;
    private readonly OrbitInputController _orbitInput;
    private readonly ContinuousEditGesture _numericEdit = new();
    private bool _closeConfirmed;
    private GridLength _paneDragStartWidth;
    private readonly ApplicationPreferencesStore? _preferencesStore;

    public MainWindow(MainWindowViewModel viewModel) : this(viewModel, null) { }

    internal MainWindow(MainWindowViewModel viewModel, ApplicationPreferencesStore? preferencesStore)
    {
        InitializeComponent();
        _preferencesStore = preferencesStore;
        if (preferencesStore?.Current.SidePaneWidths is { } widths)
        {
            RestorePaneWidth(FaceBrowserColumn, widths.FaceBrowser);
            RestorePaneWidth(FaceEditorColumn, widths.FaceEditor);
        }
        DarkTitleBar.Apply(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        FaceList.InputBindings.Add(new KeyBinding(
            _viewModel.LoadSelectedFaceCommand,
            new KeyGesture(Key.Enter)));
        _previewHost = new HeadPreviewHostController(this, PreviewHost, PreviewImage);
        _orbitInput = new OrbitInputController(PreviewHost, _previewHost.Camera, () => _previewHost.RequestRender());
        _viewModel.PreviewSceneReady += OnPreviewSceneReady;
        _viewModel.PreviewCleared += OnPreviewCleared;
        _viewModel.PreviewDeformationReady += OnPreviewDeformationReady;
        _viewModel.PreviewMaterialReady += OnPreviewMaterialReady;
        _viewModel.PreviewOptionsChanged += OnPreviewOptionsChanged;
        _viewModel.ExitRequested += OnExitRequested;
        _previewHost.FramePresented += _viewModel.SetFrameStatus;
        _previewHost.PreviewFailed += OnPreviewFailed;
        Closing += OnClosing;
        Closed += OnClosed;
        LayoutUpdated += OnPaneLayoutUpdated;
    }

    private void OnPaneLayoutUpdated(object? sender, EventArgs e)
    {
        if (WorkspaceLayout.ActualWidth <= 0) return;

        // Auto-sized columns include the current language's labels/check boxes.
        // Reserve useful slider space as well as space for the editor heading.
        var controlsWidth = RandomisationControls.ColumnDefinitions
            .Where(column => column.Width.IsAuto).Sum(column => column.ActualWidth);
        var editorMinimum = Math.Max(600, Math.Max(
            controlsWidth + 42 + 80 + 26, EditorActions.DesiredSize.Width + 100 + 26));
        if (Math.Abs(FaceEditorColumn.MinWidth - editorMinimum) > 0.5)
            FaceEditorColumn.MinWidth = editorMinimum;

        var chromeWidth = Math.Max(0, ActualWidth - WorkspaceLayout.ActualWidth);
        var panesMinimum = FaceBrowserColumn.MinWidth + FaceEditorColumn.MinWidth + 300 + 12;
        // The package caption can yield space; the toolbar's controls cannot.
        var toolbarMinimum = FileToolbar.DesiredSize.Width + PreviewToolbar.DesiredSize.Width + 64;
        var minimum = Math.Max(panesMinimum, toolbarMinimum) + chromeWidth;
        if (Math.Abs(MinWidth - minimum) > 0.5) MinWidth = minimum;
        FitSidePanes();
    }

    private static void RestorePaneWidth(ColumnDefinition pane, double? width)
    {
        if (width is > 0 && double.IsFinite(width.Value))
            pane.Width = new GridLength(Math.Max(pane.MinWidth, width.Value));
    }

    private void SavePaneWidths()
    {
        if (_preferencesStore is null) return;
        var widths = new SidePaneWidthsPreference(FaceBrowserColumn.Width.Value, FaceEditorColumn.Width.Value);
        try { _preferencesStore.Update(preferences => preferences with { SidePaneWidths = widths }); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"PREFERENCES:PANES-SAVE-FAILED REASON:{exception.Message}");
        }
    }

    private ColumnDefinition GetSidePane(object sender) =>
        ReferenceEquals(sender, FaceBrowserSplitter) ? FaceBrowserColumn : FaceEditorColumn;

    private void OnPaneDragStarted(object sender, DragStartedEventArgs e) =>
        _paneDragStartWidth = GetSidePane(sender).Width;

    private void OnPaneDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeSidePane(sender, e.HorizontalChange);
        e.Handled = true;
    }

    private void OnPaneDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.Canceled) GetSidePane(sender).Width = _paneDragStartWidth;
        FitSidePanes();
        if (!e.Canceled) SavePaneWidths();
    }

    private void OnPaneResizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is Thumb { IsDragging: true } thumb)
        {
            thumb.CancelDrag();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            ResizeSidePane(sender, e.Key == Key.Left ? -10 : 10);
            SavePaneWidths();
            e.Handled = true;
        }
    }

    private void ResizeSidePane(object sender, double horizontalChange)
    {
        var pane = GetSidePane(sender);
        var other = ReferenceEquals(pane, FaceBrowserColumn) ? FaceEditorColumn : FaceBrowserColumn;
        // The outside edge stays at the window edge. Only consume space that the
        // viewport can actually give up; never let a drag grow the whole grid.
        var maximum = WorkspaceLayout.ActualWidth - other.ActualWidth - 300 - 12;
        var change = ReferenceEquals(pane, FaceBrowserColumn) ? horizontalChange : -horizontalChange;
        pane.Width = new GridLength(Math.Clamp(pane.ActualWidth + change, pane.MinWidth,
            Math.Max(pane.MinWidth, maximum)));
    }

    private void FitSidePanes()
    {
        WorkspaceLayout.FitSidePanes(WorkspaceLayout.ActualWidth);
    }

    private void OnPreviewSceneReady(HeadPreviewScene scene, bool resetPosition) =>
        _previewHost.SetScene(scene, resetPosition);

    private void OnPreviewCleared(object? sender, EventArgs e) => _previewHost.ClearScene();

    private void OnPreviewFailed(Exception exception)
    {
        AppLog.Error("PREVIEW:FRAME-FAILED", exception);
        _viewModel.SetPreviewError(Loc.Format("loc_MainWindow_LiveDeformationPreviewFailedLog", AppLog.FilePath));
    }

    private void OnExitRequested(object? sender, EventArgs e) => Close();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        EndNumericEdit();
        var settings = new SettingsWindow(_viewModel) { Owner = this };
        _ = settings.ShowDialog();
        if (!settings.ClearDataRequested) return;
        _closeConfirmed = true;
        if (Application.Current is App app) app.ClearApplicationDataOnExit = true;
        Close();
    }

    private void OnPreviewDeformationReady(HeadPreviewDeformationUpdate update)
    {
        try
        {
            _previewHost.UpdateDeformation(update);
        }
        catch (Exception exception)
        {
            AppLog.Error("PREVIEW:GEOMETRY-FAILED", exception);
            _viewModel.SetPreviewError(Loc.Format("loc_MainWindow_LiveDeformationPreviewFailedLog", AppLog.FilePath));
        }
    }

    private void OnPreviewMaterialReady(HeadPreviewMaterialUpdate update)
    {
        try
        {
            _previewHost.UpdateMaterials(update);
        }
        catch (Exception exception)
        {
            AppLog.Error("PREVIEW:MATERIAL-FAILED", exception);
            _viewModel.SetPreviewError(Loc.Format("loc_MainWindow_LiveMaterialPreviewFailedLog", AppLog.FilePath));
        }
    }

    private void OnPreviewOptionsChanged(object? sender, EventArgs e) =>
        _previewHost.SetOptions(_viewModel.CreatePreviewOptions(), allowInactive: true);

    private void OnFrontClick(object sender, RoutedEventArgs e) => _previewHost.ResetFront();

    private void OnThreeQuarterClick(object sender, RoutedEventArgs e) => _previewHost.ResetThreeQuarter();

    private void OnCopyErrorClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.ErrorMessage))
        {
            Clipboard.SetText(_viewModel.ErrorMessage);
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetSingleDroppedFile(e.Data, out var path) &&
                    _viewModel.CanOpenDroppedFile(path)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (TryGetSingleDroppedFile(e.Data, out var path) &&
            _viewModel.CanOpenDroppedFile(path))
        {
            await _viewModel.OpenDroppedFileAsync(path);
        }
    }

    private static bool TryGetSingleDroppedFile(IDataObject data, out string path)
    {
        path = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files ||
            !File.Exists(files[0]))
        {
            return false;
        }

        path = files[0];
        return true;
    }

    private void OnFaceListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_viewModel.LoadSelectedFaceCommand.CanExecute(null))
        {
            _viewModel.LoadSelectedFaceCommand.Execute(null);
        }
    }

    private void OnFaceListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source)
        {
            return;
        }
        if (ItemsControl.ContainerFromElement(list, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnBoneHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin && FindVisualAncestor<Button>(origin) is not null)
        {
            return;
        }
        if (sender is DependencyObject source &&
            ItemsControl.ContainerFromElement(BoneList, source) is ListBoxItem { DataContext: BoneTransformEditorViewModel transform })
        {
            if (_viewModel.Editor is { } editor)
            {
                editor.SelectedBoneTransform = ReferenceEquals(editor.SelectedBoneTransform, transform)
                    ? null
                    : transform;
            }
            e.Handled = true;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void OnFaceContextMenuOpened(object sender, RoutedEventArgs e) =>
        _viewModel.RefreshClipboardCommandAvailability();

    private void OnEditorSliderStarted(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IContinuousEditViewModel edit } control)
        {
            _numericEdit.Begin(control, edit.BeginEdit, edit.EndEdit);
        }
    }

    private void OnEditorSliderCompleted(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _numericEdit.Complete(sender);
    }

    private void OnTextureCandidateMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaterialTextureOption item })
        {
            _ = item.EnsureThumbnailAsync();
        }
    }

    private void OnCommittedTextureMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaterialTextureEditorViewModel editor })
        {
            _ = editor.SelectedTexture?.EnsureThumbnailAsync();
        }
    }

    private void OnNumericValueKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            EndNumericEdit();
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndNumericEdit();
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnNumericValueGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _ = textBox.Dispatcher.BeginInvoke(textBox.SelectAll);
        }
    }

    private void OnNumericValueMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox { DataContext: IContinuousEditViewModel edit } control ||
            e.Delta == 0)
        {
            return;
        }

        if (!_numericEdit.IsActiveFor(control))
        {
            _numericEdit.Begin(control, edit.BeginEdit, edit.EndEdit);
        }
        else
        {
            // Another editor surface may have completed the earlier burst's history group.
            edit.BeginEdit();
        }

        var increment = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? 10f
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 1f : 0.1f;
        var direction = Math.Sign(e.Delta);
        edit.Value = NumericWheelValue.Adjust(edit.Value, increment, direction);
        _numericEdit.RestartWheelTimeout();
        e.Handled = true;
    }

    private void EndNumericEdit() => _numericEdit.Complete();

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;
        if (await _viewModel.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SavePaneWidths();
        _numericEdit.Dispose();
        Closing -= OnClosing;
        Closed -= OnClosed;
        LayoutUpdated -= OnPaneLayoutUpdated;
        _viewModel.PreviewSceneReady -= OnPreviewSceneReady;
        _viewModel.PreviewCleared -= OnPreviewCleared;
        _viewModel.PreviewDeformationReady -= OnPreviewDeformationReady;
        _viewModel.PreviewMaterialReady -= OnPreviewMaterialReady;
        _viewModel.PreviewOptionsChanged -= OnPreviewOptionsChanged;
        _viewModel.ExitRequested -= OnExitRequested;
        _previewHost.FramePresented -= _viewModel.SetFrameStatus;
        _previewHost.PreviewFailed -= OnPreviewFailed;
        _orbitInput.Dispose();
        _previewHost.Dispose();
    }
}
