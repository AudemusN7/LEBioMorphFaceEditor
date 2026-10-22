using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiWorkspaceTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("failed replacement retains the live workspace and releases its candidate", FailedReplacementKeepsLiveWorkspace),
        new("RON save failure preserves edits and Save As adopts its successful copy", SourceFileSaveRoundTrip),
        new("PCC Save As retains staged faces on failure and adopts the successful copy", PccSaveAsAdoptsSuccessfulCopy),
        new("additional Player imports and face switches retain both edited drafts", AdditionalPlayerImportsRetainStagedEdits),
        new("cancelled or failed career deletion retains source and pending edits", CareerDeletionPreservesStateOnFailure)
    ];

    private static void FailedReplacementKeepsLiveWorkspace() => RunWithDispatcher(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), $"MFE-Replacement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Current.pcc");
            var replacement = Path.Combine(root, "Replacement.pcc");
            var invalid = Path.Combine(root, $"Broken-{Guid.NewGuid():N}.pcc");
            File.Copy(Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE1 GlobalMorphs.pcc")), source);
            File.Copy(source, replacement);
            File.WriteAllText(invalid, "Not a PCC");
            var original = File.ReadAllBytes(source);
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs { ConfirmUnsavedChangesResult = UnsavedChangesChoice.Cancel,
                StandaloneNameChoiceResult = "RejectedPreview" };
            // Keep normal LE1 editing available, but force the LE2 candidate's final preview to fail.
            var previewProfiles = new MorphFaceProfileRegistry(MorphFaceProfileRegistry.CreateDefault().Profiles
                .Where(profile => profile.Game == MorphFaceGame.LE1).ToArray());
            using var shell = CreateMainWindowViewModel(reader, dialogs, previewProfiles: previewProfiles);
            await shell.OpenDroppedFileAsync(source);
            shell.SelectedFace = shell.Faces.First(face => face.ProfileKey == "le1-human-male");
            await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
            var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "Initial face did not load.");
            editor.Material.Scalars[0].Value += 0.125f;
            var workingPath = editor.CreateDraft().Source.PackagePath;
            var faces = shell.Faces.ToArray();
            var selected = shell.SelectedFace;
            var loaded = shell.LoadedFacePath;
            var details = shell.FaceDetails;
            var clears = 0;
            var scenes = 0;
            shell.PreviewCleared += (_, _) => clears++;
            shell.PreviewSceneReady += (_, _) => scenes++;
            void CheckRetained()
            {
                TestAssert.True(shell.PackagePath == source && ReferenceEquals(shell.Editor, editor) &&
                    ReferenceEquals(shell.SelectedFace, selected) && shell.Faces.SequenceEqual(faces) &&
                    shell.LoadedFacePath == loaded && shell.FaceDetails == details && shell.HasPreview && shell.IsDirty,
                    "Failed or cancelled replacement changed the live workspace.");
                TestAssert.True(clears == 0 && scenes == 0 && File.Exists(workingPath) && !shell.IsBusy &&
                    shell.SaveCommand.CanExecute(null), "Replacement retired the live preview/package or left it unusable.");
                TestAssert.True(original.SequenceEqual(File.ReadAllBytes(source)), "Replacement changed the source PCC.");
            }
            await shell.OpenDroppedFileAsync(invalid);
            TestAssert.True(!shell.HasError, "Cancelled replacement attempted to read the invalid package.");
            CheckRetained();
            dialogs.ConfirmUnsavedChangesResult = UnsavedChangesChoice.Discard;
            await shell.OpenDroppedFileAsync(invalid);
            TestAssert.True(shell.HasError, "Invalid candidate did not report its preparation error.");
            CheckRetained();
            TestAssert.True(!Directory.EnumerateFiles(Path.GetDirectoryName(workingPath)!,
                Path.GetFileNameWithoutExtension(invalid) + ".*").Any(), "Failed candidate leaked a PCC or lease.");

            var ron = Path.Combine(root, "Player.ron");
            TseHeadMorphRon.Write(ron, new MorphFacePackageContextService().CapturePlayerDefaultHead(
                MorphFaceGame.LE2, StandalonePlayerSex.Female),
                new RonExportProvenance(RonExportProducer.MFE, MorphFaceGame.LE2, "HMF", true));
            var sourceRon = File.ReadAllBytes(ron);
            var beforeCandidates = Directory.GetFiles(Path.GetDirectoryName(workingPath)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await shell.OpenDroppedFileAsync(ron);
            TestAssert.True(shell.ErrorMessage?.Contains("unsupported base head", StringComparison.OrdinalIgnoreCase) == true,
                shell.ErrorMessage ?? "Candidate did not reach the simulated final preview failure.");
            CheckRetained();
            TestAssert.True(beforeCandidates.SetEquals(Directory.GetFiles(Path.GetDirectoryName(workingPath)!)) &&
                sourceRon.SequenceEqual(File.ReadAllBytes(ron)), "Preview failure leaked a candidate or changed its source.");

            await shell.OpenDroppedFileAsync(replacement);
            TestAssert.True(!shell.HasError && shell.PackagePath == replacement && shell.Editor is null &&
                shell.SelectedFace is null && shell.LoadedFacePath is null && !shell.HasPreview && !shell.IsDirty,
                "Successful replacement did not publish an unloaded PCC.");
            TestAssert.True(clears > 0 && !File.Exists(workingPath), "Successful replacement retained the old working PCC/preview.");
        }
        finally { Directory.Delete(root, recursive: true); }
    });

    private static void SourceFileSaveRoundTrip() => RunWithDispatcher(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-SourceSave-{Guid.NewGuid():N}.ron");
        var second = Path.ChangeExtension(path, ".second.ron");
        var copy = Path.ChangeExtension(path, ".copy.ron");
        try
        {
            var game = MorphFaceGame.LE2;
            TseHeadMorphRon.Write(path, new MorphFacePackageContextService().CapturePlayerDefaultHead(game, StandalonePlayerSex.Female),
                new RonExportProvenance(RonExportProducer.MFE, game, "HMF", true));
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs
            {
                StandaloneGameChoiceResult = game,
                StandaloneNameChoiceResult = "SourceSave"
            };
            using var shell = CreateMainWindowViewModel(reader, dialogs);
            await shell.OpenDroppedFileAsync(path);
            TestAssert.True(shell.Editor is not null && !shell.HasError, shell.ErrorMessage ?? "Player setup failed.");
            var scalar = shell.Editor!.Material.Scalars.First();
            var name = scalar.Name;
            var value = scalar.Value + 0.125f;
            scalar.Value = value;
            TestAssert.True(shell.SaveCommand.CanExecute(null), "A dirty RON did not enable Save File.");
            await ((AsyncRelayCommand)shell.SaveCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && !shell.IsDirty, shell.ErrorMessage ?? "RON save stayed dirty.");
            var saved = File.ReadAllText(path);
            var data = FaceMaterialFileService.Read(saved, allowTse: true).Parameters;
            TestAssert.Near(value, data.Scalars.Single(item => item.Name == name).Value, 0.00001f);

            shell.Editor!.Material.Scalars.Single(item => item.Name == name).Value = value + 0.125f;
            File.WriteAllText(path, saved + "\n// external change\n");
            var changed = File.ReadAllText(path);
            await ((AsyncRelayCommand)shell.SaveCommand).ExecuteAsync();
            TestAssert.True(shell.HasError && shell.IsDirty, "Failed source save lost the dirty state.");
            TestAssert.Equal(changed, File.ReadAllText(path));
            File.WriteAllText(path, saved);
            await ((AsyncRelayCommand)shell.SaveCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && !shell.IsDirty, shell.ErrorMessage ?? "Staged retry failed.");

            shell.Editor!.Material.Scalars.Single(item => item.Name == name).Value = value + 0.25f;
            dialogs.ConfirmUnsavedChangesResult = UnsavedChangesChoice.Save;
            TestAssert.True(await shell.ConfirmCloseAsync(), "Closing could not save back to the original RON.");
            TestAssert.Equal(UnsavedChangesScope.SourceFile, dialogs.LastUnsavedChangesScope!.Value);
            TestAssert.True(!shell.IsDirty && !shell.HasError, "Close-save left unsaved RON edits.");

            var originalBeforeCopy = File.ReadAllText(path);
            dialogs.SaveAsPath = copy;
            await ((AsyncRelayCommand)shell.SaveAsCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && !shell.IsDirty && File.Exists(copy), shell.ErrorMessage ?? "RON Save As failed.");
            shell.Editor!.Material.Scalars.Single(item => item.Name == name).Value += 0.125f;
            await ((AsyncRelayCommand)shell.SaveCommand).ExecuteAsync();
            TestAssert.Equal(originalBeforeCopy, File.ReadAllText(path));
            TestAssert.True(File.ReadAllText(copy) != originalBeforeCopy && !shell.IsDirty, "Ctrl+S did not target the RON copy.");

            File.Copy(path, second);
            dialogs.StandaloneNameChoiceResult = "SecondSource";
            dialogs.RonImportDestinationChoiceResult = new(RonImportDestination.PlayerWorkspace, null);
            await shell.OpenDroppedFileAsync(second);
            TestAssert.True(!shell.HasError, shell.ErrorMessage ?? "Second import failed.");
            var firstBefore = File.ReadAllText(path);
            var secondBefore = File.ReadAllText(second);
            shell.Editor!.Material.Scalars.First().Value += 0.125f;
            await ((AsyncRelayCommand)shell.SaveCommand).ExecuteAsync();
            TestAssert.Equal(Loc.Text("loc_FileSave_MultipleImports"), dialogs.LastInformation);
            TestAssert.True(shell.IsDirty, "Multiple-import guidance cleared edits.");
            TestAssert.Equal(firstBefore, File.ReadAllText(path));
            TestAssert.Equal(secondBefore, File.ReadAllText(second));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(second)) File.Delete(second);
            if (File.Exists(copy)) File.Delete(copy);
        }
    });

    private static void PccSaveAsAdoptsSuccessfulCopy() => RunWithDispatcher(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-WorkspaceSaveAs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "Source.pcc");
            var copy = Path.Combine(directory, "Copy.pcc");
            var lockedPath = Path.Combine(directory, "Locked.pcc");
            File.Copy(Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc"), source);
            File.Copy(source, lockedPath);
            var original = PackageFingerprint.Capture(source);
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs { ConfirmUnsavedChangesResult = UnsavedChangesChoice.Save,
                CloneNameChoiceResult = "MFE_WorkspaceClone", SaveAsPath = lockedPath };
            using var shell = CreateMainWindowViewModel(reader, dialogs);
            await shell.OpenDroppedFileAsync(source);
            shell.SelectedFace = shell.Faces.First(face => face.ProfileKey == "le1-human-male");
            await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
            var firstFace = shell.LoadedFacePath!;
            var scalar = shell.Editor!.Material.Scalars.First();
            var firstName = scalar.Name;
            var firstValue = scalar.Value + 0.125f;
            scalar.Value = firstValue;
            shell.SelectedFace = shell.Faces.First(face => face.ProfileKey == "le1-human-female");
            await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && shell.Editor?.IsDirty == false && shell.IsDirty,
                shell.ErrorMessage ?? "Switching faces lost the first face's staged edits.");
            TestAssert.Equal(UnsavedChangesScope.Face, dialogs.LastUnsavedChangesScope!.Value);
            TestAssert.Equal(original, PackageFingerprint.Capture(source));
            await ((AsyncRelayCommand)shell.CloneMorphCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && shell.IsDirty, shell.ErrorMessage ?? "Clone did not remain staged.");
            var clonedFace = shell.LoadedFacePath!;
            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await ((AsyncRelayCommand)shell.SaveAsCommand).ExecuteAsync();
                TestAssert.True(shell.HasError && shell.IsDirty && shell.PackagePath == source,
                    "Failed Save As adopted its destination or cleared staged edits.");
                TestAssert.Equal(Loc.Text("loc_FileSave_EditsRetained"), shell.Status);
            }
            dialogs.SaveAsPath = copy;
            await ((AsyncRelayCommand)shell.SaveAsCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && !shell.IsDirty && shell.PackagePath == copy && shell.PackageName == "Copy.pcc",
                shell.ErrorMessage ?? "Successful Save As did not adopt its copy.");
            var context = new MorphFacePackageContextService();
            TestAssert.Near(firstValue, context.CaptureMaterialData(copy, firstFace).Scalars.Single(value => value.Name == firstName).Value, 0.00001f);
            TestAssert.True(MorphFaceReferenceInspector.Inspect(copy).Any(face => face.FacePath == clonedFace), "Save As lost the staged clone.");
            scalar = shell.Editor!.Material.Scalars.First();
            var nextName = scalar.Name;
            var nextValue = scalar.Value + 0.25f;
            scalar.Value = nextValue;
            await ((AsyncRelayCommand)shell.SaveCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && !shell.IsDirty && shell.PackagePath == copy,
                shell.ErrorMessage ?? "Subsequent Save did not target the adopted PCC.");
            TestAssert.Near(nextValue, context.CaptureMaterialData(copy, clonedFace).Scalars.Single(value => value.Name == nextName).Value, 0.00001f);
            TestAssert.Equal(original, PackageFingerprint.Capture(source));
            shell.Editor!.Material.Scalars.First().Value += 0.125f;
            dialogs.ConfirmUnsavedChangesResult = UnsavedChangesChoice.Cancel;
            TestAssert.True(!await shell.ConfirmCloseAsync() && shell.HasWorkspace && shell.IsDirty, "Cancel close lost the active workspace.");
            TestAssert.Equal(UnsavedChangesScope.Package, dialogs.LastUnsavedChangesScope!.Value);
            var workingPath = shell.Editor.CreateDraft().Source.PackagePath;
            dialogs.ConfirmUnsavedChangesResult = UnsavedChangesChoice.Discard;
            await shell.CloseWorkspaceAsync();
            TestAssert.True(!shell.HasWorkspace && shell.Editor is null && !shell.IsDirty && !File.Exists(workingPath) &&
                !shell.SaveAsCommand.CanExecute(null), "Closing retained workspace resources, staged edits or save commands.");

            await shell.OpenDroppedFileAsync(source);
            var cleanCopy = Path.Combine(directory, "CleanCopy.pcc");
            dialogs.SaveAsPath = cleanCopy;
            await ((AsyncRelayCommand)shell.SaveAsCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && File.Exists(cleanCopy) && shell.PackagePath == cleanCopy &&
                shell.PackageName == "CleanCopy.pcc" && !shell.IsDirty && shell.Editor is null,
                shell.ErrorMessage ?? "Save As skipped the clean package or did not adopt its destination.");
            TestAssert.Equal(original, PackageFingerprint.Capture(source));
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static void AdditionalPlayerImportsRetainStagedEdits() => RunWithDispatcher(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-RonOtherFaces-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var game in new[] { MorphFaceGame.LE2 })
            {
                var source = Path.Combine(directory, $"{game}-Player.ron");
                var exported = Path.Combine(directory, $"{game}-OtherFace.ron");
                var head = new MorphFacePackageContextService().CapturePlayerDefaultHead(game, StandalonePlayerSex.Male);
                TseHeadMorphRon.Write(source, head, new(RonExportProducer.MFE, game, "HMM", true));
                var sourceBytes = File.ReadAllBytes(source);
                using var reader = new MorphFacePackageReader();
                var dialogs = new StubEditorDialogs { StandaloneNameChoiceResult = "MFE_RonSource", RonExportPath = exported };
                using var shell = CreateMainWindowViewModel(reader, dialogs);
                await shell.OpenDroppedFileAsync(source);
                TestAssert.True(!shell.HasError && shell.Editor is not null, shell.ErrorMessage ?? "Player source did not open.");
                TestAssert.Equal(1, shell.Faces.Count);
                var sourceFace = shell.LoadedFacePath!;
                dialogs.StandaloneNameChoiceResult = "MFE_AdditionalPlayer";
                await shell.OpenDroppedFileAsync(source);
                TestAssert.True(!shell.HasError && shell.Editor is not null, shell.ErrorMessage ?? "Additional Player import failed.");
                var addedFace = shell.LoadedFacePath!;
                TestAssert.True(sourceFace != addedFace && shell.Faces.Count == 2 &&
                    shell.Faces.Any(face => face.InstancedPath == sourceFace),
                    "Appending another Player face hid or replaced an authored face.");
                var scalar = shell.Editor!.Material.Scalars.First();
                var otherName = scalar.Name;
                var otherValue = scalar.Value + 0.125f;
                scalar.Value = otherValue;
                await ((AsyncRelayCommand)shell.ExportMorphRonCommand).ExecuteAsync();
                TestAssert.True(!shell.HasError && shell.Editor?.IsDirty == false && shell.IsDirty,
                    shell.ErrorMessage ?? "Exporting another face did not retain its staged edits.");
                TestAssert.Near(otherValue, TseHeadMorphRon.Read(exported).MaterialData.Scalars.Single(value => value.Name == otherName).Value, 0.00001f);
                await ((AsyncRelayCommand)shell.SaveCommand).ExecuteAsync();
                TestAssert.True(dialogs.LastInformation == Loc.Text("loc_FileSave_MultipleImports") &&
                    shell.IsDirty && sourceBytes.SequenceEqual(File.ReadAllBytes(source)),
                    "A multiple-import workspace overwrote one source RON or discarded staged edits.");
                shell.SelectedFace = shell.Faces.Single(face => face.InstancedPath == sourceFace);
                await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
                scalar = shell.Editor!.Material.Scalars.First();
                var sourceName = scalar.Name;
                var sourceValue = scalar.Value + 0.25f;
                scalar.Value = sourceValue;
                dialogs.RonExportPath = Path.Combine(directory, $"{game}-FirstFace.ron");
                await ((AsyncRelayCommand)shell.ExportMorphRonCommand).ExecuteAsync();
                TestAssert.True(!shell.HasError && shell.IsDirty && shell.Editor?.IsDirty == false,
                    shell.ErrorMessage ?? "Exporting the first face discarded staged edits to its sibling.");
                TestAssert.Near(sourceValue, TseHeadMorphRon.Read(dialogs.RonExportPath).MaterialData.Scalars.Single(value => value.Name == sourceName).Value, 0.00001f);
                shell.SelectedFace = shell.Faces.Single(face => face.InstancedPath == addedFace);
                await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
                TestAssert.Near(otherValue, shell.Editor!.Material.Scalars.Single(value => value.Name == otherName).Value, 0.00001f);

            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static void CareerDeletionPreservesStateOnFailure() => RunWithDispatcher(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-PcsavDelete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "Career.pcsav");
            File.Copy(PcsavHeadMorphTests.Fixtures().Single(path => Path.GetFileName(path) == "LE3-Male-Custom.pcsav"), source);
            var originalBytes = File.ReadAllBytes(source);
            var original = PcsavHeadMorphWorkspace.Open(source);
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs { ConfirmDeleteMorphResult = false };
            using var shell = CreateMainWindowViewModel(reader, dialogs);
            await shell.OpenDroppedFileAsync(source);
            var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "Career did not open.");
            editor.Material.Scalars.First().Value += 0.125f;
            await ((AsyncRelayCommand)shell.DeleteMorphCommand).ExecuteAsync();
            TestAssert.True(ReferenceEquals(editor, shell.Editor) && shell.IsDirty &&
                            originalBytes.SequenceEqual(File.ReadAllBytes(source)),
                "Cancelled deletion changed the career or discarded edits.");

            dialogs.ConfirmDeleteMorphResult = true;
            var damaged = originalBytes.ToArray();
            damaged[20] ^= 0x80;
            File.WriteAllBytes(source, damaged);
            await ((AsyncRelayCommand)shell.DeleteMorphCommand).ExecuteAsync();
            TestAssert.True(ReferenceEquals(editor, shell.Editor) && shell.HasWorkspace && shell.HasError &&
                            shell.IsDirty && damaged.SequenceEqual(File.ReadAllBytes(source)),
                "Failed deletion overwrote a changed career or discarded pending edits.");
            File.WriteAllBytes(source, originalBytes);
            await ((AsyncRelayCommand)shell.DeleteMorphCommand).ExecuteAsync();
            TestAssert.True(!shell.HasWorkspace && shell.Editor is null && !shell.IsDirty,
                "Successful deletion did not close its workspace.");
            var deleted = PcsavHeadMorphWorkspace.Open(source);
            TestAssert.True(!deleted.HasHeadMorph && original.UnrelatedPrefix.SequenceEqual(deleted.UnrelatedPrefix) &&
                            original.UnrelatedSuffix.SequenceEqual(deleted.UnrelatedSuffix),
                "Deletion retained the head or changed unrelated career data.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    });
}
