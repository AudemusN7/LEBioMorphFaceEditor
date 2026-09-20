using System.IO;
using System.Windows.Input;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

/// <summary>Routes material file actions through the MESH or face workspace interchange path.</summary>
public sealed partial class MainWindowViewModel
{
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
                    editor.OtherMeshes.Select(value => value.Value).ToArray())
                : MaterialRonCodec.WriteMfe(MeshMaterialFileService.Capture(workspace, game, name, values));
            await MeshMaterialFileService.WriteAsync(path, text, workspace.Source.SourcePath);
            Status = $"Exported {(tse ? "TSE material settings" : "MFE material settings and slot assignments")} to {Path.GetFileName(path)}.";
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("MESH material export failed.", exception);
            ErrorMessage = exception.Message;
            Status = "Material export failed; the current mesh edit is retained.";
            return false;
        }
        finally { IsBusy = false; }
    }

    private async Task ImportMeshMaterialsAsync(bool tse)
    {
        if (!CanUseMeshMaterialFiles() || Editor is not { CustomMaterials: { } workspace } editor || _standaloneGame is not { } game) return;
        var path = _dialogs.ChooseMaterialImportFile(tse, Path.GetDirectoryName(_standaloneImportPath));
        if (path is null) return;
        IsBusy = true;
        ErrorMessage = null;
        Status = "Preparing material settings and texture references…";
        try
        {
            var text = await File.ReadAllTextAsync(path);
            var prepared = await new MeshMaterialFileService(_referenceService).PrepareAsync(
                text, tse, workspace, editor.CustomMaterialOptions, game);
            if (!ReferenceEquals(Editor, editor)) return;
            editor.ApplyMeshMaterialImport(prepared);
            Status = prepared.IsTse
                ? $"Imported TSE material settings from {Path.GetFileName(path)} as one undoable edit."
                : $"Imported {Path.GetFileName(path)} as one undoable material edit.";
            if (prepared.Warnings.Count > 0)
            {
                foreach (var warning in prepared.Warnings) AppLog.Warning(warning);
                const int displayLimit = 8;
                var details = string.Join("\n\n", prepared.Warnings.Take(displayLimit));
                if (prepared.Warnings.Count > displayLimit)
                    details += $"\n\nPlus {prepared.Warnings.Count - displayLimit} more warnings. All details are in the application log.";
                _dialogs.ShowInformation("Materials imported with warnings", details);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("MESH material import failed.", exception);
            ErrorMessage = exception.Message;
            Status = "Material import failed; the current mesh edit is retained.";
        }
        finally { IsBusy = false; }
    }

    private async Task ExportFaceMaterialsAsync()
    {
        if (!CanUseFaceMaterialFiles() || !await EnsureSelectedFaceLoadedForMaterialsAsync() ||
            Editor is not { } editor || SelectedFace is not { } face)
            return;
        var path = _dialogs.ChooseMaterialExportFile(false, face.ObjectName + ".materials.ron",
            Path.GetDirectoryName(_standaloneImportPath ?? _packageWorkspace?.SourcePath));
        if (path is null) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var values = editor.Material.CaptureInterchangeData();
            var game = ResolveMaterialFileGame();
            var text = MaterialRonCodec.WriteMfe(FaceMaterialFileService.Capture(game, face.ObjectName, values));
            await MeshMaterialFileService.WriteAsync(path, text,
                _standaloneImportPath ?? _packageWorkspace?.SourcePath ?? path);
            Status = $"Exported MFE material settings to {Path.GetFileName(path)}.";
        }
        catch (Exception exception)
        {
            AppLog.Error("Face material export failed.", exception);
            ErrorMessage = exception.Message;
            Status = "Material export failed; the current face edit is retained.";
        }
        finally { IsBusy = false; }
    }

    private async Task ImportFaceMaterialsAsync()
    {
        if (!CanUseFaceMaterialFiles() || !await EnsureSelectedFaceLoadedForMaterialsAsync() ||
            Editor is not { } editor)
            return;
        var path = _dialogs.ChooseMaterialImportFile(false,
            Path.GetDirectoryName(_standaloneImportPath ?? _packageWorkspace?.SourcePath));
        if (path is null) return;
        IsBusy = true;
        ErrorMessage = null;
        Status = "Reading material settings…";
        try
        {
            var text = await File.ReadAllTextAsync(path);
            var imported = FaceMaterialFileService.Read(text, allowTse: IsPlayerWorkspace);
            if (!ReferenceEquals(Editor, editor)) return;
            var warnings = imported.Warnings.Concat(await editor.MergeMaterialDataAsync(imported.Parameters)).ToList();
            Status = imported.IsTse
                ? $"Imported TSE material settings from {Path.GetFileName(path)} as one undoable edit."
                : $"Imported material settings from {Path.GetFileName(path)} as one undoable edit.";
            if (warnings.Count > 0)
            {
                foreach (var warning in warnings) AppLog.Warning(warning);
                const int displayLimit = 8;
                var details = string.Join("\n\n", warnings.Take(displayLimit));
                if (warnings.Count > displayLimit)
                    details += $"\n\nPlus {warnings.Count - displayLimit} more warnings. All details are in the application log.";
                _dialogs.ShowInformation("Materials imported with warnings", details);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Face material import failed.", exception);
            ErrorMessage = exception.Message;
            Status = "Material import failed; the current face edit is retained.";
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
            throw new InvalidOperationException("The current face workspace has no source package.");
        var reference = MorphFaceReferenceInspector.Inspect(_packageWorkspace.SourcePath)
            .SingleOrDefault(value => string.Equals(value.FacePath, LoadedFacePath,
                StringComparison.OrdinalIgnoreCase));
        if (reference is null || reference.Game == MorphFaceGame.Unsupported)
            throw new InvalidOperationException($"Could not determine the game for '{LoadedFacePath}'.");
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
