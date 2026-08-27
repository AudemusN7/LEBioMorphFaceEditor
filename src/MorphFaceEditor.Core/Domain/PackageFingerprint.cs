using System.Security.Cryptography;

namespace MorphFaceEditor.Core.Domain;

public sealed record PackageFingerprint(long Size, DateTime LastWriteTimeUtc, string Sha256)
{
    public static PackageFingerprint Capture(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPath = Path.GetFullPath(packagePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Package file was not found.", fullPath);
        }

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new PackageFingerprint(info.Length, info.LastWriteTimeUtc, hash);
    }
}
