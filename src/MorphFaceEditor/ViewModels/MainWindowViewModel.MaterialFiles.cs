using System.IO;
using System.Windows.Input;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

/// <summary>Routes material file actions through the MESH or face workspace interchange path.</summary>
public sealed partial class MainWindowViewModel
{
    internal static string AppendDatabaseImportGuidance(string details) =>
        details + "\n\n" + Loc.Text("loc_Shell_ImportWarnings_DatabaseGuidance");

    private async Task<bool> TryImportMaterialRonAsync(string sourcePath)
    {
        MaterialRonCodec.MaterialFileKind? kind;
        try
        {
            var text = await File.ReadAllTextAsync(sourcePath);
            kind = await Task.Run(() => MaterialRonCodec.IdentifyMaterialFile(text));
        }
        catch (Exception exception)
        {
            ErrorMessage = Loc.Format("loc_Shell_Error_TheRonProvenanceCouldNot", exception.Message);
            Status = Loc.Text("loc_Shell_Status_RonImportFailedTheCurrent");
            return true;
        }
        if (kind is null) return false;
        var openedSourceWorkspace = false;
        if (!CanUseMeshMaterialFiles() && !CanUseFaceMaterialFiles())
        {
            try
            {
                var target = await Task.Run(async () =>
                    MaterialRonCodec.ReadDefaultFace(await File.ReadAllTextAsync(sourcePath)));
                if (target is null)
                {
                    ErrorMessage = Loc.Text("loc_Shell_Status_RonImportFailedTheCurrent");
                    AppLog.Warning("Material RON has no default face metadata; no workspace was opened.");
                    return true;
                }
                if (!await OpenDefaultMaterialFaceAsync(target.Value.Game, target.Value.Target, sourcePath)) return true;
                openedSourceWorkspace = true;
            }
            catch (Exception exception)
            {
                ErrorMessage = Loc.Format("loc_Shell_Error_TheRonProvenanceCouldNot", exception.Message);
                return true;
            }
        }
        if (CanUseMeshMaterialFiles())
            await ImportMeshMaterialsAsync(kind == MaterialRonCodec.MaterialFileKind.Tse, sourcePath);
        else if (CanUseFaceMaterialFiles())
            await ImportFaceMaterialsAsync(sourcePath);
        if (openedSourceWorkspace && ErrorMessage is null && LoadedFacePath is { } facePath)
        {
            TrackSourceFile(sourcePath, facePath, kind == MaterialRonCodec.MaterialFileKind.Tse
                ? SourceFileFormat.TseMaterialsRon : SourceFileFormat.MfeMaterialsRon);
            Editor?.AcceptSavedState();
            _hasWorkspaceChanges = false;
            OnDirtyStateChanged();
        }
        return true;
    }

