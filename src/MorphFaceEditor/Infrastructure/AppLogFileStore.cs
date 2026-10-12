using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MorphFaceEditor.Infrastructure;

/// <summary>Daily files, compact records and per-day session numbering. The named mutex also covers other MFE processes.</summary>
internal sealed class AppLogFileStore(string directory, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private string? _sessionFile;
    private readonly string _mutexName = "Local\\MFE-Logs-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())));

    public string CurrentFilePath => FilePathFor(_clock());
    // Consumers collecting diagnostics need the file actually written, even if midnight
    // has passed and no record has created the next daily file yet.
    public string LatestFilePath => _sessionFile ?? CurrentFilePath;
    public bool VerboseLogging { get; set; }

    private string FilePathFor(DateTimeOffset date) => Path.Combine(directory,
        "morph-face-editor-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    public void Append(string level, string message, Exception? exception = null, bool verbose = false)
    {
        if (verbose && !VerboseLogging) return;
        using var mutex = new Mutex(false, _mutexName);
        Enter(mutex);
        try
        {
            var now = _clock();
            var path = FilePathFor(now);
            Directory.CreateDirectory(directory);
            var text = new StringBuilder();
            if (_sessionFile != path)
            {
                var session = 0;
                if (File.Exists(path))
                    foreach (var line in File.ReadLines(path))
                        if (line.StartsWith("SESSION ", StringComparison.Ordinal) &&
                            int.TryParse(line.AsSpan(8), out var number)) session = Math.Max(session, number);
                // A separator also protects the header after a truncated/crashed final record.
                if (File.Exists(path) && new FileInfo(path).Length > 0) text.AppendLine();
                text.Append("SESSION ").Append(session + 1).AppendLine();
            }
            text.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(level switch { "INFO" => "INF", "WARN" => "WAR", _ => "ERR" })
                .Append("] ").Append(message);
            if (exception is not null) text.AppendLine().Append(exception);
            text.AppendLine();
            File.AppendAllText(path, text.ToString(), Encoding.UTF8);
            _sessionFile = path;
        }
        finally { mutex.ReleaseMutex(); }
    }

    public IReadOnlyList<string> Prune()
    {
        var failures = new List<string>();
        try
        {
            if (!Directory.Exists(directory) || IsLink(directory)) return failures;
            using var mutex = new Mutex(false, _mutexName);
            Enter(mutex);
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "morph-face-editor-*.log"))
                {
                    if (!TryFileDate(path, out _) || IsLink(path) || path == CurrentFilePath) continue;
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < _clock().UtcDateTime.AddDays(-7)) File.Delete(path);
                    }
                    catch (Exception exception) { failures.Add($"LOG:CLEANUP FILE:{Path.GetFileName(path)} REASON:{exception.Message}"); }
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception exception) { failures.Add($"LOG:CLEANUP REASON:{exception.Message}"); }
        return failures;
    }

    internal static bool TryFileDate(string path, out DateTime date)
    {
        const string prefix = "morph-face-editor-";
        var name = Path.GetFileNameWithoutExtension(path);
        date = default;
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               DateTime.TryParseExact(name[prefix.Length..], "yyyyMMdd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out date);
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void Enter(Mutex mutex)
    {
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(2))) throw new IOException("Log writer busy."); }
        catch (AbandonedMutexException) { /* The previous process crashed; this thread now owns the mutex. */ }
    }
}
