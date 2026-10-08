using System.Numerics;
using System.Security.Cryptography;
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
    private readonly Dictionary<string, DecodedTextureAsset> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedMaterialMetadata> _materialCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _textureCacheLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _textureCacheLruNodes = new(StringComparer.OrdinalIgnoreCase);
    private long _decodedTextureBytes;
    private int _materialCacheHits;
    private int _materialCacheMisses;

    public MaterialReadMetrics LastReadMetrics { get; private set; } = new(TimeSpan.Zero, TimeSpan.Zero, 0, 0);

    private sealed record TextureMetadata(AssetIdentity Source, MaterialParameterDefinition Definition);
    private sealed record CubeMetadata(AssetIdentity Source, IReadOnlyList<AssetIdentity> Faces);
    private sealed record CachedMaterialMetadata(
        ResolvedHeadMaterial Material,
        IReadOnlyDictionary<string, TextureMetadata> Textures,
        CubeMetadata? FixedCube,
        CubeMetadata? SecondaryCube,
        IReadOnlySet<string> Dependencies,
        bool TexturesAreDefaults = true);

    /// <summary>
    /// Reads one material's detached defaults without applying a BioMorphFace
    /// override. This is the package adapter path used by Custom material
    /// evidence; the normal face loader continues to use <see cref="Read"/>.
    /// </summary>
    internal ResolvedHeadMaterial ReadMaterialEvidence(ExportEntry materialExport, bool decodeTextures = true)
    {
        ArgumentNullException.ThrowIfNull(materialExport);
        var identity = MorphFacePackageReader.ToIdentity(materialExport)!;
        var materialKey = MaterialIdentityKey.Create(identity);
        if (_materialCache.TryGetValue(materialKey, out var cached))
        {
            VerifyDependencies(cached.Dependencies);
            if (_materialCache.TryGetValue(materialKey, out cached))
            {
                _materialCacheHits++;
                return decodeTextures ? Materialize(cached, null) : cached.Material;
            }
        }

        _materialCacheMisses++;
        var material = ReadMaterialDefaults(ResolveMaterialSource(identity), identity, materialKey);
        _materialCache[materialKey] = material;
        return decodeTextures ? Materialize(material, null) : material.Material;
    }

    internal IReadOnlySet<string> GetEvidenceDependencies(AssetIdentity source) =>
        _materialCache[MaterialIdentityKey.Create(source)].Dependencies;

    // Native attachment controls do not establish Custom Mode editability.
    internal ResolvedHeadMaterial ReadFaceAttachmentEvidence(ExportEntry materialExport) =>
        ForFaceControls(ReadMaterialEvidence(materialExport));

    private static ResolvedHeadMaterial ForFaceControls(ResolvedHeadMaterial material) => material with
    {
        ContributesFaceControls = material.PreviewModel != HumanMaterialPreviewModel.Kaiden
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
                material = IsHatAttachment(materialExport)
                    ? ReadHatPreviewMaterial(materialExport, warnings, decodeTextures)
                    : ReadMaterial(materialExport, overrides, overrideTextures, warnings, decodeTextures);
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
            _materialCacheMisses);
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
        return DecodeTexture(texture, definition);
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
        PackageSourceCache.Verify(packageCache, texture.FileRef.FilePath);
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
        foreach (var key in _materialCache
                     .Where(pair => pair.Value.Dependencies.Contains(fullPath))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _materialCache.Remove(key);
        }
        foreach (var key in _textureCache
                     .Where(pair => string.Equals(
                         Path.GetFullPath(pair.Value.Source.PackagePath),
                         fullPath,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            if (_textureCache.Remove(key, out var removed))
            {
                _decodedTextureBytes -= GetDecodedByteCount(removed);
            }
            RemoveTextureCacheLru(key);
        }
    }

    private ResolvedHeadMaterial Materialize(CachedMaterialMetadata metadata, List<string>? warnings)
    {
        var textures = new Dictionary<string, MaterialTextureBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata.Textures)
        {
            try
            {
                var texture = DecodeTexture(ResolveTexture(pair.Value.Source), pair.Value.Definition);
                textures[pair.Key] = new MaterialTextureBinding(pair.Key, texture);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                warnings?.Add($"Default texture '{pair.Key}' could not be previewed; inherited/default appearance is used. {exception.Message}");
            }
        }
        var defaults = metadata.TexturesAreDefaults ? textures : new Dictionary<string, MaterialTextureBinding>();
        return metadata.Material with
        {
            Textures = textures,
            DefaultTextures = defaults,
            FixedCubeTexture = MaterializeCube(metadata.FixedCube, warnings),
            SecondaryFixedCubeTexture = MaterializeCube(metadata.SecondaryCube, warnings)
        };
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
            foreach (var identity in metadata.Faces) faces.Add(DecodeTexture(ResolveTexture(identity), definition));
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
        PackageSourceCache.Verify(packageCache, identity.PackagePath);
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
        PackageSourceCache.Verify(packageCache, identity.PackagePath);
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

    private ResolvedHeadMaterial ReadHatPreviewMaterial(
        ExportEntry materialExport,
        List<string>? warnings = null,
        bool decodeTextures = true)
    {
        var identity = MorphFacePackageReader.ToIdentity(materialExport)!;
        var materialKey = MaterialIdentityKey.Create(identity);
        if (_materialCache.TryGetValue(materialKey, out var cached))
        {
            VerifyDependencies(cached.Dependencies);
            if (_materialCache.TryGetValue(materialKey, out cached))
            {
                _materialCacheHits++;
                return (decodeTextures ? Materialize(cached, warnings) : cached.Material) with { IsPreviewOnlyAttachment = true };
            }
        }
        _materialCacheMisses++;
        materialExport = ResolveMaterialSource(identity);
        var textureValues = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var master = ReadTextureChain(
            materialExport,
            textureValues,
            visited) ?? materialExport;
        var diffuse = textureValues
            .Where(pair => pair.Value.IsA("Texture2D") && IsDiffuseParameter(pair.Key))
            .OrderByDescending(pair => pair.Key.Contains("hat", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        var textures = new Dictionary<string, TextureMetadata>(StringComparer.OrdinalIgnoreCase);
        if (diffuse.Value is not null)
        {
            var definition = new MaterialParameterDefinition(
                "Diffuse",
                "Diffuse",
                "Preview",
                MaterialParameterKind.Texture,
                HeadMaterialFamily.Accessory,
                TextureRole: TextureRole.Diffuse,
                ColorSpace: TextureColorSpace.Srgb,
                AlphaPolicy: TextureAlphaPolicy.Ignore,
                Description: "Default hat diffuse used for preview only.");
            textures[definition.Name] = new TextureMetadata(MorphFacePackageReader.ToIdentity(diffuse.Value)!, definition);
        }

        var material = new ResolvedHeadMaterial(
            materialKey,
            identity,
            master.ObjectNameString,
            HeadMaterialFamily.Accessory,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            new Dictionary<string, MaterialTextureBinding>())
        {
            IsPreviewOnlyAttachment = true
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
        foreach (var texture in textures.Values) dependencies.Add(Path.GetFullPath(texture.Source.PackagePath));
        var metadata = new CachedMaterialMetadata(material, textures, null, null, dependencies);
        _materialCache[materialKey] = metadata;
        return (decodeTextures ? Materialize(metadata, warnings) : metadata.Material) with { IsPreviewOnlyAttachment = true };
    }

    private ExportEntry? ReadTextureChain(
        ExportEntry material,
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
            ReadBaseTextureExpressions(material, textures);
            return material;
        }

        var properties = material.GetProperties(packageCache: packageCache);
        var parentPropertyName = material.IsA("RvrEffectsMaterialUser") ? "m_pBaseMaterial" : "Parent";
        var parent = ResolveOptional(
            properties.GetProp<ObjectProperty>(parentPropertyName)?.ResolveToEntry(material.FileRef),
            $"material '{material.InstancedFullPath}' {parentPropertyName}");
        var master = parent is null ? null : ReadTextureChain(parent, textures, visited);
        ReadTextureValues(properties.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues"), material, textures);
        return master;
    }

    private void ReadBaseTextureExpressions(ExportEntry material, Dictionary<string, ExportEntry> textures)
    {
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
            if (expression is null || !expression.IsA("MaterialExpressionTextureSampleParameter"))
            {
                continue;
            }

            var properties = expression.GetProperties(packageCache: packageCache);
            var name = properties.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
            var texture = ResolveOptional(
                properties.GetProp<ObjectProperty>("Texture")?.ResolveToEntry(expression.FileRef),
                $"material expression '{expression.InstancedFullPath}' texture");
            if (!string.IsNullOrWhiteSpace(name) && texture is not null)
            {
                textures[name] = texture;
            }
        }
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
        var scalars = properties.GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides")?
            .Select(value => new ScalarMaterialOverride(
                value.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty,
                value.GetProp<FloatProperty>("sValue")?.Value ?? 0))
            .Where(value => !string.IsNullOrWhiteSpace(value.Name))
            .ToArray() ?? [];
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
        var identity = MorphFacePackageReader.ToIdentity(materialExport)!;
        var materialKey = MaterialIdentityKey.Create(identity);
        if (!_materialCache.TryGetValue(materialKey, out var cached))
        {
            _materialCacheMisses++;
            cached = ReadMaterialDefaults(ResolveMaterialSource(identity), identity, materialKey);
            _materialCache[materialKey] = cached;
        }
        else
        {
            VerifyDependencies(cached.Dependencies);
            if (_materialCache.TryGetValue(materialKey, out cached))
            {
                _materialCacheHits++;
            }
            else
            {
                _materialCacheMisses++;
                cached = ReadMaterialDefaults(ResolveMaterialSource(identity), identity, materialKey);
                _materialCache[materialKey] = cached;
            }
        }

        var baseline = ForFaceControls(decodeTextures ? Materialize(cached, warnings) : cached.Material);
        if (overrides.Scalars.Count == 0 && overrides.Vectors.Count == 0 && overrideTextures.Count == 0)
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
                        decodedTextures[pair.Key] = new MaterialTextureBinding(pair.Key, DecodeTexture(pair.Value, definition));
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

    private CachedMaterialMetadata ReadMaterialDefaults(
        ExportEntry materialExport,
        AssetIdentity identity,
        string materialKey)
    {
        var scalarValues = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectorValues = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        var textureValues = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var master = ReadMaterialChain(materialExport, scalarValues, vectorValues, textureValues, visited)
            ?? materialExport;
        var previewModel = master.Game == MEGame.LE3
            ? HumanMaterialProfiles.Le3PreviewModelForMaster(master.ClassName, PccAssetPathPolicy.FromExport(master))
            : HumanMaterialPreviewModel.Default;
        // These consumers inspect the same compiled map. Share it for this read only.
        var shaderMap = new Lazy<MaterialShaderMap>(() =>
            ShaderCacheManipulator.GetMaterialShaderMapAndShaders(master).Item1);
        MaterialShaderMap GetShaderMap() => shaderMap.Value;
        // The effective master is the only family authority here. In
        // particular, an imported/custom material name must never select a
        // family merely because it resembles a stock mesh or character name.
        var family = HumanMaterialProfiles.ClassifyMaster(master.ObjectNameString);
        if (family == HeadMaterialFamily.Unknown)
        {
            // Some game builds use a master name variant not present in the
            // compact schema map. Classify that effective master only; never
            // fall back to the child MIC that happened to reference it.
            family = HeadMaterialClassifier.Classify(master.InstancedFullPath);
        }

        if (family == HeadMaterialFamily.AsariSkin)
        {
            AddAsariLiteralTextures(master, textureValues, GetShaderMap);
        }
        else if (family == HeadMaterialFamily.MaskedHair)
        {
            AddMaskedHairLiteralTextures(master, textureValues, GetShaderMap);
        }
        if (master.Game == MEGame.LE1 && family == HeadMaterialFamily.Hair &&
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
        var fixedCubeTexture = family switch
        {
            HeadMaterialFamily.Eyes => ReadFixedCubeTexture(
                master, 0, family, "__HUM_Eye_Primary_Cube", "Human eye primary reflection cube", GetShaderMap),
            HeadMaterialFamily.KroganEyes => ReadFixedCubeTexture(
                master, 0, family, "__KRO_Eye_Cube", "Krogan eye reflection cube", GetShaderMap),
            HeadMaterialFamily.SalarianEyes => ReadFixedCubeTexture(
                master, 0, family, "__SAL_Eye_Chrome_Cube", "Salarian chrome reflection cube", GetShaderMap),
            HeadMaterialFamily.TurianEyes when master.Game == MEGame.LE3 => ReadFixedCubeTexture(
                master, 0, family, "__TUR_Eye_Chrome_Cube", "LE3 Turian chrome reflection cube", GetShaderMap),
            _ => null
        };
        var secondaryFixedCubeTexture = family switch
        {
            HeadMaterialFamily.Eyes => ReadFixedCubeTexture(
                master, 1, family, "__HUM_Eye_Secondary_Cube", "Human eye secondary reflection cube", GetShaderMap),
            HeadMaterialFamily.SalarianEyes => ReadFixedCubeTexture(
                master, 1, family, "__SAL_Eye_Visor_Cube", "Salarian visor reflection cube", GetShaderMap),
            HeadMaterialFamily.TurianEyes when master.Game == MEGame.LE3 => ReadFixedCubeTexture(
                master, 1, family, "__TUR_Eye_Parameter_Cube", "LE3 Turian parameter reflection cube", GetShaderMap),
            _ => null
        };
        MaterialShaderParameterSupport? support = null;
        try
        {
            support = MaterialShaderUniformInspector.InspectSupport(master, GetShaderMap);
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
        var editorDefaultScalars = GetEditorDefaultScalars(master.Game, family, scalarValues);
        if (master.Game == MEGame.LE1 && family == HeadMaterialFamily.Skin &&
            editorDefaultScalars.ContainsKey("HED_Lips_Tint_Scalar") &&
            textureMetadata.TryGetValue("HED_Makeup_Mask", out var makeupMask) &&
            makeupMask.Source.InstancedPath.Equals("BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black", StringComparison.OrdinalIgnoreCase))
        {
            editorDefaultScalars = new Dictionary<string, float>(editorDefaultScalars, StringComparer.OrdinalIgnoreCase)
            {
                ["HED_Lips_Tint_Scalar"] = 0
            };
        }
        var template = new ResolvedHeadMaterial(
            materialKey,
            identity,
            master.ObjectNameString,
            family,
            HumanMaterialProfiles.BlendMode(family),
            HumanMaterialProfiles.IsTwoSided(family),
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
        AddCubeDependencies(fixedCubeTexture, dependencies);
        AddCubeDependencies(secondaryFixedCubeTexture, dependencies);
        dependencies.Add(Path.GetFullPath(master.FileRef.FilePath));
        return new CachedMaterialMetadata(template, textureMetadata, fixedCubeTexture, secondaryFixedCubeTexture, dependencies);
    }

    /// <summary>
    /// Provides neutral editor defaults for inherited player-material states
    /// that rely on character-creation inputs. Source scalar values remain
    /// available for provenance, and explicit face overrides take precedence.
    /// </summary>
    internal static IReadOnlyDictionary<string, float> GetEditorDefaultScalars(
        MEGame game,
        HeadMaterialFamily family,
        IReadOnlyDictionary<string, float> materialScalars,
        IReadOnlyDictionary<string, MaterialTextureBinding>? materialTextures = null)
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
            materialTextures?.GetValueOrDefault("HED_Makeup_Mask")?.Texture.Source.InstancedPath
                .Equals("BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black", StringComparison.OrdinalIgnoreCase) == true)
        {
            defaults["HED_Lips_Tint_Scalar"] = 0;
        }
        return defaults;
    }

    private CubeMetadata? ReadFixedCubeTexture(
        ExportEntry master,
        int expressionIndex,
        HeadMaterialFamily family,
        string parameterName,
        string label,
        Func<MaterialShaderMap> shaderMapProvider)
    {
        var material = ObjectBinary.From<Material>(master);
        var uniformTextures = material.SM3MaterialResource.UniformExpressionTextures;
        MaterialUniformExpressionTexture[]? expressions;
        if (master.Game is MEGame.ME1 or MEGame.ME2)
        {
            expressions = material.SM3MaterialResource.UniformCubeTextureExpressions;
        }
        else
        {
            var shaderMap = shaderMapProvider();
            expressions = shaderMap.UniformCubeTextureExpressions;
        }
        var expression = expressions is not null && expressionIndex >= 0 && expressionIndex < expressions.Length
            ? expressions[expressionIndex]
            : null;
        var textureIndex = expression?.TextureIndex;
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

        string[] faceNames = ["FacePosX", "FaceNegX", "FacePosY", "FaceNegY", "FacePosZ", "FaceNegZ"];
        var properties = cube.GetProperties(packageCache: packageCache);
        var faces = new List<AssetIdentity>(6);
        foreach (var faceName in faceNames)
        {
            var face = ResolveOptional(
                properties.GetProp<ObjectProperty>(faceName)?.ResolveToEntry(cube.FileRef),
                $"Eye cube '{cube.InstancedFullPath}' {faceName}");
            if (face is null || !face.IsA("Texture2D"))
            {
                return null;
            }
            faces.Add(MorphFacePackageReader.ToIdentity(face)!);
        }
        var identity = MorphFacePackageReader.ToIdentity(cube)!;
        return new CubeMetadata(identity, faces);
    }

    private void AddAsariLiteralTextures(
        ExportEntry master,
        Dictionary<string, ExportEntry> textures,
        Func<MaterialShaderMap> shaderMapProvider)
    {
        var material = ObjectBinary.From<Material>(master);
        var uniformTextures = material.SM3MaterialResource.UniformExpressionTextures;
        var shaderMap = shaderMapProvider();
        AddLiteral("__ASA_SkinNoise", 6);
        AddLiteral("__ASA_SpecMultiplierMask", 7);
        return;

        void AddLiteral(string name, int uniformIndex)
        {
            if (uniformIndex >= shaderMap.Uniform2DTextureExpressions.Length)
            {
                return;
            }
            var textureIndex = shaderMap.Uniform2DTextureExpressions[uniformIndex].TextureIndex;
            if (textureIndex < 0 || textureIndex >= uniformTextures.Length)
            {
                return;
            }
            var source = ResolveOptional(
                master.FileRef.GetEntry(uniformTextures[textureIndex]),
                $"Asari master material '{master.InstancedFullPath}' uniform texture {uniformIndex}");
            if (source is not null)
            {
                textures[name] = source;
            }
        }
    }

    private void AddMaskedHairLiteralTextures(
        ExportEntry master,
        Dictionary<string, ExportEntry> textures,
        Func<MaterialShaderMap> shaderMapProvider)
    {
        var material = ObjectBinary.From<Material>(master);
        var uniformTextures = material.SM3MaterialResource.UniformExpressionTextures;
        var shaderMap = shaderMapProvider();
        AddLiteral("__PROShort01_Opacity", 0);
        AddLiteral("__PROShort01_Diffuse", 1);
        AddLiteral("__PROShort01_Tangent", 2);
        AddLiteral("__PROShort01_Specular", 3);
        return;

        void AddLiteral(string name, int uniformIndex)
        {
            if (uniformIndex >= shaderMap.Uniform2DTextureExpressions.Length)
            {
                return;
            }
            var textureIndex = shaderMap.Uniform2DTextureExpressions[uniformIndex].TextureIndex;
            if (textureIndex < 0 || textureIndex >= uniformTextures.Length)
            {
                return;
            }
            var source = ResolveOptional(
                master.FileRef.GetEntry(uniformTextures[textureIndex]),
                $"PROShort01 hair master '{master.InstancedFullPath}' uniform texture {uniformIndex}");
            if (source is not null)
            {
                textures[name] = source;
            }
        }
    }

    private static MaterialParameterDefinition DescribeTexture(
        string name, HeadMaterialFamily family, HumanMaterialPreviewModel previewModel = HumanMaterialPreviewModel.Default)
    {
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

    private DecodedTextureAsset DecodeTexture(ExportEntry export, MaterialParameterDefinition definition)
    {
        var source = MorphFacePackageReader.ToIdentity(export)!;
        PackageSourceCache.Verify(packageCache, source.PackagePath);
        export = ResolveTexture(source);
        var sourceKey = MaterialIdentityKey.Create(source);
        if (_textureCache.TryGetValue(sourceKey, out var cached))
        {
            TouchTextureCache(sourceKey);
            return cached with
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
        _textureCache[sourceKey] = decoded;
        var node = _textureCacheLru.AddLast(sourceKey);
        _textureCacheLruNodes[sourceKey] = node;
        _decodedTextureBytes += decodedBytes;
        while (_decodedTextureBytes > MaximumDecodedTextureBytes && _textureCacheLru.Count > 0)
        {
            var expiredKey = _textureCacheLru.First!.Value;
            RemoveTextureCacheLru(expiredKey);
            if (_textureCache.Remove(expiredKey, out var expired))
            {
                _decodedTextureBytes -= GetDecodedByteCount(expired);
            }
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

    private void TouchTextureCache(string key)
    {
        if (!_textureCacheLruNodes.TryGetValue(key, out var node)) return;
        _textureCacheLru.Remove(node);
        _textureCacheLruNodes[key] = _textureCacheLru.AddLast(key);
    }

    private void RemoveTextureCacheLru(string key)
    {
        if (_textureCacheLruNodes.Remove(key, out var node)) _textureCacheLru.Remove(node);
    }

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

    private static bool IsHatAttachment(ExportEntry material) =>
        material.InstancedFullPath.Contains("_hat_", StringComparison.OrdinalIgnoreCase) ||
        material.ObjectNameString.Contains("hat", StringComparison.OrdinalIgnoreCase);

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