    private async Task<bool> OpenDefaultMaterialFaceAsync(string gameName, MaterialFaceTarget target, string sourcePath)
    {
        if (!Enum.TryParse<MorphFaceGame>(gameName, out var game) ||
            game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3) ||
            !MorphFaceProfileRegistry.CreateDefault().Profiles.Any(profile =>
                profile.Game == game && profile.Key == target.ProfileKey))
        {
            ErrorMessage = Loc.Format("loc_Shell_Error_MaterialDefaultFaceUnavailable", target.ProfileKey, gameName);
            return false;
        }
        string packagePath;
        string facePath;
        if (target.PlayerMorph)
        {
            var suffix = MorphFaceEditor.Core.Materials.HeadProfileIdentity.SuffixFromProfileKey(target.ProfileKey);
            if (suffix is not ("human-male" or "human-female"))
            {
                ErrorMessage = Loc.Format("loc_Shell_Error_MaterialDefaultFaceUnavailable", target.ProfileKey, gameName);
                return false;
            }
            packagePath = StandalonePlayerMorphImportService.ResolveInstalledSeed(game);
            facePath = StandalonePlayerMorphImportService.ResolvePlayerTemplate(game,
                suffix == "human-male" ? StandalonePlayerSex.Male : StandalonePlayerSex.Female);
        }
        else
        {
            var catalogue = await _referenceService.ReadTextureCatalogAsync(game);
            if (!catalogue.IsAvailable)
            {
                ErrorMessage = Loc.Format("loc_Shell_Error_NpcTargetDatabaseUnavailable", game);
                return false;
            }
            var donor = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault()).ResolveMaterialDefault(
                game, target.ProfileKey, catalogue.MorphFaceTemplates, catalogue.OfficialPackagePaths);
            if (donor is null)
            {
                ErrorMessage = Loc.Format("loc_Shell_Error_MaterialDefaultFaceUnavailable", target.ProfileKey, gameName);
                return false;
            }
            packagePath = donor.PackagePath;
            facePath = donor.FacePath;
        }
        if (!await EnsureCanAbandonWorkspaceAsync()) return false;
        if (!await OpenSourcePackagePathAsync(packagePath, facePath, game, standaloneNpc: !target.PlayerMorph)) return false;
        foreach (var other in Faces.Where(face => face != SelectedFace).ToArray()) Faces.Remove(other);
        _standaloneImportPath = Path.GetFullPath(sourcePath);
        return Editor is not null;
    }
    private readonly AsyncRelayCommand _exportTseMaterialsCommand;
    private readonly AsyncRelayCommand _importTseMaterialsCommand;
    private readonly AsyncRelayCommand _exportMaterialsCommand;
    private readonly AsyncRelayCommand _importMaterialsCommand;
    public ICommand ExportTseMaterialsCommand => _exportTseMaterialsCommand;
    public ICommand ImportTseMaterialsCommand => _importTseMaterialsCommand;
    public ICommand ExportMaterialsCommand => _exportMaterialsCommand;
    public ICommand ImportMaterialsCommand => _importMaterialsCommand;

    private bool CanUseMeshMaterialFiles() => !IsBusy && IsDetachedMeshWorkspace &&
        Editor?.CustomMaterials is not null && SelectedFace?.InstancedPath == LoadedFacePath;

    /// <summary>A selected package face can be loaded before its material file action.</summary>
    private bool CanUseFaceMaterialFiles() => !IsBusy && !IsDetachedMeshWorkspace &&
        SelectedFace is not null && WorkspacePackagePath is not null;

    public bool IsMaterialFileWorkspace => IsDetachedMeshWorkspace
        ? Editor is not null && SelectedFace?.InstancedPath == LoadedFacePath
        : SelectedFace is not null && WorkspacePackagePath is not null;

    private bool CanUseMaterialFiles() => CanUseMeshMaterialFiles() || CanUseFaceMaterialFiles();

    private async Task<bool> ExportMeshMaterialsAsync(bool tse)
    {
        if (!CanUseMeshMaterialFiles() || Editor is not { CustomMaterials: { } workspace } editor || _standaloneGame is not { } game)
            return false;
        var name = SelectedFace!.ObjectName;
        var path = _dialogs.ChooseMaterialExportFile(tse, tse ? name + ".ron" : name + ".materials.ron",
            Path.GetDirectoryName(_standaloneImportPath));
        if (path is null) return false;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var values = editor.Material.CaptureInterchangeData();
            var text = tse
                ? MeshMaterialFileService.ExportTse(workspace, values, editor.HairMesh.Value,
                    editor.OtherMeshes.Select(value => value.Value).ToArray(), game)
                : MaterialRonCodec.WriteMfe(MeshMaterialFileService.Capture(workspace, game, name, values));
            await MeshMaterialFileService.WriteAsync(path, text, workspace.Source.SourcePath);
            Status = Loc.Format(tse ? "loc_Shell_Status_ExportedTseMaterialSettings" : "loc_Shell_Status_ExportedMfeMaterialSettings", Path.GetFileName(path));
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("MESH material export failed.", exception);
            ErrorMessage = exception.Message;
            Status = Loc.Text("loc_Shell_Status_MaterialExportFailedTheCurrent");
            return false;
        }
        finally { IsBusy = false; }
    }

    private async Task ImportMeshMaterialsAsync(bool tse, string? sourcePath = null)
    {
        if (!CanUseMeshMaterialFiles() || Editor is not { CustomMaterials: { } workspace } editor || _standaloneGame is not { } game) return;
        var path = sourcePath ?? _dialogs.ChooseMaterialImportFile(tse, Path.GetDirectoryName(_standaloneImportPath));
        if (path is null) return;
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_PreparingMaterialSettingsAndTexture");
        try
        {
            var text = await File.ReadAllTextAsync(path);
            var prepared = await new MeshMaterialFileService(_referenceService).PrepareAsync(
                text, tse, workspace, editor.CustomMaterialOptions, game);
            if (!ReferenceEquals(Editor, editor)) return;
            editor.ApplyMeshMaterialImport(prepared);
            Status = Loc.Format(prepared.IsTse ? "loc_Shell_Status_ImportedTseMaterialSettings" : "loc_Shell_Status_ImportedMaterialSettings", Path.GetFileName(path));
            if (prepared.Warnings.Count > 0)
            {
                foreach (var warning in prepared.Warnings) AppLog.Warning(warning);
                const int displayLimit = 8;
                var details = string.Join("\n\n", prepared.Warnings.Take(displayLimit));
                if (prepared.Warnings.Count > displayLimit)
                    details += Loc.Format("loc_Shell_MaterialFiles_MoreWarningsInLog", prepared.Warnings.Count - displayLimit);
                _dialogs.ShowInformation(Loc.Text("loc_Shell_DialogTitle_MaterialsImportedWithWarnings"), AppendDatabaseImportGuidance(details));
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("MESH material import failed.", exception);
            ErrorMessage = exception.Message;
            Status = Loc.Text("loc_Shell_Status_MaterialImportFailedTheCurrent");
        }
        finally { IsBusy = false; }
    }

    private async Task<bool> ExportFaceMaterialsAsync()
    {
        if (!CanUseFaceMaterialFiles() || !await EnsureSelectedFaceLoadedForMaterialsAsync() ||
            Editor is not { } editor || SelectedFace is not { } face)
            return false;
        var path = _dialogs.ChooseMaterialExportFile(false, face.ObjectName + ".materials.ron",
            Path.GetDirectoryName(_standaloneImportPath ?? _packageWorkspace?.SourcePath));
        if (path is null) return false;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var values = editor.Material.CaptureInterchangeData();
            var game = ResolveMaterialFileGame();
            var target = new MaterialFaceTarget(face.ProfileKey,
                IsPlayerWorkspace || RonNpcDonorResolver.IsPlayerOrCreator(face.InstancedPath) ||
                RonNpcDonorResolver.IsPlayerOrCreator(_loadedFace?.Document.BaseHeadReference?.InstancedPath));
            var text = MaterialRonCodec.WriteMfe(FaceMaterialFileService.Capture(game, face.ObjectName, values) with
            {
                DefaultFace = MorphFaceProfileRegistry.CreateDefault().Profiles.Any(profile =>
                    profile.Game == game && profile.Key == target.ProfileKey) ? target : null
            });
            await MeshMaterialFileService.WriteAsync(path, text,
                _standaloneImportPath ?? _packageWorkspace?.SourcePath ?? path);
            Status = Loc.Format("loc_Shell_Status_ExportedMfeMaterialSettingsTo", Path.GetFileName(path));
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("Face material export failed.", exception);
            ErrorMessage = exception.Message;
            Status = Loc.Text("loc_Shell_Status_MaterialExportFailedTheCurrent2");
            return false;
        }
        finally { IsBusy = false; }
    }

    private async Task ImportFaceMaterialsAsync(string? sourcePath = null)
    {
        if (!CanUseFaceMaterialFiles() || !await EnsureSelectedFaceLoadedForMaterialsAsync() ||
            Editor is not { } editor)
            return;
        var path = sourcePath ?? _dialogs.ChooseMaterialImportFile(false,
            Path.GetDirectoryName(_standaloneImportPath ?? _packageWorkspace?.SourcePath));
        if (path is null) return;
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_ReadingMaterialSettings");
        try
        {
            var text = await File.ReadAllTextAsync(path);
            var imported = FaceMaterialFileService.Read(text, allowTse: IsPlayerWorkspace);
            if (!ReferenceEquals(Editor, editor)) return;
            var warnings = imported.Warnings.Concat(await editor.MergeMaterialDataAsync(imported.Parameters)).ToList();
            Status = Loc.Format(imported.IsTse ? "loc_Shell_Status_ImportedTseMaterialSettings" : "loc_Shell_Status_ImportedMaterialSettingsFrom", Path.GetFileName(path));
            if (warnings.Count > 0)
            {
                foreach (var warning in warnings) AppLog.Warning(warning);
                const int displayLimit = 8;
                var details = string.Join("\n\n", warnings.Take(displayLimit));
                if (warnings.Count > displayLimit)
                    details += Loc.Format("loc_Shell_MaterialFiles_MoreWarningsInLog", warnings.Count - displayLimit);
                _dialogs.ShowInformation(Loc.Text("loc_Shell_DialogTitle_MaterialsImportedWithWarnings"), AppendDatabaseImportGuidance(details));
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Face material import failed.", exception);
            ErrorMessage = exception.Message;
            Status = Loc.Text("loc_Shell_Status_MaterialImportFailedTheCurrent2");
        }
        finally { IsBusy = false; }
    }

    private Task<bool> EnsureSelectedFaceLoadedForMaterialsAsync() =>
        Editor is not null && SelectedFace?.InstancedPath == LoadedFacePath
            ? Task.FromResult(true)
            : LoadSelectedFaceAsync(confirmUnsavedChanges: true);

    private MorphFaceGame ResolveMaterialFileGame()
    {
        if (_standaloneGame is { } standaloneGame) return standaloneGame;
        if (_packageWorkspace is null || LoadedFacePath is null)
            throw new InvalidOperationException(Loc.Text("loc_Shell_Error_FaceWorkspaceHasNoSourcePackage"));
        var reference = MorphFaceReferenceInspector.Inspect(_packageWorkspace.SourcePath)
            .SingleOrDefault(value => string.Equals(value.FacePath, LoadedFacePath,
                StringComparison.OrdinalIgnoreCase));
        if (reference is null || reference.Game == MorphFaceGame.Unsupported)
            throw new InvalidOperationException(Loc.Format("loc_Shell_Error_CouldNotDetermineGameForFace", LoadedFacePath));
        return reference.Game;
    }

    private void RaiseMaterialFileCanExecuteChanged()
    {
        _exportTseMaterialsCommand.RaiseCanExecuteChanged();
        _importTseMaterialsCommand.RaiseCanExecuteChanged();
        _exportMaterialsCommand.RaiseCanExecuteChanged();
        _importMaterialsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsMaterialFileWorkspace));
    }
}
