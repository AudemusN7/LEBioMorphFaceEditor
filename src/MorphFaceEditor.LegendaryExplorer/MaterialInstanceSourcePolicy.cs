using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Instance overrides belong to their authored donor, independently of shader-graph identity.</summary>
internal static class MaterialInstanceSourcePolicy
{
    internal static bool IsInstance(IEntry entry) =>
        entry.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        entry.ClassName.Equals("MaterialInstanceTimeVarying", StringComparison.OrdinalIgnoreCase);

    internal static bool OverridesOfficialInstance(
        ExportEntry source, InstalledGameAssetResolver officialAssets, PackageCache cache) =>
        OverridesOfficialInstance(source, officialAssets, cache, new HashSet<ExportEntry>());

    private static bool OverridesOfficialInstance(
        ExportEntry source, InstalledGameAssetResolver officialAssets, PackageCache cache,
        HashSet<ExportEntry> visited)
    {
        if (!IsInstance(source)) return false;
        if (!visited.Add(source)) return false;
        var official = officialAssets.Resolve(source, cache);
        if (official is null || ReferenceEquals(source, official)) return false;
        // Compare reference identities, not package-local UIndices. Copies of an
        // unchanged stock MIC keep their stock identity; edited instance values
        // receive a new leaf name without altering their package/group ancestry.
        return !InstanceProperties(source).Equivalent(InstanceProperties(official), (left, right) =>
        {
            if (left == 0 || right == 0) return left == right;
            var authoredReference = source.FileRef.GetEntry(left);
            var officialReference = official.FileRef.GetEntry(right);
            return authoredReference is not null && officialReference is not null &&
                   authoredReference.ClassName.Equals(officialReference.ClassName, StringComparison.OrdinalIgnoreCase) &&
                   PccPackageWorkflow.CanonicalPath(authoredReference).Equals(
                       PccPackageWorkflow.CanonicalPath(officialReference), StringComparison.OrdinalIgnoreCase) &&
                   !(authoredReference is ExportEntry parentInstance && IsInstance(parentInstance) &&
                     OverridesOfficialInstance(parentInstance, officialAssets, cache, visited));
        });
    }

    private static PropertyCollection InstanceProperties(ExportEntry instance) =>
        [.. instance.GetProperties().Where(property =>
            // Official copies in different cooked packages can have different
            // baked-lighting cache keys without changing any MIC override.
            !property.Name.Name.Equals("LightingGuid", StringComparison.OrdinalIgnoreCase) &&
            !property.Name.Name.Equals("ParentLightingGuid", StringComparison.OrdinalIgnoreCase))];

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
