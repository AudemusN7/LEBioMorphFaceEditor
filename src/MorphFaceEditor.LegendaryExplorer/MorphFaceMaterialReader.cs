using System.Numerics;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using Texture2DClass = LegendaryExplorerCore.Unreal.Classes.Texture2D;
using LecImage = LegendaryExplorerCore.Textures.Image;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Resolves effective materials and decodes bounded, detached texture data from package graphs.</summary>
internal sealed class MorphFaceMaterialReader(
    PackageCache packageCache,
    GamePackageReferenceResolver referenceResolver)
{
    private const long MaximumDecodedTextureBytes = 384L * 1024 * 1024;
    private readonly Dictionary<string, LinkedListNode<DecodedTextureAsset>> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedMaterialMetadata> _materialCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumReferenceShaderMapCacheEntries = 64;
    private const int MaximumLocalShaderMapCacheEntries = 32;
    private readonly ConditionalWeakTable<ExportEntry, LocalShaderMapCache> _shaderMaps = new();
    private readonly Dictionary<ReferenceShaderMapKey, LinkedListNode<CachedReferenceShaderMap>> _referenceShaderMaps = [];
    private readonly LinkedList<CachedReferenceShaderMap> _referenceShaderMapsLru = [];
    private readonly LinkedList<DecodedTextureAsset> _textureCacheLru = new();
    private long _decodedTextureBytes;
    private int _materialCacheHits;
    private int _materialCacheMisses;
    private long _defaultsTicks;
    private long _shaderLookupTicks;
    private long _textureDecodeTicks;

    private enum ReadStage { Defaults, ShaderLookup, TextureDecode }
    private readonly struct ReadTimingScope(MorphFaceMaterialReader owner, ReadStage stage) : IDisposable
    {
        private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - _start;
            switch (stage)
            {
                case ReadStage.Defaults: owner._defaultsTicks += elapsed; break;
                case ReadStage.ShaderLookup: owner._shaderLookupTicks += elapsed; break;
                case ReadStage.TextureDecode: owner._textureDecodeTicks += elapsed; break;
            }
        }
    }

    public MaterialReadMetrics LastReadMetrics { get; private set; } = new(TimeSpan.Zero, TimeSpan.Zero, 0, 0);

    private sealed record TextureMetadata(AssetIdentity Source, MaterialParameterDefinition Definition);
    private sealed record CubeMetadata(AssetIdentity Source, IReadOnlyList<AssetIdentity> Faces);
    private sealed record CachedMaterialMetadata(
        ResolvedHeadMaterial Material,
        IReadOnlyDictionary<string, TextureMetadata> Textures,
        IReadOnlyDictionary<string, TextureMetadata> DefaultTextureOverrides,
        CubeMetadata? FixedCube,
        CubeMetadata? SecondaryCube,
        IReadOnlySet<string> Dependencies);

    private readonly record struct ReferenceShaderMapKey(
        MEGame Game,
        string Path,
        long Length,
        long LastWriteUtcTicks,
        StaticParameterSet Parameters);

    private sealed record CachedReferenceShaderMap(ReferenceShaderMapKey Key, MaterialShaderMap Map);

    private sealed class LocalShaderMapCache
    {
        private readonly Dictionary<StaticParameterSet, LinkedListNode<CachedLocalShaderMap>> _maps = [];
        private readonly LinkedList<CachedLocalShaderMap> _lru = [];

        public bool TryGet(StaticParameterSet parameters, out MaterialShaderMap? map)
        {
            if (!_maps.TryGetValue(parameters, out var node))
            {
                map = null;
                return false;
            }
            _lru.Remove(node);
            _lru.AddFirst(node);
            map = node.Value.Map;
            return true;
        }

        public void Add(StaticParameterSet parameters, MaterialShaderMap? map)
        {
            if (_maps.TryGetValue(parameters, out var existing))
            {
                _lru.Remove(existing);
                _maps.Remove(parameters);
            }
            var node = _lru.AddFirst(new CachedLocalShaderMap(parameters, map));
            _maps.Add(parameters, node);
            if (_maps.Count <= MaximumLocalShaderMapCacheEntries) return;
            var last = _lru.Last!;
            _lru.RemoveLast();
            _maps.Remove(last.Value.Parameters);
        }
    }

    private sealed record CachedLocalShaderMap(StaticParameterSet Parameters, MaterialShaderMap? Map);

    /// <summary>
    /// Reads one material's detached defaults without applying a BioMorphFace
    /// override. This is the package adapter path used by Custom material
    /// evidence; the normal face loader continues to use <see cref="Read"/>.
    /// </summary>
    internal ResolvedHeadMaterial ReadMaterialEvidence(ExportEntry materialExport, bool decodeTextures = true)
    {
        ArgumentNullException.ThrowIfNull(materialExport);
        var metadata = GetMaterialDefaults(materialExport);
        return decodeTextures ? Materialize(metadata, null) : metadata.Material;
    }

    private CachedMaterialMetadata GetMaterialDefaults(ExportEntry materialExport)
    {
        var identity = MorphFacePackageReader.ToIdentity(materialExport)!;
        var key = MaterialIdentityKey.Create(identity);
        if (_materialCache.TryGetValue(key, out var cached))
        {
            VerifyDependencies(cached.Dependencies);
            if (_materialCache.TryGetValue(key, out cached))
            {
                _materialCacheHits++;
                return cached;
            }
        }
        _materialCacheMisses++;
        return _materialCache[key] = ReadMaterialDefaults(ResolveMaterialSource(identity), identity, key);
    }

    internal IReadOnlySet<string> GetEvidenceDependencies(AssetIdentity source) =>
        _materialCache[MaterialIdentityKey.Create(source)].Dependencies;

    // Attachment and Custom evidence share the same terminal-master capabilities.
    internal ResolvedHeadMaterial ReadFaceAttachmentEvidence(ExportEntry materialExport) =>
        ForFaceControls(ReadMaterialEvidence(materialExport));

    private static ResolvedHeadMaterial ForFaceControls(ResolvedHeadMaterial material) => material with
    {
        ContributesFaceControls = material.ContributesFaceControls && material.PreviewModel != HumanMaterialPreviewModel.Kaiden
    };
    public MorphRandomisationMaterialEvidence ReadRandomisationEvidence(
        ExportEntry face,
        IReadOnlyCollection<ExportEntry> baseMaterialExports)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(baseMaterialExports);
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in baseMaterialExports.DistinctBy(value => value.InstancedFullPath))
        {
            var materialScalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var materialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
            var materialTextures = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);
            _ = ReadMaterialChain(
                material, materialScalars, materialVectors, materialTextures,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (var value in materialScalars) scalars[value.Key] = value.Value;
            foreach (var value in materialVectors) vectors[value.Key] = value.Value;
            foreach (var value in materialTextures.Where(value => value.Value.IsA("Texture2D")))
            {
                textures[value.Key] = PccAssetPathPolicy.FromExport(value.Value);
            }
        }

        var (overrides, _) = ReadOverrides(face);
        foreach (var value in overrides.Scalars) scalars[value.Name] = value.Value;
        foreach (var value in overrides.Vectors) vectors[value.Name] = value.Value;
        foreach (var value in overrides.Textures)
        {
            if (value.TextureReference is not null)
            {
                textures[value.Name] = value.TextureReference.InstancedPath;
            }
        }
        return new MorphRandomisationMaterialEvidence(scalars, vectors, textures);
    }

    public MaterialReadResult Read(
        ExportEntry face,
        IReadOnlyCollection<ExportEntry> baseMaterialExports,
        IReadOnlyCollection<ExportEntry> attachmentMaterialExports,
        bool applyFaceOverrides = true,
        bool decodeTextures = true)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(baseMaterialExports);
        ArgumentNullException.ThrowIfNull(attachmentMaterialExports);
        _materialCacheHits = 0;
        _materialCacheMisses = 0;
        _defaultsTicks = _shaderLookupTicks = _textureDecodeTicks = 0;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var (overrides, overrideTextures) = applyFaceOverrides
            ? ReadOverrides(face)
            : (MorphFaceMaterialOverrides.Empty,
                (IReadOnlyDictionary<string, ExportEntry?>)new Dictionary<string, ExportEntry?>(StringComparer.OrdinalIgnoreCase));
        var overridesFinished = System.Diagnostics.Stopwatch.GetTimestamp();
        var warnings = new List<string>();
        var resolved = baseMaterialExports
            .DistinctBy(export => MaterialIdentityKey.Create(MorphFacePackageReader.ToIdentity(export)!))
            .Select(export => ReadMaterial(export, overrides, overrideTextures, warnings, decodeTextures))
            .ToDictionary(material => material.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var materialExport in attachmentMaterialExports.DistinctBy(
                     export => MaterialIdentityKey.Create(MorphFacePackageReader.ToIdentity(export)!)))
        {
            ResolvedHeadMaterial material;
            try
            {
                material = ReadMaterial(materialExport, overrides, overrideTextures, warnings, decodeTextures);
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Attachment material '{materialExport.InstancedFullPath}' could not be read; " +
                    $"using a blank preview material. {exception.Message}");
                material = CreateAttachmentFallback(materialExport);
            }
            resolved[material.Key] = material;
        }
        var finished = System.Diagnostics.Stopwatch.GetTimestamp();
        LastReadMetrics = new MaterialReadMetrics(
            System.Diagnostics.Stopwatch.GetElapsedTime(started, overridesFinished),
            System.Diagnostics.Stopwatch.GetElapsedTime(overridesFinished, finished),
            _materialCacheHits,
            _materialCacheMisses)
        {
            Defaults = System.Diagnostics.Stopwatch.GetElapsedTime(0, _defaultsTicks),
            ShaderLookup = System.Diagnostics.Stopwatch.GetElapsedTime(0, _shaderLookupTicks),
            TextureDecode = System.Diagnostics.Stopwatch.GetElapsedTime(0, _textureDecodeTicks)
        };
        return new MaterialReadResult(overrides, new ResolvedHeadMaterialSet(resolved), warnings);
    }

    private static ResolvedHeadMaterial CreateAttachmentFallback(ExportEntry materialExport)
    {
        var identity = MorphFacePackageReader.ToIdentity(materialExport)!;
        return new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity),
            identity,
            materialExport.ObjectNameString,
            HeadMaterialFamily.Accessory,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            new Dictionary<string, MaterialTextureBinding>());
    }

    public DecodedTextureAsset DecodeTextureReference(
        ExportEntry texture,
        MaterialParameterDefinition definition)
    {
        if (!texture.IsA("Texture2D"))
        {
            throw new InvalidDataException($"'{texture.InstancedFullPath}' is {texture.ClassName}, not Texture2D.");
        }
        return DecodeTexture(MorphFacePackageReader.ToIdentity(texture)!, definition);
    }

    /// <summary>Decodes one authored mip for picker thumbnails without entering the full-preview cache.</summary>
    internal DecodedTextureAsset DecodeTextureThumbnailReference(
        ExportEntry texture,
        MaterialParameterDefinition definition,
        int maximumDimension = 128)
    {
        if (!texture.IsA("Texture2D"))
            throw new InvalidDataException($"'{texture.InstancedFullPath}' is {texture.ClassName}, not Texture2D.");
        if (maximumDimension < 1) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        var current = ResolveTexture(MorphFacePackageReader.ToIdentity(texture)!);
        var typed = new Texture2DClass(current);
        var top = typed.GetTopMip() ?? throw new InvalidDataException($"Texture '{current.InstancedFullPath}' has no readable mip.");
        var sourceFormat = LecImage.getPixelFormatType(typed.TextureFormat);
        var valid = typed.Mips.Where(mip => mip.storageType != StorageTypes.empty && mip.width > 0 && mip.height > 0)
            .OrderByDescending(mip => (long)mip.width * mip.height).ToArray();
        var selected = valid.FirstOrDefault(mip => Math.Max(mip.width, mip.height) <= maximumDimension)
            ?? valid.LastOrDefault()
            ?? top;
        var decodedMip = DecodeMip(selected, current.Game, sourceFormat);
        var alpha = false;
        for (var index = 3; index < decodedMip.Rgba8.Length; index += 4)
            if (decodedMip.Rgba8[index] != byte.MaxValue) { alpha = true; break; }
        var source = MorphFacePackageReader.ToIdentity(current)!;
        var format = current.GetProperty<EnumProperty>("Format")?.Value.Name ?? typed.TextureFormat ?? "Unknown";
        var cacheKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"thumbnail|{MaterialIdentityKey.Create(source)}|{PackageSourceCache.Version(packageCache, source.PackagePath)}|{decodedMip.Width}|{decodedMip.Height}|{format}")));
        return new DecodedTextureAsset(source, decodedMip.Width, decodedMip.Height, decodedMip.Rgba8,
            format, definition.TextureRole, definition.ColorSpace, definition.AlphaPolicy, alpha, cacheKey,
            top.width, top.height, [decodedMip]);
    }

    public void InvalidatePackage(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        _shaderMaps.Clear();
        foreach (var key in _materialCache
                     .Where(pair => pair.Value.Dependencies.Contains(fullPath))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _materialCache.Remove(key);
        }
        foreach (var key in _textureCache
                     .Where(pair => string.Equals(
                         Path.GetFullPath(pair.Value.Value.Source.PackagePath),
                         fullPath,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            if (_textureCache.Remove(key, out var removed))
            {
                _decodedTextureBytes -= GetDecodedByteCount(removed.Value);
                _textureCacheLru.Remove(removed);
            }
        }
    }

    private ResolvedHeadMaterial Materialize(CachedMaterialMetadata metadata, List<string>? warnings)
    {
        var textures = DecodeTextures(metadata.Textures, warnings);
        return metadata.Material with
        {
            Textures = textures,
            DefaultTextures = textures,
            DefaultTextureOverrides = DecodeTextures(metadata.DefaultTextureOverrides, warnings, canonicalReferences: true),
            FixedCubeTexture = MaterializeCube(metadata.FixedCube, warnings),
            SecondaryFixedCubeTexture = MaterializeCube(metadata.SecondaryCube, warnings)
        };
    }

    private Dictionary<string, MaterialTextureBinding> DecodeTextures(
        IReadOnlyDictionary<string, TextureMetadata> sources, List<string>? warnings,
        bool canonicalReferences = false)
    {
        var textures = new Dictionary<string, MaterialTextureBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in sources)
        {
            try
            {
                var texture = DecodeTexture(pair.Value.Source, pair.Value.Definition);
                if (canonicalReferences)
                    texture = texture with { Source = texture.Source with
                    {
                        InstancedPath = PccAssetPathPolicy.FromDonorOccurrence(
                            texture.Source.InstancedPath, texture.Source.PackagePath)
                    }};
                textures[pair.Key] = new MaterialTextureBinding(pair.Key, texture);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                warnings?.Add($"Default texture '{pair.Key}' could not be previewed; inherited/default appearance is used. {exception.Message}");
            }
        }
        return textures;
    }

    private DecodedTextureCubeAsset? MaterializeCube(CubeMetadata? metadata, List<string>? warnings)
    {
        if (metadata is null) return null;
        var definition = new MaterialParameterDefinition("Cube", "Cube", "Internal", MaterialParameterKind.Texture,
            HeadMaterialFamily.Eyes, TextureRole: TextureRole.Other, ColorSpace: TextureColorSpace.Srgb,
            AlphaPolicy: TextureAlphaPolicy.Ignore);
        var faces = new List<DecodedTextureAsset>(metadata.Faces.Count);
        try
        {
            foreach (var identity in metadata.Faces) faces.Add(DecodeTexture(identity, definition));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            warnings?.Add($"Fixed reflection texture could not be previewed. {exception.Message}");
            return null;
        }
        var size = faces[0].Width;
        if (faces.Any(face => face.Width != size || face.Height != size))
            throw new InvalidDataException($"Eye cube '{metadata.Source.InstancedPath}' does not contain six square, equally sized faces.");
        return new DecodedTextureCubeAsset(metadata.Source, size, faces.Select(face => face.Rgba8).ToArray(),
            definition.ColorSpace, $"cube|{MaterialIdentityKey.Create(metadata.Source)}|{string.Join('|', faces.Select(face => face.CacheKey))}", BuildCubeMipChain(faces));
    }

    private ExportEntry ResolveTexture(AssetIdentity identity)
    {
        var package = PackageSourceCache.GetPackage(packageCache, identity.PackagePath)
            ?? throw new FileNotFoundException($"Package '{identity.PackagePath}' is unavailable.");
        IEntry? entry = identity.UIndex > 0 ? package.GetEntry(identity.UIndex) : null;
        if (entry is null || !entry.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase) ||
            !entry.ClassName.Equals(identity.ClassName, StringComparison.OrdinalIgnoreCase))
        {
            entry = package.FindEntry(identity.InstancedPath, identity.ClassName);
        }
        return entry as ExportEntry ?? throw new FileNotFoundException(
            $"Texture '{identity.InstancedPath}' ({identity.ClassName}) was not found in '{identity.PackagePath}'.");
    }

    private ExportEntry ResolveMaterialSource(AssetIdentity identity)
    {
        var package = PackageSourceCache.GetPackage(packageCache, identity.PackagePath)
            ?? throw new FileNotFoundException($"Material source package '{identity.PackagePath}' is unavailable.");
        IEntry? entry = identity.UIndex > 0 ? package.GetEntry(identity.UIndex) : null;
        if (entry is null || !entry.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase) ||
            !entry.ClassName.Equals(identity.ClassName, StringComparison.OrdinalIgnoreCase))
        {
            entry = package.FindEntry(identity.InstancedPath, identity.ClassName);
        }
        return entry as ExportEntry ?? throw new FileNotFoundException(
            $"Material '{identity.InstancedPath}' ({identity.ClassName}) was not found in '{identity.PackagePath}'.");
    }

    private void VerifyDependencies(IReadOnlySet<string> dependencies)
    {
        foreach (var path in dependencies) PackageSourceCache.Verify(packageCache, path);
    }

    private static void AddCubeDependencies(CubeMetadata? cube, HashSet<string> dependencies)
    {
        if (cube is null) return;
        dependencies.Add(Path.GetFullPath(cube.Source.PackagePath));
        foreach (var face in cube.Faces) dependencies.Add(Path.GetFullPath(face.PackagePath));
    }

    private (MorphFaceMaterialOverrides Overrides, IReadOnlyDictionary<string, ExportEntry?> Textures) ReadOverrides(ExportEntry face)
    {
        var property = face.GetProperty<ObjectProperty>("m_oMaterialOverrides", packageCache);
        var materialOverride = ResolveOptional(
            property?.ResolveToEntry(face.FileRef),
            $"BioMorphFace '{face.InstancedFullPath}' m_oMaterialOverrides");
        if (materialOverride is null)
        {
            return (MorphFaceMaterialOverrides.Empty, new Dictionary<string, ExportEntry?>(StringComparer.OrdinalIgnoreCase));
        }

        var properties = materialOverride.GetProperties(packageCache: packageCache);
        var scalars = MorphFacePackageCodec.ReadScalars(properties)
            .Where(value => !string.IsNullOrWhiteSpace(value.Name))
            .ToArray();
        var vectors = properties.GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides")?
            .Select(value => new VectorMaterialOverride(
                value.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty,
                ReadLinearColor(value.GetProp<StructProperty>("cValue"))))
            .Where(value => !string.IsNullOrWhiteSpace(value.Name))
            .ToArray() ?? [];

        var textureExports = new Dictionary<string, ExportEntry?>(StringComparer.OrdinalIgnoreCase);
        var textures = properties.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides")?
            .Where(value => !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(
                value.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty))
            .Select(value =>
            {
                var name = value.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty;
                var textureReference = value.GetProp<ObjectProperty>("m_pTexture");
                var entry = textureReference?.ResolveToEntry(materialOverride.FileRef);
                // A small number of shipped/retained faces reference textures
                // absent from the installed game (for example LE1's unused
                // Salarian Add6 override). Keep the import identity for an
                // unchanged save, but fall back to the inherited material
                // texture when no export can be decoded for preview.
                var explicitlyEmbedded = entry is ExportEntry localTexture &&
                                         localTexture.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                                         PccPackageWorkflow.HasPersistedExplicitTextureLeaf(
                                             materialOverride.FileRef, localTexture.InstancedFullPath);
                var export = explicitlyEmbedded ? (ExportEntry)entry! : referenceResolver.Resolve(entry);
                // A null override means inherit, not a failed texture lookup.
                // Retain the authored row below, but only attempt to apply an
                // actual reference. Nonzero unresolved references still warn.
                if (!string.IsNullOrWhiteSpace(name) && textureReference is { Value: not 0 })
                {
                    textureExports[name] = export;
                }
                // Keep the authored package-local reference identity for save.
                // The resolved export above is only the preview source; replacing
                // an import with that external export identity makes an unchanged
                // override impossible to re-author in the opened PCC.
                var identity = MorphFacePackageReader.ToIdentity(entry);
                return new TextureMaterialOverride(name, explicitlyEmbedded && identity is not null
                    ? identity with { IsExplicitTextureDonor = true }
                    : identity);
            })
            .Where(value => !string.IsNullOrWhiteSpace(value.Name))
            .ToArray() ?? [];

        return (new MorphFaceMaterialOverrides(
            MorphFacePackageReader.ToIdentity(materialOverride),
            scalars,
            vectors,
            textures), textureExports);
    }

    private ResolvedHeadMaterial ReadMaterial(
        ExportEntry materialExport,
        MorphFaceMaterialOverrides overrides,
        IReadOnlyDictionary<string, ExportEntry?> overrideTextures,
        List<string>? warnings = null,
        bool decodeTextures = true)
    {
        var cached = GetMaterialDefaults(materialExport);
        var baseline = ForFaceControls(decodeTextures ? Materialize(cached, warnings) : cached.Material);
        if (baseline.IsPreviewOnlyAttachment || overrides.Scalars.Count == 0 && overrides.Vectors.Count == 0 && overrideTextures.Count == 0)
        {
            return baseline;
        }

        var scalarValues = new Dictionary<string, float>(baseline.Scalars, StringComparer.OrdinalIgnoreCase);
        var vectorValues = new Dictionary<string, Vector4>(baseline.Vectors, StringComparer.OrdinalIgnoreCase);
        var decodedTextures = new Dictionary<string, MaterialTextureBinding>(baseline.Textures, StringComparer.OrdinalIgnoreCase);
        foreach (var value in overrides.Scalars.Where(value => baseline.Supports(value.Name, MaterialParameterKind.Scalar)))
        {
            scalarValues[value.Name] = value.Value;
        }
        foreach (var value in overrides.Vectors.Where(value => baseline.Supports(value.Name, MaterialParameterKind.Vector)))
        {
            vectorValues[value.Name] = value.Value;
        }
        foreach (var pair in overrideTextures.Where(pair => baseline.Supports(pair.Key, MaterialParameterKind.Texture)))
        {
            if (pair.Value is not null)
            {
                var definition = DescribeTexture(pair.Key, baseline.Family, baseline.PreviewModel);
                try
                {
                    if (decodeTextures)
                        decodedTextures[pair.Key] = new MaterialTextureBinding(pair.Key, DecodeTexture(MorphFacePackageReader.ToIdentity(pair.Value)!, definition));
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    warnings?.Add($"Texture override '{pair.Key}' could not be previewed; inherited material texture is shown. {exception.Message}");
                }
            }
            else
            {
                warnings?.Add($"Texture override '{pair.Key}' could not be resolved; inherited material texture is shown.");
            }
        }

        return baseline with
        {
            Scalars = scalarValues,
            Vectors = vectorValues,
            Textures = decodedTextures
        };
    }

    private MaterialShaderMap ReadShaderMap(ExportEntry master)
    {
        using var timing = new ReadTimingScope(this, ReadStage.ShaderLookup);
        var parameters = master.ClassName == "Material"
            ? (StaticParameterSet)ObjectBinary.From<Material>(master).SM3MaterialResource.ID
            : ObjectBinary.From<MaterialInstance>(master).SM3StaticParameterSet;
        if (master.FileRef.FindExport("SeekFreeShaderCache", "ShaderCache") is { } localCache)
        {
            var maps = _shaderMaps.GetValue(localCache, _ => new LocalShaderMapCache());
            if (maps.TryGet(parameters, out var cachedMap))
            {
                if (cachedMap is not null) return cachedMap;
            }
            else
            {
                var map = ShaderMapOnlyReader.Read(localCache, parameters);
                maps.Add(parameters, map);
                if (map is not null) return map;
            }
        }
        return ReadReferenceShaderMap(master.Game, parameters)
            ?? throw new InvalidDataException($"No compiled shader map was found for '{master.InstancedFullPath}'.");
    }

    private MaterialShaderMap? ReadReferenceShaderMap(MEGame game, StaticParameterSet parameters)
    {
        var path = RefShaderCacheReader.ShaderFilePath(game);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        var key = new ReferenceShaderMapKey(game, fullPath, file.Length, file.LastWriteTimeUtc.Ticks, parameters);
        if (_referenceShaderMaps.TryGetValue(key, out var cached))
        {
            _referenceShaderMapsLru.Remove(cached);
            _referenceShaderMapsLru.AddFirst(cached);
            return cached.Value.Map;
        }

        // A changed reference cache must not keep returning maps parsed from
        // its previous contents. Removing stale versions also frees their maps.
        foreach (var staleKey in _referenceShaderMaps.Keys
                     .Where(existing => existing.Game == game &&
                                        existing.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
                                        (existing.Length != file.Length || existing.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks))
                     .ToArray())
        {
            var stale = _referenceShaderMaps[staleKey];
            _referenceShaderMaps.Remove(staleKey);
            _referenceShaderMapsLru.Remove(stale);
        }

        var map = ShaderMapOnlyReader.ReadReferenceCache(fullPath, game, parameters);
        if (map is null) return null;

        var entry = new CachedReferenceShaderMap(key, map);
        var node = _referenceShaderMapsLru.AddFirst(entry);
        _referenceShaderMaps.Add(key, node);
        if (_referenceShaderMaps.Count > MaximumReferenceShaderMapCacheEntries)
        {
            var last = _referenceShaderMapsLru.Last!;
            _referenceShaderMapsLru.RemoveLast();
            _referenceShaderMaps.Remove(last.Value.Key);
        }
        return map;
    }

    private CachedMaterialMetadata ReadMaterialDefaults(
        ExportEntry materialExport,
        AssetIdentity identity,
        string materialKey)
    {
        using var timing = new ReadTimingScope(this, ReadStage.Defaults);
        var scalarValues = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectorValues = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        var textureValues = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = ReadMaterialChain(materialExport, scalarValues, vectorValues, textureValues, visited);
        var parameterSource = root ?? materialExport; // Only provenance/game when the chain has no terminal Material.
        var definition = MorphFacePackageReader.DescribeMaterialMaster(materialExport.Game, root);
        var previewModel = definition?.PreviewModel ?? HumanMaterialPreviewModel.Default;
        // Support, sampler indices and compiled defaults share one evidence owner.
        var shader = new Lazy<MaterialShaderMetadata>(() => StockMaterialMetadata.Find(parameterSource)?.Shader
            ?? MaterialShaderUniformInspector.Inspect(parameterSource, ReadShaderMap(parameterSource)));
        MaterialShaderMetadata GetShader() => shader.Value;
        var uniformTextures = new Lazy<int[]>(() =>
            ObjectBinary.From<Material>(parameterSource).SM3MaterialResource.UniformExpressionTextures);
        if (previewModel == HumanMaterialPreviewModel.CapDecal && parameterSource.Game == MEGame.LE3)
        {
            try
            {
                foreach (var (name, value) in GetShader().ScalarDefaults) scalarValues.TryAdd(name, value);
                foreach (var (name, value) in GetShader().VectorDefaults) vectorValues.TryAdd(name, value);
            }
            catch
            {
                // Retained material expressions still provide conservative
                // values when the cooked compiled map is unavailable.
            }
        }
        var family = definition?.Family ?? HeadMaterialFamily.Unknown;

        if (family == HeadMaterialFamily.AsariSkin)
        {
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__ASA_SkinNoise", 6, "Asari master material");
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__ASA_SpecMultiplierMask", 7, "Asari master material");
        }
        else if (family == HeadMaterialFamily.MaskedHair)
        {
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__PROShort01_Opacity", 0, "PROShort01 hair master");
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__PROShort01_Diffuse", 1, "PROShort01 hair master");
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__PROShort01_Tangent", 2, "PROShort01 hair master");
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__PROShort01_Specular", 3, "PROShort01 hair master");
        }
        if (previewModel == HumanMaterialPreviewModel.CapLe2)
        {
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__CapCrewDiffuse", 1);
        }
        if (previewModel == HumanMaterialPreviewModel.VisorLens)
        {
            // The LE1/LE2 visor lens is an inline additive material with no
            // retained texture-parameter expressions. Bind its compiled
            // sampler table directly so the preview can resolve the same
            // four 2D inputs as the native shader.
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__LensScanline", 0);
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__LensEmission", 1);
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__LensDistortion", 2);
            AddLiteralTexture(parameterSource, uniformTextures.Value, textureValues, GetShader, "__LensFractal", 3);
        }
        if (parameterSource.Game == MEGame.LE1 && family == HeadMaterialFamily.Hair &&
            !textureValues.ContainsKey("HAIR_Diff"))
        {
            // LE1's backported ME3 Shepard-hair master exposes its only live
            // diffuse sampler under the retained expression export name. The
            // LE2/LE3 versions call the same sampler HAIR_Diff. Promote that
            // single proven hair diffuse to the shared semantic name while
            // retaining the anonymous source binding for exact provenance.
            var anonymousDiffuse = textureValues
                .Where(value => value.Key.StartsWith(
                                    "MaterialExpressionTextureSampleParameter2D_",
                                    StringComparison.OrdinalIgnoreCase) &&
                                value.Value.ObjectNameString.Contains("HAIR", StringComparison.OrdinalIgnoreCase) &&
                                value.Value.ObjectNameString.Contains("Diff", StringComparison.OrdinalIgnoreCase))
                .Select(value => value.Value)
                .DistinctBy(value => value.UIndex)
                .Take(2)
                .ToArray();
            if (anonymousDiffuse.Length == 1)
            {
                textureValues["HAIR_Diff"] = anonymousDiffuse[0];
            }
        }

        var textureMetadata = textureValues
            .Where(pair => pair.Value.IsA("Texture2D"))
            .Select(pair =>
            {
                var definition = DescribeTexture(pair.Key, family, previewModel);
                return new KeyValuePair<string, TextureMetadata>(
                    pair.Key, new TextureMetadata(MorphFacePackageReader.ToIdentity(pair.Value)!, definition));
            })
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var isDedicatedAttachmentPreview = definition?.IsAttachmentPreview == true;
        var fixedCubeTexture = isDedicatedAttachmentPreview
            ? ReadFixedCubeTexture(
                parameterSource, uniformTextures.Value, 0, "attachment reflection cube", GetShader)
            : family switch
        {
            HeadMaterialFamily.Eyes => ReadFixedCubeTexture(
                parameterSource, uniformTextures.Value, 0, "Human eye primary reflection cube", GetShader),
            HeadMaterialFamily.KroganEyes => ReadFixedCubeTexture(
                parameterSource, uniformTextures.Value, 0, "Krogan eye reflection cube", GetShader),
            HeadMaterialFamily.SalarianEyes => ReadFixedCubeTexture(
                parameterSource, uniformTextures.Value, 0, "Salarian chrome reflection cube", GetShader),
            HeadMaterialFamily.TurianEyes when parameterSource.Game == MEGame.LE3 => ReadFixedCubeTexture(
                parameterSource, uniformTextures.Value, 0, "LE3 Turian chrome reflection cube", GetShader),
            _ => null
        };
        var secondaryFixedCubeTexture = isDedicatedAttachmentPreview
            ? ReadFixedCubeTexture(
                parameterSource, uniformTextures.Value, 1, "secondary attachment reflection cube", GetShader)
            : family switch
        {
            HeadMaterialFamily.Eyes => ReadFixedCubeTexture(
                parameterSource, uniformTextures.Value, 1, "Human eye secondary reflection cube", GetShader),
            HeadMaterialFamily.SalarianEyes => ReadFixedCubeTexture(
                parameterSource, uniformTextures.Value, 1, "Salarian visor reflection cube", GetShader),
            HeadMaterialFamily.TurianEyes when parameterSource.Game == MEGame.LE3 => ReadFixedCubeTexture(
                parameterSource, uniformTextures.Value, 1, "LE3 Turian parameter reflection cube", GetShader),
            _ => null
        };
        var attachmentCubeParameterName = previewModel == HumanMaterialPreviewModel.Visor
            ? "HMM_HGR_ALL_CUBE"
            : "CubeMap";
        var attachmentCubeOverride = isDedicatedAttachmentPreview
            ? textureValues.GetValueOrDefault(attachmentCubeParameterName)
            : null;
        if (attachmentCubeOverride?.ClassName.Equals("TextureCube", StringComparison.OrdinalIgnoreCase) == true)
        {
            fixedCubeTexture = ReadCubeMetadata(attachmentCubeOverride, "attachment CubeMap parameter");
            secondaryFixedCubeTexture = null;
        }
        MaterialShaderParameterSupport? support = null;
        try
        {
            if (root is not null) support = GetShader().Support;
        }
        catch
        {
            // Some custom materials omit a usable cached shader map. Their
            // recovered graph values remain the conservative support surface.
        }
        var supportedScalars = support?.Scalars ?? scalarValues.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supportedVectors = support?.Vectors ?? vectorValues.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (previewModel == HumanMaterialPreviewModel.CcScalp)
        {
            // Declared in the CC graph, but never read in its recovered pixel passes.
            // Keep the authored value and overrides; only omit the ineffective control.
            supportedScalars = supportedScalars
                .Where(name => !name.Equals("HAIR_Mask_Alpha_Scalar", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        if (family == HeadMaterialFamily.Lashes)
        {
            // The trilogy lash base-pass bytecode reads opacity but has no
            // direct-light permutation. Its expression table retains these two
            // authored parameters even though the executable shader never
            // reads their scalar/vector slots. Preserve their stored values,
            // but do not expose them as working controls.
            supportedScalars = supportedScalars
                .Where(name => !name.Equals("HED_Lash_Spec_Scalar", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            supportedVectors = supportedVectors
                .Where(name => !name.Equals("HED_Lash_Diff_Vector", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var editorDefaultScalars = GetEditorDefaultScalars(parameterSource.Game, family, scalarValues,
            textureMetadata.GetValueOrDefault("HED_Makeup_Mask")?.Source.InstancedPath);
        var defaultTextureOverrides = new Dictionary<string, TextureMetadata>(StringComparer.OrdinalIgnoreCase);
        if (parameterSource.Game == MEGame.LE1 &&
            definition?.HumanFace is HumanFaceMaterialKind.FemaleNpc or HumanFaceMaterialKind.FemalePlayer &&
            string.Equals(textureMetadata.GetValueOrDefault("HED_Makeup_Mask")?.Source.InstancedPath,
                "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black", StringComparison.OrdinalIgnoreCase) &&
            referenceResolver.ResolveInstalled(MEGame.LE1,
                "BIOG_HMF_HED_PROMorph_R.Masks.HMF_HED_PROCustom_MKup_01", "Texture2D") is { } makeupMask)
        {
            // The blank native mask cannot isolate makeup regions. Keep native inheritance
            // available, but Reset must install the normal CC mask and persist that choice.
            defaultTextureOverrides["HED_Makeup_Mask"] = new(MorphFacePackageReader.ToIdentity(makeupMask)!,
                DescribeTexture("HED_Makeup_Mask", family, previewModel));
        }
        var template = new ResolvedHeadMaterial(
            materialKey,
            identity,
            root?.ObjectName.Instanced ?? string.Empty,
            family,
            isDedicatedAttachmentPreview
                ? ReadNativeBlendMode(parameterSource) ?? HumanMaterialProfiles.BlendMode(family)
                : HumanMaterialProfiles.BlendMode(family),
            isDedicatedAttachmentPreview
                ? ReadNativeTwoSided(parameterSource) ?? HumanMaterialProfiles.IsTwoSided(family)
                : HumanMaterialProfiles.IsTwoSided(family),
            scalarValues,
            vectorValues,
            new Dictionary<string, MaterialTextureBinding>())
        {
            DefaultScalars = editorDefaultScalars,
            DefaultVectors = vectorValues,
            DefaultTextures = new Dictionary<string, MaterialTextureBinding>(),
            SupportedScalars = supportedScalars,
            HasCompiledParameterSupport = support is not null,
            PreviewModel = previewModel,
            HumanFace = definition?.HumanFace ?? HumanFaceMaterialKind.None,
            LayeredHairHighlights = definition?.LayeredHairHighlights ?? false,
            IsPreviewOnlyAttachment = definition?.IsAttachmentPreview ?? false,
            ContributesFaceControls = definition is not null && !definition.IsAttachmentPreview,
            SupportedVectors = supportedVectors,
            SupportedTextures = (support?.Textures.AsEnumerable() ?? [])
                .Concat(textureValues.Keys)
                .Where(name => !name.StartsWith("__ASA_", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            FixedCubeTexture = null,
            SecondaryFixedCubeTexture = null
        };
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(identity.PackagePath)
        };
        foreach (var item in visited)
        {
            var separator = item.IndexOf('|');
            if (separator > 0) dependencies.Add(Path.GetFullPath(item[..separator]));
        }
        foreach (var texture in textureMetadata.Values)
            dependencies.Add(Path.GetFullPath(texture.Source.PackagePath));
        foreach (var texture in defaultTextureOverrides.Values)
            dependencies.Add(Path.GetFullPath(texture.Source.PackagePath));
        AddCubeDependencies(fixedCubeTexture, dependencies);
        AddCubeDependencies(secondaryFixedCubeTexture, dependencies);
        dependencies.Add(Path.GetFullPath(parameterSource.FileRef.FilePath));
        return new CachedMaterialMetadata(template, textureMetadata, defaultTextureOverrides,
            fixedCubeTexture, secondaryFixedCubeTexture, dependencies);
    }

    private static HeadMaterialBlendMode? ReadNativeBlendMode(ExportEntry master)
    {
        var value = master.GetProperty<EnumProperty>("BlendMode")?.Value.Name;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains("Masked", StringComparison.OrdinalIgnoreCase)) return HeadMaterialBlendMode.Masked;
        if (value.Contains("Additive", StringComparison.OrdinalIgnoreCase)) return HeadMaterialBlendMode.Additive;
        if (value.Contains("Translucent", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Modulate", StringComparison.OrdinalIgnoreCase))
            return HeadMaterialBlendMode.Translucent;
        if (value.Contains("Opaque", StringComparison.OrdinalIgnoreCase)) return HeadMaterialBlendMode.Opaque;
        return null;
    }

    private static bool? ReadNativeTwoSided(ExportEntry master) =>
        master.GetProperty<BoolProperty>("bTwoSided")?.Value ??
        master.GetProperty<BoolProperty>("TwoSided")?.Value;

    /// <summary>
    /// Provides neutral editor defaults for inherited player-material states
    /// that rely on character-creation inputs. Source scalar values remain
    /// available for provenance, and explicit face overrides take precedence.
    /// </summary>
    internal static IReadOnlyDictionary<string, float> GetEditorDefaultScalars(
        MEGame game,
        HeadMaterialFamily family,
        IReadOnlyDictionary<string, float> materialScalars,
        string? makeupMaskPath = null)
    {
        ArgumentNullException.ThrowIfNull(materialScalars);
        var defaults = new Dictionary<string, float>(materialScalars, StringComparer.OrdinalIgnoreCase);
        // Shared LE2/LE3 human eyes inherit 1.5 for alignment emission. A new
        // diffuse texture must not activate that red channel without authoring it.
        if (game is MEGame.LE2 or MEGame.LE3 && family == HeadMaterialFamily.Eyes &&
            defaults.ContainsKey("Emis_Scalar"))
        {
            defaults["Emis_Scalar"] = 0;
        }
        // LE1 HMF's blank inherited makeup mask is opaque black. Its compiled
        // lip mask is (1 - B) * A, so the inherited strength of one paints lip
        // colour over the entire face after a reset. Start that blank-mask
        // editor state with lipstick off; keep source and explicit face values.
        if (game == MEGame.LE1 && family == HeadMaterialFamily.Skin &&
            defaults.ContainsKey("HED_Lips_Tint_Scalar") &&
            string.Equals(makeupMaskPath, "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black", StringComparison.OrdinalIgnoreCase))
        {
            defaults["HED_Lips_Tint_Scalar"] = 0;
        }
        return defaults;
    }

    private CubeMetadata? ReadFixedCubeTexture(
        ExportEntry master,
        int[] uniformTextures,
        int expressionIndex,
        string label,
        Func<MaterialShaderMetadata> shaderProvider)
    {
        var indices = shaderProvider().CubeIndices;
        int? textureIndex = expressionIndex >= 0 && expressionIndex < indices.Length ? indices[expressionIndex] : null;
        if (textureIndex is null or < 0 || textureIndex >= uniformTextures.Length)
        {
            // Older cooked material resources do not consistently retain the
            // typed cube-expression array. UniformExpressionTextures is still
            // complete and ordered by sampler class, so recover the requested
            // cube from that authoritative table rather than silently dropping
            // fixed reflections from LE1/LE2 eyes.
            textureIndex = uniformTextures
                .Select((uIndex, index) => (Entry: master.FileRef.GetEntry(uIndex), Index: index))
                .Where(item => item.Entry?.ClassName.Equals(
                    "TextureCube", StringComparison.OrdinalIgnoreCase) == true)
                .Skip(expressionIndex)
                .Select(item => (int?)item.Index)
                .FirstOrDefault();
        }
        if (textureIndex is null)
        {
            return null;
        }

        var cube = ResolveOptional(
            master.FileRef.GetEntry(uniformTextures[textureIndex.Value]),
            $"{label} on master '{master.InstancedFullPath}'");
        if (cube is null || !cube.ClassName.Equals("TextureCube", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ReadCubeMetadata(cube, label);
    }

    private CubeMetadata? ReadCubeMetadata(ExportEntry cube, string label)
    {
        string[] faceNames = ["FacePosX", "FaceNegX", "FacePosY", "FaceNegY", "FacePosZ", "FaceNegZ"];
        var properties = cube.GetProperties(packageCache: packageCache);
        var faces = new List<AssetIdentity>(6);
        foreach (var faceName in faceNames)
        {
            var face = ResolveOptional(
                properties.GetProp<ObjectProperty>(faceName)?.ResolveToEntry(cube.FileRef),
                $"{label} '{cube.InstancedFullPath}' {faceName}");
            if (face is null || !face.IsA("Texture2D"))
            {
                return null;
            }
            faces.Add(MorphFacePackageReader.ToIdentity(face)!);
        }
        var identity = MorphFacePackageReader.ToIdentity(cube)!;
        return new CubeMetadata(identity, faces);
    }

    private void AddLiteralTexture(
        ExportEntry master,
        int[] uniformTextures,
        Dictionary<string, ExportEntry> textures,
        Func<MaterialShaderMetadata> shaderProvider,
        string parameterName,
        int uniformIndex,
        string label = "attachment master material")
    {
        var expressions = shaderProvider().TextureIndices;
        if (uniformIndex < 0 || uniformIndex >= expressions.Length) return;
        var textureIndex = expressions[uniformIndex];
        if (textureIndex < 0 || textureIndex >= uniformTextures.Length) return;
        var source = ResolveOptional(
            master.FileRef.GetEntry(uniformTextures[textureIndex]),
            $"{label} '{master.InstancedFullPath}' uniform texture {uniformIndex}");
        if (source is not null) textures[parameterName] = source;
    }

    private static MaterialParameterDefinition DescribeTexture(
        string name, HeadMaterialFamily family, HumanMaterialPreviewModel previewModel = HumanMaterialPreviewModel.Default)
    {
        if (previewModel is >= HumanMaterialPreviewModel.Hat and <= HumanMaterialPreviewModel.VisorLens)
        {
            var upperName = name.ToUpperInvariant();
            if (previewModel == HumanMaterialPreviewModel.VisorLens)
            {
                return new MaterialParameterDefinition(
                    name, name, "Attachment preview", MaterialParameterKind.Texture, HeadMaterialFamily.Accessory,
                    TextureRole: TextureRole.Other,
                    ColorSpace: upperName == "__LENSEMISSION" ? TextureColorSpace.Srgb : TextureColorSpace.Linear,
                    AlphaPolicy: TextureAlphaPolicy.Ignore);
            }
            var packedDataTexture = upperName is "HMN_HAT_DIFF" or "CAP_DIFF";
            var trueDiffuseTexture = upperName is "__CAPCREWDIFFUSE" or "DIFFUSE_MAP";
            var role = packedDataTexture
                ? TextureRole.Other
                : upperName.Contains("NORM", StringComparison.Ordinal)
                ? TextureRole.Normal
                : upperName.Contains("SPEC", StringComparison.Ordinal)
                    ? TextureRole.Specular
                    : upperName.Contains("MASK", StringComparison.Ordinal) || upperName.Contains("OPAC", StringComparison.Ordinal)
                        ? TextureRole.Mask
                        : trueDiffuseTexture
                            ? TextureRole.Diffuse
                            : TextureRole.Other;
            var colorSpace = role == TextureRole.Diffuse ? TextureColorSpace.Srgb : TextureColorSpace.Linear;
            return new MaterialParameterDefinition(
                name, name, "Attachment preview", MaterialParameterKind.Texture, HeadMaterialFamily.Accessory,
                TextureRole: role, ColorSpace: colorSpace,
                AlphaPolicy: role == TextureRole.Mask ? TextureAlphaPolicy.Mask : TextureAlphaPolicy.Ignore);
        }
        if (previewModel == HumanMaterialPreviewModel.Kaiden)
        {
            // Exact native sampler roles for preview, not editable parameter metadata.
            var role = name.ToUpperInvariant() switch
            {
                "HED_FACE_DIFF" => TextureRole.Diffuse,
                "NM" => TextureRole.Normal,
                "SPEC" => TextureRole.Specular,
                _ => TextureRole.Other
            };
            if (role != TextureRole.Other)
                return new MaterialParameterDefinition(name, name, "Internal", MaterialParameterKind.Texture, family,
                    TextureRole: role, ColorSpace: role == TextureRole.Diffuse ? TextureColorSpace.Srgb : TextureColorSpace.Linear);
        }
        return name switch
        {
            "__ASA_SkinNoise" => new MaterialParameterDefinition(
                name, "Asari skin noise", "Internal", MaterialParameterKind.Texture, family,
                TextureRole: TextureRole.Detail, ColorSpace: TextureColorSpace.Srgb),
            "__ASA_SpecMultiplierMask" => new MaterialParameterDefinition(
                name, "Asari specular multiplier mask", "Internal", MaterialParameterKind.Texture, family,
                TextureRole: TextureRole.Specular, ColorSpace: TextureColorSpace.Linear),
            "__PROShort01_Opacity" => new MaterialParameterDefinition(
                name, "PROShort01 opacity", "Internal", MaterialParameterKind.Texture, family,
                TextureRole: TextureRole.Mask, ColorSpace: TextureColorSpace.Linear,
                AlphaPolicy: TextureAlphaPolicy.Mask),
            "__PROShort01_Diffuse" => new MaterialParameterDefinition(
                name, "PROShort01 diffuse", "Internal", MaterialParameterKind.Texture, family,
                TextureRole: TextureRole.Diffuse, ColorSpace: TextureColorSpace.Srgb),
            "__PROShort01_Tangent" => new MaterialParameterDefinition(
                name, "PROShort01 fibre tangent", "Internal", MaterialParameterKind.Texture, family,
                TextureRole: TextureRole.Tangent, ColorSpace: TextureColorSpace.Linear),
            "__PROShort01_Specular" => new MaterialParameterDefinition(
                name, "PROShort01 specular", "Internal", MaterialParameterKind.Texture, family,
                TextureRole: TextureRole.Specular, ColorSpace: TextureColorSpace.Linear),
            _ => HumanMaterialProfiles.Describe(name, MaterialParameterKind.Texture, family)
        };
    }

    private ExportEntry? ReadMaterialChain(
        ExportEntry material,
        Dictionary<string, float> scalars,
        Dictionary<string, Vector4> vectors,
        Dictionary<string, ExportEntry> textures,
        HashSet<string> visited)
    {
        var identity = MaterialIdentityKey.Create(MorphFacePackageReader.ToIdentity(material)!);
        if (!visited.Add(identity))
        {
            return null;
        }

        if (string.Equals(material.ClassName, "Material", StringComparison.OrdinalIgnoreCase))
        {
            ReadBaseExpressions(material, scalars, vectors, textures);
            return material;
        }

        var properties = material.GetProperties(packageCache: packageCache);
        var parentPropertyName = material.IsA("RvrEffectsMaterialUser") ? "m_pBaseMaterial" : "Parent";
        var parent = ResolveOptional(
            properties.GetProp<ObjectProperty>(parentPropertyName)?.ResolveToEntry(material.FileRef),
            $"material '{material.InstancedFullPath}' {parentPropertyName}");
        var master = parent is null ? null : ReadMaterialChain(parent, scalars, vectors, textures, visited);
        ReadScalarValues(properties.GetProp<ArrayProperty<StructProperty>>("ScalarParameterValues"), scalars);
        ReadVectorValues(properties.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues"), vectors);
        ReadTextureValues(properties.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues"), material, textures);
        return master;
    }

    private void ReadBaseExpressions(
        ExportEntry material,
        Dictionary<string, float> scalars,
        Dictionary<string, Vector4> vectors,
        Dictionary<string, ExportEntry> textures)
    {
        if (StockMaterialMetadata.Find(material) is { } stock && referenceResolver.IsOfficialSource(material))
        {
            foreach (var (name, value) in stock.Scalars) scalars[name] = value;
            foreach (var (name, value) in stock.Vectors) vectors[name] = value;
            // Resolve logical defaults against this package, then use the normal donor/freshness policy.
            // Never retain audit-time file paths or UIndexes in the generated data.
            foreach (var (name, value) in stock.Textures)
            {
                var entry = material.FileRef.FindEntry(value.Path, value.ClassName);
                if (entry is null && value.Path.StartsWith(material.FileRef.FileNameNoExtension + ".", StringComparison.OrdinalIgnoreCase))
                    entry = material.FileRef.FindEntry(value.Path[(material.FileRef.FileNameNoExtension.Length + 1)..], value.ClassName);
                if (ResolveOptional(entry, $"master '{material.InstancedFullPath}' default texture '{name}'") is { } texture)
                    textures[name] = texture;
            }
            return;
        }
        var expressions = material.GetProperty<ArrayProperty<ObjectProperty>>("Expressions", packageCache);
        if (expressions is null)
        {
            return;
        }
        foreach (var reference in expressions)
        {
            var expression = ResolveOptional(
                reference.ResolveToEntry(material.FileRef),
                $"material '{material.InstancedFullPath}' expression");
            if (expression is null)
            {
                continue;
            }
            var properties = expression.GetProperties(packageCache: packageCache);
            var name = properties.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            if (expression.IsA("MaterialExpressionScalarParameter"))
            {
                scalars[name] = properties.GetProp<FloatProperty>("DefaultValue")?.Value ?? 0;
            }
            else if (expression.IsA("MaterialExpressionVectorParameter"))
            {
                vectors[name] = ReadLinearColor(properties.GetProp<StructProperty>("DefaultValue"));
            }
            else if (expression.IsA("MaterialExpressionTextureSampleParameter"))
            {
                var texture = ResolveOptional(
                    properties.GetProp<ObjectProperty>("Texture")?.ResolveToEntry(expression.FileRef),
                    $"material expression '{expression.InstancedFullPath}' texture '{name}'");
                if (texture is not null)
                {
                    textures[name] = texture;
                }
            }
        }
    }

    private void ReadTextureValues(
        ArrayProperty<StructProperty>? values,
        ExportEntry owner,
        Dictionary<string, ExportEntry> destination)
    {
        if (values is null)
        {
            return;
        }
        foreach (var value in values)
        {
            var name = value.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
            var texture = ResolveOptional(
                value.GetProp<ObjectProperty>("ParameterValue")?.ResolveToEntry(owner.FileRef),
                $"material '{owner.InstancedFullPath}' texture parameter '{name ?? "<unnamed>"}'");
            if (!string.IsNullOrWhiteSpace(name) && texture is not null)
            {
                destination[name] = texture;
            }
        }
    }

    private static void ReadScalarValues(ArrayProperty<StructProperty>? values, Dictionary<string, float> destination)
    {
        if (values is null)
        {
            return;
        }
        foreach (var value in values)
        {
            var name = value.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
            if (!string.IsNullOrWhiteSpace(name))
            {
                destination[name] = value.GetProp<FloatProperty>("ParameterValue")?.Value ?? 0;
            }
        }
    }

    private static void ReadVectorValues(ArrayProperty<StructProperty>? values, Dictionary<string, Vector4> destination)
    {
        if (values is null)
        {
            return;
        }
        foreach (var value in values)
        {
            var name = value.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
            if (!string.IsNullOrWhiteSpace(name))
            {
                destination[name] = ReadLinearColor(value.GetProp<StructProperty>("ParameterValue"));
            }
        }
    }

    private DecodedTextureAsset DecodeTexture(AssetIdentity source, MaterialParameterDefinition definition)
    {
        using var timing = new ReadTimingScope(this, ReadStage.TextureDecode);
        var export = ResolveTexture(source);
        source = MorphFacePackageReader.ToIdentity(export)!;
        var sourceKey = MaterialIdentityKey.Create(source);
        if (_textureCache.TryGetValue(sourceKey, out var cached))
        {
            _textureCacheLru.Remove(cached);
            _textureCacheLru.AddLast(cached);
            return cached.Value with
            {
                Role = definition.TextureRole,
                ColorSpace = definition.ColorSpace,
                AlphaPolicy = definition.AlphaPolicy
            };
        }

        var texture = new Texture2DClass(export);
        var topMip = texture.GetTopMip() ?? throw new InvalidDataException($"Texture '{export.InstancedFullPath}' has no readable mip.");
        var sourceFormat = LecImage.getPixelFormatType(texture.TextureFormat);
        var authoredMips = texture.Mips
            .Where(candidate => candidate.storageType != StorageTypes.empty &&
                                candidate.width > 0 && candidate.height > 0 &&
                                candidate.width <= topMip.width && candidate.height <= topMip.height)
            .OrderByDescending(candidate => (long)candidate.width * candidate.height)
            .DistinctBy(candidate => (candidate.width, candidate.height))
            .Select(mip => DecodeMip(mip, export.Game, sourceFormat))
            .ToArray();
        if (authoredMips.Length == 0)
        {
            authoredMips = [DecodeMip(topMip, export.Game, sourceFormat)];
        }
        var top = authoredMips[0];
        var rgba = top.Rgba8;
        var meaningfulAlpha = false;
        for (var index = 3; index < rgba.Length; index += 4)
        {
            if (rgba[index] != byte.MaxValue)
            {
                meaningfulAlpha = true;
                break;
            }
        }
        var format = export.GetProperty<EnumProperty>("Format")?.Value.Name ?? texture.TextureFormat ?? "Unknown";
        var cacheKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{sourceKey}|{PackageSourceCache.Version(packageCache, source.PackagePath)}|{top.Width}|{top.Height}|{authoredMips.Length}|{format}|{export.DataSize}")));
        var decoded = new DecodedTextureAsset(
            source,
            top.Width,
            top.Height,
            rgba,
            format,
            definition.TextureRole,
            definition.ColorSpace,
            definition.AlphaPolicy,
            meaningfulAlpha,
            cacheKey,
            topMip.width,
            topMip.height,
            authoredMips);
        var decodedBytes = GetDecodedByteCount(decoded);
        if (decodedBytes > MaximumDecodedTextureBytes)
        {
            // Large one-off textures can still render for the active material,
            // but must not turn the cache's budget into a soft limit.
            return decoded;
        }
        _textureCache[sourceKey] = _textureCacheLru.AddLast(decoded);
        _decodedTextureBytes += decodedBytes;
        while (_decodedTextureBytes > MaximumDecodedTextureBytes && _textureCacheLru.First is { } expired)
        {
            _textureCacheLru.RemoveFirst();
            _textureCache.Remove(MaterialIdentityKey.Create(expired.Value.Source));
            _decodedTextureBytes -= GetDecodedByteCount(expired.Value);
        }
        return decoded;
    }

    private static DecodedTextureMip DecodeMip(
        Texture2DMipInfo mip,
        MEGame game,
        LegendaryExplorerCore.Textures.PixelFormat sourceFormat)
    {
        var width = mip.width;
        var height = mip.height;
        var rgba = LecImage.convertRawToARGB(
            Texture2DClass.GetTextureData(mip, game),
            ref width,
            ref height,
            sourceFormat);
        // Legendary Explorer exposes ARGB pixels in BGRA byte order; the editor uses RGBA.
        for (var index = 0; index < rgba.Length; index += 4)
        {
            (rgba[index], rgba[index + 2]) = (rgba[index + 2], rgba[index]);
        }
        return new DecodedTextureMip(width, height, rgba);
    }

    private static long GetDecodedByteCount(DecodedTextureAsset texture) =>
        texture.Mips is { Count: > 0 }
            ? texture.Mips.Sum(mip => mip.Rgba8.LongLength)
            : texture.Rgba8.LongLength;

    private static IReadOnlyList<DecodedTextureCubeMip> BuildCubeMipChain(IReadOnlyList<DecodedTextureAsset> faces)
    {
        var commonLevelCount = faces.Min(face => face.Mips?.Count ?? 1);
        var levels = new List<DecodedTextureCubeMip>(commonLevelCount);
        for (var level = 0; level < commonLevelCount; level++)
        {
            var faceLevels = faces.Select(face => face.Mips is { Count: > 0 } ? face.Mips[level] : new DecodedTextureMip(face.Width, face.Height, face.Rgba8)).ToArray();
            var size = faceLevels[0].Width;
            if (faceLevels.Any(face => face.Width != size || face.Height != size))
            {
                break;
            }
            levels.Add(new DecodedTextureCubeMip(size, faceLevels.Select(face => face.Rgba8).ToArray()));
        }
        return levels;
    }

    private ExportEntry? ResolveOptional(IEntry? entry, string purpose) =>
        entry is null ? null : referenceResolver.Require(entry, purpose);

private static bool IsDiffuseParameter(string name) =>
        name.Contains("diff", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("albedo", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("basecolor", StringComparison.OrdinalIgnoreCase);

    private static Vector4 ReadLinearColor(StructProperty? property)
    {
        if (property is null)
        {
            return Vector4.Zero;
        }
        var color = CommonStructs.GetLinearColor(property);
        return new Vector4(color.R, color.G, color.B, color.A);
    }
}

internal sealed record MaterialReadResult(
    MorphFaceMaterialOverrides Overrides,
    ResolvedHeadMaterialSet Materials,
    IReadOnlyList<string> Warnings);

public sealed record MorphRandomisationMaterialEvidence(
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, Vector4> Vectors,
    IReadOnlyDictionary<string, string> Textures);
