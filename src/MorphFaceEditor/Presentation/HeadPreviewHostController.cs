using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Presentation;

public sealed class HeadPreviewHostController : IDisposable
{
    // Allow large/high-DPI viewports to render at their actual pixel size.
    private const int MaximumPreviewDimension = 16384;
    private const float InitialZoomWheelDelta = 240;
    private readonly Window _window;
    private readonly FrameworkElement _host;
    private readonly Image _image;
    private readonly DispatcherTimer _animationTimer;
    private readonly Action<IntPtr, int, int> _writePixels;
    private readonly HeadOrbitCamera _camera = new();
    private readonly HeadPreviewSceneState _scene = new();
    private HeadPreviewRenderer? _renderer;
    private HeadPreviewOptions _options = new();
    private WriteableBitmap? _bitmap;
    private bool _renderQueued;
    private bool _queuedRenderMayRunInactive;
    private bool _cameraInitialized;
    private bool _disposed;

    public HeadPreviewHostController(Window window, FrameworkElement host, Image image)
    {
        _window = window;
        _host = host;
        _image = image;
        _writePixels = WritePixels;
        _animationTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1.0 / 30)
        };
        _animationTimer.Tick += OnAnimationTimerTick;
        _window.Activated += OnWindowActivityChanged;
        _window.Deactivated += OnWindowActivityChanged;
        _window.StateChanged += OnWindowActivityChanged;
        _window.Closed += OnWindowClosed;
        _host.SizeChanged += OnWindowActivityChanged;
        _host.IsVisibleChanged += OnVisibilityChanged;
    }

    public event Action<HeadPreviewFrameInfo>? FramePresented;
    public event Action<Exception>? PreviewFailed;

    public HeadPreviewDiagnostics Diagnostics { get; } = new();
    public HeadOrbitCamera Camera => _camera;

    public void SetScene(HeadPreviewScene scene, bool resetPosition = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        if (_renderer is not null) _renderer.SetScene(scene);
        else _scene.SetScene(scene);
        // Clearing a face releases its resources, but must not make the next
        // face reset the user's orbit as if this were the first preview.
        if (!_cameraInitialized)
        {
            _camera.UpdateBounds(scene.Bounds);
            ResetCamera(_camera.ResetThreeQuarter);
            _cameraInitialized = true;
        }
        else
        {
            _camera.UpdateBounds(scene.Bounds);
            if (resetPosition)
            {
                ResetCamera(_camera.ResetPosition);
            }
        }
        // Scene publication is an explicit content update. A package/head load
        // often completes while the owner is briefly inactive (for example
        // after a file dialog closes); do not drop its first render and leave
        // the old frame visible until an unrelated input event requests one.
        RequestRender(allowInactive: true);
        UpdateAnimationTimer();
    }

    public void ClearScene()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _animationTimer.Stop();
        if (_renderer is not null) _renderer.ClearScene();
        else _scene.ClearScene();
        _bitmap = null;
        _image.Source = null;
    }

    public void SetOptions(HeadPreviewOptions options, bool allowInactive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        UpdateAnimationTimer();
        RequestRender(allowInactive);
    }

    public void UpdateDeformation(HeadPreviewDeformationUpdate update)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderer is not null) _renderer.UpdateDeformation(update);
        else _scene.UpdateDeformation(update);
        RequestRender();
    }

    public void UpdateMaterials(HeadPreviewMaterialUpdate update)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);
        if (_renderer is not null) _renderer.UpdateMaterials(update.Materials);
        else _scene.UpdateMaterials(update.Materials);
        UpdateAnimationTimer();
        // A modal HDR picker deactivates the owner window. An explicit material
        // preview still needs one frame; ordinary lifecycle renders remain gated.
        RequestRender(allowInactive: true);
    }

    public void ResetFront()
    {
        ResetCamera(_camera.ResetFront);
        RequestRender();
    }

    public void ResetThreeQuarter()
    {
        ResetCamera(_camera.ResetThreeQuarter);
        RequestRender();
    }

    private void ResetCamera(Action resetView)
    {
        resetView();
        _camera.Zoom(InitialZoomWheelDelta);
    }

    public void RequestRender(bool allowInactive = false)
    {
        if (!CanRenderNow(allowInactive))
        {
            return;
        }
        if (_renderQueued)
        {
            _queuedRenderMayRunInactive |= allowInactive;
            Diagnostics.CoalescedRenderRequests++;
            return;
        }

        _renderQueued = true;
        _queuedRenderMayRunInactive = allowInactive;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var renderMayRunInactive = _queuedRenderMayRunInactive;
            _renderQueued = false;
            _queuedRenderMayRunInactive = false;
            RenderNow(renderMayRunInactive);
        });
    }

    private bool CanRenderNow(bool allowInactive = false) =>
        !_disposed && _scene.IsLoaded && _window.IsVisible && _host.IsVisible &&
        _window.WindowState != WindowState.Minimized &&
        (allowInactive || _window.IsActive) &&
        _host.ActualWidth > 0 && _host.ActualHeight > 0;

    private void RenderNow(bool allowInactive = false)
    {
        if (!CanRenderNow(allowInactive))
        {
            return;
        }

        try
        {
            var (width, height) = GetPixelSize();
            EnsureRenderer(width, height);
            var frame = _renderer!.RenderInto(_camera, _options, _writePixels);
            // Publish a replacement only after its first pixels are ready.
            if (!ReferenceEquals(_image.Source, _bitmap)) _image.Source = _bitmap;
            Diagnostics.FramesRendered++;
            FramePresented?.Invoke(frame);
        }
        catch (Exception exception)
        {
            _animationTimer.Stop();
            PreviewFailed?.Invoke(exception);
        }
    }

    private void WritePixels(IntPtr pixels, int bufferSize, int stride) =>
        _bitmap!.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight),
            pixels, bufferSize, stride);

    private void EnsureRenderer(int width, int height)
    {
        if (_renderer is null)
        {
            _renderer = new HeadPreviewRenderer(width, height, _scene);
            Diagnostics.RendererCreations++;
        }
        else if (_renderer.Resize(width, height))
        {
            Diagnostics.ResizeOperations++;
        }

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        }
    }

    private (int Width, int Height) GetPixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(_host);
        var width = Math.Clamp((int)Math.Round(_host.ActualWidth * dpi.DpiScaleX), 1, MaximumPreviewDimension);
        var height = Math.Clamp((int)Math.Round(_host.ActualHeight * dpi.DpiScaleY), 1, MaximumPreviewDimension);
        return (width, height);
    }

    private void OnAnimationTimerTick(object? sender, EventArgs e) => RequestRender();

    private void UpdateAnimationTimer()
    {
        var animate = CanRenderNow() &&
                      _options.RenderMode != HeadPreviewRenderMode.Diagnostic &&
                      _scene.HasAnimatedMaterials(_options.ShowAttachment);
        if (animate) _animationTimer.Start();
        else _animationTimer.Stop();
    }

    private void OnWindowActivityChanged(object? sender, EventArgs e)
    {
        UpdateAnimationTimer();
        RequestRender();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateAnimationTimer();
        RequestRender();
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _animationTimer.Stop();
        _animationTimer.Tick -= OnAnimationTimerTick;
        _window.Activated -= OnWindowActivityChanged;
        _window.Deactivated -= OnWindowActivityChanged;
        _window.StateChanged -= OnWindowActivityChanged;
        _window.Closed -= OnWindowClosed;
        _host.SizeChanged -= OnWindowActivityChanged;
        _host.IsVisibleChanged -= OnVisibilityChanged;
        _scene.ClearScene();
        if (_renderer is not null)
        {
            _renderer.Dispose();
            _renderer = null;
            Diagnostics.RendererDisposals++;
        }
        _image.Source = null;
        _bitmap = null;
    }
}
