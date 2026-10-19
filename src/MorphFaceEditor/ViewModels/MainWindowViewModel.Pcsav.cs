using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PcsavHeadMorphWorkspace? _pcsavWorkspace;
    // Native Save can be current while the disposable PCC still holds an older editing projection.
    private bool _pcsavDraftPendingInPackage;
    private MorphFaceDocument? _pcsavSavedDraft;

    private async Task OpenCareerSaveAsync(string path)
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            Status = Loc.Text("loc_Pcsav_Reading");
            PcsavHeadMorphWorkspace source;
            try
            {
                source = await Task.Run(() => PcsavHeadMorphWorkspace.Open(path));
            }
            finally { IsBusy = false; }
            if (!source.HasHeadMorph && !_dialogs.ConfirmCreateCareerMorph(source.FirstName)) return;
            IsBusy = true;
            HeadMorphPayload head;
            try { head = await Task.Run(() => source.HasHeadMorph ? source.Head :
                _packageContextService.CapturePlayerDefaultHead(source.Game, source.Sex)); }
            finally { IsBusy = false; }
            await ImportStandaloneMorphAsync(path, source.Game, career: source, playerHead: head);
            if (ReferenceEquals(_pcsavWorkspace, source) && ErrorMessage is null)
                Status = Loc.Format(source.HasHeadMorph ? "loc_Pcsav_Opened" : "loc_Pcsav_NewHead", source.FirstName);
        }
        catch (Exception exception)
        {
            AppLog.Error($"PCSAV:OPEN-FAILED FILE:{path}", exception);
            ErrorMessage = FormatPcsavError(exception);
            Status = Loc.Text("loc_Shell_Status_SelectedFileCouldNotOpen");
        }
    }

    private async Task<bool> SaveCareerFileAsync(string? destination = null)
    {
        if (_pcsavWorkspace is not { } career || _sourceFile is not { } source ||
            WorkspacePackagePath is not { } workspace) return false;
        using var saveLock = new SaveControlLock(this);
        var editor = Editor;
        var draft = editor?.CreateDraft();
        var direct = editor is { HasPendingRepair: false } && draft is { ScalpMaterialSelection: null } &&
            !editor.HasAssetReferenceChanges(draft);
        if (!direct && (Editor?.IsDirty == true || _pcsavDraftPendingInPackage) && !await FlushEditorToWorkspaceAsync()) return false;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (direct)
            {
                await Task.Run(() => career.SaveEditedHead(draft!, destination));
                _pcsavDraftPendingInPackage |= editor!.IsDirty;
                editor.AcceptSavedState();
            }
            else
                await Task.Run(() => career.SaveEditedHead(
                    _packageContextService.CaptureHead(workspace, source.FacePath), destination));
            _pcsavSavedDraft = Editor?.SavedDraft;
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
            AppLog.Error($"PCSAV:SAVE-FAILED FILE:{career.SourcePath}", exception);
            ErrorMessage = FormatPcsavError(exception);
            Status = Loc.Text("loc_FileSave_EditsRetained");
            return false;
        }
        finally { IsBusy = false; }
    }

    private static string FormatPcsavError(Exception exception) => UserFacingMessage.FromException(exception) is not null
        ? UserFacingMessageFormatter.Format(exception)
        : Loc.Text(exception is PcsavException failure
        ? failure.Reason switch
        {
            PcsavError.UnsupportedFormat => "loc_Pcsav_Unsupported",
            PcsavError.SourceChanged => "loc_FileSave_SourceChanged",
            _ => "loc_Pcsav_InvalidFile"
        }
        : "loc_Pcsav_OperationFailed");

    private async Task<bool> StageSavedCareerDraftForReloadAsync()
    {
        var discardImport = _pcsavWorkspace?.HasPendingHeadImport == true;
        if ((!_pcsavDraftPendingInPackage && !discardImport) || Editor is not { } editor) return true;
        using var saveLock = new SaveControlLock(this);
        var current = editor.CreateDraft();
        var saved = (_pcsavSavedDraft ?? editor.SavedDraft) with
        { Source = current.Source, SourceFingerprint = current.SourceFingerprint };
        try
        {
            await Task.Run(() => _packageWriter.SaveExisting(saved));
            _referenceService.InvalidatePackage(saved.Source.PackagePath);
            if (discardImport)
            {
                _pcsavWorkspace!.DiscardHeadImport();
                _hasWorkspaceChanges = false;
                OnDirtyStateChanged();
            }
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("PCSAV:STAGE-SAVED-HEAD-FAILED", exception);
            ErrorMessage = FormatPcsavError(exception);
            Status = Loc.Text("loc_FileSave_EditsRetained");
            return false;
        }
    }

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
            AppLog.Error($"PCSAV:HEAD-DELETE-FAILED FILE:{career.SourcePath}", exception);
            ErrorMessage = FormatPcsavError(exception);
            Status = Loc.Text("loc_FileSave_EditsRetained");
        }
        finally { IsBusy = false; }
    }
}
