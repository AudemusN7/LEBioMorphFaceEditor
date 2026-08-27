using System.Windows;
using MorphFaceEditor.Presentation;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Models;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace MorphFaceEditor;

/// <summary>Thin WPF event bridge between bindings, orbit input and the preview host.</summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly HeadPreviewHostController _previewHost;
    private readonly OrbitInputController _orbitInput;
    private readonly DispatcherTimer _numericWheelCommitTimer;
    private IContinuousEditViewModel? _numericWheelEdit;
    private bool _closeConfirmed;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        _previewHost = new HeadPreviewHostController(this, PreviewHost, PreviewImage);
        _orbitInput = new OrbitInputController(PreviewHost, _previewHost.Camera, () => _previewHost.RequestRender());
        _numericWheelCommitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _numericWheelCommitTimer.Tick += OnNumericWheelCommitTimer;
        _viewModel.PreviewSceneReady += OnPreviewSceneReady;
        _viewModel.PreviewDeformationReady += OnPreviewDeformationReady;
        _viewModel.PreviewMaterialReady += OnPreviewMaterialReady;
        _viewModel.PreviewOptionsChanged += OnPreviewOptionsChanged;
        _previewHost.FramePresented += _viewModel.SetFrameStatus;
        _previewHost.PreviewFailed += _viewModel.SetPreviewError;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnPreviewSceneReady(HeadPreviewScene scene, bool resetPosition) =>
        _previewHost.SetScene(scene, resetPosition);

    private void OnPreviewDeformationReady(HeadPreviewDeformationUpdate update)
    {
        try
        {
            _previewHost.UpdateDeformation(update);
        }
        catch (Exception exception)
        {
            AppLog.Error("Live deformation preview update failed.", exception);
            _viewModel.SetPreviewError($"Live deformation preview failed. Log: {AppLog.FilePath}");
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
            AppLog.Error("Live material preview update failed.", exception);
            _viewModel.SetPreviewError($"Live material preview failed. Log: {AppLog.FilePath}");
        }
    }

    private void OnPreviewOptionsChanged(object? sender, EventArgs e) =>
        _previewHost.SetOptions(_viewModel.CreatePreviewOptions(), allowInactive: true);

    private void OnFrontClick(object sender, RoutedEventArgs e) => _previewHost.ResetFront();

    private void OnThreeQuarterClick(object sender, RoutedEventArgs e) => _previewHost.ResetThreeQuarter();

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

    private void OnFaceContextMenuOpened(object sender, RoutedEventArgs e) =>
        _viewModel.RefreshClipboardCommandAvailability();

    private void OnEditorSliderStarted(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        EndNumericWheelEdit();
        if (sender is FrameworkElement { DataContext: IContinuousEditViewModel edit })
        {
            edit.BeginEdit();
        }
    }

    private void OnEditorSliderCompleted(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IContinuousEditViewModel edit })
        {
            edit.EndEdit();
        }
    }

    private void OnTextureCandidateMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaterialTextureOption item })
        {
            _ = item.EnsureThumbnailAsync();
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
            EndNumericWheelEdit();
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndNumericWheelEdit();
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
        if (sender is not TextBox { IsKeyboardFocusWithin: true, DataContext: IContinuousEditViewModel edit } ||
            e.Delta == 0)
        {
            return;
        }

        if (!ReferenceEquals(_numericWheelEdit, edit))
        {
            EndNumericWheelEdit();
            _numericWheelEdit = edit;
            edit.BeginEdit();
        }

        var increment = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? 10f
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 1f : 0.1f;
        var direction = Math.Sign(e.Delta);
        edit.Value = NumericWheelValue.Adjust(edit.Value, increment, direction);
        _numericWheelCommitTimer.Stop();
        _numericWheelCommitTimer.Start();
        e.Handled = true;
    }

    private void OnNumericValueLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IContinuousEditViewModel edit } &&
            ReferenceEquals(_numericWheelEdit, edit))
        {
            EndNumericWheelEdit();
        }
    }

    private void OnNumericWheelCommitTimer(object? sender, EventArgs e) => EndNumericWheelEdit();

    private void EndNumericWheelEdit()
    {
        _numericWheelCommitTimer.Stop();
        var edit = _numericWheelEdit;
        _numericWheelEdit = null;
        edit?.EndEdit();
    }

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
        EndNumericWheelEdit();
        _numericWheelCommitTimer.Tick -= OnNumericWheelCommitTimer;
        Closing -= OnClosing;
        Closed -= OnClosed;
        _viewModel.PreviewSceneReady -= OnPreviewSceneReady;
        _viewModel.PreviewDeformationReady -= OnPreviewDeformationReady;
        _viewModel.PreviewMaterialReady -= OnPreviewMaterialReady;
        _viewModel.PreviewOptionsChanged -= OnPreviewOptionsChanged;
        _previewHost.FramePresented -= _viewModel.SetFrameStatus;
        _previewHost.PreviewFailed -= _viewModel.SetPreviewError;
        _orbitInput.Dispose();
        _previewHost.Dispose();
    }
}
