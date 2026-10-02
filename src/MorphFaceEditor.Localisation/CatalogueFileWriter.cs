using System.Security.Cryptography;

namespace MorphFaceEditor.Localisation;

internal sealed record CatalogueWrite(string Path, byte[] Bytes, byte[]? ExpectedHash);

internal static class CatalogueFileWriter
{
    internal static byte[]? Fingerprint(string path) => File.Exists(path) ? SHA256.HashData(File.ReadAllBytes(path)) : null;

    internal static void Write(params CatalogueWrite[] writes)
    {
        var plans = new List<(CatalogueWrite Write, string Candidate, byte[]? Original)>();
        var installed = new List<(CatalogueWrite Write, byte[]? Original)>();
        try
        {
            foreach (var write in writes)
            {
                Verify(write);
                var original = File.Exists(write.Path) ? File.ReadAllBytes(write.Path) : null;
                var candidate = write.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                plans.Add((write, candidate, original));
                using var file = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(write.Bytes); file.Flush(flushToDisk: true);
            }
            foreach (var (write, candidate, original) in plans)
            {
                Verify(write);
                if (original is null) File.Move(candidate, write.Path);
                else File.Replace(candidate, write.Path, write.Path + ".localiser.bak");
                installed.Add((write, original));
            }
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var (write, original) in installed.AsEnumerable().Reverse())
            {
                try
                {
                    // Never replace an intervening external edit during rollback.
                    if (Fingerprint(write.Path) is not { } current || !current.SequenceEqual(SHA256.HashData(write.Bytes)))
                        throw new IOException($"File changed before rollback: {write.Path}");
                    if (original is null) File.Delete(write.Path);
                    else
                    {
                        var rollback = write.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try { File.WriteAllBytes(rollback, original); File.Replace(rollback, write.Path, null); }
                        finally { if (File.Exists(rollback)) File.Delete(rollback); }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { rollbackFailures.Add(exception); }
            }
            if (rollbackFailures.Count > 0)
                throw new IOException("Saving failed and a file could not be restored. Previous files remain in .localiser.bak backups.",
                    new AggregateException(new[] { failure }.Concat(rollbackFailures)));
            throw;
        }
        finally { foreach (var plan in plans) if (File.Exists(plan.Candidate)) File.Delete(plan.Candidate); }
    }

    private static void Verify(CatalogueWrite write)
    {
        var current = Fingerprint(write.Path);
        if (current is null ? write.ExpectedHash is not null : write.ExpectedHash is null || !current.SequenceEqual(write.ExpectedHash))
            throw new IOException("The localisation changed on disk after it was loaded. Load it again or save to a different file. Your draft is still in this window.");
    }
}
