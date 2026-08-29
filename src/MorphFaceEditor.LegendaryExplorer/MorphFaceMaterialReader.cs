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
    private readonly Dictionary<string, ResolvedHeadMaterial> _materialCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _textureCacheOrder = new();
    private long _decodedTextureBytes;
    private int _materialCacheHits;
    private int _materialCacheMisses;

    public MaterialReadMetrics LastReadMetrics { get; private set; } = new(TimeSpan.Zero, TimeSpan.Zero, 0, 0);

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
                textures[value.Key] = value.Value.InstancedFullPath;
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
        bool applyFaceOverrides = true)
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
        var resolved = baseMaterialExports
            .DistinctBy(export => MaterialIdentityKey.Create(MorphFacePackageReader.ToIdentity(export)!))
            .Select(export => ReadMaterial(export, overrides, overrideTextures))
            .ToDictionary(material => material.Key, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var materialExport in attachmentMaterialExports.DistinctBy(
                     export => MaterialIdentityKey.Create(MorphFacePackageReader.ToIdentity(export)!)))
        {
            ResolvedHeadMaterial material;
            try
            {
                material = IsHatAttachment(materialExport)
                    ? ReadHatPreviewMaterial(materialExport)
                    : ReadMaterial(materialExport, overrides, overrideTextures);
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

    public void InvalidatePackage(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        foreach (var key in _materialCache
                     .Where(pair => string.Equals(
                         Path.GetFullPath(pair.Value.Source.PackagePath),
                         fullPath,
                         StringComparison.OrdinalIgnoreCase))
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
                EvictMaterialsReferencing(removed);
            }
        }
        if (_textureCacheOrder.Count > 0)
        {
            var retained = _textureCacheOrder.Where(_textureCache.ContainsKey).ToArray();
            _textureCacheOrder.Clear();
            foreach (var key in retained)
            {
                _textureCacheOrder.Enqueue(key);
            }
        }
    }

    private ResolvedHeadMaterial ReadHatPreviewMaterial(ExportEntry materialExport)
    {
        var identity = MorphFacePackageReader.ToIdentity(materialExport)!;
        var materialKey = MaterialIdentityKey.Create(identity);
        if (_materialCache.TryGetValue(materialKey, out var cached))
        {
            _materialCacheHits++;
            return cached;
        }
        _materialCacheMisses++;
        var textureValues = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);
        var master = ReadTextureChain(
            materialExport,
            textureValues,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)) ?? materialExport;
        var diffuse = textureValues
            .Where(pair => pair.Value.IsA("Texture2D") && IsDiffuseParameter(pair.Key))
            .OrderByDescending(pair => pair.Key.Contains("hat", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        var decodedTextures = new Dictionary<string, MaterialTextureBinding>(StringComparer.OrdinalIgnoreCase);
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
            decodedTextures[definition.Name] = new MaterialTextureBinding(
                definition.Name,
                DecodeTexture(diffuse.Value, definition));
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
            decodedTextures);
        _materialCache[materialKey] = material;
        return material;
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
            .Select(value =>
            {
                var name = value.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty;
                var entry = value.GetProp<ObjectProperty>("m_pTexture")?.ResolveToEntry(materialOverride.FileRef);
                // A small number of shipped/retained faces reference textures
                // absent from the installed game (for example LE1's unused
                // Salarian Add6 override). Keep the import identity for an
                // unchanged save, but fall back to the inherited material
                // texture when no export can be decoded for preview.
                var export = referenceResolver.Resolve(entry);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    textureExports[name] = export;
                }
                // Keep the authored package-local reference identity for save.
                // The resolved export above is only the preview source; replacing
                // an import with that external export identity makes an unchanged
                // override impossible to re-author in the opened PCC.
                return new TextureMaterialOverride(name, MorphFacePackageReader.ToIdentity(entry));
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
        IReadOnlyDictionary<string, ExportEntry?> overrideTextures)
    {
        var identity = MorphFacePackageReader.ToIdentity(materialExport)!;
        var materialKey = MaterialIdentityKey.Create(identity);
        if (!_materialCache.TryGetValue(materialKey, out var baseline))
        {
            _materialCacheMisses++;
            baseline = ReadMaterialDefaults(materialExport, identity, materialKey);
            _materialCache[materialKey] = baseline;
        }
        else
        {
            _materialCacheHits++;
        }

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
                var definition = HumanMaterialProfiles.Describe(pair.Key, MaterialParameterKind.Texture, baseline.Family);
                decodedTextures[pair.Key] = new MaterialTextureBinding(pair.Key, DecodeTexture(pair.Value, definition));
            }
        }

        return baseline with
        {
            Scalars = scalarValues,
            Vectors = vectorValues,
            Textures = decodedTextures
        };
    }

    private ResolvedHeadMaterial ReadMaterialDefaults(
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
        var family = HumanMaterialProfiles.ClassifyMaster(master.ObjectNameString);
        if (family == HeadMaterialFamily.Unknown)
        {
            family = InferFamily(materialExport.InstancedFullPath);
        }

        if (family == HeadMaterialFamily.AsariSkin)
        {
            AddAsariLiteralTextures(master, textureValues);
        }
        else if (family == HeadMaterialFamily.MaskedHair)
        {
            AddMaskedHairLiteralTextures(master, textureValues);
        }

        var decodedTextures = textureValues
            .Where(pair => pair.Value.IsA("Texture2D"))
            .Select(pair =>
            {
                var definition = DescribeTexture(pair.Key, family);
                return new MaterialTextureBinding(pair.Key, DecodeTexture(pair.Value, definition));
            })
            .ToDictionary(binding => binding.ParameterName, StringComparer.OrdinalIgnoreCase);
        var fixedCubeTexture = family switch
        {
            HeadMaterialFamily.Eyes => ReadFixedCubeTexture(
                master, 0, family, "__HUM_Eye_Primary_Cube", "Human eye primary reflection cube"),
            HeadMaterialFamily.KroganEyes => ReadFixedCubeTexture(
                master, 0, family, "__KRO_Eye_Cube", "Krogan eye reflection cube"),
            HeadMaterialFamily.SalarianEyes => ReadFixedCubeTexture(
                master, 0, family, "__SAL_Eye_Chrome_Cube", "Salarian chrome reflection cube"),
            HeadMaterialFamily.TurianEyes when master.Game == MEGame.LE3 => ReadFixedCubeTexture(
                master, 0, family, "__TUR_Eye_Chrome_Cube", "LE3 Turian chrome reflection cube"),
            _ => null
        };
        var secondaryFixedCubeTexture = family switch
        {
            HeadMaterialFamily.Eyes => ReadFixedCubeTexture(
                master, 1, family, "__HUM_Eye_Secondary_Cube", "Human eye secondary reflection cube"),
            HeadMaterialFamily.SalarianEyes => ReadFixedCubeTexture(
                master, 1, family, "__SAL_Eye_Visor_Cube", "Salarian visor reflection cube"),
            HeadMaterialFamily.TurianEyes when master.Game == MEGame.LE3 => ReadFixedCubeTexture(
                master, 1, family, "__TUR_Eye_Parameter_Cube", "LE3 Turian parameter reflection cube"),
            _ => null
        };
        MaterialShaderParameterSupport? support = null;
        try
        {
            support = MaterialShaderUniformInspector.InspectSupport(master);
        }
        catch
        {
            // Some custom materials omit a usable cached shader map. Their
            // recovered graph values remain the conservative support surface.
        }
        var supportedScalars = support?.Scalars ?? scalarValues.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supportedVectors = support?.Vectors ?? vectorValues.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        return new ResolvedHeadMaterial(
            materialKey,
            identity,
            master.ObjectNameString,
            family,
            HumanMaterialProfiles.BlendMode(family),
            HumanMaterialProfiles.IsTwoSided(family),
            scalarValues,
            vectorValues,
            decodedTextures)
        {
            DefaultScalars = scalarValues,
            DefaultVectors = vectorValues,
            DefaultTextures = decodedTextures,
            SupportedScalars = supportedScalars,
            SupportedVectors = supportedVectors,
            SupportedTextures = (support?.Textures.AsEnumerable() ?? textureValues.Keys)
                .Where(name => !name.StartsWith("__ASA_", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            FixedCubeTexture = fixedCubeTexture,
            SecondaryFixedCubeTexture = secondaryFixedCubeTexture
        };
    }

    private DecodedTextureCubeAsset? ReadFixedCubeTexture(
        ExportEntry master,
        int expressionIndex,
        HeadMaterialFamily family,
        string parameterName,
        string label)
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
            var (shaderMap, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(master);
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
        var definition = new MaterialParameterDefinition(
            parameterName,
            label,
            "Internal",
            MaterialParameterKind.Texture,
            family,
            TextureRole: TextureRole.Other,
            ColorSpace: TextureColorSpace.Srgb,
            AlphaPolicy: TextureAlphaPolicy.Ignore,
            Description: $"Fixed {cube.ObjectNameString} binding recovered from the compiled eye shader.");
        var faces = new List<DecodedTextureAsset>(6);
        foreach (var faceName in faceNames)
        {
            var face = ResolveOptional(
                properties.GetProp<ObjectProperty>(faceName)?.ResolveToEntry(cube.FileRef),
                $"Eye cube '{cube.InstancedFullPath}' {faceName}");
            if (face is null || !face.IsA("Texture2D"))
            {
                return null;
            }
            faces.Add(DecodeTexture(face, definition));
        }

        var size = faces[0].Width;
        if (faces.Any(face => face.Width != size || face.Height != size))
        {
            throw new InvalidDataException($"Eye cube '{cube.InstancedFullPath}' does not contain six square, equally sized faces.");
        }
        var identity = MorphFacePackageReader.ToIdentity(cube)!;
        return new DecodedTextureCubeAsset(
            identity,
            size,
            faces.Select(face => face.Rgba8).ToArray(),
            faces[0].ColorSpace,
            $"cube|{MaterialIdentityKey.Create(identity)}",
            BuildCubeMipChain(faces));
    }

    private void AddAsariLiteralTextures(
        ExportEntry master,
        Dictionary<string, ExportEntry> textures)
    {
        var material = ObjectBinary.From<Material>(master);
        var uniformTextures = material.SM3MaterialResource.UniformExpressionTextures;
        var (shaderMap, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(master);
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
        Dictionary<string, ExportEntry> textures)
    {
        var material = ObjectBinary.From<Material>(master);
        var uniformTextures = material.SM3MaterialResource.UniformExpressionTextures;
        var (shaderMap, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(master);
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

    private static MaterialParameterDefinition DescribeTexture(string name, HeadMaterialFamily family) => name switch
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
        var sourceKey = MaterialIdentityKey.Create(source);
        if (_textureCache.TryGetValue(sourceKey, out var cached))
        {
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
            $"{sourceKey}|{top.Width}|{top.Height}|{authoredMips.Length}|{format}|{export.DataSize}")));
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
        _textureCache[sourceKey] = decoded;
        _textureCacheOrder.Enqueue(sourceKey);
        _decodedTextureBytes += GetDecodedByteCount(decoded);
        while (_decodedTextureBytes > MaximumDecodedTextureBytes && _textureCacheOrder.Count > 1)
        {
            var expiredKey = _textureCacheOrder.Dequeue();
            if (_textureCache.Remove(expiredKey, out var expired))
            {
                _decodedTextureBytes -= GetDecodedByteCount(expired);
                EvictMaterialsReferencing(expired);
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

    private void EvictMaterialsReferencing(DecodedTextureAsset expired)
    {
        foreach (var key in _materialCache
                     .Where(pair =>
                         pair.Value.Textures.Values.Any(binding =>
                             string.Equals(binding.Texture.CacheKey, expired.CacheKey, StringComparison.OrdinalIgnoreCase)) ||
                         CubeReferences(pair.Value.FixedCubeTexture) ||
                         CubeReferences(pair.Value.SecondaryFixedCubeTexture))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _materialCache.Remove(key);
        }

        bool CubeReferences(DecodedTextureCubeAsset? cube) => cube is not null &&
            cube.Rgba8Faces.Any(face => ReferenceEquals(expired.Rgba8, face));
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

    private static HeadMaterialFamily InferFamily(string value)
    {
        if (value.Contains("BAT_HED", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Batarian", StringComparison.OrdinalIgnoreCase))
        {
            return HeadMaterialFamily.BatarianSkin;
        }
        if (value.Contains("KRO_HED", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("KRO_EYE", StringComparison.OrdinalIgnoreCase))
        {
            return value.Contains("EYE", StringComparison.OrdinalIgnoreCase)
                ? HeadMaterialFamily.KroganEyes
                : HeadMaterialFamily.KroganSkin;
        }
        if (value.Contains("TUR_HED", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("TUR_EYE", StringComparison.OrdinalIgnoreCase))
        {
            return value.Contains("EYE", StringComparison.OrdinalIgnoreCase)
                ? HeadMaterialFamily.TurianEyes
                : HeadMaterialFamily.TurianSkin;
        }
        if (value.Contains("SAL_HED", StringComparison.OrdinalIgnoreCase))
        {
            return value.Contains("EYE", StringComparison.OrdinalIgnoreCase)
                ? HeadMaterialFamily.SalarianEyes
                : HeadMaterialFamily.SalarianSkin;
        }
        if (value.Contains("_hat_", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("hat", StringComparison.OrdinalIgnoreCase)) return HeadMaterialFamily.Accessory;
        if (value.Contains("PROShort01", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("PROShort_01", StringComparison.OrdinalIgnoreCase)) return HeadMaterialFamily.MaskedHair;
        if (value.Contains("lash", StringComparison.OrdinalIgnoreCase)) return HeadMaterialFamily.Lashes;
        if (value.Contains("eye", StringComparison.OrdinalIgnoreCase)) return HeadMaterialFamily.Eyes;
        if (value.Contains("scalp", StringComparison.OrdinalIgnoreCase)) return HeadMaterialFamily.Scalp;
        if (value.Contains("hair", StringComparison.OrdinalIgnoreCase) || value.Contains("_hir_", StringComparison.OrdinalIgnoreCase)) return HeadMaterialFamily.Hair;
        if (value.Contains("hed", StringComparison.OrdinalIgnoreCase) || value.Contains("face", StringComparison.OrdinalIgnoreCase)) return HeadMaterialFamily.Skin;
        return HeadMaterialFamily.Accessory;
    }

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
