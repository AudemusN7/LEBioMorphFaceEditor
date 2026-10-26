using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Services;

/// <summary>
/// Performs one compacting full collection after sustained, genuine background idleness
/// when the managed heap has substantial spare committed capacity.
/// </summary>
internal sealed class IdleMemoryCleanup : IDisposable
{
    private const long MinimumCommittedBytes = 2L * 1024 * 1024 * 1024;
    private const long MinimumReclaimableBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan RequiredIdlePeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CollectionCooldown = TimeSpan.FromMinutes(2);
    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly PackageReferenceService _packageReferences;
    private readonly DispatcherTimer _poll;
    private long _lastInputTimestamp;
    private long _workIdleSince;
    private long _backgroundSince;
    private long _lastCollectionTimestamp;
    private int _inputGeneration;
    private int _disposedFlag;
    private bool _wasForeground;
    private bool _attemptedThisIdlePeriod;
    private bool _running;
    private bool _disposed;

    internal IdleMemoryCleanup(
        Window window,
        MainWindowViewModel viewModel,
        PackageReferenceService packageReferences)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _packageReferences = packageReferences ?? throw new ArgumentNullException(nameof(packageReferences));

        var now = Stopwatch.GetTimestamp();
        _lastInputTimestamp = now;
        _workIdleSince = now;
        _wasForeground = IsCurrentProcessForeground();
        _backgroundSince = _wasForeground ? 0 : now;

        _poll = new DispatcherTimer(DispatcherPriority.ApplicationIdle, window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _poll.Tick += OnPoll;
        InputManager.Current.PreProcessInput += OnInput;
        _window.Activated += OnWindowActivated;
        _window.Closed += OnWindowClosed;
        _poll.Start();
        AppLog.Information($"MEMORY:IDLE-CLEANUP-READY BACKGROUNDSEC:{RequiredIdlePeriod.TotalSeconds:F0} " +
            $"COOLDOWNSEC:{CollectionCooldown.TotalSeconds:F0} MINCOMMITTED:{MinimumCommittedBytes} MINSPARE:{MinimumReclaimableBytes}");
    }

    internal static bool HasReclaimableCapacity(long committedBytes, long managedBytes)
    {
        if (committedBytes < MinimumCommittedBytes) return false;
        return committedBytes - managedBytes >= MinimumReclaimableBytes;
    }

