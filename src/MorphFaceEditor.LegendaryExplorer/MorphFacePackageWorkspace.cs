using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Domain;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

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
    private readonly FileStream _lease;

    private static string WorkspaceDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LE BioMorphFace Editor", "Workspaces");

    public MorphFacePackageWorkspace(string sourcePackagePath, bool canCommit = true)
        : this(sourcePackagePath, canCommit, WorkspaceDirectory) { }

    internal MorphFacePackageWorkspace(string sourcePackagePath, bool canCommit, string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePackagePath);
        SourcePath = Path.GetFullPath(sourcePackagePath);
        if (!File.Exists(SourcePath))
        {
            throw new FileNotFoundException("The source PCC does not exist.", SourcePath);
        }

        _sourceFingerprint = PackageFingerprint.Capture(SourcePath);
        Directory.CreateDirectory(workspaceDirectory);
        // LEC derives BioWare's import/load tier from the filename prefix (for
        // example, BioD_ files may resolve LE2 post-load startup imports).
        WorkingPath = Path.Combine(
            workspaceDirectory,
            $"{Path.GetFileNameWithoutExtension(SourcePath)}.{Guid.NewGuid():N}.workspace.pcc");
        using var gate = LockDirectory(workspaceDirectory);
        _lease = new FileStream(WorkingPath + ".lease", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try { File.Copy(SourcePath, WorkingPath, overwrite: false); }
        catch
        {
            _lease.Dispose();
            File.Delete(WorkingPath + ".lease");
            throw;
        }
        CanCommit = canCommit;
    }

    public static IReadOnlyList<string> CleanupOrphanedFiles()
    {
        // Older builds have no leases. Leave their files alone while another editor may own them.
        Process[] others = [];
        try
        {
            others = Process.GetProcessesByName("MorphFaceEditor");
            return CleanupOrphanedFiles(WorkspaceDirectory, others.Any(process => process.Id != Environment.ProcessId));
        }
        catch (Exception exception) { return [$"REASON:{exception.Message}"]; }
        finally { foreach (var process in others) process.Dispose(); }
    }

    internal static IReadOnlyList<string> CleanupOrphanedFiles(string directory, bool preserveUnleased = false)
    {
        var failures = new List<string>();
        try
        {
            if (!Directory.Exists(directory) || IsLink(directory)) return failures;
            using var gate = LockDirectory(directory);
            var packages = Directory.EnumerateFiles(directory, "*.workspace.pcc").ToArray();
            var leases = Directory.EnumerateFiles(directory, "*.workspace.pcc.lease").ToArray();
            foreach (var path in packages.Concat(leases.Select(path => path[..^6])).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var leasePath = path + ".lease";
                try
                {
                    if (File.Exists(path) && IsLink(path) || File.Exists(leasePath) && IsLink(leasePath)) continue;
                    if (preserveUnleased && !File.Exists(leasePath)) continue;
                    FileStream lease;
                    try { lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
                    { continue; } // A live workspace owns this lock, even if LEC has closed the PCC itself.
                    using (lease) { if (File.Exists(path)) File.Delete(path); }
                    File.Delete(leasePath);
                }
                catch (Exception exception) { failures.Add($"FILE:{Path.GetFileName(path)} REASON:{exception.Message}"); }
            }
        }
        catch (Exception exception) { failures.Add($"REASON:{exception.Message}"); }
        return failures;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static IDisposable LockDirectory(string directory)
    {
        var name = "Local\\MFE-Workspaces-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())));
        var mutex = new Mutex(false, name);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(2))) throw new IOException("Workspace directory busy."); }
            catch (AbandonedMutexException) { }
            return new DirectoryLock(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class DirectoryLock(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    public string SourcePath { get; private set; }
    public string WorkingPath { get; }
    public bool CanCommit { get; }

    public void Commit() => Commit(null);

    public void Commit(string? destination)
    {
        ThrowIfDisposed();
        var target = Path.GetFullPath(destination ?? SourcePath);
        if (target.Equals(WorkingPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The working package cannot be the save destination.");
        var savingSource = target.Equals(SourcePath, StringComparison.OrdinalIgnoreCase);
        var targetFingerprint = File.Exists(target) ? PackageFingerprint.Capture(target) : (PackageFingerprint?)null;
        if (!CanCommit)
        {
            throw new InvalidOperationException(
                "This detached workspace is read-only with respect to its installed seed and cannot be committed.");
        }
        if (savingSource)
        {
            FileSaveTransaction.VerifyUnchanged(SourcePath, _sourceFingerprint,
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

        using (var save = new FileSaveTransaction(target, targetFingerprint, ".commit.tmp"))
        {
            File.Copy(WorkingPath, save.CandidatePath, overwrite: false);
            save.Commit("The destination PCC changed while saving.");
            SourcePath = target;
            _sourceFingerprint = PackageFingerprint.Capture(SourcePath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        using var gate = LockDirectory(Path.GetDirectoryName(WorkingPath)!);
        try
        {
            if (File.Exists(WorkingPath)) File.Delete(WorkingPath);
        }
        finally
        {
            _lease.Dispose();
            _disposed = true;
            File.Delete(WorkingPath + ".lease");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
