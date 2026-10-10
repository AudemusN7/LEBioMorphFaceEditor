using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MorphFaceEditor.Core.Materials;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Matrix = SharpDX.Matrix;

namespace MorphFaceEditor.Rendering;

/// <summary>Owns the D3D11 preview device, GPU resources, draw passes and deterministic readback/disposal.</summary>
public sealed class HeadPreviewRenderer : IDisposable
{
    private const int MaximumBoneCount = 128;
    private const long MaximumGpuTextureBytes = 512L * 1024 * 1024;
    private const int ShadowMapSize = 2048;
    private readonly Device _device;
    private readonly DeviceContext _context;
    private readonly string _deviceKind;
    private readonly Stopwatch _animationClock = Stopwatch.StartNew();
    private readonly VertexShader _vertexShader;
    private readonly PixelShader _pixelShader;
    private readonly PixelShader _shadowPixelShader;
    private readonly VertexShader _postVertexShader;
    private readonly PixelShader _postPixelShader;
    private readonly InputLayout _inputLayout;
    private readonly Buffer _sceneBuffer;
    private readonly Buffer _materialBuffer;
    private readonly Buffer _skinningBuffer;
    private readonly Buffer _meshBuffer;
    private readonly Buffer _postProcessBuffer;
    private readonly Buffer _shadowBuffer;
    private readonly RasterizerState _solidRasterizer;
    private readonly RasterizerState _shadowRasterizer;
    private readonly RasterizerState _wireframeRasterizer;
    private readonly RasterizerState _solidBackFacesRasterizer;
    private readonly RasterizerState _solidFrontFacesRasterizer;
    private readonly RasterizerState _wireframeBackFacesRasterizer;
    private readonly RasterizerState _wireframeFrontFacesRasterizer;
    private readonly DepthStencilState _depthState;
    private readonly DepthStencilState _depthReadState;
    private readonly DepthStencilState _shadowDepthState;
    private readonly BlendState _opaqueBlendState;
    private readonly BlendState _translucentBlendState;
    private readonly BlendState _additiveBlendState;
    private readonly BlendState _depthOnlyBlendState;
    private readonly SamplerState _materialSampler;
    private readonly SamplerState _postSampler;
    private readonly SamplerState _shadowSampler;
    private readonly SampleDescription _sampleDescription;
    private readonly List<MeshBuffers> _meshes = [];
    private readonly Dictionary<string, HeadPreviewMaterial> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GpuTexture> _gpuTextures = new(StringComparer.OrdinalIgnoreCase);
    private int _width;
    private int _height;
    private Texture2D _renderTexture = null!;
    private RenderTargetView _renderTarget = null!;
    private Texture2D _normalTexture = null!;
    private RenderTargetView _normalTarget = null!;
    private Texture2D _ambientDiffuseTexture = null!;
    private RenderTargetView _ambientDiffuseTarget = null!;
    private Texture2D _aoNormalTexture = null!;
    private ShaderResourceView _aoNormalShaderView = null!;
    private Texture2D _depthTexture = null!;
    private DepthStencilView _depthView = null!;
    private ShaderResourceView _depthShaderView = null!;
    private Texture2D _aoDepthTexture = null!;
    private ShaderResourceView _aoDepthShaderView = null!;
    private Texture2D _resolvedTexture = null!;
    private ShaderResourceView _resolvedShaderView = null!;
    private Texture2D _resolvedAmbientTexture = null!;
    private ShaderResourceView _resolvedAmbientShaderView = null!;
    private Texture2D _postTexture = null!;
    private RenderTargetView _postTarget = null!;
    private Texture2D _stagingTexture = null!;
    private Texture2D _shadowTexture = null!;
    private DepthStencilView _shadowDepthView = null!;
    private ShaderResourceView _shadowShaderView = null!;
    private int _skinningPaletteCount;
    private System.Numerics.Matrix4x4[] _skinningPalette = [];
    private float _shadowRadius;
    private int _textureUploadCount;
    private long _gpuTextureBytes;
    private long _textureUseSerial;
    private bool _disposed;

