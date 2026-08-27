using System.IO;
using System.Text;

namespace MorphFaceEditor.Infrastructure;

public static class AppLog
{
    private static readonly object Sync = new();
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

    private static void Write(string level, string message, Exception? exception)
    {
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
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(FilePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become another failure path.
        }
    }
}
