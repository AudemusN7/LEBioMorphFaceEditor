using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Owns an adjacent candidate and installs it only if the destination is unchanged.
/// Callers write and verify their format before committing; disposal removes an unused candidate.
/// </summary>
internal sealed class FileSaveTransaction : IDisposable
{
    // Test-only interruption boundary after format verification and before final conflict checks/install.
    internal static Action<string, string>? BeforeCommitForTesting { get; set; }

    private readonly string _destination;
    private readonly PackageFingerprint? _expectedDestination;

    internal FileSaveTransaction(string destination, PackageFingerprint? expectedDestination, string suffix = ".tmp")
    {
        _destination = Path.GetFullPath(destination);
        _expectedDestination = expectedDestination;
        CandidatePath = Path.Combine(Path.GetDirectoryName(_destination)!,
            $".{Path.GetFileName(_destination)}.{Guid.NewGuid():N}{suffix}");
    }

    internal string CandidatePath { get; }

    internal void Commit(string conflictMessage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeCommitForTesting?.Invoke(CandidatePath, _destination);
        VerifyUnchanged(_destination, _expectedDestination, conflictMessage);
        cancellationToken.ThrowIfCancellationRequested();
        if (_expectedDestination is null)
        {
            // A new-file save must never turn into an overwrite if another process
            // creates the destination after the final conflict check.
            File.Move(CandidatePath, _destination);
            return;
        }

        var backup = $"{_destination}.{Guid.NewGuid():N}.backup";
        try
        {
            File.Replace(CandidatePath, _destination, backup, ignoreMetadataErrors: true);
            File.Delete(backup);
        }
        catch
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, _destination, overwrite: true);
                File.Delete(backup);
            }
            throw;
        }
    }

    internal static void VerifyUnchanged(string path, PackageFingerprint? expected, string conflictMessage)
    {
        if (File.Exists(path) != (expected is not null) ||
            expected is not null && PackageFingerprint.Capture(path) != expected)
            throw new IOException(conflictMessage);
    }

    public void Dispose()
    {
        if (File.Exists(CandidatePath)) File.Delete(CandidatePath);
    }
}
