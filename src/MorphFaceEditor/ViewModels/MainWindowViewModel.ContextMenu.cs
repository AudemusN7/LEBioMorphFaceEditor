using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

/// <summary>Coordinates right-click export operations while package mutation stays in the LE boundary.</summary>
public sealed partial class MainWindowViewModel
{
    private bool CanUseFaceContextMenu() =>
        !IsBusy && SelectedFace is not null && WorkspacePackagePath is not null;

    private bool CanMutatePackageContext() =>
        CanUseFaceContextMenu() && _packageWorkspace?.CanCommit == true;

    private bool CanCopyMorphData() =>
        CanUseFaceContextMenu() && !IsAlnMorphClipboardFace(SelectedFace);

    private bool CanUseMaterialContextMenu() =>
        CanUseFaceContextMenu() &&
        (_packageWorkspace?.CanCommit == true || IsStandaloneFixedBakeFace());

    private bool IsStandaloneFixedBakeFace() =>
        IsStandaloneWorkspace &&
        SelectedFace is { } selectedFace &&
        (_fixedBakeFacePaths.Contains(selectedFace.InstancedPath) ||
         _relativeBakeFacePaths.Contains(selectedFace.InstancedPath));

    private bool CanPasteMorphData() =>
        CanMutatePackageContext() && !IsAlnMorphClipboardFace(SelectedFace) &&
        _clipboard.Contains(MorphFaceClipboardKind.Morph);

    private static bool IsAlnMorphClipboardFace(BioMorphFaceListItem? face) =>
        face is not null &&
        (face.ProfileTag.Equals("[ALN]", StringComparison.OrdinalIgnoreCase) ||
         face.ProfileKey.EndsWith("-vorcha", StringComparison.OrdinalIgnoreCase));

    private bool CanPasteMaterialData() =>
        CanUseMaterialContextMenu() && _clipboard.Contains(MorphFaceClipboardKind.Material);

    public void RefreshClipboardCommandAvailability()
    {
        RaiseMaterialFileCanExecuteChanged();
        _pasteMorphDataCommand.RaiseCanExecuteChanged();
        _pasteMaterialDataCommand.RaiseCanExecuteChanged();
    }

