using System.Numerics;
using System.Diagnostics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Diagnostics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed record TextureReadRequest(
    string PackagePath, string TexturePath, MaterialParameterDefinition Definition);

public sealed record TextureReadResult(DecodedTextureAsset? Texture, Exception? Error);

public sealed class MorphFacePackageReader : IDisposable
{
    private readonly PreviewPackageCache _packageCache = new();
    private readonly GamePackageReferenceResolver _referenceResolver;
    private readonly MorphFaceMaterialReader _materialReader;
    private readonly Dictionary<string, CachedSkeletalMesh> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageFingerprint> _fingerprintCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _meshContentFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public MorphFacePackageReader()
    {
        _referenceResolver = new GamePackageReferenceResolver(_packageCache);
        _materialReader = new MorphFaceMaterialReader(_packageCache, _referenceResolver);
        PackageSourceCache.RegisterInvalidation(_packageCache, InvalidatePackage);
    }

    public MorphFaceLoadMetrics? LastLoadMetrics { get; private set; }
    public PccCacheStatistics PackageCacheStatistics => _packageCache.Statistics;
    internal int MeshContentFingerprintReadCount { get; private set; }

    /// <summary>Reads geometry and rig only; material references and pixels are never resolved.</summary>
    public string? ReadSkeletalMeshContentFingerprint(AssetIdentity identity)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        if (!identity.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return null;
        var fullPath = RequireFile(identity.PackagePath);
        PackageSourceCache.Verify(_packageCache, fullPath);
        var key = fullPath + "|" + identity.InstancedPath;
        if (_meshContentFingerprints.TryGetValue(key, out var cached)) return cached;
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var export = FindDetachedAttachmentExport(package, identity.InstancedPath);
        var binary = export.GetBinaryData<SkeletalMesh>();
        MeshContentFingerprintReadCount++;
        return _meshContentFingerprints[key] = SkeletalMeshContentFingerprint.Compute(binary, package.Game,
            ReadLodMaterialMaps(export, binary.LODModels.Length, binary.Materials?.Length ?? 0));
    }

