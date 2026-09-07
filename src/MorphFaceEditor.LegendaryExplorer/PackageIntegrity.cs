using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Shared export hierarchy and reference checks performed before a transaction is installed.</summary>
internal static class PackageIntegrity
{
    // Existing-package edits preserve unrelated source quirks. Include the table index in
    // each issue identity so a newly added duplicate cannot inherit an older exemption.
    internal static IReadOnlySet<string> CaptureIssues(IMEPackage package) => CollectIssues(package).ToHashSet();

    internal static ExportEntry? EnsurePackagePath(IMEPackage package, string assetPath, string? assetClass = null)
    {
        // Check the entire path before creating anything: import promotion needs its own proven relink policy.
        var paths = assetPath.Split('.').SkipLast(1).ToArray();
        var path = string.Empty;
        foreach (var segment in paths)
        {
            path = path.Length == 0 ? segment : $"{path}.{segment}";
            if (package.FindEntry(path, "Package") is ImportEntry)
                throw new InvalidDataException($"Cannot materialise '{assetPath}': import ancestor or asset '{path}' occupies the required export identity.");
        }
        if (assetClass is not null && package.FindEntry(assetPath, assetClass) is ImportEntry)
            throw new InvalidDataException($"Cannot materialise '{assetPath}': an import occupies the required {assetClass} export identity.");
        ExportEntry? parent = null;
        path = string.Empty;
        foreach (var segment in paths)
        {
            path = path.Length == 0 ? segment : $"{path}.{segment}";
            var existing = package.FindEntry(path, "Package");
            parent = existing as ExportEntry ?? package.CreatePackageExport(NameReference.FromInstancedString(segment), parent);
        }
        return parent;
    }

    internal static void Verify(IMEPackage package, IReadOnlySet<string>? allowedExistingIssues = null)
    {
        var issues = CollectIssues(package);
        var introduced = allowedExistingIssues is null
            ? issues
            : issues.Where(issue => !allowedExistingIssues.Contains(issue)).ToArray();
        if (introduced.Count > 0)
            throw new InvalidDataException("Package integrity verification failed: " + string.Join("; ", introduced));
    }

    private static IReadOnlyList<string> CollectIssues(IMEPackage package)
    {
        // Full-path formatting and LEC's duplicate checker both walk parents. Validate
        // every chain (including imports) before either can encounter a cycle.
        foreach (var entry in package.Exports.Cast<IEntry>().Concat(package.Imports))
        {
            var visited = new HashSet<int> { entry.UIndex };
            for (var link = entry.idxLink; link != 0; link = package.GetEntry(link)?.idxLink ?? 0)
                if (!visited.Add(link))
                    throw new InvalidDataException($"Entry #{entry.UIndex} has a circular parent chain.");
        }
        var issues = new List<string>();
        foreach (var export in package.Exports)
        {
            for (var link = export.idxLink; link != 0;)
            {
                if (package.GetEntry(link) is not ExportEntry parent)
                {
                    issues.Add($"Export {Identity(export)} has import or missing parent #{link} {Identity(package.GetEntry(link))}.");
                    break;
                }
                link = parent.idxLink;
            }
        }
        var duplicates = EntryChecker.CheckForDuplicateIndices(package);
        issues.AddRange(duplicates.Select(value => $"Duplicate entry identity {Identity(value.Entry)}."));
        foreach (var export in package.Exports)
            foreach (var reference in References(export))
                if (reference.Value != 0 && !package.IsEntry(reference.Value))
                    issues.Add($"Export {Identity(export)} {reference.Key} references missing entry {reference.Value}.");
        return issues;
    }

    private static string Identity(IEntry? entry) => entry is null
        ? "<missing>"
        : $"#{entry.UIndex} {entry.ClassName} '{entry.InstancedFullPath}'";

    internal static Dictionary<string, int> References(ExportEntry export)
    {
        var result = new Dictionary<string, int>
        {
            ["header.class"] = export.idxClass,
            ["header.super"] = export.idxSuperClass,
            ["header.archetype"] = export.idxArchetype
        };
        ReadProperties(export.GetProperties(), "property", result);
        // LEC also relinks references preceding the property stream and in the component map.
        var preProperties = export.GetPrePropBinary();
        if (export.HasStack)
        {
            result.Add("stack.node", LegendaryExplorerCore.Gammtek.IO.EndianReader.ToInt32(preProperties, 0, export.FileRef.Endian));
            result.Add("stack.state", LegendaryExplorerCore.Gammtek.IO.EndianReader.ToInt32(preProperties, 4, export.FileRef.Endian));
        }
        else if (export.TemplateOwnerClassIdx is var templateOffset && templateOffset >= 0)
            result.Add("template.owner", LegendaryExplorerCore.Gammtek.IO.EndianReader.ToInt32(preProperties, templateOffset, export.FileRef.Endian));
        if (export.HasComponentMap)
        {
            var slot = 0;
            foreach (var component in export.ComponentMap)
                result.Add($"component[{slot++}]", component.Value + 1);
        }
        var binary = ObjectBinary.From(export);
        binary?.ForEachUIndex(export.Game, new ReferenceCollector(result));
        return result;
    }

    private static void ReadProperties(IEnumerable<Property> properties, string prefix, Dictionary<string, int> result)
    {
        foreach (var property in properties)
        {
            var key = $"{prefix}.{property.Name.Instanced}[{property.StaticArrayIndex}]";
            switch (property)
            {
                case ObjectProperty reference:
                    result.Add(key, reference.Value);
                    break;
                case StructProperty structure:
                    ReadProperties(structure.Properties, key, result);
                    break;
                case ArrayProperty<ObjectProperty> array:
                    for (var i = 0; i < array.Count; i++) result.Add($"{key}[{i}]", array[i].Value);
                    break;
                case ArrayProperty<StructProperty> array:
                    for (var i = 0; i < array.Count; i++) ReadProperties(array[i].Properties, $"{key}[{i}]", result);
                    break;
                case DelegateProperty callback:
                    result.Add(key, callback.Value.ContainingObjectUIndex);
                    break;
            }
        }
    }

    private readonly struct ReferenceCollector(Dictionary<string, int> result) : IUIndexAction
    {
        public void Invoke(ref int uIndex, string propName) => result.Add($"binary.{propName}", uIndex);
    }
}
