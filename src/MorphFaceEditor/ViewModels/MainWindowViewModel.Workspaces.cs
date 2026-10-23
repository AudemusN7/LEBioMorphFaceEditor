using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Models;
using MorphFaceEditor.Presentation;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

public sealed partial class MainWindowViewModel
{
    // Package actions can target an unloaded sidebar face; detached mesh actions have their own gate.
    private bool CanTargetPackageFace() => !IsBusy && SelectedFace is not null && WorkspacePackagePath is not null;

    private sealed record PreparedFace(MorphFacePreviewLoadResult Preview, FaceEditorViewModel Editor,
        TextureCatalogProfile TextureProfile);

    private sealed record WorkspaceContents(IReadOnlyList<BioMorphFaceListItem> Faces,
        PackageReferenceCatalog References, BioMorphFaceListItem? Selected);

    // Takes ownership of the candidate on both success and failure. No shell state changes during preparation.
    private async Task<MorphFacePreviewLoadResult?> PrepareAndPublishWorkspaceAsync(EditorWorkspace candidate,
        string? preferredFacePath = null, CancellationToken cancellationToken = default)
    {
        WorkspaceContents contents;
        PreparedFace? prepared = null;
        try
        {
            contents = await ReadWorkspaceContentsAsync(candidate, preferredFacePath, cancellationToken);
            var npcImport = candidate.Kind == EditorWorkspaceKind.Npc && candidate.ImportedFile?.Format == WorkspaceFileFormat.MorphRon;
            if (preferredFacePath is not null && !string.Equals(contents.Selected?.InstancedPath, preferredFacePath, StringComparison.OrdinalIgnoreCase))
                throw new System.IO.InvalidDataException(npcImport ? Loc.Text("loc_Shell_Error_ImportedNpcFaceMissingFromCatalogue")
                    : Loc.Format("loc_Shell_Error_FaceMissingFromPcc", preferredFacePath));
            if (contents.Selected is not null)
                prepared = await PrepareFaceAsync(candidate, contents.References, contents.Selected, cancellationToken);
            if (npcImport && prepared?.Preview.EditingSession.CanEditMorphFeatures == false)
                throw new System.IO.InvalidDataException(Loc.Format("loc_Shell_Error_ImportedNpcMorphTargetsUnavailable",
                    prepared.Preview.EditingSession.EditBlockReason));
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            prepared?.Editor.Dispose();
            _referenceService.InvalidatePackage(candidate.Package!.WorkingPath);
            candidate.Dispose();
            throw;
        }
        ReplaceWorkspace(candidate);
        PublishWorkspaceContents(contents, clearSearch: true);
        if (prepared is not null) PublishFace(prepared, contents.Selected!);
        return prepared?.Preview;
    }

