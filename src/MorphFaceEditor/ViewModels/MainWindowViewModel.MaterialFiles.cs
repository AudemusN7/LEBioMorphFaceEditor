using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using System.IO;
using System.Windows.Input;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

/// <summary>Routes material file actions through the MESH or face workspace interchange path.</summary>
public sealed partial class MainWindowViewModel
{
    internal static string AppendDatabaseImportGuidance(string details) =>
        details + "\n\n" + Loc.Text("loc_Shell_ImportWarnings_DatabaseGuidance");

    private async Task<bool> TryImportMaterialRonAsync(string sourcePath, RonImportFile source)
    {
        MaterialRonCodec.MaterialFileKind? kind;
        try
        {
            kind = await Task.Run(() => MaterialRonCodec.IdentifyMaterialFile(source.Input));
        }
        catch (Exception exception)
        {
            ErrorMessage = Loc.Format("loc_Shell_Error_TheRonProvenanceCouldNot", exception.Message);
            Status = Loc.Text("loc_Shell_Status_RonImportFailedTheCurrent");
            return true;
        }
        if (kind is null) return false;
        if (!CanUseMeshMaterialFiles() && !CanTargetPackageFace())
        {
            try
            {
                var target = await Task.Run(() => MaterialRonCodec.ReadDefaultFace(source.Input));
                if (target is null)
                {
                    ErrorMessage = Loc.Text("loc_Shell_Status_RonImportFailedTheCurrent");
                    AppLog.Warning("RON:MATERIAL-IMPORT-BLOCKED REASON:no-default-face WORKSPACE:none");
                    return true;
                }
                await OpenDefaultMaterialFaceAsync(target.Value.Game, target.Value.Target, sourcePath, source,
                    kind == MaterialRonCodec.MaterialFileKind.Tse ? WorkspaceFileFormat.TseMaterialsRon : WorkspaceFileFormat.MfeMaterialsRon);
            }
            catch (Exception exception)
            {
                ErrorMessage = Loc.Format("loc_Shell_Error_TheRonProvenanceCouldNot", exception.Message);
                return true;
            }
            return true;
        }
        if (CanUseMeshMaterialFiles())
            await ImportMeshMaterialsAsync(kind == MaterialRonCodec.MaterialFileKind.Tse, sourcePath, source);
        else if (CanTargetPackageFace())
            await ImportFaceMaterialsAsync(sourcePath, source);
        return true;
    }

