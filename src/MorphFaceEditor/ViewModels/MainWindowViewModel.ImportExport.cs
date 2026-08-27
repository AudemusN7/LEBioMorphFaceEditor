using System.IO;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;

namespace MorphFaceEditor.ViewModels;

/// <summary>Coordinates file interchange while format and package work remain behind services.</summary>
public sealed partial class MainWindowViewModel
{
    private async Task ExportMorphMeshAsync(MorphMeshFormat format)
    {
        if (SelectedFace is not { } selected || WorkspacePackagePath is not { } workspacePath)
        {
            return;
        }
        var outputDirectory = _dialogs.ChooseMeshExportDirectory(
            PackagePath is null ? null : Path.GetDirectoryName(PackagePath));
        if (outputDirectory is null || !await FlushSelectedExportSourceAsync(selected))
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = $"Baking {selected.DisplayName} and exporting {format} through UModel…";
        try
        {
            var result = await _interchangeService.ExportMeshAsync(
                workspacePath,
                selected.InstancedPath,
                outputDirectory,
                format);
            Status = $"Exported {selected.DisplayName} as {format} ({result.ProducedFiles.Count} file(s)).";
            _dialogs.ShowInformation(
                "Morph mesh exported",
                $"The baked morph was exported through UModel as {format}.\n\nOutput: {outputDirectory}");
        }
        catch (Exception exception)
        {
            AppLog.Error($"{format} morph export failed.", exception);
            ErrorMessage = $"The morph mesh could not be exported: {exception.Message}";
            Status = "Morph mesh export failed; the PCC was not modified.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportMorphRonAsync()
    {
        if (SelectedFace is not { } selected || WorkspacePackagePath is not { } workspacePath)
        {
            return;
        }
        var destination = _dialogs.ChooseRonExportFile(
            $"{selected.ObjectName}.ron",
            PackagePath is null ? null : Path.GetDirectoryName(PackagePath));
        if (destination is null || !await FlushSelectedExportSourceAsync(selected))
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = $"Exporting {selected.DisplayName} as Trilogy Save Editor RON…";
        try
        {
            await Task.Run(() => _packageContextService.ExportRon(
                workspacePath,
                selected.InstancedPath,
                destination));
            Status = $"Exported {selected.DisplayName} as {Path.GetFileName(destination)}.";
        }
        catch (Exception exception)
        {
            AppLog.Error("RON morph export failed.", exception);
            ErrorMessage = $"The RON head morph could not be exported: {exception.Message}";
            Status = "RON export failed; the PCC was not modified.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportMorphAsync()
    {
        if (SelectedFace is not { } template || WorkspacePackagePath is not { } workspacePath)
        {
            return;
        }
        var sourcePath = _dialogs.ChooseMorphImportFile(
            PackagePath is null ? null : Path.GetDirectoryName(PackagePath));
        if (sourcePath is null)
        {
            return;
        }
        var siblingNames = Faces
            .Where(face => string.Equals(GetParentPath(face.InstancedPath), GetParentPath(template.InstancedPath),
                StringComparison.OrdinalIgnoreCase))
            .Select(face => face.ObjectName)
            .ToArray();
        var suggestedName = SuggestCloneName(
            SanitizeObjectName(Path.GetFileNameWithoutExtension(sourcePath)),
            siblingNames);
        var objectName = _dialogs.ChooseCloneName(suggestedName, siblingNames);
        if (objectName is null || !await EnsureCanAbandonEditorChangesAsync())
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = $"Importing {Path.GetFileName(sourcePath)} using {template.DisplayName} as the profile template…";
        var packageModified = false;
        try
        {
            MorphFaceSaveResult result;
            MorphFaceEditor.Core.Deformation.MorphMeshFitResult? fit = null;
            var sourceExtension = Path.GetExtension(sourcePath);
            var isLegacy = sourceExtension.Equals(".me2headmorph", StringComparison.OrdinalIgnoreCase) ||
                           sourceExtension.Equals(".me3headmorph", StringComparison.OrdinalIgnoreCase);
            if (sourceExtension.Equals(".ron", StringComparison.OrdinalIgnoreCase))
            {
                result = await Task.Run(() => _packageContextService.ImportHeadMorph(
                    workspacePath,
                    template.InstancedPath,
                    objectName,
                    sourcePath));
            }
            else if (isLegacy)
            {
                IsBusy = false;
                if (!await EnsureContextTargetLoadedAsync(template) || Editor is null)
                {
                    return;
                }
                IsBusy = true;
                Status = $"Converting {Path.GetFileName(sourcePath)} to the selected Legendary Edition profile…";
                var legacy = await Task.Run(() => _packageContextService.ReadLegacyHeadMorph(sourcePath));
                Editor.ApplyMorphData(legacy.MorphData);
                var convertedDraft = Editor.CreateDraft();
                var convertedMorph = new MorphFaceEditor.Core.Domain.MorphFaceMorphData(
                    convertedDraft.MorphFeatures,
                    convertedDraft.FinalSkeleton,
                    convertedDraft.BakedLods);
                result = await Task.Run(() => _packageContextService.ImportConvertedLegacyHeadMorph(
                    workspacePath,
                    template.InstancedPath,
                    objectName,
                    sourcePath,
                    convertedMorph));
            }
            else
            {
                IsBusy = false;
                if (!await EnsureContextTargetLoadedAsync(template) || Editor is null || _loadedFace is null)
                {
                    return;
                }
                IsBusy = true;
                Status = $"Solving {Path.GetFileName(sourcePath)} against the selected morph-target profile…";
                var candidates = await Task.Run(() => _interchangeService.ReadMeshPositions(
                    sourcePath,
                    _loadedFace.BaseHead));
                fit = await Task.Run(() => Editor.FitMeshPositions(candidates));
                result = await Task.Run(() => _packageContextService.CloneMorphWithData(
                    workspacePath,
                    template.InstancedPath,
                    objectName,
                    fit.MorphData));
            }
            packageModified = true;
            MarkWorkspaceChanged();
            if (!await RefreshWorkspaceAsync(result.FaceInstancedPath))
            {
                ErrorMessage = "The morph was imported, but the refreshed workspace could not be opened.";
                Status = "Import completed; workspace reload failed.";
                return;
            }
            Status = fit is null
                ? isLegacy
                    ? $"Imported and verified {result.FaceInstancedPath}; converted legacy morph data to the selected LE profile."
                    : $"Imported and verified {result.FaceInstancedPath} from {sourceExtension.TrimStart('.')}."
                : fit.UsedSourcePrior
                    ? $"Imported and verified {result.FaceInstancedPath}; restored exported sliders and all stored LODs, " +
                      $"max mesh difference {fit.MaximumError:G4}."
                : $"Imported and verified {result.FaceInstancedPath}; recovered {fit.RecoveredFeatureCount} sliders, " +
                  $"RMS {fit.RootMeanSquareError:G4}, max {fit.MaximumError:G4}.";
            if (fit is { MaximumError: > 0.001f })
            {
                _dialogs.ShowInformation(
                    "Mesh projected onto morph sliders",
                    $"The mesh was imported as the nearest face representable by the selected profile.\n\n" +
                    $"Recovered sliders: {fit.RecoveredFeatureCount}\n" +
                    $"Coordinate mapping: {fit.CoordinateSystem}\n" +
                    $"RMS vertex error: {fit.RootMeanSquareError:G6}\n" +
                    $"Maximum vertex error: {fit.MaximumError:G6}\n\n" +
                    "Mesh-only files cannot recover metadata-only controls, bone-only controls, or manual final-skeleton edits.");
            }
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("Import Morph", "The morph could not be imported", exception, packageModified);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> FlushSelectedExportSourceAsync(BioMorphFaceListItem selected) =>
        !IsLoaded(selected) || Editor?.IsDirty != true || await FlushEditorToWorkspaceAsync();

    private static string SanitizeObjectName(string value)
    {
        var sanitized = new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "ImportedMorph";
        }
        return char.IsDigit(sanitized[0]) ? $"Morph_{sanitized}" : sanitized;
    }
}
