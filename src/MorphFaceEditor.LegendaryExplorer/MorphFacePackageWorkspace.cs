using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Owns an AppData working PCC for one editor session. Package-level edits
/// affect only the working copy until Commit installs a verified copy over the
/// source package; disposal discards anything left uncommitted.
/// </summary>
public sealed class MorphFacePackageWorkspace : IDisposable
{
    private PackageFingerprint _sourceFingerprint;
    private bool _disposed;

    public MorphFacePackageWorkspace(string sourcePackagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePackagePath);
        SourcePath = Path.GetFullPath(sourcePackagePath);
        if (!File.Exists(SourcePath))
        {
            throw new FileNotFoundException("The source PCC does not exist.", SourcePath);
        }

        _sourceFingerprint = PackageFingerprint.Capture(SourcePath);
        var workspaceDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LE BioMorphFace Editor",
            "Workspaces");
        Directory.CreateDirectory(workspaceDirectory);
        WorkingPath = Path.Combine(
            workspaceDirectory,
            $".{Path.GetFileNameWithoutExtension(SourcePath)}.{Guid.NewGuid():N}.workspace.pcc");
        File.Copy(SourcePath, WorkingPath, overwrite: false);
    }

    public string SourcePath { get; }
    public string WorkingPath { get; }

    public void Commit()
    {
        ThrowIfDisposed();
        if (PackageFingerprint.Capture(SourcePath) != _sourceFingerprint)
        {
            throw new IOException(
                "The source PCC changed outside the editor while temporary edits were pending. Reload it before saving.");
        }

        LegendaryExplorerCoreRuntime.Initialize();
        using (var package = MEPackageHandler.OpenMEPackage(WorkingPath, forceLoadFromDisk: true))
        {
            if (package.Game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3))
            {
                throw new InvalidDataException($"The temporary workspace contains an unsupported {package.Game} package.");
            }
        }

        var commitPath = Path.Combine(
            Path.GetDirectoryName(SourcePath)!,
            $".{Path.GetFileName(SourcePath)}.{Guid.NewGuid():N}.commit.tmp");
        try
        {
            File.Copy(WorkingPath, commitPath, overwrite: false);
            AtomicReplace(commitPath, SourcePath);
            _sourceFingerprint = PackageFingerprint.Capture(SourcePath);
        }
        finally
        {
            if (File.Exists(commitPath))
            {
                File.Delete(commitPath);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (File.Exists(WorkingPath))
        {
            File.Delete(WorkingPath);
        }
        _disposed = true;
    }

    private static void AtomicReplace(string temporaryPath, string destination)
    {
        var backup = $"{destination}.{Guid.NewGuid():N}.backup";
        try
        {
            File.Replace(temporaryPath, destination, backup, ignoreMetadataErrors: true);
            File.Delete(backup);
        }
        catch
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, destination, overwrite: true);
                File.Delete(backup);
            }
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
