using System.Text;
using System.Text.Json;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.DataCompiler;

internal static class CorpusCommands
{
    public static void Audit(string directory, string outputFile)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var corpusDirectory = Path.GetFullPath(directory);
        var outputPath = Path.GetFullPath(outputFile);
        var packagePaths = Directory.EnumerateFiles(corpusDirectory, "*.pcc", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).Contains("GlobalMorphs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packagePaths.Length != 9)
        {
            throw new InvalidDataException(
                $"Expected the nine canonical GlobalMorphs packages in '{corpusDirectory}', but found {packagePaths.Length}.");
        }

        var audit = packagePaths.Select(AuditCorpusPackage).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(audit, new JsonSerializerOptions
        {
            WriteIndented = true
        }), new UTF8Encoding(false));
        foreach (var auditedPackage in audit)
        {
            Console.WriteLine(
                $"{auditedPackage.FileName}: {auditedPackage.Game}; {auditedPackage.Faces.Count} faces; " +
                $"{auditedPackage.ReferencedAssets.Count} referenced assets; {auditedPackage.PackageStoredTextures.Count} package-stored textures");
        }
        Console.WriteLine($"Wrote {outputPath}");
    }

    public static void Reconcile(string directory, string outputFile)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var corpusDirectory = Path.GetFullPath(directory);
        var outputPath = Path.GetFullPath(outputFile);
        var audits = Directory.EnumerateFiles(corpusDirectory, "*.pcc", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).Contains("GlobalMorphs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(AuditCorpusPackage)
            .ToDictionary(value => value.FileName, StringComparer.OrdinalIgnoreCase);
        var pairs = new[]
        {
            ("LE1", "LE2"), ("LE1", "LE3"),
            ("LE2", "LE1"), ("LE2", "LE3"),
            ("LE3", "LE1"), ("LE3", "LE2")
        };
        var results = pairs.SelectMany(pair => BuildReconciliation(
                audits[$"{pair.Item1} GlobalMorphs.pcc"],
                audits[$"{pair.Item1} to {pair.Item2} GlobalMorphs.pcc"],
                pair.Item1,
                pair.Item2))
            .OrderBy(value => value.SourceGame, StringComparer.Ordinal)
            .ThenBy(value => value.TargetGame, StringComparer.Ordinal)
            .ThenBy(value => value.Kind, StringComparer.Ordinal)
            .ThenBy(value => value.Parameter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        }), new UTF8Encoding(false));
        Console.WriteLine($"Wrote {results.Length} changed/omitted asset decisions, including reviewed exceptions, to {outputPath}");
    }

    public static void WriteMaterialOracle(string directory, string outputFile)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var oracleDirectory = Path.GetFullPath(directory);
        var oracleOutputPath = Path.GetFullPath(outputFile);
        var oracleGames = new Dictionary<string, MaterialOracleGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var gameName in new[] { "LE1", "LE2", "LE3" })
        {
            var sourcePath = Path.Combine(oracleDirectory, $"{gameName} GlobalMorphs.pcc");
            using var sourcePackage = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
            var materialRoots = sourcePackage.Exports
                .Where(entry => IsMaterialGraphRootClass(entry.ClassName) && !IsTrashedMaterialGraphEntry(entry))
                .ToArray();
            var entries = materialRoots
                .SelectMany(root => EntryImporter.GetAllReferencesOfExport(root).Append<IEntry>(root))
                .Distinct()
                .Where(entry => !IsTrashedMaterialGraphEntry(entry))
                .Select(entry => new MaterialOracleEntry(
                    entry.ClassName,
                    entry.InstancedFullPath,
                    entry is ExportEntry ? "Export" : "Import"))
                .OrderBy(entry => entry.ClassName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (gameName.Equals("LE2", StringComparison.OrdinalIgnoreCase))
            {
                // These Player-only dependencies have reviewed native Startup import
                // roles, independent of whether the NPC GlobalMorphs corpus contains them.
                var reviewedPlayerImports = new[]
                {
                    new MaterialOracleEntry("RvrEffectsMaterialUser",
                        "BIOG_Humanoid_EYE_OVRD.HMM_EYE_MASTER_OVRD_MAT_USER", "Import"),
                    new MaterialOracleEntry("Texture2D",
                        "BIOG_Humanoid_MASTER_MTR_R.IrisGradientMask", "Import"),
                    new MaterialOracleEntry("TextureCube",
                        "BIOG_Humanoid_MASTER_MTR_R.Cube.Cube_EyeReflection", "Import")
                };
                foreach (var reviewedImport in reviewedPlayerImports)
                {
                    var matches = entries.Where(entry =>
                            entry.ClassName.Equals(reviewedImport.ClassName, StringComparison.OrdinalIgnoreCase) &&
                            entry.Path.Equals(reviewedImport.Path, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (matches.Length > 1)
                    {
                        throw new InvalidDataException(
                            $"Expected at most one LE2 GlobalMorphs role for '{reviewedImport.Path}', found {matches.Length}.");
                    }
                    entries = matches.Length == 0
                        ? entries.Append(reviewedImport).ToArray()
                        : entries.Select(entry =>
                            entry.ClassName.Equals(reviewedImport.ClassName, StringComparison.OrdinalIgnoreCase) &&
                            entry.Path.Equals(reviewedImport.Path, StringComparison.OrdinalIgnoreCase)
                                ? entry with { Kind = "Import" }
                                : entry).ToArray();
                }
            }
            if (gameName.Equals("LE3", StringComparison.OrdinalIgnoreCase))
            {
                // These exact LE3 attachment-hair parents are exported by reviewed
                // official basegame/DLC donors but are absent from the GlobalMorphs
                // projection. Retain their native export roles so attachment
                // material support remains independent of preview shader support.
                var reviewedHairExports = new[]
                {
                    new MaterialOracleEntry("RvrEffectsMaterialUser",
                        "EffectsMaterials.Users.Human.HMN_HED_PRO_MASTER_HAIR_MAT_USER_CC", "Export"),
                    new MaterialOracleEntry("Material",
                        "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HED_PRO_MASTER_HAIR_MAT_CC", "Export"),
                    new MaterialOracleEntry("RvrEffectsMaterialUser",
                        "EffectsMaterials.Users.HMM_HED_PRO_USER_SCALP_MAT_CC", "Export"),
                    new MaterialOracleEntry("Material",
                        "BIOG_Humanoid_MASTER_MTR_R.Human.HMM_HED_PRO_MASTER_SCALP_MAT_CC", "Export"),
                    new MaterialOracleEntry("RvrEffectsMaterialUser",
                        "BIOG_HMM_HED_PROMorph.Kaiden.Master.BIOG_HMM_HED_Kaiden_USER", "Export"),
                    new MaterialOracleEntry("Material",
                        "BIOG_HMM_HED_PROMorph.Kaiden.Master.BIOG_HMM_HED_Kaiden_MASTER", "Export"),
                    new MaterialOracleEntry("Material",
                        "BIOG_Humanoid_MASTER_MTR_R.Human.Hair.Human_Hair_MASTER", "Export")
                };
                foreach (var reviewedHairExport in reviewedHairExports)
                {
                    var matches = entries.Where(entry =>
                            entry.ClassName.Equals(reviewedHairExport.ClassName, StringComparison.OrdinalIgnoreCase) &&
                            entry.Path.Equals(reviewedHairExport.Path, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (matches.Length > 1)
                    {
                        throw new InvalidDataException(
                            $"Expected at most one LE3 GlobalMorphs role for '{reviewedHairExport.Path}', found {matches.Length}.");
                    }
                    if (matches.Any(entry => !entry.Kind.Equals("Export", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"Reviewed LE3 hair role conflicts with corpus role for '{reviewedHairExport.Path}'.");
                    if (matches.Length == 0)
                        entries = entries.Append(reviewedHairExport).ToArray();
                }
            }
            oracleGames.Add(gameName, new MaterialOracleGame(entries));
            Console.WriteLine($"{gameName}: {entries.Length} material/texture identities");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(oracleOutputPath)!);
        File.WriteAllText(oracleOutputPath, JsonSerializer.Serialize(
            new MaterialOracleDocument(1, oracleGames),
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Wrote {oracleOutputPath}");
    }

    static bool IsMaterialGraphRootClass(string className) =>
        className.Equals("Material", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase);

    static bool IsTrashedMaterialGraphEntry(IEntry entry) =>
        entry.InstancedFullPath.Equals("ME3ExplorerTrashPackage", StringComparison.OrdinalIgnoreCase) ||
        entry.InstancedFullPath.StartsWith("ME3ExplorerTrashPackage.", StringComparison.OrdinalIgnoreCase);

    static CorpusPackageAudit AuditCorpusPackage(string packagePath)
    {
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var faces = package.Exports
            .Where(export => !export.IsDefaultObject &&
                             export.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase))
            .OrderBy(export => export.InstancedFullPath, StringComparer.OrdinalIgnoreCase)
            .Select(face =>
            {
                var properties = face.GetProperties();
                var materialOverride = properties.GetProp<ObjectProperty>("m_oMaterialOverrides")?.ResolveToEntry(package)
                                       as ExportEntry;
                var textureOverrides = materialOverride?.GetProperty<ArrayProperty<StructProperty>>(
                        "m_aTextureOverrides")?
                    .Select(value => new CorpusTextureOverride(
                        value.GetProp<NameProperty>("nName")?.Value.Instanced ?? "<unnamed>",
                        DescribeCorpusEntry(value.GetProp<ObjectProperty>("m_pTexture")?.ResolveToEntry(package))))
                    .OrderBy(value => value.Parameter, StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? [];
                return new CorpusFaceAudit(
                    face.InstancedFullPath,
                    DescribeCorpusEntry(properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package)),
                    DescribeCorpusEntry(properties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package)),
                    properties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
                        .Select(value => DescribeCorpusEntry(value.ResolveToEntry(package)))
                        .ToArray() ?? [],
                    materialOverride?.InstancedFullPath,
                    textureOverrides);
            })
            .ToArray();
        var referencedAssets = faces
            .SelectMany(face => new[] { face.BaseHead, face.Hair }
                .Concat(face.OtherMeshes)
                .Concat(face.TextureOverrides.Select(value => value.Texture)))
            .Where(value => value is not null)
            .Cast<CorpusEntryAudit>()
            .DistinctBy(value => $"{value.Kind}|{value.ClassName}|{value.Path}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.ClassName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var storedTextures = package.Exports
            .Where(export => export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            .Select(export =>
            {
                var texture = new LegendaryExplorerCore.Unreal.Classes.Texture2D(export);
                return new CorpusStoredTexture(
                    export.InstancedFullPath,
                    texture.GetTopMip().IsPackageStored,
                    DescribeCorpusEntry(export.Parent));
            })
            .Where(value => value.IsPackageStored)
            .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CorpusPackageAudit(
            Path.GetFileName(packagePath),
            package.Game.ToString(),
            faces,
            referencedAssets,
            storedTextures);
    }

    static CorpusEntryAudit? DescribeCorpusEntry(IEntry? entry) => entry is null
        ? null
        : new CorpusEntryAudit(
            entry is ExportEntry ? "Export" : "Import",
            entry.ClassName,
            entry.InstancedFullPath,
            entry.UIndex,
            entry.Parent is null ? null : $"{(entry.Parent is ExportEntry ? "Export" : "Import")}:{entry.Parent.InstancedFullPath}");

    static IEnumerable<CorpusReconciliationDecision> BuildReconciliation(
        CorpusPackageAudit source,
        CorpusPackageAudit target,
        string sourceGame,
        string targetGame)
    {
        var targetFaces = target.Faces.ToDictionary(value => value.Path, StringComparer.OrdinalIgnoreCase);
        var evidence = new List<(string Kind, string? Parameter, string SourcePath, string? TargetPath)>();
        foreach (var sourceFace in source.Faces)
        {
            if (!targetFaces.TryGetValue(sourceFace.Path, out var targetFace))
            {
                continue;
            }
            if (sourceFace.Hair is not null)
            {
                evidence.Add(("Hair", null, sourceFace.Hair.Path, targetFace.Hair?.Path));
            }
            for (var index = 0; index < sourceFace.OtherMeshes.Count; index++)
            {
                if (sourceFace.OtherMeshes[index] is { } other)
                {
                    evidence.Add(("Other", null, other.Path,
                        index < targetFace.OtherMeshes.Count ? targetFace.OtherMeshes[index]?.Path : null));
                }
            }
            var targetTextures = targetFace.TextureOverrides
                .GroupBy(value => value.Parameter, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Texture?.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var texture in sourceFace.TextureOverrides.Where(value => value.Texture is not null))
            {
                targetTextures.TryGetValue(texture.Parameter, out var targetPath);
                evidence.Add(("Texture", texture.Parameter, texture.Texture!.Path, targetPath));
            }
        }

        foreach (var group in evidence.GroupBy(value =>
                     $"{value.Kind}\u001f{value.Parameter}\u001f{value.SourcePath}", StringComparer.OrdinalIgnoreCase))
        {
            var targets = group.Select(value => value.TargetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (targets.Length != 1)
            {
                // The accepted corpus retained these two exact native cap aliases.
                // Some faces omit the cap, so aggregate fixture evidence alone is
                // ambiguous; an omission must not erase the reviewed mapping.
                const string capSource = "BIOG_HMM_HIR_PRO_R.Hat_Cap.HMM_HAT_Cap_MDL";
                const string capTarget = "BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_MDL";
                var reviewedSample = group.First();
                if (sourceGame == "LE3" && targetGame is "LE1" or "LE2" &&
                    reviewedSample.Kind == "Hair" && reviewedSample.Parameter is null &&
                    reviewedSample.SourcePath.Equals(capSource, StringComparison.OrdinalIgnoreCase) &&
                    targets.Any(path => path?.Equals(capTarget, StringComparison.OrdinalIgnoreCase) == true) &&
                    targets.All(path => path is null || path.Equals(capTarget, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"Retained reviewed {sourceGame}->{targetGame} cap alias despite omitted fixture instances.");
                    yield return new CorpusReconciliationDecision(
                        sourceGame, targetGame, "Hair", null, capSource, capTarget, 1);
                    continue;
                }
                Console.Error.WriteLine(
                    $"Skipped ambiguous {sourceGame}->{targetGame} reconciliation for {group.Key}: " +
                    string.Join(", ", targets.Select(value => value ?? "<omitted>")));
                continue;
            }
            var sample = group.First();
            if (targets[0] is not null &&
                targets[0]!.Equals(sample.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return new CorpusReconciliationDecision(
                sourceGame,
                targetGame,
                sample.Kind,
                sample.Parameter,
                sample.SourcePath,
                targets[0],
                group.Count());
        }
    }

    sealed record CorpusPackageAudit(
        string FileName,
        string Game,
        IReadOnlyList<CorpusFaceAudit> Faces,
        IReadOnlyList<CorpusEntryAudit> ReferencedAssets,
        IReadOnlyList<CorpusStoredTexture> PackageStoredTextures);

    sealed record CorpusFaceAudit(
        string Path,
        CorpusEntryAudit? BaseHead,
        CorpusEntryAudit? Hair,
        IReadOnlyList<CorpusEntryAudit?> OtherMeshes,
        string? MaterialOverride,
        IReadOnlyList<CorpusTextureOverride> TextureOverrides);

    sealed record CorpusTextureOverride(string Parameter, CorpusEntryAudit? Texture);
    sealed record CorpusEntryAudit(string Kind, string ClassName, string Path, int UIndex, string? Parent);
    sealed record CorpusStoredTexture(string Path, bool IsPackageStored, CorpusEntryAudit? Parent);
    sealed record CorpusReconciliationDecision(
        string SourceGame,
        string TargetGame,
        string Kind,
        string? Parameter,
        string SourcePath,
        string? TargetPath,
        int EvidenceCount);

    sealed record MaterialOracleDocument(
        int FormatVersion,
        IReadOnlyDictionary<string, MaterialOracleGame> Games);

    sealed record MaterialOracleGame(IReadOnlyList<MaterialOracleEntry> Entries);
    sealed record MaterialOracleEntry(string ClassName, string Path, string Kind);
}
