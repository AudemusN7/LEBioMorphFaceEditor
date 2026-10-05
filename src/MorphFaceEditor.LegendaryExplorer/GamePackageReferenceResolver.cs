using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Resolves cross-package Unreal imports, with a deterministic cooked-package
/// fallback for files whose import table cannot be resolved by the object DB.
/// </summary>
internal sealed class GamePackageReferenceResolver(PackageCache packageCache, InstalledGameAssetResolver? installedAssets = null)
{
    private readonly InstalledGameAssetResolver _installedAssets = installedAssets ?? new();

    public ExportEntry? Resolve(IEntry? entry)
    {
        if (entry is null) return null;
        if (entry is ExportEntry local && (MaterialInstanceSourcePolicy.IsInstance(local) ||
            (local.ClassName is "Texture2D" or "TextureCube" or "SkeletalMesh") && _installedAssets.CanUseLocalAsset(local)))
        {
            // The open package already supplies these authored assets. Canonical
            // effects/base material graphs still use the official resolver below.
            return local;
        }
        return _installedAssets.Resolve(entry, packageCache) ?? (entry switch
        {
            ExportEntry export => export,
            ImportEntry import => TryResolveImport(import),
            _ => null
        });
    }

    public ExportEntry Require(IEntry entry, string purpose)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var resolved = Resolve(entry);
        if (resolved is not null)
        {
            return resolved;
        }

        var candidates = entry is ImportEntry import
            ? CandidatePackagePaths(import).ToArray()
            : [];
        var searched = candidates.Length == 0
            ? "no fallback package could be inferred"
            : $"searched {string.Join(", ", candidates.Select(path => $"'{path}'"))}";
        throw new InvalidDataException(
            $"Could not resolve {purpose} '{entry.InstancedFullPath}' ({entry.ClassName}) " +
            $"referenced by '{entry.FileRef.FilePath}'; {searched}.");
    }

    private ExportEntry? TryResolveImport(ImportEntry import)
    {
        foreach (var packagePath in CandidatePackagePaths(import))
        {
            if (!File.Exists(packagePath))
            {
                continue;
            }

            try
            {
                var package = PackageSourceCache.GetPackage(packageCache, packagePath);
                if (package is null)
                {
                    continue;
                }
                if (package.FindEntry(import.InstancedFullPath, import.ClassName) is ExportEntry exact)
                {
                    return _installedAssets.Resolve(exact, packageCache) ?? exact;
                }
            }
            catch
            {
                // Continue through the bounded candidate set. Require() reports
                // every attempted path if no usable export can be recovered.
            }
        }
        try
        {
            if (EntryImporter.ResolveImport(import, packageCache) is { } resolved)
                return _installedAssets.Resolve(resolved, packageCache) ?? resolved;
        }
        catch
        {
            // Legacy metadata can be incomplete. Require() reports the exact
            // bounded search after native import resolution also fails.
        }
        return null;
    }

    private IEnumerable<string> CandidatePackagePaths(ImportEntry import)
    {
        var packageRoot = import.InstancedFullPath.Split('.', 2)[0];
        var fileNames = new List<string> { packageRoot + ".pcc" };
        if (packageRoot.Equals("EffectsMaterials", StringComparison.OrdinalIgnoreCase))
        {
            // LE2 seek-free packages duplicate EffectsMaterials exports rather
            // than shipping an EffectsMaterials.pcc. Prefer stable core/player
            // packages which collectively contain the Human head masters.
            fileNames.AddRange(["SFXGame.pcc", "BioP_ProCer.pcc", "BioP_OmgHub.pcc"]);
        }
        if (packageRoot.Equals("EngineMaterials", StringComparison.OrdinalIgnoreCase))
        {
            fileNames.Add("Engine.pcc");
        }
        if (import.InstancedFullPath.Contains("HMF_", StringComparison.OrdinalIgnoreCase))
        {
            fileNames.Add("BIOG_HMF_HED_PROMorph_R.pcc");
        }
        if (import.InstancedFullPath.Contains("HMM_", StringComparison.OrdinalIgnoreCase))
        {
            fileNames.Add("BIOG_HMM_HED_PROMorph.pcc");
        }
        if (import.InstancedFullPath.Contains("HMN_HED", StringComparison.OrdinalIgnoreCase))
        {
            // LE3's shared Human lash/hair users are seek-free duplicates in
            // both sex-specific head packages rather than SFXGame.pcc.
            fileNames.Add("BIOG_HMM_HED_PROMorph.pcc");
            fileNames.Add("BIOG_HMF_HED_PROMorph_R.pcc");
        }

        var directories = new[]
        {
            Path.GetDirectoryName(import.FileRef.FilePath),
            LegendaryExplorerCoreRuntime.GetCookedPath(import.FileRef.Game)
        };
        var official = _installedAssets.GetPackageFiles(import.Game);
        return fileNames.Where(official.ContainsKey).Select(fileName => official[fileName]).Concat(directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .SelectMany(directory => fileNames.Select(fileName => Path.GetFullPath(Path.Combine(directory!, fileName)))))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
