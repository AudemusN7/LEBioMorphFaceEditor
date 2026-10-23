using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace MorphFaceEditor.Rendering;

/// <summary>One preview image and only the views required to use it.</summary>
internal sealed class HeadPreviewSurface : IDisposable
{
    private bool _disposed;

    private HeadPreviewSurface(Texture2D texture, int width, int height)
    {
        Texture = texture;
        Width = width;
        Height = height;
    }

    public Texture2D Texture { get; }
    public int Width { get; }
    public int Height { get; }
    public RenderTargetView Target { get; private set; } = null!;
    public ShaderResourceView ShaderView { get; private set; } = null!;
    public DepthStencilView DepthView { get; private set; } = null!;

    public static HeadPreviewSurface Color(
        Device device, int width, int height, Format format, BindFlags bindings, SampleDescription samples) =>
        Create(device, Description(width, height, format, bindings, samples), depth: false);

    public static HeadPreviewSurface Depth(
        Device device, int width, int height, BindFlags bindings, SampleDescription samples) =>
        Create(device, Description(width, height, Format.R32_Typeless, bindings, samples), depth: true);

    public static HeadPreviewSurface Staging(Device device, int width, int height)
    {
        var description = Description(width, height, Format.B8G8R8A8_UNorm, BindFlags.None, new SampleDescription(1, 0));
        description.Usage = ResourceUsage.Staging;
        description.CpuAccessFlags = CpuAccessFlags.Read;
        return Create(device, description, depth: false);
    }

    private static Texture2DDescription Description(
        int width, int height, Format format, BindFlags bindings, SampleDescription samples) => new()
    {
        Width = width,
        Height = height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDescription = samples,
        Usage = ResourceUsage.Default,
        BindFlags = bindings
    };

    private static HeadPreviewSurface Create(Device device, Texture2DDescription description, bool depth)
    {
        var surface = new HeadPreviewSurface(new Texture2D(device, description), description.Width, description.Height);
        try
        {
            if ((description.BindFlags & BindFlags.RenderTarget) != 0)
            {
                surface.Target = new RenderTargetView(device, surface.Texture);
            }
            if ((description.BindFlags & BindFlags.DepthStencil) != 0)
            {
                surface.DepthView = new DepthStencilView(device, surface.Texture, new DepthStencilViewDescription
                {
                    Format = Format.D32_Float,
                    Dimension = description.SampleDescription.Count > 1
                        ? DepthStencilViewDimension.Texture2DMultisampled
                        : DepthStencilViewDimension.Texture2D,
                    Texture2D = new DepthStencilViewDescription.Texture2DResource { MipSlice = 0 }
                });
            }
            if ((description.BindFlags & BindFlags.ShaderResource) != 0)
            {
                surface.ShaderView = depth
                    ? new ShaderResourceView(device, surface.Texture, new ShaderResourceViewDescription
                    {
                        Format = Format.R32_Float,
                        Dimension = description.SampleDescription.Count > 1
                            ? ShaderResourceViewDimension.Texture2DMultisampled
                            : ShaderResourceViewDimension.Texture2D,
                        Texture2D = new ShaderResourceViewDescription.Texture2DResource
                        {
                            MostDetailedMip = 0,
                            MipLevels = 1
                        }
                    })
                    : new ShaderResourceView(device, surface.Texture);
            }
            return surface;
        }
        catch
        {
            surface.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ShaderView?.Dispose();
        DepthView?.Dispose();
        Target?.Dispose();
        Texture.Dispose();
    }
}

/// <summary>The fixed set of preview surfaces that share one viewport size.</summary>
internal sealed class HeadPreviewRenderTargets : IDisposable
{
    private readonly List<HeadPreviewSurface> _surfaces = new(18);

    public HeadPreviewRenderTargets(Device device, int width, int height, SampleDescription samples)
    {
        Width = width;
        Height = height;
        var singleSample = new SampleDescription(1, 0);
        const BindFlags sampledTarget = BindFlags.RenderTarget | BindFlags.ShaderResource;
        var bloomWidth = Math.Max(1, (width + 3) / 4);
        var bloomHeight = Math.Max(1, (height + 3) / 4);
        try
        {
            Bloom = Add(HeadPreviewSurface.Color(device, bloomWidth, bloomHeight, Format.R16G16B16A16_Float, sampledTarget, singleSample));
            BloomScratch = Add(HeadPreviewSurface.Color(device, bloomWidth, bloomHeight, Format.R16G16B16A16_Float, sampledTarget, singleSample));
            // Preserve full-precision selected depth and its matching covered MSAA normal.
            AoSurface = Add(HeadPreviewSurface.Color(device, width, height, Format.R32G32B32A32_Float, sampledTarget, singleSample));
            AoReceiverPlane = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16B16A16_Float, sampledTarget, singleSample));
            AoVisibility = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16_Float, sampledTarget, singleSample));
            AoScratch = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16_Float, sampledTarget, singleSample));
            AoComposited = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16B16A16_Float, sampledTarget, singleSample));
            Color = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16B16A16_Float, BindFlags.RenderTarget, samples));
            Normal = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16B16A16_Float, BindFlags.RenderTarget, samples));
            OpaquePlane = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16B16A16_Float, sampledTarget, samples));
            AmbientDiffuse = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16B16A16_Float, BindFlags.RenderTarget, samples));
            AoNormal = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16B16A16_Float, BindFlags.ShaderResource, samples));
            Depth = Add(HeadPreviewSurface.Depth(device, width, height, BindFlags.DepthStencil, samples));
            AoDepth = Add(HeadPreviewSurface.Depth(device, width, height, BindFlags.ShaderResource, samples));
            Resolved = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16B16A16_Float, BindFlags.ShaderResource, singleSample));
            ResolvedAmbient = Add(HeadPreviewSurface.Color(device, width, height, Format.R16G16B16A16_Float, BindFlags.ShaderResource, singleSample));
            Post = Add(HeadPreviewSurface.Color(device, width, height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget, singleSample));
            Staging = Add(HeadPreviewSurface.Staging(device, width, height));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width { get; }
    public int Height { get; }
    public HeadPreviewSurface Color { get; }
    public HeadPreviewSurface Normal { get; }
    public HeadPreviewSurface OpaquePlane { get; }
    public HeadPreviewSurface AmbientDiffuse { get; }
    public HeadPreviewSurface AoNormal { get; }
    public HeadPreviewSurface Depth { get; }
    public HeadPreviewSurface AoDepth { get; }
    public HeadPreviewSurface Resolved { get; }
    public HeadPreviewSurface ResolvedAmbient { get; }
    public HeadPreviewSurface AoSurface { get; }
    public HeadPreviewSurface AoReceiverPlane { get; }
    public HeadPreviewSurface AoVisibility { get; }
    public HeadPreviewSurface AoScratch { get; }
    public HeadPreviewSurface AoComposited { get; }
    public HeadPreviewSurface Post { get; }
    public HeadPreviewSurface Staging { get; }
    public HeadPreviewSurface Bloom { get; }
    public HeadPreviewSurface BloomScratch { get; }

    private HeadPreviewSurface Add(HeadPreviewSurface surface)
    {
        _surfaces.Add(surface);
        return surface;
    }

    public void Dispose()
    {
        foreach (var surface in _surfaces) surface.Dispose();
        _surfaces.Clear();
    }
}
