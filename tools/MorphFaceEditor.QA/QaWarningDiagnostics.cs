using LegendaryExplorerCore.Packages;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

internal sealed record QaWarningReview(string Original, string Message, string? AssetPath,
    string Classification, int? ExportIndex = null, int ImportCount = 0);

internal static class QaWarningDiagnostics
{
    internal static QaWarningReview[] InspectSavedPackage(string path, IReadOnlyList<string> warnings)
    {
        if (!warnings.Any(value => ImportPath(value) is not null))
            return warnings.Select(value => new QaWarningReview(value, value, null, "Unclassified production warning")).ToArray();
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        return Inspect(package, warnings);
    }

    internal static QaWarningReview[] Inspect(IMEPackage package, IEnumerable<string> warnings) =>
        warnings.Select(warning =>
        {
            var path = ImportPath(warning);
            if (path is null) return new QaWarningReview(warning, warning, null, "Unclassified production warning");
            var exports = package.Exports.Where(value => value.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            var imports = package.Imports.Count(value => value.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            // The warning text omits class. Confirm the saved class against the
            // canonical oracle before treating a path match as the expected asset.
            if (exports.Length == 1 && imports == 0 &&
                MaterialDependencyOracle.Instance.GetKind(package.Game, exports[0].ClassName, path) != MaterialOracleEntryKind.Unknown)
                return new QaWarningReview(warning,
                    $"Historical relinker warning: {warning}. Freshly reopened output contains {exports[0].ClassName} export #{exports[0].UIndex} at this exact path and no matching import; this is not an unresolved import in the saved PCC.",
                    path, "Verified export in saved output", exports[0].UIndex, imports);
            const string allowedLe2 = "BIOG_Humanoid_MASTER_MTR_R.Skin_HumanHED_SpecMulitplier_Mask";
            if (package.Game == MEGame.LE2 && path.Equals(allowedLe2, StringComparison.OrdinalIgnoreCase) &&
                exports.Length == 0 && imports == 1 && package.Imports.Any(value =>
                    value.ClassName == "Texture2D" && value.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return new QaWarningReview(warning,
                    $"Known vanilla LE2 optional specular-mask import: {warning}. The Community Patch supplies it; the PCC audit permits this exact identity.",
                    path, "Allowed optional LE2 native import", ImportCount: imports);
            return new QaWarningReview(warning, warning, path, "Unreconciled production import warning", ImportCount: imports);
        }).ToArray();

    private static string? ImportPath(string warning)
    {
        const string prefix = "Import ";
        const string suffix = " could not be resolved";
        if (!warning.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = warning.IndexOf(suffix, prefix.Length, StringComparison.Ordinal);
        return end > prefix.Length ? warning[prefix.Length..end] : null;
    }
}
