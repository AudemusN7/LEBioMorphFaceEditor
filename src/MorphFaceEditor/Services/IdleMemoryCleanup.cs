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
    private long _quietSince;
    private long _backgroundSince;
    private long _lastCollectionTimestamp;
    private int _inputGeneration;
    private bool _attemptedThisIdlePeriod;
    private bool _running;
    private volatile bool _disposed;

    internal IdleMemoryCleanup(
        Window window,
        MainWindowViewModel viewModel,
        PackageReferenceService packageReferences)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _packageReferences = packageReferences ?? throw new ArgumentNullException(nameof(packageReferences));

        var now = Stopwatch.GetTimestamp();
        _quietSince = now;
        _backgroundSince = IsCurrentProcessForeground() ? 0 : now;

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

    internal static bool HasReclaimableCapacity(long committedBytes, long managedBytes) =>
        committedBytes >= MinimumCommittedBytes && committedBytes - managedBytes >= MinimumReclaimableBytes;

    private async void OnPoll(object? sender, EventArgs args)
    {
        if (_disposed || _running) return;

        var now = Stopwatch.GetTimestamp();
        if (IsCurrentProcessForeground())
        {
            if (_backgroundSince != 0) RecordActivity(now);
            _backgroundSince = 0;
            return;
        }

        if (_backgroundSince == 0) _backgroundSince = now;

        var editor = _viewModel.Editor;
        if (!_viewModel.HasWorkspace || _viewModel.IsBusy || editor is null || editor.IsRandomising ||
            _packageReferences.IsReading || !editor.WaitForTextureHistoryWritesAsync().IsCompletedSuccessfully)
        {
            _quietSince = now;
            return;
        }

        // Observe busy/archive work on every poll, including while the quiet
        // period is still counting down. Otherwise a late load could look idle.
        if (Stopwatch.GetElapsedTime(_backgroundSince, now) < RequiredIdlePeriod ||
            Stopwatch.GetElapsedTime(_quietSince, now) < RequiredIdlePeriod ||
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
            _quietSince = now;
            return;
        }

        // No dispatcher yield separates the state checks above. The worker
        // rechecks input, disposal and process focus after it is scheduled.
        var inputGeneration = Volatile.Read(ref _inputGeneration);
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
                    $"MANAGED:{measurement.Before.ManagedBytes}->{measurement.After.ManagedBytes} " +
                    $"COMMITTED:{measurement.Before.CommittedBytes}->{measurement.After.CommittedBytes} " +
                    $"RESIDENT:{measurement.Before.ResidentBytes}->{measurement.After.ResidentBytes}");
            }
            else if (IsCurrentProcessForeground())
            {
                // The background check can race a window activation after the
                // UI-thread checks but before the worker starts the collection.
                RecordActivity(Stopwatch.GetTimestamp());
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

    private bool CollectionInterrupted(int inputGeneration) =>
        _disposed || Volatile.Read(ref _inputGeneration) != inputGeneration || IsCurrentProcessForeground();

    private CollectionMeasurement? CollectIfStillIdle(IDisposable lease, int inputGeneration)
    {
        // Release on the worker: a UI reader must never wait for a lease whose
        // release is queued behind that same UI thread.
        using (lease)
        {
            if (CollectionInterrupted(inputGeneration)) return null;

            var before = CaptureMemorySnapshot();
            // A natural collection may have already reclaimed the spare
            // capacity while this worker was being scheduled.
            if (!HasReclaimableCapacity(before.CommittedBytes, before.ManagedBytes)) return null;
            // Last guard immediately before the blocking operation. UI input
            // and foreground changes advance the generation or fail this check.
            if (CollectionInterrupted(inputGeneration)) return null;

            var stopwatch = Stopwatch.StartNew();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            stopwatch.Stop();

            var blockingInfo = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var pause = TimeSpan.Zero;
            foreach (var duration in blockingInfo.PauseDurations) pause += duration;
            return new CollectionMeasurement(stopwatch.Elapsed, pause, before, CaptureMemorySnapshot());
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
        RecordActivity(Stopwatch.GetTimestamp());
    }

    private void RecordActivity(long now)
    {
        Interlocked.Increment(ref _inputGeneration);
        _quietSince = now;
        _attemptedThisIdlePeriod = false;
    }

    private void OnWindowActivated(object? sender, EventArgs args)
    {
        if (!IsCurrentProcessForeground()) return;
        RecordActivity(Stopwatch.GetTimestamp());
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
        MemorySnapshot Before,
        MemorySnapshot After);
}
