using System.Diagnostics;
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
    private const int MaximumBoneCount = HeadPreviewSceneState.MaximumBoneCount;
    private const int DefaultShadowMapSize = 1024;
    private readonly Device _device;
    private readonly DeviceContext _context;
    private readonly string _deviceKind;
    private readonly Stopwatch _animationClock = Stopwatch.StartNew();
    private readonly VertexShader _vertexShader;
    private readonly PixelShader _pixelShader;
    private readonly PixelShader _shadowPixelShader;
    private readonly VertexShader _postVertexShader;
    private readonly PixelShader _postPixelShader;
    private readonly PixelShader _aoSurfaceShader;
    private readonly PixelShader _ambientOcclusionShader;
    private readonly PixelShader _aoBlurShader;
    private readonly PixelShader _applyAmbientOcclusionShader;
    private readonly PixelShader _bloomExtractShader;
    private readonly PixelShader _bloomBlurShader;
    private readonly InputLayout _inputLayout;
    private readonly Buffer _sceneBuffer;
    private readonly Buffer _materialBuffer;
    private readonly Buffer _skinningBuffer;
    private readonly Buffer _meshBuffer;
    private readonly Buffer _postProcessBuffer;
    private readonly Buffer _shadowBuffer;
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
    private readonly BlendState _additiveBlendState;
    private readonly BlendState _depthOnlyBlendState;
    private readonly SamplerState _materialSampler;
    private readonly SamplerState _postSampler;
    private readonly SampleDescription _sampleDescription;
    private readonly HeadPreviewSceneState _sceneState;
    private readonly List<MeshBuffers> _meshes = [];
    private readonly Dictionary<HeadPreviewMaterial, HeadPreviewPreparedMaterial> _materials = new(ReferenceEqualityComparer.Instance);
    private readonly HeadPreviewTextureCache _textureCache;
    private int _width;
    private int _height;
    private HeadPreviewRenderTargets _targets = null!;
    private HeadPreviewSurface? _gradingLut;
    private HeadPreviewPostProcessSettings? _gradingSettings;
    private HeadPreviewSurface _shadowMap = null!;

    private int SkinningPaletteCount => _sceneState.SkinningPalette.Count;
    private float _shadowRadius;
    private bool _disposed;

    public HeadPreviewRenderer(int width, int height)
        : this(width, height, new HeadPreviewSceneState())
    {
    }

    public HeadPreviewRenderer(int width, int height, HeadPreviewSceneState sceneState)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _sceneState = sceneState ?? throw new ArgumentNullException(nameof(sceneState));
        _width = width;
        _height = height;
        try
        {
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
            _textureCache = new HeadPreviewTextureCache(_device, _context);
            var hdrSupport = _device.CheckFormatSupport(Format.R16G16B16A16_Float);
            var requiredHdrSupport = FormatSupport.RenderTarget | FormatSupport.Blendable | FormatSupport.ShaderSample;
            if ((hdrSupport & requiredHdrSupport) != requiredHdrSupport)
            {
                throw new NotSupportedException(
                    "The selected D3D11 device cannot render, blend, and sample the required R16G16B16A16_FLOAT preview target.");
            }
            _sampleDescription = SelectMultisampleDescription();

            using var vertexBytecode = LoadShader("VSMain", "vs_5_0");
            using var pixelBytecode = LoadShader("PSMain", "ps_5_0");
            using var shadowPixelBytecode = LoadShader("PSShadow", "ps_5_0");
            using var postVertexBytecode = LoadShader("VSPostProcess", "vs_5_0");
            using var postPixelBytecode = LoadShader("PSPostProcess", "ps_5_0");
            using var aoSurfaceBytecode = LoadShader(
                _sampleDescription.Count > 1 ? "PSAoSurfaceMsaa" : "PSAoSurfaceSingle", "ps_5_0");
            using var ambientOcclusionBytecode = LoadShader("PSAmbientOcclusion", "ps_5_0");
            using var aoBlurBytecode = LoadShader("PSAoBlur", "ps_5_0");
            using var applyAmbientOcclusionBytecode = LoadShader("PSApplyAmbientOcclusion", "ps_5_0");
            using var bloomExtractBytecode = LoadShader("PSBloomExtract", "ps_5_0");
            using var bloomBlurBytecode = LoadShader("PSBloomBlur", "ps_5_0");
            _vertexShader = new VertexShader(_device, vertexBytecode);
            _pixelShader = new PixelShader(_device, pixelBytecode);
            _shadowPixelShader = new PixelShader(_device, shadowPixelBytecode);
            _postVertexShader = new VertexShader(_device, postVertexBytecode);
            _postPixelShader = new PixelShader(_device, postPixelBytecode);
            _aoSurfaceShader = new PixelShader(_device, aoSurfaceBytecode);
            _ambientOcclusionShader = new PixelShader(_device, ambientOcclusionBytecode);
            _aoBlurShader = new PixelShader(_device, aoBlurBytecode);
            _applyAmbientOcclusionShader = new PixelShader(_device, applyAmbientOcclusionBytecode);
            _bloomExtractShader = new PixelShader(_device, bloomExtractBytecode);
            _bloomBlurShader = new PixelShader(_device, bloomBlurBytecode);
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
            EnsureRenderTargets();
            _shadowMap = HeadPreviewSurface.Depth(_device, DefaultShadowMapSize, DefaultShadowMapSize,
                BindFlags.DepthStencil | BindFlags.ShaderResource, new SampleDescription(1, 0));
            if (_sceneState.IsLoaded) UploadScene();
        }
        catch
        {
            // No renderer instance is returned when construction fails.
            // Release partial GPU state without discarding the shared CPU scene.
            Dispose(releaseScene: false);
            throw;
        }
    }

    public string DeviceKind => _deviceKind;
    public int DeviceCreationCount => 1;
    public int TextureUploadCount => _textureCache.UploadCount;
    public int MultisampleCount => _sampleDescription.Count;
    public int MaterialAnisotropy => 8;
    public long GpuTextureBytes => _textureCache.ByteCount;

    /// <summary>Releases face resources while retaining the device, shaders and render targets.</summary>
    public void ClearScene()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // D3D bindings retain references too: unbind before releasing the old face.
        _context.ClearState();
        DisposeMeshes();
        _shadowRadius = 0;
        _sceneState.ClearScene();
        _textureCache.Clear();
        _context.Flush();
    }

    public void SetScene(HeadPreviewScene scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sceneState.SetScene(scene);
        UploadScene();
    }

    private void UploadScene()
    {
        DisposeMeshes();
        _shadowRadius = 0;
        UploadSkinningPalette();
        foreach (var mesh in _sceneState.Meshes)
        {
            _meshes.Add(CreateMeshBuffers(mesh));
        }
        PrepareMaterials();
        // Keep reusable textures from the previous scene only while the
        // bounded LRU has room; active resources are protected from eviction.
        _textureCache.SetActiveKeys(GetActiveTextureKeys());
        _textureCache.TrimToBudget();
    }

    public void UpdateMaterials(IReadOnlyDictionary<string, HeadPreviewMaterial> materials)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sceneState.UpdateMaterials(materials);
        PrepareMaterials();
        _textureCache.SetActiveKeys(GetActiveTextureKeys());
        _textureCache.PruneInactive();
    }

    public void UpdateDeformation(HeadPreviewDeformationUpdate update)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sceneState.UpdateDeformation(update);
        UploadMeshVertices(update.MeshName);
        foreach (var attachment in update.AttachmentUpdates)
        {
            UploadMeshVertices(attachment.MeshName);
        }
        UploadSkinningPalette();
    }

    private void UploadMeshVertices(string meshName)
    {
        var mesh = _meshes.SingleOrDefault(candidate => string.Equals(candidate.Name, meshName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Preview mesh '{meshName}' is not loaded.");
        WriteDynamicVertices(mesh.VertexBuffer, mesh.CpuMesh.Vertices);
    }

    private unsafe void UploadSkinningPalette()
    {
        var palette = _sceneState.SkinningPalette;
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
    }

    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_targets is not null && _width == width && _height == height) return;
        _context.ClearState();
        // Release first: retaining both full viewport groups would double the
        // already substantial target allocation during a high-DPI resize.
        _targets?.Dispose();
        _targets = null!;
        var replacement = new HeadPreviewRenderTargets(_device, width, height, _sampleDescription);
        _targets = replacement;
        _width = width;
        _height = height;
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
        EnsureRenderTargets();
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
        var view = camera.CreateViewMatrix();
        var projection = camera.CreateProjectionMatrix(_width / (float)_height);
        var cameraPosition = camera.Position;
        var lighting = HeadPreviewLightingPresets.Get(options.LightingPreset);
        var environment = options.Environment ?? HeadPreviewLightingPresets.GetEnvironment(options.LightingPreset);
        var scene = new SceneConstants
        {
            WorldViewProjection = view * projection,
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
            RenderParameters = new Vector4(options.RenderMode == HeadPreviewRenderMode.Unlit ? 1 : 0, 0, 0, 0),
            // Preset room bounce shares the camera-relative direct-light rig.
            // An explicit authored environment retains its world-space contract.
            EnvironmentSh0 = new Vector4(ToDx(environment.Coefficient0), options.Environment is null ? 1 : 0),
            EnvironmentSh1 = new Vector4(ToDx(environment.Coefficient1), 0),
            EnvironmentSh2 = new Vector4(ToDx(environment.Coefficient2), 0),
            EnvironmentSh3 = new Vector4(ToDx(environment.Coefficient3), 0),
            EnvironmentSh4 = new Vector4(ToDx(environment.Coefficient4), 0),
            EnvironmentSh5 = new Vector4(ToDx(environment.Coefficient5), 0),
            EnvironmentSh6 = new Vector4(ToDx(environment.Coefficient6), 0),
            EnvironmentSh7 = new Vector4(ToDx(environment.Coefficient7), 0),
            EnvironmentSh8 = new Vector4(ToDx(environment.Coefficient8), 0)
        };
        var draws = _meshes
            .Where(mesh => !mesh.IsAttachment || options.ShowAttachment)
            .SelectMany(mesh => mesh.Sections
                .Where(section => section.IndexCount > 0)
                .Select(section => (
                    Mesh: mesh,
                    Section: section,
                    Material: section.Material)))
            .ToArray();
        var shadowDrawCalls = 0;
        var shadowsEnabled = options.RenderMode == HeadPreviewRenderMode.Shaded && options.ShadowsEnabled;
        var ambientOcclusionEnabled = options.AmbientOcclusionEnabled && options.PostProcessingEnabled &&
                                      options.RenderMode != HeadPreviewRenderMode.Unlit;
        if (shadowsEnabled)
        {
            shadowDrawCalls = RenderShadowMap(draws, ref scene, camera, lighting, options);
        }
        else
        {
            SetShadowConstants(Matrix.Identity, enabled: false, options);
            _context.PixelShader.SetShaderResources(17, new ShaderResourceView?[] { null });
        }

        ConfigurePipeline(options);
        _context.UpdateSubresource(ref scene, _sceneBuffer);
        _context.PixelShader.SetShaderResources(17, shadowsEnabled ? _shadowMap.ShaderView : null);
        var drawCalls = 0;
        foreach (var draw in draws.Where(draw => !IsTranslucent(draw.Material, options.RenderMode)))
        {
            DrawSection(draw.Mesh, draw.Section, draw.Material, options.RenderMode, options.Wireframe, frameAnimationTime);
            drawCalls++;
        }
        var postProcessSettings = options.PostProcessSettings ?? HeadPreviewPostProcessProfiles.Get(options.LightingPreset);
        var postProcess = PreparePostProcess(lighting, camera, options.BackgroundColor,
            postProcessSettings, options.PostProcessingEnabled);
        if (ambientOcclusionEnabled)
        {
            RenderAmbientOcclusionVisibility(ref postProcess);
            // Preserve the opaque receiver plane while the remaining scene draws add colour.
            _context.OutputMerger.SetTargets(_targets.Depth.DepthView,
                _targets.Color.Target, _targets.Normal.Target, _targets.AmbientDiffuse.Target);
            BindGeometryPipeline(options);
        }
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
        _context.PixelShader.SetShaderResources(0, new ShaderResourceView?[10]);
        ApplyPostProcess(ref postProcess, ambientOcclusionEnabled, postProcessSettings);

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
        HeadPreviewOptions options)
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
        // Refit from the visible caster geometry each frame. Keeping the old
        // maximum here makes an undone morph or hidden attachment permanently
        // enlarge the map and change its depth precision.
        _shadowRadius = Math.Max(radius * 1.15f, 0.01f);
        radius = _shadowRadius;
        var texelSize = (2 * radius) / _shadowMap.Width;
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
        var cameraViewProjection = scene.WorldViewProjection;
        scene.WorldViewProjection = lightViewProjection;
        _context.UpdateSubresource(ref scene, _sceneBuffer);
        SetShadowConstants(lightViewProjection, enabled: false, options);

        // t17 aliases the depth resource used as the DSV below. Explicitly
        // unbind it first to avoid D3D11's read/write resource hazard.
        _context.PixelShader.SetShaderResources(17, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets(_shadowMap.DepthView);
        _context.ClearDepthStencilView(_shadowMap.DepthView, DepthStencilClearFlags.Depth, 1, 0);
        _context.Rasterizer.SetViewport(0, 0, _shadowMap.Width, _shadowMap.Width);
        _context.Rasterizer.State = _solidRasterizer;
        _context.OutputMerger.SetDepthStencilState(_depthState);
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
            DrawShadowSection(draw.Mesh, draw.Section, draw.Material);
        }

        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
        scene.WorldViewProjection = cameraViewProjection;
        SetShadowConstants(lightViewProjection, enabled: true, options);
        return casters.Length;
    }

    private void DrawShadowSection(
        MeshBuffers mesh,
        HeadPreviewSection section,
        HeadPreviewMaterial definition)
    {
        _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(mesh.VertexBuffer, Marshal.SizeOf<GpuVertex>(), 0));
        _context.InputAssembler.SetIndexBuffer(mesh.IndexBuffer, Format.R32_UInt, 0);
        var meshConstants = new MeshConstants
        {
            SkinningParameters = new Vector4(mesh.ApplySkinning && SkinningPaletteCount > 0 ? 1 : 0, SkinningPaletteCount, 0, 0)
        };
        _context.UpdateSubresource(ref meshConstants, _meshBuffer);
        var prepared = _materials[definition];
        var bindings = prepared.Bindings;
        var material = prepared.GetConstants(HeadPreviewRenderMode.Shaded);
        _context.UpdateSubresource(ref material, _materialBuffer);
        BindMaterial(definition, bindings, HeadPreviewRenderMode.Shaded);
        // BindMaterial selects camera-view blend/depth states; the shadow pass
        // always writes opaque depth after PSShadow has clipped coverage.
        _context.OutputMerger.SetBlendState(_depthOnlyBlendState);
        _context.OutputMerger.SetDepthStencilState(_depthState);
        _context.Rasterizer.State = _solidRasterizer;
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
        foreach (var vertex in mesh.CpuMesh.Vertices)
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
        foreach (var vertex in mesh.CpuMesh.Vertices)
        {
            radiusSquared = Math.Max(radiusSquared, System.Numerics.Vector3.DistanceSquared(GetPosedPosition(mesh, vertex), center));
        }
        return Math.Max(MathF.Sqrt(radiusSquared), 0.01f);
    }

    private System.Numerics.Vector3 GetPosedPosition(MeshBuffers mesh, HeadPreviewVertex vertex)
    {
        if (!mesh.ApplySkinning || SkinningPaletteCount == 0)
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
            result += boneIndex >= 0 && boneIndex < SkinningPaletteCount
                ? System.Numerics.Vector3.Transform(vertex.Position, _sceneState.SkinningPalette[boneIndex]) * weight
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
                1f / _shadowMap.Width,
                0.0035f,
                0.075f * Math.Max(_shadowRadius, 0.01f)),
            ShadowFilterParameters = new Vector4(
                Math.Clamp(HeadPreviewLightingPresets.Get(options.LightingPreset).ShadowSoftnessWorld,
                    0, Math.Max(_shadowRadius * 2, 0.01f))
                    / (2 * Math.Max(_shadowRadius, 0.01f)),
                0.15f / Math.Max(_shadowRadius * 5 - 0.01f, 0.01f),
                0,
                0)
        };
        _context.UpdateSubresource(ref parameters, _shadowBuffer);
        _context.PixelShader.SetConstantBuffer(5, _shadowBuffer);
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
                mesh.ApplySkinning && SkinningPaletteCount > 0 ? 1 : 0,
                SkinningPaletteCount,
                0,
                0)
        };
        _context.UpdateSubresource(ref meshConstants, _meshBuffer);
        var prepared = _materials[definition];
        var bindings = prepared.Bindings;
        var material = prepared.GetConstants(renderMode);
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

    private void ConfigurePipeline(HeadPreviewOptions options)
    {
        var background = options.BackgroundColor ?? new System.Numerics.Vector3(0.035f, 0.043f, 0.055f);
        var linearBackground = new System.Numerics.Vector3(
            ToHdrFloat(SrgbToLinear(background.X)),
            ToHdrFloat(SrgbToLinear(background.Y)),
            ToHdrFloat(SrgbToLinear(background.Z)));
        _context.OutputMerger.SetTargets(_targets.Depth.DepthView, _targets.Color.Target, _targets.Normal.Target, _targets.AmbientDiffuse.Target, _targets.OpaquePlane.Target);
        _context.ClearRenderTargetView(_targets.Color.Target, new Color4(
            linearBackground.X,
            linearBackground.Y,
            linearBackground.Z,
            1));
        _context.ClearRenderTargetView(_targets.Normal.Target, new Color4(0.5f, 0.5f, 1, 1));
        _context.ClearRenderTargetView(_targets.OpaquePlane.Target, new Color4(0.5f, 0.5f, 1, 1));
        _context.ClearRenderTargetView(_targets.AmbientDiffuse.Target, new Color4(0, 0, 0, 0));
        _context.ClearDepthStencilView(_targets.Depth.DepthView, DepthStencilClearFlags.Depth, 1, 0);
        BindGeometryPipeline(options);
    }

    private void BindGeometryPipeline(HeadPreviewOptions options)
    {
        _context.OutputMerger.SetDepthStencilState(_depthState);
        _context.Rasterizer.SetViewport(0, 0, _width, _height);
        _context.Rasterizer.State = options.Wireframe ? _wireframeRasterizer : _solidRasterizer;
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

        // Finish uploads/eviction before binding either group for this draw.
        ShaderResourceView?[] views =
        [
            _textureCache.GetView(bindings.Diffuse),
            _textureCache.GetView(bindings.Normal),
            _textureCache.GetView(bindings.Mask),
            _textureCache.GetView(bindings.Detail),
            _textureCache.GetView(bindings.Auxiliary1),
            _textureCache.GetView(bindings.Auxiliary2),
            _textureCache.GetView(bindings.Auxiliary3),
            _textureCache.GetView(bindings.Auxiliary4),
            _textureCache.GetCubeView(material.FixedCubeTexture),
            _textureCache.GetCubeView(material.SecondaryFixedCubeTexture)
        ];
        var auxiliary5 = _textureCache.GetView(bindings.Auxiliary5);
        var auxiliary6 = _textureCache.GetView(bindings.Auxiliary6);
        _context.PixelShader.SetShaderResources(0, views);
        _context.PixelShader.SetShaderResources(15, auxiliary5, auxiliary6);
    }

    private BlendState CreateBlendState(bool translucent)
    {
        var description = new BlendStateDescription();
        for (var targetIndex = 0; targetIndex < 4; targetIndex++)
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
        for (var targetIndex = 0; targetIndex < 4; targetIndex++)
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

    private PostProcessConstants PreparePostProcess(
        HeadPreviewLightingRig lighting,
        HeadOrbitCamera camera,
        System.Numerics.Vector3? backgroundColor,
        HeadPreviewPostProcessSettings settings,
        bool postProcessingEnabled)
    {
        var background = backgroundColor ?? new System.Numerics.Vector3(0.035f, 0.043f, 0.055f);
        var constants = new PostProcessConstants
        {
            InverseProjection = Matrix.Invert(camera.CreateProjectionMatrix(_width / (float)_height)),
            PixelSizeAndSamples = new Vector4(
                1f / _width,
                1f / _height,
                _sampleDescription.Count,
                0),
            DepthParameters = new Vector4(
                0,
                HeadPreviewAmbientOcclusionSettings.AngularBias,
                0,
                0),
            BloomParameters = new Vector4(
                lighting.BloomThreshold,
                lighting.BloomIntensity,
                settings.BloomScreenBlendThreshold,
                0),
            DisplayParameters = new Vector4(
                lighting.Exposure,
                postProcessingEnabled ? 1 : 0,
                0,
                settings.ToneCurveStrength),
            DisplayBackground = new Vector4(
                ToHdrFloat(SrgbToLinear(background.X)),
                ToHdrFloat(SrgbToLinear(background.Y)),
                ToHdrFloat(SrgbToLinear(background.Z)),
                0),
            BloomTint = new Vector4(ToDx(settings.BloomTint), 0),
            BloomPassParameters = Vector4.Zero,
            AoRadiusParameters = new Vector4(
                HeadPreviewAmbientOcclusionSettings.ContactRadiusWorld,
                HeadPreviewAmbientOcclusionSettings.CavityRadiusWorld,
                HeadPreviewAmbientOcclusionSettings.ContactStrength,
                HeadPreviewAmbientOcclusionSettings.CavityStrength),
            AoFilterParameters = new Vector4(
                0, 0,
                HeadPreviewAmbientOcclusionSettings.FilterDepthSigmaWorld,
                0),
            AoParameters = new Vector4(HeadPreviewAmbientOcclusionSettings.PowerExponent, 0, 0, 0)
        };
        _context.UpdateSubresource(ref constants, _postProcessBuffer);
        return constants;
    }

    private void ApplyPostProcess(
        ref PostProcessConstants constants,
        bool ambientOcclusionEnabled,
        HeadPreviewPostProcessSettings settings)
    {
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
        if (_sampleDescription.Count > 1)
        {
            _context.ResolveSubresource(_targets.Color.Texture, 0, _targets.Resolved.Texture, 0, Format.R16G16B16A16_Float);
            _context.ResolveSubresource(_targets.AmbientDiffuse.Texture, 0, _targets.ResolvedAmbient.Texture, 0, Format.R16G16B16A16_Float);
        }
        else
        {
            _context.CopyResource(_targets.Color.Texture, _targets.Resolved.Texture);
            _context.CopyResource(_targets.AmbientDiffuse.Texture, _targets.ResolvedAmbient.Texture);
        }
        var postProcessingEnabled = constants.DisplayParameters.Y > 0.5f;
        if (postProcessingEnabled) UpdateGradingLut(settings);

        // AO modifies only the isolated ambient-diffuse contribution, in linear
        // HDR. Both bloom and display conversion consume this same composition.
        var sceneColor = ambientOcclusionEnabled && postProcessingEnabled
            ? ComposeAmbientOcclusion()
            : _targets.Resolved.ShaderView;
        if (postProcessingEnabled && constants.BloomParameters.Y > 0)
            RenderBloom(ref constants, settings.BloomRadius1080, sceneColor);

        PreparePostPass(_targets.Post.Target, _postPixelShader, _width, _height);
        _context.PixelShader.SetShaderResources(10, sceneColor);
        _context.PixelShader.SetShaderResources(18, _targets.ResolvedAmbient.ShaderView,
            postProcessingEnabled && constants.BloomParameters.Y > 0 ? _targets.Bloom.ShaderView : null,
            _gradingLut?.ShaderView);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(10, new ShaderResourceView?[] { null });
        _context.PixelShader.SetShaderResources(18, new ShaderResourceView?[3]);
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
    }

    private void PreparePostPass(RenderTargetView target, PixelShader shader, int width, int height)
    {
        _context.OutputMerger.SetTargets((DepthStencilView?)null, target);
        _context.OutputMerger.SetDepthStencilState(null);
        _context.OutputMerger.SetBlendState(_opaqueBlendState);
        _context.Rasterizer.SetViewport(0, 0, width, height);
        _context.Rasterizer.State = _solidRasterizer;
        _context.InputAssembler.InputLayout = null;
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _context.VertexShader.Set(_postVertexShader);
        _context.PixelShader.Set(shader);
        _context.PixelShader.SetConstantBuffer(4, _postProcessBuffer);
        _context.PixelShader.SetSampler(1, _postSampler);
    }

    private void RenderAmbientOcclusionVisibility(ref PostProcessConstants constants)
    {
        // Consume opaque depth and material normals before translucency changes them.
        PreparePostPass(_targets.AoSurface.Target, _aoSurfaceShader, _width, _height);
        _context.OutputMerger.SetTargets((DepthStencilView?)null, _targets.AoSurface.Target, _targets.AoReceiverPlane.Target);
        _context.PixelShader.SetShaderResources(11,
            _sampleDescription.Count > 1 ? _targets.Depth.ShaderView : null,
            _sampleDescription.Count > 1 ? null : _targets.Depth.ShaderView,
            _sampleDescription.Count > 1 ? _targets.Normal.ShaderView : null,
            _sampleDescription.Count > 1 ? null : _targets.Normal.ShaderView);
        _context.PixelShader.SetShaderResources(23,
            _sampleDescription.Count > 1 ? _targets.OpaquePlane.ShaderView : null,
            _sampleDescription.Count > 1 ? null : _targets.OpaquePlane.ShaderView);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(11, new ShaderResourceView?[4]);
        _context.PixelShader.SetShaderResources(23, new ShaderResourceView?[2]);
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);

        PreparePostPass(_targets.AoVisibility.Target, _ambientOcclusionShader, _width, _height);
        _context.PixelShader.SetShaderResources(21, _targets.AoSurface.ShaderView);
        _context.PixelShader.SetShaderResources(25, _targets.AoReceiverPlane.ShaderView);
        _context.PixelShader.SetShaderResources(11,
            _sampleDescription.Count > 1 ? _targets.Depth.ShaderView : null);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(11, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);

        constants.AoFilterParameters.X = 1;
        constants.AoFilterParameters.Y = 0;
        _context.UpdateSubresource(ref constants, _postProcessBuffer);
        PreparePostPass(_targets.AoScratch.Target, _aoBlurShader, _width, _height);
        _context.PixelShader.SetShaderResources(22, _targets.AoVisibility.ShaderView);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(22, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);

        constants.AoFilterParameters.X = 0;
        constants.AoFilterParameters.Y = 1;
        _context.UpdateSubresource(ref constants, _postProcessBuffer);
        PreparePostPass(_targets.AoVisibility.Target, _aoBlurShader, _width, _height);
        _context.PixelShader.SetShaderResources(22, _targets.AoScratch.ShaderView);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(22, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
        _context.PixelShader.SetShaderResources(21, new ShaderResourceView?[2]);
        _context.PixelShader.SetShaderResources(25, new ShaderResourceView?[] { null });
    }

    private ShaderResourceView ComposeAmbientOcclusion()
    {
        PreparePostPass(_targets.AoComposited.Target, _applyAmbientOcclusionShader, _width, _height);
        _context.PixelShader.SetShaderResources(10, _targets.Resolved.ShaderView);
        _context.PixelShader.SetShaderResources(18, _targets.ResolvedAmbient.ShaderView);
        _context.PixelShader.SetShaderResources(22, _targets.AoVisibility.ShaderView);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(10, new ShaderResourceView?[] { null });
        _context.PixelShader.SetShaderResources(18, new ShaderResourceView?[] { null });
        _context.PixelShader.SetShaderResources(22, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
        return _targets.AoComposited.ShaderView;
    }

    private void RenderBloom(ref PostProcessConstants constants, float radius1080, ShaderResourceView sceneColor)
    {
        var width = _targets.Bloom.Width;
        var height = _targets.Bloom.Height;
        PreparePostPass(_targets.Bloom.Target, _bloomExtractShader, width, height);
        _context.PixelShader.SetShaderResources(10, sceneColor);
        _context.PixelShader.SetShaderResources(18, _targets.ResolvedAmbient.ShaderView);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(10, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);

        // Express Gaussian width in the actual reduced target's texels, while
        // retaining its screen-space size across viewport sizes and odd dimensions.
        var sigmaPixels = radius1080 * _height / 1080f;
        constants.BloomPassParameters = new Vector4(1f / width, 0, sigmaPixels * width / _width, 0);
        _context.UpdateSubresource(ref constants, _postProcessBuffer);
        PreparePostPass(_targets.BloomScratch.Target, _bloomBlurShader, width, height);
        _context.PixelShader.SetShaderResources(19, _targets.Bloom.ShaderView);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(19, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);

        constants.BloomPassParameters = new Vector4(0, 1f / height, sigmaPixels * height / _height, 0);
        _context.UpdateSubresource(ref constants, _postProcessBuffer);
        PreparePostPass(_targets.Bloom.Target, _bloomBlurShader, width, height);
        _context.PixelShader.SetShaderResources(19, _targets.BloomScratch.ShaderView);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResources(19, new ShaderResourceView?[] { null });
        _context.OutputMerger.SetTargets((DepthStencilView?)null, (RenderTargetView?)null);
    }

    private void UpdateGradingLut(HeadPreviewPostProcessSettings settings)
    {
        if (_gradingSettings == settings) return;
        var pixels = HeadPreviewPostProcessProfiles.BuildGradingLut(settings);
        _gradingLut ??= HeadPreviewSurface.Color(_device, 256, 16, Format.R32G32B32A32_Float,
            BindFlags.ShaderResource, new SampleDescription(1, 0));
        using var stream = new DataStream(pixels.Length * sizeof(float), true, true);
        stream.WriteRange(pixels);
        _context.UpdateSubresource(new DataBox(stream.DataPointer, 256 * 4 * sizeof(float), 0), _gradingLut.Texture);
        _gradingSettings = settings;
    }

    private static float SrgbToLinear(float value)
    {
        var channel = Math.Clamp(value, 0, 1);
        return channel <= 0.04045f
            ? channel / 12.92f
            : MathF.Pow((channel + 0.055f) / 1.055f, 2.4f);
    }

    private static float ToHdrFloat(float value) => (float)(System.Half)value;

    private void EnsureRenderTargets() =>
        _targets ??= new HeadPreviewRenderTargets(_device, _width, _height, _sampleDescription);

    private byte[] ReadTexture(byte[]? destination)
    {
        _context.CopyResource(_targets.Post.Texture, _targets.Staging.Texture);
        var data = _context.MapSubresource(_targets.Staging.Texture, 0, MapMode.Read, MapFlags.None);
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
            _context.UnmapSubresource(_targets.Staging.Texture, 0);
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

    private Buffer CreateDynamicVertexBuffer(IReadOnlyList<HeadPreviewVertex> vertices)
    {
        var buffer = new Buffer(
            _device,
            checked(vertices.Count * Marshal.SizeOf<GpuVertex>()),
            ResourceUsage.Dynamic,
            BindFlags.VertexBuffer,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            Marshal.SizeOf<GpuVertex>());
        try
        {
            WriteDynamicVertices(buffer, vertices);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private MeshBuffers CreateMeshBuffers(HeadPreviewSceneState.Mesh mesh)
    {
        var vertices = CreateDynamicVertexBuffer(mesh.Vertices);
        try
        {
            return new MeshBuffers(vertices,
                Buffer.Create(_device, BindFlags.IndexBuffer, mesh.Indices.ToArray()), mesh);
        }
        catch
        {
            vertices.Dispose();
            throw;
        }
    }

    private unsafe void WriteDynamicVertices(Buffer buffer, IReadOnlyList<HeadPreviewVertex> vertices)
    {
        var data = _context.MapSubresource(buffer, 0, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            var destination = new Span<GpuVertex>((void*)data.DataPointer, vertices.Count);
            for (var index = 0; index < vertices.Count; index++)
            {
                destination[index] = ToGpuVertex(vertices[index]);
            }
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
            if (colorQuality > 0 && depthQuality > 0)
            {
                // D3D11_STANDARD_MULTISAMPLE_PATTERN: predictable sample positions
                // are required when reconstructing each selected AO depth sample.
                return new SampleDescription(count, -1);
            }
        }
        return new SampleDescription(1, 0);
    }

    private static ShaderBytecode LoadShader(string entryPoint, string profile) =>
        HeadPreviewShaders.Load(entryPoint, profile);

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
        _materials.Clear();
        foreach (var mesh in _meshes)
        {
            mesh.Dispose();
        }
        _meshes.Clear();
    }

    private void PrepareMaterials()
    {
        _materials.Clear();
        foreach (var mesh in _meshes)
        foreach (var section in mesh.Sections)
        {
            var definition = section.Material;
            if (!_materials.ContainsKey(definition))
                _materials.Add(definition, new HeadPreviewPreparedMaterial(definition));
        }
    }

    private HashSet<string> GetActiveTextureKeys()
    {
        var materials = _meshes.SelectMany(mesh => mesh.Sections).Select(section => section.Material);
        return materials.SelectMany(material => material.Textures.Values).Select(texture => texture.Key)
            .Concat(materials.Select(material => material.FixedCubeTexture?.Key).OfType<string>())
            .Concat(materials.Select(material => material.SecondaryFixedCubeTexture?.Key).OfType<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose() => Dispose(releaseScene: true);

    private void Dispose(bool releaseScene)
    {
        if (_disposed)
        {
            return;
        }
        _context?.ClearState();
        _context?.Flush();
        DisposeMeshes();
        if (releaseScene) _sceneState.ClearScene();
        _textureCache?.Dispose();
        _targets?.Dispose();
        _gradingLut?.Dispose();
        _shadowMap?.Dispose();

        _postSampler?.Dispose();
        _materialSampler?.Dispose();
        _depthOnlyBlendState?.Dispose();
        _additiveBlendState?.Dispose();
        _translucentBlendState?.Dispose();
        _opaqueBlendState?.Dispose();
        _depthReadState?.Dispose();
        _depthState?.Dispose();
        _wireframeFrontFacesRasterizer?.Dispose();
        _wireframeBackFacesRasterizer?.Dispose();
        _solidFrontFacesRasterizer?.Dispose();
        _solidBackFacesRasterizer?.Dispose();
        _wireframeRasterizer?.Dispose();
        _solidRasterizer?.Dispose();
        _materialBuffer?.Dispose();
        _shadowBuffer?.Dispose();
        _postProcessBuffer?.Dispose();
        _meshBuffer?.Dispose();
        _skinningBuffer?.Dispose();
        _sceneBuffer?.Dispose();
        _inputLayout?.Dispose();
        _pixelShader?.Dispose();
        _shadowPixelShader?.Dispose();
        _vertexShader?.Dispose();
        _postPixelShader?.Dispose();
        _aoSurfaceShader?.Dispose();
        _ambientOcclusionShader?.Dispose();
        _aoBlurShader?.Dispose();
        _applyAmbientOcclusionShader?.Dispose();
        _bloomExtractShader?.Dispose();
        _bloomBlurShader?.Dispose();
        _postVertexShader?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
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
        public Matrix View;
        public Vector4 CameraPosition;
        public Vector4 KeyLightDirection;
        public Vector4 KeyLightColor;
        public Vector4 FillLightDirection;
        public Vector4 FillLightColor;
        public Vector4 RimLightDirection;
        public Vector4 RimLightColor;
        public Vector4 AmbientColor;
        public Vector4 RenderParameters;
        public Vector4 EnvironmentSh0;
        public Vector4 EnvironmentSh1;
        public Vector4 EnvironmentSh2;
        public Vector4 EnvironmentSh3;
        public Vector4 EnvironmentSh4;
        public Vector4 EnvironmentSh5;
        public Vector4 EnvironmentSh6;
        public Vector4 EnvironmentSh7;
        public Vector4 EnvironmentSh8;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowConstants
    {
        public Matrix LightViewProjection;
        public Vector4 ShadowParameters;
        public Vector4 ShadowFilterParameters;
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
        public Vector4 BloomTint;
        public Vector4 BloomPassParameters;
        public Vector4 AoRadiusParameters;
        public Vector4 AoFilterParameters;
        public Vector4 AoParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshConstants
    {
        public Vector4 SkinningParameters;
    }

    private sealed record MeshBuffers(
        Buffer VertexBuffer,
        Buffer IndexBuffer,
        HeadPreviewSceneState.Mesh CpuMesh) : IDisposable
    {
        public IReadOnlyList<HeadPreviewSection> Sections => CpuMesh.Sections;
        public bool IsAttachment => CpuMesh.IsAttachment;
        public bool ApplySkinning => CpuMesh.ApplySkinning;
        public string Name => CpuMesh.Name;

        public void Dispose()
        {
            IndexBuffer.Dispose();
            VertexBuffer.Dispose();
        }
    }
}
