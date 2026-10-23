using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Controls;

public partial class BoneTransformControl : UserControl
{
    private readonly ContinuousEditGesture _gesture = new();
    private readonly object _wheelGesture = new();
    private BoneTransformEditorViewModel? _model;

    public BoneTransformControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        CompleteInteraction();
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
        _gesture.Begin(Pad, _model.BeginPuckEdit, _model.EndPuckEdit);
        Pad.CaptureMouse();
        ApplyPadPosition(e.GetPosition(PuckCanvas));
        e.Handled = true;
    }

    private void OnPadMouseMove(object sender, MouseEventArgs e)
    {
        if (_gesture.IsActiveFor(Pad))
        {
            ApplyPadPosition(e.GetPosition(PuckCanvas));
            e.Handled = true;
        }
    }

    private void OnPadMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gesture.IsActiveFor(Pad))
        {
            return;
        }
        ApplyPadPosition(e.GetPosition(PuckCanvas));
        Pad.ReleaseMouseCapture();
        _gesture.Complete(Pad);
        e.Handled = true;
    }

    private void OnPadMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_model is null || !_model.IsAvailable || e.Delta == 0)
        {
            return;
        }
        var dragging = _gesture.IsActiveFor(Pad);
        if (!dragging && !_gesture.IsActiveFor(_wheelGesture))
        {
            _gesture.Begin(Pad, _model.BeginPuckEdit, _model.EndPuckEdit, _wheelGesture);
        }
        else if (!dragging)
        {
            _model.BeginPuckEdit();
        }
        var increment = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? 10f
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 1f : 0.1f;
        var adjusted = NumericWheelValue.Adjust(_model.X.Value, increment, Math.Sign(e.Delta));
        _model.X.Value = Math.Clamp(adjusted, _model.X.Minimum, _model.X.Maximum);
        if (!dragging)
        {
            _gesture.RestartWheelTimeout();
        }
        e.Handled = true;
    }

    private void OnDepthMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_model is null || !_model.IsAvailable)
        {
            return;
        }
        _gesture.Begin(DepthTrack, _model.BeginPuckEdit, _model.EndPuckEdit);
        DepthTrack.CaptureMouse();
        ApplyDepthPosition(e.GetPosition(DepthTrack));
        e.Handled = true;
    }

    private void OnDepthMouseMove(object sender, MouseEventArgs e)
    {
        if (_gesture.IsActiveFor(DepthTrack))
        {
            ApplyDepthPosition(e.GetPosition(DepthTrack));
            e.Handled = true;
        }
    }

    private void OnDepthMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gesture.IsActiveFor(DepthTrack))
        {
            return;
        }
        ApplyDepthPosition(e.GetPosition(DepthTrack));
        DepthTrack.ReleaseMouseCapture();
        _gesture.Complete(DepthTrack);
        e.Handled = true;
    }

    private void ApplyDepthPosition(Point point)
    {
        if (_model is null || DepthTrack.ActualWidth <= 0)
        {
            return;
        }
        var thumbWidth = DepthThumb.Width;
        var travel = Math.Max(1, DepthTrack.ActualWidth - thumbWidth);
        var amount = Math.Clamp((point.X - thumbWidth / 2) / travel, 0, 1);
        _model.X.Value = Interpolate(_model.X.Minimum, _model.X.Maximum, amount);
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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompleteInteraction();
        DetachModel();
    }

    private void CompleteInteraction()
    {
        if (Pad.IsMouseCaptured)
        {
            Pad.ReleaseMouseCapture();
        }
        if (DepthTrack.IsMouseCaptured)
        {
            DepthTrack.ReleaseMouseCapture();
        }
        _gesture.Complete();
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
