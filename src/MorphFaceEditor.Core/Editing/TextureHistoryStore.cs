using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Editing;

/// <summary>Lossless history images with bounded hot pixels and background temporary-file backing.</summary>
internal sealed class TextureHistoryStore
{
    internal const long DefaultHotBytes = 256L * 1024 * 1024;
    private const int InlineBytes = 4096;
    private readonly object _sync = new();
    private readonly ConditionalWeakTable<DecodedTextureAsset, HistoryTextureSnapshot> _textures = new();
    private readonly ConditionalWeakTable<byte[], PixelBlock> _blocks = new();
    private readonly Dictionary<PixelBlock, LinkedListNode<HotPixels>> _hot = [];
    private readonly LinkedList<HotPixels> _lru = [];
    private readonly Queue<PendingWrite> _pending = [];
    private readonly long _hotBudget;
    private readonly Func<SafeFileHandle> _openArchive;
    private SafeFileHandle? _archive;
    private bool _writing;
    private TaskCompletionSource? _idle;
    private bool _storageFailed;
    private long _hotBytes;
    private long _pendingBytes;
    private long _archivedBytes;
    private long _fallbackBytes;
    private long _storageFailures;
    private long _reads;
    private double _readMilliseconds;

    internal TextureHistoryStore(long hotBudget = DefaultHotBytes, Func<SafeFileHandle>? openArchive = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hotBudget);
        _hotBudget = hotBudget;
        _openArchive = openArchive ?? (() => File.OpenHandle(
            Path.Combine(Path.GetTempPath(), $"MFE-TextureHistory-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            FileOptions.DeleteOnClose | FileOptions.RandomAccess));
    }

    internal HistoryTextureSnapshot Capture(DecodedTextureAsset texture)
    {
        lock (_sync)
        {
            if (_textures.TryGetValue(texture, out var existing)) return existing;
            var top = CapturePixels(texture.Rgba8);
            var mips = texture.Mips?.Select(mip => new HistoryMip(mip.Width, mip.Height, CapturePixels(mip.Rgba8))).ToArray();
            var snapshot = new HistoryTextureSnapshot(this, texture, top, mips);
            _textures.Add(texture, snapshot);
            return snapshot;
        }
    }

    private PixelBlock CapturePixels(byte[] pixels)
    {
        if (!_blocks.TryGetValue(pixels, out var block))
        {
            block = new PixelBlock(pixels);
            _blocks.Add(pixels, block);
            if (pixels.Length <= InlineBytes) block.Inline = pixels;
            else
            {
                _pending.Enqueue(new(block, pixels));
                _pendingBytes += pixels.Length;
                if (!_writing)
                {
                    _writing = true;
                    _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    ThreadPool.QueueUserWorkItem(static owner => owner.DrainWrites(), this, preferLocal: false);
                }
            }
        }
        Touch(block, pixels);
        return block;
    }

    private void DrainWrites()
    {
        while (true)
        {
            PendingWrite work;
            lock (_sync)
            {
                if (!_pending.TryDequeue(out work!))
                {
                    _writing = false;
                    _idle!.TrySetResult();
                    return;
                }
            }
            Write(work);
            // Do not leave the last large buffer rooted in an idle worker.
            work = null!;
        }
    }

    private void Write(PendingWrite work)
    {
        try
        {
            if (_storageFailed) throw new IOException("Texture history archive is unavailable.");
            _archive ??= _openArchive();
            var offset = _archivedBytes;
            RandomAccess.Write(_archive, work.Pixels, offset);
            lock (_sync)
            {
                work.Block.Offset = offset;
                _archivedBytes += work.Pixels.Length;
            }
        }
        catch (Exception)
        {
            // Storage is an optimisation. Keep the exact image in RAM on failure;
            // disk-full/permission failures must never destroy an Undo step.
            lock (_sync)
            {
                _storageFailed = true;
                work.Block.Inline = work.Pixels;
                _fallbackBytes += work.Pixels.Length;
                _storageFailures++;
            }
        }
        finally
        {
            lock (_sync) _pendingBytes -= work.Pixels.Length;
        }
    }

    private byte[] RestorePixels(PixelBlock block)
    {
        SafeFileHandle archive;
        long offset;
        lock (_sync)
        {
            if (block.Live.TryGetTarget(out var live))
            {
                Touch(block, live);
                return live;
            }
            if (block.Inline is { } inline) return inline;
            // A pending write owns its source array, so it stays available through
            // the weak reference until the completed archive offset is published.
            archive = _archive ?? throw new InvalidDataException("Missing texture history archive.");
            offset = block.Offset;
            if (offset < 0) throw new InvalidDataException("Texture history image is not available.");
        }
        var started = Stopwatch.GetTimestamp();
        var pixels = GC.AllocateUninitializedArray<byte>(block.Length);
        var read = 0;
        while (read < pixels.Length)
        {
            var count = RandomAccess.Read(archive, pixels.AsSpan(read), offset + read);
            if (count == 0) throw new EndOfStreamException("Incomplete texture history image.");
            read += count;
        }
        lock (_sync)
        {
            block.Live.SetTarget(pixels);
            _blocks.Add(pixels, block);
            Touch(block, pixels);
            _reads++;
            _readMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return pixels;
    }

    private void Touch(PixelBlock block, byte[] pixels)
    {
        if (_hot.TryGetValue(block, out var node))
        {
            _lru.Remove(node);
            _lru.AddLast(node);
            return;
        }
        if (pixels.Length <= InlineBytes || pixels.LongLength > _hotBudget) return;
        _hot.Add(block, _lru.AddLast(new HotPixels(block, pixels)));
        _hotBytes += pixels.Length;
        while (_hotBytes > _hotBudget && _lru.First is { } oldest)
        {
            _lru.RemoveFirst();
            _hot.Remove(oldest.Value.Block);
            _hotBytes -= oldest.Value.Pixels.Length;
        }
    }

    internal TextureHistoryStatistics Statistics
    {
        get
        {
            lock (_sync) return new(_hotBytes, _pendingBytes, _archivedBytes, _fallbackBytes,
                _storageFailures, _reads, _readMilliseconds);
        }
    }

    internal Task WhenWritesComplete
    {
        get { lock (_sync) return _idle?.Task ?? Task.CompletedTask; }
    }

    // The history owns the store, and snapshots own it while they can be replayed.
    // SafeFileHandle closes/deletes the archive on collection, including after a
    // workspace closes. DeleteOnClose also cleans it up on process termination.
    private sealed record PendingWrite(PixelBlock Block, byte[] Pixels);
    private sealed record HotPixels(PixelBlock Block, byte[] Pixels);
    internal sealed class PixelBlock(byte[] pixels)
    {
        internal readonly int Length = pixels.Length;
        internal readonly WeakReference<byte[]> Live = new(pixels);
        internal byte[]? Inline;
        internal long Offset = -1;
    }
    internal sealed record HistoryMip(int Width, int Height, PixelBlock Pixels);

    internal sealed class HistoryTextureSnapshot
    {
        private readonly TextureHistoryStore _store;
        private readonly DecodedTextureAsset _metadata;
        private readonly WeakReference<DecodedTextureAsset> _live;
        private readonly PixelBlock _top;
        private readonly HistoryMip[]? _mips;

        internal HistoryTextureSnapshot(TextureHistoryStore store, DecodedTextureAsset texture, PixelBlock top, HistoryMip[]? mips)
        {
            _store = store;
            _metadata = texture with { Rgba8 = [], Mips = null };
            _live = new(texture);
            _top = top;
            _mips = mips;
        }

        internal AssetIdentity Source => _metadata.Source;

        internal DecodedTextureAsset Restore()
        {
            lock (_store._sync)
                if (_live.TryGetTarget(out var live))
                {
                    _store.Touch(_top, live.Rgba8);
                    if (_mips is not null && live.Mips is { } liveMips)
                        for (var index = 0; index < _mips.Length; index++)
                            _store.Touch(_mips[index].Pixels, liveMips[index].Rgba8);
                    return live;
                }
            var pixels = _store.RestorePixels(_top);
            var mips = _mips?.Select(mip => new DecodedTextureMip(mip.Width, mip.Height,
                _store.RestorePixels(mip.Pixels))).ToArray();
            var restored = _metadata with { Rgba8 = pixels, Mips = mips };
            lock (_store._sync)
            {
                _live.SetTarget(restored);
                _store._textures.Add(restored, this);
            }
            return restored;
        }
    }
}

public sealed record TextureHistoryStatistics(long HotBytes, long PendingBytes, long ArchivedBytes,
    long FallbackBytes, long StorageFailures, long ArchiveReads, double ArchiveReadMilliseconds);
