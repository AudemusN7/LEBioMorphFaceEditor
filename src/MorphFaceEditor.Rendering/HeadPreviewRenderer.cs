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
    private readonly Device _device;
    private readonly DeviceContext _context;
    private readonly string _deviceKind;
    private readonly VertexShader _vertexShader;
    private readonly PixelShader _pixelShader;
    private readonly VertexShader _postVertexShader;
    private readonly PixelShader _postPixelShader;
    private readonly InputLayout _inputLayout;
    private readonly Buffer _sceneBuffer;
    private readonly Buffer _materialBuffer;
    private readonly Buffer _skinningBuffer;
    private readonly Buffer _meshBuffer;
    private readonly Buffer _postProcessBuffer;
    private readonly RasterizerState _solidRasterizer;
    private readonly RasterizerState _wireframeRasterizer;
    private readonly RasterizerState _solidBackFacesRasterizer;
    private readonly RasterizerState _solidFrontFacesRasterizer;
    private readonly RasterizerState _wireframeBackFacesRasterizer;
    private readonly RasterizerState _wireframeFrontFacesRasterizer;
    private readonly DepthStencilState _depthState;
    private readonly DepthStencilState _depthReadState;
    private readonly BlendState _opaqueBlendState;
    private readonly BlendState _translucentBlendState;
    private readonly BlendState _depthOnlyBlendState;
    private readonly SamplerState _materialSampler;
    private readonly SamplerState _postSampler;
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
    private Texture2D _aoNormalTexture = null!;
    private ShaderResourceView _aoNormalShaderView = null!;
    private Texture2D _depthTexture = null!;
    private DepthStencilView _depthView = null!;
    private ShaderResourceView _depthShaderView = null!;
    private Texture2D _aoDepthTexture = null!;
    private ShaderResourceView _aoDepthShaderView = null!;
    private Texture2D _resolvedTexture = null!;
    private ShaderResourceView _resolvedShaderView = null!;
    private Texture2D _postTexture = null!;
    private RenderTargetView _postTarget = null!;
    private Texture2D _stagingTexture = null!;
    private int _skinningPaletteCount;
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
        _sampleDescription = SelectMultisampleDescription();

        using var vertexBytecode = CompileShader("VSMain", "vs_5_0");
        using var pixelBytecode = CompileShader("PSMain", "ps_5_0");
        using var postVertexBytecode = CompileShader("VSPostProcess", "vs_5_0");
        using var postPixelBytecode = CompileShader(
            _sampleDescription.Count > 1 ? "PSPostProcessMsaa" : "PSPostProcessSingle",
            "ps_5_0");
        _vertexShader = new VertexShader(_device, vertexBytecode);
        _pixelShader = new PixelShader(_device, pixelBytecode);
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
        _solidRasterizer = CreateRasterizer(FillMode.Solid, CullMode.None);
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
        _opaqueBlendState = CreateBlendState(false);
        _translucentBlendState = CreateBlendState(true);
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
        CreateRenderTargets();
    }

    public string DeviceKind => _deviceKind;
    public int DeviceCreationCount => 1;
    public int TextureUploadCount => _textureUploadCount;
    public int MultisampleCount => _sampleDescription.Count;
    public int MaterialAnisotropy => 8;
    public long GpuTextureBytes => _gpuTextureBytes;

    public void SetScene(HeadPreviewScene scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        DisposeMeshes();
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
                vertices.Length));
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
        WriteDynamicVertices(mesh.VertexBuffer, vertices.Select(ToGpuVertex).ToArray());
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

    public HeadPreviewFrame Render(HeadOrbitCamera camera, HeadPreviewOptions options, byte[]? destination = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(options);
        var requiredLength = checked(_width * _height * 4);
        if (destination is not null && destination.Length != requiredLength)
        {
            throw new ArgumentException($"Destination must contain exactly {requiredLength} bytes.", nameof(destination));
        }

        var stopwatch = Stopwatch.StartNew();
        ConfigurePipeline(options);
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
                lighting.Exposure,
                lighting.KeyAngularRadius,
                lighting.AmbientFresnel,
                options.RenderMode == HeadPreviewRenderMode.Unlit ? 1 : 0),
            SkyColor = new Vector4(lighting.AmbientColor.X, lighting.AmbientColor.Y, lighting.AmbientColor.Z, 0),
            GroundColor = new Vector4(lighting.GroundColor.X, lighting.GroundColor.Y, lighting.GroundColor.Z, 0),
            HorizonColor = new Vector4(lighting.HorizonColor.X, lighting.HorizonColor.Y, lighting.HorizonColor.Z, 0),
            EnvironmentParameters = new Vector4(lighting.AmbientIntensity, 0, 0, 0)
        };
        _context.UpdateSubresource(ref scene, _sceneBuffer);

        var draws = _meshes
            .Where(mesh => !mesh.IsAttachment || options.ShowAttachment)
            .SelectMany(mesh => mesh.Sections
                .Where(section => section.IndexCount > 0)
                .Select(section => (
                    Mesh: mesh,
                    Section: section,
                    Material: _materials.GetValueOrDefault(section.Material.Key, section.Material))))
            .ToArray();
        var drawCalls = 0;
        foreach (var draw in draws.Where(draw => !IsTranslucent(draw.Material, options.RenderMode)))
        {
            DrawSection(draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe);
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
                    depthThreshold: 1f);
                drawCalls++;
            }
        }
        foreach (var draw in translucentDraws)
        {
            if (draw.Material.Family == HeadMaterialFamily.Hair)
            {
                // TwoSidedSeparatePass renders back-facing cards first, then
                // front-facing cards. Cull-none in one draw retains index order
                // and is visibly wrong on layered shell hairstyles.
                DrawSection(
                    draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe,
                    facePass: HairFacePass.BackFaces);
                DrawSection(
                    draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe,
                    facePass: HairFacePass.FrontFaces);
                drawCalls += 2;
            }
            else
            {
                DrawSection(draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe);
                drawCalls++;
            }
        }
        if (options.RenderMode != HeadPreviewRenderMode.Diagnostic)
        {
            // bUseLitTranslucencyPostRenderDepthPass writes depth after colour
            // for every non-zero 8-bit alpha texel in BioWare's shader.
            foreach (var draw in hairDraws)
            {
                DrawSection(
                    draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe,
                    depthThreshold: 1f / 255f);
                drawCalls++;
            }
        }

        _context.PixelShader.SetShaderResources(0, new ShaderResourceView?[10]);
        ApplyPostProcess(
            lighting,
            camera,
            options.AmbientOcclusionEnabled && options.RenderMode != HeadPreviewRenderMode.Unlit);

        var pixels = ReadTexture(destination);
        stopwatch.Stop();
        return new HeadPreviewFrame(_width, _height, pixels, stopwatch.Elapsed, drawCalls, _deviceKind);
    }

    private void DrawSection(
        MeshBuffers mesh,
        HeadPreviewSection section,
        HeadPreviewMaterial definition,
        HeadPreviewRenderMode renderMode,
        bool wireframe,
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
        material.BlendMode == HeadMaterialBlendMode.Translucent;

    private void CaptureAmbientOcclusionInputs()
    {
        // UE3 evaluates screen-space AO from the opaque scene before lit
        // translucency. Snapshotting here keeps hair/lash cards out of both the
        // occluder depth and smooth-normal buffers while preserving opaque hats.
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
        _context.CopyResource(_depthTexture, _aoDepthTexture);
        _context.CopyResource(_normalTexture, _aoNormalTexture);
        _context.OutputMerger.SetTargets(_depthView, _renderTarget, _normalTarget);
    }

    private void ConfigurePipeline(HeadPreviewOptions options)
    {
        var background = options.BackgroundColor ?? new System.Numerics.Vector3(0.035f, 0.043f, 0.055f);
        _context.OutputMerger.SetTargets(_depthView, _renderTarget, _normalTarget);
        _context.OutputMerger.SetDepthStencilState(_depthState);
        _context.Rasterizer.SetViewport(0, 0, _width, _height);
        _context.Rasterizer.State = options.Wireframe ? _wireframeRasterizer : _solidRasterizer;
        _context.ClearRenderTargetView(_renderTarget, new Color4(
            Math.Clamp(background.X, 0, 1),
            Math.Clamp(background.Y, 0, 1),
            Math.Clamp(background.Z, 0, 1),
            1));
        _context.ClearRenderTargetView(_normalTarget, new Color4(0.5f, 0.5f, 1, 1));
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
        _context.PixelShader.SetSampler(0, _materialSampler);
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
        _context.OutputMerger.SetBlendState(translucent ? _translucentBlendState : _opaqueBlendState);
        _context.OutputMerger.SetDepthStencilState(translucent ? _depthReadState : _depthState);

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
            return existing.View;
        }
        if (texture.Rgba8Faces.Count != 6)
        {
            throw new InvalidDataException($"Cube texture '{texture.Key}' must contain exactly six faces.");
        }

        var authoredMips = GetValidCubeMips(texture);
        var streams = new List<DataStream>(authoredMips.Count * 6);
        try
        {
            var format = texture.ColorSpace == TextureColorSpace.Srgb
                ? Format.R8G8B8A8_UNorm_SRgb
                : Format.R8G8B8A8_UNorm;
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
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.TextureCube
            }, rectangles.ToArray());
            var byteCount = authoredMips.Sum(mip => mip.Rgba8Faces.Sum(face => (long)face.Length));
            EvictGpuTexturesToFit(byteCount, texture.Key);
            var gpuTexture = new GpuTexture(resource, new ShaderResourceView(_device, resource), byteCount, ++_textureUseSerial);
            _gpuTextures[texture.Key] = gpuTexture;
            _gpuTextureBytes += byteCount;
            _textureUploadCount++;
            return gpuTexture.View;
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
            return existing.View;
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
            var format = texture.ColorSpace == TextureColorSpace.Srgb
                ? Format.R8G8B8A8_UNorm_SRgb
                : Format.R8G8B8A8_UNorm;
            var resource = new Texture2D(_device, new Texture2DDescription
            {
                Width = texture.Width,
                Height = texture.Height,
                MipLevels = authoredMips.Count,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            }, rectangles);
            var byteCount = authoredMips.Sum(mip => (long)mip.Rgba8.Length);
            EvictGpuTexturesToFit(byteCount, texture.Key);
            var gpuTexture = new GpuTexture(resource, new ShaderResourceView(_device, resource), byteCount, ++_textureUseSerial);
            _gpuTextures[texture.Key] = gpuTexture;
            _gpuTextureBytes += byteCount;
            _textureUploadCount++;
            return gpuTexture.View;
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
        description.RenderTarget[0] = new RenderTargetBlendDescription
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
        bool ambientOcclusionEnabled)
    {
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
        if (_sampleDescription.Count > 1)
        {
            _context.ResolveSubresource(_renderTexture, 0, _resolvedTexture, 0, Format.B8G8R8A8_UNorm);
        }
        else
        {
            _context.CopyResource(_renderTexture, _resolvedTexture);
        }
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
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(10, new ShaderResourceView?[5]);
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
    }

    private static float ClampFinite(float value, float fallback, float minimum, float maximum) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private void CreateRenderTargets()
    {
        _renderTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
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
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });
        _resolvedShaderView = new ShaderResourceView(_device, _resolvedTexture);
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
        foreach (var count in new[] { 4, 2 })
        {
            var colorQuality = _device.CheckMultisampleQualityLevels(Format.B8G8R8A8_UNorm, count);
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
        _aoDepthShaderView.Dispose();
        _aoDepthTexture.Dispose();
        _depthShaderView.Dispose();
        _depthView.Dispose();
        _depthTexture.Dispose();
        _aoNormalShaderView.Dispose();
        _aoNormalTexture.Dispose();
        _normalTarget.Dispose();
        _normalTexture.Dispose();
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
        _postSampler.Dispose();
        _materialSampler.Dispose();
        _depthOnlyBlendState.Dispose();
        _translucentBlendState.Dispose();
        _opaqueBlendState.Dispose();
        _depthReadState.Dispose();
        _depthState.Dispose();
        _wireframeFrontFacesRasterizer.Dispose();
        _wireframeBackFacesRasterizer.Dispose();
        _solidFrontFacesRasterizer.Dispose();
        _solidBackFacesRasterizer.Dispose();
        _wireframeRasterizer.Dispose();
        _solidRasterizer.Dispose();
        _materialBuffer.Dispose();
        _postProcessBuffer.Dispose();
        _meshBuffer.Dispose();
        _skinningBuffer.Dispose();
        _sceneBuffer.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
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
    private struct PostProcessConstants
    {
        public Matrix InverseProjection;
        public Vector4 PixelSizeAndSamples;
        public Vector4 DepthParameters;
        public Vector4 BloomParameters;
    }

    private sealed class GpuTexture(
        Texture2D resource,
        ShaderResourceView view,
        long byteCount,
        long lastUsed) : IDisposable
    {
        public Texture2D Resource { get; } = resource;
        public ShaderResourceView View { get; } = view;
        public long ByteCount { get; } = byteCount;
        public long LastUsed { get; set; } = lastUsed;

        public void Dispose()
        {
            View.Dispose();
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
        int VertexCount) : IDisposable
    {
        public void Dispose()
        {
            IndexBuffer.Dispose();
            VertexBuffer.Dispose();
        }
    }
}
