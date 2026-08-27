using System.Numerics;
using System.Diagnostics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Diagnostics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using BinaryMorphFace = LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed class MorphFacePackageReader : IDisposable
{
    // A face can reference its mesh, materials and textures across more than a dozen PCCs.
    // Keeping that working set resident avoids repeatedly reopening 100+ MiB game packages.
    private readonly PackageCache _packageCache = new() { CacheMaxSize = 16 };
    private readonly GamePackageReferenceResolver _referenceResolver;
    private readonly MorphFaceMaterialReader _materialReader;
    private readonly Dictionary<string, CachedSkeletalMesh> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageFingerprint> _fingerprintCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public MorphFacePackageReader()
    {
        _referenceResolver = new GamePackageReferenceResolver(_packageCache);
        _materialReader = new MorphFaceMaterialReader(_packageCache, _referenceResolver);
    }

    public MorphFaceLoadMetrics? LastLoadMetrics { get; private set; }

    public MorphRandomisationMaterialEvidence LoadRandomisationMaterialEvidence(
        string packagePath,
        string exportSelector)
    {
        ThrowIfDisposed();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = _packageCache.GetCachedPackage(fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var face = ExportSelector.Find(package, exportSelector, "BioMorphFace");
        var baseHead = ResolveObjectReference(face, "m_oBaseHead")
            ?? throw new InvalidDataException($"BioMorphFace '{face.InstancedFullPath}' has no resolvable base head.");
        var materials = new List<ExportEntry>();
        _ = GetSkeletalMesh(baseHead, materials);
        return _materialReader.ReadRandomisationEvidence(face, materials);
    }

    public LoadedMorphFace Load(string packagePath, string exportSelector)
    {
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
        var package = _packageCache.GetCachedPackage(fullPath)
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
        var directHairEntry = ResolveObjectReference(export, "m_oHairMesh");
        var otherMeshProperties = export.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes");
        var otherMeshEntries = otherMeshProperties?
            .Select((item, index) => (Entry: item.ResolveToEntry(export.FileRef), Index: index))
            .Where(item => item.Entry is not null)
            .Select(item => (
                Entry: _referenceResolver.Require(
                    item.Entry!,
                    $"BioMorphFace '{export.InstancedFullPath}' m_oOtherMeshes[{item.Index}]"),
                item.Index))
            .ToArray() ?? [];
        document = document with
        {
            HairMeshReference = ToIdentity(directHairEntry),
            OtherMeshReferences = otherMeshEntries.Select(item => ToIdentity(item.Entry)).ToArray()
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
        var materialResult = _materialReader.Read(export, baseMaterialExports, attachmentMaterialExports);
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
            _materialReader.LastReadMetrics.CacheMisses);
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
            Warnings = materialResult.Warnings
        };
    }

    public void InvalidatePackage(string packagePath)
    {
        ThrowIfDisposed();
        var fullPath = Path.GetFullPath(packagePath);
        _fingerprintCache.Remove(fullPath);
        _materialReader.InvalidatePackage(fullPath);
        foreach (var key in _meshCache.Keys
                     .Where(key => key.StartsWith(fullPath + "|", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
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
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = _packageCache.GetCachedPackage(fullPath)
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

    public DecodedTextureAsset LoadTexture(
        string packagePath,
        string textureSelector,
        MaterialParameterDefinition definition)
    {
        ThrowIfDisposed();
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var package = _packageCache.GetCachedPackage(fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var entry = package.FindEntry(textureSelector, "Texture2D")
            ?? throw new KeyNotFoundException($"Texture2D reference '{textureSelector}' was not found in {package.FilePath}.");
        var texture = _referenceResolver.Require(
            entry,
            $"Texture2D reference '{textureSelector}'");
        return _materialReader.DecodeTextureReference(texture, definition);
    }

    private MorphFaceDocument ReadDocument(ExportEntry export)
    {
        var properties = export.GetProperties();
        var binary = export.GetBinaryData<BinaryMorphFace>();
        var features = properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")?
            .Select(item => new MorphFeatureValue(
                item.GetProp<NameProperty>("sFeatureName")?.Value.Instanced ?? string.Empty,
                item.GetProp<FloatProperty>("Offset")?.Value ?? 0f))
            .ToArray() ?? [];
        var bones = properties.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton")?
            .Select(item => new BoneTranslation(
                item.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty,
                ReadVector(item.GetProp<StructProperty>("vPos"))))
            .ToArray() ?? [];
        var lods = binary.LODs?
            .Select(lod => lod?.ToArray() ?? [])
            .ToArray() ?? [];

        return new MorphFaceDocument(
            ToIdentity(export)!,
            CaptureFingerprint(export.FileRef.FilePath),
            ToIdentity(properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(export.FileRef)),
            ToIdentity(properties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(export.FileRef)),
            features,
            bones,
            MorphFaceMaterialOverrides.Empty,
            lods,
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
                var reference = export.FileRef.GetEntry(index)
                    ?? throw new InvalidDataException(
                        $"SkeletalMesh '{export.InstancedFullPath}' has invalid material UIndex {index}.");
                var resolved = _referenceResolver.Require(
                    reference,
                    $"SkeletalMesh '{export.InstancedFullPath}' material slot");
                var material = RecoverKroganCorpusMaterialSlot(export, slot, resolved);
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
        var lod = lods[0];
        return new SkeletalMeshAsset(
            ToIdentity(export)!,
            lod.Positions,
            lod.Normals,
            lod.Topology,
            lod.RenderData,
            lods.Select(value => value.Positions).ToArray(),
            lods);
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

    private static ExportEntry RecoverKroganCorpusMaterialSlot(
        ExportEntry mesh,
        int slot,
        ExportEntry resolved)
    {
        if (!mesh.InstancedFullPath.EndsWith(
                "BIOG_KRO_HED_PROMorph.KRO_HED_PROBase_MDL",
                StringComparison.OrdinalIgnoreCase) ||
            !resolved.ClassName.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase))
        {
            return resolved;
        }

        // The consolidated LE2 research corpus contains the correct local
        // Krogan MICs but retained two pre-port expression UIndices on the
        // embedded base mesh. Recover only this exact mesh/slot corruption;
        // ordinary game packages and all other material references retain the
        // normal resolver path.
        var expectedPath = slot switch
        {
            0 => "BIOG_KRO_HED_PROMorph.KRO_HED_PROBASE_MAT_1a",
            1 => "BIOG_KRO_HED_PROMorph._Eye.KRO_EYE_PROBASE_MAT_1a",
            _ => null
        };
        return expectedPath is null
            ? resolved
            : mesh.FileRef.Exports.FirstOrDefault(export =>
                export.InstancedFullPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) &&
                export.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) ?? resolved;
    }

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
                materialExports.Skip(firstMaterialIndex).ToArray());
            return asset;
        }

        foreach (var material in cached.Materials)
        {
            materialExports.Add(material);
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

    private static Vector3 ReadVector(StructProperty? property) =>
        property is null ? Vector3.Zero : CommonStructs.GetVector3(property);

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

        _packageCache.Dispose();
        _meshCache.Clear();
        _fingerprintCache.Clear();
        _disposed = true;
    }

    private sealed record CachedSkeletalMesh(
        SkeletalMeshAsset Asset,
        IReadOnlyList<ExportEntry> Materials);
}
