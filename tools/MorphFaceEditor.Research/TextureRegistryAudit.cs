using System.Text;
using System.Text.Json;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

internal static class TextureRegistryAudit
{
    public static void Run(string gameName, string outputDirectory)
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
}
