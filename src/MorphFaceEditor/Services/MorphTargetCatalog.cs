using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

public sealed class MorphTargetCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IReadOnlyList<MorphTargetAsset>> _targets =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MorphTargetAsset> Load(
        MorphFaceProfile profile,
        MorphFaceGame game = MorphFaceGame.LE1,
        string? sourcePackagePath = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            if (profile.TargetSetName is not null)
            {
                var bundleKey = $"{game}.{profile.TargetSetName}";
                if (_targets.TryGetValue(bundleKey, out var bundledCached))
                {
                    return bundledCached;
                }

                var bundled = LoadBundle(bundleKey);
                if (bundled is not null)
                {
                    _targets[bundleKey] = bundled;
                    return bundled;
                }
            }

            // Retained for non-default/custom profiles which do not have a bundled catalogue.
            if (!string.IsNullOrWhiteSpace(sourcePackagePath) && profile.TargetSetName is not null)
            {
                var sourcePath = Path.GetFullPath(sourcePackagePath);
                var sourceKey = $"{game}|{sourcePath}|{profile.TargetSetName}";
                if (_targets.TryGetValue(sourceKey, out var sourceCached))
                {
                    return sourceCached;
                }
                try
                {
                    var embedded = MorphTargetPackageReader.LoadSet(sourcePath, profile.TargetSetName);
                    _targets[sourceKey] = embedded;
                    return embedded;
                }
                catch (KeyNotFoundException)
                {
                    // Custom profiles may reference a standalone target package instead.
                }
            }

            var targetPackagePath = ResolveTargetPackage(profile, game, sourcePackagePath);
            var cacheKey = $"{game}|{targetPackagePath}|{profile.TargetSetName}";
            if (_targets.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var loaded = profile.TargetSetName is null
                ? MorphTargetPackageReader.LoadAll(targetPackagePath)
                : MorphTargetPackageReader.LoadSet(targetPackagePath, profile.TargetSetName);
            _targets[cacheKey] = loaded;
            return loaded;
        }
    }

    private static IReadOnlyList<MorphTargetAsset>? LoadBundle(string bundleKey)
    {
        var resourceName = $"MorphFaceEditor.Assets.MorphTargets.{bundleKey}.mft.br";
        using var stream = typeof(MorphTargetCatalog).Assembly.GetManifestResourceStream(resourceName);
        return stream is null ? null : MorphTargetBundleSerializer.Read(stream, bundleKey);
    }

    private static string ResolveTargetPackage(
        MorphFaceProfile profile,
        MorphFaceGame game,
        string? sourcePackagePath)
    {
        if (string.IsNullOrWhiteSpace(profile.TargetPackageName))
        {
            throw new FileNotFoundException(
                $"{profile.DisplayName} has no bundled or standalone source for MorphTargetSet '{profile.TargetSetName}'.",
                sourcePackagePath);
        }
        var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game)
            ?? throw new DirectoryNotFoundException($"The {game} CookedPCConsole directory was not discovered.");
        return Path.Combine(cookedPath, profile.TargetPackageName);
    }
}
