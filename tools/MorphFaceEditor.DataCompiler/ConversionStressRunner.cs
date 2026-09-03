using System.Diagnostics;
using System.Text.Json;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.DataCompiler;

internal static class ConversionStressRunner
{
    private static readonly MorphFaceGame[] Games =
        [MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3];

    internal static int Run(string corpusDirectory, string outputDirectory, int seed)
        => Run(corpusDirectory, outputDirectory, seed, selectAll: false);

    internal static int RunAll(string corpusDirectory, string outputDirectory)
        => Run(corpusDirectory, outputDirectory, seed: 0, selectAll: true);

    private static int Run(string corpusDirectory, string outputDirectory, int seed, bool selectAll)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var corpus = Path.GetFullPath(corpusDirectory);
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "ports"));

        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var selections = selectAll
            ? SelectAllFaces(corpus, profiles)
            : SelectFaces(corpus, profiles, seed);
        if (!selectAll && selections.Count != 115)
        {
            throw new InvalidDataException($"Stress selection produced {selections.Count} originals, expected 115.");
        }

        var attempts = selections.SelectMany(selection => Games
                .Where(game => game != selection.Game)
                .Select(target => new PortAttempt(selection, target)))
            .ToArray();
        if (!selectAll && attempts.Length != 230)
        {
            throw new InvalidDataException($"Stress selection produced {attempts.Length} ports, expected 230.");
        }

        WriteJsonAtomic(Path.Combine(output, "selection-manifest.json"), new
        {
            RunKind = selectAll ? "full" : "stress",
            Seed = seed,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OriginalFaceCount = selections.Count,
            PlannedPortCount = attempts.Length,
            PlannedFinalFaceInstances = selections.Count + attempts.Length,
            Selections = selections
        });

        var registry = new TextureCatalogService(
            new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));
        var service = new MorphFaceConversionService(
            profiles,
            new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            registry);
        var resultsPath = Path.Combine(output, "results.jsonl");
        var progressPath = Path.Combine(output, "progress.json");
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;

        for (var index = 0; index < attempts.Length; index++)
        {
            var attempt = attempts[index];
            var sourceGame = attempt.Source.Game.ToString();
            var targetGame = attempt.TargetGame.ToString();
            var direction = $"{sourceGame}-to-{targetGame}";
            var destinationDirectory = Path.Combine(output, "ports", direction, attempt.Source.Species);
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(
                destinationDirectory,
                $"{index + 1:D3}_{Sanitize(attempt.Source.FacePath.Split('.').Last())}.pcc");
            var stopwatch = Stopwatch.StartNew();
            string status;
            string? error = null;
            try
            {
                if (File.Exists(destination))
                {
                    status = "skipped-existing";
                    skipped++;
                }
                else
                {
                    var canonicalPort = Path.Combine(corpus, $"{sourceGame} to {targetGame} GlobalMorphs.pcc");
                    var result = service.Convert(new MorphFaceConversionRequest(
                        Path.Combine(corpus, $"{sourceGame} GlobalMorphs.pcc"),
                        attempt.Source.FacePath,
                        attempt.TargetGame,
                        destination,
                        CreateNewPackage: true,
                        canonicalPort));
                    ValidatePort(destination, canonicalPort, attempt.Source.FacePath, result.SaveResult.FaceInstancedPath);
                    status = "passed";
                    succeeded++;
                }
            }
            catch (Exception exception)
            {
                status = "failed";
                failed++;
                error = exception.ToString();
            }
            stopwatch.Stop();

            AppendJsonLine(resultsPath, new
            {
                Attempt = index + 1,
                Total = attempts.Length,
                attempt.Source.Game,
                attempt.Source.Species,
                attempt.Source.FacePath,
                TargetGame = attempt.TargetGame,
                Destination = destination,
                Status = status,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                Error = error,
                FinishedAtUtc = DateTimeOffset.UtcNow
            });
            WriteJsonAtomic(progressPath, new
            {
                Seed = seed,
                Completed = index + 1,
                Total = attempts.Length,
                Succeeded = succeeded,
                Failed = failed,
                Skipped = skipped,
                OriginalFaceCount = selections.Count,
                FinalFaceInstancesCompleted = selections.Count + succeeded + skipped,
                Current = $"{direction} {attempt.Source.Species} {attempt.Source.FacePath}",
                LastStatus = status,
                LastError = error is null ? null : exceptionSummary(error),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                IsComplete = index + 1 == attempts.Length
            });
            Console.WriteLine(
                $"[{index + 1,3}/{attempts.Length}] {status.ToUpperInvariant(),16} " +
                $"{direction} {attempt.Source.Species} {attempt.Source.FacePath} " +
                $"({stopwatch.Elapsed.TotalSeconds:F1}s; pass={succeeded}, fail={failed}, skip={skipped})");
        }

        WriteJsonAtomic(Path.Combine(output, "summary.json"), new
        {
            RunKind = selectAll ? "full" : "stress",
            Seed = seed,
            OriginalFaceCount = selections.Count,
            AttemptedPortCount = attempts.Length,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = skipped,
            FinalFaceInstances = selections.Count + succeeded + skipped,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
        Console.WriteLine(
            $"COMPLETE originals={selections.Count}; ports={attempts.Length}; " +
            $"passed={succeeded}; failed={failed}; skipped={skipped}; " +
            $"final instances={selections.Count + succeeded + skipped}.");
        return 0;
    }

    internal static int RetestFailures(
        string corpusDirectory,
        string priorResultsPath,
        string outputDirectory)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var corpus = Path.GetFullPath(corpusDirectory);
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var attempts = File.ReadLines(Path.GetFullPath(priorResultsPath))
            .Select(line => JsonSerializer.Deserialize<RetestSource>(line)
                            ?? throw new InvalidDataException("A prior result row was empty."))
            .Where(value => value.Status == "failed" &&
                            !(value.Species.Equals("vorcha", StringComparison.OrdinalIgnoreCase) &&
                              value.TargetGame == MorphFaceGame.LE1))
            .OrderBy(value => value.Attempt)
            .ToArray();
        var registry = new TextureCatalogService(
            new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));
        var service = new MorphFaceConversionService(
            MorphFaceProfileRegistry.CreateDefault(),
            new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            registry);
        var resultsPath = Path.Combine(output, "results.jsonl");
        var passed = 0;
        var failed = 0;
        foreach (var attempt in attempts)
        {
            var sourceGame = attempt.Game.ToString();
            var targetGame = attempt.TargetGame.ToString();
            var destination = Path.Combine(
                output,
                $"{attempt.Attempt:D3}_{sourceGame}_to_{targetGame}_{Sanitize(attempt.FacePath.Split('.').Last())}.pcc");
            var stopwatch = Stopwatch.StartNew();
            string status;
            string? error = null;
            try
            {
                var canonicalPort = Path.Combine(corpus, $"{sourceGame} to {targetGame} GlobalMorphs.pcc");
                var result = service.Convert(new MorphFaceConversionRequest(
                    Path.Combine(corpus, $"{sourceGame} GlobalMorphs.pcc"),
                    attempt.FacePath,
                    attempt.TargetGame,
                    destination,
                    CreateNewPackage: true,
                    canonicalPort));
                ValidatePort(destination, canonicalPort, attempt.FacePath, result.SaveResult.FaceInstancedPath);
                status = "passed";
                passed++;
            }
            catch (Exception exception)
            {
                status = "failed";
                error = exception.ToString();
                failed++;
            }
            stopwatch.Stop();
            AppendJsonLine(resultsPath, new
            {
                attempt.Attempt,
                attempt.Game,
                attempt.TargetGame,
                attempt.Species,
                attempt.FacePath,
                Destination = destination,
                Status = status,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                Error = error
            });
            Console.WriteLine(
                $"[{passed + failed,2}/{attempts.Length}] {status.ToUpperInvariant(),6} " +
                $"{sourceGame}->{targetGame} {attempt.FacePath} ({stopwatch.Elapsed.TotalSeconds:F1}s)");
        }
        WriteJsonAtomic(Path.Combine(output, "summary.json"), new
        {
            Retested = attempts.Length,
            Passed = passed,
            Failed = failed,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
        return failed == 0 ? 0 : 1;
    }

    private static List<SelectedFace> SelectFaces(
        string corpusDirectory,
        MorphFaceProfileRegistry profiles,
        int seed)
    {
        var random = new Random(seed);
        var selected = new List<SelectedFace>();
        foreach (var game in Games)
        {
            var packagePath = Path.Combine(corpusDirectory, $"{game} GlobalMorphs.pcc");
            var candidates = MorphFaceReferenceInspector.Inspect(packagePath)
                .Select(face => (Face: face, Profile: profiles.Find(game, face.FacePath, face.BaseHeadPath)))
                .Where(value => value.Profile is not null &&
                                !value.Profile.Key.EndsWith("-female-turian", StringComparison.OrdinalIgnoreCase))
                .Select(value => new SelectedFace(
                    game,
                    Species(value.Profile!.Key),
                    value.Profile.Key,
                    value.Face.FacePath,
                    value.Face.FaceUIndex,
                    value.Face.BaseHeadPath))
                .OrderBy(value => value.Species, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.FacePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var expectedSpecies = game == MorphFaceGame.LE1 ? 7 : 8;
            var groups = candidates.GroupBy(value => value.Species, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (groups.Length != expectedSpecies)
            {
                throw new InvalidDataException(
                    $"{game} exposed {groups.Length} non-TUF species groups, expected {expectedSpecies}: " +
                    string.Join(", ", groups.Select(group => group.Key)));
            }
            foreach (var group in groups)
            {
                var pool = group.ToList();
                if (pool.Count == 0)
                {
                    throw new InvalidDataException(
                        $"{game} {group.Key} has no recognised faces.");
                }
                Shuffle(pool, random);
                for (var sample = 0; sample < 5; sample++)
                {
                    selected.Add(pool[sample % pool.Count] with
                    {
                        SampleOrdinal = sample + 1,
                        SpeciesPopulation = pool.Count,
                        IsRepeatedSample = sample >= pool.Count
                    });
                }
            }
        }
        return selected;
    }

    private static List<SelectedFace> SelectAllFaces(
        string corpusDirectory,
        MorphFaceProfileRegistry profiles)
    {
        var selected = new List<SelectedFace>();
        foreach (var game in Games)
        {
            var packagePath = Path.Combine(corpusDirectory, $"{game} GlobalMorphs.pcc");
            selected.AddRange(MorphFaceReferenceInspector.Inspect(packagePath)
                .Select(face => (Face: face, Profile: profiles.Find(game, face.FacePath, face.BaseHeadPath)))
                .Where(value => value.Profile is not null &&
                                !value.Profile.Key.EndsWith("-female-turian", StringComparison.OrdinalIgnoreCase))
                .Select(value => new SelectedFace(
                    game,
                    Species(value.Profile!.Key),
                    value.Profile.Key,
                    value.Face.FacePath,
                    value.Face.FaceUIndex,
                    value.Face.BaseHeadPath))
                .OrderBy(value => value.Species, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.FacePath, StringComparer.OrdinalIgnoreCase));
        }
        if (selected.Count == 0)
        {
            throw new InvalidDataException("Full conversion selection found no supported, non-TUF faces.");
        }
        return selected;
    }

    private static void ValidatePort(
        string packagePath,
        string canonicalPortPath,
        string canonicalFacePath,
        string outputFacePath)
    {
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var face = package.FindExport(outputFacePath, "BioMorphFace")
                   ?? throw new InvalidDataException("Converted BioMorphFace was missing after reopen.");
        if (package.Exports.Any(export => export.GetProperties().Any(ContainsUnknownProperty)))
        {
            throw new InvalidDataException("Converted package contains UnknownProperty data.");
        }
        var mixed = package.Exports.FirstOrDefault(export => export.Parent is ImportEntry);
        if (mixed is not null)
        {
            throw new InvalidDataException(
                $"Export '{mixed.InstancedFullPath}' is beneath import '{mixed.Parent?.InstancedFullPath}'.");
        }
        if (package.Exports.Cast<IEntry>().Concat(package.Imports).Any(entry =>
                entry.InstancedFullPath.Contains("MFE_EmbeddedTextures", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Obsolete MFE_EmbeddedTextures hierarchy was recreated.");
        }
        var invalidHmmPath = package.Exports.Cast<IEntry>().Concat(package.Imports).FirstOrDefault(entry =>
            entry.InstancedFullPath.Equals("BIOG_HMM_HED_PROMorph_R", StringComparison.OrdinalIgnoreCase) ||
            entry.InstancedFullPath.StartsWith("BIOG_HMM_HED_PROMorph_R.", StringComparison.OrdinalIgnoreCase));
        if (invalidHmmPath is not null)
        {
            throw new InvalidDataException(
                $"Invalid HMM morph package alias survived conversion: {invalidHmmPath.InstancedFullPath}.");
        }
        var duplicateIndices = EntryChecker.CheckForDuplicateIndices(package);
        if (duplicateIndices.Count > 0)
        {
            throw new InvalidDataException(
                "Duplicate entry identities: " +
                string.Join("; ", duplicateIndices.Select(value => value.Message)));
        }
        var referenceCheck = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(referenceCheck, package, LECLocalizationShim.NonLocalizedStringConverter);
        var propertyIssues = referenceCheck.GetBlockingErrors()
            .Concat(referenceCheck.GetSignificantIssues())
            .ToArray();
        if (propertyIssues.Length > 0)
        {
            throw new InvalidDataException(
                "LEC reference/property failures: " +
                string.Join("; ", propertyIssues.Select(value => value.Message)));
        }
        using (var cache = new PackageCache())
        {
            var unresolved = package.Imports.Where(import =>
            {
                if (import.IsAKnownNativeClass() ||
                    import.InstancedFullPath.StartsWith("Core.", StringComparison.OrdinalIgnoreCase) ||
                    import.InstancedFullPath.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase)) return false;
                try
                {
                    return EntryImporter.ResolveImport(import, cache) is null;
                }
                catch
                {
                    return true;
                }
            }).Select(import => import.InstancedFullPath).ToArray();
            if (unresolved.Length > 0)
            {
                throw new InvalidDataException("Unresolvable imports: " + string.Join(", ", unresolved));
            }
        }

        using var canonical = MEPackageHandler.OpenMEPackage(canonicalPortPath, forceLoadFromDisk: true);
        var canonicalFace = canonical.FindExport(canonicalFacePath, "BioMorphFace")
                            ?? throw new InvalidDataException(
                                $"Canonical directional corpus has no face '{canonicalFacePath}'.");
        AssertEqualReferences("hair", [ReadHair(canonicalFace)], [ReadHair(face)]);
        AssertEqualReferences("other meshes", ReadOthers(canonicalFace), ReadOthers(face));
        AssertEqualReferences(
            "textures",
            ReadTextures(canonicalFace).Select(value => NormalizeTextureReference(package.Game, value)).ToArray(),
            ReadTextures(face).Select(value => NormalizeTextureReference(package.Game, value)).ToArray());
    }

    private static string? ReadHair(ExportEntry face) =>
        face.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(face.FileRef)?.InstancedFullPath;

    private static IReadOnlyList<string?> ReadOthers(ExportEntry face) =>
        face.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
            .Select(value => value.ResolveToEntry(face.FileRef)?.InstancedFullPath)
            .ToArray() ?? [];

    private static IReadOnlyList<string?> ReadTextures(ExportEntry face)
    {
        var material = face.GetProperty<ObjectProperty>("m_oMaterialOverrides")?
            .ResolveToEntry(face.FileRef) as ExportEntry;
        return material?.GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides")?
            .Select(value =>
                $"{value.GetProp<NameProperty>("nName")?.Value.Instanced}=" +
                value.GetProp<ObjectProperty>("m_pTexture")?.ResolveToEntry(face.FileRef)?.InstancedFullPath)
            .ToArray() ?? [];
    }

    private static string? NormalizeTextureReference(MEGame targetGame, string? value)
    {
        if (value is null) return null;
        var separator = value.IndexOf('=');
        if (separator < 0) return value;
        var path = value[(separator + 1)..];
        if (path.StartsWith(
                "BIOG_HMM_HED_PROMorph_R.", StringComparison.OrdinalIgnoreCase))
        {
            path = "BIOG_HMM_HED_PROMorph." + path["BIOG_HMM_HED_PROMorph_R.".Length..];
        }
        if (targetGame is MEGame.LE1 or MEGame.LE2 && path.EndsWith(
                ".HMF_HED_PROLash_Opac_M01", StringComparison.OrdinalIgnoreCase))
        {
            path = "BIOG_Humanoid_MASTER_MTR_R.Human.HMF_HED_PROLash_Opac_M01";
        }
        return $"{value[..(separator + 1)]}{path}";
    }

    private static void AssertEqualReferences(
        string label,
        IReadOnlyList<string?> expected,
        IReadOnlyList<string?> actual)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var missing = expected.Except(actual, comparer).ToArray();
        var extra = actual.Except(expected, comparer).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
        {
            throw new InvalidDataException(
                $"Canonical {label} mismatch. Missing [{string.Join(", ", missing)}]; " +
                $"extra [{string.Join(", ", extra)}].");
        }
    }

    private static bool ContainsUnknownProperty(Property property) => property switch
    {
        UnknownProperty => true,
        StructProperty structure => structure.Properties.Any(ContainsUnknownProperty),
        ArrayProperty<StructProperty> array => array.Any(value => value.Properties.Any(ContainsUnknownProperty)),
        _ => false
    };

    private static string Species(string profileKey) => profileKey[(profileKey.IndexOf('-') + 1)..];

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());

    private static void Shuffle<T>(IList<T> values, Random random)
    {
        for (var index = values.Count - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (values[index], values[other]) = (values[other], values[index]);
        }
    }

    private static void AppendJsonLine(string path, object value) => File.AppendAllText(
        path,
        JsonSerializer.Serialize(value) + Environment.NewLine);

    private static void WriteJsonAtomic(string path, object value)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temporary, path, overwrite: true);
    }

    private static string exceptionSummary(string error)
    {
        var line = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line ?? error;
    }

    private sealed record PortAttempt(SelectedFace Source, MorphFaceGame TargetGame);

    private sealed record RetestSource(
        int Attempt,
        MorphFaceGame Game,
        MorphFaceGame TargetGame,
        string Species,
        string FacePath,
        string Status);

    private sealed record SelectedFace(
        MorphFaceGame Game,
        string Species,
        string ProfileKey,
        string FacePath,
        int FaceUIndex,
        string? BaseHeadPath)
    {
        public int SampleOrdinal { get; init; }
        public int SpeciesPopulation { get; init; }
        public bool IsRepeatedSample { get; init; }
    }
}
