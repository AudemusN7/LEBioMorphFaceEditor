using System.Reflection;
using System.Runtime.CompilerServices;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
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
        new("reader eviction releases PCCs while preserving geometry, pixels and source refresh", ReaderEvictionPreservesDetachedData),
        new("batched interleaved texture reads reuse donors and isolate failures", BatchTextureReadsReuseDonors)
    ];

    private static void BatchTextureReadsReuseDonors()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var root = Path.Combine(Path.GetTempPath(), $"MFE-BatchTextures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = Enumerable.Range(0, 2).Select(index => Path.Combine(root, $"Donor{index}.pcc")).ToArray();
            foreach (var path in paths)
            {
                using var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE1);
                package.CreateExport("Payload", "Object", indexed: false).Data = new byte[2 * 1024 * 1024];
                for (var index = 0; index < 2; index++)
                {
                    var texture = package.CreateExport($"Pixels{index}", "Texture2D", indexed: false);
                    texture.WriteProperty(new EnumProperty(new NameReference("PF_A8R8G8B8"),
                        new NameReference("EPixelFormat"), MEGame.LE1, "Format"));
                    texture.WriteBinary(new UTexture2D { Mips =
                        [new UTexture2D.Texture2DMipMap([(byte)(20 + index), 30, 40, 255], 1, 1)] });
                }
                package.Save(path);
            }

            using var reader = new MorphFacePackageReader();
            var cache = (PreviewPackageCache)typeof(MorphFacePackageReader).GetField("_packageCache",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
            typeof(PreviewPackageCache).GetField("_maximumBytes", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(cache, 3L * 1024 * 1024);
            var definition = new MaterialParameterDefinition("Preview", "Preview", "Preview", MaterialParameterKind.Texture,
                HeadMaterialFamily.Unknown, TextureRole: TextureRole.Diffuse, ColorSpace: TextureColorSpace.Srgb,
                AlphaPolicy: TextureAlphaPolicy.Ignore);
            var requests = new[]
            {
                new TextureReadRequest(paths[0], "Pixels0", definition),
                new TextureReadRequest(paths[1], "Pixels0", definition),
                new TextureReadRequest(Path.Combine(root, "missing.pcc"), "Missing", definition),
                new TextureReadRequest(paths[0], "Pixels1", definition),
                new TextureReadRequest(paths[1], "Pixels1", definition)
            };
            var results = reader.LoadTextures(requests);
            TestAssert.Equal(requests.Length, results.Count);
            foreach (var index in new[] { 0, 1, 3, 4 })
                TestAssert.True(results[index].Error is null &&
                    results[index].Texture?.Source.InstancedPath ==
                        $"{Path.GetFileNameWithoutExtension(requests[index].PackagePath)}.{requests[index].TexturePath}" &&
                    Path.GetFullPath(results[index].Texture!.Source.PackagePath) == requests[index].PackagePath,
                    $"Texture request {index} failed or returned the wrong identity: {results[index].Error}; " +
                    $"actual {results[index].Texture?.Source}; expected {requests[index].PackagePath}|{requests[index].TexturePath}.");
            TestAssert.True(results[2].Texture is null && results[2].Error is not null,
                "A missing donor did not produce an isolated item error.");
            TestAssert.Equal(2L, reader.PackageCacheStatistics.Misses);
            TestAssert.True(reader.PackageCacheStatistics.EstimatedBytes <= reader.PackageCacheStatistics.BudgetBytes,
                "The batch left cold donors above the cache budget.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var threw = false;
            try { reader.LoadTextures(requests, cancelled.Token); }
            catch (OperationCanceledException) { threw = true; }
            TestAssert.True(threw, "Cancellation was returned as an item error instead of stopping the batch.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
