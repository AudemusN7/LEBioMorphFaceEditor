using System.Windows;
using System.Windows.Input;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Presentation;

public sealed class OrbitInputController : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly HeadOrbitCamera _camera;
    private readonly Action _requestRender;
    private Point _lastPosition;
    private MouseButton? _dragButton;
    private bool _disposed;

    public OrbitInputController(FrameworkElement host, HeadOrbitCamera camera, Action requestRender)
    {
        _host = host;
        _camera = camera;
        _requestRender = requestRender;
        _host.MouseDown += OnMouseDown;
        _host.MouseMove += OnMouseMove;
        _host.MouseUp += OnMouseUp;
        _host.MouseWheel += OnMouseWheel;
        _host.LostMouseCapture += OnLostMouseCapture;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle or MouseButton.Right))
        {
            return;
        }
        _dragButton = e.ChangedButton;
        _lastPosition = e.GetPosition(_host);
        _host.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragButton is null || !_host.IsMouseCaptured)
        {
            return;
        }

        var current = e.GetPosition(_host);
        var delta = current - _lastPosition;
        _lastPosition = current;
        if (_dragButton == MouseButton.Left)
        {
            // The presented image is mirrored, so horizontal input is mirrored too.
            _camera.Orbit((float)(delta.X * 0.008), (float)(delta.Y * 0.008));
        }
        else if (_dragButton == MouseButton.Middle)
        {
            var hostHeight = Math.Max(_host.ActualHeight, 1);
            var verticalFovRadians = _camera.VerticalFieldOfViewDegrees * MathF.PI / 180f;
            var worldUnitsPerPixel = 2f * _camera.Distance * MathF.Tan(verticalFovRadians * 0.5f) / (float)hostHeight;
            _camera.Pan(
                (float)delta.X * worldUnitsPerPixel,
                (float)delta.Y * worldUnitsPerPixel);
        }
        else
        {
            _camera.Zoom((float)(-delta.Y * 12));
        }
        _requestRender();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != _dragButton)
        {
            return;
        }
        _dragButton = null;
        _host.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _camera.Zoom(e.Delta);
        _requestRender();
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e) => _dragButton = null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _host.MouseDown -= OnMouseDown;
        _host.MouseMove -= OnMouseMove;
        _host.MouseUp -= OnMouseUp;
        _host.MouseWheel -= OnMouseWheel;
        _host.LostMouseCapture -= OnLostMouseCapture;
        if (_host.IsMouseCaptured)
        {
            _host.ReleaseMouseCapture();
        }
    }
}
