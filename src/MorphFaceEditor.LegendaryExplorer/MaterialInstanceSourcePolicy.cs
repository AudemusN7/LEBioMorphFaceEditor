using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using System.Globalization;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Instance overrides belong to their authored donor, independently of shader-graph identity.</summary>
internal static class MaterialInstanceSourcePolicy
{
    internal static bool IsInstance(IEntry entry) =>
        entry.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        entry.ClassName.Equals("MaterialInstanceTimeVarying", StringComparison.OrdinalIgnoreCase);

    internal static bool OverridesOfficialInstance(
        ExportEntry source, InstalledGameAssetResolver officialAssets, PackageCache cache)
    {
        if (!IsInstance(source)) return false;
        using var comparisons = new InstanceComparisonCache(cache) { CacheMaxSize = 64 };
        return OverridesOfficialInstance(source, officialAssets, comparisons, new HashSet<ExportEntry>());
    }

    // Equality needs complete instance properties and reference identities, not
    // the surrounding PCC's mesh/texture data. Keep these partial readers out of
    // the caller's materialisation cache and alive through parent/diagnostic checks.
    private sealed class InstanceComparisonCache(PackageCache sourceCache) : PackageCache
    {
        public override IMEPackage GetCachedPackage(string packagePath, bool openIfNotInCache = true,
            Func<string, IMEPackage>? openPackageMethod = null)
        {
            PackageSourceCache.Verify(sourceCache, packagePath);
            // Borrow without inserting: disposing this cache must not release
            // a full reader still owned by the save/export operation.
            return sourceCache.GetCachedPackage(packagePath, openIfNotInCache: false)
                ?? base.GetCachedPackage(packagePath, openIfNotInCache,
                    static path => MEPackageHandler.UnsafePartialLoad(path, IsInstance));
        }
    }

    private static bool OverridesOfficialInstance(
        ExportEntry source, InstalledGameAssetResolver officialAssets, PackageCache cache,
        HashSet<ExportEntry> visited)
    {
        if (!IsInstance(source)) return false;
        if (!visited.Add(source)) return false;
        var officialVariants = officialAssets.ResolveOfficialVariants(
            source.Game, PccAssetPathPolicy.FromExport(source), source.ClassName, cache);

        var sourceProperties = InstanceProperties(source);
        // Compare reference identities, not package-local UIndices. Copies of an
        // unchanged stock MIC keep their stock identity if they match any one
        // complete official cooked variant. Each candidate gets its own cycle
        // guard so one package's parent chain cannot contaminate another's.
        ExportEntry? diagnosticOfficial = null;
        foreach (var official in officialVariants)
        {
            if (ReferenceEquals(source, official)) return false;
            var candidateVisited = new HashSet<ExportEntry>(visited);
            var officialProperties = InstanceProperties(official);
            var equivalent = sourceProperties.Equivalent(officialProperties, (left, right) =>
                ReferencesEquivalent(source, official, left, right, officialAssets, cache, candidateVisited));
            if (equivalent) return false;
            diagnosticOfficial ??= official;
        }

        if (diagnosticOfficial is null) return false;

        if (MaterialInstanceComparisonObserver.IsActive)
        {
            IReadOnlyList<MaterialInstancePropertyDifference> differences;
            using (MaterialInstanceComparisonObserver.Suppress())
            {
                differences = FindDifferences(source, diagnosticOfficial, sourceProperties,
                    InstanceProperties(diagnosticOfficial),
                    officialAssets, cache);
            }
            MaterialInstanceComparisonObserver.Publish(new(
                Identity(source), ToMorphGame(source.Game),
                Identity(diagnosticOfficial), ToMorphGame(diagnosticOfficial.Game),
                differences));
        }
        return true;
    }

    internal static IDisposable ObserveComparisons(Action<MaterialInstanceComparisonDiagnostic> callback) =>
        MaterialInstanceComparisonObserver.Observe(callback);

    private static bool ReferencesEquivalent(
        ExportEntry source, ExportEntry official, int left, int right,
        InstalledGameAssetResolver officialAssets, PackageCache cache, HashSet<ExportEntry> visited)
    {
        if (left == 0 || right == 0) return left == right;
        var authoredReference = source.FileRef.GetEntry(left);
        var officialReference = official.FileRef.GetEntry(right);
        return authoredReference is not null && officialReference is not null &&
               authoredReference.ClassName.Equals(officialReference.ClassName, StringComparison.OrdinalIgnoreCase) &&
               PccPackageWorkflow.CanonicalPath(authoredReference).Equals(
                   PccPackageWorkflow.CanonicalPath(officialReference), StringComparison.OrdinalIgnoreCase) &&
               !(authoredReference is ExportEntry parentInstance && IsInstance(parentInstance) &&
                 OverridesOfficialInstance(parentInstance, officialAssets, cache,
                     new HashSet<ExportEntry>(visited)));
    }

