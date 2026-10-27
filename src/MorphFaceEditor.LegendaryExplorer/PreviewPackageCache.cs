using System.Diagnostics;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Bounds the reader's retained PCC payloads without interrupting an in-flight asset read.</summary>
internal sealed class PreviewPackageCache : PackageCache
{
    // Several large human donor PCCs need to stay hot together; a smaller
    // budget repeatedly decompresses them and increases allocation pressure.
    internal const long DefaultMaximumBytes = 1536L * 1024 * 1024;
    private readonly Dictionary<string, long> _packageBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _maximumBytes;
    private long _hits;
    private long _misses;
    private long _evictions;
    private TimeSpan _openTime;
    private int _operationDepth;
    private string? _latestPath;

    internal PreviewPackageCache(long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _maximumBytes = maximumBytes;
        CacheMaxSize = 16;
    }

    internal IDisposable BeginOperation()
    {
        lock (syncObj) _operationDepth++;
        return new Operation(this);
    }

    internal PccCacheStatistics Statistics
    {
        get
        {
            lock (syncObj)
                return new(Cache.Count, RetainedBytes(), _maximumBytes, _hits, _misses, _evictions, _openTime);
        }
    }

    public override IMEPackage GetCachedPackage(string packagePath, bool openIfNotInCache = true,
        Func<string, IMEPackage>? openPackageMethod = null)
    {
        lock (syncObj)
        {
            var hit = packagePath is not null && Cache.ContainsKey(packagePath);
            var started = Stopwatch.GetTimestamp();
            var package = base.GetCachedPackage(packagePath!, openIfNotInCache, openPackageMethod);
            if (package is null) return null!;
            if (hit) _hits++;
            else
            {
                _misses++;
                _openTime += Stopwatch.GetElapsedTime(started);
            }
            _latestPath = package.FilePath;
            return package;
        }
    }

    public override void InsertIntoCache(IMEPackage package)
    {
        lock (syncObj)
        {
            _packageBytes[package.FilePath] = EstimateBytes(package);
            _latestPath = package.FilePath;
            base.InsertIntoCache(package);
        }
    }

    public override void CheckCacheFullness(bool gcOnRelease = false, bool largeGc = false)
    {
        lock (syncObj)
        {
            // A face/material read can revisit its dependency packages many times.
            // Finish that read before trimming, so a large graph is opened once.
            if (_operationDepth > 0) return;
            foreach (var stale in _packageBytes.Keys.Where(path => !Cache.ContainsKey(path)).ToArray())
                _packageBytes.Remove(stale);
            var bytes = RetainedBytes();
            foreach (var candidate in Cache.Keys
                         .Where(path => !path.Equals(_latestPath, StringComparison.OrdinalIgnoreCase) && !IsResidentPackage(path))
                         .OrderBy(path => LastAccessMap.TryGetValue(path, out var lastUse) ? lastUse : DateTime.MinValue).ToArray())
            {
                if (bytes <= _maximumBytes && (CacheMaxSize <= 0 || Cache.Count <= CacheMaxSize)) break;
                bytes -= _packageBytes.GetValueOrDefault(candidate);
                ReleasePackage(candidate);
                _evictions++;
            }
            // One oversized current package (plus explicitly resident dependencies)
            // may exceed the budget. Keep it reusable rather than reopening it on
            // every texture read. Eviction never forces a GC during editing.
        }
    }

    public override void ReleasePackage(string packagePath)
    {
        lock (syncObj)
        {
            _packageBytes.Remove(packagePath);
            base.ReleasePackage(packagePath);
        }
    }

    public override void ReleasePackages(bool gc = false)
    {
        lock (syncObj)
        {
            _packageBytes.Clear();
            _latestPath = null;
            base.ReleasePackages(gc);
        }
    }

    private long RetainedBytes() => _packageBytes.Where(pair => Cache.ContainsKey(pair.Key)).Sum(pair => pair.Value);

    internal static long EstimateBytes(IMEPackage package) =>
        // Use uncompressed export sizes, not the often much smaller PCC on disk.
        // Include a conservative allowance for entry/name metadata without reading
        // ExportEntry.Data, which would copy its potentially enormous byte arrays.
        package.Exports.Sum(export => (long)export.DataSize + 256) + package.Imports.Count * 128L +
        package.Names.Sum(name => 64L + name.Length * 2L) + 1024;

    private void EndOperation()
    {
        lock (syncObj)
        {
            _operationDepth--;
            CheckCacheFullness();
        }
    }

    private sealed class Operation(PreviewPackageCache owner) : IDisposable
    {
        private PreviewPackageCache? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndOperation();
    }
}

/// <summary>Cumulative read-cache diagnostics; estimated bytes include uncompressed exports and metadata.</summary>
public sealed record PccCacheStatistics(int Packages, long EstimatedBytes, long BudgetBytes,
    long Hits, long Misses, long Evictions, TimeSpan PackageOpenTime);
