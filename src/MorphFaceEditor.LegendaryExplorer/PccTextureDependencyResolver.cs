using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Resolves and ports a texture for a newly-authored PCC. This is deliberately
/// separate from the standalone/RON texture materialiser: a PCC can reference
/// the installed game's TFC, so the donor texture's bulk-data storage must be
/// copied unchanged rather than decoded and replaced with package data.
/// </summary>
internal static class PccTextureDependencyResolver
{
    /// <summary>
    /// Resolves an exact official texture identity by mount priority.
    /// Object-name matching is forbidden because player lashes and cube faces
    /// deliberately reuse names at multiple logical paths.
    /// </summary>
    internal static PccTextureSource Resolve(
        AssetIdentity identity,
        IReadOnlyList<TextureCatalogCandidate> catalog,
        bool preferBiog)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.InstancedPath);

        var requestedPath = identity.InstancedPath;
        var all = catalog.Where(candidate => candidate.IsEligibleOfficialDefault &&
                candidate.EffectiveOccurrence.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
            .Select(candidate => (Candidate: candidate, Occurrence: candidate.EffectiveOccurrence,
                Path: PccAssetPathPolicy.FromDonorOccurrence(candidate.InstancedPath, candidate.EffectiveOccurrence.PackagePath)))
            .ToArray();
        var matches = all.Where(value => value.Path.Equals(requestedPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0 && !string.IsNullOrWhiteSpace(identity.PackagePath))
        {
            var qualified = PccAssetPathPolicy.FromDonorOccurrence(requestedPath, identity.PackagePath);
            matches = all.Where(value => value.Path.Equals(qualified, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (matches.Length == 0)
        {
            matches = all.Where(value => value.Candidate.InstancedPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            // A package-local name only identifies an asset inside its donor.
            // Different package-qualified identities must never be merged.
            var donorPath = PccAssetPathPolicy.FromDonorOccurrence(requestedPath, identity.PackagePath);
            if (matches.Any(value => value.Path.Equals(donorPath, StringComparison.OrdinalIgnoreCase)))
                matches = matches.Where(value => value.Path.Equals(donorPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            else if (matches.Select(value => value.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidDataException($"Texture2D '{requestedPath}' maps to multiple exact package-qualified catalogue identities.");
        }
        if (matches.Length == 0)
        {
            throw new KeyNotFoundException(
                $"Texture2D '{identity.InstancedPath}' is not present in the installed texture database.");
        }

        matches = matches.Where(value => value.Occurrence.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc).ToArray();
        if (matches.Length == 0)
        {
            throw new KeyNotFoundException(
                $"Texture2D '{identity.InstancedPath}' has no exact official installed donor.");
        }

        var selected = matches.OrderByDescending(value => value.Occurrence.MountPriority)
            .ThenByDescending(value => IsSeekfreePackage(value.Occurrence.PackagePath))
            .ThenBy(value => value.Occurrence.PackagePath, StringComparer.OrdinalIgnoreCase).First();
        return new PccTextureSource(
            requestedPath,
            selected.Path,
            selected.Occurrence,
            IsExplicitDonor: false);
    }

    /// <summary>
    /// Imports a donor Texture2D while preserving its original binary bulk-data
    /// metadata (including external mip storage, TFC name, offsets and sizes).
    /// No image decode, mip replacement, or TFC creation occurs here.
    /// </summary>
    internal static ExportEntry Materialize(
        IMEPackage destination,
        AssetIdentity identity,
        IReadOnlyList<TextureCatalogCandidate> catalog,
        bool preferBiog,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var source = Resolve(identity, catalog, preferBiog);
        return MaterializeResolved(destination, source, warnings);
    }

    /// <summary>
    /// Ports an already-resolved exact donor when the compact texture database
    /// does not catalogue that inherited material dependency.
    /// </summary>
    internal static ExportEntry MaterializeDirect(
        IMEPackage destination,
        AssetIdentity identity,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(identity);
        using var cache = new PackageCache();
        var donor = new InstalledGameAssetResolver().Resolve(destination.Game, identity, cache)
            ?? throw new InvalidDataException($"Texture2D '{identity.InstancedPath}' has no exact official or authored donor.");
        // Keep this resolved donor alive through its owning cache. Resolving it
        // again in MaterializeResolved opens the same PCC a second time.
        return ImportDonor(destination, donor, identity.InstancedPath, warnings);
    }

    internal static ExportEntry MaterializeResolved(
        IMEPackage destination,
        PccTextureSource source,
        ICollection<string>? warnings)
    {
        using var cache = new PackageCache();
        LegendaryExplorerCoreRuntime.Initialize();
        var resolver = new InstalledGameAssetResolver();
        ExportEntry? donorTexture = source.IsExplicitDonor
            ? resolver.Resolve(destination.Game,
                new AssetIdentity(source.Occurrence.PackagePath, source.InstancedPath, source.Occurrence.ExportUIndex, "Texture2D")
                { IsExplicitTextureDonor = true }, cache)
            : resolver.Resolve(destination.Game, source.InstancedPath, "Texture2D", cache);
        if (donorTexture is null && source.IsExplicitDonor)
        {
            donorTexture = ResolveSourceExport(cache.GetCachedPackage(source.Occurrence.PackagePath)
                ?? throw new FileNotFoundException("The selected texture donor could not be opened.", source.Occurrence.PackagePath),
                source.InstancedPath, source.Occurrence.ExportUIndex);
        }
        if (donorTexture is null)
            throw new InvalidDataException(
                $"Texture2D '{source.InstancedPath}' has no exact official installed donor.");
        return ImportDonor(destination, donorTexture, source.InstancedPath, warnings);
    }

    private static ExportEntry ImportDonor(
        IMEPackage destination, ExportEntry donorTexture, string requestedPath,
        ICollection<string>? warnings)
    {
        if (!donorTexture.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Texture2D '{requestedPath}' has no exact texture donor.");
        if (donorTexture.Game != destination.Game)
            throw new InvalidDataException($"Texture '{requestedPath}' is from {donorTexture.Game}, but the destination is {destination.Game}.");
        var path = PccAssetPathPolicy.FromExport(donorTexture);
        var textureExport = PccPackageWorkflow.ImportDependencyGraph(
            destination,
            donorTexture,
            destinationParent: null,
            textureCatalog: null,
            preferBiogTextures: false,
            warnings: warnings,
            applyCorpusMaterialPolicy: false,
            targetPathOverride: path);
        if (!textureExport.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The materialised Texture2D path changed from '{path}' " +
                $"to '{textureExport.InstancedFullPath}'.");
        }

        VerifyBulkDataPreserved(donorTexture, textureExport);
        return textureExport;
    }

    internal static bool IsCharacterCreatorPackage(string packagePath)
    {
        var packageName = Path.GetFileNameWithoutExtension(packagePath);
        return packageName.StartsWith("BioP_Char", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("EntryMenu", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSeekfreePackage(string packagePath)
    {
        var packageName = Path.GetFileNameWithoutExtension(packagePath);
        return packageName.StartsWith("BIOG", StringComparison.OrdinalIgnoreCase) &&
               !IsCharacterCreatorPackage(packagePath);
    }

    internal static ExportEntry ResolveSourceExport(
        IMEPackage source,
        string instancedPath,
        int sourceUIndex)
    {
        bool Matches(ExportEntry export) =>
            export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
            (export.InstancedFullPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase) ||
             $"{Path.GetFileNameWithoutExtension(source.FilePath)}.{export.InstancedFullPath}"
                 .Equals(instancedPath, StringComparison.OrdinalIgnoreCase));

        if (sourceUIndex > 0 && source.IsUExport(sourceUIndex) &&
            source.GetUExport(sourceUIndex) is { } indexed && Matches(indexed))
        {
            return indexed;
        }
        var matches = source.Exports.Where(Matches).Take(2).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Texture2D '{instancedPath}' was {(matches.Length == 0 ? "not found" : "ambiguous")} in '{source.FilePath}'.");
    }

    internal static void VerifyBulkDataPreserved(ExportEntry source, ExportEntry destination)
    {
        var sourceTexture = new Texture2D(source);
        var destinationTexture = new Texture2D(destination);
        var sourceCacheName = source.GetProperty<NameProperty>("TextureFileCacheName")?.Value.Instanced;
        var destinationCacheName = destination.GetProperty<NameProperty>("TextureFileCacheName")?.Value.Instanced;
        if (sourceCacheName is not null && !string.Equals(
                sourceCacheName,
                destinationCacheName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Texture2D '{source.InstancedFullPath}' changed TextureFileCacheName " +
                $"'{sourceCacheName ?? "None"}' -> '{destinationCacheName ?? "None"}' during PCC import.");
        }

        var sourceMips = sourceTexture.Mips.Where(mip => mip.storageType != StorageTypes.empty).ToArray();
        var destinationMips = destinationTexture.Mips.Where(mip => mip.storageType != StorageTypes.empty).ToArray();
        if (sourceMips.Length != destinationMips.Length)
        {
            throw new InvalidDataException(
                $"Texture2D '{source.InstancedFullPath}' changed mip count during PCC import " +
                $"({sourceMips.Length} -> {destinationMips.Length}).");
        }

        for (var index = 0; index < sourceMips.Length; index++)
        {
            var expected = sourceMips[index];
            var actual = destinationMips[index];
            var expectedExternal = ((int)expected.storageType & (int)StorageFlags.externalFile) != 0;
            var actualExternal = ((int)actual.storageType & (int)StorageFlags.externalFile) != 0;
            var metadataChanged = expectedExternal != actualExternal ||
                expected.storageType != actual.storageType ||
                expected.uncompressedSize != actual.uncompressedSize ||
                expected.compressedSize != actual.compressedSize ||
                expectedExternal && (expected.externalOffset != actual.externalOffset ||
                    !string.Equals(expected.TextureCacheName, actual.TextureCacheName,
                        StringComparison.OrdinalIgnoreCase));
            if (metadataChanged)
            {
                throw new InvalidDataException(
                    $"Texture2D '{source.InstancedFullPath}' changed external mip storage metadata during PCC import " +
                    $"(mip {index}: storage {expected.storageType}->{actual.storageType}, " +
                    $"offset {expected.externalOffset}->{actual.externalOffset}, " +
                    $"cache '{expected.TextureCacheName}'->'{actual.TextureCacheName}').");
            }
        }
    }

}

internal sealed record PccTextureSource(
    string RequestedPath,
    string InstancedPath,
    TextureCatalogOccurrence Occurrence,
    bool IsExplicitDonor = false);
