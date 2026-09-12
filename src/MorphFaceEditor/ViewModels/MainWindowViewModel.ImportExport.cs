using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
using MorphFaceEditor.Presentation;
using MorphFaceEditor.Services;

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
        var game = _dialogs.ChooseStandaloneImportGame();
        if (game is null)
        {
            return;
        }
        var sourcePath = _dialogs.ChooseMorphImportFile(
            _standaloneImportPath is not null
                ? Path.GetDirectoryName(_standaloneImportPath)
                : PackagePath is null ? null : Path.GetDirectoryName(PackagePath));
        if (sourcePath is null)
        {
            return;
        }
        await RouteMorphImportAsync(sourcePath, game.Value);
    }

    private async Task ImportMorphAsync(string sourcePath)
    {
        var game = _dialogs.ChooseStandaloneImportGame();
        if (game is not null)
        {
            await RouteMorphImportAsync(sourcePath, game.Value);
        }
    }

    private async Task RouteMorphImportAsync(string sourcePath, MorphFaceGame game)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var hasMatchingPccContext = CanMutatePackageContext() && _loadedFace?.Game == game;
        if (RequiresRonImportDestination(sourcePath, hasMatchingPccContext))
        {
            var destination = _dialogs.ChooseRonImportDestination(
                SelectedFace?.DisplayName ?? LoadedFacePath ?? "the selected BioMorphFace");
            if (destination is null)
            {
                return;
            }
            if (destination == RonImportDestination.PlayerWorkspace)
            {
                await ImportStandaloneMorphAsync(sourcePath, game);
                return;
            }
            await ImportMorphIntoPackageAsync(sourcePath);
            return;
        }
        if (extension is ".me2headmorph" or ".me3headmorph")
        {
            await ImportStandaloneMorphAsync(sourcePath, game);
            return;
        }
        if (hasMatchingPccContext)
        {
            await ImportMorphIntoPackageAsync(sourcePath);
            return;
        }
        await ImportStandaloneMorphAsync(sourcePath, game);
    }

    internal static bool RequiresRonImportDestination(
        string sourcePath,
        bool hasMatchingPccContext) =>
        hasMatchingPccContext &&
        Path.GetExtension(sourcePath).Equals(".ron", StringComparison.OrdinalIgnoreCase);

    private async Task ImportStandaloneMorphAsync(string sourcePath, MorphFaceGame game)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var isRon = extension == ".ron";
        var isLegacy = extension is ".me2headmorph" or ".me3headmorph";
        var isMesh = extension is ".psk" or ".pskx" or ".gltf" or ".glb";
        if (!isRon && !isLegacy && !isMesh)
        {
            ErrorMessage = "Standalone import accepts Trilogy Save Editor .ron, Gibbed " +
                           ".me2headmorph/.me3headmorph, and PSK/PSKX or glTF/GLB mesh files.";
            Status = "Standalone import requires a supported head-morph or mesh file.";
            return;
        }
        if (isLegacy)
        {
            try
            {
                StandaloneLegacyHeadMorphImportService.ValidateSourceGame(game, sourcePath);
            }
            catch (Exception exception)
            {
                ErrorMessage = exception.Message;
                Status = "The selected game does not match the Gibbed head morph.";
                return;
            }
        }
        StandalonePlayerMeshAnalysis? meshAnalysis = null;
        if (isMesh)
        {
            IsBusy = true;
            ErrorMessage = null;
            Status = $"Inspecting {Path.GetFileName(sourcePath)} topology…";
            try
            {
                meshAnalysis = await Task.Run(() => _standaloneMeshImportService.Analyze(game, sourcePath));
            }
            catch (Exception exception)
            {
                AppLog.Error($"Mesh analysis failed for '{sourcePath}' as {game}.", exception);
                ErrorMessage = $"The mesh could not be imported: {exception.Message}";
                Status = "Mesh import failed; the current workspace was not changed.";
                return;
            }
            finally
            {
                IsBusy = false;
            }
        }
        var appendingToCurrentGame = _standaloneGame == game && _packageWorkspace is not null &&
                                     (meshAnalysis is null || meshAnalysis.Recognition is not null);
        var existingNames = appendingToCurrentGame
            ? Faces.Select(face => face.ObjectName).ToArray()
            : Array.Empty<string>();
        var suggestedName = SuggestImportName(
            SanitizeObjectName(Path.GetFileNameWithoutExtension(sourcePath)),
            existingNames);
        var objectName = _dialogs.ChooseStandaloneMorphName(suggestedName, existingNames);
        if (objectName is null)
        {
            return;
        }

        var canContinue = appendingToCurrentGame
            ? await EnsureCanAbandonEditorChangesAsync()
            : await EnsureCanAbandonWorkspaceAsync();
        if (!canContinue)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = $"Importing {Path.GetFileName(sourcePath)} as a standalone {game} player morph…";
        try
        {
            if (meshAnalysis is { Recognition: null })
            {
                var detached = await Task.Run(() =>
                    _detachedMeshPreviewLoadService.Load(game, meshAnalysis.ImportedMesh));
                PublishDetachedMeshWorkspace(detached, sourcePath, objectName, game);
                return;
            }

            StandalonePlayerAssetCatalog? assetCatalog = null;
            if (isRon)
            {
                var textureCatalog = await _referenceService.ReadTextureCatalogAsync(game);
                assetCatalog = await Task.Run(() => StandalonePlayerAssetCatalog.ForRon(
                    game,
                    sourcePath,
                    textureCatalog.Candidates));
            }

            string importedFacePath;
            IReadOnlyList<string> importWarnings;
            StandalonePlayerMeshRecognition? meshRecognition = null;
            if (appendingToCurrentGame)
            {
                var workspace = _packageWorkspace!;
                MorphFaceSaveResult saveResult;
                if (isRon)
                {
                    saveResult = await Task.Run(() => _standaloneImportService.ImportPlayerRonIntoWorkspace(
                        game, sourcePath, objectName, workspace, assetCatalog));
                }
                else if (isLegacy)
                {
                    saveResult = await Task.Run(() => _standaloneLegacyImportService.ImportIntoWorkspace(
                        game, sourcePath, objectName, workspace));
                }
                else
                {
                    var meshResult = await Task.Run(() => _standaloneMeshImportService.ImportIntoWorkspace(
                        game, objectName, workspace, meshAnalysis!));
                    saveResult = meshResult.SaveResult;
                    meshRecognition = meshResult.Recognition;
                }
                importedFacePath = saveResult.FaceInstancedPath;
                importWarnings = saveResult.Warnings;
            }
            else if (isRon)
            {
                var result = await Task.Run(() => _standaloneImportService.ImportPlayerRon(
                    game, sourcePath, objectName, assetCatalog));
                CancelPendingLoad();
                SetEditor(null, null);
                DisposePackageWorkspace();
                _packageWorkspace = result.Workspace;
                _standaloneGame = game;
                SetDetachedMeshSource(null);
                _fixedBakeFacePaths.Clear();
                _relativeBakeFacePaths.Clear();
                PackagePath = result.Workspace.SourcePath;
                importedFacePath = result.ImportedFacePath;
                importWarnings = result.SaveResult.Warnings;
            }
            else if (isLegacy)
            {
                var result = await Task.Run(() => _standaloneLegacyImportService.Import(
                    game, sourcePath, objectName));
                CancelPendingLoad();
                SetEditor(null, null);
                DisposePackageWorkspace();
                _packageWorkspace = result.Workspace;
                _standaloneGame = game;
                SetDetachedMeshSource(null);
                _fixedBakeFacePaths.Clear();
                _relativeBakeFacePaths.Clear();
                PackagePath = result.Workspace.SourcePath;
                importedFacePath = result.ImportedFacePath;
                importWarnings = result.SaveResult.Warnings;
            }
            else
            {
                var result = await Task.Run(() => _standaloneMeshImportService.Import(
                    game, objectName, meshAnalysis!));
                CancelPendingLoad();
                SetEditor(null, null);
                DisposePackageWorkspace();
                _packageWorkspace = result.Workspace;
                _standaloneGame = game;
                SetDetachedMeshSource(null);
                _fixedBakeFacePaths.Clear();
                _relativeBakeFacePaths.Clear();
                PackagePath = result.Workspace.SourcePath;
                importedFacePath = result.ImportedFacePath;
                importWarnings = result.SaveResult.Warnings;
                meshRecognition = result.Recognition;
            }
            _standaloneImportPath = Path.GetFullPath(sourcePath);
            if (isRon)
            {
                // A classified Player RON's LOD0 array is ordered for the exact
                // selected-game HMM/HMF template. Its authored bake therefore
                // supports canonical target deltas without inverse fitting.
                _relativeBakeFacePaths.Add(importedFacePath);
                _fixedBakeFacePaths.Remove(importedFacePath);
            }
            else
            {
                _fixedBakeFacePaths.Add(importedFacePath);
                _relativeBakeFacePaths.Remove(importedFacePath);
            }
            _hasWorkspaceChanges = false;
            OnPropertyChanged(nameof(PackageName));
            OnPropertyChanged(nameof(PackageDisplayName));
            OnDirtyStateChanged();
            if (!await RefreshWorkspaceAsync(importedFacePath, clearSearch: true))
            {
                ErrorMessage ??= "The player head morph was imported, but its detached workspace could not be opened.";
                Status = "Standalone import completed; workspace load failed.";
            }
            else if (importWarnings.Count > 0)
            {
                foreach (var warning in importWarnings)
                {
                    AppLog.Warning(warning);
                }
                Status = $"Imported {importedFacePath} with {importWarnings.Count} visible asset warning(s).";
                _dialogs.ShowInformation(
                    "Player morph imported with warnings",
                    "The morph was imported and its authored references were retained, but some assets could not be resolved for preview:\n\n- " +
                    string.Join("\n- ", importWarnings));
            }
            else if (isRon)
            {
                Status = $"Imported {importedFacePath} as a canonical {game} Player RON; " +
                         "authored geometry preserved with relative morph editing ready.";
            }
            else if (meshRecognition is not null)
            {
                Status = $"Imported {importedFacePath} as a recognised {game} " +
                         $"{meshRecognition.Sex} player mesh ({meshRecognition.CoordinateSystem}); " +
                         "fixed-bake geometry, canonical bone rig and material editing ready.";
            }
        }
        catch (Exception exception)
        {
            AppLog.Error($"Standalone player import failed for '{sourcePath}' as {game}.", exception);
            ErrorMessage = $"The standalone player morph could not be imported: {exception.Message}";
            Status = "Standalone import failed; the current workspace was not changed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PublishDetachedMeshWorkspace(
        DetachedMeshPreviewLoadResult result,
        string sourcePath,
        string objectName,
        MorphFaceGame game)
    {
        CancelPendingLoad();
        SetEditor(null, null);
        DisposePackageWorkspace();
        SetDetachedMeshSource(result.Detached.Source);
        _standaloneGame = game;
        _standaloneImportPath = Path.GetFullPath(sourcePath);
        _fixedBakeFacePaths.Clear();
        _relativeBakeFacePaths.Clear();
        _hasWorkspaceChanges = false;
        PackagePath = Path.GetFullPath(sourcePath);
        _referenceCatalog = new PackageReferenceCatalog([], []);
        FaceSearchText = string.Empty;
        Faces.Clear();
        var face = new BioMorphFaceListItem(
            0,
            result.Preview.Loaded.Document.Source.InstancedPath,
            objectName,
            result.Preview.Profile.ExportTag,
            result.Preview.Profile.ExportTagColor,
            result.Preview.Profile.Key);
        Faces.Add(face);
        SelectedFace = face;
        LoadedFacePath = face.InstancedPath;

        var topology = result.Preview.Loaded.BaseHead.Topology;
        var previewAttachmentOptions = result.PreviewAttachments
            .Select(identity => new PackageAssetListItem(identity))
            .ToArray();
        var editor = new FaceEditorViewModel(
            result.Preview.EditingSession,
            result.Preview.Profile.UiProfile,
            result.Preview.MaterialEditingSession,
            _colorDialog,
            _referenceService,
            sourcePath,
            [],
            previewAttachmentOptions,
            null,
            [],
            SetEditorError,
            result.Preview.Profile.Key,
            _randomisationCatalog,
            randomisationInclusionState: _randomisationInclusionState,
            ignoresAuthoredGeometry: false,
            allowsAttachmentEditing: true,
            customMaterialWorkspace: result.CustomMaterials,
            customMaterialOptions: result.MaterialOptions,
            materialRandomisationProfileKey: $"{game.ToString().ToLowerInvariant()}-human-female",
            previewOnlyAttachments: true);
        SetEditor(editor, result.Preview.Loaded);
        FaceDetails = $"{topology.VertexCount:N0} vertices · {topology.IndexCount / 3:N0} triangles · " +
                      $"{topology.Sections.Count} sections · {topology.ReferenceSkeleton.Count} verified bones · " +
                      $"{result.CustomMaterials?.UsedSlots.Count ?? 0} used material slots · detached LOD0 preview";
        HasPreview = true;
        var cameraFamily = PreviewCameraGrouping.ForProfile(result.Preview.Profile.Key);
        var resetCamera = !string.Equals(_previewCameraFamily, cameraFamily, StringComparison.OrdinalIgnoreCase);
        _previewCameraFamily = cameraFamily;
        PreviewSceneReady?.Invoke(result.Preview.Scene, resetCamera);
        var textureCatalogProfile = TextureCatalogProfiles.For(result.Preview.Profile);
        _ = LoadTextureRegistryAsync(
            editor, game, result.Preview.Profile, textureCatalogProfile, CancellationToken.None);
        foreach (var warning in result.Detached.Warnings)
        {
            AppLog.Warning(warning);
        }
        Status = result.Preview.EditingSession.CanEditBones
            ? $"Loaded {objectName} as an unrecognised custom mesh; material assignment, LOD0 preview and verified bone controls ready with exact source data retained. Morph controls are disabled."
            : $"Loaded {objectName} as an unrecognised custom mesh; material assignment and LOD0 preview ready with exact source data retained. Morph and bone controls are disabled.";
        OnPropertyChanged(nameof(PackageName));
        OnPropertyChanged(nameof(PackageDisplayName));
        OnDirtyStateChanged();
        RaiseFaceContextCanExecuteChanged();
    }

    private void SetDetachedMeshSource(ImportedMeshAsset? source)
    {
        _detachedMeshSource = source;
        OnPropertyChanged(nameof(IsDetachedMeshWorkspace));
    }

    private async Task ImportMorphIntoPackageAsync(string sourcePath)
    {
        if (SelectedFace is not { } template || WorkspacePackagePath is not { } workspacePath)
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

    private static string SuggestImportName(
        string sourceName,
        IReadOnlyCollection<string> existingNames)
    {
        if (!existingNames.Contains(sourceName, StringComparer.OrdinalIgnoreCase))
        {
            return sourceName;
        }
        for (var number = 2; ; number++)
        {
            var candidate = $"{sourceName}_{number}";
            if (!existingNames.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }
}
