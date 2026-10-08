using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>A snapshot of one actual MIC comparison that caused the authored instance to differ from its stock copy.</summary>
internal sealed record MaterialInstanceComparisonDiagnostic(
    AssetIdentity Source,
    MorphFaceGame SourceGame,
    AssetIdentity Official,
    MorphFaceGame OfficialGame,
    IReadOnlyList<MaterialInstancePropertyDifference> Differences);

/// <summary>A normalized top-level property whose value differs between an authored MIC and its selected official copy.</summary>
internal sealed record MaterialInstancePropertyDifference(
    string PropertyName,
    string SourceValue,
    string OfficialValue);

/// <summary>Async-flow-scoped opt-in observer used by bounded diagnostics such as QA.</summary>
internal static class MaterialInstanceComparisonObserver
{
    private static readonly AsyncLocal<ObserverFrame?> CurrentFrame = new();
    private static readonly AsyncLocal<int> SuppressionDepth = new();

    internal static bool IsActive => SuppressionDepth.Value == 0 && CurrentFrame.Value is not null;

    internal static IDisposable Observe(Action<MaterialInstanceComparisonDiagnostic> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var frame = new ObserverFrame(callback, CurrentFrame.Value);
        CurrentFrame.Value = frame;
        return new ObserverScope(frame);
    }

    internal static IDisposable Suppress()
    {
        SuppressionDepth.Value++;
        return new SuppressionScope();
    }

    internal static void Publish(MaterialInstanceComparisonDiagnostic diagnostic)
    {
        if (SuppressionDepth.Value != 0 || CurrentFrame.Value is not { } frame) return;

        // A diagnostic collector must never change the package rename decision.
        try
        {
            frame.Callback(diagnostic);
        }
        catch
        {
            // Deliberately isolate observer failures from production policy.
        }
    }

    private sealed record ObserverFrame(
        Action<MaterialInstanceComparisonDiagnostic> Callback,
        ObserverFrame? Parent);

    private sealed class ObserverScope(ObserverFrame frame) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(CurrentFrame.Value, frame))
            {
                CurrentFrame.Value = frame.Parent;
            }
        }
    }

    private sealed class SuppressionScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SuppressionDepth.Value = Math.Max(0, SuppressionDepth.Value - 1);
        }
    }
}
