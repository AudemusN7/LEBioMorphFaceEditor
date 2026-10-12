using System.IO;

namespace MorphFaceEditor.Infrastructure;

public static class AppLog
{
    /// <summary>In-process diagnostics for automation; observers cannot affect editor operations.</summary>
    public static event Action<string, string>? MessageWritten;
    private static readonly object Sync = new();
    private static bool _fileWritesStopped;
    private static int _fileWriteSuppressionCount;
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LE BioMorphFace Editor",
        "Logs");
    private static readonly AppLogFileStore Files = new(DirectoryPath);
    public static string FilePath => Files.LatestFilePath;

    public static void StartSession(IEnumerable<string> arguments, Version? version)
    {
        Files.VerboseLogging = arguments.Contains("-verboselogging", StringComparer.OrdinalIgnoreCase);
        // Write the header/start record before any startup service can report a warning.
        Information($"Application starting VERSION:{version} VERBOSE:{(Files.VerboseLogging ? "ON" : "OFF")}");
        foreach (var failure in Files.Prune()) Warning(failure);
    }

    public static void Information(string message) => Write("INFO", message, null);
    public static void Verbose(string message) => Write("INFO", message, null, verbose: true);
    public static void Warning(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    // Shutdown reset must wait for active appends and prevent background diagnostics recreating AppData.
    internal static void StopFileWrites()
    {
        lock (Sync) _fileWritesStopped = true;
    }

    /// <summary>Stops persistent log appends for a scoped automation run while keeping in-process observers active.</summary>
    public static IDisposable SuppressPersistentWrites()
    {
        lock (Sync) _fileWriteSuppressionCount++;
        return new PersistentWriteSuppression();
    }

    private static void Write(string level, string message, Exception? exception, bool verbose = false)
    {
        if (MessageWritten is { } observers)
            foreach (Action<string, string> observer in observers.GetInvocationList())
                try { observer(level, message); } catch { }
        try
        {
            lock (Sync)
            {
                if (_fileWritesStopped || _fileWriteSuppressionCount > 0) return;
                Files.Append(level, message, exception, verbose);
            }
        }
        catch
        {
            // Diagnostics must never become another failure path.
        }
    }

    private sealed class PersistentWriteSuppression : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed) return;
                _disposed = true;
                _fileWriteSuppressionCount--;
            }
        }
    }
}
