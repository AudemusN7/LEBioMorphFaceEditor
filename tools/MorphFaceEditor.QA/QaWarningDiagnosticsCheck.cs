using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace MorphFaceEditor.QA;

internal static class QaWarningDiagnosticsCheck
{
    internal static void Run(QaContext c)
    {
        const string asset = "BIOG_Humanoid_EYE_OVRD.HMM_EYE_MASTER_OVRD_MAT_USER";
        var warning = $"Import {asset} could not be resolved - cannot be safely used";
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("QaWarnings.pcc", MEGame.LE2);
        var root = package.CreatePackageExport("BIOG_Humanoid_EYE_OVRD");
        var material = package.CreateExport("HMM_EYE_MASTER_OVRD_MAT_USER", "RvrEffectsMaterialUser", root, indexed: false);
        var verified = QaWarningDiagnostics.Inspect(package, [warning]).Single();
        QaContext.Require(verified.Classification == "Verified export in saved output" &&
            verified.ExportIndex == material.UIndex && verified.Original == warning,
            "Saved-export warning reconciliation lost the exact export evidence or original warning.");
        c.AddWarningReviews("Self-check saved-export warning", System.IO.Path.Combine(c.Output, "synthetic-package.pcc"), [verified]);
        QaContext.Require(c.Current.Notes.Count == 1 && c.Current.Warnings.Count == 0,
            "Verified historical relinker evidence was reported as an unresolved current-output warning.");
        var other = QaWarningDiagnostics.Inspect(package,
            ["Import OtherPackage.HMM_EYE_MASTER_OVRD_MAT_USER could not be resolved"]).Single();
        QaContext.Require(other.Classification == "Unreconciled production import warning",
            "Warning reconciliation used a basename fallback.");
        using var wrongClassPackage = MEPackageHandler.CreateMemoryEmptyPackage("QaWarningWrongClass.pcc", MEGame.LE2);
        var wrongClassRoot = wrongClassPackage.CreatePackageExport("BIOG_Humanoid_EYE_OVRD");
        wrongClassPackage.CreateExport("HMM_EYE_MASTER_OVRD_MAT_USER", "MaterialInstanceConstant", wrongClassRoot, indexed: false);
        QaContext.Require(QaWarningDiagnostics.Inspect(wrongClassPackage, [warning]).Single().Classification ==
            "Unreconciled production import warning", "A path match with the wrong canonical class concealed an import warning.");
        // CreateImport intentionally returns an existing export; construct the
        // malformed duplicate directly so this regression really contains both roles.
        package.AddImport(new ImportEntry(package, root, "HMM_EYE_MASTER_OVRD_MAT_USER")
        {
            ClassName = "RvrEffectsMaterialUser", PackageFile = "Engine"
        });
        QaContext.Require(QaWarningDiagnostics.Inspect(package, [warning]).Single().Classification ==
            "Unreconciled production import warning", "A matching import was concealed by a duplicate export.");
    }
}
