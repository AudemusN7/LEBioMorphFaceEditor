using MorphFaceEditor.Tests;

if (args.Length >= 3 && args[0].Equals("--d4-interrupted-save-worker", StringComparison.OrdinalIgnoreCase))
{
    return PackageIntegrityTests.RunInterruptedSaveWorker(args[1], args[2]);
}

// Suites keep routine feature work local. The expensive/full path is always explicit.
var suites = new Dictionary<string, IReadOnlyList<TestCase>>(StringComparer.OrdinalIgnoreCase)
{
    ["core"] = DeformationTests.All.Concat(EditingTests.All).Concat(DetachedMeshPreviewTests.All)
        .Concat(CustomMaterialWorkspaceTests.All).Concat(RandomisationTests.Runtime).ToArray(),
    ["materials"] = MaterialTests.All,
    ["rendering"] = RenderingTests.All,
    ["ui"] = UiSmokeTests.All.Concat(TextureRegistrySettingsTests.All)
        .Concat(MorphTargetCatalogTests.All).Concat(MorphFaceCatalogTests.All)
        .Concat(CustomMeshTests.All).Concat(MaterialInterchangeTests.All).ToArray(),
    ["package"] = PackageContextTests.All.Concat(PackageIntegrityTests.All).Concat(ActorAssignmentInventoryTests.All)
        .Concat(ActorAssignmentTransactionTests.All).Concat(CustomMaterialEvidenceTests.All)
        .Concat(RonStressTests.All).ToArray(),
    ["c4"] = ImportedSkeletalMeshWriterTests.All
        .Concat(CustomMeshPccMaterializerTests.All)
        .Concat(PccTextureDependencyResolverTests.All)
        .ToArray(),
    ["tooling"] = RandomisationTests.Tooling
};

if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
{
    foreach (var (suiteKey, cases) in suites)
    {
        Console.WriteLine($"{suiteKey} ({cases.Count})");
        foreach (var test in cases)
        {
            Console.WriteLine($"  {test.Name}");
        }
    }
    return 0;
}

var suiteArgument = Array.FindIndex(args, value => value.Equals("--suite", StringComparison.OrdinalIgnoreCase));
var suiteName = suiteArgument >= 0 && suiteArgument + 1 < args.Length ? args[suiteArgument + 1] : "core";
IReadOnlyList<TestCase> selected = suiteName.Equals("all", StringComparison.OrdinalIgnoreCase)
    ? suites.Values.SelectMany(value => value).ToArray()
    : suites.TryGetValue(suiteName, out var selectedSuite)
        ? selectedSuite
        : throw new ArgumentException($"Unknown suite '{suiteName}'. Use --list for valid suites.");

// Remaining positional arguments are optional name filters within the selected suite.
var filters = args.Where((value, index) =>
    !value.StartsWith("--", StringComparison.Ordinal) &&
    !(suiteArgument >= 0 && index == suiteArgument + 1)).ToArray();
var tests = selected.Where(test => filters.Length == 0 || filters.Any(filter =>
    test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();

if (tests.Length == 0)
{
    Console.Error.WriteLine($"No tests matched suite '{suiteName}'.");
    return 2;
}

var failures = new List<string>();
var skipped = 0;
var partial = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (TestSkippedException exception)
    {
        skipped++;
        Console.WriteLine($"SKIP {test.Name}: {exception.Message}");
    }
    catch (TestPartialException exception)
    {
        partial++;
        Console.WriteLine($"PARTIAL {test.Name}: {exception.Message}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

if (skipped == 0 && partial == 0)
{
    Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
}
else
{
    Console.WriteLine($"{tests.Length - failures.Count - skipped - partial} passed, {partial} partial, {skipped} skipped, " +
                      $"{failures.Count} failed ({tests.Length} tests).");
}
return failures.Count == 0 ? 0 : 1;
