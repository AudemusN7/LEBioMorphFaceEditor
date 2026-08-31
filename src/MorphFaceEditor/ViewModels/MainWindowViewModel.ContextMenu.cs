using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

/// <summary>Coordinates right-click export operations while package mutation stays in the LE boundary.</summary>
public sealed partial class MainWindowViewModel
{
    private bool CanUseFaceContextMenu() =>
        !IsBusy && SelectedFace is not null && WorkspacePackagePath is not null;

    private bool CanPasteMorphData() =>
        CanUseFaceContextMenu() && _clipboard.Contains(MorphFaceClipboardKind.Morph);

    private bool CanPasteMaterialData() =>
        CanUseFaceContextMenu() && _clipboard.Contains(MorphFaceClipboardKind.Material);

    public void RefreshClipboardCommandAvailability()
    {
        _pasteMorphDataCommand.RaiseCanExecuteChanged();
        _pasteMaterialDataCommand.RaiseCanExecuteChanged();
    }

    private void RaiseFaceContextCanExecuteChanged()
    {
        _cloneMorphCommand.RaiseCanExecuteChanged();
        _deleteMorphCommand.RaiseCanExecuteChanged();
        _convertMorphCommand.RaiseCanExecuteChanged();
        _copyMorphDataCommand.RaiseCanExecuteChanged();
        _pasteMorphDataCommand.RaiseCanExecuteChanged();
        _copyMaterialDataCommand.RaiseCanExecuteChanged();
        _pasteMaterialDataCommand.RaiseCanExecuteChanged();
        _importMorphCommand.RaiseCanExecuteChanged();
        _exportMorphPskCommand.RaiseCanExecuteChanged();
        _exportMorphGltfCommand.RaiseCanExecuteChanged();
        _exportMorphMd5Command.RaiseCanExecuteChanged();
        _exportMorphRonCommand.RaiseCanExecuteChanged();
        _assignMorphToActorCommand.RaiseCanExecuteChanged();
        _assignMaterialsToActorCommand.RaiseCanExecuteChanged();
    }