    private async void OnPoll(object? sender, EventArgs args)
    {
        if (_disposed || _running) return;

        var now = Stopwatch.GetTimestamp();
        var foreground = IsCurrentProcessForeground();
        if (foreground)
        {
            if (!_wasForeground) ResetForForeground(now);
            _wasForeground = true;
            _backgroundSince = 0;
            return;
        }

        if (_wasForeground)
        {
            _wasForeground = false;
            _backgroundSince = now;
        }
        else if (_backgroundSince == 0)
        {
            _backgroundSince = now;
        }

        var editor = _viewModel.Editor;
        if (!_viewModel.HasWorkspace || _viewModel.IsBusy || editor is null || editor.IsRandomising ||
            _packageReferences.IsReading)
        {
            ResetWorkQuietClock(now);
            return;
        }

        var historyWrites = editor.WaitForTextureHistoryWritesAsync();
        if (!historyWrites.IsCompletedSuccessfully)
        {
            ResetWorkQuietClock(now);
            return;
        }

        // Observe busy/archive work on every poll, including while the quiet
        // period is still counting down. Otherwise a late load could look idle.
        if (Stopwatch.GetElapsedTime(_backgroundSince, now) < RequiredIdlePeriod ||
            Stopwatch.GetElapsedTime(_lastInputTimestamp, now) < RequiredIdlePeriod ||
            Stopwatch.GetElapsedTime(_workIdleSince, now) < RequiredIdlePeriod ||
            _attemptedThisIdlePeriod ||
            (_lastCollectionTimestamp != 0 &&
             Stopwatch.GetElapsedTime(_lastCollectionTimestamp, now) < CollectionCooldown))
            return;

        var memoryInfo = GC.GetGCMemoryInfo();
        var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        if (!HasReclaimableCapacity(memoryInfo.TotalCommittedBytes, managedBytes))
            return;

        var lease = _packageReferences.TryAcquireIdleLease();
        if (lease is null)
        {
            ResetWorkQuietClock(now);
            return;
        }

        var inputGeneration = Volatile.Read(ref _inputGeneration);
        if (!IsReadyToCollect(editor, inputGeneration, now))
        {
            lease.Dispose();
            ResetWorkQuietClock(Stopwatch.GetTimestamp());
            return;
        }

        _running = true;
        _attemptedThisIdlePeriod = true;
        try
        {
            var result = await Task.Run(() => CollectIfStillIdle(lease, inputGeneration));
            if (result is { } measurement)
            {
                _lastCollectionTimestamp = Stopwatch.GetTimestamp();
                AppLog.Information(
                    $"MEMORY:IDLE-CLEANUP WALLMS:{measurement.Duration.TotalMilliseconds:F2} " +
                    $"GCPAUSEMS:{measurement.FullBlockingPause.TotalMilliseconds:F2} " +
                    $"MANAGED:{measurement.ManagedBefore}->{measurement.ManagedAfter} " +
                    $"COMMITTED:{measurement.CommittedBefore}->{measurement.CommittedAfter} " +
                    $"RESIDENT:{measurement.ResidentBefore}->{measurement.ResidentAfter}");
            }
            else if (IsCurrentProcessForeground())
            {
                // The background check can race a window activation after the
                // UI-thread checks but before the worker starts the collection.
                ResetForForeground(Stopwatch.GetTimestamp());
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("MEMORY:IDLE-CLEANUP-FAILED", exception);
        }
        finally
        {
            // The worker normally releases this before completing. This is an
            // idempotent safety net for failures before the worker starts.
            lease.Dispose();
            _running = false;
        }
    }

    private bool IsReadyToCollect(FaceEditorViewModel editor, int inputGeneration, long now) =>
        !_disposed && !_viewModel.IsBusy && _viewModel.HasWorkspace &&
        ReferenceEquals(editor, _viewModel.Editor) && !editor.IsRandomising &&
        editor.WaitForTextureHistoryWritesAsync().IsCompletedSuccessfully &&
        Volatile.Read(ref _inputGeneration) == inputGeneration &&
        _backgroundSince != 0 && Stopwatch.GetElapsedTime(_backgroundSince, now) >= RequiredIdlePeriod &&
        Stopwatch.GetElapsedTime(_lastInputTimestamp, now) >= RequiredIdlePeriod &&
        Stopwatch.GetElapsedTime(_workIdleSince, now) >= RequiredIdlePeriod &&
        !IsCurrentProcessForeground();

    private CollectionMeasurement? CollectIfStillIdle(IDisposable lease, int inputGeneration)
    {
        try
        {
            if (Volatile.Read(ref _disposedFlag) != 0 ||
                Volatile.Read(ref _inputGeneration) != inputGeneration ||
                IsCurrentProcessForeground())
                return null;

            var before = CaptureMemorySnapshot();
            // A natural collection may have already reclaimed the spare
            // capacity while this worker was being scheduled.
            if (!HasReclaimableCapacity(before.CommittedBytes, before.ManagedBytes)) return null;
            // Last guard immediately before the blocking operation. UI input
            // and foreground changes advance the generation or fail this check.
            if (Volatile.Read(ref _disposedFlag) != 0 ||
                Volatile.Read(ref _inputGeneration) != inputGeneration ||
                IsCurrentProcessForeground())
                return null;

            var stopwatch = Stopwatch.StartNew();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            stopwatch.Stop();

            var blockingInfo = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var pause = TimeSpan.Zero;
            foreach (var duration in blockingInfo.PauseDurations) pause += duration;
            var after = CaptureMemorySnapshot();
            return new CollectionMeasurement(
                stopwatch.Elapsed,
                pause,
                before.ManagedBytes,
                after.ManagedBytes,
                before.CommittedBytes,
                after.CommittedBytes,
                before.ResidentBytes,
                after.ResidentBytes);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static MemorySnapshot CaptureMemorySnapshot()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var info = GC.GetGCMemoryInfo();
        return new MemorySnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            info.TotalCommittedBytes,
            process.WorkingSet64);
    }

    private void OnInput(object sender, PreProcessInputEventArgs args)
    {
        if (args.StagingItem.Input is not (KeyboardEventArgs or MouseEventArgs or StylusEventArgs or TouchEventArgs)) return;
        var now = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _inputGeneration);
        _lastInputTimestamp = now;
        _workIdleSince = now;
        _attemptedThisIdlePeriod = false;
    }

    private void ResetForForeground(long now)
    {
        Interlocked.Increment(ref _inputGeneration);
        _lastInputTimestamp = now;
        _workIdleSince = now;
        _attemptedThisIdlePeriod = false;
    }

    private void ResetWorkQuietClock(long now) => _workIdleSince = now;

    private void OnWindowActivated(object? sender, EventArgs args)
    {
        if (!IsCurrentProcessForeground()) return;
        var now = Stopwatch.GetTimestamp();
        ResetForForeground(now);
        _wasForeground = true;
        _backgroundSince = 0;
    }

    private static bool IsCurrentProcessForeground()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private void OnWindowClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _disposedFlag, 1);
        _poll.Stop();
        _poll.Tick -= OnPoll;
        InputManager.Current.PreProcessInput -= OnInput;
        _window.Activated -= OnWindowActivated;
        _window.Closed -= OnWindowClosed;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private sealed record MemorySnapshot(long ManagedBytes, long CommittedBytes, long ResidentBytes);

    private sealed record CollectionMeasurement(
        TimeSpan Duration,
        TimeSpan FullBlockingPause,
        long ManagedBefore,
        long ManagedAfter,
        long CommittedBefore,
        long CommittedAfter,
        long ResidentBefore,
        long ResidentAfter);
}
