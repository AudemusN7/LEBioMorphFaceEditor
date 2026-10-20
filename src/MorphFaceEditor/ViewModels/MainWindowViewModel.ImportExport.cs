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
            AppLog.Error($"MORPH:EXPORT-FAILED FORMAT:{format}", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_TheMorphMeshCouldNot", exception.Message);
            Status = Loc.Text("loc_Shell_Status_MorphMeshExportFailedThe");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ExportMorphRonAsync() => ExportMorphRonAsync(false);

    private async Task ExportMorphRonAsync(bool morphFeaturesOnly)
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
                _packageContextService.ExportRon(workspacePath, selected.InstancedPath, destination, provenance, morphFeaturesOnly);
            });
            Status = Loc.Format("loc_Shell_Status_ExportedAs", selected.DisplayName, Path.GetFileName(destination));
        }
        catch (Exception exception)
        {
            AppLog.Error("RON:EXPORT-FAILED", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_TheRonHeadMorphCould", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_RonExportFailedThePcc");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExportMorphRon() => CanTargetPackageFace() &&
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
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Format("loc_Shell_Status_PreparingPlayerDefaults", choice.Game);
        try
        {
            var head = await Task.Run(() => _packageContextService.CapturePlayerDefaultHead(choice.Game, choice.Sex));
            var sourcePath = StandalonePlayerMorphImportService.ResolveInstalledSeed(choice.Game);
            IsBusy = false;
            if (await ImportStandaloneMorphAsync(sourcePath, choice.Game, newPlayerMode: true, playerHead: head))
                Status = Loc.Format("loc_Shell_Status_CreatedFromPlayerDefaults", LoadedFacePath, choice.Game);
        }
        catch (Exception exception)
        {
            AppLog.Error("PLAYER:CREATE-FAILED", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_ThePlayerMorphCouldNot", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_NewPlayerMorphFailedThe");
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
        var sourcePath = _dialogs.ChooseMorphImportFile(Path.GetDirectoryName(_workspace?.BrowsePath));
        if (sourcePath is null)
        {
            return;
        }
        await RouteMorphImportAsync(sourcePath, game.Value);
    }

    private async Task ImportMorphAsync(string sourcePath)
    {
        if (Path.GetExtension(sourcePath).Equals(".ron", StringComparison.OrdinalIgnoreCase))
        {
            await RouteRonImportAsync(sourcePath);
            return;
        }
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

    private async Task RouteRonImportAsync(string sourcePath, MorphFaceGame? requestedGame = null)
    {
        RonExportProvenance? provenance;
        RonImportFile source;
        try
        {
            source = await RonImportFile.ReadAsync(sourcePath);
            if (await TryImportMorphFeaturesRonAsync(sourcePath, source)) return;
            if (await TryImportMaterialRonAsync(sourcePath, source)) return;
            provenance = await Task.Run(() => TseHeadMorphRon.ReadProvenance(source.Input));
        }
        catch (Exception exception)
        {
            ErrorMessage = Loc.Format("loc_Shell_Error_TheRonProvenanceCouldNot", exception.Message);
            Status = Loc.Text("loc_Shell_Status_RonImportFailedTheCurrent");
            return;
        }

        if ((requestedGame ?? (provenance?.Producer == RonExportProducer.MFE
                ? provenance.Game : _dialogs.ChooseStandaloneImportGame())) is not { } targetGame) return;

        // Untagged Trilogy Save Editor RONs follow the accepted HMM/HMF Player route.
        if (provenance is null)
        {
            await ImportStandaloneMorphAsync(sourcePath, targetGame, ronSource: source);
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
        if (isHuman && provenance.Producer != RonExportProducer.MFE)
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
            await ImportStandaloneMorphAsync(sourcePath, targetGame, ronSource: source);
            return;
        }

        await ImportStandaloneNpcMorphAsync(
            sourcePath, provenance.Game, targetGame, archetype, source);
    }

    private async Task ImportStandaloneNpcMorphAsync(
        string sourcePath,
        MorphFaceGame sourceGame,
        MorphFaceGame targetGame,
        string archetype,
        RonImportFile source)
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
            var head = await Task.Run(() => TseHeadMorphRon.Read(source.Input), cancellationToken);
            var assets = await Task.Run(() => StandalonePlayerAssetCatalog.ForHead(
                targetGame, head, textureCatalog.Candidates, donor.PackagePath), cancellationToken);
            imported = await Task.Run(() => new StandaloneNpcMorphImportService().ImportNpcHead(
                new StandaloneNpcMorphImportRequest(
                    sourceGame,
                    targetGame,
                    archetype,
                    head,
                    donor.PackagePath,
                    donor.FacePath,
                    objectName,
                    assets,
                    IsVerifiedNativeNpcDonor: true,
                    SourceTextureCatalog: sourceCatalog.Candidates,
                    TargetTextureCatalog: textureCatalog.Candidates,
                    CancellationToken: cancellationToken)), cancellationToken);
            importWarnings = UserFacingMessageFormatter.FormatWarnings(imported.SaveResult);

            var candidate = EditorWorkspace.ForNpc(imported.Workspace, targetGame);
            var importedFacePath = imported.ImportedFacePath;
            candidate.GeometryModes[importedFacePath] = MorphFaceGeometryMode.RelativeBake;
            candidate.TrackImportedFile(sourcePath, importedFacePath, WorkspaceFileFormat.MorphRon, source.Fingerprint);
            imported = null; // Candidate publication now owns the disposable package, including failure cleanup.
            var preview = (await PrepareAndPublishWorkspaceAsync(candidate, importedFacePath, cancellationToken))!;
            var selected = SelectedFace!;
            LogWorkspaceCreated("NPC", targetGame, sourcePath, PackageWorkspace!.SourcePath);
            _faceDetailsPrefix = null;
            _faceDetailsHasBakedFallback = false;
            FaceDetails = Loc.Format("loc_Shell_FaceDetails_NpcNativeRelativeBake", preview.Loaded.BaseHead.Topology.VertexCount, preview.Loaded.Document.BakedLods.Count);
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
            AppLog.Error($"NPC:IMPORT-FAILED {archetype} {targetGame} FILE:{sourcePath}", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_TheStandaloneNpcMorphCould", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_NpcImportFailedTheCurrent");
        }
        finally
        {
            imported?.Dispose();
            _npcImportCancellation?.Dispose();
            _npcImportCancellation = null;
            _cancelNpcImportCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanCancelNpcImport));
            IsBusy = false;
        }
    }

    private async Task<bool> ImportStandaloneMorphAsync(string sourcePath, MorphFaceGame game, bool newPlayerMode = false,
        PcsavHeadMorphWorkspace? career = null, HeadMorphPayload? playerHead = null, RonImportFile? ronSource = null)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var isPlayerHead = playerHead is not null || extension == ".ron";
        var isLegacy = extension is ".me2headmorph" or ".me3headmorph";
        var isMesh = extension is ".psk" or ".pskx" or ".gltf" or ".glb";
        if (!isPlayerHead && !isLegacy && !isMesh)
        {
            ErrorMessage = Loc.Text("loc_Shell_Error_StandaloneImportSupportedFormats");
            Status = Loc.Text("loc_Shell_Status_StandaloneImportRequiresASupported");
            return false;
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
                return false;
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
                AppLog.Error($"MESH:ANALYSIS-FAILED {game} FILE:{sourcePath}", exception);
                ErrorMessage = Loc.Format("loc_Shell_Error_TheMeshCouldNotBe", exception.Message);
                Status = Loc.Text("loc_Shell_Status_MeshImportFailedTheCurrent");
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }
        var appendingToCurrentGame = career is null && _workspace is { Kind: EditorWorkspaceKind.Player, Career: null } &&
                                     _workspace.Game == game &&
                                     (meshAnalysis is null || meshAnalysis.Recognition is not null);
        var existingNames = appendingToCurrentGame
            ? Faces.Select(face => face.ObjectName).ToArray()
            : Array.Empty<string>();
        var suggestedName = SuggestImportName(
            newPlayerMode ? "PlayerMorph" : SanitizeObjectName(Path.GetFileNameWithoutExtension(sourcePath)),
            existingNames);
        var objectName = career is not null ? SanitizeObjectName(career.FirstName) : newPlayerMode
            ? _dialogs.ChooseNewPlayerMorphName(suggestedName, existingNames)
            : _dialogs.ChooseStandaloneMorphName(suggestedName, existingNames);
        if (objectName is null)
        {
            return false;
        }

        var canContinue = appendingToCurrentGame
            ? await EnsureCanAbandonEditorChangesAsync()
            : await EnsureCanAbandonWorkspaceAsync();
        if (!canContinue)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = career is not null ? Loc.Text("loc_Pcsav_Reading") :
            Loc.Format("loc_Shell_Status_ImportingAsAStandalonePlayer", Path.GetFileName(sourcePath), game);
        IReadOnlyList<UserFacingMessage> unavailableCareerAttachments = [];
        EditorWorkspace? replacement = null;
        try
        {
            if (meshAnalysis is { Recognition: null })
            {
                var detached = await Task.Run(() =>
                    _detachedMeshPreviewLoadService.Load(game, meshAnalysis.ImportedMesh));
                PublishDetachedMeshWorkspace(detached, sourcePath, objectName, game);
                return true;
            }

            StandalonePlayerAssetCatalog? assetCatalog = null;
            if (isPlayerHead)
            {
                if (playerHead is null)
                {
                    ronSource ??= await RonImportFile.ReadAsync(sourcePath);
                    playerHead = await Task.Run(() => TseHeadMorphRon.Read(ronSource.Input));
                }
                var textureCatalog = await _referenceService.ReadTextureCatalogAsync(game);
                if (textureCatalog.IsAvailable)
                {
                    assetCatalog = await Task.Run(() => StandalonePlayerAssetCatalog.ForHead(
                        game,
                        playerHead,
                        textureCatalog.Candidates,
                        attachmentMeshes: textureCatalog.AttachmentMeshes));
                }
                else
                {
                    AppLog.Verbose($"REGISTRY:UNAVAILABLE {game} IMPORT:PLAYER-RON ASSETS:staged-package-only");
                }
                if (career is not null)
                {
                    assetCatalog ??= await Task.Run(() => StandalonePlayerAssetCatalog.ForHead(game, playerHead, []));
                    playerHead = await Task.Run(() => career.HasHeadMorph
                        ? career.PrepareEditingHead(assetCatalog, out unavailableCareerAttachments) : playerHead);
                }
            }

            string importedFacePath;
            IReadOnlyList<string> importWarnings;
            StandalonePlayerMeshRecognition? meshRecognition = null;
            if (appendingToCurrentGame)
            {
                var workspace = PackageWorkspace!;
                MorphFaceSaveResult saveResult;
                if (isPlayerHead)
                {
                    saveResult = await Task.Run(() => _standaloneImportService.ImportPlayerHeadIntoWorkspace(
                        game, playerHead!, objectName, workspace, assetCatalog));
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
                importWarnings = UserFacingMessageFormatter.FormatWarnings(saveResult);
            }
            else
            {
                MorphFacePackageWorkspace createdWorkspace;
                if (isPlayerHead)
                {
                    var result = await Task.Run(() => _standaloneImportService.ImportPlayerHead(
                        game, playerHead!, objectName, assetCatalog));
                    if (career is not null)
                    {
                        try { if (result.Sex != career.Sex) throw new PcsavException(PcsavError.InvalidFile); }
                        catch { result.Dispose(); throw; }
                    }
                    createdWorkspace = result.Workspace;
                    importedFacePath = result.ImportedFacePath;
                    importWarnings = UserFacingMessageFormatter.FormatWarnings(result.SaveResult);
                }
                else if (isLegacy)
                {
                    var result = await Task.Run(() => _standaloneLegacyImportService.Import(game, sourcePath, objectName));
                    createdWorkspace = result.Workspace;
                    importedFacePath = result.ImportedFacePath;
                    importWarnings = UserFacingMessageFormatter.FormatWarnings(result.SaveResult);
                }
                else
                {
                    var result = await Task.Run(() => _standaloneMeshImportService.Import(game, objectName, meshAnalysis!));
                    createdWorkspace = result.Workspace;
                    importedFacePath = result.ImportedFacePath;
                    importWarnings = UserFacingMessageFormatter.FormatWarnings(result.SaveResult);
                    meshRecognition = result.Recognition;
                }
                replacement = career is null ? EditorWorkspace.ForPlayer(createdWorkspace, game)
                    : EditorWorkspace.ForCareer(createdWorkspace, career, importedFacePath);
            }
            // Append mutates the current owner. Replacement prepares independently before the swap.
            var target = replacement ?? _workspace!;
            target.GeometryModes[importedFacePath] = isPlayerHead
                ? MorphFaceGeometryMode.RelativeBake : MorphFaceGeometryMode.FixedBake;
            if (career is { HasHeadMorph: false }) target.MarkChanged(importedFacePath);
            if (newPlayerMode) target.ImportedFile = null;
            else if (career is null)
                target.TrackImportedFile(sourcePath, importedFacePath,
                    isPlayerHead ? WorkspaceFileFormat.MorphRon :
                    isMesh ? WorkspaceFileFormat.Mesh : WorkspaceFileFormat.LegacyMorph, ronSource?.Fingerprint);
            if (replacement is not null)
            {
                replacement = null; // Preparation takes ownership even if it fails.
                await PrepareAndPublishWorkspaceAsync(target, importedFacePath);
            }
            else if (!await RefreshWorkspaceAsync(importedFacePath, clearSearch: true))
            {
                ErrorMessage ??= Loc.Text("loc_Shell_Error_PlayerMorphImportedWorkspaceCouldNotOpen");
                Status = Loc.Text("loc_Shell_Status_StandaloneImportCompletedWorkspaceLoad");
                return false;
            }
            LogWorkspaceCreated(newPlayerMode ? "NEW-PLAYER" : "PLAYER", game,
                career?.SourcePath ?? sourcePath, PackageWorkspace!.SourcePath, append: appendingToCurrentGame);
            OnPropertyChanged(nameof(PackageName));
            OnDirtyStateChanged();
            if (importWarnings.Count > 0)
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
            else if (isPlayerHead)
            {
                Status = Loc.Format("loc_Shell_Status_ImportedAsCanonicalPlayerReady", importedFacePath, game);
            }
            else if (meshRecognition is not null)
            {
                Status = Loc.Format("loc_Shell_Status_ImportedAsRecognisedPlayerMeshReady", importedFacePath, game,
                    meshRecognition.Sex, meshRecognition.CoordinateSystem);
            }
            if (career is not null && Editor is not null)
            {
                _workspace!.SavedCareerDraft = Editor.CreateDraft();
                career.SetEditingBaseline(_workspace!.SavedCareerDraft);
            }
            if (unavailableCareerAttachments.Count > 0 && Editor is not null)
                _dialogs.ShowInformation(Loc.Text("loc_Shell_DialogTitle_PlayerMorphImportedWithWarnings"),
                    Loc.Format("loc_Pcsav_UnavailableAttachments", unavailableCareerAttachments.Count) + "\n\n" +
                    string.Join("\n", unavailableCareerAttachments.Select(UserFacingMessageFormatter.Format)));
            return ErrorMessage is null;
        }
        catch (Exception exception)
        {
            AppLog.Error($"PLAYER:IMPORT-FAILED {game} FILE:{sourcePath}", exception);
            ErrorMessage = career is not null ? FormatPcsavError(exception) :
                Loc.Format("loc_Shell_Error_TheStandalonePlayerMorphCould", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_StandaloneImportFailedTheCurrent");
            return false;
        }
        finally
        {
            replacement?.Dispose();
            IsBusy = false;
        }
    }

    private void PublishDetachedMeshWorkspace(
        DetachedMeshPreviewLoadResult result,
        string sourcePath,
        string objectName,
        MorphFaceGame game)
    {
        var topology = result.Preview.Loaded.BaseHead.Topology;
        var previewAttachmentOptions = result.PreviewAttachments
            .Select(identity => new PackageAssetListItem(identity))
            .ToArray();
        var editor = new FaceEditorViewModel(
            result.Preview.EditingSession,
            result.Preview.Profile.GetUiProfile(result.CustomMaterials),
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
            customMaterialRegistryAvailable: result.IsMaterialRegistryAvailable,
            isTextureRegistryPending: true);
        ReplaceWorkspace(EditorWorkspace.ForMesh(result.Detached.Source, game));
        LogWorkspaceCreated("CUSTOM-MESH", game, sourcePath);
        _referenceCatalog = new PackageReferenceCatalog([], []);
        FaceSearchText = string.Empty;
        Faces.Clear();
        var face = new BioMorphFaceListItem(
            0,
            result.Preview.Loaded.Document.Source.InstancedPath,
            objectName,
            result.Preview.Profile.ExportTag,
            result.Preview.Profile.GetExportTagColor(),
            result.Preview.Profile.Key);
        Faces.Add(face);
        SelectedFace = face;
        LoadedFacePath = face.InstancedPath;

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
            var fit = await Task.Run(() => Editor.FitMeshPositions(candidates));
            var result = await Task.Run(() => _packageContextService.CloneMorphWithData(
                workspacePath,
                template.InstancedPath,
                objectName,
                fit.MorphData));
            packageModified = true;
            MarkWorkspaceChanged(result.FaceInstancedPath);
            if (!await RefreshWorkspaceAsync(result.FaceInstancedPath))
            {
                ErrorMessage = Loc.Text("loc_Shell_ErrorMessage_TheMorphWasImportedBut");
                Status = Loc.Text("loc_Shell_Status_ImportCompletedWorkspaceReloadFailed");
                return;
            }
            Status = fit.UsedSourcePrior
                ? Loc.Format("loc_Shell_Status_ImportedMorphRestoredLods", result.FaceInstancedPath, fit.MaximumError)
                : Loc.Format("loc_Shell_Status_ImportedMorphRecoveredSliders", result.FaceInstancedPath, fit.RecoveredFeatureCount, fit.RootMeanSquareError, fit.MaximumError);
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
        !IsLoaded(selected) || Editor?.IsDirty != true && !CareerDraftPendingInPackage || await FlushEditorToWorkspaceAsync();

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
