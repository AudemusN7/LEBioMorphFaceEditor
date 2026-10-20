using System.IO;
using System.Text.Json;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Capture edits into a disposable copy; generating a report never commits or cleans the editor.</summary>
    internal async Task<DiagnosticArchiveResult> GenerateDiagnosticsAsync(
        string destination, DiagnosticArchiveService? archiveService = null)
    {
        if (IsBusy) throw new InvalidOperationException(Loc.Text("loc_Shell_Diagnostics_WaitForCurrentOperation"));
        var workspacePath = WorkspacePackagePath;
        var draft = Editor?.IsDirty == true ? Editor.CreateDraft() :
            CareerDraftPendingInPackage ? Editor?.SavedDraft : null;
        var meshSource = DetachedMeshSource;
        var meshPath = meshSource?.SourcePath;
        var materialText = IsDetachedMeshWorkspace && Editor is { CustomMaterials: { } materials } editor &&
                           StandaloneGame is { } game
            ? MaterialRonCodec.WriteMfe(MeshMaterialFileService.Capture(materials, game,
                SelectedFace?.ObjectName ?? "Mesh", editor.Material.CaptureInterchangeData()))
            : null;
        var scratch = Path.Combine(Path.GetTempPath(), $"MFE-Diagnostics-{Guid.NewGuid():N}");
        IsBusy = true;
        try
        {
            AppLog.Information($"DIAGNOSTICS:EXPORT FILE:\"{destination}\" LOG-DAYS:3");
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(scratch);
                var files = new List<string>();
                var warnings = new List<string>();
                if (workspacePath is not null)
                {
                    var snapshot = Path.Combine(scratch, Path.GetFileName(workspacePath));
                    File.Copy(workspacePath, snapshot);
                    if (draft is not null)
                    {
                        try
                        {
                            _packageWriter.SaveExisting(draft with { Source = draft.Source with { PackagePath = snapshot } });
                        }
                        catch (Exception exception)
                        {
                            // A report must still be useful when the problem being reported is a failed save.
                            File.Copy(workspacePath, snapshot, overwrite: true);
                            var pendingEdits = Path.Combine(scratch, "PendingEdits.json");
                            File.WriteAllText(pendingEdits, JsonSerializer.Serialize(draft,
                                new JsonSerializerOptions { IncludeFields = true }));
                            files.Add(pendingEdits);
                            warnings.Add(Loc.Format("loc_Shell_Diagnostics_PendingEditsCouldNotApply",
                                "PendingEdits.json", exception.Message));
                            AppLog.Warning(warnings[^1]);
                        }
                    }
                    files.Add(snapshot);
                }
                else if (meshPath is not null)
                {
                    if (File.Exists(meshPath))
                    {
                        var snapshot = Path.Combine(scratch, Path.GetFileName(meshPath));
                        File.Copy(meshPath, snapshot);
                        files.Add(snapshot);
                    }
                    else warnings.Add(Loc.Format("loc_Shell_Diagnostics_OriginalMeshUnavailable", "MeshWorkspace.json"));
                    // Decoded geometry also covers external glTF buffers and verified rig edits.
                    var meshWorkspace = Path.Combine(scratch, "MeshWorkspace.json");
                    File.WriteAllText(meshWorkspace, JsonSerializer.Serialize(new { Source = meshSource, PendingEdits = draft },
                        new JsonSerializerOptions { IncludeFields = true }));
                    files.Add(meshWorkspace);
                    if (materialText is not null)
                    {
                        var materialPath = Path.Combine(scratch, "Workspace.materials.ron");
                        File.WriteAllText(materialPath, materialText);
                        files.Add(materialPath);
                    }
                }
                return (archiveService ?? new DiagnosticArchiveService(ApplicationDataPaths.CreateDefault(), AppLog.FilePath))
                    .Export(destination, files, warnings);
            });
        }
        catch (Exception exception)
        {
            AppLog.Error("DIAGNOSTICS:EXPORT-FAILED", exception);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (Exception exception) { AppLog.Warning($"DIAGNOSTICS:CLEANUP-FAILED REASON:{exception.Message}"); }
            finally { IsBusy = false; }
        }
    }
}
