using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
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
        var resolved = Resolve(entry.Game, path, entry.ClassName, cache);
        if (resolved is not null || entry is not ImportEntry import) return resolved;
        // A level import can belong to its BioP/parent level rather than a
        // same-named BIOG. Keep LEC's contextual search, using official winners.
        var files = GetPackageFiles(entry.Game);
        foreach (var name in EntryImporter.GetPossibleImportFiles(import))
            if (files.TryGetValue(name, out var packagePath) &&
                TryExact(cache, packagePath, path, entry.ClassName) is { } contextual)
                return contextual;
        return null;
    }

    internal bool CanUseLocalAsset(ExportEntry entry)
    {
        if (entry.Game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3)) return true;
        return IsOfficialPackage(entry.FileRef.FilePath, entry.Game) ||
            ResolveIndexedOfficialOccurrences(entry.Game, entry.InstancedFullPath, entry.ClassName).Count == 0;
    }

    private IReadOnlyList<InstalledAssetOccurrence> ResolveIndexedOfficialOccurrences(MEGame game, string path, string className)
    {
        return _snapshot(game) is { } snapshot
            ? TextureRegistryIndex.For(snapshot).OfficialOccurrences(path, className) : [];
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
    /// Returns the complete indexed official MIC variant set in donor precedence order.
    /// </summary>
    internal IEnumerable<ExportEntry> ResolveOfficialVariants(
        MEGame game, string path, string className, PackageCache cache)
    {
        if (game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3) ||
            !InstalledAssetOccurrence.IsSupportedClass(className)) yield break;

        // Missing/outdated databases retain the exact named-package lookup used
        // elsewhere. Complete variant comparison requires a current database.
        if (_snapshot(game) is null)
        {
            if (Resolve(game, path, className, cache) is { } named) yield return named;
            yield break;
        }
        foreach (var occurrence in ResolveIndexedOfficialOccurrences(game, path, className))
        {
            var export = TryExact(cache, occurrence.PackagePath, path, className, occurrence.ExportUIndex);
            if (export is not null) yield return export;
        }
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
        return TextureRegistryBuilder.SelectOfficialFilenameWinners(
                TextureRegistryBuilder.GetOfficialPhysicalPackagePaths(ToMorphGame(game)), ToMorphGame(game))
            .ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);
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

    private static readonly TextureRegistryStore Store = new(TextureRegistryPaths.CreateDefault());
    private static TextureRegistrySnapshot? ReadSnapshot(MEGame game)
    {
        if (game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3)) return null;
        return Store.ReadWithStatus(ToMorphGame(game)).Snapshot;
    }
    private static MorphFaceGame ToMorphGame(MEGame game) => game switch
    {
        MEGame.LE1 => MorphFaceGame.LE1, MEGame.LE2 => MorphFaceGame.LE2,
        MEGame.LE3 => MorphFaceGame.LE3, _ => MorphFaceGame.Unsupported
    };
}
