using System.Text;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.DataCompiler;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

if (args.Length >= 1 && args[0] == "audit-randomiser")
{
    if (args.Length != 7)
    {
        Console.Error.WriteLine(
            "Usage: audit-randomiser <le1.pcc> <le2.pcc> <le3.pcc> <report.md> <values.csv> <bundle.mfr.br>");
        return 2;
    }
    var rawFaces = MorphRandomisationCorpusReader.Read(args[1..4]);
    var definitions = RandomisationProfileDefinitionFactory.CreateDefault(
        MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog());
    var compilation = RandomisationCorpusCompiler.Compile(
        rawFaces, definitions, ReviewedRandomisationExclusions.All);
    WriteText(args[4], compilation.Markdown);
    WriteText(args[5], compilation.Csv);
    EnsureParent(args[6]);
    using (var output = File.Create(args[6]))
    {
        MorphRandomisationBundleSerializer.Write(output, compilation.Corpus);
    }
    Console.WriteLine(
        $"Audited {compilation.Faces.Count} faces; " +
        $"embedded {compilation.Faces.Count(value => value.DonorEligible)} eligible donors; " +
        $"reported {compilation.Faces.Count(value => !value.DonorEligible)} exclusions.");
    return 0;
}

if (args.Length < 2 || args[0] is not ("scan" or "compile"))
{
    Console.Error.WriteLine(
        "Usage: scan <directory> | compile <package.pcc> <set-name> <output.mft.br> | " +
        "audit-randomiser <le1.pcc> <le2.pcc> <le3.pcc> <report.md> <values.csv> <bundle.mfr.br>");
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
