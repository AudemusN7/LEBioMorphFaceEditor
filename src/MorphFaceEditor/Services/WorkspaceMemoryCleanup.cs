using System.Diagnostics;
using System.Runtime;
using System.Windows.Threading;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Services;

/// <summary>Reclaims retired editor data after its bindings and preview have been detached.</summary>
internal static class WorkspaceMemoryCleanup
{
    internal static Task ReleaseAsync(PackageReferenceService references, Task? historyWrites = null) =>
        ReleaseAsync(references, preserveDecodedTextures: false, "WORKSPACE-CLOSE", historyWrites);

    internal static Task ReleaseForArchetypeChangeAsync(PackageReferenceService references, Task? historyWrites = null) =>
        ReleaseAsync(references, preserveDecodedTextures: true, "ARCHETYPE-CHANGE", historyWrites);

    private static async Task ReleaseAsync(PackageReferenceService references, bool preserveDecodedTextures, string reason, Task? historyWrites)
    {
        var started = Stopwatch.GetTimestamp();
        var managedBefore = GC.GetTotalMemory(false);
        var committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
        try
        {
            // A pending archive write owns its source pixels. Drain it before
            // collecting the retired editor, without retaining the editor itself.
            if (historyWrites is not null) await historyWrites;
            // Let binding/unload work cancel old picker requests before draining
            // the shared reader. Registry indexes and morph-target bundles stay warm.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await references.ClearCachesAsync(preserveDecodedTextures);

            await Task.Run(static () =>
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                // WPF TextEditor finalizers can retain entire old control/editor
                // trees through this first collection. Never wait on the UI thread.
                GC.WaitForPendingFinalizers();
            });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Run(static () =>
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            });
        }
        catch (Exception exception)
        {
            // The previous editor has already retired; optional reclamation
            // must not turn that into a failed close or face load.
            AppLog.Error($"MEMORY:{reason}-CLEANUP-FAILED", exception);
        }
        finally
        {
            AppLog.Information($"MEMORY:{reason} MANAGED:{managedBefore}->{GC.GetTotalMemory(false)} " +
                $"COMMITTED:{committedBefore}->{GC.GetGCMemoryInfo().TotalCommittedBytes} " +
                $"MS:{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        }
    }
}
