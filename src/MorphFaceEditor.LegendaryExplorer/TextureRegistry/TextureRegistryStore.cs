using MorphFaceEditor.Core.Domain;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Reads and atomically replaces MFE's versioned Brotli-compressed registry files.</summary>
public sealed class TextureRegistryStore(TextureRegistryPaths paths)
{
    // Release generation uses MFT1 so development MFTR schema v1 cannot collide.
    public const uint Magic = 0x3154464D;
    public const uint DevelopmentMagic = 0x5254464D;
    private const long MaximumJsonBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new InstalledAssetOccurrenceListConverter() }
    };
    private const int MaximumCachedRegistryFiles = 12;
    private static readonly object ReadCacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CachedRead>> ReadCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<CachedRead> ReadCacheLru = new();

    public TextureRegistryStatus GetStatus(MorphFaceGame game)
    {
        var result = ReadWithStatus(game);
        return WithFreshness(result.Status, result.Snapshot);
    }
    public TextureRegistryStatus GetModsStatus(MorphFaceGame game)
    {
        var result = ReadModsWithStatus(game);
        return WithFreshness(result.Status, result.Snapshot);
    }

    public TextureRegistryStatus GetManualStatus(MorphFaceGame game)
    {
        var result = ReadManualWithStatus(game);
        var status = WithFreshness(result.Status, result.Snapshot);
        return status.State == TextureRegistryState.Ready && status.ErrorMessage is not null
            ? status with { ErrorMessage = $"{status.ErrorMessage} {GetManualReportPath(game)}" }
            : status;
    }

    /// <summary>Status of automatically scanned and manually added custom assets for Settings.</summary>
    public TextureRegistryStatus GetCustomStatus(MorphFaceGame game)
    {
        var manual = ReadManualWithStatus(game);
        var mods = ReadModsWithStatus(game);
        var manualStatus = GetManualStatus(game);
        var modsStatus = WithFreshness(mods.Status, mods.Snapshot);
        var isStale = manualStatus.IsStale || modsStatus.IsStale;
        foreach (var status in new[] { manualStatus, modsStatus })
            if (status.State is not (TextureRegistryState.Ready or TextureRegistryState.Missing))
                return status with { IsStale = isStale };

        foreach (var sidecar in new[] { manual, mods })
            if (sidecar.Snapshot is { } snapshot &&
                snapshot.Candidates.Count + snapshot.AttachmentMeshes.Count + snapshot.ManualAssets.Count > 0)
                return (sidecar == manual ? manualStatus : modsStatus) with { IsStale = isStale };

        return manualStatus with { State = TextureRegistryState.Missing, IsStale = isStale };
    }

    private static TextureRegistryStatus WithFreshness(
        TextureRegistryStatus status, TextureRegistrySnapshot? snapshot) =>
        snapshot is null ? status : status with { IsStale = IsSnapshotStale(snapshot) };

    private static bool IsSnapshotStale(TextureRegistrySnapshot snapshot)
    {
        if (snapshot.MountedPackagePaths.Count == 0 && snapshot.MountedPackages.Count == 0) return false;
        var fingerprints = snapshot.MountedPackages.GroupBy(
                value => Path.GetFullPath(value.PackagePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var representedPaths = snapshot.MountedPackagePaths.Count > 0
            ? snapshot.MountedPackagePaths
            : snapshot.MountedPackages.Select(value => value.PackagePath).ToArray();
        foreach (var path in representedPaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !fingerprints.TryGetValue(fullPath, out var fingerprint) ||
                !fingerprint.IsCurrent()) return true;
        }
        return false;
    }

    internal TextureRegistryReadResult ReadWithStatus(MorphFaceGame game) =>
        ReadWithStatus(game, paths.GetPath(game));

    internal TextureRegistryReadResult ReadModsWithStatus(MorphFaceGame game) =>
        ReadWithStatus(game, paths.GetModsPath(game));

    internal TextureRegistryReadResult ReadManualWithStatus(MorphFaceGame game) =>
        ReadWithStatus(game, paths.GetManualPath(game));

    private static TextureRegistryReadResult ReadWithStatus(MorphFaceGame game, string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (ReadCacheLock)
        {
            var file = new FileInfo(fullPath);
            var exists = file.Exists;
            var length = exists ? file.Length : -1;
            var stamp = exists ? file.LastWriteTimeUtc : DateTime.MinValue;
            if (ReadCache.TryGetValue(fullPath, out var cachedNode))
            {
                var cached = cachedNode.Value;
                if (cached.Game == game &&
                    cached.Exists == exists && cached.Length == length && cached.LastWriteTimeUtc == stamp)
                {
                    ReadCacheLru.Remove(cachedNode);
                    ReadCacheLru.AddFirst(cachedNode);
                    return cached.Result;
                }
                ReadCache.Remove(fullPath);
                ReadCacheLru.Remove(cachedNode);
            }

            var result = ReadFileWithStatus(game, fullPath, file);
            var node = ReadCacheLru.AddFirst(new CachedRead(fullPath, game,
                exists, length, stamp, result));
            ReadCache[fullPath] = node;
            while (ReadCache.Count > MaximumCachedRegistryFiles)
            {
                var last = ReadCacheLru.Last!;
                ReadCache.Remove(last.Value.Path);
                ReadCacheLru.RemoveLast();
            }
            return result;
        }
    }

    private static TextureRegistryReadResult ReadFileWithStatus(
        MorphFaceGame game, string path, FileInfo file)
    {
        if (!file.Exists)
            return new(new TextureRegistryStatus(game, TextureRegistryState.Missing, null, null, null, null, null), null);
        try
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                if (stream.Length < HeaderSize)
                {
                    throw new InvalidDataException("The texture registry header is malformed.");
                }
                var magic = reader.ReadUInt32();
                if (magic != Magic && magic != DevelopmentMagic)
                    throw new InvalidDataException("The texture registry header is malformed.");
                var version = reader.ReadInt32();
                if (magic == DevelopmentMagic || version != TextureRegistrySnapshot.CurrentSchemaVersion)
                {
                    return new TextureRegistryReadResult(
                        new TextureRegistryStatus(game, TextureRegistryState.Outdated, path,
                            file.Length, file.LastWriteTimeUtc, null,
                            $"Registry schema v{version} is not supported by this editor version.")
                        {
                            UnsupportedSchemaVersion = version,
                            IsDevelopmentSchema = magic == DevelopmentMagic
                        },
                        null);
                }
            }

            var snapshot = ReadFile(path, game);
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
            return new TextureRegistryReadResult(
                new TextureRegistryStatus(game, TextureRegistryState.Failed, path,
                    file.Exists ? file.Length : null, file.Exists ? file.LastWriteTimeUtc : null,
                    null, exception.Message),
                null);
        }
    }

    public TextureRegistrySnapshot Read(MorphFaceGame game) => ReadFile(paths.GetPath(game), game);
    /// <summary>Shared compact lookup snapshot; raw Read remains the persistence round-trip API.</summary>
    public TextureRegistrySnapshot ReadForLookup(MorphFaceGame game)
    {
        var result = ReadWithStatus(game);
        return result.Snapshot ?? throw new InvalidDataException(result.Status.ErrorMessage ??
            $"The {game} asset database is unavailable. Build it in Mesh/Texture Databases.");
    }
    public TextureRegistrySnapshot ReadMods(MorphFaceGame game) => ReadFile(paths.GetModsPath(game), game);
    public TextureRegistrySnapshot ReadManual(MorphFaceGame game) => ReadFile(paths.GetManualPath(game), game);
    public bool HasManual(MorphFaceGame game) => File.Exists(paths.GetManualPath(game));
    public string GetManualReportPath(MorphFaceGame game) =>
        Path.ChangeExtension(paths.GetManualPath(game), ".missing.log");

    public void DeleteManual(MorphFaceGame game)
    {
        var path = paths.GetManualPath(game);
        if (File.Exists(path)) File.Delete(path);
        InvalidateCachedPath(path);
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

    internal (string Path, long Length, DateTime LastWriteTimeUtc)? GetModsFileFingerprint(MorphFaceGame game)
    {
        var path = paths.GetModsPath(game);
        var file = new FileInfo(path);
        return file.Exists ? (Path.GetFullPath(path), file.Length, file.LastWriteTimeUtc) : null;
    }

    internal void WriteInstalledAndModsAtomic(
        TextureRegistrySnapshot installed,
        TextureRegistrySnapshot mods,
        CancellationToken cancellationToken = default,
        Action? beforeVerification = null)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(mods);
        var game = installed.Game;
        if (game != mods.Game) throw new InvalidDataException("Registry pair games do not match.");
        Validate(installed, game);
        Validate(mods, game);
        cancellationToken.ThrowIfCancellationRequested();
        var installedPath = paths.GetPath(game);
        var modsPath = paths.GetModsPath(game);
        Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
        var installedTemp = $"{installedPath}.{Guid.NewGuid():N}.tmp";
        var modsTemp = $"{modsPath}.{Guid.NewGuid():N}.tmp";
        var installedBackup = $"{installedPath}.{Guid.NewGuid():N}.bak";
        var modsBackup = $"{modsPath}.{Guid.NewGuid():N}.bak";
        var installedMoved = false;
        var modsMoved = false;
        var pairSettled = true;
        try
        {
            WriteTemporary(installed, installedTemp);
            WriteTemporary(mods, modsTemp);
            beforeVerification?.Invoke();
            VerifyTemporary(installed, installedTemp, game);
            VerifyTemporary(mods, modsTemp, game);
            cancellationToken.ThrowIfCancellationRequested();

            pairSettled = false;
            if (File.Exists(installedPath)) File.Move(installedPath, installedBackup);
            if (File.Exists(modsPath)) File.Move(modsPath, modsBackup);
            File.Move(installedTemp, installedPath);
            installedMoved = true;
            File.Move(modsTemp, modsPath);
            modsMoved = true;
            pairSettled = true;
            InvalidateCachedPath(installedPath);
            InvalidateCachedPath(modsPath);
        }
        catch (Exception writeFailure)
        {
            if (!pairSettled)
            {
                List<Exception>? rollbackFailures = null;
                void AttemptRollback(Action action)
                {
                    try { action(); }
                    catch (Exception rollbackFailure) { (rollbackFailures ??= []).Add(rollbackFailure); }
                }
                if (installedMoved && File.Exists(installedPath)) AttemptRollback(() => File.Delete(installedPath));
                if (modsMoved && File.Exists(modsPath)) AttemptRollback(() => File.Delete(modsPath));
                if (File.Exists(installedBackup)) AttemptRollback(() => File.Move(installedBackup, installedPath, overwrite: true));
                if (File.Exists(modsBackup)) AttemptRollback(() => File.Move(modsBackup, modsPath, overwrite: true));
                pairSettled = rollbackFailures is null;
                InvalidateCachedPath(installedPath);
                InvalidateCachedPath(modsPath);
                if (!pairSettled)
                    throw new AggregateException(
                        "Registry pair publication failed and rollback was incomplete; backup files were retained.",
                        [writeFailure, .. rollbackFailures!]);
            }
            throw;
        }
        finally
        {
            foreach (var temporary in new[] { installedTemp, modsTemp })
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); } catch { }
                }
            if (pairSettled)
                foreach (var backup in new[] { installedBackup, modsBackup })
                    if (File.Exists(backup))
                    {
                        try { File.Delete(backup); } catch { }
                    }
        }
    }

    internal void WriteModsAtomic(
        TextureRegistrySnapshot mods,
        CancellationToken cancellationToken = default,
        Action? beforeVerification = null)
    {
        ArgumentNullException.ThrowIfNull(mods);
        WriteAtomic(mods, paths.GetModsPath(mods.Game), cancellationToken, beforeVerification);
    }

    public void WriteAtomic(
        TextureRegistrySnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        WriteAtomic(snapshot, cancellationToken, beforeVerification: null);

    internal void WriteAtomic(
        TextureRegistrySnapshot snapshot,
        CancellationToken cancellationToken,
        Action? beforeVerification) =>
        WriteAtomic(snapshot, paths.GetPath(snapshot.Game), cancellationToken, beforeVerification);

    public void WriteManualAtomic(TextureRegistrySnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        WriteAtomic(snapshot, paths.GetManualPath(snapshot.Game), cancellationToken, null);

    private static void WriteAtomic(
        TextureRegistrySnapshot snapshot,
        string targetPath,
        CancellationToken cancellationToken,
        Action? beforeVerification)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var game = snapshot.Game;
        Validate(snapshot, game);
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        try
        {
            WriteTemporary(snapshot, temporaryPath);

            beforeVerification?.Invoke();
            VerifyTemporary(snapshot, temporaryPath, game);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
            InvalidateCachedPath(targetPath);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private static void WriteTemporary(TextureRegistrySnapshot snapshot, string temporaryPath)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (json.LongLength > MaximumJsonBytes)
            throw new InvalidDataException("The texture registry payload exceeds the 256 MiB limit.");
        using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(TextureRegistrySnapshot.CurrentSchemaVersion);
            writer.Write((int)snapshot.Game);
            writer.Write((long)json.Length);
        }
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(json);
        output.Flush(flushToDisk: true);
    }

    private static void VerifyTemporary(TextureRegistrySnapshot expected, string temporaryPath, MorphFaceGame game)
    {
        var verified = ReadFile(temporaryPath, game);
        if (verified.SchemaVersion != expected.SchemaVersion || verified.Game != expected.Game ||
            verified.InstalledPackageCount != expected.InstalledPackageCount ||
            verified.Candidates.Count != expected.Candidates.Count || verified.Assets.Count != expected.Assets.Count ||
            verified.AttachmentMeshes.Count != expected.AttachmentMeshes.Count ||
            verified.ManualAssets.Count != expected.ManualAssets.Count ||
            verified.MorphFaceTemplates.Count != expected.MorphFaceTemplates.Count)
            throw new InvalidDataException("The written texture registry failed verification.");
    }

    private static void InvalidateCachedPath(string path)
    {
        lock (ReadCacheLock)
        {
            var key = Path.GetFullPath(path);
            if (ReadCache.Remove(key, out var node)) ReadCacheLru.Remove(node);
        }
    }

    private sealed record CachedRead(string Path, MorphFaceGame Game,
        bool Exists, long Length, DateTime LastWriteTimeUtc, TextureRegistryReadResult Result);

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
        if (snapshot.Game != expectedGame)
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
    /// path/class locations.
    /// </summary>
    private sealed class InstalledAssetOccurrenceListConverter : JsonConverter<IReadOnlyList<InstalledAssetOccurrence>>
    {
        public override IReadOnlyList<InstalledAssetOccurrence> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
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

private const int HeaderSize = sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(long);
}

internal sealed record TextureRegistryReadResult(
    TextureRegistryStatus Status,
    TextureRegistrySnapshot? Snapshot);
