using MorphFaceEditor.Tests;

if (args.Length >= 3 && args[0].Equals("--d4-interrupted-save-worker", StringComparison.OrdinalIgnoreCase))
    return PackageIntegrityTests.RunInterruptedSaveWorker(args[1], args[2]);

var list = false;
string? suiteName = null;
var filters = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    switch (args[index].ToLowerInvariant())
    {
        case "--help":
            Console.WriteLine("MFE tests: [--suite <name|all>] [--list] [name-or-class filters...]");
            Console.WriteLine("Suites: " + string.Join(", ", TestCatalog.Suites.Keys));
            Console.WriteLine("Default: core. --list without a suite lists everything. Filters match names or classes (OR).");
            return 0;
        case "--list": list = true; break;
        case "--suite":
            if (suiteName is not null || ++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Specify --suite once, followed by a suite name. Use --help for usage.");
                return 2;
            }
            suiteName = args[index];
            break;
        default:
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Unknown option '{args[index]}'. Use --help for usage.");
                return 2;
            }
            filters.Add(args[index]);
            break;
    }
}

suiteName ??= list ? "all" : "core";
if (!suiteName.Equals("all", StringComparison.OrdinalIgnoreCase) && !TestCatalog.Suites.ContainsKey(suiteName))
{
    Console.Error.WriteLine($"Unknown suite '{suiteName}'. Use --help for valid suites.");
    return 2;
}
var selected = TestCatalog.Suites.Where(suite => suiteName.Equals("all", StringComparison.OrdinalIgnoreCase) ||
    suite.Key.Equals(suiteName, StringComparison.OrdinalIgnoreCase));
var groups = selected.Select(suite => (Name: suite.Key, Cases: suite.Value.Where(test => filters.Count == 0 ||
    filters.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        test.Owner.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray())).ToArray();
var tests = groups.SelectMany(group => group.Cases).ToArray();
if (tests.Length == 0)
{
    Console.Error.WriteLine($"No tests matched suite '{suiteName}'.");
    return 2;
}
if (list)
{
    foreach (var group in groups.Where(group => group.Cases.Length > 0))
    {
        Console.WriteLine($"{group.Name} ({group.Cases.Length})");
        foreach (var test in group.Cases) Console.WriteLine($"  {test.Owner}: {test.Name}");
    }
    return 0;
}

// Run repository-relative fixtures from the checkout even when the executable
// was invoked elsewhere; never copy the game corpus into build outputs.
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "MorphFaceEditor.slnx"))) root = root.Parent;
if (root is not null) Directory.SetCurrentDirectory(root.FullName);

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
        failures.Add($"FAIL {test.Name}: {exception}");
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
