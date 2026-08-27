using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

public static class PackageAssetInspector
{
    public static PackageInventory Inventory(string packagePath, IEnumerable<string>? classNames = null)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var fullPath = RequireFile(packagePath);
        var classSet = classNames is null
            ? null
            : new HashSet<string>(classNames, StringComparer.OrdinalIgnoreCase);

        using var package = MEPackageHandler.OpenMEPackage(fullPath, forceLoadFromDisk: true);
        var entries = package.Exports
            .Where(export => classSet is null || classSet.Contains(export.ClassName))
            .Select(export => new PackageEntrySummary(
                export.UIndex,
                export.InstancedFullPath,
                export.ObjectName.Instanced,
                export.ClassName,
                export.IsDefaultObject))
            .OrderBy(entry => entry.UIndex)
            .ToArray();

        return new PackageInventory(
            fullPath,
            package.Game.ToString(),
            PackageFingerprint.Capture(fullPath),
            entries);
    }

    public static AssetDiscoveryResult Discover(
        string rootPath,
        IReadOnlyCollection<string> classNames,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(classNames);
        if (classNames.Count == 0)
        {
            throw new ArgumentException("At least one class name is required.", nameof(classNames));
        }

        var fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(fullRoot);
        }

        var packagePaths = Directory
            .EnumerateFiles(fullRoot, "*.pcc", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var classSet = new HashSet<string>(classNames, StringComparer.OrdinalIgnoreCase);
        var matches = new List<PackageInventory>();
        var failures = new List<string>();

        for (var index = 0; index < packagePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = packagePaths[index];
            progress?.Invoke(index + 1, packagePaths.Length, path);
            try
            {
                using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
                var entries = package.Exports
                    .Where(export => classSet.Contains(export.ClassName) && !export.IsDefaultObject)
                    .Select(export => new PackageEntrySummary(
                        export.UIndex,
                        export.InstancedFullPath,
                        export.ObjectName.Instanced,
                        export.ClassName,
                        export.IsDefaultObject))
                    .OrderBy(entry => entry.UIndex)
                    .ToArray();

                if (entries.Length > 0)
                {
                    matches.Add(new PackageInventory(
                        Path.GetFullPath(path),
                        package.Game.ToString(),
                        PackageFingerprint.Capture(path),
                        entries));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                failures.Add($"{path}: {exception.Message}");
            }
        }

        return new AssetDiscoveryResult(fullRoot, packagePaths.Length, failures.Count, matches, failures);
    }

    private static string RequireFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException("Package file was not found.", fullPath);
    }
}
