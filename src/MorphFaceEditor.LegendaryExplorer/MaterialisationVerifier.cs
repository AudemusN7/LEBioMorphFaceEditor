using LegendaryExplorerCore.Misc;
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
    internal static void Relink(
        RelinkerOptionsPackage options,
        IReadOnlySet<ExportEntry>? preexistingExports = null,
        IReadOnlyDictionary<ExportEntry, (ExportEntry Source, string TargetPath)>? stagedCopies = null)
    {
        try
        {
            // A graph can contain multiple source identities that intentionally
            // resolve to the same destination export. Relinking each alias would
            // process the destination binary more than once: after the first pass
            // its UIndices are destination indices, so a second pass interprets
            // them as donor indices and corrupts otherwise valid references.
            // Process each destination once, using its recorded staging donor
            // when available. An alias may appear before that donor in the map.
            // The map may grow while Relinker adds imports, so keep an ordered
            // worklist and append newly added mappings as they arrive.
            var processedTargets = new HashSet<ExportEntry>();
            var processedSources = new HashSet<IEntry>();
            var pendingSources = GetPendingSources(options.CrossPackageMap);
            var pendingIndex = 0;
            var refreshPendingSources = false;
            void OnMappingChanged(object sender, DictionaryChangedEvent<IEntry, IEntry> change)
            {
                if (change.Type == DictChangeType.AddItem)
                {
                    if (change.Key is ExportEntry && change.Value is ExportEntry)
                        pendingSources.Add(change.Key);
                }
                else
                {
                    // Changes/removals are uncommon in the relinker path. Refresh
                    // after the active Relink call so order matches a fresh map walk.
                    refreshPendingSources = true;
                }
            }

            options.CrossPackageMap.OnDictionaryChanged += OnMappingChanged;
            try
            {
                while (true)
                {
                    if (refreshPendingSources)
                    {
                        pendingSources = GetPendingSources(options.CrossPackageMap, processedSources);
                        pendingIndex = 0;
                        refreshPendingSources = false;
                    }
                    if (pendingIndex >= pendingSources.Count)
                        break;

                    var sourceEntry = pendingSources[pendingIndex++];
                    if (sourceEntry is not ExportEntry source ||
                        !options.CrossPackageMap.TryGetValue(sourceEntry, out var currentTarget) ||
                        currentTarget is not ExportEntry target ||
                        !processedSources.Add(sourceEntry))
                        continue;
                    if (!processedTargets.Add(target))
                        continue;
                    if (preexistingExports?.Contains(target) == true)
                        continue;
                    if (stagedCopies?.TryGetValue(target, out var stagedCopy) == true)
                    {
                        source = stagedCopy.Source;
                        if (!options.CrossPackageMap.TryGetValue(source, out var stagedTarget) ||
                            !ReferenceEquals(stagedTarget, target))
                            throw new InvalidDataException(
                                $"The recorded donor '{source.InstancedFullPath}' is not bound to its staged export.");
                    }
                    Relinker.Relink(source, target, options);
                }
            }
            finally
            {
                options.CrossPackageMap.OnDictionaryChanged -= OnMappingChanged;
            }
        }
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

    private static List<IEntry> GetPendingSources(
        IDictionary<IEntry, IEntry> mappings,
        IReadOnlySet<IEntry>? processedSources = null) =>
        mappings.Where(pair => pair.Key is ExportEntry && pair.Value is ExportEntry &&
                               processedSources?.Contains(pair.Key) != true)
            .Select(pair => pair.Key)
            .ToList();

    internal static void Verify(ExportEntry root, MEGame sourceGame, RelinkerOptionsPackage options,
        ICollection<string>? warnings = null, bool rootOnly = false,
        IReadOnlyDictionary<ExportEntry, (ExportEntry Source, string TargetPath)>? stagedCopies = null,
        IReadOnlySet<ExportEntry>? preservedExports = null)
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
                    !ReferenceEquals(target.FileRef, root.FileRef) ||
                    rootOnly && !ReferenceEquals(target, root) || preservedExports?.Contains(target) == true) continue;

                (ExportEntry Source, string TargetPath)? stagedCopy = null;
                if (stagedCopies?.TryGetValue(target, out var recordedCopy) == true)
                {
                    stagedCopy = recordedCopy;
                }
                if (stagedCopy is { } provenance)
                {
                    if (!target.InstancedFullPath.Equals(provenance.TargetPath, StringComparison.OrdinalIgnoreCase) ||
                        !target.ClassName.Equals(provenance.Source.ClassName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Staged export '{target.InstancedFullPath}' no longer matches its recorded donor identity " +
                            $"'{provenance.Source.ClassName} {provenance.TargetPath}'.");
                    }
                    if (!options.CrossPackageMap.TryGetValue(provenance.Source, out var stagedTarget) ||
                        !ReferenceEquals(stagedTarget, target))
                    {
                        throw new InvalidDataException(
                            $"The recorded donor '{provenance.Source.InstancedFullPath}' is not bound to its staged export.");
                    }
                    if (!SameCanonicalAssetIdentity(source, provenance.Source))
                    {
                        throw new InvalidDataException(
                            $"Source alias '{source.ClassName} {source.InstancedFullPath}' does not identify the staged donor " +
                            $"'{provenance.Source.ClassName} {provenance.Source.InstancedFullPath}'.");
                    }
                    if (!ReferenceEquals(source, provenance.Source))
                    {
                        // The alias is checked above; the destination's content and
                        // dependency graph are checked only against the donor that
                        // EntryImporter actually copied into this target.
                        continue;
                    }
                }
                if (stagedCopy is not null && !checkedEntries.Add(target))
                {
                    continue;
                }
                checkedEntries.Add(target);
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
                    var hasMappedEntry = options.CrossPackageMap.TryGetValue(donor, out var mapped);
                    var expected = hasMappedEntry
                        ? mapped : root.FileRef.FindEntry(donor.InstancedFullPath, donor.ClassName);
                    var actual = root.FileRef.GetEntry(reference.Value);
                    var exactCanonicalEquivalent = !hasMappedEntry && actual is not null &&
                                                   actual.ClassName == donor.ClassName &&
                                                   HasExactPackageQualifiedIdentity(donor, actual);
                    if (actual is null || actual.ClassName != donor.ClassName ||
                        (!exactCanonicalEquivalent &&
                         (expected is null || !ReferenceEquals(expected.FileRef, root.FileRef) ||
                          actual.UIndex != expected.UIndex)))
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

    private static bool HasExactPackageQualifiedIdentity(IEntry donor, IEntry actual)
    {
        var donorPath = donor.InstancedFullPath;
        var actualPath = actual.InstancedFullPath;
        if (donorPath.Equals(actualPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var donorPackage = Path.GetFileNameWithoutExtension(donor.FileRef.FilePath);
        if (string.IsNullOrWhiteSpace(donorPackage))
        {
            return false;
        }
        var prefix = donorPackage + ".";
        return actualPath.Equals(prefix + donorPath, StringComparison.OrdinalIgnoreCase) ||
               donorPath.Equals(prefix + actualPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameCanonicalAssetIdentity(IEntry alias, ExportEntry donor)
    {
        if (!alias.ClassName.Equals(donor.ClassName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (alias is ExportEntry aliasExport &&
            PccAssetPathPolicy.FromExport(aliasExport).Equals(
                PccAssetPathPolicy.FromExport(donor), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return HasExactPackageQualifiedIdentity(alias, donor);
    }
}
