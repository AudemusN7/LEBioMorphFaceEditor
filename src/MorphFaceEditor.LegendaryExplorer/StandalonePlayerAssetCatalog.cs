using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

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
        var loadedFiles = MELoadedFiles.GetFilesLoadedInGame(ToMeGame(game), forceUseCached: true);
        string? fallbackPackage = null;
        try { fallbackPackage = StandalonePlayerMorphImportService.ResolveInstalledSeed(game); }
        catch (Exception) { /* The seek-free package may still resolve without a player seed. */ }
        return requestedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && !path.Equals("None", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Path: path, Identity:
                FindInstalledExport(loadedFiles, path, "SkeletalMesh", packageLocalPath: true) ??
                FindPackageExport(fallbackPackage, path, "SkeletalMesh")))
            .Where(value => value.Identity is not null)
            .ToDictionary(value => value.Path, value => value.Identity!, StringComparer.OrdinalIgnoreCase);
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
        string? referencePackagePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ronPath);
        ArgumentNullException.ThrowIfNull(textures);
        LegendaryExplorerCoreRuntime.Initialize();

        var ron = TseHeadMorphRon.Read(ronPath);
        var requestedTextures = ron.MaterialData.Textures
            .Select(value => value.TextureReference?.InstancedPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requestedMeshes = ron.AccessoryMeshes
            .Prepend(ron.HairMesh)
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           !path.Equals("None", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var loadedFiles = MELoadedFiles.GetFilesLoadedInGame(ToMeGame(game), forceUseCached: true);
        using var packageCache = new PackageCache { CacheMaxSize = 8 };
        var referencePath = referencePackagePath is null
            ? StandalonePlayerMorphImportService.ResolveInstalledSeed(game)
            : Path.GetFullPath(referencePackagePath);
        var referencePackage = packageCache.GetCachedPackage(referencePath)
                               ?? throw new InvalidDataException(
                                   $"Could not open installed RON reference package '{referencePath}'.");
        var referenceResolver = new GamePackageReferenceResolver(packageCache);
        var textureAssets = new List<AssetIdentity>();
        foreach (var requestedPath in requestedTextures)
        {
            var registryMatches = textures
                .Where(candidate => MatchesRequestedTexturePath(candidate, game, requestedPath))
                .Take(2)
                .ToArray();
            if (registryMatches.Length == 1 && File.Exists(registryMatches[0].EffectiveOccurrence.PackagePath))
            {
                var occurrence = registryMatches[0].EffectiveOccurrence;
                textureAssets.Add(new AssetIdentity(
                    Path.GetFullPath(occurrence.PackagePath),
                    requestedPath,
                    occurrence.ExportUIndex,
                    "Texture2D"));
                continue;
            }

            if ((FindInstalledExport(loadedFiles, requestedPath, "Texture2D") ??
                 FindReferencedExport(referencePackage, referenceResolver, requestedPath, "Texture2D")) is { } texture)
            {
                textureAssets.Add(texture);
            }
        }

        var meshes = new List<AssetIdentity>();
        foreach (var requestedPath in requestedMeshes)
        {
            if ((FindInstalledExport(loadedFiles, requestedPath, "SkeletalMesh") ??
                 FindReferencedExport(referencePackage, referenceResolver, requestedPath, "SkeletalMesh")) is { } mesh)
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

    private static AssetIdentity? FindInstalledExport(
        IReadOnlyDictionary<string, string> loadedFiles,
        string requestedPath,
        string className,
        bool packageLocalPath = false)
    {
        var rootPackage = requestedPath.Split('.')[0] + ".pcc";
        if (!loadedFiles.TryGetValue(rootPackage, out var packagePath))
        {
            packagePath = loadedFiles
                .FirstOrDefault(pair => pair.Key.Equals(rootPackage, StringComparison.OrdinalIgnoreCase))
                .Value;
        }
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            return null;
        }

        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var matches = package.Exports
            .Where(export => export.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) &&
                             (export.InstancedFullPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase) ||
                              CanonicalPath(package, export).Equals(requestedPath, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? new AssetIdentity(
                Path.GetFullPath(packagePath),
                packageLocalPath ? matches[0].InstancedFullPath : requestedPath,
                matches[0].UIndex,
                matches[0].ClassName)
            : null;
    }

    private static AssetIdentity? FindPackageExport(string? packagePath, string requestedPath, string className)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath)) return null;
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var matches = package.Exports.Where(export => export.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) &&
                                                       export.InstancedFullPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length == 1
            ? new AssetIdentity(Path.GetFullPath(packagePath), matches[0].InstancedFullPath,
                matches[0].UIndex, matches[0].ClassName)
            : null;
    }

    private static bool CandidateMatchesGame(TextureCatalogCandidate candidate, MorphFaceGame game) =>
        (candidate.Game, game) switch
        {
            (TextureCatalogGame.LE1, MorphFaceGame.LE1) => true,
            (TextureCatalogGame.LE2, MorphFaceGame.LE2) => true,
            (TextureCatalogGame.LE3, MorphFaceGame.LE3) => true,
            _ => false
        };

    internal static bool MatchesRequestedTexturePath(
        TextureCatalogCandidate candidate,
        MorphFaceGame game,
        string requestedPath)
    {
        if (!CandidateMatchesGame(candidate, game))
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
}
