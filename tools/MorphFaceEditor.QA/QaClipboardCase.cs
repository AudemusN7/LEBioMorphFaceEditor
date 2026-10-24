using System.Windows.Input;
using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private static async Task Clipboard(QaContext c)
    {
        var source = c.Source();
        var copy = c.Artifact("clipboard-workspace.pcc");
        c.MeasureSync("clipboard package copy", () => { File.Copy(source.PackagePath, copy); return true; }, QaStageKind.FileTransfer);
        c.Current.Evidence["clipboardPackagePath"] = copy;
        c.ExistingPackageSources[copy] = source.PackagePath;
        using (var package = MEPackageHandler.OpenMEPackage(copy, forceLoadFromDisk: true))
        {
            c.ExistingPackageBaselines[copy] = PackageIntegrity.CaptureIssues(package);
            c.Current.Evidence["existingPackageBaseline"] = c.ExistingPackageBaselines[copy];
        }
        using var reader = new MorphFacePackageReader();
        var references = new PackageReferenceService(reader, c.Textures);
        var context = new MorphFacePackageContextService();
        var cloned = c.MeasureSync("clipboard target clone/export", () => context.CloneMorph(copy, source.FacePath, "QaClipboardTarget"), QaStageKind.FileTransfer);
        c.Current.Evidence["clipboardFixture"] = "An independent clone in the copied open PCC guarantees identical source topology and vertex order.";
        var clipboard = new QaClipboard();
        var dialogs = new QaDialogs();
        using var vm = new MainWindowViewModel(dialogs, new MorphFaceCatalogService(c.Profiles),
            new MorphFacePreviewLoadService(c.Scenes, c.Targets, c.Profiles, reader), c.Scenes,
            new QaColorDialog(), references, new MorphFacePackageWriter(), context,
            new MorphFaceConversionService(c.Profiles, c.Targets, context, c.Textures),
            new MorphFaceInterchangeService(), clipboard, MorphRandomisationCatalog.LoadEmbedded(),
            recentFiles: new RecentFileService(),
            startupPreferences: new StartupPreferencesService(c.Artifact("startup-preferences.json")));
        await c.Measure("open clipboard package", () => vm.OpenDroppedFileAsync(copy), QaStageKind.Load);
        CheckVm(vm);
        var sourceItem = vm.Faces.Single(face => face.InstancedPath == source.FacePath);
        async Task Select(MorphFaceEditor.Models.BioMorphFaceListItem face, string purpose)
        {
            vm.SelectedFace = face;
            await c.Measure($"Clipboard {purpose}: load {face.InstancedPath}", () => Execute(vm.LoadSelectedFaceCommand), QaStageKind.Load);
            CheckVm(vm);
            QaContext.Require(vm.Editor is not null, "Selected face did not load into the editor.");
        }
        async Task SaveClipboard(string purpose)
        {
            await c.Measure($"Clipboard {purpose}: save copied PCC", async () =>
            {
                c.Current.Evidence["clipboardSaveState/" + purpose] = new
                {
                    Face = vm.SelectedFace?.InstancedPath, vm.IsBusy, PackageDirty = vm.IsDirty,
                    EditorDirty = vm.Editor?.IsDirty, vm.Editor?.CanEditMorphFeatures,
                    EditableSliders = vm.Editor?.Features.Count(value => value.IsEditable),
                    SaveEnabled = vm.SaveCommand.CanExecute(null)
                };
                QaContext.Require(vm.SaveCommand.CanExecute(null),
                    $"Clipboard {purpose}: SaveCommand is disabled for '{vm.SelectedFace?.InstancedPath}'. " +
                    $"Editor dirty={vm.Editor?.IsDirty}, package dirty={vm.IsDirty}, busy={vm.IsBusy}, " +
                    $"editable morph sliders={vm.Editor?.Features.Count(value => value.IsEditable)}. " +
                    "The requested paste/Save workflow did not complete; no replacement edit was applied.");
                await Execute(vm.SaveCommand);
            }, QaStageKind.FileTransfer);
            CheckVm(vm);
        }
        await Select(sourceItem, "morph copy source");
        var editor = vm.Editor!;
        var isAln = c.Face.Family.Equals("ALN", StringComparison.OrdinalIgnoreCase);
        if (isAln)
            QaContext.Require(editor.Features.All(value => !value.IsEditable),
                "The ALN profile unexpectedly exposes editable morph sliders.");
        var draft = editor.CreateDraft();
        var random = c.RandomFor("clipboard-edit");
        var editable = editor.Features.Where(value => value.IsEditable).ToArray();
        var feature = editable.Length > 0 ? QaCoverageSelection.Pick(editable, random) : null;
        if (feature is not null)
        {
            feature.BeginEdit(); feature.Value += 0.005f + random.NextSingle() * 0.02f; feature.EndEdit();
        }
        else c.Current.Evidence["morphPasteScope"] = isAln
            ? "ALN geometry is intentionally non-editable; morph clipboard refusal is expected and the copied PCC must preserve authored geometry."
            : "No safe morph slider; copies/pastes preserved authored morph data, and asserts unsupported controls remain unavailable.";
        await EditMaterial(c, editor, 0.025f);
        var sourceBeforeCopy = editor.CreateDraft();
        MorphFaceMorphData? morph = null;
        string? expectedAlnCopyRefusal = null;
        if (isAln)
        {
            QaContext.Require(!vm.CopyMorphDataCommand.CanExecute(null),
                "ALN Copy Morph Data remained enabled and could create a morph payload.");
            expectedAlnCopyRefusal = "Copy Morph Data command was disabled for the non-editable ALN profile.";
        }
        else
        {
            QaContext.Require(vm.CopyMorphDataCommand.CanExecute(null),
                "Required editor command 'Copy Morph Data' is disabled.");
            await Execute(vm.CopyMorphDataCommand);
            CheckVm(vm);
            morph = clipboard.Get(MorphFaceClipboardKind.Morph).MorphData;
        }
        QaContext.Require(!isAln || !clipboard.Contains(MorphFaceClipboardKind.Morph),
            "ALN morph copy created a clipboard payload.");
        var afterCopy = editor.CreateDraft();
        AssertGeometry(sourceBeforeCopy, afterCopy);
        QaContext.Require(sourceBeforeCopy.MorphFeatures.SequenceEqual(afterCopy.MorphFeatures),
            "Morph copy changed the source face's authored features.");
        await SaveClipboard("edited copy source");
        var destination = vm.Faces.Single(face => face.InstancedPath == cloned.FaceInstancedPath);
        await Select(destination, "morph paste target");
        var beforePaste = vm.Editor!.CreateDraft();
        var expectedMorph = morph ?? new MorphFaceMorphData(sourceBeforeCopy.MorphFeatures,
            sourceBeforeCopy.FinalSkeleton, sourceBeforeCopy.BakedLods);
        QaContext.Require(beforePaste.BakedLods.Count == expectedMorph.BakedLods.Count &&
            beforePaste.BakedLods.Zip(expectedMorph.BakedLods).All(pair => pair.First.Length == pair.Second.Length),
            "Representative clipboard pair has incompatible LOD vertex counts.");
        string? expectedAlnRefusal = null;
        if (isAln)
        {
            QaContext.Require(!vm.PasteMorphDataCommand.CanExecute(null),
                "ALN Paste Morph Data remained enabled and could transfer morph geometry.");
            expectedAlnRefusal = "Paste Morph Data command was disabled for the non-editable ALN profile.";
        }
        else if (vm.PasteMorphDataCommand.CanExecute(null))
        {
            await Execute(vm.PasteMorphDataCommand);
            if (!vm.Editor!.CanEditMorphFeatures)
            {
                QaContext.Require(vm.ErrorMessage?.Contains("disabled", StringComparison.OrdinalIgnoreCase) == true,
                    "Unsafe morph clipboard paste was not explicitly rejected.");
                c.Current.Evidence["expectedMorphPasteRejection"] = vm.ErrorMessage;
            }
            else
                CheckVm(vm);
        }
        else QaContext.Require(false, "Required editor command 'Paste Morph Data' is disabled.");
        var targetDraft = vm.Editor!.CreateDraft();
        c.Current.Evidence["morphClipboardPasteState"] = new
        {
            StateChanged = !MorphFaceEditor.Core.Editing.MorphFaceEditorStateComparer.Equals(beforePaste, targetDraft),
            vm.Editor.CanEditMorphFeatures, vm.Editor.CanEditBones,
            EditableSliders = vm.Editor.Features.Count(value => value.IsEditable),
            vm.Editor.IsDirty, SaveEnabled = vm.SaveCommand.CanExecute(null)
        };
        if (isAln)
        {
            AssertState(beforePaste, targetDraft, "ALN morph clipboard refusal preserves the complete target draft");
            QaContext.Require(vm.Editor.Features.All(value => !value.IsEditable),
                "ALN morph clipboard paste unexpectedly exposed editable morph sliders.");
            c.Current.Evidence["expectedAlnMorphClipboardRefusal"] = new
            {
                CopyCommandEnabled = vm.CopyMorphDataCommand.CanExecute(null),
                ClipboardPayloadCreated = morph is not null,
                CopyRefusal = expectedAlnCopyRefusal,
                PasteCommandEnabled = vm.PasteMorphDataCommand.CanExecute(null),
                Refusal = expectedAlnRefusal,
                GeometryPreserved = true
            };
            c.Current.Notes.Add(expectedAlnRefusal is null && expectedAlnCopyRefusal is null
                ? "ALN morph clipboard operation left authored geometry unchanged."
                : "ALN morph clipboard refusal was expected; source and target geometry remained unchanged.");
        }
        else if (!vm.Editor.CanEditMorphFeatures)
            AssertState(beforePaste, targetDraft, "rejected unsafe paste leaves the complete draft unchanged");
        if (feature is not null)
            QaContext.Require(!MorphFaceEditor.Core.Editing.MorphFaceEditorStateComparer.Equals(beforePaste, targetDraft),
                "Morph clipboard paste was a no-op.");
        var expectedFeatures = expectedMorph.MorphFeatures.ToDictionary(value => value.Name, value => value.Offset, StringComparer.OrdinalIgnoreCase);
        var actualFeatures = targetDraft.MorphFeatures.ToDictionary(value => value.Name, value => value.Offset, StringComparer.OrdinalIgnoreCase);
        QaContext.Require(expectedFeatures.Keys.Union(actualFeatures.Keys, StringComparer.OrdinalIgnoreCase)
            .All(name => expectedFeatures.GetValueOrDefault(name) == actualFeatures.GetValueOrDefault(name)),
            "Morph clipboard command lost semantic feature values.");
        var expectedBones = expectedMorph.FinalSkeleton.ToDictionary(value => value.BoneName, value => value.Translation);
        QaContext.Require(targetDraft.FinalSkeleton.Count == expectedMorph.FinalSkeleton.Count && targetDraft.FinalSkeleton.All(value =>
            expectedBones.TryGetValue(value.BoneName, out var expectedBone) && System.Numerics.Vector3.Distance(expectedBone, value.Translation) <= 0.00001f),
            "Morph clipboard command lost bone values.");
        // ALN intentionally rejects Morph Paste. In that expected no-op, the cloned
        // target stays clean and SaveCommand is correctly disabled. Material Paste
        // still runs below and owns the persisted clipboard/save verification.
        var morphPasteSaveExpected = !isAln && vm.Editor.CanEditMorphFeatures;
        c.Current.Evidence["morphPasteTargetSave"] = new
        {
            Expected = morphPasteSaveExpected,
            SkippedBecause = isAln ? "ALN morph clipboard operations are intentionally blocked; the unchanged target has nothing to save." : null
        };
        if (morphPasteSaveExpected) await SaveClipboard("morph paste target");
        await Select(sourceItem, "material copy source");
        await Execute(vm.CopyMaterialDataCommand); CheckVm(vm);
        var material = clipboard.Get(MorphFaceClipboardKind.Material).MaterialData!;
        await Select(destination, "material paste target");
        await Execute(vm.PasteMaterialDataCommand); CheckVm(vm);
        var expected = vm.Editor!.CreateDraft();
        QaContext.Require(MaterialSignature(material) == MaterialSignature(new MorphFaceMaterialData(
            expected.MaterialOverrides.Scalars, expected.MaterialOverrides.Vectors, expected.MaterialOverrides.Textures)),
            "Material clipboard command lost override values.");
        await SaveClipboard("material paste target");
        var reopened = await c.Load(copy, destination.InstancedPath);
        AssertGeometry(expected, reopened.Loaded.Document);
        QaContext.Require(MaterialSignature(material) == MaterialSignature(context.CaptureMaterialData(copy, destination.InstancedPath)),
            "Saved clipboard material data differs after reopening.");
        c.Current.Evidence["clipboard"] = new { Source = sourceItem.InstancedPath, Target = destination.InstancedPath,
            clipboard.Writes, clipboard.Reads, Transport = "Production versioned codec through an in-memory clipboard; Windows clipboard untouched" };
        c.Current.Evidence["editorMessages"] = dialogs.Messages;
        await c.Render(reopened.Scene, "clipboard-reopened.png");
    }

    private static async Task Execute(ICommand command,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(command))] string? commandName = null)
    {
        QaContext.Require(command.CanExecute(null), $"Required editor command '{commandName}' is disabled.");
        if (command is AsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync();
        else command.Execute(null);
    }

    private static void CheckVm(MainWindowViewModel vm) => QaContext.Require(
        string.IsNullOrWhiteSpace(vm.ErrorMessage), vm.ErrorMessage ?? "Editor workflow failed.");
}