    public MorphRandomisationMaterialEvidence LoadRandomisationMaterialEvidence(
        string packagePath,
        string exportSelector)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var face = ExportSelector.Find(package, exportSelector, "BioMorphFace");
        // Offline corpus evidence belongs to the reviewed package, not an
        // installed cooked copy whose MIC parents may differ at the same path.
        var headReference = face.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package);
        var baseHead = headReference as ExportEntry ?? (headReference is null
            ? null : _referenceResolver.Require(headReference, "randomisation corpus base head"))
            ?? throw new InvalidDataException($"BioMorphFace '{face.InstancedFullPath}' has no resolvable base head.");
        var materials = new List<ExportEntry>();
        _ = GetSkeletalMesh(baseHead, materials);
        return _materialReader.ReadRandomisationEvidence(face, materials);
    }

    public LoadedMorphFace Load(string packagePath, string exportSelector, bool decodeTextures = true)
    {
        using var cacheOperation = _packageCache.BeginOperation();
        var started = Stopwatch.GetTimestamp();
        var checkpoint = started;
        static TimeSpan Mark(ref long checkpoint)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(checkpoint, now);
            checkpoint = now;
            return elapsed;
        }

        ThrowIfDisposed();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var packageOpenTime = Mark(ref checkpoint);
        var export = ExportSelector.Find(package, exportSelector, "BioMorphFace");
        var document = ReadDocument(export);
        var documentTime = Mark(ref checkpoint);
        var baseHeadEntry = ResolveObjectReference(export, "m_oBaseHead")
            ?? throw new InvalidDataException($"BioMorphFace '{export.InstancedFullPath}' has no resolvable m_oBaseHead SkeletalMesh.");
        if (!string.Equals(baseHeadEntry.ClassName, "SkeletalMesh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"m_oBaseHead resolved to {baseHeadEntry.ClassName}, not SkeletalMesh.");
        }

        var baseMaterialExports = new List<ExportEntry>();
        var baseHead = GetSkeletalMesh(baseHeadEntry, baseMaterialExports);
        var baseHeadTime = Mark(ref checkpoint);
        var referenceWarnings = new List<string>();
        var rawHairEntry = export.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(export.FileRef);
        var directHairEntry = ResolveOptionalMeshReference(
            rawHairEntry,
            $"BioMorphFace '{export.InstancedFullPath}' m_oHairMesh",
            referenceWarnings);
        var otherMeshProperties = export.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes");
        var rawOtherMeshEntries = otherMeshProperties?
            .Select((item, index) => (Entry: item.ResolveToEntry(export.FileRef), Index: index))
            .ToArray() ?? [];
        var otherMeshEntries = rawOtherMeshEntries
            .Where(item => item.Entry is not null)
            .Select(item => (
                Entry: ResolveOptionalMeshReference(
                    item.Entry,
                    $"BioMorphFace '{export.InstancedFullPath}' m_oOtherMeshes[{item.Index}]",
                    referenceWarnings),
                item.Index))
            .Where(item => item.Entry is not null)
            .Select(item => (Entry: item.Entry!, item.Index))
            .ToArray();
        document = document with
        {
            HairMeshReference = ToIdentity(rawHairEntry),
            OtherMeshReferences = rawOtherMeshEntries.Select(item => ToIdentity(item.Entry)).ToArray()
        };
        var attachmentMaterialExports = new List<ExportEntry>();
        var hairMesh = directHairEntry is null
            ? null
            : string.Equals(directHairEntry.ClassName, "SkeletalMesh", StringComparison.OrdinalIgnoreCase)
                ? GetSkeletalMesh(directHairEntry, attachmentMaterialExports)
                : throw new InvalidDataException($"m_oHairMesh resolved to {directHairEntry.ClassName}, not SkeletalMesh.");
        var otherMeshes = otherMeshEntries
            .Select(item => string.Equals(item.Entry.ClassName, "SkeletalMesh", StringComparison.OrdinalIgnoreCase)
                ? GetSkeletalMesh(item.Entry, attachmentMaterialExports)
                : throw new InvalidDataException($"m_oOtherMeshes[{item.Index}] resolved to {item.Entry.ClassName}, not SkeletalMesh."))
            .ToArray();
        var attachmentTime = Mark(ref checkpoint);
        var materialResult = _materialReader.Read(export, baseMaterialExports, attachmentMaterialExports,
            decodeTextures: decodeTextures);
        var materialTime = Mark(ref checkpoint);
        document = document with { MaterialOverrides = materialResult.Overrides };
        var diagnostics = TopologyDiagnostics.Analyze(baseHead, document);
        var diagnosticsTime = Mark(ref checkpoint);
        LastLoadMetrics = new MorphFaceLoadMetrics(
            Stopwatch.GetElapsedTime(started),
            packageOpenTime,
            documentTime,
            baseHeadTime,
            attachmentTime,
            materialTime,
            diagnosticsTime,
            _materialReader.LastReadMetrics.Overrides,
            _materialReader.LastReadMetrics.Resolution,
            _materialReader.LastReadMetrics.CacheHits,
            _materialReader.LastReadMetrics.CacheMisses)
        {
            MaterialDefaults = _materialReader.LastReadMetrics.Defaults,
            MaterialShaderLookup = _materialReader.LastReadMetrics.ShaderLookup,
            MaterialTextureDecode = _materialReader.LastReadMetrics.TextureDecode
        };
        return new LoadedMorphFace(document, baseHead, hairMesh, materialResult.Materials, diagnostics)
        {
            Game = package.Game switch
            {
                MEGame.LE1 => MorphFaceGame.LE1,
                MEGame.LE2 => MorphFaceGame.LE2,
                MEGame.LE3 => MorphFaceGame.LE3,
                _ => MorphFaceGame.Unsupported
            },
            OtherMeshes = otherMeshes,
            Warnings = materialResult.Warnings.Concat(referenceWarnings).Distinct().ToArray()
        };
    }

    /// <summary>Releases derived package/mesh/material data; the reader remains reusable.</summary>
    public void ClearCaches(bool preserveDecodedTextures = false)
    {
        ThrowIfDisposed();
        // Explicit workspace/archetype teardown also retires the detached
        // geometry and material caches; ordinary PCC eviction leaves them warm.
        _meshCache.Clear();
        _fingerprintCache.Clear();
        _meshContentFingerprints.Clear();
        _materialReader.ClearCaches(preserveDecodedTextures);
        _packageCache.ReleasePackages();
        // Retained pixels still need their original source stamps so an edited
        // PCC invalidates them when it is next opened.
        if (!preserveDecodedTextures) PackageSourceCache.Clear(_packageCache);
    }

    /// <summary>Keeps only decoded textures used by the newly loaded face and its defaults.</summary>
    public void RetainTextureCache(ResolvedHeadMaterialSet materials)
    {
        ThrowIfDisposed();
        _materialReader.RetainTextureCache(materials);
    }

    public void InvalidatePackage(string packagePath)
    {
        ThrowIfDisposed();
        var fullPath = Path.GetFullPath(packagePath);
        _fingerprintCache.Remove(fullPath);
        foreach (var key in _meshContentFingerprints.Keys.Where(key =>
                     key.StartsWith(fullPath + "|", StringComparison.OrdinalIgnoreCase)).ToArray())
            _meshContentFingerprints.Remove(key);
        _materialReader.InvalidatePackage(fullPath);
        foreach (var key in _meshCache.Where(pair =>
                     pair.Key.StartsWith(fullPath + "|", StringComparison.OrdinalIgnoreCase) ||
                     pair.Value.Materials.Any(material => Path.GetFullPath(material.PackagePath)
                         .Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
                     .Select(pair => pair.Key).ToArray())
        {
            _meshCache.Remove(key);
        }
        _packageCache.ReleasePackages(path => string.Equals(
            Path.GetFullPath(path),
            fullPath,
            StringComparison.OrdinalIgnoreCase));
    }

    public LoadedAttachment LoadAttachment(
        string packagePath,
        string faceSelector,
        string meshSelector)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var face = ExportSelector.Find(package, faceSelector, "BioMorphFace");
        var meshEntry = package.FindEntry(meshSelector, "SkeletalMesh")
            ?? throw new KeyNotFoundException($"SkeletalMesh reference '{meshSelector}' was not found in {package.FilePath}.");
        var meshExport = ResolveExport(meshEntry)
            ?? throw new InvalidDataException($"SkeletalMesh reference '{meshSelector}' could not be resolved.");
        var materialExports = new List<ExportEntry>();
        var mesh = ReadSkeletalMesh(meshExport, materialExports);
        // A replacement mesh must bring its own material defaults. Reapplying the
        // open face's overrides here would make the outgoing hair's texture maps
        // masquerade as defaults on the newly selected mesh.
        var materials = _materialReader.Read(face, [], materialExports, applyFaceOverrides: false).Materials;
        return new LoadedAttachment(ToIdentity(meshExport)!, mesh, materials);
    }

    /// <summary>Loads an exact installed attachment for detached preview without requiring a BioMorphFace owner.</summary>
    public LoadedAttachment LoadDetachedAttachment(string packagePath, string meshSelector)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var meshExport = _referenceResolver.Require(
            FindDetachedAttachmentExport(package, meshSelector), "selected attachment mesh");
        var materialExports = new List<ExportEntry>();
        var mesh = ReadSkeletalMesh(meshExport, materialExports);
        var materials = materialExports
            .DistinctBy(material => MaterialIdentityKey.Create(ToIdentity(material)!))
            .Select(material => _materialReader.ReadFaceAttachmentEvidence(material))
            .ToDictionary(material => material.Key, StringComparer.OrdinalIgnoreCase);
        return new LoadedAttachment(
            ToIdentity(meshExport)!, mesh, new ResolvedHeadMaterialSet(materials));
    }

    /// <summary>
    /// Validates the exact mesh and material-slot dependency graph used by
    /// detached attachment preview without decoding material textures. This is
    /// used while building preview menus so malformed review exports are not
    /// offered as choices that will fail later on selection.
    /// </summary>
    public void ValidateDetachedAttachment(string packagePath, string meshSelector)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var meshExport = FindDetachedAttachmentExport(package, meshSelector);
        _ = ReadSkeletalMesh(meshExport, new List<ExportEntry>());
    }

    private static ExportEntry FindDetachedAttachmentExport(IMEPackage package, string meshSelector)
    {
        var matches = package.Exports.Where(export =>
                !export.IsDefaultObject &&
                export.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                (export.InstancedFullPath.Equals(meshSelector, StringComparison.OrdinalIgnoreCase) ||
                 PccAssetPathPolicy.FromDonorOccurrence(export.InstancedFullPath, package.FilePath)
                     .Equals(meshSelector, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"SkeletalMesh '{meshSelector}' was {(matches.Length == 0 ? "not found" : "ambiguous")} in '{package.FilePath}'.");
    }

    /// <summary>
    /// Reads the material evidence used by one exact SkeletalMesh export.
    /// Geometry is intentionally not decoded: this adapter owns package
    /// identity, LOD section-to-slot ownership, and detached material data;
    /// Custom Mode owns the later assignment of that evidence to custom mesh
    /// sections.
    /// </summary>
    public MeshMaterialEvidence LoadMeshMaterialEvidence(
        string packagePath,
        string meshSelector,
        bool decodeTextures = true)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var meshExport = _referenceResolver.Require(
            ExportSelector.Find(package, meshSelector, "SkeletalMesh"), "mesh material evidence");
        var mesh = meshExport.GetBinaryData<SkeletalMesh>();
        if (mesh.LODModels is not { Length: > 0 })
        {
            throw new InvalidDataException(
                $"SkeletalMesh '{meshExport.InstancedFullPath}' has no LOD models.");
        }

        var materialUIndices = mesh.Materials ?? [];
        var usedSections = ResolveUsedMaterialSections(meshExport, mesh, materialUIndices.Length);

        var slots = usedSections
            .Select(pair =>
            {
                var materialReference = meshExport.FileRef.GetEntry(materialUIndices[pair.Key])
                    ?? throw new InvalidDataException(
                        $"SkeletalMesh '{meshExport.InstancedFullPath}' has invalid material UIndex " +
                        $"{materialUIndices[pair.Key]} in slot {pair.Key}.");
                var materialExport = CanonicalAttachmentMaterialPolicy.ResolveReplacement(
                    meshExport, materialReference, _packageCache) ?? _referenceResolver.Require(
                    materialReference,
                    $"SkeletalMesh '{meshExport.InstancedFullPath}' material slot {pair.Key}");
                var material = _materialReader.ReadMaterialEvidence(materialExport, decodeTextures);
                return new MeshMaterialEvidenceSlot(
                    pair.Key,
                    ToIdentity(materialExport)!,
                    material,
                    pair.Value.ToArray());
            })
            .ToArray();

        return new MeshMaterialEvidence(ToIdentity(meshExport)!, slots);
    }

    /// <summary>
    /// Reads only the identities and effective material families used by one
    /// exact SkeletalMesh export. Unlike <see cref="LoadMeshMaterialEvidence"/>,
    /// this menu-discovery path does not read parameter defaults, textures or
    /// compiled shader maps.
    /// </summary>
    public MeshMaterialSourceCatalog LoadMeshMaterialSources(
        string packagePath,
        string meshSelector)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var meshExport = _referenceResolver.Require(
            ExportSelector.Find(package, meshSelector, "SkeletalMesh"), "mesh material sources");
        var mesh = meshExport.GetBinaryData<SkeletalMesh>();
        if (mesh.LODModels is not { Length: > 0 })
        {
            throw new InvalidDataException(
                $"SkeletalMesh '{meshExport.InstancedFullPath}' has no LOD models.");
        }

        var materialUIndices = mesh.Materials ?? [];
        var usedSections = ResolveUsedMaterialSections(meshExport, mesh, materialUIndices.Length);

        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(meshExport.FileRef.FilePath)
        };
        var slots = usedSections.Select(pair =>
        {
            var materialReference = meshExport.FileRef.GetEntry(materialUIndices[pair.Key])
                ?? throw new InvalidDataException(
                    $"SkeletalMesh '{meshExport.InstancedFullPath}' has invalid material UIndex " +
                    $"{materialUIndices[pair.Key]} in slot {pair.Key}.");
            var materialExport = CanonicalAttachmentMaterialPolicy.ResolveReplacement(
                meshExport, materialReference, _packageCache) ?? _referenceResolver.Require(
                materialReference,
                $"SkeletalMesh '{meshExport.InstancedFullPath}' material slot {pair.Key}");
            var source = ToIdentity(materialExport)!;
            var (master, graphDependencies) = ResolveMaterialMaster(materialExport);
            dependencies.UnionWith(graphDependencies);

            var family = DescribeMaterialMaster(meshExport.Game, master)?.Family ?? HeadMaterialFamily.Unknown;

            return new MeshMaterialSourceSlot(pair.Key, source, family, pair.Value.ToArray());
        }).ToArray();

        return new MeshMaterialSourceCatalog(ToIdentity(meshExport)!, slots, dependencies);
    }

    internal static HeadMaterialDefinition? DescribeMaterialMaster(MEGame game, ExportEntry? master) =>
        master?.Game == game && master.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase)
            ? HeadMaterialCatalog.Find(game switch
            {
                MEGame.LE1 => MorphFaceGame.LE1,
                MEGame.LE2 => MorphFaceGame.LE2,
                MEGame.LE3 => MorphFaceGame.LE3,
                _ => MorphFaceGame.Unsupported
            }, master.ObjectName.Instanced)
            : null;

    private (ExportEntry Master, IReadOnlySet<string> Dependencies) ResolveMaterialMaster(ExportEntry material)
    {
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = material;
        while (true)
        {
            var identity = ToIdentity(current)!;
            var key = MaterialIdentityKey.Create(identity);
            if (!visited.Add(key))
                throw new InvalidDataException($"Material parent cycle detected at '{current.InstancedFullPath}'.");
            dependencies.Add(Path.GetFullPath(current.FileRef.FilePath));

            if (current.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase))
            {
                return (current, dependencies);
            }

            var parentPropertyName = current.IsA("RvrEffectsMaterialUser") ? "m_pBaseMaterial" : "Parent";
            var parentReference = current.GetProperty<ObjectProperty>(parentPropertyName)?.ResolveToEntry(current.FileRef)
                ?? throw new InvalidDataException(
                    $"Material '{current.InstancedFullPath}' has no resolvable {parentPropertyName}.");
            current = _referenceResolver.Require(parentReference,
                $"material '{current.InstancedFullPath}' {parentPropertyName}");
        }
    }

    private static SortedDictionary<int, List<MeshMaterialEvidenceSection>> ResolveUsedMaterialSections(
        ExportEntry meshExport,
        SkeletalMesh mesh,
        int materialCount)
    {
        var materialMaps = ReadLodMaterialMaps(meshExport, mesh.LODModels.Length, materialCount);
        var usedSections = new SortedDictionary<int, List<MeshMaterialEvidenceSection>>();
        for (var lodIndex = 0; lodIndex < mesh.LODModels.Length; lodIndex++)
        {
            var sectionIndex = 0;
            foreach (var section in mesh.LODModels[lodIndex].Sections ?? [])
            {
                var slotIndex = LodMaterialMap.Resolve(
                    section.MaterialIndex,
                    materialMaps[lodIndex],
                    materialCount,
                    $"SkeletalMesh '{meshExport.InstancedFullPath}' LOD {lodIndex} section");
                if (!usedSections.TryGetValue(slotIndex, out var slotSections))
                {
                    slotSections = [];
                    usedSections.Add(slotIndex, slotSections);
                }
                slotSections.Add(new MeshMaterialEvidenceSection(lodIndex, sectionIndex));
                sectionIndex++;
            }
        }
        return usedSections;
    }

    /// <summary>Materialises an exact detached template on selection, through the normal bounded pixel cache.</summary>
    public ResolvedHeadMaterial LoadMaterialEvidence(AssetIdentity source)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var package = PackageSourceCache.GetPackage(_packageCache, RequireFile(source.PackagePath))
            ?? throw new InvalidDataException($"Could not open material source '{source.PackagePath}'.");
        return _materialReader.ReadMaterialEvidence(
            _referenceResolver.Require(ExportSelector.Find(package, source.InstancedPath, source.ClassName),
                "custom material template"));
    }

    public ResolvedHeadMaterial LoadLe3CcScalpMaterial(bool female)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var path = Le3CcScalpMaterialPolicy.CanonicalDonorPath(female);
        var resolved = new InstalledGameAssetResolver().Resolve(
            MEGame.LE3, path, "MaterialInstanceConstant", _packageCache);
        var source = resolved is null ? null : new AssetIdentity(
            resolved.FileRef.FilePath, path, resolved.UIndex, resolved.ClassName);
        if (source is null)
        {
            var seedPath = StandalonePlayerMorphImportService.ResolveInstalledSeed(MorphFaceGame.LE3);
            source = new AssetIdentity(seedPath, path, 0, "MaterialInstanceConstant");
        }
        var material = LoadMaterialEvidence(source);
        if (material.PreviewModel != HumanMaterialPreviewModel.CcScalp)
            throw new InvalidDataException($"The official scalp donor '{path}' does not use the CC scalp graph.");
        return material;
    }

    public IReadOnlyList<TextureReadResult> LoadTextures(
        IReadOnlyList<TextureReadRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requests);
        var results = new TextureReadResult[requests.Count];
        var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < requests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var path = RequireFile(requests[i].PackagePath);
                if (!groups.TryGetValue(path, out var indexes))
                    groups.Add(path, indexes = []);
                indexes.Add(i);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[i] = new TextureReadResult(null, error);
            }
        }

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cacheOperation = _packageCache.BeginOperation();
            foreach (var index in group.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = requests[index];
                try
                {
                    results[index] = new TextureReadResult(
                        LoadTexture(group.Key, request.TexturePath, request.Definition), null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results[index] = new TextureReadResult(null, error);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    public DecodedTextureAsset LoadTexture(
        string packagePath,
        string textureSelector,
        MaterialParameterDefinition definition)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        if (_materialReader.TryGetCachedTexture(fullPath, textureSelector, definition, out var cached)) return cached;
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var entry = package.FindEntry(textureSelector, "Texture2D")
            ?? InstalledGameAssetResolver.RequireExact(_packageCache, fullPath, textureSelector, "Texture2D");
        // This API is used by the texture picker: the selected physical donor
        // is explicit, including a mod replacement at an otherwise stock path.
        var texture = entry as ExportEntry ?? _referenceResolver.Require(
            entry, $"Texture2D reference '{textureSelector}'");
        var decoded = _materialReader.DecodeTextureReference(texture, definition);
        // A seekfree donor may expose a package-local export path while the
        // picker requested its package-qualified identity. Keep the physical
        // package/UIndex, but return the canonical path that PCC writers use.
        var canonicalPath = PccAssetPathPolicy.FromExport(texture);
        return decoded.Source.InstancedPath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase)
            ? decoded
            : decoded with { Source = decoded.Source with { InstancedPath = canonicalPath } };
    }

    /// <summary>Loads one authored mip suitable for a small picker thumbnail without populating the full pixel cache.</summary>
    public DecodedTextureAsset LoadTextureThumbnail(
        string packagePath,
        string textureSelector,
        MaterialParameterDefinition definition)
    {
        ThrowIfDisposed();
        using var cacheOperation = _packageCache.BeginOperation();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = PackageSourceCache.GetPackage(_packageCache, fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var entry = package.FindEntry(textureSelector, "Texture2D")
            ?? InstalledGameAssetResolver.RequireExact(_packageCache, fullPath, textureSelector, "Texture2D");
        var texture = entry as ExportEntry ?? _referenceResolver.Require(
            entry, $"Texture2D reference '{textureSelector}'");
        var decoded = _materialReader.DecodeTextureThumbnailReference(texture, definition, maximumDimension: 128);
        var canonicalPath = PccAssetPathPolicy.FromExport(texture);
        return decoded.Source.InstancedPath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase)
            ? decoded
            : decoded with { Source = decoded.Source with { InstancedPath = canonicalPath } };
    }

    private MorphFaceDocument ReadDocument(ExportEntry export)
    {
        var properties = export.GetProperties();
        var morph = MorphFacePackageCodec.ReadMorphData(export, properties);

        return new MorphFaceDocument(
            ToIdentity(export)!,
            CaptureFingerprint(export.FileRef.FilePath),
            ToIdentity(properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(export.FileRef)),
            ToIdentity(properties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(export.FileRef)),
            morph.MorphFeatures,
            morph.FinalSkeleton,
            MorphFaceMaterialOverrides.Empty,
            morph.BakedLods,
            properties.Select(property => property.Name.Instanced).ToArray());
    }

    private ExportEntry? ResolveObjectReference(ExportEntry owner, string propertyName)
    {
        var entry = owner.GetProperty<ObjectProperty>(propertyName)?.ResolveToEntry(owner.FileRef);
        return entry is null
            ? null
            : _referenceResolver.Require(
                entry,
                $"BioMorphFace '{owner.InstancedFullPath}' {propertyName}");
    }

    private ExportEntry? ResolveOptionalMeshReference(
        IEntry? entry,
        string purpose,
        ICollection<string> warnings)
    {
        if (entry is null)
        {
            return null;
        }
        if (!entry.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{purpose} resolved to {entry.ClassName}, not SkeletalMesh.");
        }
        var resolved = _referenceResolver.Resolve(entry);
        if (resolved is not null)
        {
            if (!MorphFaceAttachmentTransferEngine.HasSupportedMaterials(resolved.FileRef, resolved, out var unsupported))
            {
                warnings.Add($"Optional attachment '{entry.InstancedFullPath}' was omitted because {unsupported} is outside MFE's supported canonical materials.");
                return null;
            }
            return resolved;
        }
        warnings.Add(
            $"Could not resolve optional attachment '{entry.InstancedFullPath}'. " +
            "Its exact authored reference is retained, but the attachment is omitted from preview.");
        return null;
    }

    private SkeletalMeshAsset ReadSkeletalMesh(ExportEntry export, ICollection<ExportEntry> materialExports)
    {
        var mesh = export.GetBinaryData<SkeletalMesh>();
        if (mesh.LODModels is not { Length: > 0 })
        {
            throw new InvalidDataException($"SkeletalMesh '{export.InstancedFullPath}' has no LOD models.");
        }

        var materialSlots = mesh.Materials?
            .Select((index, slot) =>
            {
                // Some stock attachment meshes retain an empty material slot that no LOD
                // section uses (LE3 HMM Ssk_02 is one example). Keep the slot so section
                // indices remain stable, but do not try to resolve a package entry for UIndex 0.
                if (index == 0)
                {
                    return null;
                }

                var reference = export.FileRef.GetEntry(index)
                    ?? throw new InvalidDataException(
                        $"SkeletalMesh '{export.InstancedFullPath}' has invalid material UIndex {index}.");
                var resolved = CanonicalAttachmentMaterialPolicy.ResolveReplacement(export, reference, _packageCache)
                    ?? _referenceResolver.Require(
                    reference,
                    $"SkeletalMesh '{export.InstancedFullPath}' material slot");
                var material = RecoverCorpusMaterialSlot(export, slot, resolved);
                materialExports.Add(material);
                return ToIdentity(material);
            })
            .ToArray() ?? [];
        var skeleton = mesh.RefSkeleton?
            .Select(bone => new ReferenceBone(
                bone.Name.Instanced,
                bone.ParentIndex,
                bone.Position,
                bone.Orientation))
            .ToArray() ?? [];
        var lodMaterialMaps = ReadLodMaterialMaps(export, mesh.LODModels.Length, materialSlots.Length);
        var lods = mesh.LODModels
            .Select((model, index) => ReadSkeletalMeshLod(
                export, mesh, model, index, skeleton, materialSlots, lodMaterialMaps[index]))
            .ToArray();
        foreach (var candidateLod in lods)
        {
            foreach (var section in candidateLod.Topology.Sections)
            {
                if (materialSlots[section.MaterialIndex] is null)
                {
                    throw new InvalidDataException(
                        $"SkeletalMesh '{export.InstancedFullPath}' LOD {candidateLod.LodIndex} section " +
                        $"references null material slot {section.MaterialIndex}.");
                }
            }
        }

        return new SkeletalMeshAsset(ToIdentity(export)!, lods);
    }

    private static SkeletalMeshLod ReadSkeletalMeshLod(
        ExportEntry export,
        SkeletalMesh mesh,
        StaticLODModel lod,
        int lodIndex,
        IReadOnlyList<ReferenceBone> skeleton,
        IReadOnlyList<AssetIdentity?> materialSlots,
        IReadOnlyList<int> materialMap)
    {
        Vector3[] positions;
        Vector3[] normals;
        Vector4[] tangents;
        Vector2[] textureCoordinates;
        BoneIndex4[] boneIndices;
        Vector4[] boneWeights;
        if (lod.VertexBufferGPUSkin?.VertexData is { } gpuVertices)
        {
            positions = gpuVertices.Select(vertex => vertex.Position).ToArray();
            normals = gpuVertices.Select(vertex => (Vector3)vertex.TangentZ).ToArray();
            tangents = gpuVertices.Select(vertex => SkeletalMeshTangentBasis.ToRenderTangent(
                (Vector4)vertex.TangentX,
                (Vector4)vertex.TangentZ)).ToArray();
            textureCoordinates = gpuVertices.Select(vertex => new Vector2(vertex.UV.X, vertex.UV.Y)).ToArray();
            boneIndices = gpuVertices
                .Select((vertex, index) => ResolveBoneIndices(index, vertex.InfluenceBones, lod.Chunks))
                .ToArray();
            boneWeights = gpuVertices.Select(vertex => ToWeights(vertex.InfluenceWeights)).ToArray();
        }
        else if (lod.ME1VertexBufferGPUSkin is { } me1Vertices)
        {
            positions = me1Vertices.Select(vertex => vertex.Position).ToArray();
            normals = me1Vertices.Select(vertex => (Vector3)vertex.TangentZ).ToArray();
            tangents = me1Vertices.Select(vertex => SkeletalMeshTangentBasis.ToRenderTangent(
                (Vector4)vertex.TangentX,
                (Vector4)vertex.TangentZ)).ToArray();
            textureCoordinates = me1Vertices.Select(vertex => vertex.UV).ToArray();
            boneIndices = me1Vertices
                .Select((vertex, index) => ResolveBoneIndices(index, vertex.InfluenceBones, lod.Chunks))
                .ToArray();
            boneWeights = me1Vertices.Select(vertex => ToWeights(vertex.InfluenceWeights)).ToArray();
        }
        else
        {
            throw new InvalidDataException(
                $"SkeletalMesh '{export.InstancedFullPath}' LOD {lodIndex} has no supported GPU skin vertex buffer.");
        }

        var sections = lod.Sections?
            .Select(section => new MeshSectionTopology(
                LodMaterialMap.Resolve(
                    section.MaterialIndex,
                    materialMap,
                    materialSlots.Count,
                    $"SkeletalMesh '{export.InstancedFullPath}' LOD {lodIndex} section"),
                section.ChunkIndex,
                checked((int)section.BaseIndex),
                NormalizeTriangleCount(export.Game, section.BaseIndex, section.NumTriangles, lod.IndexBuffer?.Length ?? 0)))
            .ToArray() ?? [];
        var chunks = lod.Chunks?
            .Select(chunk => new MeshChunkTopology(
                checked((int)chunk.BaseVertexIndex),
                chunk.NumRigidVertices,
                chunk.NumSoftVertices,
                chunk.MaxBoneInfluences,
                chunk.BoneMap?.Select(index => (int)index).ToArray() ?? []))
            .ToArray() ?? [];
        var topology = new SkeletalMeshTopology(
            lodIndex,
            positions.Length,
            lod.IndexBuffer?.Length ?? 0,
            mesh.Materials?.Length ?? 0,
            sections,
            chunks,
            skeleton,
            lod.ActiveBoneIndices?.Select(index => (int)index).ToArray() ?? [],
            lod.RequiredBones?.Select(index => (int)index).ToArray() ?? [],
            SkeletalMeshTopologyHasher.Hash(lod, mesh));

        var renderData = new SkeletalMeshRenderData(
            tangents,
            textureCoordinates,
            boneIndices,
            boneWeights,
            lod.IndexBuffer?.Select(index => (int)index).ToArray() ?? [],
            materialSlots);

        return new SkeletalMeshLod(lodIndex, positions, normals, topology, renderData);
    }

    private static IReadOnlyList<int[]> ReadLodMaterialMaps(
        ExportEntry export,
        int lodCount,
        int materialCount)
    {
        var identity = Enumerable.Range(0, materialCount).ToArray();
        var result = Enumerable.Range(0, lodCount)
            .Select(_ => identity.ToArray())
            .ToArray();
        var lodInfo = export.GetProperty<ArrayProperty<StructProperty>>("LODInfo");
        if (lodInfo is null)
        {
            return result;
        }

        for (var lodIndex = 0; lodIndex < Math.Min(lodCount, lodInfo.Count); lodIndex++)
        {
            var authoredMap = lodInfo[lodIndex].GetProp<ArrayProperty<IntProperty>>("LODMaterialMap");
            if (authoredMap is not { Count: > 0 })
            {
                continue;
            }
            for (var localIndex = 0; localIndex < Math.Min(authoredMap.Count, materialCount); localIndex++)
            {
                result[lodIndex][localIndex] = authoredMap[localIndex].Value;
            }
        }
        return result;
    }

    private static ExportEntry RecoverCorpusMaterialSlot(
        ExportEntry mesh,
        int slot,
        ExportEntry resolved)
    {
        if (mesh.InstancedFullPath.EndsWith(
                "BIOG_KRO_HED_PROMorph.KRO_HED_PROBase_MDL",
                StringComparison.OrdinalIgnoreCase) &&
            resolved.ClassName.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase))
        {
            // The consolidated LE2 research corpus contains the correct local
            // Krogan MICs but retained two pre-port expression UIndices.
            var expectedPath = slot switch
            {
                0 => "BIOG_KRO_HED_PROMorph.KRO_HED_PROBASE_MAT_1a",
                1 => "BIOG_KRO_HED_PROMorph._Eye.KRO_EYE_PROBASE_MAT_1a",
                _ => null
            };
            return FindLocalMic(mesh, expectedPath) ?? resolved;
        }
        if (mesh.InstancedFullPath.EndsWith(
                "BIOG_ALN_HED_PROMorph_R.ALN_HED_PROBase_MDL",
                StringComparison.OrdinalIgnoreCase) &&
            resolved.ClassName.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase))
        {
            // The LE3 Vorcha corpus retains pre-port material-array UIndices;
            // the matching local MICs are authoritative in both games.
            var expectedPath = slot switch
            {
                0 => "BIOG_ALN_HED_PROMorph_R.PROBase.ALN_HED_PROBASE_MAT_1a",
                1 => "BIOG_ALN_HED_PROMorph_R.ALN_EYE_MAT_1a",
                _ => null
            };
            return FindLocalMic(mesh, expectedPath) ?? resolved;
        }
        return resolved;
    }

    private static ExportEntry? FindLocalMic(ExportEntry mesh, string? expectedPath) => expectedPath is null ? null :
        mesh.FileRef.Exports.FirstOrDefault(export =>
            export.InstancedFullPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) &&
            export.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase));

    private SkeletalMeshAsset GetSkeletalMesh(ExportEntry export, ICollection<ExportEntry> materialExports)
    {
        var identity = ToIdentity(export)!;
        var key = MaterialIdentityKey.Create(identity);
        if (!_meshCache.TryGetValue(key, out var cached))
        {
            var firstMaterialIndex = materialExports.Count;
            var asset = ReadSkeletalMesh(export, materialExports);
            _meshCache[key] = new CachedSkeletalMesh(
                asset,
                materialExports.Skip(firstMaterialIndex).Select(material => ToIdentity(material)!).ToArray());
            return asset;
        }

        foreach (var material in cached.Materials)
        {
            // Retain parsed geometry, not ExportEntry objects that would pin
            // whole PCCs after the package cache has evicted them.
            var package = PackageSourceCache.GetPackage(_packageCache, material.PackagePath)
                ?? throw new FileNotFoundException($"Material package '{material.PackagePath}' is unavailable.");
            materialExports.Add(ExportSelector.Find(package, material.InstancedPath, material.ClassName));
        }
        return cached.Asset;
    }

    private PackageFingerprint CaptureFingerprint(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        var info = new FileInfo(fullPath);
        if (_fingerprintCache.TryGetValue(fullPath, out var cached) &&
            cached.Size == info.Length && cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
        {
            return cached;
        }

        var captured = PackageFingerprint.Capture(fullPath);
        _fingerprintCache[fullPath] = captured;
        return captured;
    }

    private static int NormalizeTriangleCount(MEGame game, uint baseIndex, int triangleCount, int indexCount)
    {
        var lastIndexExclusive = (long)baseIndex + (long)triangleCount * 3;
        if (lastIndexExclusive <= indexCount ||
            game is not (MEGame.ME1 or MEGame.ME2 or MEGame.ME3 or MEGame.LE1 or MEGame.LE2 or MEGame.LE3))
        {
            return triangleCount;
        }

        // Some trilogy attachment sections retained the original 16-bit count
        // while the surrounding converter exposed the field as a 32-bit
        // integer. This occurs in stock LE3 HMM hair as well as legacy LE1/LE2
        // assets. Preserve genuine validation failures, but recover the packed
        // value only when its low word describes a valid index span.
        var legacyTriangleCount = triangleCount & ushort.MaxValue;
        var legacyLastIndexExclusive = (long)baseIndex + (long)legacyTriangleCount * 3;
        return legacyTriangleCount != triangleCount && legacyLastIndexExclusive <= indexCount
            ? legacyTriangleCount
            : triangleCount;
    }

    private static BoneIndex4 ResolveBoneIndices(
        int vertexIndex,
        Influences localIndices,
        IReadOnlyList<SkelMeshChunk>? chunks)
    {
        var chunk = chunks?.FirstOrDefault(candidate =>
            vertexIndex >= candidate.BaseVertexIndex &&
            vertexIndex < candidate.BaseVertexIndex + candidate.NumRigidVertices + candidate.NumSoftVertices);
        if (chunk?.BoneMap is not { } boneMap)
        {
            return default;
        }

        int Resolve(int influenceIndex)
        {
            var localIndex = localIndices[influenceIndex];
            return localIndex < boneMap.Length ? boneMap[localIndex] : 0;
        }

        return new BoneIndex4(Resolve(0), Resolve(1), Resolve(2), Resolve(3));
    }

    private static Vector4 ToWeights(Influences weights)
    {
        const float scale = 1f / byte.MaxValue;
        return new Vector4(
            weights[0] * scale,
            weights[1] * scale,
            weights[2] * scale,
            weights[3] * scale);
    }

    private ExportEntry? ResolveExport(IEntry? entry) => _referenceResolver.Resolve(entry);

    internal static AssetIdentity? ToIdentity(IEntry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        return new AssetIdentity(
            Path.GetFullPath(entry.FileRef.FilePath),
            entry.InstancedFullPath,
            entry.UIndex,
            entry.ClassName,
            entry is ImportEntry);
    }

    private static string RequireFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException("Package file was not found.", fullPath);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ClearCaches();
        _packageCache.Dispose();
        _disposed = true;
    }

    private sealed record CachedSkeletalMesh(
        SkeletalMeshAsset Asset,
        IReadOnlyList<AssetIdentity> Materials);
}

