using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
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
            var provenance = CreateRonExportProvenance(selected);
            await Task.Run(() => _packageContextService.ExportRon(
                workspacePath,
                selected.InstancedPath,
                destination,
                provenance));
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

    private RonExportProvenance? CreateRonExportProvenance(BioMorphFaceListItem selected)
    {
        if (_loadedFace is null)
        {
            return null;
        }
        var archetype = selected.ProfileTag.Trim('[', ']');
        if (archetype is not ("ALN" or "ASA" or "BAT" or "HMM" or "HMF" or "KRO" or "SAL" or "TUF" or "TUR"))
        {
            return null;
        }
        var basePath = _loadedFace.Document.BaseHeadReference?.InstancedPath ?? string.Empty;
        var player = IsPlayerWorkspace ||
                     basePath.Contains("Player_Base_", StringComparison.OrdinalIgnoreCase) ||
                     basePath.Contains("Player_Iconic_", StringComparison.OrdinalIgnoreCase) ||
                     basePath.Contains("CharacterCreation_Base_", StringComparison.OrdinalIgnoreCase);
        if (player && Editor?.CanEditMorphFeatures != true)
        {
            return null;
        }
        return new RonExportProvenance(
            RonExportProducer.MFE,
            typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            _loadedFace.Game,
            archetype,
            player);
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
        if (extension == ".ron")
        {
            await RouteRonImportAsync(sourcePath, game);
            return;
        }
        var hasMatchingPccContext = CanMutatePackageContext() && _loadedFace?.Game == game;
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

    private async Task RouteRonImportAsync(string sourcePath, MorphFaceGame targetGame)
    {
        RonExportProvenance? provenance;
        try
        {
            provenance = await Task.Run(() => TseHeadMorphRon.ReadProvenance(sourcePath));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"The RON provenance could not be read: {exception.Message}";
            Status = "RON import failed; the current workspace was not changed.";
            return;
        }

        // Untagged Trilogy Save Editor RONs follow the accepted HMM/HMF Player route.
        if (provenance is null)
        {
            await ImportStandaloneMorphAsync(sourcePath, targetGame);
            return;
        }

        if (!StandaloneNpcMorphImportService.AreGamesCompatible(provenance.Game, targetGame))
        {
            ErrorMessage = $"A {provenance.Game} RON cannot be imported directly into {targetGame}. " +
                           "LE3 cross-game morphs require the explicit conversion workflow.";
            Status = "RON import failed; the current workspace was not changed.";
            return;
        }

        var isHuman = provenance.Archetype is "HMM" or "HMF";
        var destination = provenance.PlayerMorph
            ? RonImportDestination.PlayerWorkspace
            : RonImportDestination.NpcFace;
        var archetype = provenance.Archetype;
        if (isHuman)
        {
            var choice = _dialogs.ChooseRonImportDestination(
                targetGame,
                [new RonNpcArchetypeOption(archetype, archetype == "HMM" ? "Human Male" : "Human Female")],
                allowPlayer: true);
            if (choice is null)
            {
                return;
            }
            destination = choice.Destination;
            archetype = choice.NpcArchetypeKey ?? archetype;
        }

        if (destination == RonImportDestination.PlayerWorkspace)
        {
            if (!isHuman)
            {
                ErrorMessage = $"{provenance.Archetype} has no supported standalone Player Morph route.";
                Status = "RON import failed; the current workspace was not changed.";
                return;
            }
            await ImportStandaloneMorphAsync(sourcePath, targetGame);
            return;
        }

        await ImportStandaloneNpcMorphAsync(
            sourcePath, provenance.Game, targetGame, archetype);
    }

    private async Task ImportStandaloneNpcMorphAsync(
        string sourcePath,
        MorphFaceGame sourceGame,
        MorphFaceGame targetGame,
        string archetype)
    {
        var suggestedName = SuggestImportName(
            SanitizeObjectName(Path.GetFileNameWithoutExtension(sourcePath)), []);
        var objectName = _dialogs.ChooseStandaloneMorphName(suggestedName, []);
        if (objectName is null || !await EnsureCanAbandonWorkspaceAsync())
        {
            return;
        }

        IsBusy = true;
        _npcImportCancellation = new CancellationTokenSource();
        var cancellationToken = _npcImportCancellation.Token;
        _cancelNpcImportCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancelNpcImport));
        ErrorMessage = null;
        Status = $"Preparing a native {targetGame} {archetype} NPC workspace…";
        StandaloneNpcMorphImportResult? imported = null;
        FaceEditorViewModel? preparedEditor = null;
        IReadOnlyList<string> importWarnings = [];
        try
        {
            var textureCatalog = await _referenceService.ReadTextureCatalogAsync(targetGame, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!textureCatalog.IsAvailable)
            {
                throw new InvalidOperationException(
                    $"The {targetGame} mesh/texture database is unavailable. Build it in Mesh/Texture Databases before importing an NPC RON.");
            }
            var donor = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault())
                .Resolve(targetGame, archetype, textureCatalog.MorphFaceTemplates);
            var sourceCatalog = sourceGame == targetGame
                ? textureCatalog
                : await _referenceService.ReadTextureCatalogAsync(sourceGame, cancellationToken);
            if (!sourceCatalog.IsAvailable)
            {
                throw new InvalidOperationException(
                    $"The {sourceGame} mesh/texture database is unavailable. Build it in Mesh/Texture Databases before transferring this NPC RON.");
            }
            var assets = await Task.Run(() => StandalonePlayerAssetCatalog.ForRon(
                targetGame, sourcePath, textureCatalog.Candidates, donor.PackagePath), cancellationToken);
            imported = await Task.Run(() => new StandaloneNpcMorphImportService().ImportNpcRon(
                new StandaloneNpcMorphImportRequest(
                    sourceGame,
                    targetGame,
                    archetype,
                    sourcePath,
                    donor.PackagePath,
                    donor.FacePath,
                    objectName,
                    assets,
                    IsVerifiedNativeNpcDonor: true,
                    SourceTextureCatalog: sourceCatalog.Candidates,
                    TargetTextureCatalog: textureCatalog.Candidates,
                    CancellationToken: cancellationToken)), cancellationToken);
            importWarnings = imported.SaveResult.Warnings;

            // Read and prepare the complete candidate before replacing the active editor.
            var candidatePath = imported.Workspace.WorkingPath;
            var catalogTask = _catalogService.ReadAsync(candidatePath, cancellationToken);
            var referencesTask = _referenceService.ReadCatalogAsync(candidatePath, cancellationToken);
            await Task.WhenAll(catalogTask, referencesTask);
            var catalog = await catalogTask;
            var references = await referencesTask;
            var selected = catalog.Faces.FirstOrDefault(face =>
                string.Equals(face.InstancedPath, imported.ImportedFacePath,
                    StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException(
                "The imported NPC face was absent from its verified workspace catalogue.");
            var preview = await _previewLoadService.LoadAsync(
                candidatePath,
                selected.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken,
                geometryMode: MorphFaceGeometryMode.RelativeBake);
            if (!preview.EditingSession.CanEditMorphFeatures)
            {
                throw new InvalidDataException(
                    "The imported NPC face does not have a usable native morph target set: " +
                    preview.EditingSession.EditBlockReason);
            }
            var textureProfile = TextureCatalogProfiles.For(preview.Profile);
            preparedEditor = new FaceEditorViewModel(
                preview.EditingSession,
                preview.Profile.UiProfile,
                preview.MaterialEditingSession,
                _colorDialog,
                _referenceService,
                candidatePath,
                references.Textures,
                references.SkeletalMeshes,
                preview.Loaded.Document.HairMeshReference,
                preview.Loaded.Document.OtherMeshReferences,
                SetEditorError,
                preview.Profile.Key,
                _randomisationCatalog,
                randomisationInclusionState: _randomisationInclusionState,
                registryTextureCandidates: [],
                textureCatalogProfile: textureProfile,
                isTextureRegistryAvailable: false,
                ignoresAuthoredGeometry: preview.Profile.IgnoresAuthoredGeometry);

            cancellationToken.ThrowIfCancellationRequested();
            CancelPendingLoad();
            SetEditor(null, null);
            DisposePackageWorkspace();
            _packageWorkspace = imported.Workspace;
            imported = null;
            _standaloneGame = targetGame;
            _isStandaloneNpcWorkspace = true;
            _standaloneImportPath = Path.GetFullPath(sourcePath);
            SetDetachedMeshSource(null);
            _fixedBakeFacePaths.Clear();
            _relativeBakeFacePaths.Clear();
            _relativeBakeFacePaths.Add(selected.InstancedPath);
            _hasWorkspaceChanges = false;
            PackagePath = _packageWorkspace.SourcePath;
            _referenceCatalog = references;
            FaceSearchText = string.Empty;
            Faces.Clear();
            foreach (var face in catalog.Faces)
            {
                Faces.Add(face);
            }
            SelectedFace = selected;
            LoadedFacePath = preview.Loaded.Document.Source.InstancedPath;
            FaceDetails = $"{preview.Loaded.BaseHead.Topology.VertexCount:N0} vertices · " +
                          $"{preview.Loaded.Document.BakedLods.Count} authored LODs · " +
                          "native NPC relative bake";
            SetEditor(preparedEditor, preview.Loaded);
            preparedEditor = null;
            HasPreview = true;
            var cameraFamily = PreviewCameraGrouping.ForProfile(preview.Profile.Key);
            var resetCamera = _previewCameraFamily is not null &&
                              !string.Equals(_previewCameraFamily, cameraFamily,
                                  StringComparison.OrdinalIgnoreCase);
            _previewCameraFamily = cameraFamily;
            PreviewSceneReady?.Invoke(preview.Scene, resetCamera);
            OnPropertyChanged(nameof(PackageName));
            OnPropertyChanged(nameof(PackageDisplayName));
            OnDirtyStateChanged();
            RaiseFaceContextCanExecuteChanged();
            _ = LoadTextureRegistryAsync(
                Editor!, targetGame, preview.Profile, textureProfile, CancellationToken.None);
            Status = $"Imported {selected.DisplayName} as a {targetGame} {archetype} NPC morph; " +
                     "authored geometry preserved with native morph editing ready.";
            foreach (var warning in importWarnings.Concat(preview.Loaded.Warnings))
            {
                AppLog.Warning(warning);
            }
            if (importWarnings.Count > 0)
            {
                Status = $"Imported {selected.DisplayName} with {importWarnings.Count} asset or static-LOD warning(s).";
                _dialogs.ShowInformation(
                    "NPC morph imported with warnings",
                    "The authored data was retained. Review these preview or material limitations:\n\n- " +
                    string.Join("\n- ", importWarnings));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = null;
            Status = "NPC import cancelled; the current workspace was not changed.";
        }
        catch (Exception exception)
        {
            AppLog.Error($"Standalone NPC import failed for '{sourcePath}' as {targetGame} {archetype}.", exception);
            ErrorMessage = $"The standalone NPC morph could not be imported: {exception.Message}";
            Status = "NPC import failed; the current workspace was not changed.";
        }
        finally
        {
            preparedEditor?.Dispose();
            imported?.Dispose();
            _npcImportCancellation?.Dispose();
            _npcImportCancellation = null;
            _cancelNpcImportCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanCancelNpcImport));
            IsBusy = false;
        }
    }

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
                                     !_isStandaloneNpcWorkspace &&
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
                _isStandaloneNpcWorkspace = false;
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
                _isStandaloneNpcWorkspace = false;
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
                _isStandaloneNpcWorkspace = false;
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
        _isStandaloneNpcWorkspace = false;
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
