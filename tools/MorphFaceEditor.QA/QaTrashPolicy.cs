using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

/// <summary>Limits the QA-only unresolved-import exemption to an unreferenced LEC trash subtree.</summary>
internal static class QaTrashPolicy
{
    private const string TrashRoot = "ME3ExplorerTrashPackage";

    internal sealed record Classification(IReadOnlySet<int> IgnoredImportIndices, IReadOnlyList<object> Evidence);

    internal static Classification ClassifyUnreferencedTrashImports(IMEPackage package)
    {
        var trashEntries = package.Exports.Cast<IEntry>().Concat(package.Imports)
            .Where(entry => IsTrashPath(entry.InstancedFullPath))
            .ToArray();
        var trashIndices = trashEntries.Select(entry => entry.UIndex).ToHashSet();
        var trashImports = trashEntries.OfType<ImportEntry>().ToArray();
        if (trashIndices.Count == 0)
            return new Classification(trashIndices, []);

        // Parent links are outer links for both import and export entries. Entries
        // whose exact full paths are under the trash root are subtree contents, so
        // their internal ancestry is allowed. A retained entry may not be parented
        // under that subtree.
        foreach (var entry in package.Exports.Cast<IEntry>().Concat(package.Imports))
        {
            if (trashIndices.Contains(entry.UIndex)) continue;
            var visited = new HashSet<int>();
            for (var parentIndex = entry.idxLink; parentIndex != 0 && visited.Add(parentIndex);)
            {
                if (trashIndices.Contains(parentIndex))
                    throw Referenced(entry, "outer/parent", package.GetEntry(parentIndex));
                parentIndex = package.GetEntry(parentIndex)?.idxLink ?? 0;
            }
        }

        // PackageIntegrity.References is the canonical graph projection for export
        // header, property, stack, component-map, and binary UIndex edges.
        foreach (var export in package.Exports)
        {
            if (trashIndices.Contains(export.UIndex)) continue;
            foreach (var (site, targetIndex) in PackageIntegrity.References(export))
            {
                if (targetIndex != 0 && trashIndices.Contains(targetIndex))
                    throw Referenced(export, site, package.GetEntry(targetIndex));
            }
        }

        var ignored = trashImports.Select(import => (object)new
        {
            Path = import.InstancedFullPath,
            import.ClassName,
            import.UIndex,
            Role = "Ignored unresolved ME3ExplorerTrashPackage import (unreferenced subtree)"
        }).ToArray();
        return new Classification(trashImports.Select(import => import.UIndex).ToHashSet(), ignored);
    }

    internal static bool IsTrashPath(string path) =>
        path.Equals(TrashRoot, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(TrashRoot + ".", StringComparison.OrdinalIgnoreCase);

    private static InvalidDataException Referenced(IEntry source, string site, IEntry? target) =>
        new($"Retained entry #{source.UIndex} '{source.InstancedFullPath}' {site} references ME3ExplorerTrashPackage " +
            $"entry #{target?.UIndex ?? 0} '{target?.InstancedFullPath ?? "<missing>"}'.");
}
