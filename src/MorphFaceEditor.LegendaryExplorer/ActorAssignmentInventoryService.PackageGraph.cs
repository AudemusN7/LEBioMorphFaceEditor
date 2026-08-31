using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.Unreal;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed partial class ActorAssignmentInventoryService
{
    // Two reverse-reference hops expose factory -> type and pawn -> behavior -> type fan-out.
    private static IReadOnlyList<ActorAssignmentReference> FindReferrers(IMEPackage package, ExportEntry target)
    {
        var result = new List<ActorAssignmentReference>();
        var frontier = new HashSet<int> { target.UIndex };
        for (var depth = 0; depth < 2 && frontier.Count > 0; depth++)
        {
            var next = new HashSet<int>();
            foreach (var export in package.Exports.Where(export => export.UIndex != target.UIndex))
            {
                foreach (var (uIndex, path) in EnumerateObjectReferences(export.GetProperties()))
                {
                    if (!frontier.Contains(uIndex)) continue;
                    result.Add(new ActorAssignmentReference(
                        export.UIndex, export.ClassName, export.InstancedFullPath, path));
                    next.Add(export.UIndex);
                }
            }
            frontier = next;
        }
        return result
            .DistinctBy(reference => (reference.UIndex, reference.PropertyPath))
            .OrderBy(reference => reference.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.PropertyPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<(int UIndex, string Path)> EnumerateObjectReferences(
        PropertyCollection properties,
        string prefix = "")
    {
        foreach (var property in properties)
        {
            var path = string.IsNullOrEmpty(prefix) ? property.Name.Name : $"{prefix}.{property.Name.Name}";
            switch (property)
            {
                case ObjectProperty reference when reference.Value != 0:
                    yield return (reference.Value, path);
                    break;
                case ArrayProperty<ObjectProperty> array:
                    for (var index = 0; index < array.Count; index++)
                    {
                        if (array[index].Value != 0) yield return (array[index].Value, $"{path}[{index}]");
                    }
                    break;
                case StructProperty structure:
                    foreach (var nested in EnumerateObjectReferences(structure.Properties, path)) yield return nested;
                    break;
                case ArrayProperty<StructProperty> structures:
                    for (var index = 0; index < structures.Count; index++)
                    {
                        foreach (var nested in EnumerateObjectReferences(structures[index].Properties, $"{path}[{index}]"))
                            yield return nested;
                    }
                    break;
            }
        }
    }

    private static (ExportEntry Owner, ExportEntry Value, bool Inherited)? ResolveRelated(
        ExportEntry source,
        IReadOnlyList<string> propertyNames,
        GamePackageReferenceResolver resolver,
        PackageCache cache)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = FindProperty<ObjectProperty>(source, propertyName, resolver, cache);
            if (property?.Property.ResolveToEntry(property.Owner.FileRef) is { } entry &&
                resolver.Resolve(entry) is { } resolved)
            {
                return (property.Owner, resolved, property.Inherited);
            }
        }
        return null;
    }

    private static FoundProperty<T>? FindProperty<T>(
        ExportEntry source,
        string propertyName,
        GamePackageReferenceResolver resolver,
        PackageCache cache) where T : Property
    {
        foreach (var (owner, inherited) in EnumerateArchetypeChain(source, resolver))
        {
            if (owner.GetProperties(packageCache: cache).GetProp<T>(propertyName) is { } property)
            {
                return new FoundProperty<T>(owner, property, inherited);
            }
        }
        return null;
    }

    private static IEnumerable<(ExportEntry Export, bool Inherited)> EnumerateArchetypeChain(
        ExportEntry source,
        GamePackageReferenceResolver resolver)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExportEntry? current = source;
        var inherited = false;
        while (current is not null)
        {
            var key = $"{current.FileRef.FilePath}|{current.UIndex}|{current.InstancedFullPath}";
            if (!visited.Add(key)) yield break;
            yield return (current, inherited);
            current = resolver.Resolve(current.Archetype);
            inherited = true;
        }
    }

    private static MorphFaceGame ToMorphFaceGame(MEGame game) => game switch
    {
        MEGame.LE1 => MorphFaceGame.LE1,
        MEGame.LE2 => MorphFaceGame.LE2,
        MEGame.LE3 => MorphFaceGame.LE3,
        _ => MorphFaceGame.Unsupported
    };

    private sealed record FoundProperty<T>(ExportEntry Owner, T Property, bool Inherited) where T : Property;

    /// <summary>Lazy English TLK resolver; failed or absent installations degrade to technical identity.</summary>
    private sealed class InstalledGameNameResolver
    {
        private readonly Dictionary<MorphFaceGame, IReadOnlyList<ITalkFile>> _cache = [];

        public string? Resolve(MorphFaceGame game, int stringRef)
        {
            try
            {
                if (!_cache.TryGetValue(game, out var files))
                {
                    var leGame = game switch
                    {
                        MorphFaceGame.LE1 => MEGame.LE1,
                        MorphFaceGame.LE2 => MEGame.LE2,
                        MorphFaceGame.LE3 => MEGame.LE3,
                        _ => MEGame.Unknown
                    };
                    files = leGame == MEGame.Unknown
                        ? []
                        : TLKSystem.LoadTLKs(leGame, MELocalization.INT, male: true);
                    _cache[game] = files;
                }
                foreach (var file in files)
                {
                    var value = file.FindDataById(stringRef, returnNullIfNotFound: true, noQuotes: true);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch
            {
                // Actor identity always retains the string reference and export/type names.
            }
            return null;
        }
    }
}