    private static IReadOnlyList<MaterialInstancePropertyDifference> FindDifferences(
        ExportEntry source, ExportEntry official,
        PropertyCollection sourceProperties, PropertyCollection officialProperties,
        InstalledGameAssetResolver officialAssets, PackageCache cache)
    {
        var names = sourceProperties.Select(property => property.Name.Instanced)
            .Concat(officialProperties.Select(property => property.Name.Instanced))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        var differences = new List<MaterialInstancePropertyDifference>();
        foreach (var name in names)
        {
            var sourceProperty = sourceProperties.FirstOrDefault(property =>
                property.Name.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase));
            var officialProperty = officialProperties.FirstOrDefault(property =>
                property.Name.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase));
            var equivalent = sourceProperty is not null && officialProperty is not null &&
                new PropertyCollection { sourceProperty }.Equivalent(
                    new PropertyCollection { officialProperty }, (left, right) =>
                        ReferencesEquivalent(source, official, left, right, officialAssets, cache,
                            new HashSet<ExportEntry> { source }));
            if (!equivalent)
            {
                differences.Add(new(name,
                    sourceProperty is null ? "<absent>" : FormatValue(sourceProperty, source.FileRef),
                    officialProperty is null ? "<absent>" : FormatValue(officialProperty, official.FileRef)));
            }
        }
        return differences;
    }

    private static AssetIdentity Identity(ExportEntry instance) => new(
        instance.FileRef.FilePath, PccPackageWorkflow.CanonicalPath(instance), instance.UIndex,
        instance.ClassName, IsImport: false);

    private static MorphFaceGame ToMorphGame(MEGame game) => game switch
    {
        MEGame.LE1 => MorphFaceGame.LE1,
        MEGame.LE2 => MorphFaceGame.LE2,
        MEGame.LE3 => MorphFaceGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, "MIC comparison has an unsupported game.")
    };

    private static string FormatValue(Property property, IMEPackage package) => property switch
    {
        ObjectProperty value => FormatReference(package, value.Value),
        StructProperty value => $"{value.StructType} {{{string.Join(", ", value.Properties.Select(child => $"{child.Name.Instanced}={FormatValue(child, package)}"))}}}",
        ArrayProperty<StructProperty> value => "[" + string.Join(" | ", value.Select(item => FormatValue(item, package))) + "]",
        ArrayProperty<ObjectProperty> value => "[" + string.Join(" | ", value.Select(item => FormatValue(item, package))) + "]",
        ArrayProperty<FloatProperty> value => "[" + string.Join(", ", value.Select(item => item.Value.ToString("R", CultureInfo.InvariantCulture))) + "]",
        ArrayProperty<IntProperty> value => "[" + string.Join(", ", value.Select(item => item.Value.ToString(CultureInfo.InvariantCulture))) + "]",
        NameProperty value => value.Value.Instanced,
        EnumProperty value => value.Value.Instanced,
        BoolProperty value => value.Value.ToString(),
        FloatProperty value => value.Value.ToString("R", CultureInfo.InvariantCulture),
        IntProperty value => value.Value.ToString(CultureInfo.InvariantCulture),
        ByteProperty value => value.Value.ToString(CultureInfo.InvariantCulture),
        StrProperty value => value.Value,
        _ => property.ToString() ?? property.GetType().Name
    };

    private static string FormatReference(IMEPackage package, int uindex)
    {
        if (uindex == 0) return "<null>";
        var entry = package.GetEntry(uindex);
        if (entry is null) return $"<unresolved UIndex {uindex}>";
        var role = entry is ExportEntry ? "export" : "import";
        return $"{entry.ClassName}|{PccPackageWorkflow.CanonicalPath(entry)}|{role}|{Path.GetFileName(entry.FileRef.FilePath)}";
    }

    private static PropertyCollection InstanceProperties(ExportEntry instance)
    {
        PropertyCollection properties = [.. instance.GetProperties().Where(property =>
            // Official copies in different cooked packages can have different
            // baked-lighting cache keys without changing any MIC override.
            !property.Name.Name.Equals("LightingGuid", StringComparison.OrdinalIgnoreCase) &&
            !property.Name.Name.Equals("ParentLightingGuid", StringComparison.OrdinalIgnoreCase) &&
            !property.Name.Name.Equals("m_Guid", StringComparison.OrdinalIgnoreCase))];

        foreach (var name in new[] { "TextureParameterValues", "VectorParameterValues", "ScalarParameterValues" })
        {
            if (properties.GetProp<ArrayProperty<StructProperty>>(name) is not { } parameters) continue;
            // LEX creates dynamic MIC parameters with zero ExpressionGUIDs. Compare
            // their authored names/values independently of these bookkeeping IDs.
            var comparisonParameters = parameters.DeepClone();
            foreach (var parameter in comparisonParameters)
            {
                parameter.Properties.RemoveAll(property =>
                    property.Name.Name.Equals("ExpressionGUID", StringComparison.OrdinalIgnoreCase));
            }
            // These arrays are maps keyed by ParameterName. Unreal does not
            // attach meaning to their order; sort by that key while keeping
            // duplicate-name entries stable so duplicate ordering remains data.
            var orderedParameters = new ArrayProperty<StructProperty>(
                comparisonParameters
                    .Select((parameter, index) => (parameter, index,
                        name: parameter.GetProp<NameProperty>("ParameterName")?.Value.Instanced ?? string.Empty))
                    .OrderBy(item => item.name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.index)
                    .Select(item => item.parameter)
                    .ToArray(), name);
            properties.AddOrReplaceProp(orderedParameters);
        }
        return properties;
    }

    internal static string UniqueInstancePath(IMEPackage destination, string originalPath)
    {
        for (var suffix = 1; ; suffix++)
        {
            var candidate = originalPath + (suffix == 1 ? "_MFE" : $"_MFE{suffix}");
            if (!destination.Exports.Cast<IEntry>().Concat(destination.Imports)
                    .Any(entry => entry.InstancedFullPath.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }
}
