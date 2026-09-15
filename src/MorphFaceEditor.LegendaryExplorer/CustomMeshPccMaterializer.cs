using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// The binary mesh writer is deliberately supplied by the caller.  This keeps
/// material/package authoring independent from the PSK/glTF conversion code and
/// gives all supported interchange formats one PCC save path.
/// </summary>
public interface ICustomMeshBinaryWriter
{
    ExportEntry Write(
        IMEPackage package,
        ExportEntry packageRoot,
        ImportedMeshAsset source,
        string meshName,
        IReadOnlyList<CustomMeshMaterialReference> materials);
}

/// <summary>One imported slot and the MIC which must occupy that slot.</summary>
public sealed record CustomMeshMaterialReference(int MaterialIndex, ExportEntry Material);

public sealed record CustomMeshPccSaveRequest(
    ImportedMeshAsset SourceMesh,
    CustomMaterialWorkspace Workspace,
    MorphFaceMaterialData MaterialData,
    MorphFaceGame Game,
    string DestinationPath,
    string MeshName,
    ICustomMeshBinaryWriter MeshWriter,
    IReadOnlyList<TextureCatalogCandidate> TextureCatalog);

public sealed record CustomMeshPccMaterialResult(
    int MaterialIndex,
    string MaterialPath,
    string SourceMaterialPath,
    string Scope,
    IReadOnlyList<string> Parameters);

