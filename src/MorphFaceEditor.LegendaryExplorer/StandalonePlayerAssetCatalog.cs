using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Exact installed-game asset candidates used when a standalone player RON
/// references an export that is not already present in its seed package.
/// Candidates are deliberately path-based; same-name substitution is never
/// performed by the standalone importer.
/// </summary>
public sealed record StandalonePlayerAssetCatalog(
    MorphFaceGame Game,
    IReadOnlyList<AssetIdentity> Textures,
    IReadOnlyList<AssetIdentity> SkeletalMeshes)
{
    public static StandalonePlayerAssetCatalog Empty(MorphFaceGame game) =>
        new(game, [], []);

    /// <summary>
    /// Resolves full seek-free mesh paths only through the installed package named by each path.
    /// No object-name or cross-package substitution is permitted.
    /// </summary>
    public static IReadOnlyDictionary<string, AssetIdentity> ResolveInstalledSkeletalMeshes(
        MorphFaceGame game,
        IEnumerable<string> requestedPaths)
    {
        ArgumentNullException.ThrowIfNull(requestedPaths);
        LegendaryExplorerCoreRuntime.Initialize();
        var installedAssets = new InstalledGameAssetResolver();
        using var packageCache = new CatalogHeaderCache { CacheMaxSize = 8 };
        var loadedFiles = installedAssets.GetPackageFiles(ToMeGame(game));
        var referenceResolver = new GamePackageReferenceResolver(packageCache, installedAssets);
        string? fallbackPackage = null;
        IMEPackage? seedPackage = null;
        var attemptedSeedLookup = false;
        return requestedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && !path.Equals("None", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var indexed = referenceResolver.ResolveInstalled(ToMeGame(game), path, "SkeletalMesh");
                var identity = indexed is not null
                    ? Identity(indexed, path)
                    : FindInstalledExport(loadedFiles, path, "SkeletalMesh", packageCache) is { } exact
                        ? Identity(exact, exact.InstancedFullPath)
                        : FindPackageExport(SeedPackage(), path, "SkeletalMesh");
                return (Path: path, Identity: identity);
            })
            .Where(value => value.Identity is not null)
            .ToDictionary(value => value.Path, value => value.Identity!, StringComparer.OrdinalIgnoreCase);

        IMEPackage? SeedPackage()
        {
            if (seedPackage is not null) return seedPackage;
            if (!attemptedSeedLookup)
            {
                attemptedSeedLookup = true;
                try { fallbackPackage = StandalonePlayerMorphImportService.ResolveInstalledSeed(game, loadedFiles); }
                catch (Exception) { /* The seek-free package may still resolve without a player seed. */ }
            }
            return string.IsNullOrWhiteSpace(fallbackPackage)
                ? null
                : seedPackage = PackageSourceCache.GetPackage(packageCache, fallbackPackage);
        }
    }

    /// <summary>
    /// Builds the bounded donor set needed by one RON. The verified texture
    /// registry is preferred; remaining references are resolved only through
    /// their named installed package or the seed's exact import target.
    /// </summary>
    public static StandalonePlayerAssetCatalog ForRon(
        MorphFaceGame game,
        string ronPath,
        IReadOnlyList<TextureCatalogCandidate> textures,
        string? referencePackagePath = null,
        IReadOnlyList<AttachmentMeshCandidate>? attachmentMeshes = null)
        => ForHead(game, TseHeadMorphRon.Read(ronPath), textures, referencePackagePath, attachmentMeshes);

    public static StandalonePlayerAssetCatalog ForHead(
        MorphFaceGame game,
        HeadMorphPayload head,
        IReadOnlyList<TextureCatalogCandidate> textures,
        string? referencePackagePath = null,
        IReadOnlyList<AttachmentMeshCandidate>? attachmentMeshes = null)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(textures);
        LegendaryExplorerCoreRuntime.Initialize();

        var requestedTextures = head.MaterialData.Textures
            .Select(value => value.TextureReference?.InstancedPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requestedMeshes = head.AccessoryMeshes
            .Prepend(head.HairMesh)
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           !path.Equals("None", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var installedAssets = new InstalledGameAssetResolver();
        var loadedFiles = installedAssets.GetPackageFiles(ToMeGame(game));
        using var packageCache = new CatalogHeaderCache { CacheMaxSize = 8 };
        var officialPaths = loadedFiles.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IMEPackage? referencePackage = null;
        string? resolvedReferencePath = referencePackagePath is null ? null : Path.GetFullPath(referencePackagePath);
        IMEPackage ReferencePackage()
        {
            resolvedReferencePath ??= StandalonePlayerMorphImportService.ResolveInstalledSeed(game, loadedFiles);
            return referencePackage ??= PackageSourceCache.GetPackage(packageCache, resolvedReferencePath)
                ?? throw new InvalidDataException($"Could not open installed RON reference package '{resolvedReferencePath}'.");
        }
        var referenceResolver = new GamePackageReferenceResolver(packageCache, installedAssets);
        var textureAssets = new List<AssetIdentity>();
        foreach (var requestedPath in requestedTextures)
        {
            if (referenceResolver.ResolveInstalled(ToMeGame(game), requestedPath, "Texture2D") is { } installedTexture)
            {
                textureAssets.Add(Identity(installedTexture, requestedPath));
                continue;
            }
            var registryMatches = textures
                .Where(candidate => MatchesRequestedTexturePath(candidate, game, requestedPath))
                .Take(2)
                .ToArray();
            if (registryMatches.Length == 1 && File.Exists(registryMatches[0].EffectiveOccurrence.PackagePath) &&
                (officialPaths.Contains(registryMatches[0].EffectiveOccurrence.PackagePath) ||
                 registryMatches[0].EffectiveOccurrence.Origin is TextureCatalogOrigin.Mod or TextureCatalogOrigin.Manual))
            {
                var occurrence = registryMatches[0].EffectiveOccurrence;
                textureAssets.Add(new AssetIdentity(
                    Path.GetFullPath(occurrence.PackagePath),
                    requestedPath,
                    occurrence.ExportUIndex,
                    "Texture2D"));
                continue;
            }

            if (FindInstalledExport(loadedFiles, requestedPath, "Texture2D", packageCache) is { } namedTexture)
            {
                textureAssets.Add(Identity(namedTexture, requestedPath));
                continue;
            }

            if (FindReferencedExport(ReferencePackage(), referenceResolver, requestedPath, "Texture2D") is { } texture)
            {
                textureAssets.Add(texture);
            }
        }

        var meshes = new List<AssetIdentity>();
        foreach (var requestedPath in requestedMeshes)
        {
            if (referenceResolver.ResolveInstalled(ToMeGame(game), requestedPath, "SkeletalMesh") is { } installedMesh)
            {
                meshes.Add(Identity(installedMesh, requestedPath));
                continue;
            }
            if (attachmentMeshes is not null &&
                AttachmentMeshCatalogResolver.Resolve(attachmentMeshes, requestedPath) is { } indexedMesh)
            {
                meshes.Add(indexedMesh);
                continue;
            }
            if (FindInstalledExport(loadedFiles, requestedPath, "SkeletalMesh", packageCache) is { } namedMesh)
            {
                meshes.Add(Identity(namedMesh, requestedPath));
                continue;
            }
            if (FindReferencedExport(ReferencePackage(), referenceResolver, requestedPath, "SkeletalMesh") is { } mesh)
            {
                meshes.Add(mesh);
            }
        }

        return new StandalonePlayerAssetCatalog(game, textureAssets, meshes);
    }

    private static AssetIdentity? FindReferencedExport(
        IMEPackage seedPackage,
        GamePackageReferenceResolver resolver,
        string requestedPath,
        string className)
    {
        var resolved = resolver.Resolve(seedPackage.FindEntry(requestedPath, className));
        if (resolved is null ||
            !resolved.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) ||
            !CanonicalPath(resolved.FileRef, resolved).Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new AssetIdentity(
            Path.GetFullPath(resolved.FileRef.FilePath),
            requestedPath,
            resolved.UIndex,
            resolved.ClassName);
    }

    private static ExportEntry? FindInstalledExport(
        IReadOnlyDictionary<string, string> loadedFiles,
        string requestedPath,
        string className,
        PackageCache packageCache)
    {
        var rootPackage = requestedPath.Split('.')[0] + ".pcc";
        if (!loadedFiles.TryGetValue(rootPackage, out var packagePath) ||
            PackageSourceCache.GetPackage(packageCache, packagePath) is not { } package)
            return null;

        var matches = package.Exports
            .Where(export => export.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) &&
                             (export.InstancedFullPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase) ||
                              CanonicalPath(package, export).Equals(requestedPath, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static AssetIdentity? FindPackageExport(IMEPackage? package, string requestedPath, string className)
    {
        if (package is null) return null;
        var matches = package.Exports.Where(export => export.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) &&
                                                       export.InstancedFullPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length == 1
            ? new AssetIdentity(Path.GetFullPath(package.FilePath), matches[0].InstancedFullPath,
                matches[0].UIndex, matches[0].ClassName)
            : null;
    }

    private static AssetIdentity Identity(ExportEntry export, string requestedPath) => new(
        Path.GetFullPath(export.FileRef.FilePath), requestedPath, export.UIndex, export.ClassName);

    internal static bool MatchesRequestedTexturePath(
        TextureCatalogCandidate candidate,
        MorphFaceGame game,
        string requestedPath)
    {
        if (candidate.Game != game)
        {
            return false;
        }
        if (candidate.InstancedPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var packageName = Path.GetFileNameWithoutExtension(candidate.EffectiveOccurrence.PackagePath);
        return !string.IsNullOrWhiteSpace(packageName) &&
               $"{packageName}.{candidate.InstancedPath}".Equals(
                   requestedPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalPath(IMEPackage package, IEntry entry) =>
        entry is ImportEntry || entry.InstancedFullPath.StartsWith("BIO", StringComparison.OrdinalIgnoreCase)
            ? entry.InstancedFullPath
            : $"{Path.GetFileNameWithoutExtension(package.FilePath)}.{entry.InstancedFullPath}";

    private static MEGame ToMeGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => MEGame.LE1,
        MorphFaceGame.LE2 => MEGame.LE2,
        MorphFaceGame.LE3 => MEGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game,
            "Standalone player imports support only Legendary Edition games.")
    };

    // Catalogue results contain detached identities only. Their lookup needs
    // package tables, never texture/mesh payloads; keep these readers private so
    // no partial package can enter the later materialisation operation.
    private sealed class CatalogHeaderCache : PackageCache
    {
        public override IMEPackage GetCachedPackage(string packagePath, bool openIfNotInCache = true,
            Func<string, IMEPackage>? openPackageMethod = null) =>
            base.GetCachedPackage(packagePath, openIfNotInCache,
                static path => MEPackageHandler.UnsafePartialLoad(path, _ => false));
    }
}
