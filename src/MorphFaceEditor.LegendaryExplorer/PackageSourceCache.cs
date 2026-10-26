using System.Runtime.CompilerServices;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Checks only packages requested by an operation, never the installed package set.</summary>
internal static class PackageSourceCache
{
    private static readonly ConditionalWeakTable<PackageCache, Sources> Caches = new();

    internal static void RegisterInvalidation(PackageCache cache, Action<string> invalidate) =>
        Caches.GetValue(cache, static _ => new Sources()).Invalidate = invalidate;

    internal static void Clear(PackageCache cache)
    {
        // Keep the reader's invalidation callback when its empty cache is reused.
        if (Caches.TryGetValue(cache, out var sources)) sources.Stamps.Clear();
    }

    internal static IMEPackage? GetPackage(PackageCache cache, string path)
    {
        Verify(cache, path);
        return File.Exists(path) ? cache.GetCachedPackage(path) : null;
    }

    internal static void Verify(PackageCache cache, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        var stamp = file.Exists ? (file.Length, file.LastWriteTimeUtc) : ((long, DateTime)?)null;
        var sources = Caches.GetValue(cache, static _ => new Sources());
        if (sources.Stamps.TryGetValue(fullPath, out var previous) && previous != stamp)
        {
            // Release before reopening; decoded metadata/pixels tied to this source
            // must be dropped too. Package eviction itself does not dispose live data.
            cache.ReleasePackages(value => Path.GetFullPath(value).Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            sources.Invalidate?.Invoke(fullPath);
        }
        sources.Stamps[fullPath] = stamp;
    }

    internal static string Version(PackageCache cache, string path)
    {
        var stamp = Caches.GetValue(cache, static _ => new Sources()).Stamps.GetValueOrDefault(Path.GetFullPath(path));
        return stamp is { } value ? $"{value.Length}|{value.LastWriteTimeUtc.Ticks}" : "missing";
    }

    private sealed class Sources
    {
        public readonly Dictionary<string, (long Length, DateTime LastWriteTimeUtc)?> Stamps =
            new(StringComparer.OrdinalIgnoreCase);
        public Action<string>? Invalidate;
    }
}
