using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Core.Materials;
using System.Text.Json;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private static async Task PackageAudit(QaContext c)
    {
        // Standalone execution still creates a real new output to prove the audit route.
        var source = c.Source();
        var preview = await c.Load(source.PackagePath, source.FacePath);
        await c.Save(preview.Loaded.Document, source.PackagePath, "audit-control.pcc");
        var auditDirectory = c.AuditOutputDirectory ?? c.Output;
        if (c.AuditOutputDirectory is not null) RestoreBaselines(c, auditDirectory);
        c.Current.Evidence["auditedDirectory"] = auditDirectory;
        var files = Directory.EnumerateFiles(auditDirectory, "*.pcc", SearchOption.AllDirectories).Order().ToArray();
        var audits = new List<object>();
        var failures = new List<string>();
        foreach (var path in files)
        {
            try
            {
                var evidence = await c.Measure("PCC read/import audit " + Path.GetFileName(path), () => Task.Run(() => Audit(c, path)), QaStageKind.FileTransfer);
                audits.Add(evidence);
            }
            catch (Exception error) { failures.Add(Path.GetRelativePath(auditDirectory, path) + ": " + error.Message); }
        }
        c.Current.Evidence["audits"] = audits;
        c.Current.Evidence["auditFailures"] = failures;
        QaContext.Require(failures.Count == 0, "Exported PCC audit failed:\n" + string.Join("\n", failures));
        QaContext.Require(audits.Count > 0, "No exported PCC was audited.");
    }

    private static void RestoreBaselines(QaContext c, string directory)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(QaOutputFiles.FindResults(directory)));
        QaContext.Require(report.RootElement.GetProperty("Complete").GetBoolean(), "Re-audit requires a completed run.");
        var restored = new List<object>();
        foreach (var result in report.RootElement.GetProperty("Results").EnumerateArray()
            .Where(value => value.GetProperty("Group").GetString() is "clipboard" or "cc-scalp-switch"))
        {
            var evidence = result.GetProperty("Evidence");
            var scalpCase = result.GetProperty("Group").GetString() == "cc-scalp-switch";
            if (scalpCase && !evidence.TryGetProperty("existingPackagePath", out _)) continue;
            var output = scalpCase ? evidence.GetProperty("existingPackagePath").GetString()!
                : evidence.TryGetProperty("clipboardPackagePath", out var recorded)
                    ? recorded.GetString()! : Path.Combine(directory, result.GetProperty("Id").GetString()!, "clipboard-workspace.pcc");
            output = Path.GetFullPath(output);
            if (!File.Exists(output)) continue;
            var source = scalpCase ? evidence.GetProperty("existingPackageSource").GetString()!
                : evidence.GetProperty("sourceFace").GetProperty("PackagePath").GetString()!;
            IReadOnlySet<string> baseline;
            var captured = evidence.TryGetProperty("existingPackageBaseline", out var stored);
            if (captured) baseline = stored.EnumerateArray().Select(value => value.GetString()!).ToHashSet();
            else
            {
                c.TrackInput(source);
                using var package = MEPackageHandler.OpenMEPackage(source, forceLoadFromDisk: true);
                baseline = PackageIntegrity.CaptureIssues(package);
            }
            c.ExistingPackageBaselines[output] = baseline;
            c.ExistingPackageSources[output] = source;
            c.TrackInput(output);
            restored.Add(new { Output = output, Source = source, IssueCount = baseline.Count,
                BaselineOrigin = captured ? "Captured before mutation in original run" : "Reconstructed from current original input" });
        }
        c.Current.Evidence["existingPackageBaselineSources"] = restored;
        if (restored.Count > 0 && report.RootElement.GetProperty("Results").EnumerateArray()
            .Any(value => value.GetProperty("Group").GetString() is "clipboard" or "cc-scalp-switch" &&
                !value.GetProperty("Evidence").TryGetProperty("existingPackageBaseline", out _)))
            c.Current.Warnings.Add("This older run did not persist its baseline sets. Structural baselines were reconstructed from current original inputs; this does not baseline or excuse unresolved imports.");
    }

    private static object Audit(QaContext c, string path)
    {
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        c.ExistingPackageBaselines.TryGetValue(Path.GetFullPath(path), out var baseline);
        PackageIntegrity.Verify(package, baseline);
        VerifyCustomMeshForcedExports(package);
        var check = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(check, package, LECLocalizationShim.NonLocalizedStringConverter);
        var issues = check.GetBlockingErrors().Concat(check.GetSignificantIssues()).ToArray();
        QaContext.Require(issues.Length == 0, "LEC reference/property issues: " + string.Join("; ", issues.Select(value => value.Message)));
        var trashPolicy = QaTrashPolicy.ClassifyUnreferencedTrashImports(package);
        using var cache = new PackageCache();
        var resolver = new InstalledGameAssetResolver();
        var imports = new List<object>();
        foreach (var import in package.Imports)
        {
            if (trashPolicy.IgnoredImportIndices.Contains(import.UIndex)) continue;
            if (import.IsAKnownNativeClass() || import.InstancedFullPath.StartsWith("Core.", StringComparison.OrdinalIgnoreCase) ||
                import.InstancedFullPath.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase)) continue;
            const string le2EyePackage = "BIOG_Humanoid_EYE_OVRD";
            if (package.Game == MEGame.LE2 && import.ClassName == "Package" && import.idxLink == 0 &&
                import.InstancedFullPath.Equals(le2EyePackage, StringComparison.OrdinalIgnoreCase))
            {
                // LEC's package-root
                // resolver reports a false negative; child imports remain audited.
                QaContext.Require(package.FindExport(le2EyePackage, "Package") is null,
                    "LE2 eye override package import was duplicated as an export.");
                imports.Add(new { Path = import.InstancedFullPath,
                    Role = "Verified LE2 eye package root; known LEC resolver false negative" });
                continue;
            }
            const string allowedLe2 = "BIOG_Humanoid_MASTER_MTR_R.Skin_HumanHED_SpecMulitplier_Mask";
            if (package.Game == MEGame.LE2 && import.ClassName == "Texture2D" &&
                import.InstancedFullPath.Equals(allowedLe2, StringComparison.OrdinalIgnoreCase))
            {
                QaContext.Require(package.FindExport(allowedLe2, "Texture2D") is null, "LE2 optional native import was duplicated as an export.");
                imports.Add(new { Path = import.InstancedFullPath, Role = "Allowed optional LE2 native import" });
                continue;
            }
            var declared = EntryImporter.ResolveImport(import, cache);
            if (declared is null && package.Game == MEGame.LE2 &&
                import.ClassName == "RvrEffectsMaterialUser" &&
                import.InstancedFullPath.Equals(
                    "BIOG_Humanoid_EYE_OVRD.HMM_EYE_MASTER_OVRD_MAT_USER", StringComparison.OrdinalIgnoreCase))
            {
                // The same localized Startup lookup fails in LEC for the child.
                // Require an exact installed donor rather than exempting the asset.
                declared = resolver.Resolve(package.Game, import.InstancedFullPath, import.ClassName, cache);
            }
            if (declared is null)
            {
                var provenance = "";
                if (c.ExistingPackageSources.TryGetValue(Path.GetFullPath(path), out var source))
                {
                    c.TrackInput(source);
                    var original = cache.GetCachedPackage(source);
                    var originalImport = original?.Imports.FirstOrDefault(value => value.UIndex == import.UIndex &&
                        value.ClassName == import.ClassName && value.InstancedFullPath.Equals(import.InstancedFullPath, StringComparison.OrdinalIgnoreCase));
                    provenance = originalImport is null ? " Not present at this import index in the original input."
                        : EntryImporter.ResolveImport(originalImport, cache) is null
                            ? " The same import index/class/path is also unresolved in the original input."
                            : " The same import resolves in the original input, but not in this copied output.";
                }
                throw new InvalidDataException("Import cannot resolve its declared package identity: " + import.InstancedFullPath +
                    $" ({import.ClassName}, #{import.UIndex})." + provenance);
            }
            var official = resolver.Resolve(package.Game, import.InstancedFullPath, import.ClassName, cache);
            if (InstalledAssetOccurrence.IsSupportedClass(import.ClassName))
                QaContext.Require(official is not null, "Import has no exact installed official asset occurrence: " + import.InstancedFullPath);
            if (official is not null)
                QaContext.Require(PccAssetPathPolicy.FromExport(official).Equals(import.InstancedFullPath, StringComparison.OrdinalIgnoreCase) &&
                    official.ClassName == import.ClassName, "Official resolution substituted an incorrect identity: " + import.InstancedFullPath);
            imports.Add(new { Path = import.InstancedFullPath, import.ClassName,
                DeclaredDonor = declared!.FileRef.FilePath, OfficialDonor = official?.FileRef.FilePath });
        }
        return new { Package = path, Game = package.Game.ToString(), Exports = package.ExportCount,
            Imports = package.ImportCount, ExistingIssueCount = baseline?.Count ?? 0,
            IgnoredTrashImports = trashPolicy.Evidence, ResolvedImports = imports };
    }

    private static void VerifyCustomMeshForcedExports(IMEPackage package)
    {
        var root = package.FindExport("MorphFaceEditor", "Package");
        if (root is null) return;
        var meshes = package.Exports.Where(export => export.idxLink == root.UIndex &&
            export.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (meshes.Length == 0) return;

        var missing = new List<string>();
        if (!root.IsForcedExport)
            missing.Add($"root #{root.UIndex} '{root.InstancedFullPath}'");
        missing.AddRange(meshes.Where(mesh => !mesh.IsForcedExport)
            .Select(mesh => $"SkeletalMesh child #{mesh.UIndex} '{mesh.InstancedFullPath}'"));
        QaContext.Require(missing.Count == 0,
            "Custom mesh output entries are not ForcedExport: " + string.Join("; ", missing));
    }
}