    private void RaiseFaceContextCanExecuteChanged()
    {
        RaiseMaterialFileCanExecuteChanged();
        _cloneMorphCommand.RaiseCanExecuteChanged();
        _deleteMorphCommand.RaiseCanExecuteChanged();
        _convertMorphCommand.RaiseCanExecuteChanged();
        _copyMorphDataCommand.RaiseCanExecuteChanged();
        _pasteMorphDataCommand.RaiseCanExecuteChanged();
        _copyMaterialDataCommand.RaiseCanExecuteChanged();
        _pasteMaterialDataCommand.RaiseCanExecuteChanged();
        _importMorphCommand.RaiseCanExecuteChanged();
        _newPlayerMorphCommand.RaiseCanExecuteChanged();
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
        Status = Loc.Format("loc_Shell_Status_Deleting", source.DisplayName);
        var packageModified = false;
        try
        {
            AppLog.Information($"Trashing face '{source.InstancedPath}' and its descendants in temporary workspace '{workspacePath}'.");
            await Task.Run(() => _packageContextService.DeleteMorph(workspacePath, source.InstancedPath));
            packageModified = true;
            MarkWorkspaceChanged();
            if (!await RefreshWorkspaceAsync(preferredFacePath))
            {
                ErrorMessage = Loc.Text("loc_Shell_ErrorMessage_TheMorphWasDeletedBut");
                Status = Loc.Text("loc_Shell_Status_DeleteCompletedWorkspaceReloadFailed");
                return;
            }
            Status = Loc.Format("loc_Shell_Status_DeletedSaveThePackageTo", source.InstancedPath);
            AppLog.Information($"Face deletion verified: '{source.InstancedPath}'.");
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("loc_Shell_Operation_DeleteMorph", "loc_Shell_Error_MorphCouldNotDelete", exception, packageModified);
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
        Status = Loc.Format("loc_Shell_Status_Cloning", source.DisplayName);
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
                ErrorMessage = Loc.Text("loc_Shell_ErrorMessage_TheMorphWasClonedBut");
                Status = Loc.Text("loc_Shell_Status_CloneCompletedWorkspaceReloadFailed");
                return;
            }
            Status = Loc.Format("loc_Shell_Status_ClonedAndVerified", result.FaceInstancedPath);
            AppLog.Information($"Face clone verified: '{result.FaceInstancedPath}'.");
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("loc_Shell_Operation_CloneMorph", "loc_Shell_Error_MorphCouldNotClone", exception, packageModified);
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
        Status = Loc.Format("loc_Shell_Status_ConvertingTo", source.DisplayName, request.TargetGame);
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
                ? Loc.Format("loc_Shell_Status_ConvertedAndVerified", result.SaveResult.FaceInstancedPath, result.TargetProfile)
                : Loc.Format("loc_Shell_Status_ConvertedAndVerifiedWithWarnings", result.SaveResult.FaceInstancedPath, result.SaveResult.Warnings.Count);
            var dropped = result.DroppedFeatureCount == 0
                ? Loc.Text("loc_Shell_Conversion_NoUnsupportedSourceSliders")
                : Loc.Format("loc_Shell_Conversion_UnsupportedSlidersReset", result.DroppedFeatureCount);
            var geometryWarning = result.SourceGeometryValidated
                ? string.Empty
                : Loc.Text("loc_Shell_Conversion_BakedGeometryWarning");
            var textureWarnings = result.SaveResult.Warnings.Count == 0
                ? string.Empty
                : Loc.Format("loc_Shell_Conversion_TextureDependencyReport", string.Join("\n- ", result.SaveResult.Warnings));
            if (result.SaveResult.Warnings.Count > 0)
            {
                AppLog.Warning(
                    $"Morph conversion completed with {result.SaveResult.Warnings.Count} warning(s): " +
                    string.Join("; ", result.SaveResult.Warnings));
            }
            _dialogs.ShowInformation(
                Loc.Text("loc_Shell_DialogTitle_MorphConverted"),
                Loc.Format("loc_Shell_Dialog_MorphConverted", result.SourceProfile, result.TargetProfile,
                    result.SaveResult.PackagePath, result.SaveResult.FaceInstancedPath,
                    result.TransferredFeatureCount, dropped, result.ScalarCount, result.VectorCount,
                    result.TextureCount, result.SaveResult.LodCount, geometryWarning, textureWarnings));
        }
        catch (Exception exception)
        {
            AppLog.Error($"Morph conversion failed for '{source.InstancedPath}'.", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_TheMorphCouldNotBe", exception.Message);
            Status = Loc.Text("loc_Shell_Status_MorphConversionFailedTheSource");
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
        if (IsAlnMorphClipboardFace(source))
        {
            Status = Loc.Text("loc_Shell_Status_MorphDataCopyIsUnavailable");
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Format("loc_Shell_Status_CopyingMorphDataFrom", source.DisplayName);
        try
        {
            var data = IsLoaded(source) && Editor is not null
                ? ToMorphData(Editor.CreateDraft())
                : await Task.Run(() =>
                    _packageContextService.CaptureMorphData(workspacePath, source.InstancedPath));
            _clipboard.Set(MorphFaceClipboardPayload.Morph(source.ProfileKey, source.InstancedPath, data));
            _pasteMorphDataCommand.RaiseCanExecuteChanged();
            Status = Loc.Format("loc_Shell_Status_CopiedMorphSlidersFinalBones", data.MorphFeatures.Count, data.FinalSkeleton.Count, data.BakedLods.Count);
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("loc_Shell_Operation_CopyMorphData", "loc_Shell_Error_MorphDataCouldNotCopy", exception);
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
        if (IsAlnMorphClipboardFace(target))
        {
            Status = Loc.Text("loc_Shell_Status_MorphDataPasteIsUnavailable");
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
            Status = Loc.Format("loc_Shell_Status_PastingMorphDataInto", target.DisplayName);
            Editor.ApplyMorphData(payload.MorphData!);
            Status = Loc.Format("loc_Shell_Status_PastedMorphDataIntoSave", target.DisplayName);
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("loc_Shell_Operation_PasteMorphData", "loc_Shell_Error_MorphDataCouldNotPaste", exception);
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
        Status = Loc.Format("loc_Shell_Status_CopyingMaterialDataFrom", source.DisplayName);
        try
        {
            var data = IsLoaded(source) && Editor is not null
                ? ToMaterialData(Editor.CreateDraft())
                : await Task.Run(() =>
                    _packageContextService.CaptureMaterialData(workspacePath, source.InstancedPath));
            _clipboard.Set(MorphFaceClipboardPayload.Material(source.ProfileKey, source.InstancedPath, data));
            _pasteMaterialDataCommand.RaiseCanExecuteChanged();
            Status = Loc.Format("loc_Shell_Status_CopiedMaterialScalarVectorTexture", data.Scalars.Count, data.Vectors.Count, data.Textures.Count);
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("loc_Shell_Operation_CopyMaterialData", "loc_Shell_Error_MaterialDataCouldNotCopy", exception);
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
            Status = Loc.Format("loc_Shell_Status_PastingMaterialDataInto", target.DisplayName);
            await Editor.ApplyMaterialDataAsync(payload.MaterialData!);
            Status = Loc.Format("loc_Shell_Status_PastedMaterialDataIntoSave", target.DisplayName);
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("loc_Shell_Operation_PasteMaterialData", "loc_Shell_Error_MaterialDataCouldNotPaste", exception);
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
            throw new InvalidOperationException(Loc.Format("loc_Shell_Error_ClipboardProfileMismatch",
                ProfileLabel(payload.ProfileKey), target.DisplayName, ProfileLabel(target.ProfileKey)));
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
        var operationName = Loc.Text(operation);
        var messageText = Loc.Text(message);
        AppLog.Error($"{Loc.ReadEmbeddedEnglish()[operation]} failed.", exception);
        ErrorMessage = packageModified
            ? Loc.Format("loc_Shell_Error_TemporaryEditsRetainedWorkspaceRefreshFailed", exception.Message)
            : Loc.Format("loc_Shell_Error_ContextOperationFailed", messageText, exception.Message);
        Status = packageModified
            ? Loc.Format("loc_Shell_Status_ContextOperationUpdatedWorkspaceReloadFailed", operationName)
            : Loc.Format("loc_Shell_Status_ContextOperationFailedSourceUnmodified", operationName);
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

    private static string ProfileLabel(string profileKey)
    {
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            return Loc.Text("loc_Shell_Profile_Unknown");
        }
        var parts = profileKey.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return profileKey;
        }
        var gameKey = parts[0].ToLowerInvariant() switch
        {
            "le1" => "loc_Shell_Game_LE1",
            "le2" => "loc_Shell_Game_LE2",
            "le3" => "loc_Shell_Game_LE3",
            _ => null
        };
        var species = string.Join("-", parts.Skip(1));
        var speciesKey = species switch
        {
            "human-male" => "loc_Shell_ProfileSpecies_HumanMale",
            "human-female" => "loc_Shell_ProfileSpecies_HumanFemale",
            "female-turian" => "loc_Shell_ProfileSpecies_FemaleTurian",
            "asari" => "loc_Shell_ProfileSpecies_Asari",
            "salarian" => "loc_Shell_ProfileSpecies_Salarian",
            "turian" => "loc_Shell_ProfileSpecies_Turian",
            "krogan" => "loc_Shell_ProfileSpecies_Krogan",
            "batarian" => "loc_Shell_ProfileSpecies_Batarian",
            "vorcha" => "loc_Shell_ProfileSpecies_Vorcha",
            _ => null
        };
        return gameKey is not null && speciesKey is not null
            ? Loc.Format("loc_Shell_Profile_Label", Loc.Text(gameKey), Loc.Text(speciesKey))
            : profileKey;
    }
}
