using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using System.IO;
using System.Windows.Input;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task<bool> TryImportMorphFeaturesRonAsync(string path, RonImportFile source)
    {
        try
        {
            if (!await Task.Run(() => TseHeadMorphRon.IsMorphFeaturesOnly(source.Input))) return false;
            var (head, metadata) = await Task.Run(() => TseHeadMorphRon.ReadDocument(source.Input));
            if (metadata is null || metadata.Producer != RonExportProducer.MFE)
                throw new InvalidDataException(Loc.Text("loc_RonFiles_Incompatible"));
            if (!HasWorkspace)
            {
                var profile = MorphFaceProfileRegistry.CreateDefault().Profiles.SingleOrDefault(value =>
                    value.Game == metadata.Game && value.ExportTag.Trim('[', ']').Equals(metadata.Archetype, StringComparison.OrdinalIgnoreCase));
                if (profile is null) throw new InvalidDataException(Loc.Text("loc_RonFiles_Incompatible"));
                if (await OpenDefaultMaterialFaceAsync(metadata.Game.ToString(), new MaterialFaceTarget(profile.Key, metadata.PlayerMorph),
                    path, source, WorkspaceFileFormat.MorphFeaturesRon, head.MorphData))
                    Status = Loc.Format("loc_RonFiles_Imported", Path.GetFileName(path));
                return true;
            }
            if (!await EnsureSelectedFaceLoadedForMaterialsAsync() || Editor is null || SelectedFace is null || WorkspacePackagePath is null)
                return true;
            var target = CreateRonExportProvenance(WorkspacePackagePath, SelectedFace, IsPlayerWorkspace);
            if (!MatchesRonTarget(metadata, target.Game, target.Archetype, target.PlayerMorph, featuresOnly: true))
                throw new InvalidDataException(Loc.Text("loc_RonFiles_Incompatible"));
            Editor.ApplyMorphFeatures(head.MorphData);
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
        () => ExportMorphRonAsync(true), () => CanExportMorphRon() && !IsAlnFace(SelectedFace));

    private static bool MatchesRonTarget(RonExportProvenance source, MorphFaceGame game, string archetype, bool player, bool featuresOnly = false) =>
        (source.Game == game || featuresOnly && StandaloneNpcMorphImportService.AreGamesCompatible(source.Game, game)) &&
        source.Archetype.Equals(archetype, StringComparison.OrdinalIgnoreCase) &&
        (source.PlayerMorph == player || player && !source.PlayerMorph &&
            (archetype.Equals("HMM", StringComparison.OrdinalIgnoreCase) || archetype.Equals("HMF", StringComparison.OrdinalIgnoreCase)));

    private async Task ImportFaceRonAsync(bool featuresOnly)
    {
        if (!CanExportMorphRon() || SelectedFace is not { } face || WorkspacePackagePath is not { } workspace ||
            !await EnsureSelectedFaceLoadedForMaterialsAsync() || Editor is null) return;
        var path = _dialogs.ChooseMorphImportFile(Path.GetDirectoryName(_workspace?.BrowsePath));
        if (path is null) return;
        IsBusy = true;
        ErrorMessage = null;
        var backup = Path.Combine(Path.GetDirectoryName(workspace)!, $".MFE-RonImport-{Guid.NewGuid():N}.pcc");
        var hadGeometryMode = _workspace!.GeometryModes.TryGetValue(face.InstancedPath, out var previousGeometryMode);
        var refreshAttempted = false;
        try
        {
            var player = CreateRonExportProvenance(workspace, face, IsPlayerWorkspace).PlayerMorph;
            var game = ResolveMaterialFileGame();
            var document = await Task.Run(() => TseHeadMorphRon.ReadDocument(path));
            var metadata = document.Provenance;
            if (metadata is null ? !player : !MatchesRonTarget(metadata, game, face.ProfileTag.Trim('[', ']'), player, featuresOnly))
                throw new InvalidDataException(Loc.Text("loc_RonFiles_Incompatible"));
            if (featuresOnly) Editor.ApplyMorphFeatures(document.Head.MorphData);
            else
            {
                var catalog = await _referenceService.ReadTextureCatalogAsync(game);
                var assets = await Task.Run(() => StandalonePlayerAssetCatalog.ForHead(game, document.Head,
                    catalog.Candidates, referencePackagePath: workspace, attachmentMeshes: catalog.AttachmentMeshes));
                if (await Editor.TryImportMatchingHeadAsync(document.Head, assets,
                    async () =>
                    {
                        if (player) await Task.Run(() => _packageContextService.VerifyPlayerRuntimeAssets(game,
                            document.Head.MaterialData, document.Head.HairMesh, document.Head.AccessoryMeshes));
                        foreach (var mesh in assets.SkeletalMeshes)
                            await _referenceService.ValidateDetachedAttachmentAsync(mesh.PackagePath, mesh.InstancedPath);
                    },
                    draft => CareerWorkspace?.SetImportedHead(document.Head, draft, recordHistory: true)))
                {
                    Status = Loc.Format("loc_RonFiles_Imported", Path.GetFileName(path));
                    return;
                }
                IsBusy = false;
                if ((Editor.IsDirty || CareerDraftPendingInPackage) && !await FlushEditorToWorkspaceAsync()) return;
                IsBusy = true;
                File.Copy(workspace, backup);
                var result = await Task.Run(() => _packageContextService.ReplaceHead(workspace, face.InstancedPath, document.Head, player, assets));
                _workspace!.GeometryModes[face.InstancedPath] = MorphFaceGeometryMode.RelativeBake;
                refreshAttempted = true;
                if (!await RefreshWorkspaceAsync(face.InstancedPath))
                    throw new InvalidDataException(Loc.Text("loc_RonFiles_ImportFailed"));
                CareerWorkspace?.SetImportedHead(document.Head, Editor!.CreateDraft());
                _workspace!.MarkChanged(face.InstancedPath);
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
                if (hadGeometryMode) _workspace!.GeometryModes[face.InstancedPath] = previousGeometryMode;
                else _workspace!.GeometryModes.Remove(face.InstancedPath);
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