    private async Task<bool> OpenDefaultMaterialFaceAsync(string gameName, MaterialFaceTarget target, string sourcePath,
        RonImportFile source, WorkspaceFileFormat format, MorphFaceMorphData? features = null)
    {
        if (!Enum.TryParse<MorphFaceGame>(gameName, out var game) ||
            game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3) ||
            !MorphFaceProfileRegistry.CreateDefault().Profiles.Any(profile =>
                profile.Game == game && profile.Key == target.ProfileKey))
        {
            ErrorMessage = Loc.Format("loc_Shell_Error_MaterialDefaultFaceUnavailable", target.ProfileKey, gameName);
            return false;
        }
        string packagePath;
        string facePath;
        if (target.PlayerMorph)
        {
            var suffix = MorphFaceEditor.Core.Materials.HeadProfileIdentity.SuffixFromProfileKey(target.ProfileKey);
            if (suffix is not ("human-male" or "human-female"))
            {
                ErrorMessage = Loc.Format("loc_Shell_Error_MaterialDefaultFaceUnavailable", target.ProfileKey, gameName);
                return false;
            }
            packagePath = StandalonePlayerMorphImportService.ResolveInstalledSeed(game);
            facePath = StandalonePlayerMorphImportService.ResolvePlayerTemplate(game,
                suffix == "human-male" ? StandalonePlayerSex.Male : StandalonePlayerSex.Female);
        }
        else
        {
            var catalogue = await _referenceService.ReadTextureCatalogAsync(game);
            if (!catalogue.IsAvailable)
            {
                ErrorMessage = Loc.Format("loc_Shell_Error_NpcTargetDatabaseUnavailable", game);
                return false;
            }
            var donor = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault()).ResolveMaterialDefault(
                game, target.ProfileKey, catalogue.MorphFaceTemplates, catalogue.OfficialPackagePaths);
            if (donor is null)
            {
                ErrorMessage = Loc.Format("loc_Shell_Error_MaterialDefaultFaceUnavailable", target.ProfileKey, gameName);
                return false;
            }
            packagePath = donor.PackagePath;
            facePath = donor.FacePath;
        }
        if (!await EnsureCanAbandonWorkspaceAsync()) return false;
        MorphFacePackageWorkspace? candidate = null;
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_View_MainWindow_ReadingPackageAndMorphData");
        try
        {
            candidate = await Task.Run(() => new MorphFacePackageWorkspace(packagePath, canCommit: false));
            var workspace = candidate.WorkingPath;
            using var donorReader = new MorphFacePackageReader();
            var donorFace = await Task.Run(() => donorReader.Load(workspace, facePath, decodeTextures: false));
            var catalog = await _catalogService.ReadAsync(workspace);
            // The donor supplies its native graph and base mesh, not its authored NPC edits.
            var defaults = new MorphFaceMorphData([], donorFace.BaseHead.Topology.ReferenceSkeleton
                .Select(bone => new BoneTranslation(bone.Name, bone.Position)).ToArray(), donorFace.BaseHead.Lods
                .Take(donorFace.Document.BakedLods.Count).Select(lod => lod.Positions.ToArray()).ToArray());
            var name = SuggestImportName(SanitizeObjectName(Path.GetFileNameWithoutExtension(sourcePath)),
                catalog.Faces.Select(face => face.ObjectName).ToArray());
            var created = await Task.Run(() => _packageContextService.CloneMorphWithData(
                workspace, facePath, name, defaults, new MorphFaceMaterialData([], [], []), clearAttachmentReferences: true));
            _referenceService.InvalidatePackage(workspace);
            var preview = await _previewLoadService.LoadAsync(workspace, created.FaceInstancedPath);
            var references = await _referenceService.ReadCatalogAsync(workspace);
            var textureCatalog = await _referenceService.ReadTextureCatalogAsync(game);
            string? preparationError = null;
            using var editor = new FaceEditorViewModel(preview.EditingSession, preview.Profile.GetUiProfile(),
                preview.MaterialEditingSession, _colorDialog, _referenceService, workspace, references.Textures,
                references.SkeletalMeshes, preview.Loaded.Document.HairMeshReference, preview.Loaded.Document.OtherMeshReferences,
                message => preparationError = message, preview.Profile.Key, _randomisationCatalog,
                registryTextureCandidates: textureCatalog.Candidates, textureCatalogProfile: TextureCatalogProfiles.For(preview.Profile),
                isTextureRegistryAvailable: textureCatalog.IsAvailable,
                ignoresAuthoredGeometry: preview.Profile.IgnoresAuthoredGeometry);
            if (target.PlayerMorph)
            {
                var available = await Task.Run(() => _packageContextService.FindAvailablePlayerAssets(game,
                    references.Textures.Concat(references.SkeletalMeshes).Select(value => value.Identity)));
                editor.RestrictPlayerReferences(available);
                var sex = HeadProfileIdentity.SuffixFromProfileKey(target.ProfileKey) == "human-male"
                    ? StandalonePlayerSex.Male : StandalonePlayerSex.Female;
                var materialDefaults = await Task.Run(() => _packageContextService.CapturePlayerDefaultMaterials(game, sex));
                var corrections = materialDefaults.Textures.Where(value => donorFace.Document.MaterialOverrides.Textures.Any(original =>
                    original.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(original.TextureReference?.InstancedPath, value.TextureReference?.InstancedPath, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (corrections.Length > 0) await editor.MergeMaterialDataAsync(new([], [], corrections));
            }
            IReadOnlyList<string> warnings = [];
            if (features is not null) editor.ApplyMorphFeatures(features);
            else
            {
                var imported = await Task.Run(() => FaceMaterialFileService.Read(source.Input, target.PlayerMorph));
                warnings = await ApplyFaceMaterialsAsync(editor, imported, game, target.PlayerMorph, () => true);
            }
            if (preparationError is not null) throw new InvalidDataException(preparationError);
            var draft = editor.CreateDraft();
            await Task.Run(() => _packageWriter.SaveExisting(draft));

            // Only the finished, verified working copy is attached to the live shell.
            var preparedWorkspace = target.PlayerMorph ? EditorWorkspace.ForPlayer(candidate, game, importedFacesOnly: true)
                : EditorWorkspace.ForNpc(candidate, game, importedFacesOnly: true);
            preparedWorkspace.GeometryModes[created.FaceInstancedPath] = MorphFaceGeometryMode.RelativeBake;
            preparedWorkspace.TrackImportedFile(sourcePath, created.FaceInstancedPath, format, source.Fingerprint);
            candidate = null; // Preparation takes ownership of the verified candidate.
            await PrepareAndPublishWorkspaceAsync(preparedWorkspace, created.FaceInstancedPath);
            LogWorkspaceCreated(target.PlayerMorph ? "PLAYER" : "NPC", game, sourcePath, packagePath);
            OnDirtyStateChanged();
            Status = Loc.Format("loc_RonFiles_Imported", Path.GetFileName(sourcePath));
            ShowMaterialImportWarnings(warnings);
            return true;
        }
        finally
        {
            candidate?.Dispose();
            IsBusy = false;
        }
    }
    private readonly AsyncRelayCommand _exportTseMaterialsCommand;
    private readonly AsyncRelayCommand _importTseMaterialsCommand;
    private readonly AsyncRelayCommand _exportMaterialsCommand;
    private readonly AsyncRelayCommand _importMaterialsCommand;
    public ICommand ExportTseMaterialsCommand => _exportTseMaterialsCommand;
    public ICommand ImportTseMaterialsCommand => _importTseMaterialsCommand;
    public ICommand ExportMaterialsCommand => _exportMaterialsCommand;
    public ICommand ImportMaterialsCommand => _importMaterialsCommand;

    private bool CanUseMeshMaterialFiles() => !IsBusy && IsDetachedMeshWorkspace &&
        Editor?.CustomMaterials is not null && SelectedFace?.InstancedPath == LoadedFacePath;

    public bool IsMaterialFileWorkspace => IsDetachedMeshWorkspace
        ? Editor is not null && SelectedFace?.InstancedPath == LoadedFacePath
        : SelectedFace is not null && WorkspacePackagePath is not null;

    private bool CanUseMaterialFiles() => CanUseMeshMaterialFiles() || CanTargetPackageFace();

    private async Task<bool> ExportMeshMaterialsAsync(bool tse)
    {
        if (!CanUseMeshMaterialFiles() || Editor is not { CustomMaterials: { } workspace } editor || StandaloneGame is not { } game)
            return false;
        var name = SelectedFace!.ObjectName;
        var path = _dialogs.ChooseMaterialExportFile(tse, tse ? name + ".ron" : name + ".materials.ron",
            Path.GetDirectoryName(_workspace!.BrowsePath));
        if (path is null) return false;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var values = editor.Material.CaptureInterchangeData();
            var text = tse
                ? MeshMaterialFileService.ExportTse(workspace, values, editor.HairMesh.Value,
                    editor.OtherMeshes.Select(value => value.Value).ToArray(), game)
                : MaterialRonCodec.WriteMfe(MeshMaterialFileService.WithAttachments(
                    MeshMaterialFileService.Capture(workspace, game, name, values),
                    editor.HairMesh.Value, editor.OtherMeshes.Select(mesh => mesh.Value).ToArray()));
            await MeshMaterialFileService.WriteAsync(path, text, workspace.Source.SourcePath);
            Status = Loc.Format(tse ? "loc_Shell_Status_ExportedTseMaterialSettings" : "loc_Shell_Status_ExportedMfeMaterialSettings", Path.GetFileName(path));
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("MESH:MATERIAL-EXPORT-FAILED", exception);
            ErrorMessage = exception.Message;
            Status = Loc.Text("loc_Shell_Status_MaterialExportFailedTheCurrent");
            return false;
        }
        finally { IsBusy = false; }
    }

    private async Task ImportMeshMaterialsAsync(bool tse, string? sourcePath = null, RonImportFile? source = null)
    {
        if (!CanUseMeshMaterialFiles() || Editor is not { CustomMaterials: { } workspace } editor || StandaloneGame is not { } game) return;
        var path = sourcePath ?? _dialogs.ChooseMaterialImportFile(tse, Path.GetDirectoryName(_workspace!.BrowsePath));
        if (path is null) return;
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_PreparingMaterialSettingsAndTexture");
        try
        {
            source ??= await RonImportFile.ReadAsync(path);
            var input = await Task.Run(() => source.Input);
            var prepared = await new MeshMaterialFileService(_referenceService).PrepareAsync(
                input, tse, workspace, editor.CustomMaterialOptions, game);
            if (!ReferenceEquals(Editor, editor)) return;
            editor.ApplyMeshMaterialImport(prepared);
            Status = Loc.Format(prepared.IsTse ? "loc_Shell_Status_ImportedTseMaterialSettings" : "loc_Shell_Status_ImportedMaterialSettings", Path.GetFileName(path));
            if (prepared.Warnings.Count > 0)
            {
                foreach (var warning in prepared.Warnings) AppLog.Warning(warning);
                const int displayLimit = 8;
                var details = string.Join("\n\n", prepared.Warnings.Take(displayLimit));
                if (prepared.Warnings.Count > displayLimit)
                    details += Loc.Format("loc_Shell_MaterialFiles_MoreWarningsInLog", prepared.Warnings.Count - displayLimit);
                _dialogs.ShowInformation(Loc.Text("loc_Shell_DialogTitle_MaterialsImportedWithWarnings"), AppendDatabaseImportGuidance(details));
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("MESH:MATERIAL-IMPORT-FAILED", exception);
            ErrorMessage = exception.Message;
            Status = Loc.Text("loc_Shell_Status_MaterialImportFailedTheCurrent");
        }
        finally { IsBusy = false; }
    }

    private async Task<bool> ExportFaceMaterialsAsync()
    {
        if (!CanTargetPackageFace() || !await EnsureSelectedFaceLoadedForMaterialsAsync() ||
            Editor is not { } editor || SelectedFace is not { } face)
            return false;
        var path = _dialogs.ChooseMaterialExportFile(false, face.ObjectName + ".materials.ron",
            Path.GetDirectoryName(_workspace?.BrowsePath));
        if (path is null) return false;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var values = editor.Material.CaptureInterchangeData();
            var game = ResolveMaterialFileGame();
            var document = MeshMaterialFileService.WithAttachments(
                FaceMaterialFileService.Capture(game, face.ObjectName, values),
                editor.HairMesh.Value, editor.OtherMeshes.Select(mesh => mesh.Value).ToArray());
            var target = new MaterialFaceTarget(face.ProfileKey,
                IsPlayerWorkspace || RonNpcDonorResolver.IsPlayerOrCreator(face.InstancedPath) ||
                RonNpcDonorResolver.IsPlayerOrCreator(_loadedFace?.Document.BaseHeadReference?.InstancedPath));
            if (target.PlayerMorph)
                await Task.Run(() => _packageContextService.VerifyPlayerRuntimeAssets(game, values,
                    document.HairMesh!, document.AccessoryMeshes));
            var text = MaterialRonCodec.WriteMfe(document with
            {
                DefaultFace = MorphFaceProfileRegistry.CreateDefault().Profiles.Any(profile =>
                    profile.Game == game && profile.Key == target.ProfileKey) ? target : null
            });
            await MeshMaterialFileService.WriteAsync(path, text,
                _workspace?.BrowsePath ?? path);
            Status = Loc.Format("loc_Shell_Status_ExportedMfeMaterialSettingsTo", Path.GetFileName(path));
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("FACE:MATERIAL-EXPORT-FAILED", exception);
            ErrorMessage = UserFacingMessageFormatter.Format(exception);
            Status = Loc.Text("loc_Shell_Status_MaterialExportFailedTheCurrent2");
            return false;
        }
        finally { IsBusy = false; }
    }

    private async Task ImportFaceMaterialsAsync(string? sourcePath = null, RonImportFile? source = null)
    {
        if (!CanTargetPackageFace() || !await EnsureSelectedFaceLoadedForMaterialsAsync() ||
            Editor is not { } editor)
            return;
        var path = sourcePath ?? _dialogs.ChooseMaterialImportFile(false,
            Path.GetDirectoryName(_workspace?.BrowsePath));
        if (path is null) return;
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_ReadingMaterialSettings");
        try
        {
            source ??= await RonImportFile.ReadAsync(path);
            var allowTse = IsPlayerWorkspace;
            var imported = await Task.Run(() => FaceMaterialFileService.Read(source.Input, allowTse));
            var game = ResolveMaterialFileGame();
            var warnings = await ApplyFaceMaterialsAsync(editor, imported, game, allowTse, () => ReferenceEquals(Editor, editor));
            if (!ReferenceEquals(Editor, editor)) return;
            Status = Loc.Format(imported.IsTse ? "loc_Shell_Status_ImportedTseMaterialSettings" : "loc_Shell_Status_ImportedMaterialSettingsFrom", Path.GetFileName(path));
            ShowMaterialImportWarnings(warnings);
        }
        catch (Exception exception)
        {
            AppLog.Error("FACE:MATERIAL-IMPORT-FAILED", exception);
            ErrorMessage = exception.Message;
            Status = Loc.Text("loc_Shell_Status_MaterialImportFailedTheCurrent2");
        }
        finally { IsBusy = false; }
    }

    private async Task<IReadOnlyList<string>> ApplyFaceMaterialsAsync(FaceEditorViewModel editor,
        FaceMaterialFileService.PreparedImport imported, MorphFaceGame game, bool player, Func<bool> isCurrent)
    {
        var warnings = imported.Warnings.ToList();
        var attachments = editor.CanEditAttachments
            ? await new MeshMaterialFileService(_referenceService).PreparePreviewAttachmentsAsync(
                imported.HairMesh, imported.AccessoryMeshes, game, warnings)
            : null;
        if (player && attachments is not null)
        {
            var references = editor.AttachmentMeshes.SelectMany(mesh => mesh.Options).Select(option => option.Identity)
                .Concat([attachments.Hair, attachments.Accessory]).OfType<AssetIdentity>().ToArray();
            var available = await Task.Run(() => _packageContextService.FindAvailablePlayerAssets(game, references));
            bool Safe(AssetIdentity? identity)
            {
                if (identity is null || available.Allows(identity)) return true;
                warnings.Add(Loc.Format("loc_PlayerAsset_SeekFreeSourceRequired", identity.ClassName,
                    TextureCatalogPicker.CanonicalPath(identity.InstancedPath, identity.PackagePath)));
                return false;
            }
            attachments = attachments with
            {
                ApplyHair = attachments.ApplyHair && Safe(attachments.Hair),
                ApplyAccessory = attachments.ApplyAccessory && Safe(attachments.Accessory)
            };
            if (!isCurrent()) return warnings;
            foreach (var mesh in editor.AttachmentMeshes) mesh.RestrictPlayerReferences(available);
        }
        if (!isCurrent()) return warnings;
        warnings.AddRange(await editor.ImportFaceMaterialDataAsync(imported.Parameters, attachments));
        return warnings;
    }

    private void ShowMaterialImportWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;
        foreach (var warning in warnings) AppLog.Warning(warning);
        const int displayLimit = 8;
        var details = string.Join("\n\n", warnings.Take(displayLimit));
        if (warnings.Count > displayLimit)
            details += Loc.Format("loc_Shell_MaterialFiles_MoreWarningsInLog", warnings.Count - displayLimit);
        _dialogs.ShowInformation(Loc.Text("loc_Shell_DialogTitle_MaterialsImportedWithWarnings"), AppendDatabaseImportGuidance(details));
    }

    private Task<bool> EnsureSelectedFaceLoadedForMaterialsAsync() =>
        Editor is not null && SelectedFace?.InstancedPath == LoadedFacePath
            ? Task.FromResult(true)
            : LoadSelectedFaceAsync(confirmUnsavedChanges: true);

    private MorphFaceGame ResolveMaterialFileGame()
    {
        if (_workspace is null)
            throw new InvalidOperationException(Loc.Text("loc_Shell_Error_FaceWorkspaceHasNoSourcePackage"));
        if (_workspace.Game == MorphFaceGame.Unsupported)
            throw new InvalidOperationException(Loc.Format("loc_Shell_Error_CouldNotDetermineGameForFace", LoadedFacePath));
        return _workspace.Game;
    }

    private void RaiseMaterialFileCanExecuteChanged()
    {
        _exportTseMaterialsCommand.RaiseCanExecuteChanged();
        _importTseMaterialsCommand.RaiseCanExecuteChanged();
        _exportMaterialsCommand.RaiseCanExecuteChanged();
        _importMaterialsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsMaterialFileWorkspace));
    }
}