public sealed record CustomMeshPccSaveResult(
    string PackagePath,
    string PackageRootPath,
    string MeshPath,
    IReadOnlyList<CustomMeshPccMaterialResult> Materials,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Creates the new PCC used by a detached MESH workspace.  Attachments are not
/// part of this request and therefore cannot accidentally be written to the
/// authoring package.
/// </summary>
public sealed class CustomMeshPccMaterializer
{
    private const string PackageRootName = "MorphFaceEditor";

    public CustomMeshPccSaveResult Save(CustomMeshPccSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceMesh);
        ArgumentNullException.ThrowIfNull(request.Workspace);
        ArgumentNullException.ThrowIfNull(request.MaterialData);
        ArgumentNullException.ThrowIfNull(request.MeshWriter);
        ValidateRequest(request);
        LegendaryExplorerCoreRuntime.Initialize();

        var destination = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException("The PCC destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var originalDestination = File.Exists(destination)
            ? PackageFingerprint.Capture(destination)
            : null;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.mfe-mesh.tmp.pcc");

        try
        {
            var warnings = new List<string>();
            IReadOnlyList<CustomMeshPccMaterialResult> materialResults;
            string meshPath;
            using (var package = MEPackageHandler.CreateMemoryEmptyPackage(
                       temporaryPath,
                       ToMeGame(request.Game)))
            {
                var packageRoot = package.CreateExport(PackageRootName, "Package", indexed: false);
                var materials = MaterializeMaterials(package, packageRoot, request, warnings,
                    out materialResults);
                var mesh = request.MeshWriter.Write(
                    package,
                    packageRoot,
                    request.SourceMesh,
                    request.MeshName,
                    materials);
                if (mesh is null || !mesh.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The custom mesh writer did not return a SkeletalMesh export.");
                }
                if (!ReferenceEquals(mesh.Parent, packageRoot))
                {
                    throw new InvalidDataException(
                        $"The custom mesh writer placed '{mesh.InstancedFullPath}' outside '{packageRoot.InstancedFullPath}'.");
                }

                meshPath = mesh.InstancedFullPath;
                package.Save(temporaryPath);
            }

            Verify(temporaryPath, request, materialResults, meshPath);
            if (originalDestination is null)
            {
                if (File.Exists(destination))
                {
                    throw new IOException("The PCC destination was created by another process. Nothing was replaced.");
                }
            }
            else if (!File.Exists(destination) || PackageFingerprint.Capture(destination) != originalDestination)
            {
                throw new IOException("The PCC destination changed while it was being written. Nothing was replaced.");
            }

            AtomicReplace(temporaryPath, destination);
            return new CustomMeshPccSaveResult(
                destination,
                $"{PackageRootName}",
                meshPath,
                materialResults,
                warnings);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static IReadOnlyList<CustomMeshMaterialReference> MaterializeMaterials(
        IMEPackage destination,
        ExportEntry packageRoot,
        CustomMeshPccSaveRequest request,
        ICollection<string> warnings,
        out IReadOnlyList<CustomMeshPccMaterialResult> results)
    {
        var scoped = MeshMaterialInterchange.Split(request.MaterialData);
        var used = request.Workspace.UsedSlots
            .OrderBy(slot => slot.MaterialIndex)
            .ToArray();
        var assignments = request.Workspace.Assignments
            .ToDictionary(value => value.Slot.MaterialIndex);
        var output = new List<CustomMeshMaterialReference>(used.Length);
        var materialResults = new List<CustomMeshPccMaterialResult>(used.Length);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materialByKey = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in used)
        {
            if (!assignments.TryGetValue(slot.MaterialIndex, out var assignment))
            {
                throw new InvalidDataException($"Material slot {slot.MaterialIndex} has no assigned material.");
            }

            var option = assignment.Option;
            var sourceIdentity = option.Template.Source;
            var materialKey = MaterialIdentityKey.Create(sourceIdentity) + "|" +
                              option.EffectiveParameterScopeKey;
            if (!materialByKey.TryGetValue(materialKey, out var material))
            {
                var preferBiogTextures = option.EffectiveParameterScopeKey.Equals(
                    "human",
                    StringComparison.OrdinalIgnoreCase);
                material = ImportMaterialSource(
                    destination,
                    packageRoot,
                    sourceIdentity,
                    request.TextureCatalog,
                    preferBiogTextures,
                    warnings);
                var materialName = MakeMaterialName(
                    request.MeshName,
                    MaterialToken(option),
                    usedNames);
                material.ObjectName = new NameReference(materialName);
                var sourceScope = GetScopeData(request.MaterialData, scoped, option.EffectiveParameterScopeKey);
                var parameterNames = WriteMaterialParameters(
                    destination,
                    material,
                    option.Template,
                    sourceScope,
                    request.TextureCatalog,
                    preferBiogTextures,
                    warnings);
                materialByKey.Add(materialKey, material);
                materialResults.Add(new CustomMeshPccMaterialResult(
                    slot.MaterialIndex,
                    material.InstancedFullPath,
                    sourceIdentity.InstancedPath,
                    option.EffectiveParameterScopeKey,
                    parameterNames));
            }
            output.Add(new CustomMeshMaterialReference(slot.MaterialIndex, material));
        }

        results = materialResults;
        return output;
    }

    private static ExportEntry ImportMaterialSource(
        IMEPackage destination,
        ExportEntry packageRoot,
        AssetIdentity identity,
        IReadOnlyList<TextureCatalogCandidate> textureCatalog,
        bool preferBiogTextures,
        ICollection<string> warnings)
    {
        if (!File.Exists(identity.PackagePath))
        {
            throw new FileNotFoundException(
                $"The selected material source package no longer exists: {identity.PackagePath}",
                identity.PackagePath);
        }
        using var source = MEPackageHandler.OpenMEPackage(identity.PackagePath, forceLoadFromDisk: true);
        if (source.Game != destination.Game)
        {
            throw new InvalidDataException(
                $"Material '{identity.InstancedPath}' is from {source.Game}, but the destination is {destination.Game}.");
        }
        var sourceExport = ResolveMaterialExport(source, identity);
        using var packageCache = new PackageCache { CacheMaxSize = 16 };
        var resolver = new GamePackageReferenceResolver(packageCache);
        var state = new MaterialDependencyState(
            destination,
            resolver,
            textureCatalog,
            preferBiogTextures,
            warnings);
        return state.ImportRoot(sourceExport, packageRoot);
    }

    /// <summary>
    /// Imports the selected MIC and resolves its material graph before LEC gets
    /// a chance to manufacture dangling stock imports.  The imported child is
    /// placed below MorphFaceEditor; dependencies retain their canonical game
    /// paths as exports in the new package.
    /// </summary>
    private sealed class MaterialDependencyState
    {
        private readonly IMEPackage _destination;
        private readonly GamePackageReferenceResolver _resolver;
        private readonly IReadOnlyList<TextureCatalogCandidate> _textureCatalog;
        private readonly bool _preferBiogTextures;
        private readonly ICollection<string> _warnings;
        private readonly Dictionary<string, ExportEntry> _materialized = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _destinationSources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<IEntry, IEntry> _relinkTargets = [];
        private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ExportEntry, PropertyCollection> _sanitizedSources = [];

        public MaterialDependencyState(
            IMEPackage destination,
            GamePackageReferenceResolver resolver,
            IReadOnlyList<TextureCatalogCandidate> textureCatalog,
            bool preferBiogTextures,
            ICollection<string> warnings)
        {
            _destination = destination;
            _resolver = resolver;
            _textureCatalog = textureCatalog;
            _preferBiogTextures = preferBiogTextures;
            _warnings = warnings;
        }

        public ExportEntry ImportRoot(ExportEntry source, ExportEntry packageRoot)
        {
            try
            {
                var key = SourceKey(source);
                var dependencyTargets = ResolveDependencies(source);
                ReserveDependencyPaths(dependencyTargets);
                foreach (var dependency in dependencyTargets)
                {
                    _ = ImportDependency(dependency);
                }

                var imported = Import(source, packageRoot.UIndex, key);
                var parentReference = source.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(source.FileRef);
                if (parentReference is not null &&
                    _relinkTargets.TryGetValue(parentReference, out var destinationParent))
                {
                    imported.WriteProperty(new ObjectProperty(destinationParent, "Parent"));
                }
                return imported;
            }
            finally
            {
                foreach (var (sanitized, originalProperties) in _sanitizedSources)
                {
                    sanitized.WriteProperties(originalProperties);
                }
                _sanitizedSources.Clear();
            }
        }

        private ExportEntry ImportDependency(MaterialDependency dependency)
        {
            ExportEntry imported;
            if (dependency.TextureIdentity is { } textureIdentity)
            {
                imported = dependency.ResolveTextureThroughCatalog
                    ? PccTextureDependencyResolver.Materialize(
                        _destination,
                        textureIdentity,
                        _textureCatalog,
                        _preferBiogTextures,
                        _warnings)
                    : PccTextureDependencyResolver.MaterializeDirect(
                        _destination,
                        textureIdentity,
                        _warnings);
            }
            else
            {
                imported = ImportDependencyExport(dependency.Source ?? throw new InvalidDataException(
                    $"Material dependency '{dependency.Reference.InstancedFullPath}' has no source export."));
            }
            _relinkTargets[dependency.Reference] = imported;
            if (dependency.Source is not null)
            {
                _relinkTargets[dependency.Source] = imported;
            }
            return imported;
        }

        private ExportEntry ImportDependencyExport(ExportEntry source)
        {
            var key = SourceKey(source);
            if (_materialized.TryGetValue(key, out var existing)) return existing;
            if (!_active.Add(key))
                throw new InvalidDataException(
                    $"Circular material dependency graph detected at '{source.InstancedFullPath}' " +
                    $"({source.ClassName} in '{source.FileRef.FilePath}').");
            try
            {
                var dependencies = ResolveDependencies(source);
                ReserveDependencyPaths(dependencies);
                var canonicalPath = CanonicalPath(source.FileRef, source);
                var destinationKey = $"{source.ClassName}:{canonicalPath}";
                if (_destinationSources.TryGetValue(destinationKey, out var priorSource) &&
                    !string.Equals(priorSource, key, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Material dependency identity collision at '{canonicalPath}' ({source.ClassName}).");
                }
                if (_destination.FindExport(canonicalPath, source.ClassName) is { } existingExport)
                {
                    _destinationSources[destinationKey] = key;
                    _materialized[key] = existingExport;
                    return existingExport;
                }
                var parent = PackageIntegrity.EnsurePackagePath(_destination, canonicalPath, source.ClassName);
                foreach (var dependency in dependencies) _ = ImportDependency(dependency);
                if (_destination.FindEntry(canonicalPath, source.ClassName) is ImportEntry)
                {
                    throw new InvalidDataException(
                        $"Cannot materialise material dependency '{canonicalPath}': an import occupies the export identity.");
                }
                var imported = Import(source, parent?.UIndex ?? 0, key);
                var parentReference = source.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(source.FileRef);
                if (parentReference is not null &&
                    _relinkTargets.TryGetValue(parentReference, out var destinationParent))
                {
                    imported.WriteProperty(new ObjectProperty(destinationParent, "Parent"));
                }
                _materialized[key] = imported;
                _destinationSources[destinationKey] = key;
                return imported;
            }
            finally
            {
                _active.Remove(key);
            }
        }

        private void ReserveDependencyPaths(IEnumerable<MaterialDependency> dependencies)
        {
            foreach (var dependency in dependencies)
            {
                string? path = null;
                if (dependency.TextureIdentity is { } textureIdentity)
                {
                    path = textureIdentity.InstancedPath;
                    if (dependency.ResolveTextureThroughCatalog)
                    {
                        try
                        {
                            path = PccTextureDependencyResolver.Resolve(
                                textureIdentity,
                                _textureCatalog,
                                _preferBiogTextures).InstancedPath;
                        }
                        catch (KeyNotFoundException)
                        {
                            // The direct donor or preserved-import branch uses
                            // the original logical path.
                        }
                    }
                }
                else if (dependency.Source is { } source)
                {
                    path = CanonicalPath(source.FileRef, source);
                }
                if (!string.IsNullOrWhiteSpace(path) && path.Contains('.'))
                {
                    _ = PackageIntegrity.EnsurePackagePath(_destination, path);
                }
            }
        }

        private ExportEntry Import(ExportEntry source, int parentUIndex, string key)
        {
            if (source.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            {
                var canonical = CanonicalPath(source.FileRef, source);
                var identity = new AssetIdentity(
                    Path.GetFullPath(source.FileRef.FilePath),
                    canonical,
                    source.UIndex,
                    source.ClassName);
                return PccTextureDependencyResolver.Materialize(
                    _destination,
                    identity,
                    _textureCatalog,
                    _preferBiogTextures,
                    _warnings);
            }
            var isInstance = source.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
                             source.ClassName.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase);
            var isBaseMaterial = source.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase);
            var isEffectUser = source.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase);
            if ((isBaseMaterial || isEffectUser) && !_sanitizedSources.ContainsKey(source))
            {
                _sanitizedSources[source] = source.GetProperties();
                var portableProperties = source.GetProperties();
                if (isBaseMaterial)
                {
                    // Cooked packages execute the compiled shader resource. The
                    // editor-only expression graph is not required at runtime
                    // and recursively cloning it is what corrupts the compiled
                    // uniform texture indices.
                    var expressionIndices = portableProperties
                        .GetProp<ArrayProperty<ObjectProperty>>("Expressions")?
                        .Select(value => value.Value)
                        .Where(value => value != 0)
                        .ToHashSet() ?? [];
                    var graphInputs = portableProperties
                        .Where(property => !property.Name.Instanced.Equals("Expressions", StringComparison.OrdinalIgnoreCase) &&
                                           ReferencesAny(property, expressionIndices))
                        .Select(property => property.Name.Instanced)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    portableProperties.RemoveNamedProperty("Expressions");
                    portableProperties.RemoveNamedProperty("PhysMaterial");
                    foreach (var propertyName in graphInputs)
                    {
                        portableProperties.RemoveNamedProperty(propertyName);
                    }
                }
                else
                {
                    portableProperties.RemoveNamedProperty("m_pMultiplexor");
                    portableProperties.RemoveNamedProperty("m_pParentMaterial");
                }
                source.WriteProperties(portableProperties);
            }
            var relinker = new RelinkerOptionsPackage
            {
                ImportExportDependencies = false,
                GenerateImportsForGlobalFiles = false
            };
            // CrossPackageMap is also the verifier's proof set. Do not seed it
            // with mappings accumulated for earlier sibling materials: BioWare
            // ships canonical masters duplicated through multiple seek-free
            // packages, whose binary UIndexes are package-local even when the
            // logical object path is identical. Only this source export's
            // actual references are relevant to its clone operation.
            var referenced = PackageIntegrity.References(source)
                .Where(pair => pair.Value != 0)
                .Where(pair => !source.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) ||
                               pair.Key.Contains("m_pBaseMaterial", StringComparison.OrdinalIgnoreCase))
                .Select(pair => source.FileRef.GetEntry(pair.Value))
                .Where(entry => entry is not null)
                .Cast<IEntry>()
                .ToHashSet();
            var seededMappings = _relinkTargets
                .Where(pair => referenced.Contains(pair.Key))
                .Where(pair => !isInstance || pair.Key.ClassName.StartsWith("Texture", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var directUIndexMap = seededMappings
                .Where(pair => ReferenceEquals(pair.Key.FileRef, source.FileRef))
                .ToDictionary(pair => pair.Key.UIndex, pair => pair.Value.UIndex);
            relinker.CustomRelinkUIndex = (
                IMEPackage _, ExportEntry _, ref int uIndex, string _, string _,
                RelinkerOptionsPackage _, out EntryStringPair report) =>
            {
                report = null!;
                if (!directUIndexMap.TryGetValue(uIndex, out var targetUIndex)) return false;
                uIndex = targetUIndex;
                return true;
            };
            ExternalSkeletalMeshMaterializer.PrepareReferencedPackagePaths(_destination, source);
            var imported = EntryImporter.ImportExport(_destination, source, parentUIndex, relinker);
            MaterialisationVerifier.Relink(relinker);
            if (imported is not ExportEntry export || !IsPortableDependencyClass(export.ClassName))
            {
                throw new InvalidDataException(
                    $"LEC did not materialise '{source.InstancedFullPath}' as a material dependency export.");
            }
            if (export.ClassName.Equals("TextureCube", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var faceName in new[]
                         {
                             "FacePosX", "FaceNegX", "FacePosY", "FaceNegY", "FacePosZ", "FaceNegZ"
                         })
                {
                    var faceReference = source.GetProperty<ObjectProperty>(faceName)?.ResolveToEntry(source.FileRef);
                    if (faceReference is not null &&
                        TryGetRelinkTarget(faceReference, out var destinationFace))
                    {
                        export.WriteProperty(new ObjectProperty(destinationFace, faceName));
                    }
                    else if (source.GetProperty<ObjectProperty>(faceName) is { Value: > 0 } faceProperty)
                    {
                        throw new InvalidDataException(
                            $"TextureCube '{source.InstancedFullPath}' could not map {faceName} source UIndex {faceProperty.Value}; " +
                            $"resolved source is '{faceReference?.InstancedFullPath ?? "<none>"}', available exact targets: " +
                            string.Join(", ", _relinkTargets.Keys
                                .Where(value => value.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                                .Select(value => $"{value.UIndex}:{value.InstancedFullPath}").Take(12)));
                    }
                }
                // The pinned relinker reports cube-face properties before the
                // explicit exact-path rewrite above. Output verification below
                // remains authoritative for every retained reference.
                relinker.RelinkReport.Clear();
            }
            if (isInstance)
            {
                var parentReference = source.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(source.FileRef);
                if (parentReference is not null &&
                    _relinkTargets.TryGetValue(parentReference, out var destinationParent))
                {
                    export.WriteProperty(new ObjectProperty(destinationParent, "Parent"));
                }

                var verification = new RelinkerOptionsPackage();
                verification.CrossPackageMap[source] = export;
                foreach (var (donor, target) in seededMappings)
                {
                    verification.CrossPackageMap[donor] = target;
                }
                MaterialisationVerifier.Verify(export, source.Game, verification, _warnings, rootOnly: true);
            }
            else
            {
                foreach (var (donor, target) in seededMappings)
                {
                    relinker.CrossPackageMap[donor] = target;
                }
                MaterialisationVerifier.Verify(export, source.Game, relinker, _warnings, rootOnly: true);
            }
            return export;
        }

        private bool TryGetRelinkTarget(IEntry reference, out IEntry target)
        {
            if (_relinkTargets.TryGetValue(reference, out target!)) return true;
            var referencePath = reference is ExportEntry export
                ? CanonicalPath(export.FileRef, export)
                : reference.InstancedFullPath;
            foreach (var (candidate, destination) in _relinkTargets)
            {
                var candidatePath = candidate is ExportEntry candidateExport
                    ? CanonicalPath(candidateExport.FileRef, candidateExport)
                    : candidate.InstancedFullPath;
                if (candidate.ClassName.Equals(reference.ClassName, StringComparison.OrdinalIgnoreCase) &&
                    candidatePath.Equals(referencePath, StringComparison.OrdinalIgnoreCase))
                {
                    target = destination;
                    return true;
                }
            }
            target = null!;
            return false;
        }

        private static bool ReferencesAny(Property property, IReadOnlySet<int> uIndices) => property switch
        {
            ObjectProperty reference => uIndices.Contains(reference.Value),
            StructProperty structure => structure.Properties.Any(value => ReferencesAny(value, uIndices)),
            ArrayProperty<ObjectProperty> references => references.Any(value => uIndices.Contains(value.Value)),
            ArrayProperty<StructProperty> structures => structures.Any(structure =>
                structure.Properties.Any(value => ReferencesAny(value, uIndices))),
            _ => false
        };

        private IReadOnlyList<MaterialDependency> ResolveDependencies(ExportEntry source)
        {
            var dependencies = new List<MaterialDependency>();
            foreach (var (referenceSite, uIndex) in PackageIntegrity.References(source))
            {
                if (uIndex == 0 || source.FileRef.GetEntry(uIndex) is not { } reference ||
                    ReferenceEquals(reference, source) || !IsPortableDependencyClass(reference.ClassName))
                {
                    continue;
                }

                // A RvrEffectsMaterialUser is the render-chain wrapper. Its
                // multiplexor points into the large gameplay/VFX effect graph,
                // which is neither needed by nor referenced from an authored
                // head MIC. GlobalMorphs proves the wrapper itself is an export;
                // only m_pBaseMaterial belongs in this PCC.
                if (source.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) &&
                    !referenceSite.Contains("m_pBaseMaterial", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (source.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase) &&
                    referenceSite.Contains("PhysMaterial", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var canonicalPath = reference is ExportEntry referenceExport
                    ? CanonicalPath(referenceExport.FileRef, referenceExport)
                    : reference.InstancedFullPath;
                var oracleKind = MaterialDependencyOracle.Instance.GetKind(
                    _destination.Game,
                    reference.ClassName,
                    canonicalPath);
                if (oracleKind == MaterialOracleEntryKind.Import)
                {
                    _relinkTargets[reference] = EnsureImport(canonicalPath, reference.ClassName);
                    continue;
                }

                // Owned expressions are cloned with their Material. Treating
                // them as independent roots creates package ancestors in the
                // identity their owner must occupy.
                if (reference is ExportEntry owned &&
                    owned.ClassName.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase) &&
                    IsOwnedSubobject(owned, source))
                {
                    continue;
                }

                if (reference.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                {
                    var directSource = reference as ExportEntry ?? _resolver.Resolve(reference);
                    var identity = new AssetIdentity(
                        directSource is null
                            ? Path.GetFullPath(reference.FileRef.FilePath)
                            : Path.GetFullPath(directSource.FileRef.FilePath),
                        canonicalPath,
                        directSource?.UIndex ?? 0,
                        "Texture2D");

                    if (oracleKind == MaterialOracleEntryKind.Export && directSource is not null)
                    {
                        dependencies.Add(new MaterialDependency(
                            reference,
                            directSource,
                            identity,
                            ResolveTextureThroughCatalog: false));
                        continue;
                    }

                    // The native corpus intentionally omits a small set of
                    // player-only authored textures. They must resolve by their
                    // exact BIOG identity through the installed texture DB.
                    try
                    {
                        _ = PccTextureDependencyResolver.Resolve(
                            identity,
                            _textureCatalog,
                            _preferBiogTextures);
                        dependencies.Add(new MaterialDependency(
                            reference,
                            directSource,
                            identity,
                            ResolveTextureThroughCatalog: true));
                    }
                    catch (KeyNotFoundException)
                    {
                        if (directSource is not null && reference is ExportEntry)
                        {
                            dependencies.Add(new MaterialDependency(
                                reference,
                                directSource,
                                identity,
                                ResolveTextureThroughCatalog: false));
                        }
                        else
                        {
                            _relinkTargets[reference] = EnsureImport(canonicalPath, "Texture2D");
                            _warnings.Add(
                                $"Preserved stock texture import '{canonicalPath}' referenced by " +
                                $"'{source.InstancedFullPath}' because it is absent from the native corpus and texture database.");
                        }
                    }
                    continue;
                }

                var resolved = reference switch
                {
                    ExportEntry export => export,
                    ImportEntry import when oracleKind == MaterialOracleEntryKind.Export => _resolver.Require(
                        import,
                        $"oracle export dependency of '{source.InstancedFullPath}'"),
                    ImportEntry import when IsMaterialClass(import.ClassName) ||
                                            import.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) => _resolver.Require(
                        import,
                        $"material parent of '{source.InstancedFullPath}'"),
                    _ => null
                };
                if (resolved is null)
                {
                    _relinkTargets[reference] = EnsureImport(canonicalPath, reference.ClassName);
                    continue;
                }
                if (!dependencies.Any(value => ReferenceEquals(value.Source, resolved)))
                {
                    dependencies.Add(new MaterialDependency(
                        reference,
                        resolved,
                        null,
                        ResolveTextureThroughCatalog: false));
                }
            }
            return dependencies;
        }

        private static bool IsOwnedSubobject(ExportEntry candidate, ExportEntry owner)
        {
            for (IEntry? parent = candidate.Parent; parent is not null; parent = parent.Parent)
            {
                if (ReferenceEquals(parent, owner)) return true;
            }
            return false;
        }

        private IEntry EnsureImport(string path, string className)
        {
            if (PackageIntegrity.FindExactEntry(_destination, path, className) is { } existing)
            {
                return existing;
            }
            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                throw new InvalidDataException(
                    $"Cannot preserve malformed {className} import path '{path}'.");
            }
            // Package ancestors remain exports so a later real dependency can
            // be materialised below the same logical stock package without an
            // import/export ancestor collision. UE3 imports may be parented to
            // those exported package containers.
            var parent = PackageIntegrity.EnsurePackagePath(_destination, path);
            return _destination.CreateImport(
                className,
                NameReference.FromInstancedString(segments[^1]),
                parent);
        }

        private static bool IsPortableDependencyClass(string className) =>
            className.Equals("Material", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("MaterialFunction", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("MaterialFunctionInstance", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("TextureCube", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("TextureRenderTarget2D", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("TextureMovie", StringComparison.OrdinalIgnoreCase);

        private static string SourceKey(ExportEntry source) =>
            $"{Path.GetFullPath(source.FileRef.FilePath)}|{source.UIndex}|{source.ClassName}";

        private static string CanonicalPath(IMEPackage package, ExportEntry source)
        {
            var path = source.InstancedFullPath;
            if (MaterialDependencyOracle.Instance.GetKind(package.Game, source.ClassName, path) !=
                MaterialOracleEntryKind.Unknown)
            {
                return path;
            }
            var qualified = $"{Path.GetFileNameWithoutExtension(package.FilePath)}.{path}";
            if (MaterialDependencyOracle.Instance.GetKind(package.Game, source.ClassName, qualified) !=
                MaterialOracleEntryKind.Unknown)
            {
                return qualified;
            }
            return path.StartsWith("BIO", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("EffectsMaterials.", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("EngineMaterials.", StringComparison.OrdinalIgnoreCase)
                ? path
                : qualified;
        }

        private sealed record MaterialDependency(
            IEntry Reference,
            ExportEntry? Source,
            AssetIdentity? TextureIdentity,
            bool ResolveTextureThroughCatalog);
    }

    private static ExportEntry ResolveMaterialExport(IMEPackage source, AssetIdentity identity)
    {
        if (identity.UIndex > 0 && source.IsUExport(identity.UIndex) &&
            source.GetUExport(identity.UIndex) is { } indexed &&
            IsMaterialClass(indexed.ClassName) &&
            indexed.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase))
        {
            return indexed;
        }
        var matches = source.Exports.Where(export =>
                IsMaterialClass(export.ClassName) &&
                export.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Material '{identity.InstancedPath}' was {(matches.Length == 0 ? "not found" : "ambiguous")} in '{source.FilePath}'.");
    }

    private static bool IsMaterialClass(string className) =>
        className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("Material", StringComparison.OrdinalIgnoreCase);

    private static MorphFaceMaterialData GetScopeData(
        MorphFaceMaterialData source,
        IReadOnlyDictionary<string, MorphFaceMaterialData> scopes,
        string scope)
    {
        if (scopes.TryGetValue(scope, out var scoped))
        {
            return scoped;
        }
        // Older detached documents were unscoped.  They are safe to consume
        // only when the request contains one parameter namespace.
        if (scopes.Count == 0)
        {
            return source;
        }
        return new MorphFaceMaterialData([], [], []);
    }

    private static IReadOnlyList<string> WriteMaterialParameters(
        IMEPackage destination,
        ExportEntry material,
        ResolvedHeadMaterial template,
        MorphFaceMaterialData source,
        IReadOnlyList<TextureCatalogCandidate> textureCatalog,
        bool preferBiogTextures,
        ICollection<string> warnings)
    {
        var names = new List<string>();
        var properties = material.GetProperties();
        var supportedScalars = source.Scalars
            .Where(value => IsSerializableMaterialParameter(value.Name) &&
                            template.Supports(value.Name, MaterialParameterKind.Scalar))
            .ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var supportedVectors = source.Vectors
            .Where(value => IsSerializableMaterialParameter(value.Name) &&
                            template.Supports(value.Name, MaterialParameterKind.Vector))
            .ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var supportedTextures = source.Textures
            .Where(value => IsSerializableMaterialParameter(value.Name) &&
                            template.Supports(value.Name, MaterialParameterKind.Texture))
            .ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);

        if (supportedScalars.Count > 0 || template.SupportedScalars.Count > 0)
        {
            var existing = properties.GetProp<ArrayProperty<StructProperty>>("ScalarParameterValues")?.ToList()
                           ?? new List<StructProperty>();
            var values = existing.Where(value =>
                    IsSerializableMaterialParameter(ParameterName(value)) &&
                    !supportedScalars.ContainsKey(ParameterName(value)))
                .ToList();
            foreach (var value in supportedScalars.Values)
            {
                values.Add(new StructProperty("ScalarParameterValue", false,
                    ExpressionGuid(),
                    new NameProperty(value.Name, "ParameterName"),
                    new FloatProperty(value.Value, "ParameterValue")));
                names.Add(value.Name);
            }
            properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(values, "ScalarParameterValues"));
        }

        if (supportedVectors.Count > 0 || template.SupportedVectors.Count > 0)
        {
            var existing = properties.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues")?.ToList()
                           ?? new List<StructProperty>();
            var values = existing.Where(value =>
                    IsSerializableMaterialParameter(ParameterName(value)) &&
                    !supportedVectors.ContainsKey(ParameterName(value)))
                .ToList();
            foreach (var value in supportedVectors.Values)
            {
                values.Add(new StructProperty("VectorParameterValue", false,
                    ExpressionGuid(),
                    new StructProperty("LinearColor", false,
                        new FloatProperty(value.Value.X, "R"),
                        new FloatProperty(value.Value.Y, "G"),
                        new FloatProperty(value.Value.Z, "B"),
                        new FloatProperty(value.Value.W, "A"))
                    { Name = "ParameterValue", IsImmutable = true },
                    new NameProperty(value.Name, "ParameterName")));
                names.Add(value.Name);
            }
            properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(values, "VectorParameterValues"));
        }

        if (supportedTextures.Count > 0 || template.SupportedTextures.Count > 0)
        {
            var existing = properties.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues")?.ToList()
                           ?? new List<StructProperty>();
            var values = existing.Where(value =>
                    IsSerializableMaterialParameter(ParameterName(value)) &&
                    !supportedTextures.ContainsKey(ParameterName(value)))
                .ToList();
            foreach (var value in supportedTextures.Values)
            {
                if (value.TextureReference is null)
                {
                    continue;
                }
                var texture = ResolveAuthoredTexture(
                    destination,
                    value.TextureReference,
                    textureCatalog,
                    preferBiogTextures,
                    warnings);
                values.Add(new StructProperty("TextureParameterValue", false,
                    ExpressionGuid(),
                    new NameProperty(value.Name, "ParameterName"),
                    new ObjectProperty(texture, "ParameterValue")));
                names.Add(value.Name);
            }
            properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(values, "TextureParameterValues"));
        }

        material.WriteProperties(properties);
        return names;
    }

    private static IEntry ResolveAuthoredTexture(
        IMEPackage destination,
        AssetIdentity identity,
        IReadOnlyList<TextureCatalogCandidate> textureCatalog,
        bool preferBiogTextures,
        ICollection<string> warnings)
    {
        PccTextureSource? resolved = null;
        try
        {
            resolved = PccTextureDependencyResolver.Resolve(identity, textureCatalog, preferBiogTextures);
        }
        catch (KeyNotFoundException)
        {
            // Player-authored textures are not guaranteed to be represented in
            // the native GlobalMorphs corpus or the compact texture catalogue.
        }

        var canonicalPath = resolved?.InstancedPath ?? identity.InstancedPath;
        if (MaterialDependencyOracle.Instance.GetKind(destination.Game, "Texture2D", canonicalPath) ==
            MaterialOracleEntryKind.Import)
        {
            if (PackageIntegrity.FindExactEntry(destination, canonicalPath, "Texture2D") is { } existing)
            {
                return existing;
            }
            var segments = canonicalPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                throw new InvalidDataException($"Cannot preserve malformed Texture2D import path '{canonicalPath}'.");
            }
            var parent = PackageIntegrity.EnsurePackagePath(destination, canonicalPath);
            return destination.CreateImport(
                "Texture2D",
                NameReference.FromInstancedString(segments[^1]),
                parent);
        }

        return resolved is not null
            ? PccTextureDependencyResolver.Materialize(
                destination,
                identity,
                textureCatalog,
                preferBiogTextures,
                warnings)
            : PccTextureDependencyResolver.MaterializeDirect(destination, identity, warnings);
    }

    private static string ParameterName(StructProperty value) =>
        value.GetProp<NameProperty>("ParameterName")?.Value.Instanced ?? string.Empty;

    internal static bool IsSerializableMaterialParameter(string name) =>
        !name.Trim().Equals("SelectionColor", StringComparison.OrdinalIgnoreCase);

    private static StructProperty ExpressionGuid()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return new StructProperty("Guid", false,
            new IntProperty(BitConverter.ToInt32(bytes, 0), "A"),
            new IntProperty(BitConverter.ToInt32(bytes, 4), "B"),
            new IntProperty(BitConverter.ToInt32(bytes, 8), "C"),
            new IntProperty(BitConverter.ToInt32(bytes, 12), "D"))
        { Name = "ExpressionGUID", IsImmutable = true };
    }

    private static string MakeMaterialName(string meshName, string materialName, ISet<string> used)
    {
        var baseName = $"{SanitizeName(meshName)}_{materialName}_MAT";
        var name = baseName;
        var suffix = 2;
        while (!used.Add(name))
        {
            name = $"{baseName}_{suffix++}";
        }
        return name;
    }

    private static string MaterialToken(CustomMaterialAssignmentOption option)
    {
        var scope = option.EffectiveParameterScopeKey.ToLowerInvariant();
        return (scope, option.Family) switch
        {
            ("human", HeadMaterialFamily.Skin) when
                IsHumanMale(option) => "HMMFace",
            ("human", HeadMaterialFamily.Skin) => "HMFFace",
            ("human", HeadMaterialFamily.Scalp) when
                IsHumanMale(option) => "HMMScalp",
            ("human", HeadMaterialFamily.Scalp) => "HMFScalp",
            ("human", HeadMaterialFamily.Eyes) when
                IsHumanMale(option) => "HMMEye",
            ("human", HeadMaterialFamily.Eyes) => "HMF-ASAEye",
            ("human", HeadMaterialFamily.Lashes) when
                IsHumanMale(option) => "HMMLash",
            ("human", HeadMaterialFamily.Lashes) => "HMF-ASALash",
            ("human", HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair) when
                option.Id.Contains("human-iconic-femshep-hair", StringComparison.OrdinalIgnoreCase) => "IconicHair",
            ("human", HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair) => "Hair",
            ("asari", HeadMaterialFamily.Skin or HeadMaterialFamily.AsariSkin) => "ASAFace",
            ("salarian", HeadMaterialFamily.Skin or HeadMaterialFamily.SalarianSkin) => "SALFace",
            ("salarian", HeadMaterialFamily.Eyes or HeadMaterialFamily.SalarianEyes) => "SALEye",
            ("turian", HeadMaterialFamily.Skin or HeadMaterialFamily.TurianSkin) => "TURFace",
            ("turian", HeadMaterialFamily.Eyes or HeadMaterialFamily.TurianEyes) => "TUREye",
            ("krogan", HeadMaterialFamily.Skin or HeadMaterialFamily.KroganSkin) => "KROFace",
            ("krogan", HeadMaterialFamily.Eyes or HeadMaterialFamily.KroganEyes) => "KROEye",
            ("batarian", HeadMaterialFamily.Skin or HeadMaterialFamily.BatarianSkin) => "BATFace",
            ("vorcha", HeadMaterialFamily.Skin or HeadMaterialFamily.VorchaSkin) => "ALNFace",
            ("vorcha", HeadMaterialFamily.Eyes or HeadMaterialFamily.VorchaEyes or HeadMaterialFamily.TurianEyes) => "ALNEye",
            _ => throw new InvalidDataException(
                $"Material '{option.Label}' has no PCC naming token for scope '{scope}' and family '{option.Family}'.")
        };
    }

    private static bool IsHumanMale(CustomMaterialAssignmentOption option) =>
        option.AppearanceCompatibilityKey.Equals("human-male", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeName(string value)
    {
        var chars = value.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray();
        var result = chars.Length == 0 ? "Material" : new string(chars);
        return char.IsLetter(result[0]) || result[0] == '_' ? result : $"_{result}";
    }

    private static void Verify(
        string path,
        CustomMeshPccSaveRequest request,
        IReadOnlyList<CustomMeshPccMaterialResult> expectedMaterials,
        string expectedMeshPath)
    {
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        if (package.Game != ToMeGame(request.Game))
        {
            throw new InvalidDataException("The custom mesh PCC was written for the wrong game.");
        }
        var root = package.FindExport(PackageRootName, "Package")
                   ?? throw new InvalidDataException("The custom mesh PCC has no MorphFaceEditor package export.");
        var mesh = package.FindExport(expectedMeshPath, "SkeletalMesh")
                   ?? throw new InvalidDataException("The custom mesh PCC has no written SkeletalMesh export.");
        if (!ReferenceEquals(mesh.Parent, root))
        {
            throw new InvalidDataException("The written SkeletalMesh is outside the MorphFaceEditor package.");
        }
        foreach (var expected in expectedMaterials)
        {
            var material = package.FindExport(expected.MaterialPath)
                           ?? throw new InvalidDataException(
                               $"The written material '{expected.MaterialPath}' could not be reopened.");
            if (!ReferenceEquals(material.Parent, root))
            {
                throw new InvalidDataException(
                    $"The written material '{expected.MaterialPath}' is outside the MorphFaceEditor package.");
            }
        }
        PackageIntegrity.Verify(package);
    }

    private static void ValidateRequest(CustomMeshPccSaveRequest request)
    {
        if (request.Game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3))
        {
            throw new InvalidDataException($"Custom mesh PCC writing does not support {request.Game}.");
        }
        ValidateObjectName(request.MeshName);
        if (request.Workspace.Source != request.SourceMesh)
        {
            throw new InvalidOperationException("The custom workspace does not belong to the supplied mesh asset.");
        }
        if (request.Workspace.Assignments.Count == 0)
        {
            throw new InvalidDataException("At least one custom material assignment is required.");
        }
        if (request.Workspace.Assignments.Select(value => value.Slot.MaterialIndex).Distinct().Count() !=
            request.Workspace.Assignments.Count)
        {
            throw new InvalidDataException("Custom material assignments contain duplicate material slots.");
        }
    }

    private static void ValidateObjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(char.IsLetter(value[0]) || value[0] == '_') ||
            value.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                "The mesh export name may contain letters, digits, and underscores, and cannot begin with a digit.",
                nameof(value));
        }
    }

    private static MEGame ToMeGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => MEGame.LE1,
        MorphFaceGame.LE2 => MEGame.LE2,
        MorphFaceGame.LE3 => MEGame.LE3,
        _ => throw new InvalidDataException($"Unsupported source game '{game}'.")
    };

    private static void AtomicReplace(string temporaryPath, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(temporaryPath, destination);
            return;
        }
        var backup = $"{destination}.{Guid.NewGuid():N}.backup";
        try
        {
            File.Replace(temporaryPath, destination, backup, ignoreMetadataErrors: true);
            File.Delete(backup);
        }
        catch
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, destination, overwrite: true);
                File.Delete(backup);
            }
            throw;
        }
    }
}
