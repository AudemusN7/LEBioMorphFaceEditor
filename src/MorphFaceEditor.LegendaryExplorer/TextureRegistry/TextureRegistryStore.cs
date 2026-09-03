using System.IO.Compression;
using System.Text.Json;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Reads and atomically replaces MFE's versioned Brotli-compressed registry files.</summary>
public sealed class TextureRegistryStore(TextureRegistryPaths paths)
{
    public const uint Magic = 0x5254464D;
    private const long MaximumJsonBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public TextureRegistryStatus GetStatus(MorphFaceGame game) => ReadWithStatus(game).Status;

    internal TextureRegistryReadResult ReadWithStatus(MorphFaceGame game)
    {
        var path = paths.GetPath(game);
        if (!File.Exists(path))
        {
            return new TextureRegistryReadResult(
                new TextureRegistryStatus(game, TextureRegistryState.Missing,
                    null, null, null, null, null),
                null);
        }

        try
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                if (stream.Length < HeaderSize || reader.ReadUInt32() != Magic)
                {
                    throw new InvalidDataException("The texture registry header is malformed.");
                }
                var version = reader.ReadInt32();
                if (version != TextureRegistrySnapshot.CurrentSchemaVersion)
                {
                    var outdatedFile = new FileInfo(path);
                    return new TextureRegistryReadResult(
                        new TextureRegistryStatus(game, TextureRegistryState.Outdated, path,
                            outdatedFile.Length, outdatedFile.LastWriteTimeUtc, null,
                            $"Registry schema v{version} is not supported by this editor version."),
                        null);
                }
            }

            var snapshot = ReadFile(path, game);
            var file = new FileInfo(path);
            return new TextureRegistryReadResult(
                new TextureRegistryStatus(game, TextureRegistryState.Ready, path,
                    file.Length, snapshot.BuiltAtUtc, snapshot.Candidates.Count, null),
                snapshot);
        }
        catch (Exception exception)
        {
            var file = new FileInfo(path);
            return new TextureRegistryReadResult(
                new TextureRegistryStatus(game, TextureRegistryState.Failed, path,
                    file.Exists ? file.Length : null, file.Exists ? file.LastWriteTimeUtc : null,
                    null, exception.Message),
                null);
        }
    }

    public TextureRegistrySnapshot Read(MorphFaceGame game) => ReadFile(paths.GetPath(game), game);

    internal (string Path, long Length, DateTime LastWriteTimeUtc)? GetFileFingerprint(MorphFaceGame game)
    {
        var path = paths.GetPath(game);
        var file = new FileInfo(path);
        return file.Exists ? (path, file.Length, file.LastWriteTimeUtc) : null;
    }

    public void WriteAtomic(
        TextureRegistrySnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        WriteAtomic(snapshot, cancellationToken, beforeVerification: null);

    internal void WriteAtomic(
        TextureRegistrySnapshot snapshot,
        CancellationToken cancellationToken,
        Action? beforeVerification)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var game = ToMorphFaceGame(snapshot.Game);
        Validate(snapshot, game);
        var targetPath = paths.GetPath(game);
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            if (json.LongLength > MaximumJsonBytes)
            {
                throw new InvalidDataException("The texture registry payload exceeds the 256 MiB limit.");
            }

            using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    writer.Write(TextureRegistrySnapshot.CurrentSchemaVersion);
                    writer.Write((int)game);
                    writer.Write((long)json.Length);
                }
                using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    brotli.Write(json);
                }
                output.Flush(flushToDisk: true);
            }

            beforeVerification?.Invoke();
            var verified = ReadFile(temporaryPath, game);
            if (verified.SchemaVersion != snapshot.SchemaVersion ||
                verified.Game != snapshot.Game ||
                verified.InstalledPackageCount != snapshot.InstalledPackageCount ||
                verified.Candidates.Count != snapshot.Candidates.Count ||
                verified.MorphFaceTemplates.Count != snapshot.MorphFaceTemplates.Count)
            {
                throw new InvalidDataException("The written texture registry failed verification.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private static TextureRegistrySnapshot ReadFile(string path, MorphFaceGame expectedGame)
    {
        using var input = File.OpenRead(path);
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        if (input.Length < HeaderSize || reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("The texture registry header is malformed.");
        }
        var schemaVersion = reader.ReadInt32();
        if (schemaVersion != TextureRegistrySnapshot.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Texture registry schema v{schemaVersion} is not supported.");
        }
        var storedGame = (MorphFaceGame)reader.ReadInt32();
        if (storedGame != expectedGame)
        {
            throw new InvalidDataException($"The registry contains {storedGame}, not {expectedGame}.");
        }
        var jsonLength = reader.ReadInt64();
        if (jsonLength is <= 0 or > MaximumJsonBytes)
        {
            throw new InvalidDataException("The texture registry declares an invalid payload size.");
        }

        var json = new byte[checked((int)jsonLength)];
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true))
        {
            brotli.ReadExactly(json);
            if (brotli.ReadByte() != -1)
            {
                throw new InvalidDataException("The texture registry payload exceeds its declared size.");
            }
        }

        var snapshot = JsonSerializer.Deserialize<TextureRegistrySnapshot>(json, JsonOptions)
            ?? throw new InvalidDataException("The texture registry payload is empty.");
        Validate(snapshot, expectedGame);
        return snapshot;
    }

    private static void Validate(TextureRegistrySnapshot snapshot, MorphFaceGame expectedGame)
    {
        if (snapshot.SchemaVersion != TextureRegistrySnapshot.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Texture registry schema v{snapshot.SchemaVersion} is not supported.");
        }
        if (ToMorphFaceGame(snapshot.Game) != expectedGame)
        {
            throw new InvalidDataException("The registry header and payload games do not match.");
        }
        if (snapshot.InstalledPackageCount < 0 || snapshot.Candidates is null ||
            snapshot.MorphFaceTemplates is null)
        {
            throw new InvalidDataException("The texture registry payload is incomplete.");
        }
        foreach (var candidate in snapshot.Candidates)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.InstancedPath) ||
                candidate.EffectiveOccurrence is null || candidate.Occurrences is null ||
                candidate.Occurrences.Count == 0)
            {
                throw new InvalidDataException("The texture registry contains an incomplete candidate.");
            }
        }
        foreach (var template in snapshot.MorphFaceTemplates)
        {
            if (template is null || string.IsNullOrWhiteSpace(template.PackagePath) ||
                template.ExportUIndex <= 0 || string.IsNullOrWhiteSpace(template.FacePath))
            {
                throw new InvalidDataException("The texture registry contains an incomplete morph-face template.");
            }
        }
    }

    private static MorphFaceGame ToMorphFaceGame(TextureCatalogGame game) => game switch
    {
        TextureCatalogGame.LE1 => MorphFaceGame.LE1,
        TextureCatalogGame.LE2 => MorphFaceGame.LE2,
        TextureCatalogGame.LE3 => MorphFaceGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Unsupported texture registry game.")
    };

    private const int HeaderSize = sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(long);
}

internal sealed record TextureRegistryReadResult(
    TextureRegistryStatus Status,
    TextureRegistrySnapshot? Snapshot);
