using System.IO;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool HasMultipleStandaloneImports => _workspace?.HasMultipleImports == true;
    private bool CanSaveOriginalRon => _workspace?.CanSaveRon == true;

    private async Task<bool> SaveSourceFileAsync()
    {
        if (HasMultipleStandaloneImports)
        {
            _dialogs.ShowInformation(Loc.Text("loc_View_MainWindow_SavePackage"), Loc.Text("loc_FileSave_MultipleImports"));
            return false;
        }
        if (PackageWorkspace?.CanCommit == true)
            return await CommitWorkspaceAsync(showConfirmation: true);
        if (CareerWorkspace is not null) return await SaveCareerFileAsync();
        if (CanSaveOriginalRon)
            return await SaveOriginalRonAsync();
        if (_workspace?.NeedsMeshSaveChoice == true)
        {
            var choice = _dialogs.ChooseMeshSaveFormat();
            var saved = choice switch
            {
                MeshSaveChoice.Pcc => await SaveMorphToPccAsync(),
                MeshSaveChoice.MaterialsRon => IsDetachedMeshWorkspace
                    ? await ExportMeshMaterialsAsync(false) : await ExportFaceMaterialsAsync(),
                _ => false
            };
            if (saved)
            {
                Editor?.AcceptSavedState(materialsOnly: choice == MeshSaveChoice.MaterialsRon);
                if (choice == MeshSaveChoice.Pcc) _workspace!.AcceptSavedChanges();
                OnDirtyStateChanged();
                if (IsDirty) ShowRemainingSourceEdits();
            }
            return saved && !IsDirty;
        }
        return await SaveMorphToPccAsync();
    }

    private async Task SaveSourceFileAsAsync()
    {
        if (HasMultipleStandaloneImports)
        {
            _dialogs.ShowInformation(Loc.Text("loc_FileSave_SaveAs"), Loc.Text("loc_FileSave_MultipleImports"));
            return;
        }
        var source = _workspace?.SavePath;
        if (source is null) { await SaveSourceFileAsync(); return; }
        var destination = _dialogs.ChooseSaveAsFile(source);
        if (destination is null) return;
        if (CareerWorkspace is not null) { await SaveCareerFileAsync(destination); return; }
        if (CanSaveOriginalRon) { await SaveOriginalRonAsync(destination); return; }
        using var saveLock = new SaveControlLock(this);
        if (Editor?.IsDirty == true && !await FlushEditorToWorkspaceAsync()) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await Task.Run(() => PackageWorkspace!.Commit(destination));
            _workspace!.AcceptSavedChanges();
            OnWorkspaceChanged();
            Status = Loc.Format("loc_Shell_Status_PackageSaved", PackageName);
        }
        catch (Exception exception)
        {
            AppLog.Error("FILE:SAVE-AS-FAILED", exception);
            ErrorMessage = Loc.Format("loc_FileSave_Failed", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_FileSave_EditsRetained");
        }
        finally { IsBusy = false; }
    }

    private async Task<bool> SaveOriginalRonAsync(string? destination = null)
    {
        if (SourceFile is not { } source || WorkspacePackagePath is not { } workspacePath)
            return false;
        using var saveLock = new SaveControlLock(this);
        // Export from the source face, even if another base head is selected in the sidebar.
        if (LoadedFacePath != source.FacePath && Editor?.IsDirty == true)
        {
            _dialogs.ShowInformation(Loc.Text("loc_View_MainWindow_SavePackage"), Loc.Text("loc_FileSave_OtherFace"));
            return false;
        }
        if (source.Format == WorkspaceFileFormat.MorphRon &&
            Editor?.IsDirty == true && !await FlushEditorToWorkspaceAsync()) return false;
        IsBusy = true;
        ErrorMessage = null;
        var target = Path.GetFullPath(destination ?? source.Path);
        var savingSource = target.Equals(source.Path, StringComparison.OrdinalIgnoreCase);
        var candidate = Path.Combine(Path.GetDirectoryName(target)!,
            $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var targetFingerprint = File.Exists(target) ? EditorWorkspace.FileFingerprint(target) : null;
            var face = Faces.Single(value => value.InstancedPath.Equals(source.FacePath, StringComparison.OrdinalIgnoreCase));
            var player = IsPlayerWorkspace;
            if (source.Format == WorkspaceFileFormat.MorphFeaturesRon)
            {
                if (LoadedFacePath != source.FacePath || Editor is null) return false;
                var draft = Editor.CreateDraft();
                var provenance = CreateRonExportProvenance(workspacePath, face, player);
                await Task.Run(() => TseHeadMorphRon.WriteMorphFeatures(candidate, ToMorphData(draft), provenance));
                TseHeadMorphRon.Validate(candidate);
            }
            else if (source.Format == WorkspaceFileFormat.MorphRon)
            {
                await Task.Run(() =>
                {
                    var provenance = CreateRonExportProvenance(workspacePath, face, player);
                    _packageContextService.ExportRon(workspacePath, source.FacePath, candidate, provenance);
                    TseHeadMorphRon.Validate(candidate);
                });
            }
            else
            {
                if (LoadedFacePath != source.FacePath || Editor is not { } editor) return false;
                var values = editor.Material.CaptureInterchangeData();
                var game = ResolveMaterialFileGame();
                var document = MeshMaterialFileService.WithAttachments(
                    FaceMaterialFileService.Capture(game, face.ObjectName, values),
                    editor.HairMesh.Value, editor.OtherMeshes.Select(mesh => mesh.Value).ToArray()) with
                {
                    DefaultFace = new MaterialFaceTarget(face.ProfileKey, player)
                };
                if (player)
                    await Task.Run(() => _packageContextService.VerifyPlayerRuntimeAssets(game, values,
                        document.HairMesh!, document.AccessoryMeshes));
                string text;
                if (source.Format == WorkspaceFileFormat.TseMaterialsRon)
                {
                    text = MaterialRonCodec.WriteTse(values, document.HairMesh!,
                        document.AccessoryMeshes!, document.Game, document.DefaultFace);
                }
                else
                {
                    var original = MaterialRonCodec.ReadMfe(await File.ReadAllTextAsync(source.Path));
                    text = MaterialRonCodec.WriteMfe(original with
                    {
                        Parameters = document.Parameters,
                        HairMesh = document.HairMesh,
                        AccessoryMeshes = document.AccessoryMeshes
                    });
                }
                await File.WriteAllTextAsync(candidate, text);
                _ = FaceMaterialFileService.Read(await File.ReadAllTextAsync(candidate), allowTse: true);
            }
            if (savingSource && EditorWorkspace.FileFingerprint(source.Path) != source.Fingerprint ||
                File.Exists(target) != (targetFingerprint is not null) ||
                targetFingerprint is not null && EditorWorkspace.FileFingerprint(target) != targetFingerprint)
                throw new IOException(Loc.Text("loc_FileSave_SourceChanged"));
            var fingerprint = EditorWorkspace.FileFingerprint(candidate);
            File.Move(candidate, target, overwrite: true);
            _workspace!.ImportedFile = source with { Path = target, Fingerprint = fingerprint };
            OnPropertyChanged(nameof(PackageName));
            if (source.Format == WorkspaceFileFormat.MorphRon) _workspace.AcceptSavedFace(source.FacePath);
            else if (source.Format == WorkspaceFileFormat.MorphFeaturesRon) Editor?.AcceptSavedState(featuresOnly: true);
            else Editor?.AcceptSavedState(materialsOnly: true);
            OnDirtyStateChanged();
            Status = Loc.Format("loc_Shell_Status_PackageSaved", Path.GetFileName(target));
            if (IsDirty) ShowRemainingSourceEdits();
            return !IsDirty;
        }
        catch (Exception exception)
        {
            AppLog.Error($"RON:SAVE-FAILED FILE:{source.Path}", exception);
            ErrorMessage = Loc.Format("loc_FileSave_Failed", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_FileSave_EditsRetained");
            return false;
        }
        finally
        {
            try { if (File.Exists(candidate)) File.Delete(candidate); }
            finally { IsBusy = false; }
        }
    }

    private void ShowRemainingSourceEdits() => _dialogs.ShowInformation(
        Loc.Text("loc_View_MainWindow_SavePackage"), Loc.Text(SourceFile?.Format == WorkspaceFileFormat.MorphFeaturesRon
            ? "loc_FileSave_MorphFeaturesOnly" : "loc_FileSave_MaterialsOnly"));
}
