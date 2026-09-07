using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Checks the relink map against the final property and binary slots. A numerically valid
/// UIndex is insufficient: it must identify the dependency selected by the relinker.
/// </summary>
internal static class MaterialisationVerifier
{
    internal static void Relink(RelinkerOptionsPackage options)
    {
        try { Relinker.RelinkAll(options); }
        catch (Exception exception)
        {
            var rootPair = options.CrossPackageMap.FirstOrDefault(pair => pair.Key is ExportEntry && pair.Value is ExportEntry);
            if (rootPair.Value is ExportEntry root)
            {
                foreach (var report in options.RelinkReport)
                    MaterialisationDiagnostics.Observer.Value?.Invoke(new(rootPair.Key.Game, root.Game, root.ClassName,
                        root.InstancedFullPath, report.Entry?.InstancedFullPath, report.Entry?.ClassName,
                        report.Message, "unresolved", MaterialisationIssueSeverity.Fatal, false));
                MaterialisationDiagnostics.Observer.Value?.Invoke(new(rootPair.Key.Game, root.Game, root.ClassName,
                    root.InstancedFullPath, root.InstancedFullPath, root.ClassName,
                    exception.Message, "unresolved", MaterialisationIssueSeverity.Fatal, false));
            }
            throw new InvalidDataException("Required relink failed: " + exception.Message, exception);
        }
    }

    internal static void Verify(ExportEntry root, MEGame sourceGame, RelinkerOptionsPackage options,
        ICollection<string>? warnings = null)
    {
        var issues = new List<MaterialisationDiagnostic>();
        var checkedEntries = new HashSet<IEntry>();
        var prunedEntries = new HashSet<IEntry>();
        try
        {
            if (ObjectBinary.From(root) is null && root.ClassName is "SkeletalMesh" or "Texture2D")
                throw new InvalidDataException($"Cannot parse materialised {root.ClassName} '{root.InstancedFullPath}'.");

            foreach (var pair in options.CrossPackageMap)
            {
                if (pair.Key is not ExportEntry source || pair.Value is not ExportEntry target ||
                    !ReferenceEquals(target.FileRef, root.FileRef)) continue;
                var original = PackageIntegrity.References(source);
                var final = PackageIntegrity.References(target);
                foreach (var reference in final)
                {
                    if (!original.TryGetValue(reference.Key, out var originalIndex))
                    {
                        if (reference.Value != 0)
                            throw new InvalidDataException($"'{target.InstancedFullPath}' gained an unverified reference at {reference.Key}.");
                        continue;
                    }
                    if (originalIndex == 0)
                    {
                        if (reference.Value != 0)
                            throw new InvalidDataException($"'{target.InstancedFullPath}' changed a null reference at {reference.Key}.");
                        continue;
                    }
                    var donor = source.FileRef.GetEntry(originalIndex)
                        ?? throw new InvalidDataException($"'{source.InstancedFullPath}' has an unresolved donor reference at {reference.Key} ({originalIndex}).");
                    var expected = options.CrossPackageMap.TryGetValue(donor, out var mapped)
                        ? mapped : root.FileRef.FindEntry(donor.InstancedFullPath, donor.ClassName);
                    var actual = root.FileRef.GetEntry(reference.Value);
                    if (expected is null || actual is null || !ReferenceEquals(expected.FileRef, root.FileRef) ||
                        actual.UIndex != expected.UIndex || actual.ClassName != donor.ClassName)
                        throw new InvalidDataException($"Required relink failed for '{target.InstancedFullPath}' {reference.Key}: " +
                            $"expected {donor.ClassName} '{donor.InstancedFullPath}', got " +
                            $"{actual?.ClassName ?? "None"} '{actual?.InstancedFullPath ?? "None"}' ({reference.Value}).");
                }
                var removed = original.Keys.Except(final.Keys).ToArray();
                if (removed.Length > 0)
                {
                    // Only LEC's cross-game property pruning may remove slots implicitly.
                    if (source.Game == target.Game || removed.Any(key => !key.StartsWith("property.", StringComparison.Ordinal)))
                        throw new InvalidDataException($"'{target.InstancedFullPath}' lost required reference slots: {string.Join(", ", removed)}.");
                    prunedEntries.Add(target);
                }
                checkedEntries.Add(target);
            }
            if (!checkedEntries.Contains(root))
                throw new InvalidDataException($"Materialised root '{root.InstancedFullPath}' is absent from the verified relink map.");

            foreach (var report in options.RelinkReport)
            {
                var affected = report.Entry;
                if (affected is not null && options.CrossPackageMap.TryGetValue(affected, out var mapped)) affected = mapped;
                // This is the exact informational message produced by the pinned LEC build,
                // admitted only with its associated, verified cross-game destination export.
                var pruningNotice = affected is ExportEntry entry && checkedEntries.Contains(entry) &&
                    sourceGame != root.Game && report.Message ==
                    $"{entry.UIndex} {entry.InstancedFullPath}: Some properties were removed from this object because they do not exist in {entry.Game}!";
                var severity = pruningNotice ? MaterialisationIssueSeverity.Warning : MaterialisationIssueSeverity.Fatal;
                issues.Add(new(sourceGame, root.Game, root.ClassName, root.InstancedFullPath,
                    affected?.InstancedFullPath, affected?.ClassName, report.Message,
                    pruningNotice ? "removed incompatible properties; retained references verified" :
                    checkedEntries.Contains(affected!) ? "retained" : "unresolved", severity, pruningNotice));
            }
            if (prunedEntries.Any(entry => !issues.Any(issue => issue.EntryPath == entry.InstancedFullPath &&
                    issue.Severity == MaterialisationIssueSeverity.Warning)))
                throw new InvalidDataException($"Materialised '{root.InstancedFullPath}' lost reference properties without a confirmed pruning report.");
        }
        catch (Exception exception)
        {
            foreach (var report in options.RelinkReport.Where(report => !issues.Any(issue => issue.Message == report.Message)))
                issues.Add(new(sourceGame, root.Game, root.ClassName, root.InstancedFullPath,
                    report.Entry?.InstancedFullPath, report.Entry?.ClassName, report.Message, "unresolved",
                    MaterialisationIssueSeverity.Fatal, false));
            issues.Add(new(sourceGame, root.Game, root.ClassName, root.InstancedFullPath,
                root.InstancedFullPath, root.ClassName, exception.Message, "unresolved",
                MaterialisationIssueSeverity.Fatal, false));
        }
        foreach (var issue in issues) MaterialisationDiagnostics.Observer.Value?.Invoke(issue);
        var fatal = issues.Where(issue => issue.Severity == MaterialisationIssueSeverity.Fatal).ToArray();
        if (fatal.Length > 0)
            throw new InvalidDataException($"Materialisation of '{root.InstancedFullPath}' failed verification: " +
                string.Join("; ", fatal.Select(issue => issue.Message)));
        foreach (var issue in issues) warnings?.Add(issue.Message);
        MaterialisationDiagnostics.Observer.Value?.Invoke(new(sourceGame, root.Game, root.ClassName,
            root.InstancedFullPath, root.InstancedFullPath, root.ClassName, "Materialised root verified",
            "retained", MaterialisationIssueSeverity.Warning, true));
    }
}