    private async Task DeleteMorphAsync()
    {
        if (SelectedFace is not { } source || WorkspacePackagePath is not { } workspacePath ||
            !_dialogs.ConfirmDeleteMorph(source.InstancedPath))
        {
            return;
        }
        if (!await EnsureCanAbandonEditorChangesAsync())
        {
            RestoreLoadedFaceSelection();
            return;
        }

        var selectedIndex = Faces.IndexOf(source);
        var preferredFacePath = Faces.ElementAtOrDefault(selectedIndex + 1)?.InstancedPath ??
                                Faces.ElementAtOrDefault(selectedIndex - 1)?.InstancedPath;
        IsBusy = true;
        ErrorMessage = null;
        Status = $"Deleting {source.DisplayName}…";
        var packageModified = false;
        try
        {
            AppLog.Information($"Trashing face '{source.InstancedPath}' and its descendants in temporary workspace '{workspacePath}'.");
            await Task.Run(() => _packageContextService.DeleteMorph(workspacePath, source.InstancedPath));
            packageModified = true;
            MarkWorkspaceChanged();
            if (!await RefreshWorkspaceAsync(preferredFacePath))
            {
                ErrorMessage = "The morph was deleted, but the refreshed workspace could not be opened.";
                Status = "Delete completed; workspace reload failed.";
                return;
            }
            Status = $"Deleted {source.InstancedPath}; save the package to commit the change.";
            AppLog.Information($"Face deletion verified: '{source.InstancedPath}'.");
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("Delete Morph", "The morph could not be deleted", exception, packageModified);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CloneMorphAsync()
    {
        if (SelectedFace is not { } source || WorkspacePackagePath is not { } workspacePath)
        {
            return;
        }

        var siblingNames = Faces
            .Where(face => string.Equals(GetParentPath(face.InstancedPath), GetParentPath(source.InstancedPath),
                StringComparison.OrdinalIgnoreCase))
            .Select(face => face.ObjectName)
            .ToArray();
        var objectName = _dialogs.ChooseCloneName(SuggestCloneName(source.ObjectName, siblingNames), siblingNames);
        if (objectName is null || !await EnsureCanAbandonEditorChangesAsync())
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = $"Cloning {source.DisplayName}…";
        var packageModified = false;
        try
        {
            AppLog.Information($"Cloning face '{source.InstancedPath}' as '{objectName}' in temporary workspace '{workspacePath}'.");
            var result = await Task.Run(() =>
                _packageContextService.CloneMorph(workspacePath, source.InstancedPath, objectName));
            packageModified = true;
            MarkWorkspaceChanged();
            if (!await RefreshWorkspaceAsync(result.FaceInstancedPath))
            {
                ErrorMessage = "The morph was cloned, but the refreshed workspace could not be opened.";
                Status = "Clone completed; workspace reload failed.";
                return;
            }
            Status = $"Cloned and verified {result.FaceInstancedPath}.";
            AppLog.Information($"Face clone verified: '{result.FaceInstancedPath}'.");
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("Clone Morph", "The morph could not be cloned", exception, packageModified);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConvertMorphAsync()
    {
        if (SelectedFace is not { } source || WorkspacePackagePath is not { } workspacePath || PackagePath is null)
        {
            return;
        }
        var sourceGame = source.ProfileKey.StartsWith("le3-", StringComparison.OrdinalIgnoreCase)
            ? MorphFaceEditor.LegendaryExplorer.MorphFaceGame.LE3
            : source.ProfileKey.StartsWith("le2-", StringComparison.OrdinalIgnoreCase)
                ? MorphFaceEditor.LegendaryExplorer.MorphFaceGame.LE2
                : MorphFaceEditor.LegendaryExplorer.MorphFaceGame.LE1;
        var request = _dialogs.ChooseMorphConversionDestination(
            sourceGame,
            $"{source.ObjectName}_Converted.pcc",
            PackagePath);
        if (request is null || !await FlushSelectedExportSourceAsync(source))
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = $"Converting {source.DisplayName} to {request.TargetGame}…";
        try
        {
            AppLog.Information(
                $"Converting face '{source.InstancedPath}' from '{workspacePath}' to {request.TargetGame} " +
                $"in '{request.DestinationPackagePath}' (new package: {request.CreateNewPackage}).");
            var result = await Task.Run(() => _conversionService.Convert(new MorphFaceConversionRequest(
                workspacePath,
                source.InstancedPath,
                request.TargetGame,
                request.DestinationPackagePath,
                request.CreateNewPackage,
                request.TemplatePackagePath)));
            Status = result.SaveResult.Warnings.Count == 0
                ? $"Converted and verified {result.SaveResult.FaceInstancedPath} ({result.TargetProfile})."
                : $"Converted and verified {result.SaveResult.FaceInstancedPath} with {result.SaveResult.Warnings.Count} warning(s).";
            var dropped = result.DroppedFeatureCount == 0
                ? "No non-zero source sliders were unsupported."
                : $"Unsupported non-zero source sliders reset to neutral: {result.DroppedFeatureCount}.";
            var geometryWarning = result.SourceGeometryValidated
                ? string.Empty
                : "\nThe source used baked read-only geometry; serialized sliders and bone residuals transferred, " +
                  "but shape detail not represented by them could not be preserved.";
            var textureWarnings = result.SaveResult.Warnings.Count == 0
                ? string.Empty
                : "\n\nTexture/dependency report:\n- " + string.Join("\n- ", result.SaveResult.Warnings);
            if (result.SaveResult.Warnings.Count > 0)
            {
                AppLog.Warning(
                    $"Morph conversion completed with {result.SaveResult.Warnings.Count} warning(s): " +
                    string.Join("; ", result.SaveResult.Warnings));
            }
            _dialogs.ShowInformation(
                "Morph converted",
                $"The morph was converted successfully.\n\n" +
                $"Source profile: {result.SourceProfile}\nTarget profile: {result.TargetProfile}\n" +
                $"Package: {result.SaveResult.PackagePath}\nFace: {result.SaveResult.FaceInstancedPath}\n" +
                $"Transferred non-zero sliders: {result.TransferredFeatureCount}\n{dropped}\n" +
                $"Material overrides (S/V/T): {result.ScalarCount}/{result.VectorCount}/{result.TextureCount}\n" +
                $"Baked destination LODs: {result.SaveResult.LodCount}" + geometryWarning + textureWarnings);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Morph conversion failed for '{source.InstancedPath}'.", exception);
            ErrorMessage = $"The morph could not be converted: {exception.Message}";
            Status = "Morph conversion failed; the source PCC was not modified.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CopyMorphDataAsync()
    {
        if (SelectedFace is not { } source || WorkspacePackagePath is not { } workspacePath)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = $"Copying morph data from {source.DisplayName}…";
        try
        {
            var data = IsLoaded(source) && Editor is not null
                ? ToMorphData(Editor.CreateDraft())
                : await Task.Run(() =>
                    _packageContextService.CaptureMorphData(workspacePath, source.InstancedPath));
            _clipboard.Set(MorphFaceClipboardPayload.Morph(source.ProfileKey, source.InstancedPath, data));
            _pasteMorphDataCommand.RaiseCanExecuteChanged();
            Status = $"Copied {data.MorphFeatures.Count} morph sliders, {data.FinalSkeleton.Count} final bones, and {data.BakedLods.Count} baked LODs.";
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("Copy Morph Data", "Morph data could not be copied", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PasteMorphDataAsync()
    {
        if (SelectedFace is not { } target || WorkspacePackagePath is null)
        {
            return;
        }

        try
        {
            var payload = _clipboard.Get(MorphFaceClipboardKind.Morph);
            EnsureMatchingProfile(payload, target);
            if (!await EnsureContextTargetLoadedAsync(target) || Editor is null)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            Status = $"Pasting morph data into {target.DisplayName}…";
            Editor.ApplyMorphData(payload.MorphData!);
            Status = $"Pasted morph data into {target.DisplayName}; save or switch faces to resolve the unsaved edit.";
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("Paste Morph Data", "Morph data could not be pasted", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CopyMaterialDataAsync()
    {
        if (SelectedFace is not { } source || WorkspacePackagePath is not { } workspacePath)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = $"Copying material data from {source.DisplayName}…";
        try
        {
            var data = IsLoaded(source) && Editor is not null
                ? ToMaterialData(Editor.CreateDraft())
                : await Task.Run(() =>
                    _packageContextService.CaptureMaterialData(workspacePath, source.InstancedPath));
            _clipboard.Set(MorphFaceClipboardPayload.Material(source.ProfileKey, source.InstancedPath, data));
            _pasteMaterialDataCommand.RaiseCanExecuteChanged();
            Status = $"Copied {data.Scalars.Count}/{data.Vectors.Count}/{data.Textures.Count} material scalar/vector/texture overrides.";
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("Copy Material Data", "Material data could not be copied", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PasteMaterialDataAsync()
    {
        if (SelectedFace is not { } target || WorkspacePackagePath is null)
        {
            return;
        }

        try
        {
            var payload = _clipboard.Get(MorphFaceClipboardKind.Material);
            EnsureMatchingProfile(payload, target);
            if (!await EnsureContextTargetLoadedAsync(target) || Editor is null)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            Status = $"Pasting material data into {target.DisplayName}…";
            await Editor.ApplyMaterialDataAsync(payload.MaterialData!);
            Status = $"Pasted material data into {target.DisplayName}; save or switch faces to resolve the unsaved edit.";
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("Paste Material Data", "Material data could not be pasted", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void EnsureMatchingProfile(MorphFaceClipboardPayload payload, BioMorphFaceListItem target)
    {
        if (!string.Equals(payload.ProfileKey, target.ProfileKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The clipboard contains {ProfileLabel(payload.ProfileKey)} data, but {target.DisplayName} is {ProfileLabel(target.ProfileKey)}. " +
                "Morph and material data can only be pasted between matching species and game profiles.");
        }
    }

    private async Task<bool> EnsureContextTargetLoadedAsync(BioMorphFaceListItem target)
    {
        if (IsLoaded(target) && Editor is not null)
        {
            return true;
        }
        SelectedFace = Faces.FirstOrDefault(face => string.Equals(
            face.InstancedPath,
            target.InstancedPath,
            StringComparison.OrdinalIgnoreCase));
        return SelectedFace is not null && await LoadSelectedFaceAsync(confirmUnsavedChanges: true);
    }

    private bool IsLoaded(BioMorphFaceListItem face) => string.Equals(
        LoadedFacePath,
        face.InstancedPath,
        StringComparison.OrdinalIgnoreCase);

    private static MorphFaceEditor.Core.Domain.MorphFaceMorphData ToMorphData(
        MorphFaceEditor.Core.Domain.MorphFaceDocument document) => new(
        document.MorphFeatures,
        document.FinalSkeleton,
        document.BakedLods);

    private static MorphFaceEditor.Core.Domain.MorphFaceMaterialData ToMaterialData(
        MorphFaceEditor.Core.Domain.MorphFaceDocument document) => new(
        document.MaterialOverrides.Scalars,
        document.MaterialOverrides.Vectors,
        document.MaterialOverrides.Textures);

    private void ReportContextOperationFailure(
        string operation,
        string message,
        Exception exception,
        bool packageModified = false)
    {
        AppLog.Error($"{operation} failed.", exception);
        ErrorMessage = packageModified
            ? $"Temporary edits were retained, but the workspace could not be refreshed: {exception.Message}"
            : $"{message}: {exception.Message}";
        Status = packageModified
            ? $"{operation} updated temporary edits, but the workspace reload failed."
            : $"{operation} failed; the source PCC was not modified.";
    }

    private static string SuggestCloneName(string sourceName, IReadOnlyCollection<string> existingNames)
    {
        var stem = $"{sourceName}_Copy";
        if (!existingNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return stem;
        }
        for (var number = 2; ; number++)
        {
            var candidate = $"{stem}{number}";
            if (!existingNames.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }

    private static string GetParentPath(string path)
    {
        var separator = path.LastIndexOf('.');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string ProfileLabel(string profileKey) =>
        string.IsNullOrWhiteSpace(profileKey) ? "an unknown profile" : profileKey.Replace('-', ' ');
}
