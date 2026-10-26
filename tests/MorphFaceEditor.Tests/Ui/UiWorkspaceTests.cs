using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System.Reflection;
using System.Runtime.CompilerServices;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiWorkspaceTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("failed replacement retains the live workspace and releases its candidate", FailedReplacementKeepsLiveWorkspace),
        new("new NPC starts blank and unsaved without changing its native donor", NewNpcStartsBlank),
        new("workspace close releases cached packages and pixels and allows reopening", CloseReleasesReaderCaches),
        new("idle cleanup skips heaps dominated by useful live data", IdleCleanupRequiresSubstantialSpareCapacity),
        new("idle reader lease releases on its worker without double release", IdleReaderLeaseReleasesOffDispatcher),
        new("archetype switches release old packages while reusing shared texture pixels", ArchetypeSwitchReleasesInactiveCaches),
        new("RON save failure preserves edits and Save As adopts its successful copy", SourceFileSaveRoundTrip),
        new("partial RON saves retain unrelated edits and chronological dirty state", PartialRonSaveRetainsHistory),
        new("PCC Save As retains staged faces on failure and adopts the successful copy", PccSaveAsAdoptsSuccessfulCopy),
        new("additional Player imports and face switches retain both edited drafts", AdditionalPlayerImportsRetainStagedEdits),
        new("cancelled or failed career deletion retains source and pending edits", CareerDeletionPreservesStateOnFailure)
    ];

    private static void NewNpcStartsBlank() => RunWithDispatcher(async () =>
    {
        const MorphFaceGame game = MorphFaceGame.LE3;
        var catalogue = new MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureCatalogService(
            new MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureRegistryStore(
                MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureRegistryPaths.CreateDefault()));
        var assets = await catalogue.ReadAsync(game);
        TestAssert.True(assets.IsAvailable, "The installed LE3 texture registry is required.");
        var donor = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault())
            .Resolve(game, "HMM", assets.MorphFaceTemplates, assets.OfficialPackagePaths);
        var fingerprint = PackageFingerprint.Capture(donor.PackagePath);
        using var reader = new MorphFacePackageReader();
        var native = reader.Load(donor.PackagePath, donor.FacePath, decodeTextures: false);
        var dialogs = new StubEditorDialogs { NewNpcMorphChoiceResult = new(game, "HMM"),
            StandaloneNameChoiceResult = "MFE_NewNpc" };
        using var shell = CreateMainWindowViewModel(reader, dialogs, catalogue);
        await ((AsyncRelayCommand)shell.NewNpcMorphCommand).ExecuteAsync();
        TestAssert.True(shell.Editor is not null && !shell.HasError && shell.HasPreview,
            shell.ErrorMessage ?? "New NPC did not publish its preview.");
        TestAssert.True(shell.Faces.Count == 1 && shell.SelectedFace?.ObjectName == "MFE_NewNpc" &&
            shell.Editor!.CanEditMorphFeatures && shell.IsDirty && shell.SaveCommand.CanExecute(null) && !shell.CanCommitFace,
            "New NPC must be the sole editable, unsaved face in a detached workspace.");
        var draft = shell.Editor!.CreateDraft();
        var head = new MorphFacePackageContextService().CaptureHead(draft.Source.PackagePath, shell.LoadedFacePath!);
        TestAssert.True(head.MorphData.MorphFeatures.All(value => value.Offset == 0) &&
            head.MorphData.BakedLods.Count == native.Document.BakedLods.Count &&
            head.MorphData.BakedLods.Select((lod, index) => lod.SequenceEqual(native.BaseHead.Lods[index].Positions)).All(value => value),
            "New NPC retained donor morphs instead of native base geometry.");
        TestAssert.True(head.MorphData.FinalSkeleton.SequenceEqual(native.BaseHead.Topology.ReferenceSkeleton
            .Select(bone => new BoneTranslation(bone.Name, bone.Position))) && head.HairMesh == "None" && head.AccessoryMeshes.Count == 0 &&
            head.MaterialData.Scalars.Count == 0 && head.MaterialData.Vectors.Count == 0 && head.MaterialData.Textures.Count == 0,
            "New NPC retained donor bone edits, attachments or material overrides.");
        TestAssert.True(shell.Editor.Material.Scalars.Count + shell.Editor.Material.Vectors.Count + shell.Editor.Material.Textures.Count > 0,
            "Native material defaults were not available for editing.");
        var editor = shell.Editor;
        dialogs.StandaloneNameChoiceResult = null;
        await ((AsyncRelayCommand)shell.NewNpcMorphCommand).ExecuteAsync();
        TestAssert.True(ReferenceEquals(editor, shell.Editor) && shell.IsDirty, "Cancelling naming discarded the current NPC.");
        dialogs.StandaloneNameChoiceResult = "MFE_CancelledNpc";
        await ((AsyncRelayCommand)shell.NewNpcMorphCommand).ExecuteAsync();
        TestAssert.True(ReferenceEquals(editor, shell.Editor) && shell.IsDirty && !shell.IsBusy,
            "Cancelling the unsaved-workspace prompt discarded the current NPC.");
        TestAssert.Equal(fingerprint, PackageFingerprint.Capture(donor.PackagePath));
    });

    private static void IdleCleanupRequiresSubstantialSpareCapacity()
    {
        const long gib = 1024L * 1024 * 1024;
        TestAssert.True(!IdleMemoryCleanup.HasReclaimableCapacity(gib, gib / 4),
            "A small heap triggered an unnecessary idle collection.");
        TestAssert.True(!IdleMemoryCleanup.HasReclaimableCapacity(3 * gib, 3 * gib - gib / 4),
            "A heap mostly occupied by live data triggered compaction.");
        TestAssert.True(IdleMemoryCleanup.HasReclaimableCapacity(3 * gib, 2 * gib),
            "Substantial unused committed capacity was ignored.");
        TestAssert.True(!IdleMemoryCleanup.HasReclaimableCapacity(3 * gib, 3 * gib),
            "A large fully occupied heap was treated as reclaimable spare capacity.");
    }

    private static void IdleReaderLeaseReleasesOffDispatcher()
    {
        using var reader = new MorphFacePackageReader();
        var references = new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService());
        using var first = references.TryAcquireIdleLease();
        TestAssert.True(first is not null && references.TryAcquireIdleLease() is null,
            "Idle collection overlapped an active reader lease.");
        Task.Run(first!.Dispose).GetAwaiter().GetResult();
        using var next = references.TryAcquireIdleLease();
        TestAssert.True(next is not null, "The worker did not release its idle lease.");
        first.Dispose();
        TestAssert.True(references.TryAcquireIdleLease() is null, "Repeated disposal released a different reader's lease.");
    }

    private static void ArchetypeSwitchReleasesInactiveCaches() => RunWithDispatcher(async () =>
    {
        var source = Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE1 GlobalMorphs.pcc"));
        var original = PackageFingerprint.Capture(source);
        using var reader = new MorphFacePackageReader();
        var dialogs = new StubEditorDialogs { ConfirmUnsavedChangesResult = UnsavedChangesChoice.Cancel };
        using var shell = CreateMainWindowViewModel(reader, dialogs);
        await shell.OpenDroppedFileAsync(source);
        var maleFaces = shell.Faces.Where(face => face.ProfileKey == "le1-human-male").Take(2).ToArray();
        TestAssert.Equal(2, maleFaces.Length);
        shell.SelectedFace = maleFaces[0];
        await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
        TestAssert.True(shell.Editor is not null, shell.ErrorMessage ?? "Initial face did not load.");
        var initial = CaptureReaderRetentions(reader);
        shell.SelectedFace = maleFaces[1];
        await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
        TestAssert.True(initial.Packages.Any(reference => reference.IsAlive),
            "A face switch within the same archetype discarded the warm package cache.");

        shell.Editor!.Material.Scalars.First().Value += 0.125f;
        var retiredEditor = new WeakReference(shell.Editor);
        var retired = CaptureReaderRetentions(reader);
        var oldPixels = CaptureReaderTexturePixels(reader);
        var female = shell.Faces.First(face => face.ProfileKey == "le1-human-female");
        shell.SelectedFace = female;
        await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
        TestAssert.True(ReferenceEquals(retiredEditor.Target, shell.Editor) && shell.Editor!.IsDirty,
            "Cancelling an archetype switch discarded the current editor.");

        dialogs.ConfirmUnsavedChangesResult = UnsavedChangesChoice.Discard;
        shell.SelectedFace = female;
        await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
        TestAssert.True(shell.Editor is not null && shell.HasPreview && !shell.HasError && !shell.IsBusy,
            shell.ErrorMessage ?? "Archetype switch failed to publish the new editor.");
        var currentPixels = CaptureReaderTexturePixels(reader);
        var shared = oldPixels.Keys.Intersect(currentPixels.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        TestAssert.True(shared.Length > 0, "The fixture must exercise textures shared by male and female heads.");
        TestAssert.True(shared.All(key => ReferenceEquals(oldPixels[key].Target, currentPixels[key].Target)),
            "Switching archetype decoded shared textures again instead of reusing their pixels.");
        var unused = oldPixels.Where(pair => !currentPixels.ContainsKey(pair.Key)).Select(pair => pair.Value).ToArray();
        TestAssert.True(unused.Length > 0, "The fixture must also exercise textures no longer used by the new face.");
        await Task.Run(static () => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true));
        TestAssert.True(!retiredEditor.IsAlive && retired.Packages.All(reference => !reference.IsAlive) &&
                        unused.All(reference => !reference.IsAlive),
            "The retired archetype still owns an editor, donor PCCs or unused texture pixels.");
        shell.Editor!.Material.Scalars.First().Value += 0.125f;
        TestAssert.True(shell.Editor.IsDirty && shell.Editor.UndoCommand.CanExecute(null),
            "The new editor lost material editing or Undo after cache cleanup.");
        TestAssert.Equal(original, PackageFingerprint.Capture(source));
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Dictionary<string, WeakReference> CaptureReaderTexturePixels(MorphFacePackageReader reader)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var materials = typeof(MorphFacePackageReader).GetField("_materialReader", hidden)!.GetValue(reader)!;
        var textures = (LinkedList<DecodedTextureAsset>)materials.GetType()
            .GetField("_textureCacheLru", hidden)!.GetValue(materials)!;
        return textures.ToDictionary(texture => MaterialIdentityKey.Create(texture.Source),
            texture => new WeakReference(texture.Rgba8), StringComparer.OrdinalIgnoreCase);
    }

    private static void CloseReleasesReaderCaches() => RunWithDispatcher(async () =>
    {
        var source = Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE1 GlobalMorphs.pcc"));
        var original = PackageFingerprint.Capture(source);
        using var reader = new MorphFacePackageReader();
        var dialogs = new StubEditorDialogs { ConfirmUnsavedChangesResult = UnsavedChangesChoice.Cancel };
        using var shell = CreateMainWindowViewModel(reader, dialogs);
        await shell.OpenDroppedFileAsync(source);
        shell.SelectedFace = shell.Faces.First(face => face.ProfileKey == "le1-human-male");
        await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
        TestAssert.True(shell.Editor is not null && shell.HasPreview, shell.ErrorMessage ?? "Face did not load.");
        shell.Editor!.Material.Scalars.First().Value += 0.125f;
        var retained = CaptureReaderRetentions(reader);
        TestAssert.True(retained.Packages.Length > 1 && retained.Pixels.Length > 0,
            "The close regression requires cached donor packages and decoded texture pixels.");

        await shell.CloseWorkspaceAsync();
        TestAssert.True(shell.HasWorkspace && shell.Editor is { IsDirty: true } &&
                        retained.Packages.Any(reference => reference.IsAlive),
            "Cancelling close discarded the editor or its reusable package data.");

        dialogs.ConfirmUnsavedChangesResult = UnsavedChangesChoice.Discard;
        await shell.CloseWorkspaceAsync();
        TestAssert.True(!shell.HasWorkspace && shell.Editor is null && !shell.HasPreview && !shell.IsBusy,
            "Close did not finish clearing the workspace or left commands busy.");
        TestAssert.True(retained.Packages.All(reference => !reference.IsAlive) &&
                        retained.Pixels.All(reference => !reference.IsAlive),
            "Closed-workspace caches still retain PCCs or decoded texture pixels.");

        await shell.OpenDroppedFileAsync(source);
        shell.SelectedFace = shell.Faces.First(face => face.ProfileKey == "le1-human-male");
        await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
        TestAssert.True(shell.Editor is not null && shell.HasPreview && !shell.HasError,
            shell.ErrorMessage ?? "The cleared reader could not load another workspace.");
        TestAssert.Equal(original, PackageFingerprint.Capture(source));
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[] Packages, WeakReference[] Pixels) CaptureReaderRetentions(MorphFacePackageReader reader)
    {
        // Observe lifetimes, not dictionary counts: a mesh/material side cache can
        // keep a PCC alive even after it has left the main package cache.
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var packages = (PackageCache)typeof(MorphFacePackageReader).GetField("_packageCache", hidden)!.GetValue(reader)!;
        var materials = typeof(MorphFacePackageReader).GetField("_materialReader", hidden)!.GetValue(reader)!;
        var textures = (LinkedList<DecodedTextureAsset>)materials.GetType()
            .GetField("_textureCacheLru", hidden)!.GetValue(materials)!;
        return (packages.Cache.Values.Select(package => new WeakReference(package)).ToArray(),
            textures.Select(texture => new WeakReference(texture.Rgba8)).ToArray());
    }

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

            dialogs.NewNpcMorphChoiceResult = new(MorphFaceGame.LE2, "SAL");
            await ((AsyncRelayCommand)shell.NewNpcMorphCommand).ExecuteAsync();
            TestAssert.True(shell.HasError, "Missing NPC database did not report a creation failure.");
            CheckRetained();

            var ron = Path.Combine(root, "Player.ron");
            TseHeadMorphRon.Write(ron, new MorphFacePackageContextService().CapturePlayerDefaultHead(
                MorphFaceGame.LE2, StandalonePlayerSex.Female),
                new RonExportProvenance(RonExportProducer.MFE, MorphFaceGame.LE2, "HMF", true));
            var sourceRon = File.ReadAllBytes(ron);
            var beforeCandidates = Directory.GetFiles(Path.GetDirectoryName(workingPath)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await shell.OpenDroppedFileAsync(ron);
            TestAssert.True(shell.ErrorMessage?.Contains(Loc.Format("loc_Services_MorphFacePreviewLoad_UnsupportedBaseHead",
                    MorphFaceGame.LE2, "BIOG_HMF_HED_PROMorph_R.Custom.HMF_HED_PROCustom_MDL"), StringComparison.Ordinal) == true,
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

    private static void PartialRonSaveRetainsHistory() => RunWithDispatcher(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-PartialSave-{Guid.NewGuid():N}.ron");
        try
        {
            var game = MorphFaceGame.LE2;
            var head = new MorphFacePackageContextService().CapturePlayerDefaultHead(game, StandalonePlayerSex.Female);
            foreach (var featuresOnly in new[] { true, false })
            {
                if (featuresOnly)
                    TseHeadMorphRon.WriteMorphFeatures(path, head.MorphData, new(RonExportProducer.MFE, game, "HMF", true));
                else
                    File.WriteAllText(path, MaterialRonCodec.WriteMfe(FaceMaterialFileService.Capture(game,
                        "PartialSave", head.MaterialData) with { DefaultFace = new("le2-human-female", true) }));
                using var reader = new MorphFacePackageReader();
                using var shell = CreateMainWindowViewModel(reader, new StubEditorDialogs());
                await shell.OpenDroppedFileAsync(path);
                var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "Partial RON did not open.");
                var initial = editor.CreateDraft();
                var feature = editor.Features.First(value => value.IsEditable);
                var scalar = editor.Material.Scalars.First();
                var bone = editor.Bones.First(value => value.IsAvailable);
                feature.Value += 0.025f;
                scalar.Value += 0.125f;
                bone.Value += 0.01f;
                var edited = editor.CreateDraft();

                await ((AsyncRelayCommand)shell.SaveCommand).ExecuteAsync();
                TestAssert.True(!shell.HasError && shell.IsDirty, shell.ErrorMessage ?? "Partial save cleared unrelated edits.");
                if (featuresOnly)
                    TestAssert.Near(feature.Value, TseHeadMorphRon.ReadDocument(path).Head.MorphData.MorphFeatures
                        .Single(value => value.Name == feature.Name).Offset, 0);
                else
                    TestAssert.Near(scalar.Value, FaceMaterialFileService.Read(File.ReadAllText(path), true).Parameters.Scalars
                        .Single(value => value.Name == scalar.Name).Value, 0);

                editor.UndoCommand.Execute(null); // Bone was never saved by either file.
                TestAssert.True(shell.IsDirty, "Undoing the bone edit hid another unsaved domain.");
                editor.UndoCommand.Execute(null);
                TestAssert.True(shell.IsDirty != featuresOnly,
                    "Dirty tracking did not compare the remaining edits with the partial-save baseline.");
                editor.UndoCommand.Execute(null);
                TestAssert.True(MorphFaceEditorStateComparer.Equals(initial, editor.CreateDraft()) && shell.IsDirty,
                    "Undoing a saved value did not make the source dirty again.");
                editor.RedoCommand.Execute(null);
                editor.RedoCommand.Execute(null);
                editor.RedoCommand.Execute(null);
                TestAssert.True(MorphFaceEditorStateComparer.Equals(edited, editor.CreateDraft()) && shell.IsDirty,
                    "Partial save disturbed cross-domain redo or its dirty state.");
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
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