    private async Task<WorkspaceContents> ReadWorkspaceContentsAsync(EditorWorkspace workspace, string? preferredFacePath,
        CancellationToken cancellationToken = default)
    {
        var workspacePath = workspace.Package!.WorkingPath;
        _referenceService.InvalidatePackage(workspacePath);
        var projection = MorphFaceCatalogProjectionPolicy.ForPlayerWorkspace(
            workspace.IsStandalone ? workspace.Game : null, workspace.Package.SourcePath,
            workspace.Kind == EditorWorkspaceKind.Npc);
        var catalog = await _catalogService.ReadWorkspaceAsync(workspacePath, _referenceService, projection, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        workspace.Game = catalog.Faces.Game;
        // Staged Player/NPC packages retain donor faces for asset dependencies.
        // Only registered imports belong in their sidebar; later imports join the same map.
        var faces = catalog.Faces.Faces.Where(face =>
            (!workspace.IsStandalone || workspace.GeometryModes.ContainsKey(face.InstancedPath)) &&
            (workspace.Career is null || face.InstancedPath == workspace.SourceFacePath)).ToArray();
        var selected = preferredFacePath is null ? null : faces.FirstOrDefault(face =>
            string.Equals(face.InstancedPath, preferredFacePath, StringComparison.OrdinalIgnoreCase)) ?? faces.FirstOrDefault();
        return new(faces, catalog.References, selected);
    }

    private async Task<PreparedFace> PrepareFaceAsync(EditorWorkspace workspace, PackageReferenceCatalog references,
        BioMorphFaceListItem selected, CancellationToken cancellationToken)
    {
        var workspacePath = workspace.Package!.WorkingPath;
        var result = await _previewLoadService.LoadAsync(workspacePath,
            selected.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken,
            workspace.GeometryModes.TryGetValue(selected.InstancedPath, out var mode) ? mode : null);
        cancellationToken.ThrowIfCancellationRequested();
        var textureCatalogProfile = TextureCatalogProfiles.For(result.Profile);
        var playerAssets = workspace.Kind == EditorWorkspaceKind.Player
            ? await Task.Run(() => _packageContextService.FindAvailablePlayerAssets(result.Loaded.Game,
                references.Textures.Concat(references.SkeletalMeshes).Select(value => value.Identity)
                .Concat(result.Loaded.Document.MaterialOverrides.Textures.Where(value => value.TextureReference is not null)
                    .Select(value => value.TextureReference!))
                .Concat(result.Loaded.Document.OtherMeshReferences.OfType<AssetIdentity>())
                .Concat(result.Loaded.Document.HairMeshReference is { } hair ? [hair] : []), cancellationToken), cancellationToken)
            : null;
        var editor = new FaceEditorViewModel(
            result.EditingSession,
            result.Profile.GetUiProfile(),
            result.MaterialEditingSession,
            _colorDialog,
            _referenceService,
            workspacePath,
            references.Textures,
            references.SkeletalMeshes,
            result.Loaded.Document.HairMeshReference,
            result.Loaded.Document.OtherMeshReferences,
            SetEditorError,
            result.Profile.Key,
            _randomisationCatalog,
            randomisationInclusionState: _randomisationInclusionState,
            registryTextureCandidates: [],
            textureCatalogProfile: textureCatalogProfile,
            isTextureRegistryAvailable: false,
            isTextureRegistryPending: true,
            headImportHistory: workspace.Career,
            ignoresAuthoredGeometry: result.Profile.IgnoresAuthoredGeometry,
            playerRandomisationGame: workspace.Kind == EditorWorkspaceKind.Player ? result.Profile.Game : null);
        try
        {
            if (playerAssets is not null) editor.RestrictPlayerReferences(playerAssets);
            cancellationToken.ThrowIfCancellationRequested();
            return new(result, editor, textureCatalogProfile);
        }
        catch
        {
            editor.Dispose();
            throw;
        }
    }

    private void PublishWorkspaceContents(WorkspaceContents prepared, bool clearSearch)
    {
        _referenceCatalog = prepared.References;
        if (clearSearch) FaceSearchText = string.Empty;
        Faces.Clear();
        foreach (var face in prepared.Faces) Faces.Add(face);
        SelectedFace = prepared.Selected;
        Status = Faces.Count == 0 ? Loc.Text("loc_Shell_Status_NoBioMorphFaceExports")
            : Loc.Format("loc_Shell_Status_BioMorphFaceExportsFound", Faces.Count);
        AppLog.Verbose($"WORKSPACE:CATALOGUE FACES:{Faces.Count} FILE:\"{WorkspacePackagePath}\"");
    }

    private void PublishFace(PreparedFace prepared, BioMorphFaceListItem selected)
    {
        var result = prepared.Preview;
        var editor = prepared.Editor;
        var textureCatalogProfile = prepared.TextureProfile;
        var topology = result.Loaded.BaseHead.Topology;
        var oracle = result.EditingSession.Evaluation.OriginalOracleReport;
        var materialOverrides = result.Loaded.Document.MaterialOverrides;
        foreach (var warning in result.Loaded.Warnings) AppLog.Warning(warning);
        var displayedFinalBoneCount = result.Profile.IgnoresAuthoredGeometry
            ? 0
            : result.Loaded.Document.FinalSkeleton.Count;
        var geometryDetails = result.EditingSession.GeometryMode == MorphFaceGeometryMode.RelativeBake
            ? Loc.Text("loc_Shell_FaceDetails_RelativeImportedBake")
            : result.EditingSession.GeometryMode == MorphFaceGeometryMode.FixedBake
                ? Loc.Text("loc_Shell_FaceDetails_FixedImportedBake")
            : result.EditingSession.CanEditMorphFeatures
                ? Loc.Format("loc_Shell_FaceDetails_OracleMax", oracle!.MaximumError)
            : result.Profile.IgnoresAuthoredGeometry
                ? Loc.Text("loc_Shell_FaceDetails_BaseHeadMaterialOnlyPreview")
            : Loc.Format("loc_Shell_FaceDetails_BakedFallback", result.EditingSession.EditBlockReason);
        _faceDetailsPrefix = Loc.Format("loc_Shell_FaceDetails_LoadedMorphPrefix", topology.VertexCount,
            topology.IndexCount / 3, topology.Sections.Count, displayedFinalBoneCount,
            materialOverrides.Scalars.Count, materialOverrides.Vectors.Count, materialOverrides.Textures.Count);
        _faceDetailsHasBakedFallback = result.EditingSession.GeometryMode != MorphFaceGeometryMode.RelativeBake &&
            result.EditingSession.GeometryMode != MorphFaceGeometryMode.FixedBake &&
            !result.EditingSession.CanEditMorphFeatures && !result.Profile.IgnoresAuthoredGeometry;
        SetGeometryDetails(geometryDetails);
        LoadedFacePath = result.Loaded.Document.Source.InstancedPath;
        var speciesKey = result.Profile.Archetype;
        if (string.Equals(_loadedSpeciesKey, speciesKey, StringComparison.OrdinalIgnoreCase))
        {
            editor.SelectCategory(_lastCategoryKey);
        }
        else
        {
            _lastCategoryKey = null;
        }
        _loadedSpeciesKey = speciesKey;
        SetEditor(editor, result.Loaded);
        _activeRegistryProfile = result.Profile;
        _activeRegistryCatalogProfile = textureCatalogProfile;
        HasPreview = true;
        var cameraFamily = PreviewCameraGrouping.ForProfile(result.Profile);
        var resetCameraPosition = _previewCameraFamily is not null &&
                                  !string.Equals(_previewCameraFamily, cameraFamily, StringComparison.OrdinalIgnoreCase);
        _previewCameraFamily = cameraFamily;
        PreviewSceneReady?.Invoke(result.Scene, resetCameraPosition);
        Status = result.EditingSession.GeometryMode == MorphFaceGeometryMode.RelativeBake
            ? Loc.Format("loc_Shell_Status_LoadedFaceRelativeBake", selected.DisplayName, result.Profile.GetDisplayName())
            : result.EditingSession.GeometryMode == MorphFaceGeometryMode.FixedBake
                ? Loc.Format("loc_Shell_Status_LoadedFaceFixedBake", selected.DisplayName, result.Profile.GetDisplayName())
            : result.EditingSession.CanEditMorphFeatures
            ? Loc.Format("loc_Shell_Status_LoadedFaceLiveGeometry", selected.DisplayName, result.Profile.GetDisplayName())
            : result.Profile.IgnoresAuthoredGeometry
                ? Loc.Format("loc_Shell_Status_LoadedFaceBaseHeadPreview", selected.DisplayName, result.Profile.GetDisplayName(), result.EditingSession.EditBlockReason)
            : Loc.Format("loc_Shell_Status_LoadedFaceBakedGeometryFallback", selected.DisplayName, result.Profile.GetDisplayName(), result.EditingSession.EditBlockReason);
        AppLog.Verbose($"FACE:LOADED {LoadedFacePath} {result.Loaded.Game} EDITABLE:{result.EditingSession.CanEditMorphFeatures} " +
            $"VERTS:{topology.VertexCount} TARGETS:{result.EditingSession.Evaluation.Resolution.WeightedTargets.Count}");
        _loadCancellation ??= new CancellationTokenSource();
        _ = LoadTextureRegistryAsync(
            editor,
            result.Loaded.Game,
            result.Profile,
            textureCatalogProfile,
            _loadCancellation.Token);
    }
}
