using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Owns dependency import, canonical PCC identity/role policy, relinking, and
/// saved-package graph verification for every newly constructed asset package.
/// Format-specific callers supply only their authored roots and binary writers.
/// </summary>
internal static class PccPackageWorkflow
{
    internal static ExportEntry ImportDependencyGraph(
        IMEPackage destination,
        ExportEntry source,
        IEntry? destinationParent,
        IReadOnlyList<TextureCatalogCandidate>? textureCatalog,
        bool preferBiogTextures,
        ICollection<string>? warnings = null,
        bool applyCorpusMaterialPolicy = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Game != destination.Game)
        {
            throw new InvalidDataException(
                $"Cannot import {source.ClassName} '{source.InstancedFullPath}' from {source.Game} into {destination.Game}.");
        }
        using var session = new GraphImportSession(
            destination,
            textureCatalog ?? [],
            preferBiogTextures,
            warnings,
            applyCorpusMaterialPolicy);
        return session.ImportRoot(source, destinationParent);
    }

    internal static PccDependencyGraphSnapshot CaptureDependencyGraph(ExportEntry root) =>
        PccDependencyGraphSnapshot.Capture(root);

    /// <summary>
    /// Ports an ordinary PCC root using Legendary Explorer's mature parent and
    /// linker policy. Callers retain their source hierarchy; the corpus oracle
    /// is not used as a generic path builder.
    /// </summary>
    internal static ExportEntry ImportRootPreservingStructure(
        IMEPackage destination,
        ExportEntry source,
        ICollection<string>? warnings = null,
        bool checkImportsWhenExportingToPackage = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ExternalSkeletalMeshMaterializer.PrepareReferencedPackagePaths(destination, source);
        var issues = EntryExporter.ExportExportToPackage(
            source,
            destination,
            out var staged,
            customROP: new RelinkerOptionsPackage
            {
                ImportExportDependencies = true,
                GenerateImportsForGlobalFiles = false,
                CheckImportsWhenExportingToPackage = checkImportsWhenExportingToPackage
            });
        if (warnings is not null)
        {
            foreach (var issue in issues)
            {
                warnings.Add(issue.Message);
            }
        }
        return staged as ExportEntry ?? throw new InvalidDataException(
            $"LEC did not stage {source.ClassName} '{source.InstancedFullPath}' as an export.");
    }

    /// <summary>
    /// Imports an asset whose binary will immediately be decoded and rewritten
    /// for another game. This is the only cross-game import exception: it keeps
    /// dependency/relink verification inside the PCC workflow while the caller
    /// owns the explicit format conversion.
    /// </summary>
    internal static ExportEntry ImportForPackageStorageConversion(
        IMEPackage destination,
        ExportEntry source,
        IEntry? destinationParent,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ReserveReferencePackagePaths(destination, source);
        ExternalSkeletalMeshMaterializer.PrepareReferencedPackagePaths(destination, source);
        var relinker = new RelinkerOptionsPackage
        {
            ImportExportDependencies = true,
            GenerateImportsForGlobalFiles = false,
            PortImportsMemorySafe = true
        };
        var imported = EntryImporter.ImportExport(
            destination,
            source,
            destinationParent?.UIndex ?? 0,
            relinker);
        MaterialisationVerifier.Relink(relinker);
        if (imported is not ExportEntry root ||
            !root.ClassName.Equals(source.ClassName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"LEC did not stage {source.ClassName} '{source.InstancedFullPath}' for package-storage conversion.");
        }
        MaterialisationVerifier.Verify(root, source.Game, relinker, warnings);
        return root;
    }

    internal static IEntry ResolveTextureReference(
        IMEPackage destination,
        AssetIdentity identity,
        IReadOnlyList<TextureCatalogCandidate>? textureCatalog,
        bool preferBiogTextures,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(identity);
        var catalog = textureCatalog ?? [];
        PccTextureSource? resolved = null;
        if (catalog.Count > 0)
        {
            try
            {
                resolved = PccTextureDependencyResolver.Resolve(identity, catalog, preferBiogTextures);
            }
            catch (KeyNotFoundException)
            {
                // An exact direct donor remains valid for user-authored textures
                // that are intentionally outside the installed registry.
            }
        }
        var path = resolved?.InstancedPath ??
                   PccAssetPathPolicy.FromDonorOccurrence(identity.InstancedPath, identity.PackagePath);
        var oracleKind = MaterialDependencyOracle.Instance.GetKind(destination.Game, "Texture2D", path);
        if (oracleKind == MaterialOracleEntryKind.Import)
        {
            return EnsureImport(destination, path, "Texture2D");
        }
        if (PackageIntegrity.FindExactEntry(destination, path, "Texture2D") is { } existing)
        {
            if (oracleKind == MaterialOracleEntryKind.Export && existing is ImportEntry)
            {
                throw new InvalidDataException(
                    $"The corpus requires Texture2D '{path}' to be an export, but an import occupies that identity.");
            }
            return existing;
        }

        if (resolved is not null)
        {
            return PccTextureDependencyResolver.MaterializeResolved(destination, resolved, warnings);
        }
        return PccTextureDependencyResolver.MaterializeDirect(
            destination,
            identity with { InstancedPath = path },
            warnings);
    }

    internal static void VerifyDependencyGraph(
        IMEPackage reopened,
        PccDependencyGraphSnapshot expected,
        bool verifyCorpusRoles = true)
    {
        expected.Verify(reopened);
        if (verifyCorpusRoles)
        {
            VerifyCorpusRoles(reopened);
        }
    }

    internal static void AtomicReplace(string temporaryPath, string destination)
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

    /// <summary>
    /// Builds exports as shells first, then relinks the completed graph. LEC's
    /// recursive importer otherwise commits import/export roles before the
    /// corpus policy can see them, making the result depend on material-slot
    /// order. Shell-first planning also makes cycles and shared nodes explicit.
    /// </summary>
    private sealed class GraphImportSession : IDisposable
    {
        private readonly IMEPackage _destination;
        private readonly IReadOnlyList<TextureCatalogCandidate> _catalog;
        private readonly bool _preferBiogTextures;
        private readonly ICollection<string>? _warnings;
        private readonly bool _applyCorpusMaterialPolicy;
        private readonly PackageCache _cache = new() { CacheMaxSize = 64 };
        private readonly GamePackageReferenceResolver _resolver;
        private readonly RelinkerOptionsPackage _relinker;
        private readonly Dictionary<string, IEntry> _destinations = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(ExportEntry Source, ExportEntry Destination)> _textures = [];

        internal GraphImportSession(
            IMEPackage destination,
            IReadOnlyList<TextureCatalogCandidate> catalog,
            bool preferBiogTextures,
            ICollection<string>? warnings,
            bool applyCorpusMaterialPolicy)
        {
            _destination = destination;
            _catalog = catalog;
            _preferBiogTextures = preferBiogTextures;
            _warnings = warnings;
            _applyCorpusMaterialPolicy = applyCorpusMaterialPolicy;
            _resolver = new GamePackageReferenceResolver(_cache);
            _relinker = new RelinkerOptionsPackage(_cache)
            {
                ImportExportDependencies = false,
                GenerateImportsForGlobalFiles = false,
                PortImportsMemorySafe = false
            };
        }

        internal ExportEntry ImportRoot(ExportEntry source, IEntry? destinationParent)
        {
            // The caller-selected parent is the destination counterpart of the
            // source root's parent. Seed that relationship before planning any
            // sibling dependencies (for example BioMaterialOverride children of
            // a BioMorphFace container), otherwise their native parent can be
            // re-created at the unqualified source path.
            if (source.Parent is { } sourceParent && destinationParent is not null)
            {
                _relinker.CrossPackageMap[sourceParent] = destinationParent;
            }
            var targetPath = destinationParent is null
                ? source.ObjectName.Instanced
                : $"{destinationParent.InstancedFullPath}.{source.ObjectName.Instanced}";
            var root = MaterializeExport(source, targetPath, destinationParent, forceExport: true) as ExportEntry
                       ?? throw new InvalidDataException(
                           $"The authored {source.ClassName} '{source.InstancedFullPath}' was not staged as an export.");
            MaterialisationVerifier.Relink(_relinker);
            MaterialisationVerifier.Verify(root, source.Game, _relinker, _warnings);
            foreach (var (textureSource, textureDestination) in _textures)
            {
                PccTextureDependencyResolver.VerifyBulkDataPreserved(textureSource, textureDestination);
            }
            if (_applyCorpusMaterialPolicy)
            {
                VerifyCorpusRoles(_destination);
            }
            return root;
        }

        private IEntry PlanReference(IEntry reference)
        {
            if (_relinker.CrossPackageMap.TryGetValue(reference, out var mapped))
            {
                return mapped;
            }

            var canonicalPath = DependencyPath(reference);
            if (reference.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            {
                return PlanTexture(reference, canonicalPath);
            }

            var expected = ExpectedKind(reference.ClassName, canonicalPath);
            var targetPath = expected == MaterialOracleEntryKind.Unknown &&
                             reference is ExportEntry &&
                             reference.Parent is { } sourceParent &&
                             _relinker.CrossPackageMap.TryGetValue(sourceParent, out var destinationParent)
                ? $"{destinationParent.InstancedFullPath}.{reference.ObjectName.Instanced}"
                : canonicalPath;
            if (expected == MaterialOracleEntryKind.Import ||
                reference is ImportEntry && expected == MaterialOracleEntryKind.Unknown)
            {
                var imported = EnsureImport(_destination, targetPath, reference.ClassName);
                _relinker.CrossPackageMap[reference] = imported;
                return imported;
            }

            var source = reference as ExportEntry ?? _resolver.Require(
                reference,
                $"corpus export dependency of '{canonicalPath}'");
            var materialized = MaterializeExport(source, targetPath, destinationParent: null, forceExport: false);
            _relinker.CrossPackageMap[reference] = materialized;
            return materialized;
        }

        private IEntry PlanTexture(IEntry reference, string canonicalPath)
        {
            var resolved = _resolver.Resolve(reference);
            var candidate = FindTextureCandidate(_catalog, reference, resolved, canonicalPath);
            PccTextureSource? selection = null;
            if (candidate is not null)
            {
                selection = PccTextureDependencyResolver.Resolve(
                    new AssetIdentity(
                        resolved?.FileRef.FilePath ?? reference.FileRef.FilePath,
                        candidate.InstancedPath,
                        resolved?.UIndex ?? 0,
                        "Texture2D"),
                    _catalog,
                    _preferBiogTextures);
            }
            var targetPath = selection?.InstancedPath ?? canonicalPath;
            var expected = ExpectedKind("Texture2D", targetPath);
            if (expected == MaterialOracleEntryKind.Import)
            {
                var imported = EnsureImport(_destination, targetPath, "Texture2D");
                _relinker.CrossPackageMap[reference] = imported;
                return imported;
            }

            ExportEntry? source = resolved;
            if (selection is not null)
            {
                var donor = _cache.GetCachedPackage(selection.Occurrence.PackagePath)
                            ?? throw new FileNotFoundException(
                                $"The installed texture donor package is missing: {selection.Occurrence.PackagePath}",
                                selection.Occurrence.PackagePath);
                source = PccTextureDependencyResolver.ResolveSourceExport(
                    donor,
                    selection.InstancedPath,
                    selection.Occurrence.ExportUIndex);
            }
            if (source is null)
            {
                if (expected == MaterialOracleEntryKind.Export)
                {
                    throw new InvalidDataException(
                        $"The corpus requires Texture2D '{targetPath}' to be an export, but no exact donor could be resolved.");
                }
                var imported = EnsureImport(_destination, targetPath, "Texture2D");
                _relinker.CrossPackageMap[reference] = imported;
                _warnings?.Add(
                    $"Preserved unresolved stock Texture2D import '{targetPath}' while constructing the PCC.");
                return imported;
            }

            var materialized = MaterializeExport(source, targetPath, destinationParent: null, forceExport: false);
            _relinker.CrossPackageMap[reference] = materialized;
            return materialized;
        }

        private IEntry MaterializeExport(
            ExportEntry source,
            string targetPath,
            IEntry? destinationParent,
            bool forceExport)
        {
            if (_relinker.CrossPackageMap.TryGetValue(source, out var mapped))
            {
                return mapped;
            }
            var expected = ExpectedKind(source.ClassName, targetPath);
            if (!forceExport && expected == MaterialOracleEntryKind.Import)
            {
                var imported = EnsureImport(_destination, targetPath, source.ClassName);
                _relinker.CrossPackageMap[source] = imported;
                return imported;
            }

            var identityKey = $"{source.ClassName}|{targetPath}";
            if (_destinations.TryGetValue(identityKey, out var planned))
            {
                _relinker.CrossPackageMap[source] = planned;
                return planned;
            }
            if (PackageIntegrity.FindExactEntry(_destination, targetPath, source.ClassName) is { } existing)
            {
                if (existing is not ExportEntry)
                {
                    if (!forceExport && !_applyCorpusMaterialPolicy)
                    {
                        _destinations[identityKey] = existing;
                        _relinker.CrossPackageMap[source] = existing;
                        return existing;
                    }
                    throw new InvalidDataException(
                        $"Cannot materialise {source.ClassName} '{targetPath}': an import occupies the required export identity.");
                }
                _destinations[identityKey] = existing;
                _relinker.CrossPackageMap[source] = existing;
                return existing;
            }

            var parent = destinationParent ?? ResolveDestinationParent(source, targetPath);
            var importedEntry = EntryImporter.ImportExport(
                _destination,
                source,
                parent?.UIndex ?? 0,
                _relinker);
            if (importedEntry is not ExportEntry export ||
                !export.ClassName.Equals(source.ClassName, StringComparison.OrdinalIgnoreCase) ||
                !export.InstancedFullPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"LEC staged {source.ClassName} '{source.InstancedFullPath}' as " +
                    $"{importedEntry.ClassName} '{importedEntry.InstancedFullPath}', expected '{targetPath}' " +
                    $"under '{parent?.InstancedFullPath ?? "<root>"}' " +
                    $"(source parent: {source.Parent?.ClassName ?? "<none>"} '{source.Parent?.InstancedFullPath ?? "<none>"}').");
            }
            _destinations[identityKey] = export;
            _relinker.CrossPackageMap[source] = export;
            if (source.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            {
                _textures.Add((source, export));
            }

            foreach (var uIndex in PackageIntegrity.References(source).Values
                         .Where(value => value != 0)
                         .Distinct())
            {
                if (source.FileRef.GetEntry(uIndex) is { } reference)
                {
                    _ = PlanReference(reference);
                }
            }
            return export;
        }

        private IEntry? ResolveDestinationParent(ExportEntry source, string targetPath)
        {
            if (source.Parent is ExportEntry sourceParent &&
                !sourceParent.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase))
            {
                var parentPath = targetPath.Contains('.')
                    ? targetPath[..targetPath.LastIndexOf('.')]
                    : DependencyPath(sourceParent);
                return MaterializeExport(sourceParent, parentPath, destinationParent: null, forceExport: false);
            }
            return PackageIntegrity.EnsurePackagePath(_destination, targetPath, source.ClassName);
        }

        private MaterialOracleEntryKind ExpectedKind(string className, string path) =>
            _applyCorpusMaterialPolicy
                ? MaterialDependencyOracle.Instance.GetKind(_destination.Game, className, path)
                : MaterialOracleEntryKind.Unknown;

        private string DependencyPath(IEntry entry) =>
            _applyCorpusMaterialPolicy || entry is ImportEntry
                ? CanonicalPath(entry)
                : PccAssetPathPolicy.FromDonorOccurrence(
                    entry.InstancedFullPath,
                    entry.FileRef.FilePath);

        public void Dispose() => _cache.Dispose();
    }

    private static TextureCatalogCandidate? FindTextureCandidate(
        IReadOnlyList<TextureCatalogCandidate> catalog,
        IEntry referenced,
        ExportEntry? resolved,
        string canonicalPath)
    {
        var exact = catalog.Where(value =>
                value.InstancedPath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (exact.Length > 1)
        {
            throw new InvalidDataException(
                $"Texture2D '{canonicalPath}' has multiple exact catalogue identities.");
        }
        if (exact.Length == 1)
        {
            return exact[0];
        }
        exact = catalog.Where(value =>
                value.InstancedPath.Equals(referenced.InstancedFullPath, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (exact.Length > 1)
        {
            throw new InvalidDataException(
                $"Texture2D '{referenced.InstancedFullPath}' has multiple exact catalogue identities.");
        }
        if (exact.Length == 1)
        {
            return exact[0];
        }
        if (resolved is null)
        {
            return null;
        }

        var resolvedPath = Path.GetFullPath(resolved.FileRef.FilePath);
        var occurrenceMatches = catalog.Where(candidate => candidate.Occurrences
                .Append(candidate.EffectiveOccurrence)
                .Any(occurrence =>
                    occurrence.ExportUIndex == resolved.UIndex &&
                    Path.GetFullPath(occurrence.PackagePath).Equals(
                        resolvedPath,
                        StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .Take(2)
            .ToArray();
        return occurrenceMatches.Length switch
        {
            0 => null,
            1 => occurrenceMatches[0],
            _ => throw new InvalidDataException(
                $"Texture2D '{canonicalPath}' maps to multiple exact catalogue identities.")
        };
    }

    private static void VerifyCorpusRoles(IMEPackage package)
    {
        foreach (var entry in package.Exports.Cast<IEntry>().Concat(package.Imports))
        {
            if (entry.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var expected = MaterialDependencyOracle.Instance.GetKind(
                package.Game,
                entry.ClassName,
                CanonicalPath(entry));
            if (expected == MaterialOracleEntryKind.Unknown)
            {
                continue;
            }
            var actual = entry is ExportEntry
                ? MaterialOracleEntryKind.Export
                : MaterialOracleEntryKind.Import;
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"Native corpus role mismatch for {entry.ClassName} '{CanonicalPath(entry)}': " +
                    $"expected {expected}, saved {actual}.");
            }
        }
    }

    private static IEntry EnsureImport(IMEPackage destination, string path, string className)
    {
        if (PackageIntegrity.FindExactEntry(destination, path, className) is { } existing)
        {
            if (existing is ExportEntry)
            {
                throw new InvalidDataException(
                    $"Cannot preserve {className} import '{path}': an export occupies the native import identity.");
            }
            return existing;
        }

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            throw new InvalidDataException($"Cannot preserve malformed {className} import path '{path}'.");
        }
        // The asset is an import, but the enclosing BIOG package can be an
        // export in a packaged PCC when another donor asset is embedded under
        // the same identity. Reuse the actual parent role, including imported
        // LE1 Human chains staged by the source face. New package ancestors
        // are exports unless they are already beneath an import; planning them
        // as imports would make later embedded assets depend on slot order.
        IEntry? parent = null;
        var ancestorPath = string.Empty;
        foreach (var segment in segments[..^1])
        {
            ancestorPath = ancestorPath.Length == 0
                ? segment
                : $"{ancestorPath}.{segment}";
            var existingParent = PackageIntegrity.FindExactEntry(destination, ancestorPath, "Package");
            if (existingParent is not null)
            {
                parent = existingParent;
                continue;
            }
            if (parent is not ImportEntry)
            {
                parent = destination.CreatePackageExport(
                    NameReference.FromInstancedString(segment), parent as ExportEntry);
            }
            else
            {
                parent = destination.CreateImport(
                    "Package", NameReference.FromInstancedString(segment), parent);
            }
        }
        return destination.CreateImport(
            className,
            NameReference.FromInstancedString(segments[^1]),
            parent);
    }

    private static void ReserveReferencePackagePaths(IMEPackage destination, ExportEntry source)
    {
        foreach (var reference in EntryImporter.GetAllReferencesOfExport(source).Append(source))
        {
            if (reference.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var path = CanonicalPath(reference);
            if (path.Contains('.') &&
                !path.StartsWith("Core.", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("SFXGame.", StringComparison.OrdinalIgnoreCase))
            {
                _ = PackageIntegrity.EnsurePackagePath(destination, path, reference.ClassName);
            }
        }
    }

    private static string CanonicalPath(IEntry entry)
    {
        if (entry is ImportEntry)
        {
            return entry.InstancedFullPath;
        }
        var path = entry.InstancedFullPath;
        if (MaterialDependencyOracle.Instance.GetKind(entry.Game, entry.ClassName, path) !=
            MaterialOracleEntryKind.Unknown)
        {
            return path;
        }
        var qualified = $"{Path.GetFileNameWithoutExtension(entry.FileRef.FilePath)}.{path}";
        return MaterialDependencyOracle.Instance.GetKind(entry.Game, entry.ClassName, qualified) !=
               MaterialOracleEntryKind.Unknown
            ? qualified
            : path.StartsWith("BIO", StringComparison.OrdinalIgnoreCase) ||
              path.StartsWith("EffectsMaterials.", StringComparison.OrdinalIgnoreCase) ||
              path.StartsWith("EngineMaterials.", StringComparison.OrdinalIgnoreCase)
                ? path
                : qualified;
    }
}

/// <summary>
/// Path-based snapshot of a complete reachable PCC dependency graph. It is
/// captured before save and compared with the reopened package so serialization
/// cannot silently remove or redirect a required edge.
/// </summary>
internal sealed record PccDependencyGraphSnapshot(
    string RootClass,
    string RootPath,
    IReadOnlyList<PccDependencyNodeSnapshot> Nodes)
{
    internal static PccDependencyGraphSnapshot Capture(ExportEntry root)
    {
        var nodes = new List<PccDependencyNodeSnapshot>();
        var pending = new Queue<ExportEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            var key = Key(current);
            if (!visited.Add(key))
            {
                continue;
            }

            var references = new Dictionary<string, PccDependencyReferenceSnapshot>(StringComparer.Ordinal);
            foreach (var (site, uIndex) in PackageIntegrity.References(current))
            {
                var target = current.FileRef.GetEntry(uIndex);
                references.Add(site, new PccDependencyReferenceSnapshot(
                    target?.ClassName,
                    target?.InstancedFullPath,
                    target is ExportEntry,
                    uIndex == 0));
                if (target is ExportEntry targetExport)
                {
                    pending.Enqueue(targetExport);
                }
            }
            nodes.Add(new PccDependencyNodeSnapshot(
                current.ClassName,
                current.InstancedFullPath,
                references));
        }

        return new PccDependencyGraphSnapshot(root.ClassName, root.InstancedFullPath, nodes);
    }

    internal void Verify(IMEPackage reopened)
    {
        var root = reopened.FindExport(RootPath, RootClass)
                   ?? throw new InvalidDataException(
                       $"Dependency root {RootClass} '{RootPath}' was not saved.");
        var expectedByKey = Nodes.ToDictionary(
            node => Key(node.ClassName, node.Path),
            StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<ExportEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            var key = Key(current);
            if (!visited.Add(key))
            {
                continue;
            }
            if (!expectedByKey.TryGetValue(key, out var expected))
            {
                throw new InvalidDataException(
                    $"Saved dependency graph gained unexpected export {current.ClassName} '{current.InstancedFullPath}'.");
            }

            var actualReferences = PackageIntegrity.References(current);
            if (actualReferences.Count != expected.References.Count ||
                actualReferences.Keys.Except(expected.References.Keys, StringComparer.Ordinal).Any())
            {
                throw new InvalidDataException(
                    $"Saved dependency {current.ClassName} '{current.InstancedFullPath}' changed its reference slots.");
            }
            foreach (var (site, expectedReference) in expected.References)
            {
                var actualIndex = actualReferences[site];
                var actual = reopened.GetEntry(actualIndex);
                if (expectedReference.IsNull)
                {
                    if (actualIndex != 0)
                    {
                        throw new InvalidDataException(
                            $"Saved dependency '{current.InstancedFullPath}' changed null reference {site}.");
                    }
                    continue;
                }
                if (actual is null ||
                    actual is ExportEntry != expectedReference.IsExport ||
                    !actual.ClassName.Equals(expectedReference.ClassName, StringComparison.OrdinalIgnoreCase) ||
                    !actual.InstancedFullPath.Equals(expectedReference.Path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Saved dependency '{current.InstancedFullPath}' changed {site}: expected " +
                        $"{expectedReference.ClassName} '{expectedReference.Path}', got " +
                        $"{actual?.ClassName ?? "None"} '{actual?.InstancedFullPath ?? "None"}'.");
                }
                if (actual is ExportEntry actualExport)
                {
                    pending.Enqueue(actualExport);
                }
            }
        }

        var missing = expectedByKey.Keys.Except(visited, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Saved dependency graph lost {missing.Length} reachable exports; first missing: {missing[0]}.");
        }
    }

    private static string Key(IEntry entry) => Key(entry.ClassName, entry.InstancedFullPath);
    private static string Key(string className, string path) => $"{className}|{path}";
}

internal sealed record PccDependencyNodeSnapshot(
    string ClassName,
    string Path,
    IReadOnlyDictionary<string, PccDependencyReferenceSnapshot> References);

internal sealed record PccDependencyReferenceSnapshot(
    string? ClassName,
    string? Path,
    bool IsExport,
    bool IsNull);