/// <summary>Detached material evidence grouped by the source mesh's used slots.</summary>
public sealed record MeshMaterialEvidence(
    AssetIdentity Mesh,
    IReadOnlyList<MeshMaterialEvidenceSlot> Slots)
{
    /// <summary>Materials in stable ascending source-slot order, retaining duplicate refs.</summary>
    public IReadOnlyList<ResolvedHeadMaterial> Materials =>
        Slots.Select(slot => slot.Material).ToArray();
}

/// <summary>Material source catalog used to populate assignment menus without resolving controls.</summary>
public sealed record MeshMaterialSourceCatalog(
    AssetIdentity Mesh,
    IReadOnlyList<MeshMaterialSourceSlot> Slots,
    IReadOnlySet<string> Dependencies);

/// <summary>One used source slot and the family proven by its effective material root.</summary>
public sealed record MeshMaterialSourceSlot(
    int SlotIndex,
    AssetIdentity Source,
    HeadMaterialFamily Family,
    IReadOnlyList<MeshMaterialEvidenceSection> Sections);

/// <summary>One source material slot and every LOD section that consumes it.</summary>
public sealed record MeshMaterialEvidenceSlot(
    int SlotIndex,
    AssetIdentity Source,
    ResolvedHeadMaterial Material,
    IReadOnlyList<MeshMaterialEvidenceSection> Sections);

/// <summary>Stable section location within a source mesh's LOD array.</summary>
public readonly record struct MeshMaterialEvidenceSection(int LodIndex, int SectionIndex);
