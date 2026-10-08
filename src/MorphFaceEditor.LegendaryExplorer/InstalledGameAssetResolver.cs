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
    private static readonly ConditionalWeakTable<TextureRegistrySnapshot, Dictionary<string, IReadOnlyList<InstalledAssetOccurrence>>> AssetIndices = new();
    private static readonly ConditionalWeakTable<TextureRegistrySnapshot, HashSet<string>> ModPackages = new();

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

    internal bool CanUseLocalAsset(ExportEntry entry)
    {
        if (entry.Game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3)) return true;
        if (!IsOfficialPackage(entry.FileRef.FilePath, entry.Game))
            return ResolveIndexedOfficialOccurrences(entry.Game, entry.InstancedFullPath, entry.ClassName).Count == 0;
        if (_snapshot(entry.Game) is not { } snapshot) return true;
        var mods = ModPackages.GetValue(snapshot, static value => value.Assets
            .Where(asset => asset.Origin == TextureCatalogOrigin.Mod)
            .Select(asset => Path.GetFullPath(asset.PackagePath)).ToHashSet(StringComparer.OrdinalIgnoreCase));
        return !mods.Contains(Path.GetFullPath(entry.FileRef.FilePath)) ||
            ResolveIndexedOfficialOccurrences(entry.Game, entry.InstancedFullPath, entry.ClassName).Count == 0;
    }

    private IReadOnlyList<InstalledAssetOccurrence> ResolveIndexedOfficialOccurrences(MEGame game, string path, string className)
    {
        if (_snapshot(game) is not { } snapshot) return [];
        var index = AssetIndices.GetValue(snapshot, static value =>
        {
            var packages = value.OfficialPackagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return value.Assets.Where(asset =>
                    (asset.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc) &&
                    packages.Contains(Path.GetFullPath(asset.PackagePath)))
                .GroupBy(asset => Key(asset.ClassName, asset.InstancedPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<InstalledAssetOccurrence>)group
                    .OrderByDescending(asset => asset.MountPriority)
                    .ThenBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(asset => asset.ExportUIndex).ToArray(), StringComparer.OrdinalIgnoreCase);
        });
        return index.GetValueOrDefault(Key(className, path), []);
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
        if (ResolveIndexedOfficialOccurrences(game, path, className).FirstOrDefault() is { } asset)
        {
            return TryExact(cache, asset.PackagePath, path, className, asset.ExportUIndex);
        }
        // Before a database is built, only the exact named official package is
        // searched. No mod lookup, alternate object names or topology fallback.
        var files = GetPackageFiles(game);
        return files.TryGetValue(path.Split('.')[0] + ".pcc", out var packagePath)
            ? TryExact(cache, packagePath, path, className) : null;
    }

    /// <summary>
    /// Returns every indexed official occurrence of one canonical asset identity,
    /// in donor precedence order, followed by bounded exact-package checks for
    /// reviewed MIC provenance packages.
    /// </summary>
    internal IEnumerable<ExportEntry> ResolveOfficialVariants(
        MEGame game, string path, string className, PackageCache cache)
    {
        if (game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3) ||
            !InstalledAssetOccurrence.IsSupportedClass(className)) yield break;

        var resolvedOccurrences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in ResolveIndexedOfficialOccurrences(game, path, className))
        {
            var export = TryExact(cache, occurrence.PackagePath, path, className, occurrence.ExportUIndex);
            if (export is null) continue;
            var occurrenceKey = $"{export.FileRef.FilePath}|{export.UIndex}";
            if (!resolvedOccurrences.Add(occurrenceKey)) continue;
            yield return export;
        }

        // The texture registry keeps only one MIC occurrence per canonical
        // identity. Also check reviewed provenance locations that can contain
        // legitimate cooked eye variants, without opening other game
        // packages or allowing mod-sidecar replacements.
        var packageNames = new List<string>
        {
            path.Split('.')[0] + ".pcc",
            "BIOG_MORPH_FACE.pcc"
        };
        if (game == MEGame.LE2 && path.Equals(
                "BIOG_ASA_HED_PROMorph_R.ASA_HED_EYE_MAT_1a", StringComparison.OrdinalIgnoreCase))
        {
            // This reviewed basegame scene export supplies the ASA Effects-parent
            // variant absent from the usual source/template provenance checks.
            packageNames.Add("BioD_BchLmL_102BeachFight.pcc");
        }
        else if (game == MEGame.LE3 && path.Equals(
                     "BIOG_ASA_HED_PROMorph_R.PROBase.ASA_HED_PROBase_EYE_MAT_1a",
                     StringComparison.OrdinalIgnoreCase))
        {
            // This reviewed basegame scene export supplies LE3's ASA
            // Effects-parent variant with its complete parameter set.
            packageNames.Add("BioP_Cat002.pcc");
        }
        packageNames = packageNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var snapshot = _snapshot(game);
        foreach (var packageName in packageNames)
        {
            if (snapshot is not null)
            {
                foreach (var packagePath in snapshot.OfficialPackagePaths
                             .Where(candidate => Path.GetFileName(candidate).Equals(
                                 packageName, StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(candidate => candidate, StringComparer.Ordinal))
                    if (TryGetOfficialPackageVariant(packagePath, path, className, cache,
                            resolvedOccurrences) is { } export)
                        yield return export;
            }
            else
            {
                if (GetPackageFiles(game).TryGetValue(packageName, out var packagePath) &&
                    TryGetOfficialPackageVariant(packagePath, path, className, cache,
                        resolvedOccurrences) is { } export)
                    yield return export;
            }
        }
    }

    private static ExportEntry? TryGetOfficialPackageVariant(
        string packagePath, string path, string className, PackageCache cache,
        HashSet<string> resolvedOccurrences)
    {
        var export = TryExact(cache, packagePath, path, className);
        if (export is null) return null;
        var occurrenceKey = $"{export.FileRef.FilePath}|{export.UIndex}";
        return resolvedOccurrences.Add(occurrenceKey) ? export : null;
    }

    internal IReadOnlyDictionary<string, string> GetPackageFiles(MEGame game)
    {
        if (!_packageFiles.TryGetValue(game, out var files))
            _packageFiles[game] = files = _officialFiles(game);
        return files;
    }

    internal static ExportEntry RequireExact(PackageCache cache, string packagePath, string path, string className, int index = 0) =>
        TryExact(cache, packagePath, path, className, index) ?? throw new InvalidDataException(
            $"{className} '{path}' was not found in donor '{packagePath}'.");

    internal static ExportEntry? TryExact(PackageCache cache, string packagePath, string path, string className, int index = 0)
    {
        var package = PackageSourceCache.GetPackage(cache, packagePath);
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
