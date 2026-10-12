using System.IO;
using System.Security.Cryptography;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed partial class MainWindowViewModel
{
    private enum SourceFileFormat { MorphRon, MfeMaterialsRon, TseMaterialsRon, Mesh, LegacyMorph, Pcsav }
    private sealed record SourceFile(string Path, string FacePath, SourceFileFormat Format, string Fingerprint);
    private SourceFile? _sourceFile;
    private bool _hasOtherStandaloneChanges;

    private bool HasMultipleStandaloneImports => IsStandaloneWorkspace &&
        _relativeBakeFacePaths.Concat(_fixedBakeFacePaths).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    private static string FileFingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void TrackSourceFile(string path, string facePath, SourceFileFormat format)
    {
        var fullPath = Path.GetFullPath(path);
        _sourceFile = new(fullPath, facePath, format, FileFingerprint(fullPath));
        OnDirtyStateChanged();
    }

    private bool CanSaveOriginalRon => !HasMultipleStandaloneImports && _sourceFile?.Format is
        SourceFileFormat.MorphRon or SourceFileFormat.MfeMaterialsRon or SourceFileFormat.TseMaterialsRon;

    private async Task<bool> SaveSourceFileAsync()
    {
        if (HasMultipleStandaloneImports)
        {
            _dialogs.ShowInformation(Loc.Text("loc_View_MainWindow_SavePackage"), Loc.Text("loc_FileSave_MultipleImports"));
            return false;
        }
        if (_packageWorkspace?.CanCommit == true)
            return await CommitWorkspaceAsync(showConfirmation: true);
        if (CanSaveOriginalRon)
            return await SaveOriginalRonAsync();
        if (IsDetachedMeshWorkspace || _sourceFile?.Format == SourceFileFormat.Mesh)
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
                if (choice == MeshSaveChoice.Pcc) _hasWorkspaceChanges = false;
                OnDirtyStateChanged();
                if (IsDirty) ShowRemainingSourceEdits();
            }
            return saved && !IsDirty;
        }
        return await SaveMorphToPccAsync();
    }

    private async Task<bool> SaveOriginalRonAsync()
    {
        if (_sourceFile is not { } source || WorkspacePackagePath is not { } workspacePath)
            return false;
        using var saveLock = new SaveControlLock(this);
        // Export from the source face, even if another base head is selected in the sidebar.
        if (LoadedFacePath != source.FacePath && Editor?.IsDirty == true)
        {
            _dialogs.ShowInformation(Loc.Text("loc_View_MainWindow_SavePackage"), Loc.Text("loc_FileSave_OtherFace"));
            return false;
        }
        if (source.Format == SourceFileFormat.MorphRon &&
            Editor?.IsDirty == true && !await FlushEditorToWorkspaceAsync()) return false;
        IsBusy = true;
        ErrorMessage = null;
        var candidate = Path.Combine(Path.GetDirectoryName(source.Path)!,
            $".{Path.GetFileName(source.Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var face = Faces.Single(value => value.InstancedPath.Equals(source.FacePath, StringComparison.OrdinalIgnoreCase));
            var player = IsPlayerWorkspace;
            if (source.Format == SourceFileFormat.MorphRon)
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
                var document = FaceMaterialFileService.Capture(ResolveMaterialFileGame(), face.ObjectName, values) with
                {
                    DefaultFace = new MaterialFaceTarget(face.ProfileKey, player)
                };
                string text;
                if (source.Format == SourceFileFormat.TseMaterialsRon)
                {
                    var original = MaterialRonCodec.ReadTseDocument(await File.ReadAllTextAsync(source.Path));
                    text = MaterialRonCodec.WriteTse(values, original.HairMesh ?? "None",
                        original.AccessoryMeshes ?? [], document.Game, document.DefaultFace);
                }
                else
                {
                    var original = MaterialRonCodec.ReadMfe(await File.ReadAllTextAsync(source.Path));
                    text = MaterialRonCodec.WriteMfe(original with { Parameters = document.Parameters });
                }
                await File.WriteAllTextAsync(candidate, text);
                _ = FaceMaterialFileService.Read(await File.ReadAllTextAsync(candidate), allowTse: true);
            }
            if (FileFingerprint(source.Path) != source.Fingerprint)
                throw new IOException(Loc.Text("loc_FileSave_SourceChanged"));
            var fingerprint = FileFingerprint(candidate);
            File.Move(candidate, source.Path, overwrite: true);
            _sourceFile = source with { Fingerprint = fingerprint };
            if (source.Format == SourceFileFormat.MorphRon) _hasWorkspaceChanges = _hasOtherStandaloneChanges;
            else Editor?.AcceptSavedState(materialsOnly: true);
            OnDirtyStateChanged();
            Status = Loc.Format("loc_Shell_Status_PackageSaved", Path.GetFileName(source.Path));
            if (IsDirty) ShowRemainingSourceEdits();
            return !IsDirty;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Source RON save failed for '{source.Path}'.", exception);
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
        Loc.Text("loc_View_MainWindow_SavePackage"), Loc.Text("loc_FileSave_MaterialsOnly"));
}
