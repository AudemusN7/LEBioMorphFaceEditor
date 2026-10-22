using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.DataCompiler;

internal static class RegistryCommands
{
    private const int HeaderSize = sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(long);
    private const long MaximumJsonBytes = 256L * 1024 * 1024;

    public static void Unpack(string inputFile, string? outputFile)
    {
        var inputPath = Path.GetFullPath(inputFile);
        var outputPath = Path.GetFullPath(outputFile ?? Path.ChangeExtension(inputPath, ".json"));
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The JSON output must be a different file from the MFTR input.");

        using var input = File.OpenRead(inputPath);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        if (input.Length < HeaderSize)
            throw new InvalidDataException("The MFTR header is malformed.");
        var magic = reader.ReadUInt32();
        // Offline inspection remains available for rejected development files.
        if (magic != TextureRegistryStore.Magic && magic != TextureRegistryStore.DevelopmentMagic)
            throw new InvalidDataException("The MFTR header is malformed.");

        var schemaVersion = reader.ReadInt32();
        var game = reader.ReadInt32();
        var jsonLength = reader.ReadInt64();
        if (schemaVersion <= 0 || game is < 0 or > 2 || jsonLength is <= 0 or > MaximumJsonBytes)
            throw new InvalidDataException("The MFTR header contains an invalid schema, game, or JSON size.");

        // Read the wire format directly so older registries remain inspectable after
        // the editor's strongly typed registry schema has advanced.
        var json = new byte[checked((int)jsonLength)];
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true))
        {
            brotli.ReadExactly(json);
            if (brotli.ReadByte() != -1)
                throw new InvalidDataException("The MFTR payload exceeds its declared JSON size.");
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The MFTR payload is not a JSON object.");
        var formatted = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, formatted, new UTF8Encoding(false));
        Console.WriteLine($"Unpacked schema v{schemaVersion} {(MorphFaceGame)game} to {outputPath}");
    }

    public static void Audit(string gameName, string outputDirectory)
    {
        if (!Enum.TryParse<MorphFaceGame>(gameName, true, out var game) ||
            game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3))
            throw new ArgumentException("Specify LE1, LE2 or LE3.", nameof(gameName));

        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        var status = store.GetStatus(game);
        if (status.State != TextureRegistryState.Ready)
            throw new InvalidDataException($"{game} registry is {status.State}: {status.ErrorMessage}");

        var snapshot = store.Read(game);
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(output, $"{game}-registry.json"),
            JsonSerializer.Serialize(snapshot, jsonOptions), new UTF8Encoding(false));

        using var included = Writer(output, $"{game}-included.tsv",
            "ObjectPath\tPackagePath\tExportUIndex\tEffective\tOrigin\tMountPriority\tWidth\tHeight\tPixelFormat\tTextureGroup\tExternalMips");
        var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in snapshot.Candidates)
        {
            indexedPaths.Add(candidate.InstancedPath);
            foreach (var occurrence in candidate.Occurrences)
            {
                indexed.Add(Key(occurrence.PackagePath, occurrence.ExportUIndex, candidate.InstancedPath));
                Row(included, candidate.InstancedPath, occurrence.PackagePath,
                    occurrence.ExportUIndex, occurrence == candidate.EffectiveOccurrence,
                    occurrence.Origin, occurrence.MountPriority, occurrence.Width,
                    occurrence.Height, occurrence.PixelFormat, occurrence.TextureGroup,
                    occurrence.HasExternalMips);
            }
        }

        // Compare with every effective installed package, including mod packages. This
        // report reads packages but never changes the registry or installed game files.
        var loaded = MELoadedFiles.GetFilesLoadedInGame(ToMeGame(game)).Values
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var omitted = Writer(output, $"{game}-omitted.tsv",
            "ObjectPath\tPackagePath\tExportUIndex\tPathAlreadyIndexed\tMatchesDiscoveryPathRule");
        using var meshes = Writer(output, $"{game}-hir-meshes.tsv",
            "ObjectPath\tPackagePath\tExportUIndex\tIsDefaultObject");
        var missingPaths = new Dictionary<string, (int Count, string ExamplePackage, bool MatchesRule)>(
            StringComparer.OrdinalIgnoreCase);
        var omittedCount = 0;
        var meshCount = 0;
        for (var i = 0; i < loaded.Length; i++)
        {
            var path = loaded[i];
            using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            foreach (var export in package.Exports)
            {
                if (export.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                    export.ObjectName.Instanced.Contains("HIR", StringComparison.OrdinalIgnoreCase))
                {
                    Row(meshes, export.InstancedFullPath, path, export.UIndex, export.IsDefaultObject);
                    meshCount++;
                }
                if (export.IsDefaultObject ||
                    !export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
                    indexed.Contains(Key(path, export.UIndex, export.InstancedFullPath)))
                    continue;
                Row(omitted, export.InstancedFullPath, path, export.UIndex,
                    indexedPaths.Contains(export.InstancedFullPath),
                    TextureRegistryDiscovery.IsRelevantPath(export.InstancedFullPath));
                if (!indexedPaths.Contains(export.InstancedFullPath))
                {
                    var matchesRule = TextureRegistryDiscovery.IsRelevantPath(export.InstancedFullPath);
                    missingPaths.TryGetValue(export.InstancedFullPath, out var existing);
                    missingPaths[export.InstancedFullPath] =
                        (existing.Count + 1, existing.ExamplePackage ?? path, matchesRule);
                }
                omittedCount++;
            }
            if ((i + 1) % 100 == 0 || i + 1 == loaded.Length)
                Console.WriteLine($"{game}: inspected {i + 1:N0}/{loaded.Length:N0} installed packages");
        }

        using (var missing = Writer(output, $"{game}-missing-paths.tsv",
                   "ObjectPath\tOccurrences\tExamplePackagePath\tMatchesDiscoveryPathRule"))
        {
            foreach (var pair in missingPaths.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
                Row(missing, pair.Key, pair.Value.Count, pair.Value.ExamplePackage, pair.Value.MatchesRule);
        }

        var summary = new
        {
            Game = game.ToString(),
            RegistryPath = status.FilePath,
            snapshot.SchemaVersion,
            snapshot.BuiltAtUtc,
            snapshot.InstalledPackageCount,
            CurrentEffectivePackageCount = loaded.Length,
            IndexedTexturePaths = snapshot.Candidates.Count,
            IndexedTextureOccurrences = indexed.Count,
            OmittedTextureExportsInCurrentEffectivePackages = omittedCount,
            TexturePathsAbsentFromRegistry = missingPaths.Count,
            HirSkeletalMeshExportsInCurrentEffectivePackages = meshCount,
            MeshNote = "HIR in the SkeletalMesh name is the vanilla head-attachment discovery rule. This is an inventory; mesh loading is not tested."
        };
        File.WriteAllText(Path.Combine(output, $"{game}-summary.json"),
            JsonSerializer.Serialize(summary, jsonOptions), new UTF8Encoding(false));
        Console.WriteLine($"Wrote {game} audit to {output}");
    }

    private static StreamWriter Writer(string output, string name, string header)
    {
        var writer = new StreamWriter(Path.Combine(output, name), false, new UTF8Encoding(true));
        writer.WriteLine(header);
        return writer;
    }

    private static void Row(TextWriter writer, params object?[] cells) =>
        writer.WriteLine(string.Join('\t', cells.Select(cell =>
            (cell?.ToString() ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '))));

    private static string Key(string packagePath, int uIndex, string objectPath) =>
        $"{Path.GetFullPath(packagePath)}\u001f{uIndex}\u001f{objectPath}";

    private static MEGame ToMeGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => MEGame.LE1,
        MorphFaceGame.LE2 => MEGame.LE2,
        MorphFaceGame.LE3 => MEGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game))
    };

    public static void Verify(string directory)
    {
        var store = new TextureRegistryStore(new TextureRegistryPaths(directory));
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            var snapshot = store.Read(game);
            var excluded = snapshot.Candidates.Where(candidate =>
                candidate.Occurrences.Any(occurrence => TextureRegistryDiscovery.IsExcludedPath(
                    TextureCatalogPicker.CanonicalPath(candidate.InstancedPath, occurrence.PackagePath)))).ToArray();
            var unsafeMeshes = snapshot.AttachmentMeshes.Where(candidate =>
                !AttachmentMeshNamePolicy.IsInstalledHeadAttachment(candidate.EffectiveOccurrence.InstancedPath)).ToArray();
            var duplicateMeshes = snapshot.AttachmentMeshes.GroupBy(candidate => candidate.CanonicalPath,
                    StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToArray();
            var officialPaths = snapshot.OfficialPackagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var incorrectDonors = snapshot.Candidates.Where(candidate =>
            {
                var expected = candidate.Occurrences.Where(occurrence => officialPaths.Contains(occurrence.PackagePath))
                    .OrderByDescending(occurrence => occurrence.MountPriority)
                    .ThenBy(occurrence => occurrence.PackagePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                var actual = candidate.EffectiveOccurrence;
                return expected is not null &&
                    (!expected.PackagePath.Equals(actual.PackagePath, StringComparison.OrdinalIgnoreCase) ||
                     expected.ExportUIndex != actual.ExportUIndex || expected.MountPriority != actual.MountPriority ||
                     expected.Origin != actual.Origin);
            }).ToArray();
            if (excluded.Length > 0 || unsafeMeshes.Length > 0 || duplicateMeshes.Length > 0 ||
                incorrectDonors.Length > 0)
                throw new InvalidDataException($"{game} calibration failed: {excluded.Length} excluded textures, " +
                    $"{unsafeMeshes.Length} invalid meshes, {duplicateMeshes.Length} duplicate meshes, " +
                    $"{incorrectDonors.Length} textures with an incorrect official donor.");
            Console.WriteLine($"{game}: verified {snapshot.Candidates.Count} texture paths and " +
                $"{snapshot.AttachmentMeshes.Count} HIR mesh paths; no exclusion, duplicate, or official-donor violations.");
        }
    }

    public static async Task BuildAsync(string gameName, string outputDirectory)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var paths = new TextureRegistryPaths(outputDirectory);
        var builder = new TextureRegistryBuilder(new TextureRegistryStore(paths));
        MorphFaceGame[] games = gameName.ToUpperInvariant() switch
        {
            "LE1" => [MorphFaceGame.LE1],
            "LE2" => [MorphFaceGame.LE2],
            "LE3" => [MorphFaceGame.LE3],
            "ALL" => [MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3],
            _ => throw new ArgumentException("Specify LE1, LE2, LE3 or all.", nameof(gameName))
        };
        foreach (var game in games)
        {
            Console.WriteLine($"Building {game} texture database...");
            var status = await builder.RebuildAsync(game);
            if (status.State != TextureRegistryState.Ready)
            {
                throw new InvalidDataException($"{game} texture database build failed: {status.ErrorMessage}");
            }
            var snapshot = new TextureRegistryStore(paths).Read(game);
            Console.WriteLine($"{game}: {status.TextureCount} indexed textures; " +
                              $"{snapshot.AttachmentMeshes.Count} indexed HIR meshes.");
        }
    }
}
