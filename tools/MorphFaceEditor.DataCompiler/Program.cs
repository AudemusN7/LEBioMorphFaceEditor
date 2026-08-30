using System.Text;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.DataCompiler;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

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
