using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
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
    /// Resolves an exact installed texture identity. Human/player textures use
    /// BIOG as their primary PCC donor and EntryMenu/BioP_Char only as fallback.
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
        var candidate = catalog.FirstOrDefault(value =>
            value.InstancedPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase));

        if (candidate is null)
        {
            throw new KeyNotFoundException(
                $"Texture2D '{identity.InstancedPath}' is not present in the installed texture database.");
        }

        var occurrences = candidate.Occurrences.Count == 0
            ? [candidate.EffectiveOccurrence]
            : candidate.Occurrences;
        var occurrence = SelectOccurrence(
            candidate.InstancedPath,
            identity.PackagePath,
            candidate.EffectiveOccurrence,
            occurrences,
            preferBiog);
        return new PccTextureSource(requestedPath, candidate.InstancedPath, occurrence);
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
        var source = new PccTextureSource(
            identity.InstancedPath,
            identity.InstancedPath,
            new TextureCatalogOccurrence(
                identity.PackagePath,
                identity.UIndex,
                0,
                TextureCatalogOrigin.BaseGame,
                0,
                0,
                string.Empty,
                string.Empty,
                false,
                null));
        return MaterializeResolved(destination, source, warnings);
    }

    private static ExportEntry MaterializeResolved(
        IMEPackage destination,
        PccTextureSource source,
        ICollection<string>? warnings)
    {

        if (destination.FindExport(source.InstancedPath, "Texture2D") is { } existing)
        {
            return existing;
        }
        if (PackageIntegrity.FindExactEntry(destination, source.InstancedPath, "Texture2D") is ImportEntry)
        {
            throw new InvalidDataException(
                $"Cannot materialise Texture2D '{source.InstancedPath}': an import occupies the required export identity.");
        }
        if (!File.Exists(source.Occurrence.PackagePath))
        {
            throw new FileNotFoundException(
                $"The installed texture donor package is missing: {source.Occurrence.PackagePath}",
                source.Occurrence.PackagePath);
        }

        using var donor = MEPackageHandler.OpenMEPackage(
            source.Occurrence.PackagePath,
            forceLoadFromDisk: true);
        if (donor.Game != destination.Game)
        {
            throw new InvalidDataException(
                $"Texture '{source.InstancedPath}' is from {donor.Game}, but the destination is {destination.Game}.");
        }

        var donorTexture = ResolveSourceExport(
            donor,
            source.InstancedPath,
            source.Occurrence.ExportUIndex);
        var parent = PackageIntegrity.EnsurePackagePath(
            destination,
            source.InstancedPath,
            "Texture2D");
        ExternalSkeletalMeshMaterializer.PrepareReferencedPackagePaths(destination, donorTexture);

        var relinker = new RelinkerOptionsPackage
        {
            ImportExportDependencies = true,
            GenerateImportsForGlobalFiles = false
        };
        var imported = EntryImporter.ImportExport(
            destination,
            donorTexture,
            parent?.UIndex ?? 0,
            relinker);
        MaterialisationVerifier.Relink(relinker);

        if (imported is not ExportEntry textureExport ||
            !textureExport.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"LEC did not materialise Texture2D '{source.InstancedPath}' as an export.");
        }
        if (!textureExport.InstancedFullPath.Equals(source.InstancedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The materialised Texture2D path changed from '{source.InstancedPath}' " +
                $"to '{textureExport.InstancedFullPath}'.");
        }

        VerifyBulkDataPreserved(donorTexture, textureExport);
        MaterialisationVerifier.Verify(textureExport, donor.Game, relinker, warnings);
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

    private static TextureCatalogOccurrence SelectOccurrence(
        string texturePath,
        string requestedPackagePath,
        TextureCatalogOccurrence effectiveOccurrence,
        IReadOnlyList<TextureCatalogOccurrence> occurrences,
        bool preferBiog)
    {
        if (preferBiog)
        {
            var seekfree = occurrences
                .Where(value => IsSeekfreePackage(value.PackagePath))
                .OrderByDescending(HasExternalMips)
                .ThenByDescending(value => value.MountPriority)
                .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (seekfree is not null)
            {
                return seekfree;
            }

            var nonSeekfree = occurrences
                .Where(value => IsCharacterCreatorPackage(value.PackagePath))
                .OrderByDescending(HasExternalMips)
                .ThenByDescending(value => value.MountPriority)
                .ThenBy(value => value.PackagePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (nonSeekfree is not null)
            {
                return nonSeekfree;
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedPackagePath))
        {
            var requested = occurrences.FirstOrDefault(value =>
                Path.GetFullPath(value.PackagePath).Equals(
                    Path.GetFullPath(requestedPackagePath),
                    StringComparison.OrdinalIgnoreCase));
            if (requested is not null)
            {
                return requested;
            }
        }

        return effectiveOccurrence;
    }

    private static bool HasExternalMips(TextureCatalogOccurrence occurrence) =>
        occurrence.Mips.Any(mip => ((StorageFlags)mip.StorageType & StorageFlags.externalFile) != 0);

    private static ExportEntry ResolveSourceExport(
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

    private static void VerifyBulkDataPreserved(ExportEntry source, ExportEntry destination)
    {
        var sourceTexture = new Texture2D(source);
        var destinationTexture = new Texture2D(destination);
        if (!string.Equals(
                source.GetProperty<NameProperty>("TextureFileCacheName")?.Value.Instanced,
                destination.GetProperty<NameProperty>("TextureFileCacheName")?.Value.Instanced,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Texture2D '{source.InstancedFullPath}' lost its TextureFileCacheName during PCC import.");
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
    TextureCatalogOccurrence Occurrence);
