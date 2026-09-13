using System.IO;
using System.Windows.Input;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

/// <summary>Routes the four detached MESH file actions through the material interchange service.</summary>
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

    private void RaiseMaterialFileCanExecuteChanged()
    {
        _exportTseMaterialsCommand.RaiseCanExecuteChanged();
        _importTseMaterialsCommand.RaiseCanExecuteChanged();
        _exportMaterialsCommand.RaiseCanExecuteChanged();
        _importMaterialsCommand.RaiseCanExecuteChanged();
    }
}
