using System.Text;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.DataCompiler;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

if (args.Length is 3 or 4 && args[0] == "conversion-stress")
{
    var seed = args.Length == 4
        ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)
        : Random.Shared.Next();
    return ConversionStressRunner.Run(args[1], args[2], seed);
}

if (args.Length == 3 && args[0] == "conversion-full")
{
    return ConversionStressRunner.RunAll(args[1], args[2]);
}

if (args.Length == 4 && args[0] == "conversion-retest")
{
    return ConversionStressRunner.RetestFailures(args[1], args[2], args[3]);
}

if (args.Length == 3 && args[0] == "conversion-smoke")
{
    LegendaryExplorerCoreRuntime.Initialize();
    var corpusDirectory = Path.GetFullPath(args[1]);
    var outputDirectory = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(outputDirectory);
    var profiles = MorphFaceProfileRegistry.CreateDefault();
    var service = new MorphFaceConversionService(
        profiles,
        new MorphTargetCatalog(),
        new MorphFacePackageContextService(),
        new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault())));
    var cases = new[]
    {
        ("LE1", MorphFaceGame.LE2, "HMF.BIOA_PRC2_HMF_Guard01", "BIOG_HMF_HIR_PRO.Cute.HMF_HIR_Cte_MDL"),
        ("LE1", MorphFaceGame.LE3, "HMF.BIOA_PRC2_HMF_Guard01", "biog_hmf_hir_pro.Hair_Cute.HMF_HIR_Cte_MDL"),
        ("LE2", MorphFaceGame.LE1, "HMF.arv_kenson", "BIOG_HMF_HIR_PRO.Mom.HMF_HIR_Mom_MDL"),
        ("LE2", MorphFaceGame.LE3, "HMF.arv_kenson", "biog_hmf_hir_pro.Hair_Mom.HMF_HIR_Mom_MDL"),
        ("LE3", MorphFaceGame.LE1, "HMF.cat004_cerb_scientist1_face", "BIOG_HMF_HIR_PRO.PonyTail.Mom.HMF_HIR_Mom_MDL"),
        ("LE3", MorphFaceGame.LE2, "HMF.cat004_cerb_scientist1_face", "BIOG_HMF_HIR_PRO.Mom.HMF_HIR_Mom_MDL"),
        ("LE2", MorphFaceGame.LE1, "HMF.BioFace_giannaparasini", (string?)null),
        ("LE3", MorphFaceGame.LE1, "ASA.ASA_Face02", (string?)null),
        ("LE1", MorphFaceGame.LE3, "HMF.ice20_ambient_intervieweefemale", (string?)null)
    };
    foreach (var (sourceGame, targetGame, facePath, expectedHair) in cases)
    {
        var destination = Path.Combine(outputDirectory, $"{sourceGame}-to-{targetGame}-{facePath.Split('.').Last()}.pcc");
        if (File.Exists(destination))
        {
            throw new IOException($"Smoke-test output already exists: {destination}");
        }
        var result = service.Convert(new MorphFaceConversionRequest(
            Path.Combine(corpusDirectory, $"{sourceGame} GlobalMorphs.pcc"),
            facePath,
            targetGame,
            destination,
            CreateNewPackage: true,
            Path.Combine(corpusDirectory, $"{sourceGame} to {targetGame} GlobalMorphs.pcc")));
        using var package = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
        var face = package.FindExport(result.SaveResult.FaceInstancedPath, "BioMorphFace")
                   ?? throw new InvalidDataException("Converted face was not present after reopening the output.");
        var actualHair = face.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package)?.InstancedFullPath;
        if (!string.Equals(actualHair, expectedHair, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{sourceGame}->{targetGame} hair was '{actualHair}', expected '{expectedHair}'.");
        }
        var actualReferences = ReadReferences(face);
        foreach (var warning in result.SaveResult.Warnings)
        {
            Console.WriteLine($"WARN {sourceGame}->{targetGame}: {warning}");
        }
        Dictionary<string, string> canonicalImports;
        using (var canonicalPackage = MEPackageHandler.OpenMEPackage(
                   Path.Combine(corpusDirectory, $"{sourceGame} to {targetGame} GlobalMorphs.pcc"),
                   forceLoadFromDisk: true))
        {
            canonicalImports = canonicalPackage.Imports
                .GroupBy(import => import.InstancedFullPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => string.Join(" | ", group.Select(import =>
                        $"{import.ClassName}/{import.PackageFile}")),
                    StringComparer.OrdinalIgnoreCase);
            var canonicalFace = canonicalPackage.FindExport(facePath, "BioMorphFace")
                                ?? throw new InvalidDataException(
                                    $"Canonical {sourceGame}->{targetGame} corpus has no face '{facePath}'.");
            var canonicalReferences = ReadReferences(canonicalFace);
            AssertReferenceSet(
                $"{sourceGame}->{targetGame} other meshes",
                canonicalReferences.OtherMeshes,
                actualReferences.OtherMeshes);
            AssertReferenceSet(
                $"{sourceGame}->{targetGame} texture overrides",
                canonicalReferences.Textures,
                actualReferences.Textures);
        }
        var exportChildrenOfImports = package.Exports
            .Where(export => export.Parent is ImportEntry)
            .Select(export =>
            {
                var descendants = package.Exports.Cast<IEntry>().Concat(package.Imports)
                    .Where(entry => entry.InstancedFullPath.StartsWith(
                        export.InstancedFullPath + ".", StringComparison.OrdinalIgnoreCase))
                    .Select(entry => $"{(entry is ExportEntry ? "E" : "I")}:{entry.ClassName}:{entry.InstancedFullPath}");
                return $"{export.ClassName}:{export.InstancedFullPath} beneath {export.Parent!.InstancedFullPath} " +
                       $"[{string.Join(", ", descendants)}]";
            })
            .ToArray();
        if (exportChildrenOfImports.Length > 0)
        {
            throw new InvalidDataException(
                $"{sourceGame}->{targetGame} created exports beneath imports: " +
                string.Join("; ", exportChildrenOfImports));
        }
        if (package.Exports.Cast<IEntry>().Concat(package.Imports).Any(entry =>
                entry.InstancedFullPath.Contains("MFE_EmbeddedTextures", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"{sourceGame}->{targetGame} recreated the obsolete embedded-texture namespace.");
        }
        var duplicateIndices = EntryChecker.CheckForDuplicateIndices(package);
        if (duplicateIndices.Count > 0)
        {
            throw new InvalidDataException(
                $"{sourceGame}->{targetGame} contains duplicate entry indices: " +
                string.Join("; ", duplicateIndices.Select(value => value.Message)));
        }
        var referenceCheck = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(
            referenceCheck,
            package,
            LECLocalizationShim.NonLocalizedStringConverter);
        var propertyIssues = referenceCheck.GetBlockingErrors()
            .Concat(referenceCheck.GetSignificantIssues())
            .ToArray();
        if (propertyIssues.Length > 0)
        {
            throw new InvalidDataException(
                $"{sourceGame}->{targetGame} failed LEC's strict reference/property check: " +
                string.Join("; ", propertyIssues.Select(value => value.Message)));
        }
        using (var cache = new PackageCache())
        {
            var unresolved = package.Imports.Where(import =>
            {
                if (import.IsAKnownNativeClass() ||
                    import.InstancedFullPath.StartsWith("Core.", StringComparison.OrdinalIgnoreCase) ||
                    import.InstancedFullPath.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                try
                {
                    return EntryImporter.ResolveImport(import, cache) is null;
                }
                catch
                {
                    return true;
                }
            }).Select(import =>
            {
                canonicalImports.TryGetValue(import.InstancedFullPath, out var canonical);
                return $"{import.InstancedFullPath} [{import.ClassName}/{import.PackageFile}; " +
                       $"canonical={canonical ?? "absent"}]";
            }).ToArray();
            if (unresolved.Length > 0)
            {
                throw new InvalidDataException(
                    $"{sourceGame}->{targetGame} contains unresolvable imports: {string.Join(", ", unresolved)}.");
            }
        }
        Console.WriteLine(
            $"PASS {sourceGame}->{targetGame}: {Path.GetFileName(destination)}; hair={actualHair}; " +
            $"exports={package.ExportCount}; imports={package.ImportCount}; warnings={result.SaveResult.Warnings.Count}");
    }
    return 0;
}

if (args.Length >= 1 && args[0] == "audit-randomiser")
{
    if (args.Length < 7)
    {
        Console.Error.WriteLine(
            "Usage: audit-randomiser <package.pcc>... <report.md> <values.csv> <bundle.mfr.br>");
        return 2;
    }
    var outputStart = args.Length - 3;
    var rawFaces = MorphRandomisationCorpusReader.Read(args[1..outputStart]);
    var definitions = RandomisationProfileDefinitionFactory.CreateDefault(
        MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog());
    var compilation = RandomisationCorpusCompiler.Compile(
        rawFaces, definitions, ReviewedRandomisationExclusions.All);
    WriteText(args[outputStart], compilation.Markdown);
    WriteText(args[outputStart + 1], compilation.Csv);
    EnsureParent(args[outputStart + 2]);
    using (var output = File.Create(args[outputStart + 2]))
    {
        MorphRandomisationBundleSerializer.Write(output, compilation.Corpus);
    }
    var morphDonors = compilation.Faces.Count(value => value.DonorEligible);
    var embeddedDonors = compilation.Corpus.Pools.Sum(value => value.Value.Count);
    Console.WriteLine(
        $"Audited {compilation.Faces.Count} faces; " +
        $"embedded {embeddedDonors} donors ({morphDonors} morph-eligible, {embeddedDonors - morphDonors} material-only); " +
        $"reported {compilation.Faces.Count(value => !value.DonorEligible)} morph exclusions.");
    return 0;
}

if (args.Length >= 1 && args[0] == "reconstruct-vorcha")
{
    if (args.Length != 5)
    {
        Console.Error.WriteLine(
            "Usage: reconstruct-vorcha <le2-vorcha.pcc> <le3-vorcha.pcc> <le2-output.mft.br> <le3-output.mft.br>");
        return 2;
    }
    var reconstruction = VorchaMorphReconstructor.Reconstruct(args[1], args[2]);
    WriteBundle(args[3], reconstruction.Le2Targets);
    WriteBundle(args[4], reconstruction.Le3Targets);
    Console.WriteLine(
        $"Reconstructed {reconstruction.FeatureCount} Vorcha controls from {reconstruction.OracleFaceCount} LE2 oracles; " +
        $"maximum LE2 error: {reconstruction.MaximumLe2PositionError:G9} position, " +
        $"{reconstruction.MaximumLe2BoneError:G9} bone; " +
        $"LE3-compatible LODs: {string.Join(", ", reconstruction.Le3CompatibleLods)}.");
    return 0;
}

if (args.Length < 2 || args[0] is not ("scan" or "compile"))
{
    Console.Error.WriteLine(
        "Usage: scan <directory> | compile <package.pcc> <set-name> <output.mft.br> | " +
        "conversion-smoke <corpus-directory> <output-directory> | " +
        "conversion-stress <corpus-directory> <output-directory> [seed] | " +
        "conversion-full <corpus-directory> <output-directory> | " +
        "conversion-retest <corpus-directory> <prior-results.jsonl> <output-directory> | " +
        "audit-randomiser <package.pcc>... <report.md> <values.csv> <bundle.mfr.br> | " +
        "reconstruct-vorcha <le2-vorcha.pcc> <le3-vorcha.pcc> <le2-output.mft.br> <le3-output.mft.br>");
    return 2;
}

LegendaryExplorerCoreRuntime.Initialize();
if (args[0] == "compile")
{
    if (args.Length != 4)
    {
        Console.Error.WriteLine("Usage: compile <package.pcc> <set-name> <output.mft.br>");
        return 2;
    }
    var targets = MorphTargetPackageReader.LoadSet(args[1], args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    using var output = File.Create(args[3]);
    MorphTargetBundleSerializer.Write(output, targets);
    Console.WriteLine($"Wrote {targets.Count} targets to {Path.GetFullPath(args[3])}.");
    return 0;
}

var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "HMM_BaseMorphSet", "HMF_BaseMorphSet", "ASA_BaseMorphSet", "SAL_BaseMorphSet",
    "TUR_BaseMorphSet", "KRO_baseMorphSet", "BAT_BaseMorphSet"
};
foreach (var path in Directory.EnumerateFiles(args[1], "*.pcc", SearchOption.AllDirectories))
{
    try
    {
        foreach (var (setName, exportPath) in MorphTargetPackageReader.FindSets(path, wanted))
        {
            Console.WriteLine($"{setName}\t{path}\t{exportPath}");
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"SKIP\t{path}\t{exception.Message}");
    }
}
return 0;

static void WriteText(string path, string contents)
{
    EnsureParent(path);
    File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

static void EnsureParent(string path) =>
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

static void WriteBundle(string path, IReadOnlyList<MorphFaceEditor.Core.Domain.MorphTargetAsset> targets)
{
    EnsureParent(path);
    using var output = File.Create(path);
    MorphTargetBundleSerializer.Write(output, targets);
}

static FaceReferences ReadReferences(ExportEntry face)
{
    var properties = face.GetProperties();
    var others = properties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
        .Select(value => value.ResolveToEntry(face.FileRef)?.InstancedFullPath ?? "<unresolved>")
        .ToArray() ?? [];
    var materialOverride = properties.GetProp<ObjectProperty>("m_oMaterialOverrides")?
        .ResolveToEntry(face.FileRef) as ExportEntry;
    var textures = materialOverride?.GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides")?
        .Select(value =>
            $"{value.GetProp<NameProperty>("nName")?.Value.Instanced}=" +
            $"{value.GetProp<ObjectProperty>("m_pTexture")?.ResolveToEntry(face.FileRef)?.InstancedFullPath}")
        .ToArray() ?? [];
    return new FaceReferences(others, textures);
}

static void AssertReferenceSet(string label, IReadOnlyList<string> expected, IReadOnlyList<string> actual)
{
    var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).ToArray();
    var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).ToArray();
    if (missing.Length > 0 || extra.Length > 0)
    {
        throw new InvalidDataException(
            $"{label} differ from the canonical port. Missing [{string.Join(", ", missing)}]; " +
            $"extra [{string.Join(", ", extra)}].");
    }
}

sealed record FaceReferences(IReadOnlyList<string> OtherMeshes, IReadOnlyList<string> Textures);
