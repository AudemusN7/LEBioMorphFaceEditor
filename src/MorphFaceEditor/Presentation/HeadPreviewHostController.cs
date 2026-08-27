using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Presentation;

public sealed class HeadPreviewHostController : IDisposable
{
    private const int MaximumPreviewDimension = 2048;
    private const float InitialZoomWheelDelta = 240;
    private readonly Window _window;
    private readonly FrameworkElement _host;
    private readonly Image _image;
    private readonly DispatcherTimer _resizeTimer;
    private readonly HeadOrbitCamera _camera = new();
    private HeadPreviewRenderer? _renderer;
    private HeadPreviewScene? _scene;
    private HeadPreviewOptions _options = new();
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _renderQueued;
    private bool _queuedRenderMayRunInactive;
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
        _window.Activated += OnWindowActivityChanged;
        _window.Deactivated += OnWindowActivityChanged;
        _window.StateChanged += OnWindowActivityChanged;
        _window.IsVisibleChanged += OnVisibilityChanged;
        _window.Closed += OnWindowClosed;
        _host.SizeChanged += OnHostSizeChanged;
    }

    public event Action<HeadPreviewFrame>? FramePresented;
    public event Action<string>? PreviewFailed;

    public HeadPreviewDiagnostics Diagnostics { get; } = new();
    public HeadOrbitCamera Camera => _camera;

    public void SetScene(HeadPreviewScene scene, bool resetPosition = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var isFirstScene = _scene is null;
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        if (isFirstScene)
        {
            _camera.Fit(scene.Bounds);
            ResetCamera(_camera.ResetThreeQuarter);
        }
        else
        {
            _camera.UpdateBounds(scene.Bounds);
            if (resetPosition)
            {
                ResetCamera(_camera.ResetPosition);
            }
        }
        if (_renderer is not null)
        {
            _renderer.SetScene(scene);
        }
        RequestRender();
    }

    public void SetOptions(HeadPreviewOptions options, bool allowInactive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        RequestRender(allowInactive);
    }

    public void UpdateDeformation(HeadPreviewDeformationUpdate update)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);
        if (_scene is null)
        {
            throw new InvalidOperationException("A preview scene must be loaded before deformation can be updated.");
        }
        var meshIndex = _scene.Meshes.ToList().FindIndex(mesh => string.Equals(mesh.Name, update.MeshName, StringComparison.Ordinal));
        if (meshIndex < 0)
        {
            throw new KeyNotFoundException($"Preview mesh '{update.MeshName}' is not loaded.");
        }
        var meshes = _scene.Meshes.ToArray();
        meshes[meshIndex] = meshes[meshIndex] with { Vertices = update.Vertices };
        foreach (var attachmentUpdate in update.AttachmentUpdates)
        {
            var attachmentIndex = meshes.ToList().FindIndex(mesh =>
                string.Equals(mesh.Name, attachmentUpdate.MeshName, StringComparison.Ordinal));
            if (attachmentIndex < 0)
            {
                throw new KeyNotFoundException($"Preview attachment '{attachmentUpdate.MeshName}' is not loaded.");
            }
            meshes[attachmentIndex] = meshes[attachmentIndex] with { Vertices = attachmentUpdate.Vertices };
        }
        _scene = _scene with { Meshes = meshes, SkinningPalette = update.SkinningPalette };
        if (_renderer is not null)
        {
            _renderer.UpdateMeshVertices(update.MeshName, update.Vertices);
            foreach (var attachmentUpdate in update.AttachmentUpdates)
            {
                _renderer.UpdateMeshVertices(attachmentUpdate.MeshName, attachmentUpdate.Vertices);
            }
            _renderer.SetSkinningPalette(update.SkinningPalette);
        }
        RequestRender();
    }

    public void UpdateMaterials(HeadPreviewMaterialUpdate update)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);
        if (_scene is null)
        {
            throw new InvalidOperationException("A preview scene must be loaded before materials can be updated.");
        }
        var meshes = _scene.Meshes.Select(mesh => mesh with
        {
            Sections = mesh.Sections.Select(section =>
                update.Materials.TryGetValue(section.Material.Key, out var material)
                    ? section with { Material = material }
                    : section).ToArray()
        }).ToArray();
        _scene = _scene with { Meshes = meshes };
        _renderer?.UpdateMaterials(update.Materials);
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
        if (_disposed || _scene is null || !CanRenderNow(allowInactive))
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
        ? _window.IsVisible && _window.WindowState != WindowState.Minimized && !_disposed &&
          _host.ActualWidth > 0 && _host.ActualHeight > 0
        : CanRender;

    private void RenderNow(bool allowInactive = false)
    {
        if (_disposed || _scene is null || !CanRenderNow(allowInactive))
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
        }
        catch (Exception exception)
        {
            PreviewFailed?.Invoke(exception.Message);
        }
    }

    private void EnsureRenderer(int width, int height)
    {
        if (_renderer is null)
        {
            _renderer = new HeadPreviewRenderer(width, height);
            _renderer.SetScene(_scene!);
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
        if (_disposed || _scene is null)
        {
            return;
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeTimerTick(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        RequestRender();
    }

    private void OnWindowActivityChanged(object? sender, EventArgs e) => RequestRender();

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => RequestRender();

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _resizeTimer.Stop();
        _resizeTimer.Tick -= OnResizeTimerTick;
        _window.Activated -= OnWindowActivityChanged;
        _window.Deactivated -= OnWindowActivityChanged;
        _window.StateChanged -= OnWindowActivityChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.Closed -= OnWindowClosed;
        _host.SizeChanged -= OnHostSizeChanged;
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
