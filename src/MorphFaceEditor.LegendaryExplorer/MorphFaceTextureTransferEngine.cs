using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Textures;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

internal sealed record TextureTransferResult(
    MorphFaceMaterialData MaterialData,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Applies the reviewed cross-game texture policy, resolving legal destination references and
/// embedding only the small allowlist of later-game alien textures that LE1 cannot supply.
/// </summary>
internal static class MorphFaceTextureTransferEngine
{
    private static readonly string[] HumanDonorPackages =
    [
        "BIOG_HMM_HIR_PRO_R.pcc",
        "BIOG_HMF_HIR_PRO.pcc",
        "BIOG_HMM_HED_PROMorph.pcc",
        "BIOG_HMF_HED_PROMorph_R.pcc"
    ];

    private static readonly HashSet<string> EmbedIntoLe1 = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASA_HED_PRO_Tat2",
        "BAT_HED_PROMorph_Add4",
        "TUR_HED_PRO_Add2"
    };

    internal static TextureTransferResult Transfer(
        IMEPackage destination,
        MorphFaceMaterialData source,
        IReadOnlySet<string> supportedScalars,
        IReadOnlySet<string> supportedVectors,
        IReadOnlySet<string> supportedTextures,
        MEGame sourceGame,
        string sourceProfileKey,
        string sourcePackagePath,
        string? targetTemplatePackagePath)
    {
        var warnings = new List<string>();
        var textures = new List<TextureMaterialOverride>();
        using var donors = new DonorPackages();
        foreach (var value in source.Textures.Where(value =>
                     value.TextureReference is not null && supportedTextures.Contains(value.Name)))
        {
            var sourcePath = value.TextureReference!.InstancedPath;
            var decision = Decide(sourceGame, destination.Game, sourceProfileKey, value.Name, sourcePath);
            if (decision.Kind is TransferKind.UseDefault or TransferKind.Ignore)
            {
                continue;
            }

            IEntry? targetEntry;
            if (decision.Kind == TransferKind.EmbedSource)
            {
                targetEntry = EmbedPackageStoredSourceTexture(
                    destination,
                    sourceGame,
                    sourcePackagePath,
                    sourcePath,
                    donors,
                    warnings);
            }
            else
            {
                var requestedPath = decision.TargetPath ?? sourcePath;
                var resolvedPath = ResolveTargetPath(
                    destination,
                    requestedPath,
                    targetTemplatePackagePath,
                    donors);
                targetEntry = resolvedPath is null
                    ? null
                    : EnsureTextureImport(destination, resolvedPath);
            }

            if (targetEntry is null)
            {
                warnings.Add(
                    $"Texture override '{value.Name}' could not map '{sourcePath}' into {destination.Game}; " +
                    "the destination material default was used.");
                continue;
            }
            textures.Add(value with { TextureReference = ToIdentity(targetEntry) });
        }

        return new TextureTransferResult(
            new MorphFaceMaterialData(
                source.Scalars.Where(value => supportedScalars.Contains(value.Name)).ToArray(),
                source.Vectors.Where(value => supportedVectors.Contains(value.Name)).ToArray(),
                textures),
            warnings);
    }

    private static TransferDecision Decide(
        MEGame sourceGame,
        MEGame targetGame,
        string sourceProfileKey,
        string parameter,
        string sourcePath)
    {
        var objectName = ObjectName(sourcePath);
        if (targetGame == MEGame.LE1 && EmbedIntoLe1.Contains(objectName))
        {
            return new TransferDecision(TransferKind.EmbedSource);
        }
        if (string.Equals(objectName, "HMF_HIR_Afr_Mask", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferDecision(TransferKind.Ignore);
        }
        if (sourceGame == MEGame.LE2 && targetGame == MEGame.LE1 &&
            string.Equals(objectName, "GBL_HMF_HIR_White", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferDecision(
                TransferKind.Alias,
                "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_White");
        }
        if (targetGame == MEGame.LE3 &&
            string.Equals(objectName, "HMF_HIR_PROAll_SpecShift", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferDecision(
                TransferKind.Alias,
                "BIOG_HMM_HIR_PRO_R.Global.HMM_HIR_PROAll_SpecShift");
        }
        if (targetGame == MEGame.LE3 &&
            sourceProfileKey.EndsWith("human-female", StringComparison.OrdinalIgnoreCase) &&
            sourcePath.StartsWith("BIOG_HMF_HIR_PRO.", StringComparison.OrdinalIgnoreCase) &&
            IsStandaloneHairParameter(parameter))
        {
            return new TransferDecision(TransferKind.Ignore);
        }
        if (targetGame == MEGame.LE3 && objectName.EndsWith("_Tang", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferDecision(TransferKind.Ignore);
        }
        if (targetGame == MEGame.LE3 &&
            string.Equals(objectName, "HMF_HED_PROAshley_Scalp_Stack", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferDecision(TransferKind.UseDefault);
        }
        if (sourceGame == MEGame.LE3 && targetGame is MEGame.LE1 or MEGame.LE2 &&
            string.Equals(objectName, "HMF_HED_PROBase_Scalp_Bald_Norm", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferDecision(TransferKind.UseDefault);
        }
        if (sourceGame == MEGame.LE3 && targetGame is MEGame.LE1 or MEGame.LE2 &&
            string.Equals(objectName, "HMM_HED_PROJoker_FaceSD_Diff", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferDecision(
                TransferKind.Alias,
                "BIOG_HMM_HED_PROMorph.Joker.HMM_HED_PROJoker_Face_Diff_Stack");
        }
        if (sourceGame == MEGame.LE3 && targetGame is MEGame.LE1 or MEGame.LE2 &&
            string.Equals(objectName, "HMM_HED_PROJoker_ScalpSD_Diff", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferDecision(
                TransferKind.Alias,
                "BIOG_HMM_HED_PROMorph.Joker.HMM_HED_PROJoker_Scalp_Diff_Stack");
        }
        return new TransferDecision(TransferKind.Resolve);
    }

    private static bool IsStandaloneHairParameter(string parameter) =>
        parameter.StartsWith("HAIR_", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(parameter, "Hair_Spec", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveTargetPath(
        IMEPackage destination,
        string requestedPath,
        string? targetTemplatePackagePath,
        DonorPackages donors)
    {
        if (FindEntryByCanonicalPath(destination, requestedPath, "Texture2D") is not null)
        {
            return requestedPath;
        }

        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in TargetDonorPaths(destination.Game, requestedPath, targetTemplatePackagePath))
        {
            if (!File.Exists(path) || SamePath(path, destination.FilePath))
            {
                continue;
            }
            var donor = donors.Open(path);
            if (donor.Game != destination.Game)
            {
                continue;
            }
            foreach (var entry in TextureEntries(donor))
            {
                var canonicalPath = CanonicalPath(donor, entry);
                if (!candidates.TryGetValue(canonicalPath, out var packagePaths))
                {
                    candidates[canonicalPath] = packagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                packagePaths.Add(path);
            }
        }

        if (candidates.ContainsKey(requestedPath))
        {
            return requestedPath;
        }
        var objectName = ObjectName(requestedPath);
        var matches = candidates.Keys.Where(path =>
            string.Equals(ObjectName(path), objectName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static IEnumerable<string> TargetDonorPaths(
        MEGame game,
        string requestedPath,
        string? targetTemplatePackagePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(targetTemplatePackagePath))
        {
            var fullPath = Path.GetFullPath(targetTemplatePackagePath);
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }

        var loadedFiles = MELoadedFiles.GetFilesLoadedInGame(game, forceUseCached: true);
        var rootPackage = requestedPath.Split('.').FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(rootPackage) &&
            loadedFiles.TryGetValue($"{rootPackage}.pcc", out var rootPath) && seen.Add(rootPath))
        {
            yield return rootPath;
        }
        foreach (var fileName in HumanDonorPackages)
        {
            if (loadedFiles.TryGetValue(fileName, out var path) && seen.Add(path))
            {
                yield return path;
            }
        }
    }

    private static IEntry EnsureTextureImport(IMEPackage destination, string canonicalPath)
    {
        if (FindEntryByCanonicalPath(destination, canonicalPath, "Texture2D") is { } existing)
        {
            return existing;
        }
        var segments = canonicalPath.Split('.');
        IEntry? parent = null;
        var currentPath = string.Empty;
        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = index == 0 ? segments[index] : $"{currentPath}.{segments[index]}";
            var className = index == segments.Length - 1 ? "Texture2D" : "Package";
            parent = destination.FindEntry(currentPath, className) ?? destination.CreateImport(
                className,
                NameReference.FromInstancedString(segments[index]),
                parent);
        }
        return parent!;
    }

    private static IEntry EmbedPackageStoredSourceTexture(
        IMEPackage destination,
        MEGame sourceGame,
        string sourcePackagePath,
        string sourcePath,
        DonorPackages donors,
        List<string> warnings)
    {
        var sourceExport = ResolveSourceExport(sourceGame, sourcePackagePath, sourcePath, donors)
                           ?? throw new InvalidDataException(
                               $"Source texture '{sourcePath}' could not be resolved from the installed {sourceGame} packages.");
        var sourceTexture = new Texture2D(sourceExport);
        var pixelFormat = Image.getPixelFormatType(sourceTexture.TextureFormat);
        var image = sourceTexture.ToImage(pixelFormat);
        var parent = destination.FindExport("MFE_EmbeddedTextures", "Package")
                     ?? destination.CreatePackageExport("MFE_EmbeddedTextures");
        var rop = new RelinkerOptionsPackage { ImportExportDependencies = true };
        var issues = EntryImporter.ImportAndRelinkEntries(
            EntryImporter.PortingOption.CloneAllDependencies,
            sourceExport,
            destination,
            parent,
            shouldRelink: true,
            rop,
            out var importedEntry);
        warnings.AddRange(issues.Select(issue => issue.Message));
        if (importedEntry is not ExportEntry importedTexture ||
            !string.Equals(importedTexture.ClassName, "Texture2D", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"LEC did not embed source Texture2D '{sourcePath}'.");
        }
        var replacement = new Texture2D(importedTexture);
        warnings.AddRange(replacement.Replace(
            image,
            importedTexture.GetProperties(),
            isPackageStored: true));
        warnings.Add(
            $"Embedded '{sourcePath}' as package-stored because {destination.Game} has no stock equivalent. " +
            "You may wish to move this texture into your mod's TFC before release.");
        return importedTexture;
    }

    private static ExportEntry? ResolveSourceExport(
        MEGame sourceGame,
        string sourcePackagePath,
        string sourcePath,
        DonorPackages donors)
    {
        if (File.Exists(sourcePackagePath))
        {
            var sourcePackage = donors.Open(sourcePackagePath);
            if (sourcePackage.Game == sourceGame &&
                FindExportByCanonicalPath(sourcePackage, sourcePath, "Texture2D") is { } local)
            {
                return local;
            }
        }
        var rootPackage = sourcePath.Split('.').FirstOrDefault();
        var loadedFiles = MELoadedFiles.GetFilesLoadedInGame(sourceGame, forceUseCached: true);
        if (!string.IsNullOrWhiteSpace(rootPackage) &&
            loadedFiles.TryGetValue($"{rootPackage}.pcc", out var path))
        {
            return FindExportByCanonicalPath(donors.Open(path), sourcePath, "Texture2D");
        }
        return null;
    }

    private static ExportEntry? FindExportByCanonicalPath(IMEPackage package, string path, string className) =>
        FindEntryByCanonicalPath(package, path, className) as ExportEntry ??
        package.Exports.FirstOrDefault(entry =>
            string.Equals(entry.ClassName, className, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(CanonicalPath(package, entry), path, StringComparison.OrdinalIgnoreCase));

    private static IEntry? FindEntryByCanonicalPath(IMEPackage package, string path, string className)
    {
        if (package.FindEntry(path, className) is { } exact)
        {
            return exact;
        }
        var root = Path.GetFileNameWithoutExtension(package.FilePath);
        return path.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase)
            ? package.FindEntry(path[(root.Length + 1)..], className)
            : null;
    }

    private static IEnumerable<IEntry> TextureEntries(IMEPackage package) =>
        package.Exports.Cast<IEntry>().Concat(package.Imports).Where(entry =>
            string.Equals(entry.ClassName, "Texture2D", StringComparison.OrdinalIgnoreCase));

    private static string CanonicalPath(IMEPackage package, IEntry entry)
    {
        if (entry is ImportEntry || entry.InstancedFullPath.StartsWith("BIO", StringComparison.OrdinalIgnoreCase))
        {
            return entry.InstancedFullPath;
        }
        return $"{Path.GetFileNameWithoutExtension(package.FilePath)}.{entry.InstancedFullPath}";
    }

    private static string ObjectName(string path) => path.Split('.').LastOrDefault() ?? path;

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        StringComparison.OrdinalIgnoreCase);

    private static AssetIdentity ToIdentity(IEntry entry) => new(
        Path.GetFullPath(entry.FileRef.FilePath),
        entry.InstancedFullPath,
        entry.UIndex,
        entry.ClassName,
        entry is ImportEntry);

    private enum TransferKind
    {
        Resolve,
        Alias,
        UseDefault,
        Ignore,
        EmbedSource
    }

    private sealed record TransferDecision(TransferKind Kind, string? TargetPath = null);

    private sealed class DonorPackages : IDisposable
    {
        private readonly Dictionary<string, IMEPackage> _packages = new(StringComparer.OrdinalIgnoreCase);

        public IMEPackage Open(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!_packages.TryGetValue(fullPath, out var package))
            {
                _packages[fullPath] = package = MEPackageHandler.OpenMEPackage(fullPath, forceLoadFromDisk: true);
            }
            return package;
        }

        public void Dispose()
        {
            foreach (var package in _packages.Values)
            {
                package.Dispose();
            }
        }
    }
}