    public HeadPreviewRenderer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _width = width;
        _height = height;
        try
        {
            _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            _deviceKind = "Hardware";
        }
        catch (SharpDXException)
        {
            _device = new Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);
            _deviceKind = "WARP";
        }
        _context = _device.ImmediateContext;
        var hdrSupport = _device.CheckFormatSupport(Format.R16G16B16A16_Float);
        var requiredHdrSupport = FormatSupport.RenderTarget | FormatSupport.Blendable | FormatSupport.ShaderSample;
        if ((hdrSupport & requiredHdrSupport) != requiredHdrSupport)
        {
            throw new NotSupportedException(
                "The selected D3D11 device cannot render, blend, and sample the required R16G16B16A16_FLOAT preview target.");
        }
        _sampleDescription = SelectMultisampleDescription();

        using var vertexBytecode = CompileShader("VSMain", "vs_5_0");
        using var pixelBytecode = CompileShader("PSMain", "ps_5_0");
        using var shadowPixelBytecode = CompileShader("PSShadow", "ps_5_0");
        using var postVertexBytecode = CompileShader("VSPostProcess", "vs_5_0");
        using var postPixelBytecode = CompileShader(
            _sampleDescription.Count > 1 ? "PSPostProcessMsaa" : "PSPostProcessSingle",
            "ps_5_0");
        _vertexShader = new VertexShader(_device, vertexBytecode);
        _pixelShader = new PixelShader(_device, pixelBytecode);
        _shadowPixelShader = new PixelShader(_device, shadowPixelBytecode);
        _postVertexShader = new VertexShader(_device, postVertexBytecode);
        _postPixelShader = new PixelShader(_device, postPixelBytecode);
        _inputLayout = new InputLayout(
            _device,
            ShaderSignature.GetInputSignature(vertexBytecode),
            [
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TANGENT", 0, Format.R32G32B32A32_Float, 24, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 40, 0),
                new InputElement("BLENDINDICES", 0, Format.R32G32B32A32_UInt, 48, 0),
                new InputElement("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 64, 0)
            ]);
        _sceneBuffer = CreateConstantBuffer<SceneConstants>();
        _materialBuffer = CreateConstantBuffer<HeadPreviewMaterialConstants>();
        _skinningBuffer = new Buffer(
            _device,
            MaximumBoneCount * Utilities.SizeOf<Matrix>(),
            ResourceUsage.Dynamic,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0);
        _meshBuffer = CreateConstantBuffer<MeshConstants>();
        _postProcessBuffer = CreateConstantBuffer<PostProcessConstants>();
        _shadowBuffer = CreateConstantBuffer<ShadowConstants>();
        _solidRasterizer = CreateRasterizer(FillMode.Solid, CullMode.None);
        _shadowRasterizer = CreateRasterizer(FillMode.Solid, CullMode.None);
        _wireframeRasterizer = CreateRasterizer(FillMode.Wireframe, CullMode.None);
        _solidBackFacesRasterizer = CreateRasterizer(FillMode.Solid, CullMode.Front);
        _solidFrontFacesRasterizer = CreateRasterizer(FillMode.Solid, CullMode.Back);
        _wireframeBackFacesRasterizer = CreateRasterizer(FillMode.Wireframe, CullMode.Front);
        _wireframeFrontFacesRasterizer = CreateRasterizer(FillMode.Wireframe, CullMode.Back);
        _depthState = new DepthStencilState(_device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.LessEqual,
            IsStencilEnabled = false
        });
        _depthReadState = new DepthStencilState(_device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.LessEqual,
            IsStencilEnabled = false
        });
        _shadowDepthState = new DepthStencilState(_device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.LessEqual,
            IsStencilEnabled = false
        });
        _opaqueBlendState = CreateBlendState(false);
        _translucentBlendState = CreateBlendState(true);
        _additiveBlendState = CreateAdditiveBlendState();
        _depthOnlyBlendState = CreateDepthOnlyBlendState();
        _materialSampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaximumAnisotropy = 8,
            ComparisonFunction = Comparison.Never,
            BorderColor = Color.Black,
            MinimumLod = 0,
            MaximumLod = float.MaxValue,
            MipLodBias = 0
        });
        _postSampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter = Filter.MinMagLinearMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaximumAnisotropy = 1,
            ComparisonFunction = Comparison.Never,
            BorderColor = Color.Black,
            MinimumLod = 0,
            MaximumLod = 0,
            MipLodBias = 0
        });
        _shadowSampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter = Filter.ComparisonMinMagMipPoint,
            AddressU = TextureAddressMode.Border,
            AddressV = TextureAddressMode.Border,
            AddressW = TextureAddressMode.Border,
            MaximumAnisotropy = 1,
            ComparisonFunction = Comparison.LessEqual,
            BorderColor = Color.White,
            MinimumLod = 0,
            MaximumLod = 0,
            MipLodBias = 0
        });
        CreateRenderTargets();
        CreateShadowMap();
    }

    public string DeviceKind => _deviceKind;
    public int DeviceCreationCount => 1;
    public int TextureUploadCount => _textureUploadCount;
    public int MultisampleCount => _sampleDescription.Count;
    public int MaterialAnisotropy => 8;
    public long GpuTextureBytes => _gpuTextureBytes;

    /// <summary>Releases face resources while retaining the device, shaders and render targets.</summary>
    public void ClearScene()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // D3D bindings retain references too: unbind before releasing the old face.
        _context.ClearState();
        DisposeMeshes();
        _shadowRadius = 0;
        _materials.Clear();
        DisposeMaterialTextures();
        _skinningPaletteCount = 0;
        _context.Flush();
    }

    public void SetScene(HeadPreviewScene scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        DisposeMeshes();
        _shadowRadius = 0;
        _materials.Clear();
        SetSkinningPalette(scene.SkinningPalette ?? []);
        foreach (var mesh in scene.Meshes)
        {
            if (mesh.Vertices.Count == 0 || mesh.Indices.Count == 0)
            {
                continue;
            }
            var vertices = mesh.Vertices.Select(ToGpuVertex).ToArray();
            _meshes.Add(new MeshBuffers(
                CreateDynamicVertexBuffer(vertices),
                Buffer.Create(_device, BindFlags.IndexBuffer, mesh.Indices.ToArray()),
                mesh.Sections,
                mesh.IsAttachment,
                mesh.ApplySkinning,
                mesh.Name,
                vertices.Length,
                mesh.Vertices.ToArray()));
            foreach (var material in mesh.Sections.Select(section => section.Material))
            {
                _materials[material.Key] = material;
            }
        }
        // Keep reusable textures from the previous scene only while the
        // bounded LRU has room; active resources are protected from eviction.
        EvictGpuTexturesToFit(0, string.Empty);
    }

    public void UpdateMaterials(IReadOnlyDictionary<string, HeadPreviewMaterial> materials)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(materials);
        foreach (var material in materials.Values)
        {
            _materials[material.Key] = material;
        }
        PruneMaterialTextures();
    }

    public void UpdateMeshVertices(string meshName, IReadOnlyList<HeadPreviewVertex> vertices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(meshName);
        ArgumentNullException.ThrowIfNull(vertices);
        var mesh = _meshes.SingleOrDefault(candidate => string.Equals(candidate.Name, meshName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Preview mesh '{meshName}' is not loaded.");
        if (vertices.Count != mesh.VertexCount)
        {
            throw new InvalidDataException($"Preview mesh '{meshName}' expects {mesh.VertexCount} vertices; received {vertices.Count}.");
        }
        var cachedVertices = vertices.ToArray();
        mesh.CachedVertices = cachedVertices;
        WriteDynamicVertices(mesh.VertexBuffer, cachedVertices.Select(ToGpuVertex).ToArray());
    }

    public unsafe void SetSkinningPalette(IReadOnlyList<System.Numerics.Matrix4x4> palette)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(palette);
        if (palette.Count > MaximumBoneCount)
        {
            throw new InvalidDataException($"Skinning palette contains {palette.Count} bones; the renderer supports {MaximumBoneCount}.");
        }
        var data = _context.MapSubresource(_skinningBuffer, 0, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            var destination = new Span<Matrix>((void*)data.DataPointer, MaximumBoneCount);
            destination.Fill(Matrix.Identity);
            for (var index = 0; index < palette.Count; index++)
            {
                destination[index] = ToDx(palette[index]);
            }
        }
        finally
        {
            _context.UnmapSubresource(_skinningBuffer, 0);
        }
        _skinningPaletteCount = palette.Count;
        _skinningPalette = palette.ToArray();
    }

    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_width == width && _height == height)
        {
            return;
        }
        _context.ClearState();
        DisposeRenderTargets();
        _width = width;
        _height = height;
        CreateRenderTargets();
    }

    public HeadPreviewFrame Render(
        HeadOrbitCamera camera,
        HeadPreviewOptions options,
        byte[]? destination = null,
        float? animationTimeSeconds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(options);
        var requiredLength = checked(_width * _height * 4);
        if (destination is not null && destination.Length != requiredLength)
        {
            throw new ArgumentException($"Destination must contain exactly {requiredLength} bytes.", nameof(destination));
        }
        if (animationTimeSeconds is { } explicitAnimationTime && !float.IsFinite(explicitAnimationTime))
        {
            throw new ArgumentOutOfRangeException(nameof(animationTimeSeconds), "Animation time must be finite.");
        }

        var frameAnimationTime = animationTimeSeconds ?? (float)_animationClock.Elapsed.TotalSeconds;
        var stopwatch = Stopwatch.StartNew();
        var world = Matrix.Identity;
        var view = camera.CreateViewMatrix();
        var projection = camera.CreateProjectionMatrix(_width / (float)_height);
        var cameraPosition = camera.Position;
        var lighting = HeadPreviewLightingPresets.Get(options.LightingPreset);
        var scene = new SceneConstants
        {
            WorldViewProjection = world * view * projection,
            World = world,
            View = view,
            CameraPosition = new Vector4(cameraPosition.X, cameraPosition.Y, cameraPosition.Z, 1),
            KeyLightDirection = ToDirection(lighting.Key, camera),
            KeyLightColor = ToColor(lighting.Key),
            FillLightDirection = ToDirection(lighting.Fill, camera),
            FillLightColor = ToColor(lighting.Fill),
            RimLightDirection = ToDirection(lighting.Rim, camera),
            RimLightColor = ToColor(lighting.Rim),
            AmbientColor = new Vector4(
                lighting.AmbientColor.X * lighting.AmbientIntensity,
                lighting.AmbientColor.Y * lighting.AmbientIntensity,
                lighting.AmbientColor.Z * lighting.AmbientIntensity,
                0),
            LightingParameters = new Vector4(
                1,
                lighting.KeyAngularRadius,
                lighting.AmbientFresnel,
                options.RenderMode == HeadPreviewRenderMode.Unlit ? 1 : 0),
            SkyColor = new Vector4(lighting.AmbientColor.X, lighting.AmbientColor.Y, lighting.AmbientColor.Z, 0),
            GroundColor = new Vector4(lighting.GroundColor.X, lighting.GroundColor.Y, lighting.GroundColor.Z, 0),
            HorizonColor = new Vector4(lighting.HorizonColor.X, lighting.HorizonColor.Y, lighting.HorizonColor.Z, 0),
            EnvironmentParameters = new Vector4(lighting.AmbientIntensity, 0, 0, 0)
        };
        var draws = _meshes
            .Where(mesh => !mesh.IsAttachment || options.ShowAttachment)
            .SelectMany(mesh => mesh.Sections
                .Where(section => section.IndexCount > 0)
                .Select(section => (
                    Mesh: mesh,
                    Section: section,
                    Material: _materials.GetValueOrDefault(section.Material.Key, section.Material))))
            .ToArray();
        var shadowDrawCalls = 0;
        var shadowsEnabled = options.RenderMode == HeadPreviewRenderMode.Shaded && options.ShadowsEnabled;
        if (shadowsEnabled)
        {
            shadowDrawCalls = RenderShadowMap(draws, ref scene, camera, lighting, options, frameAnimationTime);
        }
        else
        {
            SetShadowConstants(Matrix.Identity, enabled: false, options);
            _context.PixelShader.SetShaderResources(17, new ShaderResourceView?[] { null });
        }

        ConfigurePipeline(options);
        _context.UpdateSubresource(ref scene, _sceneBuffer);
        _context.PixelShader.SetShaderResources(17, shadowsEnabled ? _shadowShaderView : null);
        var drawCalls = 0;
        foreach (var draw in draws.Where(draw => !IsTranslucent(draw.Material, options.RenderMode)))
        {
            DrawSection(draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe, frameAnimationTime);
            drawCalls++;
        }
        CaptureAmbientOcclusionInputs();
        var translucentDraws = draws
            .Where(draw => IsTranslucent(draw.Material, options.RenderMode))
            .ToArray();
        var hairDraws = translucentDraws
            .Where(draw => draw.Material.Family == HeadMaterialFamily.Hair)
            .ToArray();
        if (options.RenderMode != HeadPreviewRenderMode.Diagnostic)
        {
            // bUseLitTranslucencyDepthPass uses the exported ScreenDoor shader,
            // which accepts only fully opaque HAIR_Diff texels. A lower cutoff
            // blocks the rear shells while the front shell still blends with
            // the scalp, making dense styles look transparent.
            foreach (var draw in hairDraws)
            {
                DrawSection(
                    draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe,
                    frameAnimationTime,
                    depthThreshold: 1f);
                drawCalls++;
            }
        }
        foreach (var draw in translucentDraws.Where(draw => draw.Material.BlendMode != HeadMaterialBlendMode.Additive))
        {
            if (draw.Material.Family == HeadMaterialFamily.Hair)
            {
                // TwoSidedSeparatePass renders back-facing cards first, then
                // front-facing cards. Cull-none in one draw retains index order
                // and is visibly wrong on layered shell hairstyles.
                DrawSection(
                    draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe,
                    frameAnimationTime,
                    facePass: HairFacePass.BackFaces);
                DrawSection(
                    draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe,
                    frameAnimationTime,
                    facePass: HairFacePass.FrontFaces);
                drawCalls += 2;
            }
            else
            {
                DrawSection(draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe, frameAnimationTime);
                drawCalls++;
            }
        }
        // Keep additive surfaces in their own pass so their One+One blend and
        // depth-read state remain separate from ordinary alpha translucency.
        foreach (var draw in translucentDraws
                     .Where(draw => draw.Material.BlendMode == HeadMaterialBlendMode.Additive))
        {
            DrawSection(draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe, frameAnimationTime);
            drawCalls++;
        }
        if (options.RenderMode != HeadPreviewRenderMode.Diagnostic)
        {
            // bUseLitTranslucencyPostRenderDepthPass writes depth after colour
            // for every non-zero 8-bit alpha texel in BioWare's shader.
            foreach (var draw in hairDraws)
            {
                DrawSection(
                    draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe,
                    frameAnimationTime,
                    depthThreshold: 1f / 255f);
                drawCalls++;
            }
        }

        _context.PixelShader.SetShaderResources(0, new ShaderResourceView?[10]);
        ApplyPostProcess(
            lighting,
            camera,
            options.AmbientOcclusionEnabled && options.RenderMode != HeadPreviewRenderMode.Unlit,
            options.RenderMode,
            options.BackgroundColor);

        var pixels = ReadTexture(destination);
        stopwatch.Stop();
        return new HeadPreviewFrame(_width, _height, pixels, stopwatch.Elapsed, drawCalls, _deviceKind)
        {
            ShadowDrawCalls = shadowDrawCalls
        };
    }

    private int RenderShadowMap(
        IReadOnlyList<(MeshBuffers Mesh, HeadPreviewSection Section, HeadPreviewMaterial Material)> draws,
        ref SceneConstants scene,
        HeadOrbitCamera camera,
        HeadPreviewLightingRig lighting,
        HeadPreviewOptions options,
        float animationTimeSeconds)
    {
        var casters = draws.Where(IsShadowCaster).ToArray();
        if (casters.Length == 0)
        {
            SetShadowConstants(Matrix.Identity, enabled: false, options);
            _context.PixelShader.SetShaderResources(17, new ShaderResourceView?[] { null });
            return 0;
        }

        var lightDirection4 = ToDirection(lighting.Key, camera);
        var lightDirection = new System.Numerics.Vector3(lightDirection4.X, lightDirection4.Y, lightDirection4.Z);
        if (lightDirection.LengthSquared() < 1e-8f)
        {
            SetShadowConstants(Matrix.Identity, enabled: false, options);
            _context.PixelShader.SetShaderResources(17, new ShaderResourceView?[] { null });
            return 0;
        }

        var center = ComputeShadowCenter(casters);
        var radius = ComputeShadowRadius(casters, center);
        // Grow only when a pose extends beyond the current fit. That keeps a
        // stable texel scale during ordinary facial animation and morph edits.
        _shadowRadius = Math.Max(_shadowRadius, radius * 1.12f);
        radius = Math.Max(_shadowRadius, 0.01f);
        var texelSize = (2 * radius) / ShadowMapSize;
        var forward = System.Numerics.Vector3.Normalize(-lightDirection);
        var referenceUp = Math.Abs(System.Numerics.Vector3.Dot(forward, System.Numerics.Vector3.UnitY)) > 0.98f
            ? System.Numerics.Vector3.UnitZ
            : System.Numerics.Vector3.UnitY;
        var right = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(forward, referenceUp));
        var up = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(right, forward));
        var rightOffset = MathF.Round(System.Numerics.Vector3.Dot(center, right) / texelSize) * texelSize
                          - System.Numerics.Vector3.Dot(center, right);
        var upOffset = MathF.Round(System.Numerics.Vector3.Dot(center, up) / texelSize) * texelSize
                       - System.Numerics.Vector3.Dot(center, up);
        center += right * rightOffset + up * upOffset;

        var eye = center - forward * (radius * 2.5f);
        var view = Matrix.LookAtRH(ToDx(eye), ToDx(center), ToDx(up));
        var projection = Matrix.OrthoOffCenterRH(-radius, radius, -radius, radius, 0.01f, radius * 5f);
        var lightViewProjection = view * projection;
        scene.WorldViewProjection = lightViewProjection;
        _context.UpdateSubresource(ref scene, _sceneBuffer);
        SetShadowConstants(lightViewProjection, enabled: false, options);

        // t17 aliases the depth resource used as the DSV below. Explicitly
        // unbind it first to avoid D3D11's read/write resource hazard.
        _context.PixelShader.SetShaderResources(17, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets(_shadowDepthView);
        _context.ClearDepthStencilView(_shadowDepthView, DepthStencilClearFlags.Depth, 1, 0);
        _context.Rasterizer.SetViewport(0, 0, ShadowMapSize, ShadowMapSize);
        _context.Rasterizer.State = _shadowRasterizer;
        _context.OutputMerger.SetDepthStencilState(_shadowDepthState);
        _context.OutputMerger.SetBlendState(_depthOnlyBlendState);
        _context.InputAssembler.InputLayout = _inputLayout;
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _context.VertexShader.Set(_vertexShader);
        _context.VertexShader.SetConstantBuffer(0, _sceneBuffer);
        _context.VertexShader.SetConstantBuffer(2, _skinningBuffer);
        _context.VertexShader.SetConstantBuffer(3, _meshBuffer);
        _context.PixelShader.Set(_shadowPixelShader);
        _context.PixelShader.SetConstantBuffer(0, _sceneBuffer);
        _context.PixelShader.SetConstantBuffer(1, _materialBuffer);
        _context.PixelShader.SetConstantBuffer(5, _shadowBuffer);
        _context.PixelShader.SetSampler(0, _materialSampler);

        foreach (var draw in casters)
        {
            DrawShadowSection(draw.Mesh, draw.Section, draw.Material, animationTimeSeconds);
        }

        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
        scene.WorldViewProjection = Matrix.Identity * camera.CreateViewMatrix() * camera.CreateProjectionMatrix(_width / (float)_height);
        _context.UpdateSubresource(ref scene, _sceneBuffer);
        SetShadowConstants(lightViewProjection, enabled: true, options);
        return casters.Length;
    }

    private void DrawShadowSection(
        MeshBuffers mesh,
        HeadPreviewSection section,
        HeadPreviewMaterial definition,
        float animationTimeSeconds)
    {
        _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(mesh.VertexBuffer, Marshal.SizeOf<GpuVertex>(), 0));
        _context.InputAssembler.SetIndexBuffer(mesh.IndexBuffer, Format.R32_UInt, 0);
        var meshConstants = new MeshConstants
        {
            SkinningParameters = new Vector4(mesh.ApplySkinning && _skinningPaletteCount > 0 ? 1 : 0, _skinningPaletteCount, 0, 0)
        };
        _context.UpdateSubresource(ref meshConstants, _meshBuffer);
        var bindings = HeadPreviewMaterialBindings.Create(definition);
        var material = HeadPreviewMaterialConstants.Create(definition, HeadPreviewRenderMode.Shaded, bindings);
        if (definition.PreviewModel == HumanMaterialPreviewModel.VisorLens)
        {
            material.SecondaryColor = new Vector4(0, PeriodicPhase(animationTimeSeconds * 0.2f), 0, 0);
            material.TertiaryColor = new Vector4(0, PeriodicPhase(animationTimeSeconds * -0.01f), 0, 0);
            material.QuaternaryColor = new Vector4(0, PeriodicPhase(animationTimeSeconds * -0.05f), 0, 0);
        }
        _context.UpdateSubresource(ref material, _materialBuffer);
        BindMaterial(definition, bindings, HeadPreviewRenderMode.Shaded);
        // BindMaterial selects camera-view blend/depth states; the shadow pass
        // always writes opaque depth after PSShadow has clipped coverage.
        _context.OutputMerger.SetBlendState(_depthOnlyBlendState);
        _context.OutputMerger.SetDepthStencilState(_shadowDepthState);
        _context.Rasterizer.State = _shadowRasterizer;
        _context.DrawIndexed(section.IndexCount, section.BaseIndex, 0);
    }

    private static bool IsShadowCaster((MeshBuffers Mesh, HeadPreviewSection Section, HeadPreviewMaterial Material) draw) =>
        draw.Material.PreviewModel != HumanMaterialPreviewModel.VisorLens &&
        (draw.Material.BlendMode is HeadMaterialBlendMode.Opaque or HeadMaterialBlendMode.Masked ||
         draw.Material.BlendMode == HeadMaterialBlendMode.Translucent && draw.Material.Family == HeadMaterialFamily.Hair);

    private System.Numerics.Vector3 ComputeShadowCenter(
        IReadOnlyList<(MeshBuffers Mesh, HeadPreviewSection Section, HeadPreviewMaterial Material)> draws)
    {
        var min = new System.Numerics.Vector3(float.PositiveInfinity);
        var max = new System.Numerics.Vector3(float.NegativeInfinity);
        foreach (var mesh in draws.Select(draw => draw.Mesh).Distinct())
        foreach (var vertex in mesh.CachedVertices)
        {
            var position = GetPosedPosition(mesh, vertex);
            min = System.Numerics.Vector3.Min(min, position);
            max = System.Numerics.Vector3.Max(max, position);
        }
        return IsFinite(min) && IsFinite(max)
            ? (min + max) * 0.5f
            : System.Numerics.Vector3.Zero;
    }

    private float ComputeShadowRadius(
        IReadOnlyList<(MeshBuffers Mesh, HeadPreviewSection Section, HeadPreviewMaterial Material)> draws,
        System.Numerics.Vector3 center)
    {
        var radiusSquared = 0f;
        foreach (var mesh in draws.Select(draw => draw.Mesh).Distinct())
        foreach (var vertex in mesh.CachedVertices)
        {
            radiusSquared = Math.Max(radiusSquared, System.Numerics.Vector3.DistanceSquared(GetPosedPosition(mesh, vertex), center));
        }
        return Math.Max(MathF.Sqrt(radiusSquared), 0.01f);
    }

    private System.Numerics.Vector3 GetPosedPosition(MeshBuffers mesh, HeadPreviewVertex vertex)
    {
        if (!mesh.ApplySkinning || _skinningPaletteCount == 0)
        {
            return vertex.Position;
        }
        Span<uint> indices = stackalloc uint[] { vertex.BoneIndex0, vertex.BoneIndex1, vertex.BoneIndex2, vertex.BoneIndex3 };
        Span<float> weights = stackalloc float[] { vertex.BoneWeights.X, vertex.BoneWeights.Y, vertex.BoneWeights.Z, vertex.BoneWeights.W };
        if (weights[0] + weights[1] + weights[2] + weights[3] <= 0.000001f)
        {
            weights[0] = 1;
        }
        var result = System.Numerics.Vector3.Zero;
        var totalWeight = 0f;
        for (var influence = 0; influence < 4; influence++)
        {
            var weight = weights[influence];
            if (weight <= 0) continue;
            var boneIndex = (int)indices[influence];
            // The GPU palette is identity-filled past its active entries.
            // Preserve that behavior for valid, unused slots here too.
            result += boneIndex >= 0 && boneIndex < _skinningPalette.Length
                ? System.Numerics.Vector3.Transform(vertex.Position, _skinningPalette[boneIndex]) * weight
                : vertex.Position * weight;
            totalWeight += weight;
        }
        return totalWeight > 0.000001f ? result / totalWeight : vertex.Position;
    }

    private void SetShadowConstants(Matrix lightViewProjection, bool enabled, HeadPreviewOptions options)
    {
        var parameters = new ShadowConstants
        {
            LightViewProjection = lightViewProjection,
            ShadowParameters = new Vector4(
                enabled ? 1 : 0,
                1f / ShadowMapSize,
                Math.Clamp(options.ShadowDepthBias, 0, 0.01f),
                Math.Clamp(options.ShadowNormalBias, 0, 0.1f) * Math.Max(_shadowRadius, 0.01f))
        };
        _context.UpdateSubresource(ref parameters, _shadowBuffer);
        _context.PixelShader.SetConstantBuffer(5, _shadowBuffer);
        _context.PixelShader.SetSampler(2, _shadowSampler);
    }

    private void DrawSection(
        MeshBuffers mesh,
        HeadPreviewSection section,
        HeadPreviewMaterial definition,
        HeadPreviewRenderMode renderMode,
        bool wireframe,
        float animationTimeSeconds,
        float? depthThreshold = null,
        HairFacePass facePass = HairFacePass.TwoSided)
    {
        _context.Rasterizer.State = SelectRasterizer(wireframe, facePass);
        _context.InputAssembler.SetVertexBuffers(
            0,
            new VertexBufferBinding(mesh.VertexBuffer, Marshal.SizeOf<GpuVertex>(), 0));
        _context.InputAssembler.SetIndexBuffer(mesh.IndexBuffer, Format.R32_UInt, 0);
        var meshConstants = new MeshConstants
        {
            SkinningParameters = new Vector4(
                mesh.ApplySkinning && _skinningPaletteCount > 0 ? 1 : 0,
                _skinningPaletteCount,
                0,
                0)
        };
        _context.UpdateSubresource(ref meshConstants, _meshBuffer);
        var bindings = HeadPreviewMaterialBindings.Create(definition);
        var material = HeadPreviewMaterialConstants.Create(definition, renderMode, bindings);
        if (definition.PreviewModel == HumanMaterialPreviewModel.VisorLens)
        {
            material.SecondaryColor = new Vector4(0, PeriodicPhase(animationTimeSeconds * 0.2f), 0, 0);
            material.TertiaryColor = new Vector4(0, PeriodicPhase(animationTimeSeconds * -0.01f), 0, 0);
            material.QuaternaryColor = new Vector4(0, PeriodicPhase(animationTimeSeconds * -0.05f), 0, 0);
        }
        if (depthThreshold is not null)
        {
            material.SurfaceParameters.X = -depthThreshold.Value;
        }
        _context.UpdateSubresource(ref material, _materialBuffer);
        BindMaterial(definition, bindings, renderMode);
        if (depthThreshold is not null)
        {
            _context.OutputMerger.SetBlendState(_depthOnlyBlendState);
            _context.OutputMerger.SetDepthStencilState(_depthState);
        }
        _context.DrawIndexed(section.IndexCount, section.BaseIndex, 0);
    }

    private static bool IsTranslucent(HeadPreviewMaterial material, HeadPreviewRenderMode renderMode) =>
        renderMode != HeadPreviewRenderMode.Diagnostic &&
        material.BlendMode is HeadMaterialBlendMode.Translucent or HeadMaterialBlendMode.Additive;

    private static float PeriodicPhase(float value) => value - MathF.Floor(value);

    private void CaptureAmbientOcclusionInputs()
    {
        // UE3 evaluates screen-space AO from the opaque scene before lit
        // translucency. Snapshotting here keeps hair/lash cards out of both the
        // occluder depth and smooth-normal buffers while preserving opaque hats.
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
        _context.CopyResource(_depthTexture, _aoDepthTexture);
        _context.CopyResource(_normalTexture, _aoNormalTexture);
        _context.OutputMerger.SetTargets(_depthView, _renderTarget, _normalTarget, _ambientDiffuseTarget);
    }

    private void ConfigurePipeline(HeadPreviewOptions options)
    {
        var background = options.BackgroundColor ?? new System.Numerics.Vector3(0.035f, 0.043f, 0.055f);
        var linearBackground = new System.Numerics.Vector3(
            ToHdrFloat(SrgbToLinear(background.X)),
            ToHdrFloat(SrgbToLinear(background.Y)),
            ToHdrFloat(SrgbToLinear(background.Z)));
        _context.OutputMerger.SetTargets(_depthView, _renderTarget, _normalTarget, _ambientDiffuseTarget);
        _context.OutputMerger.SetDepthStencilState(_depthState);
        _context.Rasterizer.SetViewport(0, 0, _width, _height);
        _context.Rasterizer.State = options.Wireframe ? _wireframeRasterizer : _solidRasterizer;
        _context.ClearRenderTargetView(_renderTarget, new Color4(
            linearBackground.X,
            linearBackground.Y,
            linearBackground.Z,
            1));
        _context.ClearRenderTargetView(_normalTarget, new Color4(0.5f, 0.5f, 1, 1));
        _context.ClearRenderTargetView(_ambientDiffuseTarget, new Color4(0, 0, 0, 0));
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1, 0);
        _context.InputAssembler.InputLayout = _inputLayout;
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _context.VertexShader.Set(_vertexShader);
        _context.VertexShader.SetConstantBuffer(0, _sceneBuffer);
        _context.VertexShader.SetConstantBuffer(2, _skinningBuffer);
        _context.VertexShader.SetConstantBuffer(3, _meshBuffer);
        _context.PixelShader.Set(_pixelShader);
        _context.PixelShader.SetConstantBuffer(0, _sceneBuffer);
        _context.PixelShader.SetConstantBuffer(1, _materialBuffer);
        _context.PixelShader.SetConstantBuffer(5, _shadowBuffer);
        _context.PixelShader.SetSampler(0, _materialSampler);
        _context.PixelShader.SetSampler(2, _shadowSampler);
    }

    private static Vector4 ToDirection(HeadPreviewDirectionalLight light, HeadOrbitCamera camera)
    {
        var direction = camera.TransformCameraDirectionToWorld(light.Direction);
        return new Vector4(direction.X, direction.Y, direction.Z, 0);
    }

    private static Vector4 ToColor(HeadPreviewDirectionalLight light) =>
        new(light.Color.X * light.Intensity, light.Color.Y * light.Intensity, light.Color.Z * light.Intensity, 0);

    private void BindMaterial(
        HeadPreviewMaterial material,
        HeadPreviewMaterialBindings bindings,
        HeadPreviewRenderMode renderMode)
    {
        var translucent = renderMode != HeadPreviewRenderMode.Diagnostic &&
                          material.BlendMode == HeadMaterialBlendMode.Translucent;
        var additive = renderMode != HeadPreviewRenderMode.Diagnostic &&
                       material.BlendMode == HeadMaterialBlendMode.Additive;
        _context.OutputMerger.SetBlendState(additive ? _additiveBlendState : translucent ? _translucentBlendState : _opaqueBlendState);
        _context.OutputMerger.SetDepthStencilState(translucent || additive ? _depthReadState : _depthState);

        _context.PixelShader.SetShaderResources(
            0,
            ToView(bindings.Diffuse),
            ToView(bindings.Normal),
            ToView(bindings.Mask),
            ToView(bindings.Detail),
            ToView(bindings.Auxiliary1),
            ToView(bindings.Auxiliary2),
            ToView(bindings.Auxiliary3),
            ToView(bindings.Auxiliary4),
            ToCubeView(material.FixedCubeTexture),
            ToCubeView(material.SecondaryFixedCubeTexture));
        _context.PixelShader.SetShaderResources(
            15,
            ToView(bindings.Auxiliary5),
            ToView(bindings.Auxiliary6));
    }

    private ShaderResourceView? ToCubeView(HeadPreviewCubeTexture? texture)
    {
        if (texture is null)
        {
            return null;
        }
        if (_gpuTextures.TryGetValue(texture.Key, out var existing))
        {
            existing.LastUsed = ++_textureUseSerial;
            return existing.GetView(_device, texture.ColorSpace);
        }
        if (texture.Rgba8Faces.Count != 6)
        {
            throw new InvalidDataException($"Cube texture '{texture.Key}' must contain exactly six faces.");
        }

        var authoredMips = GetValidCubeMips(texture);
        var streams = new List<DataStream>(authoredMips.Count * 6);
        try
        {
            var rectangles = new List<DataRectangle>(authoredMips.Count * 6);
            for (var faceIndex = 0; faceIndex < 6; faceIndex++)
            {
                foreach (var mip in authoredMips)
                {
                    var pixels = mip.Rgba8Faces[faceIndex];
                    var stream = new DataStream(pixels.Length, true, true);
                    stream.Write(pixels, 0, pixels.Length);
                    stream.Position = 0;
                    streams.Add(stream);
                    rectangles.Add(new DataRectangle(stream.DataPointer, checked(mip.Size * 4)));
                }
            }
            var resource = new Texture2D(_device, new Texture2DDescription
            {
                Width = texture.Size,
                Height = texture.Size,
                MipLevels = authoredMips.Count,
                ArraySize = 6,
                Format = Format.R8G8B8A8_Typeless,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.TextureCube
            }, rectangles.ToArray());
            var byteCount = authoredMips.Sum(mip => mip.Rgba8Faces.Sum(face => (long)face.Length));
            EvictGpuTexturesToFit(byteCount, texture.Key);
            var gpuTexture = new GpuTexture(resource, byteCount, ++_textureUseSerial);
            var view = gpuTexture.GetView(_device, texture.ColorSpace);
            _gpuTextures[texture.Key] = gpuTexture;
            _gpuTextureBytes += byteCount;
            _textureUploadCount++;
            return view;
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    private ShaderResourceView? ToView(HeadPreviewTexture? texture)
    {
        if (texture is null)
        {
            return null;
        }
        if (_gpuTextures.TryGetValue(texture.Key, out var existing))
        {
            existing.LastUsed = ++_textureUseSerial;
            return existing.GetView(_device, texture.ColorSpace);
        }

        var authoredMips = GetValidMips(texture);
        var streams = new List<DataStream>(authoredMips.Count);
        try
        {
            var rectangles = authoredMips.Select(mip =>
            {
                var stream = new DataStream(mip.Rgba8.Length, true, true);
                stream.Write(mip.Rgba8, 0, mip.Rgba8.Length);
                stream.Position = 0;
                streams.Add(stream);
                return new DataRectangle(stream.DataPointer, checked(mip.Width * 4));
            }).ToArray();
            var resource = new Texture2D(_device, new Texture2DDescription
            {
                Width = texture.Width,
                Height = texture.Height,
                MipLevels = authoredMips.Count,
                ArraySize = 1,
                Format = Format.R8G8B8A8_Typeless,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            }, rectangles);
            var byteCount = authoredMips.Sum(mip => (long)mip.Rgba8.Length);
            EvictGpuTexturesToFit(byteCount, texture.Key);
            var gpuTexture = new GpuTexture(resource, byteCount, ++_textureUseSerial);
            var view = gpuTexture.GetView(_device, texture.ColorSpace);
            _gpuTextures[texture.Key] = gpuTexture;
            _gpuTextureBytes += byteCount;
            _textureUploadCount++;
            return view;
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    private static IReadOnlyList<DecodedTextureMip> GetValidMips(HeadPreviewTexture texture)
    {
        var candidates = texture.Mips is { Count: > 0 }
            ? texture.Mips
            : [new DecodedTextureMip(texture.Width, texture.Height, texture.Rgba8)];
        var valid = new List<DecodedTextureMip>(candidates.Count);
        for (var level = 0; level < candidates.Count; level++)
        {
            var mip = candidates[level];
            if (mip.Width != Math.Max(1, texture.Width >> level) ||
                mip.Height != Math.Max(1, texture.Height >> level) ||
                mip.Rgba8.Length != checked(mip.Width * mip.Height * 4))
            {
                break;
            }
            valid.Add(mip);
        }
        return valid.Count > 0 ? valid : [new DecodedTextureMip(texture.Width, texture.Height, texture.Rgba8)];
    }

    private static IReadOnlyList<DecodedTextureCubeMip> GetValidCubeMips(HeadPreviewCubeTexture texture)
    {
        var candidates = texture.Mips is { Count: > 0 }
            ? texture.Mips
            : [new DecodedTextureCubeMip(texture.Size, texture.Rgba8Faces)];
        var valid = new List<DecodedTextureCubeMip>(candidates.Count);
        for (var level = 0; level < candidates.Count; level++)
        {
            var mip = candidates[level];
            var expectedSize = Math.Max(1, texture.Size >> level);
            if (mip.Size != expectedSize || mip.Rgba8Faces.Count != 6 ||
                mip.Rgba8Faces.Any(face => face.Length != checked(expectedSize * expectedSize * 4)))
            {
                break;
            }
            valid.Add(mip);
        }
        return valid.Count > 0 ? valid : [new DecodedTextureCubeMip(texture.Size, texture.Rgba8Faces)];
    }

    private BlendState CreateBlendState(bool translucent)
    {
        var description = new BlendStateDescription();
        for (var targetIndex = 0; targetIndex < 3; targetIndex++)
        {
            description.RenderTarget[targetIndex] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = translucent,
                SourceBlend = BlendOption.SourceAlpha,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
                AlphaBlendOperation = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteMaskFlags.All
            };
        }
        return new BlendState(_device, description);
    }

    private BlendState CreateAdditiveBlendState()
    {
        var description = new BlendStateDescription();
        for (var targetIndex = 0; targetIndex < 3; targetIndex++)
        {
            description.RenderTarget[targetIndex] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.One,
                BlendOperation = BlendOperation.Add,
                SourceAlphaBlend = BlendOption.Zero,
                DestinationAlphaBlend = BlendOption.One,
                AlphaBlendOperation = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteMaskFlags.All
            };
        }
        return new BlendState(_device, description);
    }

    private BlendState CreateDepthOnlyBlendState()
    {
        var description = new BlendStateDescription();
        description.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = false,
            RenderTargetWriteMask = 0
        };
        return new BlendState(_device, description);
    }

    private void ApplyPostProcess(
        HeadPreviewLightingRig lighting,
        HeadOrbitCamera camera,
        bool ambientOcclusionEnabled,
        HeadPreviewRenderMode renderMode,
        System.Numerics.Vector3? backgroundColor)
    {
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
        if (_sampleDescription.Count > 1)
        {
            _context.ResolveSubresource(_renderTexture, 0, _resolvedTexture, 0, Format.R16G16B16A16_Float);
            _context.ResolveSubresource(_ambientDiffuseTexture, 0, _resolvedAmbientTexture, 0, Format.R16G16B16A16_Float);
        }
        else
        {
            _context.CopyResource(_renderTexture, _resolvedTexture);
            _context.CopyResource(_ambientDiffuseTexture, _resolvedAmbientTexture);
        }
        var background = backgroundColor ?? new System.Numerics.Vector3(0.035f, 0.043f, 0.055f);
        var constants = new PostProcessConstants
        {
            InverseProjection = Matrix.Invert(camera.CreateProjectionMatrix(_width / (float)_height)),
            PixelSizeAndSamples = new Vector4(
                1f / _width,
                1f / _height,
                _sampleDescription.Count,
                1.50f),
            DepthParameters = new Vector4(
                0.100f,
                0.25f,
                ambientOcclusionEnabled
                    ? lighting.AmbientOcclusionStrength * 1.20f
                    : 0,
                1.65f),
            BloomParameters = new Vector4(
                lighting.BloomThreshold,
                lighting.BloomIntensity,
                0,
                0),
            DisplayParameters = new Vector4(
                lighting.Exposure,
                renderMode != HeadPreviewRenderMode.Unlit ? 1 : 0,
                0,
                0),
            DisplayBackground = new Vector4(
                ToHdrFloat(SrgbToLinear(background.X)),
                ToHdrFloat(SrgbToLinear(background.Y)),
                ToHdrFloat(SrgbToLinear(background.Z)),
                0)
        };
        _context.UpdateSubresource(ref constants, _postProcessBuffer);

        _context.OutputMerger.SetTargets((DepthStencilView?)null, _postTarget);
        _context.OutputMerger.SetDepthStencilState(null);
        _context.OutputMerger.SetBlendState(_opaqueBlendState);
        _context.Rasterizer.SetViewport(0, 0, _width, _height);
        _context.Rasterizer.State = _solidRasterizer;
        _context.InputAssembler.InputLayout = null;
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _context.VertexShader.Set(_postVertexShader);
        _context.PixelShader.Set(_postPixelShader);
        _context.PixelShader.SetConstantBuffer(4, _postProcessBuffer);
        _context.PixelShader.SetSampler(1, _postSampler);
        if (_sampleDescription.Count > 1)
        {
            _context.PixelShader.SetShaderResources(
                10, _resolvedShaderView, _aoDepthShaderView, null, _aoNormalShaderView, null);
        }
        else
        {
            _context.PixelShader.SetShaderResources(
                10, _resolvedShaderView, null, _aoDepthShaderView, null, _aoNormalShaderView);
        }
        _context.PixelShader.SetShaderResources(18, _resolvedAmbientShaderView);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(10, new ShaderResourceView?[5]);
        _context.PixelShader.SetShaderResources(18, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
    }

    private static float ClampFinite(float value, float fallback, float minimum, float maximum) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static float SrgbToLinear(float value)
    {
        var channel = Math.Clamp(value, 0, 1);
        return channel <= 0.04045f
            ? channel / 12.92f
            : MathF.Pow((channel + 0.055f) / 1.055f, 2.4f);
    }

    private static float ToHdrFloat(float value) => (float)(System.Half)value;

    private void CreateShadowMap()
    {
        _shadowTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = ShadowMapSize,
            Height = ShadowMapSize,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_Typeless,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        });
        _shadowDepthView = new DepthStencilView(_device, _shadowTexture, new DepthStencilViewDescription
        {
            Format = Format.D32_Float,
            Dimension = DepthStencilViewDimension.Texture2D,
            Flags = DepthStencilViewFlags.None,
            Texture2D = new DepthStencilViewDescription.Texture2DResource { MipSlice = 0 }
        });
        _shadowShaderView = new ShaderResourceView(_device, _shadowTexture, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float,
            Dimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new ShaderResourceViewDescription.Texture2DResource { MostDetailedMip = 0, MipLevels = 1 }
        });
    }

    private void CreateRenderTargets()
    {
        _renderTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = _sampleDescription,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget
        });
        _renderTarget = new RenderTargetView(_device, _renderTexture);
        _normalTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = _sampleDescription,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget
        });
        _normalTarget = new RenderTargetView(_device, _normalTexture);
        _ambientDiffuseTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = _sampleDescription,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget
        });
        _ambientDiffuseTarget = new RenderTargetView(_device, _ambientDiffuseTexture);
        _aoNormalTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = _sampleDescription,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });
        _aoNormalShaderView = new ShaderResourceView(_device, _aoNormalTexture);
        _depthTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_Typeless,
            SampleDescription = _sampleDescription,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource
        });
        _depthView = new DepthStencilView(_device, _depthTexture, new DepthStencilViewDescription
        {
            Format = Format.D32_Float,
            Dimension = _sampleDescription.Count > 1
                ? DepthStencilViewDimension.Texture2DMultisampled
                : DepthStencilViewDimension.Texture2D,
            Texture2D = new DepthStencilViewDescription.Texture2DResource { MipSlice = 0 }
        });
        _depthShaderView = new ShaderResourceView(_device, _depthTexture, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float,
            Dimension = _sampleDescription.Count > 1
                ? ShaderResourceViewDimension.Texture2DMultisampled
                : ShaderResourceViewDimension.Texture2D,
            Texture2D = new ShaderResourceViewDescription.Texture2DResource
            {
                MostDetailedMip = 0,
                MipLevels = 1
            }
        });
        _aoDepthTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_Typeless,
            SampleDescription = _sampleDescription,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });
        _aoDepthShaderView = new ShaderResourceView(_device, _aoDepthTexture, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float,
            Dimension = _sampleDescription.Count > 1
                ? ShaderResourceViewDimension.Texture2DMultisampled
                : ShaderResourceViewDimension.Texture2D,
            Texture2D = new ShaderResourceViewDescription.Texture2DResource
            {
                MostDetailedMip = 0,
                MipLevels = 1
            }
        });
        _resolvedTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });
        _resolvedShaderView = new ShaderResourceView(_device, _resolvedTexture);
        _resolvedAmbientTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });
        _resolvedAmbientShaderView = new ShaderResourceView(_device, _resolvedAmbientTexture);
        _postTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget
        });
        _postTarget = new RenderTargetView(_device, _postTexture);
        _stagingTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read
        });
    }

    private byte[] ReadTexture(byte[]? destination)
    {
        _context.CopyResource(_postTexture, _stagingTexture);
        var data = _context.MapSubresource(_stagingTexture, 0, MapMode.Read, MapFlags.None);
        try
        {
            var pixels = destination ?? new byte[checked(_width * _height * 4)];
            for (var row = 0; row < _height; row++)
            {
                Marshal.Copy(IntPtr.Add(data.DataPointer, row * data.RowPitch), pixels, row * _width * 4, _width * 4);
            }
            return pixels;
        }
        finally
        {
            _context.UnmapSubresource(_stagingTexture, 0);
        }
    }

    private Buffer CreateConstantBuffer<T>() where T : struct => new(
        _device,
        Utilities.SizeOf<T>(),
        ResourceUsage.Default,
        BindFlags.ConstantBuffer,
        CpuAccessFlags.None,
        ResourceOptionFlags.None,
        0);

    private Buffer CreateDynamicVertexBuffer(GpuVertex[] vertices)
    {
        var buffer = new Buffer(
            _device,
            checked(vertices.Length * Marshal.SizeOf<GpuVertex>()),
            ResourceUsage.Dynamic,
            BindFlags.VertexBuffer,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            Marshal.SizeOf<GpuVertex>());
        WriteDynamicVertices(buffer, vertices);
        return buffer;
    }

    private unsafe void WriteDynamicVertices(Buffer buffer, GpuVertex[] vertices)
    {
        var data = _context.MapSubresource(buffer, 0, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            vertices.AsSpan().CopyTo(new Span<GpuVertex>((void*)data.DataPointer, vertices.Length));
        }
        finally
        {
            _context.UnmapSubresource(buffer, 0);
        }
    }

    private RasterizerState SelectRasterizer(bool wireframe, HairFacePass facePass) =>
        (wireframe, facePass) switch
        {
            (false, HairFacePass.BackFaces) => _solidBackFacesRasterizer,
            (false, HairFacePass.FrontFaces) => _solidFrontFacesRasterizer,
            (true, HairFacePass.BackFaces) => _wireframeBackFacesRasterizer,
            (true, HairFacePass.FrontFaces) => _wireframeFrontFacesRasterizer,
            (false, _) => _solidRasterizer,
            _ => _wireframeRasterizer
        };

    private RasterizerState CreateRasterizer(FillMode fillMode, CullMode cullMode) => new(_device, new RasterizerStateDescription
    {
        FillMode = fillMode,
        CullMode = cullMode,
        IsDepthClipEnabled = true
    });

    private SampleDescription SelectMultisampleDescription()
    {
        if ((_device.CheckFormatSupport(Format.R16G16B16A16_Float) & FormatSupport.MultisampleResolve) == 0)
        {
            return new SampleDescription(1, 0);
        }
        foreach (var count in new[] { 4, 2 })
        {
            var colorQuality = _device.CheckMultisampleQualityLevels(Format.R16G16B16A16_Float, count);
            var depthQuality = _device.CheckMultisampleQualityLevels(Format.D32_Float, count);
            var normalQuality = _device.CheckMultisampleQualityLevels(Format.R16G16B16A16_Float, count);
            if (colorQuality > 0 && depthQuality > 0 && normalQuality > 0)
            {
                return new SampleDescription(count, Math.Min(colorQuality, Math.Min(depthQuality, normalQuality)) - 1);
            }
        }
        return new SampleDescription(1, 0);
    }

    private static ShaderBytecode CompileShader(string entryPoint, string profile)
    {
        const string resourceName = "MorphFaceEditor.Rendering.Shaders.HeadPreview.hlsl";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded shader '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var result = ShaderBytecode.Compile(
            reader.ReadToEnd(),
            entryPoint,
            profile,
            ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness,
            EffectFlags.None);
        if (result.HasErrors)
        {
            throw new InvalidOperationException(result.Message);
        }
        return result.Bytecode;
    }

    private static GpuVertex ToGpuVertex(HeadPreviewVertex vertex) => new()
    {
        Position = ToDx(vertex.Position),
        Normal = ToDx(vertex.Normal),
        Tangent = new Vector4(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z, vertex.Tangent.W),
        TextureCoordinate = new Vector2(vertex.TextureCoordinate.X, vertex.TextureCoordinate.Y),
        BoneIndex0 = vertex.BoneIndex0,
        BoneIndex1 = vertex.BoneIndex1,
        BoneIndex2 = vertex.BoneIndex2,
        BoneIndex3 = vertex.BoneIndex3,
        BoneWeights = new Vector4(vertex.BoneWeights.X, vertex.BoneWeights.Y, vertex.BoneWeights.Z, vertex.BoneWeights.W)
    };

    private static Vector3 ToDx(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);

    private static Matrix ToDx(System.Numerics.Matrix4x4 value) => new(
        value.M11, value.M12, value.M13, value.M14,
        value.M21, value.M22, value.M23, value.M24,
        value.M31, value.M32, value.M33, value.M34,
        value.M41, value.M42, value.M43, value.M44);

    private static bool IsFinite(System.Numerics.Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void DisposeMeshes()
    {
        foreach (var mesh in _meshes)
        {
            mesh.Dispose();
        }
        _meshes.Clear();
    }

    private void DisposeMaterialTextures()
    {
        _context.PixelShader.SetShaderResources(0, new ShaderResourceView?[10]);
        foreach (var texture in _gpuTextures.Values)
        {
            texture.Dispose();
        }
        _gpuTextures.Clear();
        _gpuTextureBytes = 0;
    }

    private void EvictGpuTexturesToFit(long incomingBytes, string protectedKey)
    {
        var activeKeys = GetActiveTextureKeys();
        while (_gpuTextureBytes + incomingBytes > MaximumGpuTextureBytes && _gpuTextures.Count > 0)
        {
            var candidates = _gpuTextures
                .Where(pair => !activeKeys.Contains(pair.Key) &&
                               !string.Equals(pair.Key, protectedKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 0)
            {
                break;
            }
            var candidate = candidates.MinBy(pair => pair.Value.LastUsed);
            _context.PixelShader.SetShaderResources(0, new ShaderResourceView?[10]);
            _gpuTextures.Remove(candidate.Key);
            _gpuTextureBytes -= candidate.Value.ByteCount;
            candidate.Value.Dispose();
        }
    }

    private HashSet<string> GetActiveTextureKeys() => _materials.Values
        .SelectMany(material => material.Textures.Values)
        .Select(texture => texture.Key)
        .Concat(_materials.Values.Select(material => material.FixedCubeTexture?.Key).OfType<string>())
        .Concat(_materials.Values.Select(material => material.SecondaryFixedCubeTexture?.Key).OfType<string>())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void PruneMaterialTextures()
    {
        var activeKeys = GetActiveTextureKeys();
        var expiredKeys = _gpuTextures.Keys.Where(key => !activeKeys.Contains(key)).ToArray();
        if (expiredKeys.Length == 0)
        {
            return;
        }

        _context.PixelShader.SetShaderResources(0, new ShaderResourceView?[10]);
        foreach (var key in expiredKeys)
        {
            _gpuTextures.Remove(key, out var texture);
            if (texture is not null)
            {
                _gpuTextureBytes -= texture.ByteCount;
            }
            texture?.Dispose();
        }
    }

    private void DisposeRenderTargets()
    {
        _stagingTexture.Dispose();
        _postTarget.Dispose();
        _postTexture.Dispose();
        _resolvedShaderView.Dispose();
        _resolvedTexture.Dispose();
        _resolvedAmbientShaderView.Dispose();
        _resolvedAmbientTexture.Dispose();
        _aoDepthShaderView.Dispose();
        _aoDepthTexture.Dispose();
        _depthShaderView.Dispose();
        _depthView.Dispose();
        _depthTexture.Dispose();
        _aoNormalShaderView.Dispose();
        _aoNormalTexture.Dispose();
        _normalTarget.Dispose();
        _normalTexture.Dispose();
        _ambientDiffuseTarget.Dispose();
        _ambientDiffuseTexture.Dispose();
        _renderTarget.Dispose();
        _renderTexture.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _context.ClearState();
        _context.Flush();
        DisposeMeshes();
        DisposeMaterialTextures();
        DisposeRenderTargets();
        _shadowShaderView.Dispose();
        _shadowDepthView.Dispose();
        _shadowTexture.Dispose();
        _shadowSampler.Dispose();
        _postSampler.Dispose();
        _materialSampler.Dispose();
        _depthOnlyBlendState.Dispose();
        _additiveBlendState.Dispose();
        _translucentBlendState.Dispose();
        _opaqueBlendState.Dispose();
        _depthReadState.Dispose();
        _shadowDepthState.Dispose();
        _depthState.Dispose();
        _wireframeFrontFacesRasterizer.Dispose();
        _wireframeBackFacesRasterizer.Dispose();
        _solidFrontFacesRasterizer.Dispose();
        _solidBackFacesRasterizer.Dispose();
        _wireframeRasterizer.Dispose();
        _solidRasterizer.Dispose();
        _shadowRasterizer.Dispose();
        _materialBuffer.Dispose();
        _shadowBuffer.Dispose();
        _postProcessBuffer.Dispose();
        _meshBuffer.Dispose();
        _skinningBuffer.Dispose();
        _sceneBuffer.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _shadowPixelShader.Dispose();
        _vertexShader.Dispose();
        _postPixelShader.Dispose();
        _postVertexShader.Dispose();
        _context.Dispose();
        _device.Dispose();
        _disposed = true;
    }

    private enum HairFacePass
    {
        TwoSided,
        BackFaces,
        FrontFaces
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 TextureCoordinate;
        public uint BoneIndex0;
        public uint BoneIndex1;
        public uint BoneIndex2;
        public uint BoneIndex3;
        public Vector4 BoneWeights;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneConstants
    {
        public Matrix WorldViewProjection;
        public Matrix World;
        public Matrix View;
        public Vector4 CameraPosition;
        public Vector4 KeyLightDirection;
        public Vector4 KeyLightColor;
        public Vector4 FillLightDirection;
        public Vector4 FillLightColor;
        public Vector4 RimLightDirection;
        public Vector4 RimLightColor;
        public Vector4 AmbientColor;
        public Vector4 LightingParameters;
        public Vector4 SkyColor;
        public Vector4 GroundColor;
        public Vector4 HorizonColor;
        public Vector4 EnvironmentParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowConstants
    {
        public Matrix LightViewProjection;
        public Vector4 ShadowParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PostProcessConstants
    {
        public Matrix InverseProjection;
        public Vector4 PixelSizeAndSamples;
        public Vector4 DepthParameters;
        public Vector4 BloomParameters;
        public Vector4 DisplayParameters;
        public Vector4 DisplayBackground;
    }

    private sealed class GpuTexture(
        Texture2D resource,
        long byteCount,
        long lastUsed) : IDisposable
    {
        private readonly Dictionary<TextureColorSpace, ShaderResourceView> _views = [];
        public Texture2D Resource { get; } = resource;
        public long ByteCount { get; } = byteCount;
        public long LastUsed { get; set; } = lastUsed;

        // One uploaded image can be sampled as both colour and packed data.
        public ShaderResourceView GetView(Device device, TextureColorSpace colorSpace)
        {
            if (_views.TryGetValue(colorSpace, out var existing))
            {
                return existing;
            }
            var textureDescription = Resource.Description;
            var description = new ShaderResourceViewDescription
            {
                Format = colorSpace == TextureColorSpace.Srgb
                    ? Format.R8G8B8A8_UNorm_SRgb
                    : Format.R8G8B8A8_UNorm
            };
            if ((textureDescription.OptionFlags & ResourceOptionFlags.TextureCube) != 0)
            {
                description.Dimension = ShaderResourceViewDimension.TextureCube;
                description.TextureCube = new ShaderResourceViewDescription.TextureCubeResource
                {
                    MostDetailedMip = 0,
                    MipLevels = textureDescription.MipLevels
                };
            }
            else
            {
                description.Dimension = ShaderResourceViewDimension.Texture2D;
                description.Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    MipLevels = textureDescription.MipLevels
                };
            }
            var view = new ShaderResourceView(device, Resource, description);
            _views.Add(colorSpace, view);
            return view;
        }

        public void Dispose()
        {
            foreach (var view in _views.Values)
            {
                view.Dispose();
            }
            Resource.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshConstants
    {
        public Vector4 SkinningParameters;
    }

    private sealed record MeshBuffers(
        Buffer VertexBuffer,
        Buffer IndexBuffer,
        IReadOnlyList<HeadPreviewSection> Sections,
        bool IsAttachment,
        bool ApplySkinning,
        string Name,
        int VertexCount,
        HeadPreviewVertex[] CpuVertices) : IDisposable
    {
        public HeadPreviewVertex[] CachedVertices { get; set; } = CpuVertices;

        public void Dispose()
        {
            IndexBuffer.Dispose();
            VertexBuffer.Dispose();
        }
    }
}
