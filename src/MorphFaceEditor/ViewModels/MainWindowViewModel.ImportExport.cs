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
        Status = Loc.Format("loc_Shell_Status_BakingAndExportingThroughUmodel", selected.DisplayName, format);
        try
        {
            var result = await _interchangeService.ExportMeshAsync(
                workspacePath,
                selected.InstancedPath,
                outputDirectory,
                format);
            Status = Loc.Format("loc_Shell_Status_ExportedAsFileS", selected.DisplayName, format, result.ProducedFiles.Count);
            _dialogs.ShowInformation(
                Loc.Text("loc_Shell_DialogTitle_MorphMeshExported"),
                Loc.Format("loc_Shell_Dialog_MorphMeshExported", format, outputDirectory));
        }
        catch (Exception exception)
        {
            AppLog.Error($"{format} morph export failed.", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_TheMorphMeshCouldNot", exception.Message);
            Status = Loc.Text("loc_Shell_Status_MorphMeshExportFailedThe");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportMorphRonAsync()
    {
        if (!CanExportMorphRon() || SelectedFace is not { } selected || WorkspacePackagePath is not { } workspacePath)
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
        Status = Loc.Format("loc_Shell_Status_ExportingAsTrilogySaveEditor", selected.DisplayName);
        try
        {
            var isPlayerWorkspace = IsPlayerWorkspace;
            await Task.Run(() =>
            {
                var provenance = CreateRonExportProvenance(workspacePath, selected, isPlayerWorkspace);
                _packageContextService.ExportRon(workspacePath, selected.InstancedPath, destination, provenance);
            });
            Status = Loc.Format("loc_Shell_Status_ExportedAs", selected.DisplayName, Path.GetFileName(destination));
        }
        catch (Exception exception)
        {
            AppLog.Error("RON morph export failed.", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_TheRonHeadMorphCould", exception.Message);
            Status = Loc.Text("loc_Shell_Status_RonExportFailedThePcc");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExportMorphRon() => CanUseFaceContextMenu() &&
        SelectedFace is { SupportsMorphRonExport: true } face && HasSupportedRonArchetype(face.ProfileTag.Trim('[', ']'));

    public string? MorphRonExportUnavailableReason => SelectedFace is { } face &&
        (!face.SupportsMorphRonExport || !HasSupportedRonArchetype(face.ProfileTag.Trim('[', ']')))
            ? Loc.Text("loc_Shell_Error_RonProfileUnsupported") : null;

    private static bool HasSupportedRonArchetype(string archetype) =>
        archetype is "ALN" or "ASA" or "BAT" or "HMM" or "HMF" or "KRO" or "SAL" or "TUR";

    internal static RonExportProvenance CreateRonExportProvenance(
        string workspacePath, BioMorphFaceListItem selected, bool isPlayerWorkspace)
    {
        var reference = MorphFaceReferenceInspector.Inspect(workspacePath)
            .Single(face => face.FacePath.Equals(selected.InstancedPath, StringComparison.OrdinalIgnoreCase));
        var archetype = selected.ProfileTag.Trim('[', ']');
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var profile = profiles.Find(reference.Game, reference.FacePath, reference.BaseHeadPath) ??
                      profiles.Find(reference.Game, reference.FacePath, null);
        if (!HasSupportedRonArchetype(archetype) || !RonNpcDonorResolver.SupportsMorphRonExport(profile, reference.BaseHeadPath))
        {
            throw new InvalidOperationException(Loc.Text("loc_Shell_Error_RonProfileUnsupported"));
        }
        var basePath = reference.BaseHeadPath ?? string.Empty;
        var player = isPlayerWorkspace ||
                     reference.FacePath.Contains("Player_Base_", StringComparison.OrdinalIgnoreCase) ||
                     reference.FacePath.Contains("Player_Iconic_", StringComparison.OrdinalIgnoreCase) ||
                     reference.FacePath.Contains("CharacterCreation_Base_", StringComparison.OrdinalIgnoreCase) ||
                     basePath.Contains("Player_Base_", StringComparison.OrdinalIgnoreCase) ||
                     basePath.Contains("Player_Iconic_", StringComparison.OrdinalIgnoreCase) ||
                     basePath.Contains("CharacterCreation_Base_", StringComparison.OrdinalIgnoreCase);
        return new RonExportProvenance(
            RonExportProducer.MFE,
            typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            reference.Game,
            archetype,
            player);
    }

    private async Task NewPlayerMorphAsync()
    {
        var choice = _dialogs.ChooseNewPlayerMorph();
        if (choice is null) return;
        var directory = Path.Combine(Path.GetTempPath(), "MFE-NewPlayer-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(directory, "PlayerMorph.ron");
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Format("loc_Shell_Status_PreparingPlayerDefaults", choice.Game);
        try
        {
            Directory.CreateDirectory(directory);
            await Task.Run(() => _packageContextService.ExportRon(
                StandalonePlayerMorphImportService.ResolveInstalledSeed(choice.Game),
                StandalonePlayerMorphImportService.ResolvePlayerTemplate(choice.Game, choice.Sex), sourcePath));
            IsBusy = false;
            await ImportStandaloneMorphAsync(sourcePath, choice.Game, newPlayerMode: true);
            if (_standaloneImportPath == sourcePath)
            {
                _standaloneImportPath = null;
                if (ErrorMessage is null) Status = Loc.Format("loc_Shell_Status_CreatedFromPlayerDefaults", LoadedFacePath, choice.Game);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("New player morph creation failed.", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_ThePlayerMorphCouldNot", exception.Message);
            Status = Loc.Text("loc_Shell_Status_NewPlayerMorphFailedThe");
        }
        finally
        {
            IsBusy = false;
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (Directory.Exists(directory)) Directory.Delete(directory);
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
        if (Path.GetExtension(sourcePath).Equals(".ron", StringComparison.OrdinalIgnoreCase) &&
            await TryImportMaterialRonAsync(sourcePath)) return;
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
        // Mesh recognition belongs to the imported mesh, never to the face
        // currently selected in a PCC. Unknown topology opens Custom Mode.
        if (extension is ".me2headmorph" or ".me3headmorph" or ".psk" or ".pskx" or ".gltf" or ".glb")
        {
            await ImportStandaloneMorphAsync(sourcePath, game);
            return;
        }
        if (CanMutatePackageContext() && _loadedFace?.Game == game)
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
            if (await TryImportMaterialRonAsync(sourcePath)) return;
            provenance = await Task.Run(() => TseHeadMorphRon.ReadProvenance(sourcePath));
        }
        catch (Exception exception)
        {
            ErrorMessage = Loc.Format("loc_Shell_Error_TheRonProvenanceCouldNot", exception.Message);
            Status = Loc.Text("loc_Shell_Status_RonImportFailedTheCurrent");
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
            ErrorMessage = Loc.Format("loc_Shell_Error_RonCrossGameRequiresConversion", provenance.Game, targetGame);
            Status = Loc.Text("loc_Shell_Status_RonImportFailedTheCurrent");
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
                [new RonNpcArchetypeOption(archetype, Loc.Text(archetype == "HMM"
                    ? "loc_Shell_Archetype_HumanMale" : "loc_Shell_Archetype_HumanFemale"))],
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
                ErrorMessage = Loc.Format("loc_Shell_Error_HasNoSupportedStandalonePlayer", provenance.Archetype);
                Status = Loc.Text("loc_Shell_Status_RonImportFailedTheCurrent");
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
        Status = Loc.Format("loc_Shell_Status_PreparingANativeNpcWorkspace", targetGame, archetype);
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
                    Loc.Format("loc_Shell_Error_NpcTargetDatabaseUnavailable", targetGame));
            }
            var donor = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault())
                .Resolve(targetGame, archetype, textureCatalog.MorphFaceTemplates,
                    textureCatalog.OfficialPackagePaths);
            var sourceCatalog = sourceGame == targetGame
                ? textureCatalog
                : await _referenceService.ReadTextureCatalogAsync(sourceGame, cancellationToken);
            if (!sourceCatalog.IsAvailable)
            {
                throw new InvalidOperationException(
                    Loc.Format("loc_Shell_Error_NpcSourceDatabaseUnavailable", sourceGame));
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
                Loc.Text("loc_Shell_Error_ImportedNpcFaceMissingFromCatalogue"));
            var preview = await _previewLoadService.LoadAsync(
                candidatePath,
                selected.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken,
                geometryMode: MorphFaceGeometryMode.RelativeBake);
            if (!preview.EditingSession.CanEditMorphFeatures)
            {
                throw new InvalidDataException(
                    Loc.Format("loc_Shell_Error_ImportedNpcMorphTargetsUnavailable", preview.EditingSession.EditBlockReason));
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
            _faceDetailsPrefix = null;
            _faceDetailsHasBakedFallback = false;
            FaceDetails = Loc.Format("loc_Shell_FaceDetails_NpcNativeRelativeBake", preview.Loaded.BaseHead.Topology.VertexCount, preview.Loaded.Document.BakedLods.Count);
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
            Status = Loc.Format("loc_Shell_Status_ImportedAsNpcMorphReady", selected.DisplayName, targetGame, archetype);
            var allImportWarnings = importWarnings.Concat(preview.Loaded.Warnings).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var warning in allImportWarnings)
            {
                AppLog.Warning(warning);
            }
            if (allImportWarnings.Length > 0)
            {
                Status = Loc.Format("loc_Shell_Status_ImportedWithAssetOrStatic", selected.DisplayName, allImportWarnings.Length);
                _dialogs.ShowInformation(
                    Loc.Text("loc_Shell_DialogTitle_NpcMorphImportedWithWarnings"),
                    AppendDatabaseImportGuidance(Loc.Format("loc_Shell_Dialog_NpcMorphImportWarnings", string.Join("\n- ", allImportWarnings))));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = null;
            Status = Loc.Text("loc_Shell_Status_NpcImportCancelledTheCurrent");
        }
        catch (Exception exception)
        {
            AppLog.Error($"Standalone NPC import failed for '{sourcePath}' as {targetGame} {archetype}.", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_TheStandaloneNpcMorphCould", exception.Message);
            Status = Loc.Text("loc_Shell_Status_NpcImportFailedTheCurrent");
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

    private async Task ImportStandaloneMorphAsync(string sourcePath, MorphFaceGame game, bool newPlayerMode = false)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var isRon = extension == ".ron";
        var isLegacy = extension is ".me2headmorph" or ".me3headmorph";
        var isMesh = extension is ".psk" or ".pskx" or ".gltf" or ".glb";
        if (!isRon && !isLegacy && !isMesh)
        {
            ErrorMessage = Loc.Text("loc_Shell_Error_StandaloneImportSupportedFormats");
            Status = Loc.Text("loc_Shell_Status_StandaloneImportRequiresASupported");
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
                Status = Loc.Text("loc_Shell_Status_TheSelectedGameDoesNot");
                return;
            }
        }
        StandalonePlayerMeshAnalysis? meshAnalysis = null;
        if (isMesh)
        {
            IsBusy = true;
            ErrorMessage = null;
            Status = Loc.Format("loc_Shell_Status_InspectingTopology", Path.GetFileName(sourcePath));
            try
            {
                meshAnalysis = await Task.Run(() => _standaloneMeshImportService.Analyze(game, sourcePath));
            }
            catch (Exception exception)
            {
                AppLog.Error($"Mesh analysis failed for '{sourcePath}' as {game}.", exception);
                ErrorMessage = Loc.Format("loc_Shell_Error_TheMeshCouldNotBe", exception.Message);
                Status = Loc.Text("loc_Shell_Status_MeshImportFailedTheCurrent");
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
        var objectName = newPlayerMode
            ? _dialogs.ChooseNewPlayerMorphName(suggestedName, existingNames)
            : _dialogs.ChooseStandaloneMorphName(suggestedName, existingNames);
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
        Status = Loc.Format("loc_Shell_Status_ImportingAsAStandalonePlayer", Path.GetFileName(sourcePath), game);
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
                if (textureCatalog.IsAvailable)
                {
                    assetCatalog = await Task.Run(() => StandalonePlayerAssetCatalog.ForRon(
                        game,
                        sourcePath,
                        textureCatalog.Candidates,
                        attachmentMeshes: textureCatalog.AttachmentMeshes));
                }
                else
                {
                    AppLog.Information($"The {game} mesh/texture database is unavailable; " +
                        "Player RON import will use only assets already present in its staged player package.");
                }
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
                ErrorMessage ??= Loc.Text("loc_Shell_Error_PlayerMorphImportedWorkspaceCouldNotOpen");
                Status = Loc.Text("loc_Shell_Status_StandaloneImportCompletedWorkspaceLoad");
            }
            else if (importWarnings.Count > 0)
            {
                foreach (var warning in importWarnings)
                {
                    AppLog.Warning(warning);
                }
                Status = Loc.Format("loc_Shell_Status_ImportedWithVisibleAssetWarning", importedFacePath, importWarnings.Count);
                _dialogs.ShowInformation(
                    Loc.Text("loc_Shell_DialogTitle_PlayerMorphImportedWithWarnings"),
                    AppendDatabaseImportGuidance(Loc.Format("loc_Shell_Dialog_PlayerMorphImportWarnings", string.Join("\n- ", importWarnings))));
            }
            else if (isRon)
            {
                Status = Loc.Format("loc_Shell_Status_ImportedAsCanonicalPlayerReady", importedFacePath, game);
            }
            else if (meshRecognition is not null)
            {
                Status = Loc.Format("loc_Shell_Status_ImportedAsRecognisedPlayerMeshReady", importedFacePath, game,
                    meshRecognition.Sex, meshRecognition.CoordinateSystem);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error($"Standalone player import failed for '{sourcePath}' as {game}.", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_TheStandalonePlayerMorphCould", exception.Message);
            Status = Loc.Text("loc_Shell_Status_StandaloneImportFailedTheCurrent");
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
            previewOnlyAttachments: true,
            playerRandomisationGame: game,
            customMaterialRegistryAvailable: result.IsMaterialRegistryAvailable);
        SetEditor(editor, result.Preview.Loaded);
        _faceDetailsPrefix = null;
        _faceDetailsHasBakedFallback = false;
        FaceDetails = Loc.Format("loc_Shell_FaceDetails_DetachedMeshPreview", topology.VertexCount, topology.IndexCount / 3, topology.Sections.Count, topology.ReferenceSkeleton.Count, result.CustomMaterials?.UsedSlots.Count ?? 0);
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
        Status = Loc.Format(result.Preview.EditingSession.CanEditBones
            ? "loc_Shell_Status_LoadedUnrecognisedMeshWithBoneEditing"
            : "loc_Shell_Status_LoadedUnrecognisedMeshWithoutBoneEditing", objectName);
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
        Status = Loc.Format("loc_Shell_Status_ImportingUsingAsTheProfile", Path.GetFileName(sourcePath), template.DisplayName);
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
                Status = Loc.Format("loc_Shell_Status_ConvertingToTheSelectedLegendary", Path.GetFileName(sourcePath));
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
                Status = Loc.Format("loc_Shell_Status_SolvingAgainstTheSelectedMorph", Path.GetFileName(sourcePath));
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
                ErrorMessage = Loc.Text("loc_Shell_ErrorMessage_TheMorphWasImportedBut");
                Status = Loc.Text("loc_Shell_Status_ImportCompletedWorkspaceReloadFailed");
                return;
            }
            Status = fit is null
                ? isLegacy
                    ? Loc.Format("loc_Shell_Status_ImportedLegacyMorphVerified", result.FaceInstancedPath)
                    : Loc.Format("loc_Shell_Status_ImportedMorphVerifiedFromFormat", result.FaceInstancedPath, sourceExtension.TrimStart('.'))
                : fit.UsedSourcePrior
                    ? Loc.Format("loc_Shell_Status_ImportedMorphRestoredLods", result.FaceInstancedPath, fit.MaximumError)
                : Loc.Format("loc_Shell_Status_ImportedMorphRecoveredSliders", result.FaceInstancedPath, fit.RecoveredFeatureCount, fit.RootMeanSquareError, fit.MaximumError);
            if (sourceExtension.Equals(".ron", StringComparison.OrdinalIgnoreCase) && result.Warnings.Count > 0)
            {
                foreach (var warning in result.Warnings) AppLog.Warning(warning);
                _dialogs.ShowInformation(
                    Loc.Text("loc_Shell_DialogTitle_NpcMorphImportedWithWarnings"),
                    AppendDatabaseImportGuidance(Loc.Format("loc_Shell_Dialog_NpcMorphImportWarnings", string.Join("\n- ", result.Warnings))));
            }
            if (fit is { MaximumError: > 0.001f })
            {
                _dialogs.ShowInformation(
                    Loc.Text("loc_Shell_DialogTitle_MeshProjectedOntoMorphSliders"),
                    Loc.Format("loc_Shell_Dialog_MeshProjectedOntoMorphSliders", fit.RecoveredFeatureCount,
                        fit.CoordinateSystem, fit.RootMeanSquareError, fit.MaximumError));
            }
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure("loc_Shell_Operation_ImportMorph", "loc_Shell_Error_MorphCouldNotImport", exception, packageModified);
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
