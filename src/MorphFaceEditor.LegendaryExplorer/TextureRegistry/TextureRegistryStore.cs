using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Reads and atomically replaces MFE's versioned Brotli-compressed registry files.</summary>
public sealed class TextureRegistryStore(TextureRegistryPaths paths)
{
    public const uint Magic = 0x5254464D;
    private const long MaximumJsonBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new InstalledAssetOccurrenceListConverter() }
    };

    public TextureRegistryStatus GetStatus(MorphFaceGame game) => ReadWithStatus(game).Status;
    public TextureRegistryStatus GetManualStatus(MorphFaceGame game)
    {
        var status = ReadManualWithStatus(game).Status;
        return status.State == TextureRegistryState.Ready && status.ErrorMessage is not null
            ? status with { ErrorMessage = $"{status.ErrorMessage} {GetManualReportPath(game)}" }
            : status;
    }

    internal TextureRegistryReadResult ReadWithStatus(MorphFaceGame game) =>
        ReadWithStatus(game, paths.GetPath(game));

    internal TextureRegistryReadResult ReadManualWithStatus(MorphFaceGame game) =>
        ReadWithStatus(game, paths.GetManualPath(game), allowLegacyManual: true);

    private static TextureRegistryReadResult ReadWithStatus(MorphFaceGame game, string path, bool allowLegacyManual = false)
    {
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
                if (version != TextureRegistrySnapshot.CurrentSchemaVersion && !(allowLegacyManual && version is 10 or 11 or 12))
                {
                    var outdatedFile = new FileInfo(path);
                    return new TextureRegistryReadResult(
                        new TextureRegistryStatus(game, TextureRegistryState.Outdated, path,
                            outdatedFile.Length, outdatedFile.LastWriteTimeUtc, null,
                            $"Registry schema v{version} is not supported by this editor version."),
                        null);
                }
            }

            var snapshot = ReadFile(path, game, allowLegacyManual);
            var file = new FileInfo(path);
            var missing = snapshot.ManualAssets.Count(asset => asset.IsMissing);
            return new TextureRegistryReadResult(
                new TextureRegistryStatus(game, TextureRegistryState.Ready, path,
                    file.Length, snapshot.BuiltAtUtc, snapshot.Candidates.Count,
                    missing > 0
                        ? $"{missing} custom asset(s) could not be found. See the manual asset error log."
                        : null),
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
    public TextureRegistrySnapshot ReadManual(MorphFaceGame game) => ReadFile(paths.GetManualPath(game), game, allowLegacyManual: true);
    public bool HasManual(MorphFaceGame game) => File.Exists(paths.GetManualPath(game));
    public string GetManualReportPath(MorphFaceGame game) =>
        Path.ChangeExtension(paths.GetManualPath(game), ".missing.log");

    public void DeleteManual(MorphFaceGame game)
    {
        var path = paths.GetManualPath(game);
        if (File.Exists(path)) File.Delete(path);
        var report = GetManualReportPath(game);
        if (File.Exists(report)) File.Delete(report);
    }

    internal (string Path, long Length, DateTime LastWriteTimeUtc)? GetFileFingerprint(MorphFaceGame game)
    {
        var path = paths.GetPath(game);
        var file = new FileInfo(path);
        return file.Exists ? (path, file.Length, file.LastWriteTimeUtc) : null;
    }

    internal (string Path, long Length, DateTime LastWriteTimeUtc)? GetManualFileFingerprint(MorphFaceGame game)
    {
        var path = paths.GetManualPath(game);
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
        Action? beforeVerification) =>
        WriteAtomic(snapshot, paths.GetPath(ToMorphFaceGame(snapshot.Game)), cancellationToken, beforeVerification);

    public void WriteManualAtomic(TextureRegistrySnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        WriteAtomic(snapshot, paths.GetManualPath(ToMorphFaceGame(snapshot.Game)), cancellationToken, null);

    private static void WriteAtomic(
        TextureRegistrySnapshot snapshot,
        string targetPath,
        CancellationToken cancellationToken,
        Action? beforeVerification)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var game = ToMorphFaceGame(snapshot.Game);
        Validate(snapshot, game);
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
                verified.Assets.Count != snapshot.Assets.Count ||
                verified.AttachmentMeshes.Count != snapshot.AttachmentMeshes.Count ||
                verified.ManualAssets.Count != snapshot.ManualAssets.Count ||
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

    private static TextureRegistrySnapshot ReadFile(string path, MorphFaceGame expectedGame, bool allowLegacyManual = false)
    {
        using var input = File.OpenRead(path);
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        if (input.Length < HeaderSize || reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("The texture registry header is malformed.");
        }
        var schemaVersion = reader.ReadInt32();
        if (schemaVersion != TextureRegistrySnapshot.CurrentSchemaVersion && !(allowLegacyManual && schemaVersion is 10 or 11 or 12))
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
        if (allowLegacyManual && schemaVersion is 10 or 11 or 12 && snapshot.SchemaVersion == schemaVersion)
            snapshot = snapshot with { SchemaVersion = TextureRegistrySnapshot.CurrentSchemaVersion };
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
            snapshot.MorphFaceTemplates is null || snapshot.AttachmentMeshes is null ||
            snapshot.ManualAssets is null || snapshot.Assets is null || snapshot.OfficialPackagePaths is null ||
            snapshot.MountedPackagePaths is null || snapshot.MountedPackages is null)
        {
            throw new InvalidDataException("The texture registry payload is incomplete.");
        }
        foreach (var asset in snapshot.Assets)
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.PackagePath) || asset.ExportUIndex <= 0 ||
                string.IsNullOrWhiteSpace(asset.InstancedPath) || !InstalledAssetOccurrence.IsSupportedClass(asset.ClassName) ||
                !Enum.IsDefined(asset.Origin))
                throw new InvalidDataException("The registry contains an incomplete installed asset identity.");
        }
        if (snapshot.OfficialPackagePaths.Any(string.IsNullOrWhiteSpace) ||
            snapshot.MountedPackagePaths.Any(string.IsNullOrWhiteSpace) ||
            snapshot.MountedPackages.Any(package => package is null || string.IsNullOrWhiteSpace(package.PackagePath) || package.Length < 0))
            throw new InvalidDataException("The registry contains an incomplete installed package index.");
        var mountedPaths = snapshot.MountedPackagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (snapshot.OfficialPackagePaths.Any(path => !mountedPaths.Contains(path)))
            throw new InvalidDataException("An official package winner is absent from the physical package index.");
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
        foreach (var mesh in snapshot.AttachmentMeshes)
        {
            if (mesh is null || string.IsNullOrWhiteSpace(mesh.CanonicalPath) ||
                mesh.EffectiveOccurrence is null || mesh.Occurrences is null ||
                mesh.Occurrences.Count == 0 || mesh.Occurrences.Any(occurrence =>
                    occurrence is null || string.IsNullOrWhiteSpace(occurrence.PackagePath) ||
                    string.IsNullOrWhiteSpace(occurrence.InstancedPath) || occurrence.ExportUIndex <= 0))
                throw new InvalidDataException("The texture registry contains an incomplete attachment mesh candidate.");
        }
        foreach (var asset in snapshot.ManualAssets)
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.PackagePath) ||
                asset.ExportUIndex <= 0 || string.IsNullOrWhiteSpace(asset.InstancedPath) ||
                asset.ClassName is not ("Texture2D" or "SkeletalMesh"))
                throw new InvalidDataException("The texture registry contains an incomplete manual asset.");
        }
    }

    /// <summary>
    /// Stores the large all-occurrence identity index as package metadata plus grouped
    /// path/class locations. Legacy manual sidecars used a flat array and remain readable.
    /// </summary>
    private sealed class InstalledAssetOccurrenceListConverter : JsonConverter<IReadOnlyList<InstalledAssetOccurrence>>
    {
        public override IReadOnlyList<InstalledAssetOccurrence> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var legacy = new List<InstalledAssetOccurrence>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    legacy.Add(JsonSerializer.Deserialize<InstalledAssetOccurrence>(ref reader, options)
                        ?? throw new JsonException("The installed asset identity is null."));
                if (reader.TokenType != JsonTokenType.EndArray)
                    throw new JsonException("The legacy installed asset array is incomplete.");
                return legacy;
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("packages", out var packagesElement) ||
                !root.TryGetProperty("assets", out var assetsElement) ||
                packagesElement.ValueKind != JsonValueKind.Array || assetsElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("The compact installed asset index is malformed.");

            var packages = new List<PackageEntry>();
            foreach (var package in packagesElement.EnumerateArray())
            {
                if (package.ValueKind != JsonValueKind.Array || package.GetArrayLength() != 3)
                    throw new JsonException("The compact installed asset package entry is malformed.");
                var fields = package.EnumerateArray().ToArray();
                var path = fields[0].GetString();
                if (string.IsNullOrWhiteSpace(path))
                    throw new JsonException("The compact installed asset package path is empty.");
                packages.Add(new PackageEntry(path, fields[1].GetInt32(), (TextureCatalogOrigin)fields[2].GetInt32()));
            }

            var result = new List<InstalledAssetOccurrence>();
            foreach (var asset in assetsElement.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Array || asset.GetArrayLength() != 3)
                    throw new JsonException("The compact installed asset group is malformed.");
                var fields = asset.EnumerateArray().ToArray();
                var instancedPath = fields[0].GetString();
                var className = fields[1].GetString();
                if (string.IsNullOrWhiteSpace(instancedPath) || string.IsNullOrWhiteSpace(className) ||
                    fields[2].ValueKind != JsonValueKind.Array)
                    throw new JsonException("The compact installed asset group is incomplete.");
                foreach (var location in fields[2].EnumerateArray())
                {
                    if (location.ValueKind != JsonValueKind.Array || location.GetArrayLength() != 2)
                        throw new JsonException("The compact installed asset location is malformed.");
                    var parts = location.EnumerateArray().ToArray();
                    var packageIndex = parts[0].GetInt32();
                    var exportUIndex = parts[1].GetInt32();
                    if ((uint)packageIndex >= packages.Count)
                        throw new JsonException("The compact installed asset references an invalid package index.");
                    var package = packages[packageIndex];
                    result.Add(new InstalledAssetOccurrence(instancedPath, className, package.Path,
                        exportUIndex, package.MountPriority, package.Origin));
                }
            }
            return result;
        }

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlyList<InstalledAssetOccurrence> value,
            JsonSerializerOptions options)
        {
            var packages = new List<PackageEntry>();
            var packageIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var groups = new List<AssetGroup>();
            var groupIndexes = new Dictionary<(string Path, string ClassName), int>();
            foreach (var occurrence in value)
            {
                if (!packageIndexes.TryGetValue(occurrence.PackagePath, out var packageIndex))
                {
                    packageIndex = packages.Count;
                    packageIndexes.Add(occurrence.PackagePath, packageIndex);
                    packages.Add(new PackageEntry(occurrence.PackagePath, occurrence.MountPriority, occurrence.Origin));
                }
                else
                {
                    var package = packages[packageIndex];
                    if (package.MountPriority != occurrence.MountPriority || package.Origin != occurrence.Origin)
                        throw new JsonException("One installed package has conflicting mount metadata.");
                }

                var key = (occurrence.InstancedPath, occurrence.ClassName);
                if (!groupIndexes.TryGetValue(key, out var groupIndex))
                {
                    groupIndex = groups.Count;
                    groupIndexes.Add(key, groupIndex);
                    groups.Add(new AssetGroup(occurrence.InstancedPath, occurrence.ClassName));
                }
                groups[groupIndex].Locations.Add((packageIndex, occurrence.ExportUIndex));
            }

            writer.WriteStartObject();
            writer.WritePropertyName("packages");
            writer.WriteStartArray();
            foreach (var package in packages)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(package.Path);
                writer.WriteNumberValue(package.MountPriority);
                writer.WriteNumberValue((int)package.Origin);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("assets");
            writer.WriteStartArray();
            foreach (var group in groups)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(group.InstancedPath);
                writer.WriteStringValue(group.ClassName);
                writer.WriteStartArray();
                foreach (var (packageIndex, exportUIndex) in group.Locations)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(packageIndex);
                    writer.WriteNumberValue(exportUIndex);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private sealed record PackageEntry(string Path, int MountPriority, TextureCatalogOrigin Origin);

        private sealed class AssetGroup(string instancedPath, string className)
        {
            public string InstancedPath { get; } = instancedPath;
            public string ClassName { get; } = className;
            public List<(int PackageIndex, int ExportUIndex)> Locations { get; } = [];
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
