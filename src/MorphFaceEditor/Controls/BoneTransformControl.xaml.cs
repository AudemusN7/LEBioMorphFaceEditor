using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Controls;

public partial class BoneTransformControl : UserControl
{
    private readonly DispatcherTimer _wheelCommitTimer;
    private BoneTransformEditorViewModel? _model;
    private bool _isDragging;
    private bool _isWheelEditing;

    public BoneTransformControl()
    {
        InitializeComponent();
        _wheelCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _wheelCommitTimer.Tick += OnWheelCommitTimer;
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachModel();
        AttachModel(e.NewValue as BoneTransformEditorViewModel);
        RenderValues();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            AttachModel(DataContext as BoneTransformEditorViewModel);
            RenderValues();
        }
    }

    private void AttachModel(BoneTransformEditorViewModel? model)
    {
        _model = model;
        if (_model is not null)
        {
            _model.X.PropertyChanged += OnAxisPropertyChanged;
            _model.Y.PropertyChanged += OnAxisPropertyChanged;
            _model.Z.PropertyChanged += OnAxisPropertyChanged;
        }
    }

    private void OnAxisPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BoneAxisEditorViewModel.Value) or
            nameof(BoneAxisEditorViewModel.Minimum) or
            nameof(BoneAxisEditorViewModel.Maximum))
        {
            RenderValues();
        }
    }

    private void OnPadMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_model is null || !_model.IsAvailable)
        {
            return;
        }
        EndWheelEdit();
        _isDragging = true;
        _model.BeginPuckEdit();
        Pad.CaptureMouse();
        ApplyPadPosition(e.GetPosition(PuckCanvas));
        e.Handled = true;
    }

    private void OnPadMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            ApplyPadPosition(e.GetPosition(PuckCanvas));
            e.Handled = true;
        }
    }

    private void OnPadMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }
        ApplyPadPosition(e.GetPosition(PuckCanvas));
        Pad.ReleaseMouseCapture();
        EndDrag();
        e.Handled = true;
    }

    private void OnPadLostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void OnPadMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_model is null || !_model.IsAvailable || e.Delta == 0)
        {
            return;
        }
        if (!_isDragging && !_isWheelEditing)
        {
            _isWheelEditing = true;
            _model.BeginPuckEdit();
        }
        var increment = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? 10f
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 1f : 0.1f;
        var adjusted = NumericWheelValue.Adjust(_model.X.Value, increment, Math.Sign(e.Delta));
        _model.X.Value = Math.Clamp(adjusted, _model.X.Minimum, _model.X.Maximum);
        if (!_isDragging)
        {
            _wheelCommitTimer.Stop();
            _wheelCommitTimer.Start();
        }
        e.Handled = true;
    }

    private void ApplyPadPosition(Point point)
    {
        if (_model is null || PuckCanvas.ActualWidth <= 0 || PuckCanvas.ActualHeight <= 0)
        {
            return;
        }
        var size = Math.Min(PuckCanvas.ActualWidth, PuckCanvas.ActualHeight);
        var inset = Math.Max(14, size * 0.07);
        var travel = Math.Max(1, size - inset * 2);
        var horizontal = Math.Clamp((point.X - inset) / travel, 0, 1);
        var vertical = Math.Clamp((point.Y - inset) / travel, 0, 1);
        _model.Y.Value = Interpolate(_model.Y.Minimum, _model.Y.Maximum, horizontal);
        _model.Z.Value = Interpolate(_model.Z.Maximum, _model.Z.Minimum, vertical);
    }

    private void RenderValues()
    {
        if (_model is null || PuckCanvas.ActualWidth <= 0 || PuckCanvas.ActualHeight <= 0)
        {
            return;
        }
        var x = Normalize(_model.X.Value, _model.X.Minimum, _model.X.Maximum);
        var y = Normalize(_model.Y.Value, _model.Y.Minimum, _model.Y.Maximum);
        var z = Normalize(_model.Z.Value, _model.Z.Minimum, _model.Z.Maximum);
        var size = Math.Min(PuckCanvas.ActualWidth, PuckCanvas.ActualHeight);
        var inset = Math.Max(14, size * 0.07);
        var diameter = Math.Clamp(size * (0.06 + x * 0.06), 14, 48);
        Puck.Width = diameter;
        Puck.Height = diameter;
        var travel = Math.Max(1, size - inset * 2);
        Canvas.SetLeft(Puck, inset + y * travel - diameter / 2);
        Canvas.SetTop(Puck, inset + (1 - z) * travel - diameter / 2);
        Canvas.SetLeft(DepthThumb, x * Math.Max(0, DepthCanvas.ActualWidth - DepthThumb.Width));
    }

    private void OnControlSizeChanged(object sender, SizeChangedEventArgs e) => RenderValues();

    private void EndDrag()
    {
        if (!_isDragging)
        {
            return;
        }
        _isDragging = false;
        _model?.EndPuckEdit();
    }

    private void OnWheelCommitTimer(object? sender, EventArgs e) => EndWheelEdit();

    private void EndWheelEdit()
    {
        _wheelCommitTimer.Stop();
        if (!_isWheelEditing)
        {
            return;
        }
        _isWheelEditing = false;
        _model?.EndPuckEdit();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Pad.IsMouseCaptured)
        {
            Pad.ReleaseMouseCapture();
        }
        EndDrag();
        EndWheelEdit();
        DetachModel();
    }

    private void DetachModel()
    {
        if (_model is null)
        {
            return;
        }
        _model.X.PropertyChanged -= OnAxisPropertyChanged;
        _model.Y.PropertyChanged -= OnAxisPropertyChanged;
        _model.Z.PropertyChanged -= OnAxisPropertyChanged;
        _model = null;
    }

    private static float Interpolate(float minimum, float maximum, double amount) =>
        MathF.Round(minimum + (maximum - minimum) * (float)amount, 6);

    private static double Normalize(float value, float minimum, float maximum) =>
        maximum <= minimum ? 0.5 : Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
}
