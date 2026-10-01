using LegendaryExplorerCore.Packages;
using System.Runtime.CompilerServices;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Canonical official game donors, with explicitly selected textures as leaf exceptions.</summary>
public sealed class InstalledGameAssetResolver
{
    private readonly Func<MEGame, IReadOnlyDictionary<string, string>> _officialFiles;
    private readonly Func<MEGame, TextureRegistrySnapshot?> _snapshot;
    private readonly Dictionary<MEGame, IReadOnlyDictionary<string, string>> _packageFiles = [];
    private static readonly ConditionalWeakTable<TextureRegistrySnapshot, Dictionary<string, InstalledAssetOccurrence>> AssetIndices = new();

    internal InstalledGameAssetResolver(
        Func<MEGame, IReadOnlyDictionary<string, string>>? loadedFiles = null,
        Func<MEGame, TextureRegistrySnapshot?>? snapshot = null)
    {
        _officialFiles = loadedFiles ?? GetOfficialPackageFiles;
        _snapshot = snapshot ?? ReadSnapshot;
    }

    internal ExportEntry? Resolve(IEntry entry, PackageCache cache)
    {
        if (!InstalledAssetOccurrence.IsSupportedClass(entry.ClassName)) return null;
        var path = entry is ExportEntry export ? PccAssetPathPolicy.FromExport(export) : entry.InstancedFullPath;
        return Resolve(entry.Game, path, entry.ClassName, cache);
    }

    internal ExportEntry? Resolve(MEGame game, AssetIdentity identity, PackageCache cache)
    {
        // Only picker intent permits a texture leaf to override an official
        // identity. A physical package address alone does not imply selection.
        if (identity.IsExplicitTextureDonor &&
            identity.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
            !identity.IsImport)
            return RequireExact(cache, identity.PackagePath, identity.InstancedPath, identity.ClassName, identity.UIndex);
        var official = Resolve(game, identity.InstancedPath, identity.ClassName, cache);
        if (official is not null) return official;
        return !identity.IsImport && File.Exists(identity.PackagePath)
            ? RequireExact(cache, identity.PackagePath, identity.InstancedPath, identity.ClassName, identity.UIndex)
            : null;
    }

