using System.IO;
using System.IO.Compression;
using System.Text.Json;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Services;

internal sealed record DiagnosticArchiveResult(string Path, IReadOnlyList<string> Entries, IReadOnlyList<string> Warnings);

/// <summary>Publishes an archive only after every requested file has been copied successfully.</summary>
internal sealed class DiagnosticArchiveService(ApplicationDataPaths paths, string logPath, DateTimeOffset? now = null)
{
    public DiagnosticArchiveResult Export(string destination, IReadOnlyList<string> workspaceFiles,
        IReadOnlyList<string>? warnings = null)
    {
        destination = Path.GetFullPath(destination);
        if (!Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.Text("loc_Services_DiagnosticArchive_ChooseZip"));
        warnings ??= [];
        var files = new List<(string Source, string Entry)>();
        if (!File.Exists(logPath))
            throw new FileNotFoundException(Loc.Text("loc_Services_DiagnosticArchive_LogUnavailable"), logPath);
        files.Add((logPath, "Logs/" + Path.GetFileName(logPath)));
        var logDirectory = Path.GetDirectoryName(Path.GetFullPath(logPath))!;
        var today = (now ?? DateTimeOffset.Now).Date;
        files.AddRange(Directory.EnumerateFiles(logDirectory, "morph-face-editor-*.log")
            .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(logPath), StringComparison.OrdinalIgnoreCase) &&
                AppLogFileStore.TryFileDate(path, out var date) && date >= today.AddDays(-2) && date <= today)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => (path, "Logs/" + Path.GetFileName(path))));
        files.AddRange(workspaceFiles.Select(path => (path, "Workspace/" + Path.GetFileName(path))));
        foreach (var root in new[] { paths.CurrentDirectory, paths.LegacyDirectory })
        {
            var registries = Path.Combine(root, "TextureRegistries");
            if (!Directory.Exists(registries)) continue;
            var prefix = root == paths.CurrentDirectory ? "TextureRegistries/" : "LegacyTextureRegistries/";
            files.AddRange(Directory.EnumerateFiles(registries, "*.mftr", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => (path, prefix + Path.GetFileName(path))));
        }
        if (files.Any(file => Path.GetFullPath(file.Source).Equals(destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException(Loc.Text("loc_Services_DiagnosticArchive_DestinationIsInput"));

        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".mfe-diagnostics-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var (source, entryName) in files)
                {
                    using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    // Snapshot the current length so a busy logger cannot prolong the copy indefinitely.
                    var remaining = sourceStream.Length;
                    using var targetStream = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                    var buffer = new byte[81920];
                    while (remaining > 0)
                    {
                        var read = sourceStream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read == 0) throw new IOException(Loc.Format("loc_Services_DiagnosticArchive_SourceChanged", Path.GetFileName(source)));
                        targetStream.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
                using var manifest = archive.CreateEntry("Report.json").Open();
                JsonSerializer.Serialize(manifest, new
                {
                    ApplicationVersion = typeof(App).Assembly.GetName().Version?.ToString(),
                    CreatedUtc = DateTimeOffset.UtcNow,
                    WorkspaceIncluded = workspaceFiles.Count > 0,
                    Warnings = warnings,
                    Files = files.Select(file => file.Entry).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            File.Move(temporary, destination, overwrite: true);
            return new DiagnosticArchiveResult(destination, files.Select(file => file.Entry).ToArray(), warnings);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
