using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly ConditionalWeakTable<IMEPackage, ExplicitTextureLeafState> ExplicitTextureLeaves = new();
    private static readonly ConditionalWeakTable<IMEPackage, HashSet<string>> PersistedTextureLeafNames = new();
    private const string ExplicitTextureNamePrefix = "MFE_ExplicitTexture2D_";

    internal static ExportEntry ImportDependencyGraph(
        IMEPackage destination,
        ExportEntry source,
        IEntry? destinationParent,
        IReadOnlyList<TextureCatalogCandidate>? textureCatalog,
        bool preferBiogTextures,
        ICollection<string>? warnings = null,
        bool applyCorpusMaterialPolicy = true,
        IReadOnlyList<IEntry>? requiredExportReferences = null,
        string? targetPathOverride = null,
        InstalledGameAssetResolver? installedAssets = null,
        IReadOnlyList<ExportEntry>? authoredMaterialDonors = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Game != destination.Game)
        {
            throw new InvalidDataException(
                $"Cannot import {source.ClassName} '{source.InstancedFullPath}' from {source.Game} into {destination.Game}.");
        }
        var preexistingExports = destination.Exports.ToHashSet();
        using var session = new GraphImportSession(
            destination,
            textureCatalog ?? [],
            preferBiogTextures,
            warnings,
            applyCorpusMaterialPolicy,
            requiredExportReferences ?? [],
            installedAssets,
            authoredMaterialDonors ?? []);
        var imported = session.ImportRoot(source, destinationParent, targetPathOverride);
        NormalizeNewSupportedAssets(destination, preexistingExports);
        return imported;
    }

    internal static PccDependencyGraphSnapshot CaptureDependencyGraph(ExportEntry root)
    {
        var explicitLeaves = GetExplicitTextureLeaves(root.FileRef);
        VerifyExplicitTextureLeaves(root.FileRef, explicitLeaves);
        return PccDependencyGraphSnapshot.Capture(root, explicitLeaves);
    }

    /// <summary>
    /// The Package Editor's Clone All References endpoint, applied only after
    /// materialisation and reference editing have finished in an in-memory PCC.
    /// No donor selection or material policy is repeated during this final copy.
    /// </summary>
    internal static ExportEntry CloneCompletedRoot(
        IMEPackage destination,
        ExportEntry source,
        ICollection<string>? warnings = null,
        ExportEntry? replacementRoot = null)
    {
        if (source.Game != destination.Game)
            throw new InvalidDataException("The completed PCC must be cloned into the same game.");
        var sourceEntries = DependencyEntries(source);
        var preexistingEntries = destination.Exports.Cast<IEntry>().Concat(destination.Imports).ToHashSet();
        foreach (var entry in sourceEntries.Where(entry => entry.ClassName != "Package"))
        {
            var occupying = destination.FindEntry(entry.InstancedFullPath);
            if (occupying is not null && (occupying.ClassName != entry.ClassName ||
                (occupying is ExportEntry) != (entry is ExportEntry)))
                throw new InvalidDataException(
                    $"Cannot clone {entry.ClassName} '{entry.InstancedFullPath}': a conflicting import/export identity occupies the destination.");
        }

        using var cache = new PackageCache();
        var relinker = new RelinkerOptionsPackage
        {
            Cache = cache,
            ImportExportDependencies = true,
            GenerateImportsForGlobalFiles = false,
            ImportChildrenOfPackages = false
        };
        var preservedExports = preexistingEntries.OfType<ExportEntry>().ToHashSet();
        ExportEntry root;
        if (replacementRoot is null)
        {
            var parent = EntryExporter.PortParents(source, destination, cache: cache, customROP: relinker);
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies,
                source, destination, parent, true, relinker, out var cloned);
            root = cloned as ExportEntry ?? throw new InvalidDataException("LEC did not clone the completed root as an export.");
        }
        else
        {
            if (source.ClassName != "BioMorphFace" || replacementRoot.ClassName != "BioMorphFace" ||
                !ReferenceEquals(replacementRoot.FileRef, destination))
                throw new InvalidDataException("Only the destination BioMorphFace and its BioMaterialOverride can be replaced.");
            var incomingOverride = MorphOverride(source);
            var existingOverride = MorphOverride(replacementRoot);
            relinker.CrossPackageMap[source] = replacementRoot;
            relinker.CrossPackageMap[incomingOverride] = existingOverride;
            preservedExports.Remove(replacementRoot);
            preservedExports.Remove(existingOverride);
            foreach (var (incoming, target) in new[] { (source, replacementRoot), (incomingOverride, existingOverride) })
                if (!EntryImporter.ReplaceExportDataWithAnother(incoming, target, relinker))
                    throw new InvalidDataException($"LEC could not replace '{target.InstancedFullPath}'.");
            MaterialisationVerifier.Relink(relinker, preservedExports);
            root = replacementRoot;
        }
        MaterialisationVerifier.Verify(root, source.Game, relinker, warnings, preservedExports: preservedExports);
        foreach (var texture in sourceEntries.OfType<ExportEntry>().Where(entry => entry.ClassName == "Texture2D"))
        {
            var target = relinker.CrossPackageMap.TryGetValue(texture, out var mapped)
                ? mapped : destination.FindEntry(texture.InstancedFullPath, texture.ClassName);
            if (target is ExportEntry copied && !preservedExports.Contains(copied))
                PccTextureDependencyResolver.VerifyBulkDataPreserved(texture, copied);
            if (target is ExportEntry selected &&
                (IsRegisteredExplicitTexture(source.FileRef, texture.InstancedFullPath) ||
                 HasPersistedExplicitTextureLeaf(source.FileRef, texture.InstancedFullPath)))
                RegisterExplicitTextureLeaf(destination, selected.InstancedFullPath);
        }
        VerifyNoUnreachableEntries(root, preexistingEntries);
        return root;
    }

    // Verification only: LEC owns copying/relinking. Follow imported ancestry as
    // well as exported parents, without treating every package sibling as owned.
    internal static HashSet<IEntry> DependencyEntries(ExportEntry root)
    {
        var entries = new HashSet<IEntry>();
        var pending = new Queue<IEntry>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var entry))
        {
            if (!entries.Add(entry)) continue;
            if (entry.Parent is { } parent) pending.Enqueue(parent);
            if (entry is ExportEntry export)
                foreach (var index in PackageIntegrity.References(export).Values)
                    if (root.FileRef.GetEntry(index) is { } reference) pending.Enqueue(reference);
        }
        return entries;
    }

    internal static void VerifyNoUnreachableEntries(ExportEntry root, IReadOnlySet<IEntry>? preexistingEntries = null)
    {
        var reachable = DependencyEntries(root);
        var orphan = root.FileRef.Exports.Cast<IEntry>().Concat(root.FileRef.Imports)
            .FirstOrDefault(entry => preexistingEntries?.Contains(entry) != true && !reachable.Contains(entry));
        if (orphan is not null)
            throw new InvalidDataException($"Completed PCC contains an unused {orphan.ClassName} '{orphan.InstancedFullPath}'.");
    }

    /// <summary>
    /// Ports an ordinary PCC root using Legendary Explorer's mature parent and
    /// linker policy. Callers retain their source hierarchy; the corpus oracle
    /// is not used as a generic path builder.
    /// </summary>
    internal static ExportEntry ImportRootPreservingStructure(
        IMEPackage destination,
        ExportEntry source,
        ICollection<string>? warnings = null,
        bool checkImportsWhenExportingToPackage = true,
        IReadOnlyList<ExportEntry>? explicitTextureDonors = null,
        InstalledGameAssetResolver? installedAssets = null,
        ExportEntry? replacementRoot = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ExportEntry? sourceOverride = null;
        ExportEntry? replacementOverride = null;
        if (replacementRoot is not null)
        {
            if (source.ClassName != "BioMorphFace" || replacementRoot.ClassName != "BioMorphFace" ||
                !ReferenceEquals(replacementRoot.FileRef, destination))
                throw new InvalidDataException("Only a destination BioMorphFace and its BioMaterialOverride can be replaced.");
            sourceOverride = MorphOverride(source);
            replacementOverride = MorphOverride(replacementRoot);
        }
        using var preparationCache = new PackageCache();
        using var preparedMesh = CanonicalAttachmentMaterialPolicy.PrepareMesh(source, preparationCache, installedAssets);
        source = preparedMesh?.Mesh ?? source;
        var preexistingExports = destination.Exports.ToHashSet();
        var relinker = new RelinkerOptionsPackage
        {
            ImportExportDependencies = true,
            GenerateImportsForGlobalFiles = false,
            CheckImportsWhenExportingToPackage = checkImportsWhenExportingToPackage
        };
        var plannedMappings = new Dictionary<IEntry, IEntry>();
        PrestageExplicitTextureDonors(destination, source, explicitTextureDonors ?? [], plannedMappings);
        PreseedOfficialSupportedReferences(destination, source, plannedMappings, installedAssets);
        relinker.CustomRelinkUIndex = (
            IMEPackage importingPackage,
            ExportEntry relinkingExport,
            ref int uIndex,
            string propertyName,
            string prefix,
            RelinkerOptionsPackage options,
            out EntryStringPair result) =>
        {
            if (importingPackage.GetEntry(uIndex) is { } original &&
                plannedMappings.TryGetValue(original, out var selected))
            {
                uIndex = selected.UIndex;
                result = null!;
                return true;
            }
            result = null!;
            return false;
        };
        ExternalSkeletalMeshMaterializer.PrepareReferencedPackagePaths(destination, source, plannedMappings);
        IEntry staged;
        IReadOnlyList<EntryStringPair> issues;
        if (replacementRoot is null)
        {
            issues = EntryExporter.ExportExportToPackage(
                source, destination, out staged, customROP: relinker);
        }
        else
        {
            // LEX's ReplaceSingularWithRelink uses these same LEC data-copy and
            // relink operations. Bind both members before relinking so the face
            // retains its destination BMO, including cycles between the pair.
            relinker.CrossPackageMap[source] = replacementRoot;
            relinker.CrossPackageMap[sourceOverride!] = replacementOverride!;
            foreach (var (incoming, target) in new[]
                     { (source, replacementRoot), (sourceOverride!, replacementOverride!) })
            {
                if (!EntryImporter.ReplaceExportDataWithAnother(incoming, target, relinker))
                    throw new InvalidDataException($"LEC could not replace {target.ClassName} '{target.InstancedFullPath}'.");
            }
            // Existing dependencies contain destination indices already. Only
            // the replaced pair and newly imported exports need a donor relink.
            var preservedExports = preexistingExports.ToHashSet();
            preservedExports.Remove(replacementRoot);
            preservedExports.Remove(replacementOverride!);
            MaterialisationVerifier.Relink(relinker, preservedExports);
            issues = relinker.RelinkReport;
            staged = replacementRoot;
        }
        if (warnings is not null)
        {
            foreach (var issue in issues)
            {
                warnings.Add(issue.Message);
            }
        }
        var root = staged as ExportEntry ?? throw new InvalidDataException(
            $"LEC did not stage {source.ClassName} '{source.InstancedFullPath}' as an export.");
        NormalizeNewSupportedAssets(destination, preexistingExports);
        VerifyExplicitTextureLeaves(destination, GetExplicitTextureLeaves(destination));
        return root;
    }

    private static ExportEntry MorphOverride(ExportEntry face) =>
        face.GetProperty<ObjectProperty>("m_oMaterialOverrides")?.ResolveToEntry(face.FileRef)
            is ExportEntry { ClassName: "BioMaterialOverride" } materialOverride
            ? materialOverride
            : throw new InvalidDataException($"BioMorphFace '{face.InstancedFullPath}' has no local BioMaterialOverride.");

    private static void PrestageExplicitTextureDonors(
        IMEPackage destination,
        ExportEntry source,
        IReadOnlyList<ExportEntry> donors,
        IDictionary<IEntry, IEntry> plannedMappings)
    {
        if (donors.Count == 0) return;
        var uniqueDonors = donors.GroupBy(PccAssetPathPolicy.FromExport, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var distinct = group.GroupBy(donor =>
                        $"{Path.GetFullPath(donor.FileRef.FilePath)}|{donor.UIndex}",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(entries => entries.First()).ToArray();
                if (distinct.Length > 1)
                    throw new InvalidDataException(
                        $"Multiple explicit Texture2D donors have the same canonical identity '{group.Key}'.");
                return distinct[0];
            }).ToArray();
        var sourceTextures = ReachableReferences(source)
            .Where(entry => entry.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();
        foreach (var donor in uniqueDonors)
        {
            if (!donor.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) || donor.Game != destination.Game)
                throw new InvalidDataException("An explicit texture donor must be a same-game Texture2D export.");
            var path = PccAssetPathPolicy.FromExport(donor);
            var matchingSources = sourceTextures.Where(entry =>
                    entry.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    CanonicalPath(entry).Equals(path, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingSources.Length > 1)
                throw new InvalidDataException($"Explicit Texture2D donor '{path}' matches multiple authored reference identities.");
            if (!path.Contains('.'))
                throw new InvalidDataException($"Explicit Texture2D donor '{path}' has no package parent path.");
            var existing = PackageIntegrity.FindExactEntry(destination, path, "Texture2D");
            if (existing is ImportEntry)
                throw new InvalidDataException($"Cannot stage explicitly selected Texture2D '{path}': an import occupies the required export identity.");
            if (existing is ExportEntry existingExport)
            {
                if (ReferenceEquals(existingExport, donor))
                {
                    RegisterExplicitTextureLeaf(destination, path);
                    if (matchingSources.Length == 1) plannedMappings[matchingSources[0]] = existingExport;
                    continue;
                }
                throw new InvalidDataException(
                    $"Cannot stage explicitly selected Texture2D '{path}': a different export already occupies the required identity.");
            }

            RegisterExplicitTextureLeaf(destination, path);
            var embedded = ImportDependencyGraph(
                destination,
                donor,
                destinationParent: null,
                textureCatalog: null,
                preferBiogTextures: false,
                applyCorpusMaterialPolicy: true,
                targetPathOverride: path);
            if (!embedded.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Explicit Texture2D donor '{path}' was staged as '{embedded.InstancedFullPath}'.");
            if (matchingSources.Length == 1) plannedMappings[matchingSources[0]] = embedded;
        }
    }

    internal static IReadOnlyList<ExportEntry> ResolveExplicitTextureDonors(
        IEnumerable<AssetIdentity?> identities, MEGame game, PackageCache cache)
    {
        var selected = identities.Where(identity => identity is { IsImport: false, IsExplicitTextureDonor: true }).ToArray();
        if (selected.Length == 0) return [];
        var officialPaths = InstalledGameAssetResolver.GetOfficialPackageFiles(game).Values
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selected
            .Where(identity => !officialPaths.Contains(Path.GetFullPath(identity!.PackagePath)))
            .Select(identity => InstalledGameAssetResolver.RequireExact(cache, identity!.PackagePath,
                identity.InstancedPath, "Texture2D", identity.UIndex))
            .DistinctBy(export => $"{export.FileRef.FilePath}|{export.UIndex}", StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static MorphFaceMaterialOverrides OmitUnavailableSelectedTextures(
        MorphFaceMaterialOverrides overrides, PackageCache cache, ICollection<string> warnings)
    {
        var retained = OmitUnavailableSelectedTextures(overrides.Textures, cache, warnings);
        return retained.Count == overrides.Textures.Count ? overrides : overrides with { Textures = retained };
    }

    internal static IReadOnlyList<TextureMaterialOverride> OmitUnavailableSelectedTextures(
        IReadOnlyList<TextureMaterialOverride> textures, PackageCache cache, ICollection<string> warnings)
    {
        return textures.Where(value =>
        {
            var identity = value.TextureReference;
            if (identity is not { IsExplicitTextureDonor: true, IsImport: false } ||
                InstalledGameAssetResolver.TryExact(cache, identity.PackagePath, identity.InstancedPath,
                    "Texture2D", identity.UIndex) is not null) return true;
            warnings.Add($"Texture '{identity.InstancedPath}' for '{value.Name}' is unavailable in '{identity.PackagePath}'. " +
                "The selection was omitted from this export; the material default will be used.");
            return false;
        }).ToArray();
    }

    internal static void StageExplicitTextureDonors(
        IMEPackage destination,
        ExportEntry sourceRoot,
        IReadOnlyList<ExportEntry> donors)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(donors);
        PrestageExplicitTextureDonors(destination, sourceRoot, donors, new Dictionary<IEntry, IEntry>());
    }

    private static void PreseedOfficialSupportedReferences(
        IMEPackage destination,
        ExportEntry source,
        IDictionary<IEntry, IEntry> plannedMappings,
        InstalledGameAssetResolver? installedAssets)
    {
        using var cache = new PackageCache { CacheMaxSize = 64 };
        var resolver = installedAssets ?? new InstalledGameAssetResolver();
        var pending = new Queue<ExportEntry>();
        var visited = new HashSet<int>();
        pending.Enqueue(source);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current.UIndex)) continue;
            foreach (var reference in PackageIntegrity.References(current).Values
                         .Where(index => index != 0).Distinct()
                         .Select(current.FileRef.GetEntry).Where(entry => entry is not null).Cast<IEntry>())
            {
                if (reference.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase)) continue;
                if (plannedMappings.ContainsKey(reference)) continue;
                var nativePath = CanonicalPath(reference);
                var authoredInstance = reference is ExportEntry instance && MaterialInstanceSourcePolicy.IsInstance(instance);
                var editedInstance = authoredInstance && MaterialInstanceSourcePolicy.OverridesOfficialInstance(
                    (ExportEntry)reference, resolver, cache);
                if (!editedInstance &&
                    PackageIntegrity.FindExactEntry(destination, nativePath, reference.ClassName) is { } existingReference)
                {
                    plannedMappings[reference] = existingReference;
                    continue;
                }
                if (authoredInstance &&
                    (MaterialDependencyOracle.Instance.GetKind(destination.Game, reference.ClassName, nativePath) !=
                         MaterialOracleEntryKind.Import ||
                     editedInstance))
                {
                    var renamed = ImportDependencyGraph(destination, (ExportEntry)reference, destinationParent: null,
                        textureCatalog: null, preferBiogTextures: false, applyCorpusMaterialPolicy: true,
                        targetPathOverride: nativePath, installedAssets: resolver);
                    plannedMappings[reference] = renamed;
                    continue;
                }
                if (InstalledAssetOccurrence.IsSupportedClass(reference.ClassName))
                {
                    if (MaterialDependencyOracle.Instance.GetKind(destination.Game, reference.ClassName, nativePath) ==
                        MaterialOracleEntryKind.Import)
                    {
                        plannedMappings[reference] = EnsureImport(destination, nativePath, reference.ClassName);
                        continue;
                    }
                    if (reference is ImportEntry &&
                        MaterialDependencyOracle.Instance.GetKind(destination.Game, reference.ClassName, nativePath) ==
                            MaterialOracleEntryKind.Unknown)
                    {
                        // Installed resolution supplies content for an identity;
                        // it does not change the source package's import role.
                        // Preserve unknown-role imports instead of embedding the
                        // resolved official export as a side effect of preseed.
                        plannedMappings[reference] = EnsureImport(destination, nativePath, reference.ClassName);
                        continue;
                    }
                    var selected = resolver.Resolve(reference, cache);
                    if (selected is not null)
                    {
                        var path = PccAssetPathPolicy.FromExport(selected);
                        var existing = PackageIntegrity.FindExactEntry(destination, path, selected.ClassName);
                        if (existing is not null)
                        {
                            plannedMappings[reference] = existing;
                            continue;
                        }
                        var embedded = ImportDependencyGraph(
                            destination,
                            selected,
                            destinationParent: null,
                            textureCatalog: null,
                            preferBiogTextures: false,
                            applyCorpusMaterialPolicy: true,
                            targetPathOverride: path,
                            authoredMaterialDonors: AuthoredMeshMaterials(reference));
                        plannedMappings[reference] = embedded;
                        continue; // The staged official graph supplies its own dependencies.
                    }
                }
                if (reference is ExportEntry export) pending.Enqueue(export);
            }
        }
    }

    private static IEnumerable<IEntry> ReachableReferences(ExportEntry root)
    {
        var pending = new Queue<ExportEntry>();
        var visited = new HashSet<int>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current.UIndex)) continue;
            foreach (var index in PackageIntegrity.References(current).Values.Where(value => value != 0).Distinct())
            {
                if (current.FileRef.GetEntry(index) is not { } reference) continue;
                yield return reference;
                if (reference is ExportEntry export) pending.Enqueue(export);
            }
        }
    }

    private static IReadOnlyList<ExportEntry> AuthoredMeshMaterials(IEntry entry) =>
        entry is ExportEntry mesh && mesh.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)
            ? mesh.GetBinaryData<LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh>().Materials
                .Select(mesh.FileRef.GetEntry).OfType<ExportEntry>()
                .Where(MaterialInstanceSourcePolicy.IsInstance).Distinct().ToArray()
            : [];

    private static void RegisterExplicitTextureLeaf(IMEPackage package, string path)
    {
        ExplicitTextureLeaves.GetValue(package, _ => new ExplicitTextureLeafState())
            .Paths.Add(ExplicitLeafKey("Texture2D", path));
        // A new PCC must retain the distinction between a deliberately embedded
        // stock-path leaf and an ordinary stock reference after it is reopened.
        // An unused name-table entry records only that intent; no game-facing
        // export, property, reference or texture payload is altered.
        var name = ExplicitTextureName(path);
        package.FindNameOrAdd(name);
        if (PersistedTextureLeafNames.TryGetValue(package, out var names)) names.Add(name);
    }

    internal static bool HasPersistedExplicitTextureLeaf(IMEPackage package, string path) =>
        PersistedTextureLeafNames.GetValue(package, static value => value.Names
                .Where(name => name.StartsWith(ExplicitTextureNamePrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Contains(ExplicitTextureName(path));

    private static string ExplicitTextureName(string path) =>
        ExplicitTextureNamePrefix + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

    private static IReadOnlySet<string> GetExplicitTextureLeaves(IMEPackage package) =>
        ExplicitTextureLeaves.TryGetValue(package, out var state)
            ? state.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static bool IsRegisteredExplicitTexture(IMEPackage package, string path) =>
        GetExplicitTextureLeaves(package).Contains(ExplicitLeafKey("Texture2D", path));

    private static string ExplicitLeafKey(string className, string path) => $"{className}|{path}";

    private static void VerifyExplicitTextureLeaves(IMEPackage package, IReadOnlySet<string> paths)
    {
        foreach (var key in paths)
        {
            var split = key.IndexOf('|');
            var className = key[..split];
            var path = key[(split + 1)..];
            var matches = package.Exports.Cast<IEntry>().Concat(package.Imports)
                .Where(entry => entry.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) &&
                                entry.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0] is not ExportEntry)
                throw new InvalidDataException($"Explicitly selected {className} '{path}' must be exactly one export.");
        }
    }

    private sealed class ExplicitTextureLeafState
    {
        internal HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
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
        foreach (var export in relinker.CrossPackageMap.Values.OfType<ExportEntry>())
        {
            PccAssetPathPolicy.NormalizeMaterializedAsset(export);
        }
        return root;
    }

    private static void NormalizeNewSupportedAssets(IMEPackage package, IReadOnlySet<ExportEntry> preexistingExports)
    {
        foreach (var export in package.Exports.Where(export => !preexistingExports.Contains(export)))
        {
            PccAssetPathPolicy.NormalizeMaterializedAsset(export);
        }
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
        if (PackageIntegrity.FindExactEntry(destination, path, "Texture2D") is { } existing)
            return existing;
        var oracleKind = MaterialDependencyOracle.Instance.GetKind(destination.Game, "Texture2D", path);
        if (oracleKind == MaterialOracleEntryKind.Import)
        {
            return EnsureImport(destination, path, "Texture2D");
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
        VerifyExplicitTextureLeaves(reopened, expected.ExplicitTextureLeaves.ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (verifyCorpusRoles)
        {
            VerifyCorpusRoles(reopened,
                new HashSet<CorpusRoleIssue>(CorpusRoleIssueComparer.Instance),
                expected.ExplicitTextureLeaves.ToHashSet(StringComparer.OrdinalIgnoreCase));
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
        private readonly IReadOnlySet<string> _requiredExportIdentities;
        private readonly IReadOnlySet<CorpusRoleIssue> _baselineCorpusRoleIssues;
        private readonly IReadOnlySet<ExportEntry> _preexistingExports;
        private readonly PackageCache _cache = new() { CacheMaxSize = 64 };
        private readonly GamePackageReferenceResolver _resolver;
        private readonly InstalledGameAssetResolver _officialAssets;
        private readonly RelinkerOptionsPackage _relinker;
        private readonly Dictionary<string, IEntry> _destinations = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(ExportEntry Source, ExportEntry Destination)> _textures = [];
        private readonly Dictionary<ExportEntry, (ExportEntry Source, string TargetPath)> _stagedCopies = [];
        private readonly List<CanonicalAttachmentMaterialPolicy.PreparedMesh> _preparedMeshes = [];
        private readonly Dictionary<string, ExportEntry> _authoredMaterials = new(StringComparer.OrdinalIgnoreCase);

        internal GraphImportSession(
            IMEPackage destination,
            IReadOnlyList<TextureCatalogCandidate> catalog,
            bool preferBiogTextures,
            ICollection<string>? warnings,
            bool applyCorpusMaterialPolicy,
            IReadOnlyList<IEntry> requiredExportReferences,
            InstalledGameAssetResolver? installedAssets,
            IReadOnlyList<ExportEntry> authoredMaterialDonors)
        {
            _destination = destination;
            _catalog = catalog;
            _preferBiogTextures = preferBiogTextures;
            _warnings = warnings;
            _applyCorpusMaterialPolicy = applyCorpusMaterialPolicy;
            _requiredExportIdentities = requiredExportReferences
                .Select(entry => $"{entry.ClassName}|{CanonicalPath(entry)}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _baselineCorpusRoleIssues = applyCorpusMaterialPolicy
                ? CaptureCorpusRoleIssues(destination)
                : new HashSet<CorpusRoleIssue>(CorpusRoleIssueComparer.Instance);
            _preexistingExports = destination.Exports.ToHashSet();
            _officialAssets = installedAssets ?? new InstalledGameAssetResolver();
            foreach (var material in authoredMaterialDonors)
                _authoredMaterials.TryAdd(CanonicalPath(material), material);
            _resolver = new GamePackageReferenceResolver(_cache, _officialAssets);
            _relinker = new RelinkerOptionsPackage(_cache)
            {
                ImportExportDependencies = false,
                GenerateImportsForGlobalFiles = false,
                PortImportsMemorySafe = false
            };
        }

        internal ExportEntry ImportRoot(ExportEntry source, IEntry? destinationParent, string? targetPathOverride)
        {
            RememberAuthoredMeshMaterials(source);
            // The caller-selected parent is the destination counterpart of the
            // source root's parent. Seed that relationship before planning any
            // sibling dependencies (for example BioMaterialOverride children of
            // a BioMorphFace container), otherwise their native parent can be
            // re-created at the unqualified source path.
            if (source.Parent is { } sourceParent && destinationParent is not null)
            {
                _relinker.CrossPackageMap[sourceParent] = destinationParent;
            }
            var targetPath = targetPathOverride ?? (destinationParent is null
                ? source.ObjectName.Instanced
                : $"{destinationParent.InstancedFullPath}.{source.ObjectName.Instanced}");
            if (targetPath.Equals(CanonicalPath(source), StringComparison.OrdinalIgnoreCase) &&
                MaterialInstanceSourcePolicy.OverridesOfficialInstance(source, _officialAssets, _cache))
            {
                targetPath = MaterialInstanceSourcePolicy.UniqueInstancePath(_destination, targetPath);
                _warnings?.Add($"Authored material instance '{CanonicalPath(source)}' was preserved as '{targetPath}' to keep its overrides separate from the stock instance.");
            }
            var root = MaterializeExport(source, targetPath, destinationParent, forceExport: true) as ExportEntry
                       ?? throw new InvalidDataException(
                           $"The authored {source.ClassName} '{source.InstancedFullPath}' was not staged as an export.");
            MaterialisationVerifier.Relink(_relinker, _preexistingExports, stagedCopies: _stagedCopies);
            MaterialisationVerifier.Verify(root, source.Game, _relinker, _warnings, stagedCopies: _stagedCopies);
            foreach (var (textureSource, textureDestination) in _textures)
            {
                PccTextureDependencyResolver.VerifyBulkDataPreserved(textureSource, textureDestination);
            }
            if (_applyCorpusMaterialPolicy)
            {
                VerifyCorpusRoles(_destination, _baselineCorpusRoleIssues, GetExplicitTextureLeaves(_destination));
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
            // Geometry may come from a different official cooked occurrence,
            // but its MICs must not replace the authored mesh's instance data.
            // Official duplicates can share a path while using a Player parent.
            if (MaterialInstanceSourcePolicy.IsInstance(reference) &&
                _authoredMaterials.TryGetValue(canonicalPath, out var authoredMaterial) &&
                !ReferenceEquals(reference, authoredMaterial))
            {
                var authored = PlanReference(authoredMaterial);
                _relinker.CrossPackageMap[reference] = authored;
                return authored;
            }
            RememberAuthoredMeshMaterials(reference);
            // Callers can require an authored dependency (such as a converted
            // face's base mesh) to be embedded even when its donor uses an import
            // and the stock corpus has no role entry for that exact identity.
            if (_requiredExportIdentities.Contains($"{reference.ClassName}|{canonicalPath}"))
            {
                var requiredSource = _resolver.Require(reference, "required authored export dependency");
                var requiredExport = MaterializeExport(
                    requiredSource, canonicalPath, destinationParent: null, forceExport: true);
                _relinker.CrossPackageMap[reference] = requiredExport;
                return requiredExport;
            }
            if (reference.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            {
                return PlanTexture(reference, canonicalPath);
            }
            if (reference is ExportEntry instance && MaterialInstanceSourcePolicy.IsInstance(instance) &&
                MaterialInstanceSourcePolicy.OverridesOfficialInstance(instance, _officialAssets, _cache))
            {
                var uniquePath = MaterialInstanceSourcePolicy.UniqueInstancePath(_destination, canonicalPath);
                _warnings?.Add($"Authored material instance '{canonicalPath}' was preserved as '{uniquePath}' to keep its overrides separate from the stock instance.");
                var authored = MaterializeExport(instance, uniquePath, destinationParent: null, forceExport: true);
                _relinker.CrossPackageMap[reference] = authored;
                return authored;
            }

            var expected = ExpectedKind(reference.ClassName, canonicalPath);
            var targetPath = expected == MaterialOracleEntryKind.Unknown &&
                             reference is ExportEntry &&
                             reference.Parent is { } sourceParent &&
                             _relinker.CrossPackageMap.TryGetValue(sourceParent, out var destinationParent)
                ? $"{destinationParent.InstancedFullPath}.{reference.ObjectName.Instanced}"
                : canonicalPath;
            if (PackageIntegrity.FindExactEntry(_destination, targetPath, reference.ClassName) is { } existing)
            {
                _relinker.CrossPackageMap[reference] = existing;
                return existing;
            }
            if (expected == MaterialOracleEntryKind.Import ||
                reference is ImportEntry && expected == MaterialOracleEntryKind.Unknown)
            {
                var imported = EnsureImport(_destination, targetPath, reference.ClassName);
                _relinker.CrossPackageMap[reference] = imported;
                return imported;
            }

            var source = reference is ExportEntry directExport &&
                         !InstalledAssetOccurrence.IsSupportedClass(reference.ClassName)
                ? directExport
                : _resolver.Require(reference, $"corpus export dependency of '{canonicalPath}'");
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
                try
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
                catch (KeyNotFoundException)
                {
                    // A registry occurrence from a mod does not authorize PCC
                    // materialisation when no official donor exists. Preserve
                    // the corpus's native import role below instead.
                }
            }
            var targetPath = selection?.InstancedPath ?? canonicalPath;
            if (PackageIntegrity.FindExactEntry(_destination, targetPath, "Texture2D") is { } existing)
            {
                _relinker.CrossPackageMap[reference] = existing;
                return existing;
            }
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
            if (CanonicalAttachmentMaterialPolicy.PrepareMesh(source, _cache, _officialAssets) is { } prepared)
            {
                _preparedMeshes.Add(prepared);
                var result = MaterializeExport(prepared.Mesh, targetPath, destinationParent, forceExport);
                _relinker.CrossPackageMap[source] = result;
                return result;
            }
            // Resolving an import supplies donor content; it does not require
            // replacing the destination's native role. Corpus roles choose how
            // to construct missing dependencies. Explicit roots still require
            // an export and retain their collision check below.
            if (!forceExport && PackageIntegrity.FindExactEntry(_destination, targetPath, source.ClassName) is { } existingReference)
            {
                _relinker.CrossPackageMap[source] = existingReference;
                return existingReference;
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
                    throw new InvalidDataException(
                        $"Cannot materialise {source.ClassName} '{targetPath}': an import occupies the required export identity.");
                }
                _destinations[identityKey] = existing;
                _relinker.CrossPackageMap[source] = existing;
                return existing;
            }

            var parent = destinationParent ?? ResolveDestinationParent(source, targetPath);
            // Resolving a non-Package ancestor can stage part of this export's
            // graph (for example a TextureCube parent imports its Texture2D face
            // exports). Reuse that recursive result instead of importing the leaf
            // a second time and creating a duplicate exact path.
            if (_relinker.CrossPackageMap.TryGetValue(source, out mapped))
            {
                return mapped;
            }
            if (_destinations.TryGetValue(identityKey, out planned))
            {
                _relinker.CrossPackageMap[source] = planned;
                return planned;
            }
            if (PackageIntegrity.FindExactEntry(_destination, targetPath, source.ClassName) is ExportEntry stagedByAncestor)
            {
                _destinations[identityKey] = stagedByAncestor;
                _relinker.CrossPackageMap[source] = stagedByAncestor;
                return stagedByAncestor;
            }

            var importedEntry = EntryImporter.ImportExport(
                _destination,
                source,
                parent?.UIndex ?? 0,
                _relinker);
            if (importedEntry is ExportEntry instance && MaterialInstanceSourcePolicy.IsInstance(source) &&
                !source.ObjectName.Instanced.Equals(targetPath[(targetPath.LastIndexOf('.') + 1)..],
                    StringComparison.OrdinalIgnoreCase))
                instance.ObjectName = NameReference.FromInstancedString(targetPath[(targetPath.LastIndexOf('.') + 1)..]);
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
            _stagedCopies[export] = (source, targetPath);
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

        private IEntry? ResolveDestinationParent(IEntry source, string targetPath)
        {
            if (source.Parent is { } sourceParent &&
                !sourceParent.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase))
            {
                var parentPath = targetPath.Contains('.')
                    ? targetPath[..targetPath.LastIndexOf('.')]
                    : DependencyPath(sourceParent);
                // An outer is a reference too. A Texture2D face can live under
                // an imported TextureCube; it does not require a local cube.
                if (PackageIntegrity.FindExactEntry(_destination, parentPath, sourceParent.ClassName) is { } existing)
                {
                    _relinker.CrossPackageMap[sourceParent] = existing;
                    return existing;
                }
                if (sourceParent is ExportEntry exportedParent)
                    return MaterializeExport(exportedParent, parentPath, destinationParent: null, forceExport: true);
                var parent = _destination.CreateImport(sourceParent.ClassName,
                    NameReference.FromInstancedString(parentPath[(parentPath.LastIndexOf('.') + 1)..]),
                    ResolveDestinationParent(sourceParent, parentPath));
                _relinker.CrossPackageMap[sourceParent] = parent;
                return parent;
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

        private void RememberAuthoredMeshMaterials(IEntry entry)
        {
            foreach (var material in AuthoredMeshMaterials(entry))
                _authoredMaterials.TryAdd(CanonicalPath(material), material);
        }

        public void Dispose()
        {
            foreach (var prepared in _preparedMeshes) prepared.Dispose();
            _cache.Dispose();
        }
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
        => VerifyCorpusRoles(package,
            new HashSet<CorpusRoleIssue>(CorpusRoleIssueComparer.Instance),
            GetExplicitTextureLeaves(package));

    private static HashSet<CorpusRoleIssue> CaptureCorpusRoleIssues(IMEPackage package)
    {
        var issues = new HashSet<CorpusRoleIssue>(CorpusRoleIssueComparer.Instance);
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
                issues.Add(new CorpusRoleIssue(entry.ClassName, CanonicalPath(entry), expected, actual));
            }
        }
        return issues;
    }

    private static void VerifyCorpusRoles(
        IMEPackage package,
        IReadOnlySet<CorpusRoleIssue> baselineIssues,
        IReadOnlySet<string>? allowedExplicitTextureLeaves = null)
    {
        foreach (var issue in CaptureCorpusRoleIssues(package).Except(baselineIssues, CorpusRoleIssueComparer.Instance))
        {
            if (issue.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                issue.Expected == MaterialOracleEntryKind.Import && issue.Actual == MaterialOracleEntryKind.Export &&
                allowedExplicitTextureLeaves?.Contains(ExplicitLeafKey(issue.ClassName, issue.Path)) == true)
                continue;
            throw new InvalidDataException(
                $"Native corpus role mismatch for {issue.ClassName} '{issue.Path}': " +
                $"expected {issue.Expected}, saved {issue.Actual}.");
        }
    }

    private readonly record struct CorpusRoleIssue(
        string ClassName,
        string Path,
        MaterialOracleEntryKind Expected,
        MaterialOracleEntryKind Actual);

    private sealed class CorpusRoleIssueComparer : IEqualityComparer<CorpusRoleIssue>
    {
        internal static CorpusRoleIssueComparer Instance { get; } = new();

        public bool Equals(CorpusRoleIssue left, CorpusRoleIssue right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.ClassName, right.ClassName) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path) &&
            left.Expected == right.Expected && left.Actual == right.Actual;

        public int GetHashCode(CorpusRoleIssue issue) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(issue.ClassName),
            StringComparer.OrdinalIgnoreCase.GetHashCode(issue.Path),
            issue.Expected,
            issue.Actual);
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
        // LE1 Human chains staged by the source face. Missing ancestors retain
        // this import path's construction policy; either role can also parent
        // embedded exports in cooked MELE packages.
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
            // The localized LE2 eye override resolves through Startup. Its
            // package root must also be imported for native import resolution.
            var localizedEyeRoot = destination.Game == MEGame.LE2 &&
                ancestorPath.Equals("BIOG_Humanoid_EYE_OVRD", StringComparison.OrdinalIgnoreCase);
            if (parent is not ImportEntry && !localizedEyeRoot)
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

    internal static string CanonicalPath(IEntry entry)
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
    IReadOnlyList<PccDependencyNodeSnapshot> Nodes,
    IReadOnlyList<string> ExplicitTextureLeaves)
{
    internal static PccDependencyGraphSnapshot Capture(ExportEntry root, IReadOnlySet<string>? explicitTextureLeaves = null)
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

        return new PccDependencyGraphSnapshot(root.ClassName, root.InstancedFullPath, nodes,
            explicitTextureLeaves?.ToArray() ?? []);
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
