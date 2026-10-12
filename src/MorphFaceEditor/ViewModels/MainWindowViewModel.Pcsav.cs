using System.IO;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PcsavHeadMorphWorkspace? _pcsavWorkspace;

    private async Task OpenCareerSaveAsync(string path)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"MFE-CareerImport-{Guid.NewGuid():N}");
        var ron = Path.Combine(scratch, "PlayerHead.ron");
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            Status = Loc.Text("loc_Pcsav_Reading");
            PcsavHeadMorphWorkspace source;
            try
            {
                source = await Task.Run(() =>
                {
                    var document = PcsavHeadMorphWorkspace.Open(path);
                    return document;
                });
            }
            finally { IsBusy = false; }
            if (!source.HasHeadMorph && !_dialogs.ConfirmCreateCareerMorph(source.FirstName)) return;
            IsBusy = true;
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(scratch);
                    if (source.HasHeadMorph) source.ExportHeadRon(ron);
                    else _packageContextService.ExportRon(
                        StandalonePlayerMorphImportService.ResolveInstalledSeed(source.Game),
                        StandalonePlayerMorphImportService.ResolvePlayerTemplate(source.Game, source.Sex), ron);
                });
            }
            finally { IsBusy = false; }
            await ImportStandaloneMorphAsync(ron, source.Game, career: source);
            if (ReferenceEquals(_pcsavWorkspace, source) && ErrorMessage is null)
                Status = Loc.Format(source.HasHeadMorph ? "loc_Pcsav_Opened" : "loc_Pcsav_NewHead", source.FirstName);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Career save could not open: '{path}'.", exception);
            ErrorMessage = FormatPcsavError(exception);
            Status = Loc.Text("loc_Shell_Status_SelectedFileCouldNotOpen");
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException e) { AppLog.Warning($"Career import scratch cleanup failed: {e.Message}"); }
        }
    }

    private async Task<bool> SaveCareerFileAsync(string? destination = null)
    {
        if (_pcsavWorkspace is not { } career || _sourceFile is not { } source ||
            WorkspacePackagePath is not { } workspace) return false;
        using var saveLock = new SaveControlLock(this);
        if (Editor?.IsDirty == true && !await FlushEditorToWorkspaceAsync()) return false;
        IsBusy = true;
        ErrorMessage = null;
        var ron = Path.Combine(Path.GetTempPath(), $"MFE-CareerSave-{Guid.NewGuid():N}.ron");
        try
        {
            await Task.Run(() =>
            {
                _packageContextService.ExportRon(workspace, source.FacePath, ron);
                career.SaveEditedRon(ron, destination);
            });
            _sourceFile = source with { Path = career.SourcePath, Fingerprint = FileFingerprint(career.SourcePath) };
            _standaloneImportPath = career.SourcePath;
            OnPropertyChanged(nameof(PackageName));
            _hasWorkspaceChanges = false;
            OnDirtyStateChanged();
            Status = Loc.Format("loc_Shell_Status_PackageSaved", Path.GetFileName(career.SourcePath));
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Career save failed: '{career.SourcePath}'.", exception);
            ErrorMessage = FormatPcsavError(exception);
            Status = Loc.Text("loc_FileSave_EditsRetained");
            return false;
        }
        finally
        {
            try { if (File.Exists(ron)) File.Delete(ron); }
            finally { IsBusy = false; }
        }
    }

    private static string FormatPcsavError(Exception exception) => Loc.Text(exception is PcsavException failure
        ? failure.Reason switch
        {
            PcsavError.UnsupportedFormat => "loc_Pcsav_Unsupported",
            PcsavError.SourceChanged => "loc_FileSave_SourceChanged",
            _ => "loc_Pcsav_InvalidFile"
        }
        : "loc_Pcsav_OperationFailed");

    private async Task DeleteCareerHeadMorphAsync()
    {
        if (_pcsavWorkspace is not { } career || SelectedFace is not { } face ||
            !_dialogs.ConfirmDeleteCareerMorph(face.ObjectName)) return;
        using var saveLock = new SaveControlLock(this);
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await Task.Run(career.DeleteHeadMorph);
            ClearWorkspace();
            Status = Loc.Format("loc_Pcsav_Deleted", Path.GetFileName(career.SourcePath));
        }
        catch (Exception exception)
        {
            AppLog.Error($"Career head deletion failed: '{career.SourcePath}'.", exception);
            ErrorMessage = FormatPcsavError(exception);
            Status = Loc.Text("loc_FileSave_EditsRetained");
        }
        finally { IsBusy = false; }
    }
}
