using System.Numerics;
using System.Runtime.CompilerServices;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class TextureHistoryTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("cold texture history restores every pixel, mip and identity without retaining old arrays", ColdSnapshotIsLossless),
        new("25 material texture edits and reset retain complete atomic Undo and Redo", FullHistoryRoundTrip),
        new("unavailable texture archive falls back to exact in-memory history", ArchiveFailurePreservesHistory),
        new("released texture history automatically deletes its temporary archive", ArchiveLifetimeFollowsHistory)
    ];

    private static void ColdSnapshotIsLossless()
    {
        var store = new TextureHistoryStore(hotBudget: 0);
        var captured = CaptureFixture(store, 41);
        store.WhenWritesComplete.GetAwaiter().GetResult();
        Collect();
        TestAssert.True(!captured.Pixels.IsAlive, "History still strongly retains a cold image's original pixels.");
        var restored = captured.Snapshot.Restore();
        AssertImage(restored, 41);
        TestAssert.True(ReferenceEquals(restored.Rgba8, restored.Mips![0].Rgba8),
            "The aliased top mip was restored as a duplicate large buffer.");
        TestAssert.Equal(256 * 256 * 4 + 128 * 128 * 4 + 64 * 64 * 4, (int)store.Statistics.ArchivedBytes);
        TestAssert.True(ReferenceEquals(restored, captured.Snapshot.Restore()), "Immediate replay discarded a live decoded image.");
        TestAssert.True(ReferenceEquals(captured.Snapshot, store.Capture(restored)), "Replaying an image duplicated its stored snapshot.");
    }

    private static void FullHistoryRoundTrip()
    {
        var store = new TextureHistoryStore(hotBudget: 128 * 1024);
        var session = CreateSession();
        session.History = new EditorHistory(store);
        var original = session.GetSelectedTexture("HED_Diff")!.CacheKey;
        var oldPixels = new List<WeakReference>();
        for (var index = 1; index <= 25; index++) oldPixels.Add(ApplyEdit(session, index));
        session.ResetToDefaults();
        store.WhenWritesComplete.GetAwaiter().GetResult();
        Collect();
        TestAssert.True(oldPixels.Take(20).All(reference => !reference.IsAlive),
            "Old material history still pins decoded images beyond its hot budget.");
        TestAssert.True(store.Statistics.HotBytes <= 128 * 1024, "History exceeded its hot pixel budget.");
        session.Undo(); // Restore the 25th edit from before Reset.
        AssertEdit(session, 25);
        for (var index = 24; index >= 0; index--)
        {
            session.Undo();
            if (index > 0) AssertEdit(session, index);
            else TestAssert.Equal(original, session.GetSelectedTexture("HED_Diff")!.CacheKey);
        }
        TestAssert.True(!session.CanUndo && session.CanRedo, "Texture history lost or added an Undo step.");
        for (var index = 1; index <= 25; index++)
        {
            session.Redo();
            AssertEdit(session, index);
        }
        session.Redo();
        TestAssert.True(session.GetSelectedTexture("HED_Diff") is null,
            "Reset replay did not restore the explicit inheritance selection.");
        TestAssert.Equal(original, session.Materials.Materials.Values.First().Textures["HED_Diff"].Texture.CacheKey);
        TestAssert.True(!session.CanRedo, "Reset replay left an extra Redo step.");
        TestAssert.True(store.Statistics.ArchiveReads > 0, "The history round trip did not exercise cold archived images.");
    }

    private static void ArchiveFailurePreservesHistory()
    {
        var store = new TextureHistoryStore(0, () => throw new UnauthorizedAccessException("Simulated unavailable storage"));
        var captured = CaptureFixture(store, 57);
        store.WhenWritesComplete.GetAwaiter().GetResult();
        Collect();
        AssertImage(captured.Snapshot.Restore(), 57);
        TestAssert.True(store.Statistics.FallbackBytes > 0 && store.Statistics.StorageFailures > 0,
            "Failed archive writes did not retain a safe exact fallback.");
    }

    private static void ArchiveLifetimeFollowsHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-HistoryLifetime-{Guid.NewGuid():N}.tmp");
        var snapshot = CreateArchiveLifetime(path);
        TestAssert.True(File.Exists(path), "The history archive was not created.");
        snapshot.Snapshot = null;
        Collect();
        TestAssert.True(!File.Exists(path), "A released history retained its temporary archive.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SnapshotOwner CreateArchiveLifetime(string path)
    {
        var store = new TextureHistoryStore(0, () => File.OpenHandle(path, FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose | FileOptions.RandomAccess));
        var captured = CaptureFixture(store, 77);
        store.WhenWritesComplete.GetAwaiter().GetResult();
        return new SnapshotOwner { Snapshot = captured.Snapshot };
    }

    private sealed class SnapshotOwner { internal TextureHistoryStore.HistoryTextureSnapshot? Snapshot; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (TextureHistoryStore.HistoryTextureSnapshot Snapshot, WeakReference Pixels) CaptureFixture(TextureHistoryStore store, int seed)
    {
        var texture = Image(seed);
        return (store.Capture(texture), new WeakReference(texture.Rgba8));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ApplyEdit(MaterialEditingSession session, int index)
    {
        var image = Image(index);
        session.SetValues(new Dictionary<string, float> { ["HED_Norm_Blend"] = index / 25f },
            new Dictionary<string, Vector4> { ["SkinTone"] = new(index / 25f, 0.3f, 0.7f, 1) },
            new Dictionary<string, DecodedTextureAsset?> { ["HED_Diff"] = image });
        return new WeakReference(image.Rgba8);
    }

    private static void AssertEdit(MaterialEditingSession session, int index)
    {
        TestAssert.Near(index / 25f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
        TestAssert.Equal(new Vector4(index / 25f, 0.3f, 0.7f, 1), session.GetVector("SkinTone"));
        AssertImage(session.GetSelectedTexture("HED_Diff")!, index);
    }

    private static DecodedTextureAsset Image(int seed)
    {
        var top = Pixels(256 * 256 * 4, seed);
        return new(TestFixtures.CreateIdentity($"History{seed}", "Texture2D") with { IsExplicitTextureDonor = true },
            256, 256, top, "PF_BC7", TextureRole.Normal, TextureColorSpace.Linear,
            TextureAlphaPolicy.Translucency, true, $"history-{seed}", 1024, 1024,
            [new(256, 256, top), new(128, 128, Pixels(128 * 128 * 4, seed + 1)),
             new(64, 64, Pixels(64 * 64 * 4, seed + 2))]);
    }

    private static void AssertImage(DecodedTextureAsset actual, int seed)
    {
        var expected = Image(seed);
        var empty = Array.Empty<byte>();
        TestAssert.Equal(expected with { Rgba8 = empty, Mips = null }, actual with { Rgba8 = empty, Mips = null });
        TestAssert.True(actual.Rgba8.AsSpan().SequenceEqual(expected.Rgba8), "Restored texture pixels changed.");
        TestAssert.Equal(expected.Mips!.Count, actual.Mips!.Count);
        for (var index = 0; index < actual.Mips.Count; index++)
        {
            TestAssert.True(actual.Mips[index].Width == expected.Mips[index].Width &&
                            actual.Mips[index].Height == expected.Mips[index].Height &&
                            actual.Mips[index].Rgba8.AsSpan().SequenceEqual(expected.Mips[index].Rgba8),
                "Restored mip dimensions or pixel bytes changed.");
        }
    }

    private static byte[] Pixels(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
    }
}
