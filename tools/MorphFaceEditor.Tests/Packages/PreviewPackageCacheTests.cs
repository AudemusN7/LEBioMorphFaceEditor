using System.Reflection;
using System.Runtime.CompilerServices;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class PreviewPackageCacheTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("PCC byte budget evicts cold inputs after the read and retains hot packages", EvictsColdPackages),
        new("one oversized PCC stays reusable instead of repeatedly reopening", OversizedPackageStaysReusable),
        new("reader eviction releases PCCs while preserving geometry, pixels and source refresh", ReaderEvictionPreservesDetachedData)
    ];

    private static void EvictsColdPackages() => WithPackages((paths) =>
    {
        using var cache = new PreviewPackageCache(5 * 1024 * 1024);
        var first = cache.GetCachedPackage(paths[0]);
        var cold = LoadWeak(cache, paths[1]);
        using (cache.BeginOperation())
        {
            _ = cache.GetCachedPackage(paths[2]);
            TestAssert.True(ReferenceEquals(first, cache.GetCachedPackage(paths[0])), "A hot PCC was reopened.");
            TestAssert.Equal(3, cache.Cache.Count);
            TestAssert.True(cache.Statistics.EstimatedBytes > cache.Statistics.BudgetBytes,
                "The test did not exceed the byte budget during its protected read.");
            TestAssert.Equal(3L, cache.Statistics.Misses);
        }
        TestAssert.True(cache.CacheContains(paths[0]) && cache.CacheContains(paths[2]) && !cache.CacheContains(paths[1]),
            "Trimming did not retain the two most recently used PCCs.");
        TestAssert.True(cache.Statistics.EstimatedBytes <= cache.Statistics.BudgetBytes,
            "Cached uncompressed data remains above the byte budget.");
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        TestAssert.True(!cold.IsAlive, "The cache's tracking data still pins the evicted PCC.");
        var misses = cache.Statistics.Misses;
        _ = cache.GetCachedPackage(paths[1]);
        TestAssert.Equal(misses + 1, cache.Statistics.Misses);
        TestAssert.True(cache.CacheContains(paths[1]) && !cache.CacheContains(paths[2]),
            "Reopening a cold PCC did not update recency or evict the next cold input.");
    });

    private static void OversizedPackageStaysReusable() => WithPackages((paths) =>
    {
        using var cache = new PreviewPackageCache(1024 * 1024);
        var first = cache.GetCachedPackage(paths[0]);
        TestAssert.True(cache.Statistics.EstimatedBytes > cache.Statistics.BudgetBytes,
            "The oversized fixture did not exceed the budget.");
        TestAssert.True(ReferenceEquals(first, cache.GetCachedPackage(paths[0])), "The active oversized PCC was reopened.");
        TestAssert.Equal(1L, cache.Statistics.Misses);
        _ = cache.GetCachedPackage(paths[1]);
        TestAssert.True(cache.Cache.Count == 1 && cache.CacheContains(paths[1]),
            "An oversized PCC retained unrelated cold packages.");
    });

    private static void ReaderEvictionPreservesDetachedData()
    {
        var source = Path.GetFullPath(Path.Combine("fixtures", "Global Morphs", "LE1 GlobalMorphs.pcc"));
        var fingerprint = PackageFingerprint.Capture(source);
        var root = Path.Combine(Path.GetTempPath(), $"MFE-ReaderEviction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var reader = new MorphFacePackageReader();
            var loaded = reader.Load(source, "HMM.BIOA_PRC2_HMM_Ahern");
            var cache = (PackageCache)typeof(MorphFacePackageReader).GetField("_packageCache",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
            var packages = CapturePackages(cache);
            foreach (var path in cache.Cache.Keys.ToArray()) cache.ReleasePackage(path);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            TestAssert.True(packages.Length > 1 && packages.All(reference => !reference.IsAlive),
                "Cached mesh/material entries still pin PCCs after eviction.");
            var reloaded = reader.Load(source, "HMM.BIOA_PRC2_HMM_Ahern");
            TestAssert.True(ReferenceEquals(loaded.BaseHead, reloaded.BaseHead),
                "Evicting raw PCCs discarded reusable parsed geometry.");

            var texture = loaded.Materials.Materials.Values.SelectMany(material => material.Textures.Values)
                .Select(binding => binding.Texture).Where(value => File.Exists(value.Source.PackagePath))
                .OrderBy(value => value.Rgba8.Length).First();
            var copy = Path.Combine(root, Path.GetFileName(texture.Source.PackagePath));
            File.Copy(texture.Source.PackagePath, copy);
            var definition = new MaterialParameterDefinition("Preview", "Preview", "Preview", MaterialParameterKind.Texture,
                HeadMaterialFamily.Unknown, TextureRole: texture.Role, ColorSpace: texture.ColorSpace, AlphaPolicy: texture.AlphaPolicy);
            var decoded = reader.LoadTexture(copy, texture.Source.InstancedPath, definition);
            reader.ClearCaches(preserveDecodedTextures: true);
            var misses = reader.PackageCacheStatistics.Misses;
            var reused = reader.LoadTexture(copy, decoded.Source.InstancedPath, definition);
            TestAssert.True(ReferenceEquals(decoded.Rgba8, reused.Rgba8) && decoded.Source == reused.Source,
                "A decoded texture hit did not preserve pixels and canonical identity.");
            TestAssert.Equal(misses, reader.PackageCacheStatistics.Misses);
            using (var edited = MEPackageHandler.OpenMEPackage(copy, forceLoadFromDisk: true))
            {
                ExportSelector.Find(edited, texture.Source.InstancedPath, "Texture2D")
                    .WriteProperty(new IntProperty(1, "MFE_SourceChange"));
                edited.Save();
            }
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddSeconds(2));
            var refreshed = reader.LoadTexture(copy, decoded.Source.InstancedPath, definition);
            TestAssert.True(!ReferenceEquals(decoded.Rgba8, refreshed.Rgba8) && decoded.CacheKey != refreshed.CacheKey,
                "A changed source PCC reused stale cached pixels after package eviction.");
            TestAssert.Equal(fingerprint, PackageFingerprint.Capture(source));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadWeak(PreviewPackageCache cache, string path) => new(cache.GetCachedPackage(path));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CapturePackages(PackageCache cache) =>
        cache.Cache.Values.Select(package => new WeakReference(package)).ToArray();

    private static void WithPackages(Action<string[]> action)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var root = Path.Combine(Path.GetTempPath(), $"MFE-PccCache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = Enumerable.Range(0, 3).Select(index => Path.Combine(root, $"Donor{index}.pcc")).ToArray();
            foreach (var path in paths)
            {
                MEPackageHandler.CreateAndSavePackage(path, MEGame.LE1);
                using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
                package.CreateExport("Payload", "Object", indexed: false).Data = new byte[2 * 1024 * 1024];
                package.Save();
            }
            action(paths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
