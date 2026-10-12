using System.IO;
using System.Windows.Input;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task<bool> TryImportMorphFeaturesRonAsync(string path)
    {
        try
        {
            if (!TseHeadMorphRon.IsMorphFeaturesOnly(path)) return false;
            var metadata = TseHeadMorphRon.ReadProvenance(path);
            var data = TseHeadMorphRon.ReadMorphData(path);
            if (metadata is null || metadata.Producer != RonExportProducer.MFE)
                throw new InvalidDataException(Loc.Text("loc_RonFiles_Incompatible"));
            var opened = !HasWorkspace;
            if (opened)
            {
                var profile = MorphFaceProfileRegistry.CreateDefault().Profiles.SingleOrDefault(value =>
                    value.Game == metadata.Game && value.ExportTag.Trim('[', ']').Equals(metadata.Archetype, StringComparison.OrdinalIgnoreCase));
                if (profile is null) throw new InvalidDataException(Loc.Text("loc_RonFiles_Incompatible"));
                if (!await OpenDefaultMaterialFaceAsync(metadata.Game.ToString(), new MaterialFaceTarget(profile.Key, metadata.PlayerMorph), path))
                    return true;
            }
            if (!await EnsureSelectedFaceLoadedForMaterialsAsync() || Editor is null || SelectedFace is null || WorkspacePackagePath is null)
                return true;
            var target = CreateRonExportProvenance(WorkspacePackagePath, SelectedFace, IsPlayerWorkspace);
            if (target.Game != metadata.Game || target.Archetype != metadata.Archetype || target.PlayerMorph != metadata.PlayerMorph)
                throw new InvalidDataException(Loc.Text("loc_RonFiles_Incompatible"));
            Editor.ApplyMorphFeatures(data);
            if (opened)
            {
                _relativeBakeFacePaths.Add(SelectedFace.InstancedPath);
                TrackSourceFile(path, SelectedFace.InstancedPath, SourceFileFormat.MorphFeaturesRon);
                Editor.AcceptSavedState();
                _hasWorkspaceChanges = false;
                OnDirtyStateChanged();
            }
            Status = Loc.Format("loc_RonFiles_Imported", Path.GetFileName(path));
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("RON:MORPHS-IMPORT-FAILED", exception);
            ErrorMessage = UserFacingMessageFormatter.Format(exception);
            Status = Loc.Text("loc_RonFiles_ImportFailed");
            return true;
        }
    }

    private AsyncRelayCommand? _importMorphFaceRonCommand;
    private AsyncRelayCommand? _importMorphFeaturesRonCommand;
    private AsyncRelayCommand? _exportMorphFeaturesRonCommand;
    public ICommand ImportMorphFaceRonCommand => _importMorphFaceRonCommand ??= new(
        () => ImportFaceRonAsync(false), CanExportMorphRon);
    public ICommand ImportMorphFeaturesRonCommand => _importMorphFeaturesRonCommand ??= new(
        () => ImportFaceRonAsync(true), () => CanExportMorphRon() && Editor?.CanEditMorphFeatures == true);
    public ICommand ExportMorphFeaturesRonCommand => _exportMorphFeaturesRonCommand ??= new(
        () => ExportMorphRonAsync(true), () => CanExportMorphRon() && !IsAlnMorphClipboardFace(SelectedFace));

    private async Task ImportFaceRonAsync(bool featuresOnly)
    {
        if (!CanExportMorphRon() || SelectedFace is not { } face || WorkspacePackagePath is not { } workspace ||
            !await EnsureSelectedFaceLoadedForMaterialsAsync() || Editor is null) return;
        var path = _dialogs.ChooseMorphImportFile(Path.GetDirectoryName(_standaloneImportPath ?? PackagePath));
        if (path is null) return;
        IsBusy = true;
        ErrorMessage = null;
        var backup = Path.Combine(Path.GetDirectoryName(workspace)!, $".MFE-RonImport-{Guid.NewGuid():N}.pcc");
        var wasRelative = _relativeBakeFacePaths.Contains(face.InstancedPath);
        var wasFixed = _fixedBakeFacePaths.Contains(face.InstancedPath);
        var refreshAttempted = false;
        var snapshot = backup + ".ron";
        try
        {
            var player = CreateRonExportProvenance(workspace, face, IsPlayerWorkspace).PlayerMorph;
            File.Copy(path, snapshot);
            var game = ResolveMaterialFileGame();
            var metadata = TseHeadMorphRon.ReadProvenance(snapshot);
            if (metadata is null ? !player : metadata.Game != game || metadata.PlayerMorph != player ||
                !metadata.Archetype.Equals(face.ProfileTag.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.Text("loc_RonFiles_Incompatible"));
            var data = await Task.Run(() => TseHeadMorphRon.ReadMorphData(snapshot));
            if (featuresOnly) Editor.ApplyMorphFeatures(data);
            else
            {
                var catalog = await _referenceService.ReadTextureCatalogAsync(game);
                var assets = await Task.Run(() => StandalonePlayerAssetCatalog.ForRon(game, snapshot,
                    catalog.Candidates, referencePackagePath: workspace, attachmentMeshes: catalog.AttachmentMeshes));
                IsBusy = false;
                if (Editor.IsDirty && !await FlushEditorToWorkspaceAsync()) return;
                IsBusy = true;
                File.Copy(workspace, backup);
                var result = await Task.Run(() => _packageContextService.ReplaceRon(workspace, face.InstancedPath, snapshot, player, assets));
                _relativeBakeFacePaths.Add(face.InstancedPath);
                _fixedBakeFacePaths.Remove(face.InstancedPath);
                refreshAttempted = true;
                if (!await RefreshWorkspaceAsync(face.InstancedPath))
                    throw new InvalidDataException(Loc.Text("loc_RonFiles_ImportFailed"));
                _pcsavWorkspace?.SetImportedHead(snapshot, Editor!.CreateDraft());
                _hasWorkspaceChanges = true;
                OnDirtyStateChanged();
                if (result.Warnings.Count > 0)
                    _dialogs.ShowInformation(Loc.Text("loc_RonFiles_ImportMorphFace"), string.Join("\n\n", result.Warnings.Take(8)));
            }
            Status = Loc.Format("loc_RonFiles_Imported", Path.GetFileName(path));
        }
        catch (Exception exception)
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, workspace, overwrite: true);
                if (!wasRelative) _relativeBakeFacePaths.Remove(face.InstancedPath);
                if (wasFixed) _fixedBakeFacePaths.Add(face.InstancedPath);
                if (refreshAttempted) await RefreshWorkspaceAsync(face.InstancedPath);
            }
            AppLog.Error("RON:FACE-IMPORT-FAILED", exception);
            ErrorMessage = UserFacingMessageFormatter.Format(exception);
            Status = Loc.Text("loc_RonFiles_ImportFailed");
        }
        finally
        {
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
                if (File.Exists(snapshot)) File.Delete(snapshot);
            }
            finally { IsBusy = false; }
        }
    }

    private void RaiseRonFileCanExecuteChanged()
    {
        _importMorphFaceRonCommand?.RaiseCanExecuteChanged();
        _importMorphFeaturesRonCommand?.RaiseCanExecuteChanged();
        _exportMorphFeaturesRonCommand?.RaiseCanExecuteChanged();
    }
}