    internal ExportEntry? Resolve(MEGame game, string path, string className, PackageCache cache)
    {
        if (game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3) ||
            !InstalledAssetOccurrence.IsSupportedClass(className)) return null;
        if (_snapshot(game) is { } snapshot)
        {
            var index = AssetIndices.GetValue(snapshot, static value =>
            {
                var packages = value.OfficialPackagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                return value.Assets.Where(asset =>
                        (asset.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc) &&
                        packages.Contains(Path.GetFullPath(asset.PackagePath)))
                    .GroupBy(asset => Key(asset.ClassName, asset.InstancedPath), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.OrderByDescending(asset => asset.MountPriority)
                        .ThenBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(asset => asset.ExportUIndex).First(), StringComparer.OrdinalIgnoreCase);
            });
            if (index.TryGetValue(Key(className, path), out var asset))
            {
                var fingerprint = snapshot.MountedPackages.FirstOrDefault(file =>
                    file.PackagePath.Equals(asset.PackagePath, StringComparison.OrdinalIgnoreCase));
                if (fingerprint is not null && !fingerprint.IsCurrent())
                    throw new InvalidDataException($"Official donor '{asset.PackagePath}' changed. Rebuild this game's asset database.");
                return RequireExact(cache, asset.PackagePath, path, className, asset.ExportUIndex);
            }
        }
        // Before a database is built, only the exact named official package is
        // searched. No mod lookup, alternate object names or topology fallback.
        var files = GetPackageFiles(game);
        return files.TryGetValue(path.Split('.')[0] + ".pcc", out var packagePath)
            ? TryExact(cache, packagePath, path, className) : null;
    }

    internal IReadOnlyDictionary<string, string> GetPackageFiles(MEGame game)
    {
        if (!_packageFiles.TryGetValue(game, out var files))
            _packageFiles[game] = files = _officialFiles(game);
        return files;
    }

    internal static ExportEntry RequireExact(PackageCache cache, string packagePath, string path, string className, int index = 0) =>
        TryExact(cache, packagePath, path, className, index) ?? throw new InvalidDataException(
            $"{className} '{path}' was not found in donor '{packagePath}'. Rebuild the asset database if this donor changed.");

    private static ExportEntry? TryExact(PackageCache cache, string packagePath, string path, string className, int index = 0)
    {
        if (!File.Exists(packagePath)) return null;
        var package = cache.GetCachedPackage(packagePath);
        if (package is null) return null;
        bool Matches(ExportEntry export) => export.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase) &&
            (PccAssetPathPolicy.FromExport(export).Equals(path, StringComparison.OrdinalIgnoreCase) ||
             export.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (index > 0 && package.IsUExport(index) && Matches(package.GetUExport(index))) return package.GetUExport(index);
        var matches = package.Exports.Where(Matches).Take(2).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException($"Ambiguous {className} '{path}' in donor '{packagePath}'.")
        };
    }

    internal static IReadOnlyDictionary<string, string> GetOfficialPackageFiles(MEGame game)
    {
        if (game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LegendaryExplorerCoreRuntime.Initialize();
        return TextureRegistryBuilder.GetPhysicalPackagePaths(ToMorphGame(game))
            .Where(path => IsOfficialPackage(path, game)).GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group
                .OrderByDescending(path => LecTextureRegistryPackageScanner.GetMountPriority(path, game))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First(), StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsOfficialPackage(string path, MEGame game)
    {
        var dlc = LecTextureRegistryPackageScanner.FindDlcDirectory(path);
        return dlc is null || LegendaryExplorerCore.GameFilesystem.MELoadedDLC.IsOfficialDLC(dlc, game);
    }

    public static string ResolvePackage(MorphFaceGame game, string fileName) =>
        GetOfficialPackageFiles(LecTextureRegistryPackageScanner.ToMeGame(game)).TryGetValue(fileName, out var path)
            ? path : throw new FileNotFoundException($"The official {game} package '{fileName}' was not found.", fileName);

    public static IReadOnlyDictionary<string, string> GetOfficialPackages(MorphFaceGame game) =>
        GetOfficialPackageFiles(LecTextureRegistryPackageScanner.ToMeGame(game));

    public static IReadOnlyDictionary<string, AssetIdentity> ResolveInstalledReferences(
        MorphFaceGame game, IEnumerable<string> paths, string className)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var cache = new PackageCache { CacheMaxSize = 8 };
        var resolver = new InstalledGameAssetResolver();
        var result = new Dictionary<string, AssetIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path) &&
                     !path.Equals("None", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (resolver.Resolve(LecTextureRegistryPackageScanner.ToMeGame(game), path, className, cache) is { } export)
                result[path] = new(export.FileRef.FilePath, path, export.UIndex, className);
        return result;
    }

    public static void RefreshMounts(MorphFaceGame game) => LegendaryExplorerCoreRuntime.Initialize();
    internal static void RefreshMounts(MEGame game) => LegendaryExplorerCoreRuntime.Initialize();
    internal static bool AreMountedPackagesCurrent(MorphFaceGame game, IReadOnlyList<string> paths) =>
        paths.Count == 0 || paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(TextureRegistryBuilder.GetPhysicalPackagePaths(game));

    private static readonly TextureRegistryStore Store = new(TextureRegistryPaths.CreateDefault());
    private static readonly object Sync = new();
    private static readonly Dictionary<MEGame, ((string Path, long Length, DateTime LastWriteTimeUtc)? Fingerprint, TextureRegistrySnapshot? Snapshot)> Snapshots = [];
    private static TextureRegistrySnapshot? ReadSnapshot(MEGame game)
    {
        if (game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3)) return null;
        var mfeGame = ToMorphGame(game);
        var fingerprint = Store.GetFileFingerprint(mfeGame);
        lock (Sync)
        {
            if (Snapshots.TryGetValue(game, out var cached) && cached.Fingerprint == fingerprint) return cached.Snapshot;
            var snapshot = Store.ReadWithStatus(mfeGame).Snapshot;
            Snapshots[game] = (fingerprint, snapshot);
            return snapshot;
        }
    }
    private static MorphFaceGame ToMorphGame(MEGame game) => game switch
    {
        MEGame.LE1 => MorphFaceGame.LE1, MEGame.LE2 => MorphFaceGame.LE2,
        MEGame.LE3 => MorphFaceGame.LE3, _ => MorphFaceGame.Unsupported
    };
    private static string Key(string className, string path) => $"{className}|{path}";
}
