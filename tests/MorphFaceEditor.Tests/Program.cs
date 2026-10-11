using MorphFaceEditor.Tests;

if (args.Length >= 3 && args[0].Equals("--d4-interrupted-save-worker", StringComparison.OrdinalIgnoreCase))
{
    return PackageIntegrityTests.RunInterruptedSaveWorker(args[1], args[2]);
}

// Suites keep routine feature work local. The expensive/full path is always explicit.
var suites = new Dictionary<string, IReadOnlyList<TestCase>>(StringComparer.OrdinalIgnoreCase)
{
    ["core"] = DeformationTests.All.Concat(EditingTests.All).Concat(DetachedMeshPreviewTests.All)
        .Concat(CustomMaterialWorkspaceTests.All).Concat(RandomisationTests.Runtime)
        .Concat(PlayerFacialDetailTexturePolicyTests.All)
        .Concat(PlayerHairStyleCatalogTests.All)
        .Concat(PlayerFaceScarTexturePolicyTests.All)
        .Concat(Player2DaRandomisationPolicyTests.All).ToArray(),
    ["materials"] = MaterialTests.All,
    ["rendering"] = RenderingTests.All.Concat(HumanHeadShadowTests.All).Concat(NativeHairPreviewTests.All)
        .Concat(NativeAttachmentPreviewTests.All)
        .Concat(VisorLensPreviewRendererTests.All)
        .Concat(NativeKaidanAndCcHairPreviewTests.All).ToArray(),
    ["ui"] = UiSmokeTests.All.Concat(AutomationCommandTests.All).Concat(NpcHairRandomisationTests.All)
        .Concat(VisorLensAnimationLifecycleTests.All)
        .Concat(RandomisationPublicationTests.All)
        .Concat(ApplicationSettingsTests.All)
        .Concat(LocalisationTests.All)
        .Concat(TextureRegistrySettingsTests.All)
        .Concat(HairMeshManualSelectionTests.All)
        .Concat(PlayerWorkspaceReferencePolicyTests.All)
        .Concat(MorphTargetCatalogTests.All).Concat(MorphFaceCatalogTests.All)
        .Concat(CustomMeshTests.All).Concat(MaterialInterchangeTests.All)
        .Concat(Le2PlayerMaterialRonIdentityTests.All).ToArray(),
    ["package"] = PackageContextTests.All.Concat(FemaleBatarianTests.All).Concat(PackageIntegrityTests.All).Concat(CompletedPccCloneTests.All).Concat(CcScalpGraphSaveTests.All).Concat(ActorAssignmentInventoryTests.All)
        .Concat(ActorAssignmentTransactionTests.All).Concat(CustomMaterialEvidenceTests.All)
        .Concat(RonStressTests.All).Concat(InstalledAssetResolutionTests.All)
        .Concat(TextureTransferDonorPolicyTests.All)
        .Concat(SupportedMaterialGraphPolicyTests.All)
        .Concat(MaterialInstancePreservationTests.All).Concat(Le2LocalizedEyeOverrideImportRoleTests.All)
        .Concat(HeadHairTextureOwnershipTests.All).Concat(NativeHairMaterialSupportTests.All)
        .Concat(AttachmentMaterialPreviewTests.All)
        .Concat(NativeAttachmentMaterialReaderTests.All)
        .Concat(NativeDetachedCapMaterialTests.All)
        .Concat(NativeVisorLensTests.All)
        .Concat(CustomHumanMaterialCatalogTests.All)
        .Concat(MorphFaceInterchangeExportTests.All).ToArray(),
    ["c4"] = ImportedSkeletalMeshWriterTests.All
        .Concat(CustomMeshPccMaterializerTests.All)
        .Concat(CustomMeshForcedExportTests.All)
        .Concat(GltfInverseMorphTests.All)
        .Concat(PccTextureDependencyResolverTests.All)
        .ToArray(),
    ["tooling"] = LocaliserTests.All.Concat(RandomisationTests.Tooling).Concat(QaUtilityTests.All)
        .Concat(QaNpcEyeGraphCheckTests.All).Concat(QaTextureIdentityTests.All).ToArray()
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

void WriteResult(string message, ConsoleColor color, bool standardError = false)
{
    var previousColor = Console.ForegroundColor;
    try
    {
        Console.ForegroundColor = color;
        if (standardError)
        {
            Console.Error.WriteLine(message);
        }
        else
        {
            Console.WriteLine(message);
        }
    }
    finally
    {
        Console.ForegroundColor = previousColor;
    }
}

foreach (var test in tests)
{
    try
    {
        test.Run();
        WriteResult($"PASS {test.Name}", ConsoleColor.Green);
    }
    catch (TestSkippedException exception)
    {
        skipped++;
        WriteResult($"SKIP {test.Name}: {exception.Message}", ConsoleColor.Yellow);
    }
    catch (TestPartialException exception)
    {
        partial++;
        WriteResult($"PARTIAL {test.Name}: {exception.Message}", ConsoleColor.Yellow);
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
        WriteResult(failures[^1], ConsoleColor.Red, standardError: true);
    }
}

var summaryColor = failures.Count > 0
    ? ConsoleColor.Red
    : skipped > 0 || partial > 0
        ? ConsoleColor.Yellow
        : ConsoleColor.Green;

if (skipped == 0 && partial == 0)
{
    WriteResult($"{tests.Length - failures.Count}/{tests.Length} tests passed.", summaryColor);
}
else
{
    WriteResult($"{tests.Length - failures.Count - skipped - partial} passed, {partial} partial, {skipped} skipped, " +
                $"{failures.Count} failed ({tests.Length} tests).", summaryColor);
}
return failures.Count == 0 ? 0 : 1;
