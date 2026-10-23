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
    private readonly DispatcherTimer _resizeTimer;
    private readonly DispatcherTimer _animationTimer;
    private readonly HeadOrbitCamera _camera = new();
    private readonly HeadPreviewSceneState _scene = new();
    private HeadPreviewRenderer? _renderer;
    private HeadPreviewOptions _options = new();
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _renderQueued;
    private bool _queuedRenderMayRunInactive;
    private bool _cameraInitialized;
    private long _scenePublicationVersion;
    private long _settledScenePublicationVersion;
    private bool _disposed;

    public HeadPreviewHostController(Window window, FrameworkElement host, Image image)
    {
        _window = window;
        _host = host;
        _image = image;
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _resizeTimer.Tick += OnResizeTimerTick;
        _animationTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1.0 / 30)
        };
        _animationTimer.Tick += OnAnimationTimerTick;
        _window.Activated += OnWindowActivityChanged;
        _window.Deactivated += OnWindowActivityChanged;
        _window.StateChanged += OnWindowActivityChanged;
        _window.IsVisibleChanged += OnVisibilityChanged;
        _window.Closed += OnWindowClosed;
        _host.SizeChanged += OnHostSizeChanged;
        _host.IsVisibleChanged += OnVisibilityChanged;
    }

    public event Action<HeadPreviewFrame>? FramePresented;
    public event Action<string>? PreviewFailed;

    public HeadPreviewDiagnostics Diagnostics { get; } = new();
    public HeadOrbitCamera Camera => _camera;

    public void SetScene(HeadPreviewScene scene, bool resetPosition = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        if (_renderer is not null) _renderer.SetScene(scene);
        else _scene.SetScene(scene);
        _scenePublicationVersion++;
        // Clearing a face releases its resources, but must not make the next
        // face reset the user's orbit as if this were the first preview.
        if (!_cameraInitialized)
        {
            _camera.Fit(scene.Bounds);
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
        _resizeTimer.Stop();
        _animationTimer.Stop();
        if (_renderer is not null) _renderer.ClearScene();
        else _scene.ClearScene();
        _bitmap = null;
        _pixelBuffer = null;
        _renderQueued = false;
        _queuedRenderMayRunInactive = false;
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
        if (_disposed || !_scene.IsLoaded || !CanRenderNow(allowInactive))
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

    private bool CanRender => PreviewRenderPolicy.CanRender(new PreviewActivityState(
        _window.IsVisible,
        _window.IsActive,
        _window.WindowState == WindowState.Minimized,
        _disposed,
        _host.ActualWidth,
        _host.ActualHeight));

    private bool CanRenderNow(bool allowInactive) => allowInactive
        ? PreviewRenderPolicy.CanRenderExplicitly(new PreviewActivityState(
            _window.IsVisible,
            _window.IsActive,
            _window.WindowState == WindowState.Minimized,
            _disposed,
            _host.ActualWidth,
            _host.ActualHeight))
        : CanRender;

    private void RenderNow(bool allowInactive = false)
    {
        if (_disposed || !_scene.IsLoaded || !CanRenderNow(allowInactive))
        {
            return;
        }

        try
        {
            var (width, height) = GetPixelSize();
            EnsureRenderer(width, height);
            var frame = _renderer!.Render(_camera, _options, _pixelBuffer);
            _bitmap!.WritePixels(
                new Int32Rect(0, 0, width, height),
                frame.BgraPixels,
                checked(width * 4),
                0);
            Diagnostics.FramesRendered++;
            FramePresented?.Invoke(frame);
            if (_settledScenePublicationVersion != _scenePublicationVersion)
            {
                // Replacing a complete GPU scene can leave the first WPF-hosted
                // frame with stale material resources on some D3D drivers. A
                // camera input always repaired it because that requested the
                // next frame. Settle each scene publication here so a loaded
                // head never depends on unrelated viewport input.
                _settledScenePublicationVersion = _scenePublicationVersion;
                RequestRender(allowInactive: true);
            }
        }
        catch (Exception exception)
        {
            _animationTimer.Stop();
            PreviewFailed?.Invoke(exception.Message);
        }
    }

    private void EnsureRenderer(int width, int height)
    {
        if (_renderer is null)
        {
            _renderer = new HeadPreviewRenderer(width, height, _scene);
            Diagnostics.RendererCreations++;
        }
        else if (_pixelWidth != width || _pixelHeight != height)
        {
            _renderer.Resize(width, height);
            Diagnostics.ResizeOperations++;
        }

        if (_bitmap is null || _pixelWidth != width || _pixelHeight != height)
        {
            _pixelWidth = width;
            _pixelHeight = height;
            _pixelBuffer = new byte[checked(width * height * 4)];
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;
        }
    }

    private (int Width, int Height) GetPixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(_host);
        var width = Math.Clamp((int)Math.Round(_host.ActualWidth * dpi.DpiScaleX), 1, MaximumPreviewDimension);
        var height = Math.Clamp((int)Math.Round(_host.ActualHeight * dpi.DpiScaleY), 1, MaximumPreviewDimension);
        return (width, height);
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || !_scene.IsLoaded)
        {
            return;
        }
        UpdateAnimationTimer();
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeTimerTick(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        RequestRender();
    }

    private void OnAnimationTimerTick(object? sender, EventArgs e) => RequestRender();

    private void UpdateAnimationTimer()
    {
        var animate = CanRender && _host.IsVisible &&
                      _options.RenderMode != HeadPreviewRenderMode.Diagnostic &&
                      _scene.HasAnimatedMaterials;
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
        _resizeTimer.Stop();
        _animationTimer.Stop();
        _animationTimer.Tick -= OnAnimationTimerTick;
        _resizeTimer.Tick -= OnResizeTimerTick;
        _window.Activated -= OnWindowActivityChanged;
        _window.Deactivated -= OnWindowActivityChanged;
        _window.StateChanged -= OnWindowActivityChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.Closed -= OnWindowClosed;
        _host.SizeChanged -= OnHostSizeChanged;
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
        _pixelBuffer = null;
    }
}