internal sealed class QaClipboard : IMorphFaceClipboardService
{
    private string? _json;
    public int Writes { get; private set; }
    public int Reads { get; private set; }
    public void Set(MorphFaceClipboardPayload payload) { _json = MorphFaceClipboardCodec.Serialize(payload); Writes++; }
    public MorphFaceClipboardPayload Get(MorphFaceClipboardKind expectedKind)
    { Reads++; return MorphFaceClipboardCodec.Deserialize(_json ?? "", expectedKind); }
    public bool Contains(MorphFaceClipboardKind expectedKind)
    {
        if (_json is null) return false;
        try { _ = MorphFaceClipboardCodec.Deserialize(_json, expectedKind); return true; }
        catch (InvalidDataException) { return false; }
    }
}

internal sealed class QaDialogs : IEditorDialogService
{
    public string? ImportPath { get; set; }
    public string? RonExportPath { get; set; }
    public MorphFaceGame? ImportGame { get; set; }
    public RonImportDestinationChoice? RonDestination { get; set; }
    public List<string> Messages { get; } = [];
    private static InvalidOperationException Unexpected() => new("QA requested an unexpected interactive dialog.");
    public string? ChoosePackage(string? initialDirectory = null) => throw Unexpected();
    public MorphPackageSaveRequest? ChooseMorphPackageDestination(string suggestedFileName, string sourcePackagePath) => throw Unexpected();
    public MorphConversionSaveRequest? ChooseMorphConversionDestination(MorphFaceGame sourceGame, string suggestedFileName, string sourcePackagePath) => throw Unexpected();
    public string? ChooseCloneName(string suggestedName, IReadOnlyCollection<string> existingObjectNames) => throw Unexpected();
    public string? ChooseMorphImportFile(string? initialDirectory = null) => ImportPath ?? throw Unexpected();
    public MorphFaceGame? ChooseStandaloneImportGame() => ImportGame ?? throw Unexpected();
    public RonImportDestinationChoice? ChooseRonImportDestination(MorphFaceGame targetGame, IReadOnlyList<RonNpcArchetypeOption> archetypes, bool allowPlayer) => RonDestination ?? throw Unexpected();
    public string? ChooseStandaloneMorphName(string suggestedName, IReadOnlyCollection<string> existingObjectNames) => ImportGame is not null ? "QaImported" : throw Unexpected();
    public string? ChooseRonExportFile(string suggestedFileName, string? initialDirectory = null) => RonExportPath ?? throw Unexpected();
    public string? ChooseMeshExportDirectory(string? initialDirectory = null) => throw Unexpected();
    public ActorAssignmentCandidate? ChooseActorAssignment(ActorAssignmentInventory inventory, ActorAssignmentMode mode) => throw Unexpected();
    public bool ConfirmDeleteMorph(string facePath, bool hasReferences = false) => throw Unexpected();
    public UnsavedChangesChoice ConfirmUnsavedChanges(string assetPath, UnsavedChangesScope scope = UnsavedChangesScope.Package) => UnsavedChangesChoice.Save;
    public void ShowInformation(string title, string message) => Messages.Add(title + ": " + message);
    public void ShowTextureRegistrySettings() => throw Unexpected();
}
