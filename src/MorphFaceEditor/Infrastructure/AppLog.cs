using System.IO;
using System.Text;

namespace MorphFaceEditor.Infrastructure;

public static class AppLog
{
    /// <summary>In-process diagnostics for automation; observers cannot affect editor operations.</summary>
    public static event Action<string, string>? MessageWritten;
    private static readonly object Sync = new();
    private static bool _fileWritesStopped;
    private static int _fileWriteSuppressionCount;
    private static readonly string SessionId = Guid.NewGuid().ToString("N")[..8];
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LE BioMorphFace Editor",
        "Logs");
    public static string FilePath { get; } = Path.Combine(
        DirectoryPath,
        $"morph-face-editor-{DateTime.Now:yyyyMMdd}.log");

    public static void Information(string message) => Write("INFO", message, null);
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

    private static void Write(string level, string message, Exception? exception)
    {
        if (MessageWritten is { } observers)
            foreach (Action<string, string> observer in observers.GetInvocationList())
                try { observer(level, message); } catch { }
        try
        {
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [").Append(SessionId).Append("] ")
                .Append(level).Append(' ').Append(message);
            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }
            builder.AppendLine();
            lock (Sync)
            {
                if (_fileWritesStopped || _fileWriteSuppressionCount > 0) return;
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(FilePath, builder.ToString(), Encoding.UTF8);
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
