using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Rendering;
using System.Windows.Threading;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiPickerTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("attachment hover and commit preserve edits without publishing failed loads", AttachmentPreparationPublication),
        new("matching scalp maps suppress repeated hair-pair prompts and preserve manual donors", MatchingHairPairIsIdempotent),
        new("CC-only Mohawk pairing keeps pending edits chronological, clears Hair and respects its lock", CcOnlyMohawkPairsAndLocks),
        new("manual hair choices reject stale pairing and decline keeps only the mesh", ManualHairChoiceRejectsStalePairing),
        new("mesh picker preserves manual sources and requires proven Player runtime backing", RegistryDeduplicatesAutomaticOccurrences),
        new("physical texture donors remain distinct and explicit choices survive collapsing", TexturePickerDeduplicatesPhysicalExports),
        new("texture hover cache is bounded and reused by an undoable commit", TexturePickerBoundsPreviewCacheAndPreservesHistory),
        new("late texture decode cannot replace a newer preview or revive a disposed picker", TexturePickerRejectsStalePreviewCacheResults)
    ];

    private static void CcOnlyMohawkPairsAndLocks()
    {
        using var fixture = UiRandomisationTests.CreateHairScalpFixture(ccMohawk: true);
        var editor = fixture.Editor;
        var before = editor.CreateDraft();
        var choice = editor.HairMesh.Options.Single(value => value.Identity == fixture.Style.MeshVariants.Single());
        ManualAttachmentSelectionEventArgs? request = null;
        editor.HairMesh.ManualSelectionRequested += (_, args) => request = args;
        editor.HairMesh.Commit(choice);
        editor.HairMesh.CancelPreview(); // Closing the picker must retain the pending choice.
        TestAssert.True(request is not null && !editor.IsDirty && !editor.UndoCommand.CanExecute(null) &&
                        editor.HairMesh.PreviewSelection == choice &&
                        before.MorphFeatures.Any(value => HumanMaleHairScalpPolicy.IsHairMorph(value.Name) && value.Offset != 0) &&
                        MorphFaceEditorStateComparer.Equals(before, editor.CreateDraft()),
            "The pending picker choice authored data or disappeared when the popup closed.");
        editor.Material.Scalars.Single(value => value.Name == "HED_Norm_Blend").Value = 3;
        var beforeAcceptance = editor.CreateDraft();
        var prepared = editor.PrepareManualHairScalpPairAsync(choice.Identity!).GetAwaiter().GetResult()
            ?? throw new Exception("The CC-only LE3 Mohawk did not request its required scalp pair.");
        TestAssert.True(prepared.StyleId == "hmm-mhk" &&
                        MorphFaceEditorStateComparer.Equals(beforeAcceptance, editor.CreateDraft()),
            "Preparing the CC-only Mohawk authored a partial choice or selected another scalp style.");
        editor.CommitManualHairSelection(choice, request!.Generation, prepared);
        var accepted = editor.CreateDraft();
        TestAssert.True(accepted.HairMeshReference == choice.Identity &&
                        accepted.MorphFeatures.Where(value => HumanMaleHairScalpPolicy.IsHairMorph(value.Name))
                            .All(value => value.Offset == 0),
            "Accepting the CC Mohawk failed to retain its exact mesh and clear conflicting Hair morphs.");
        editor.UndoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(beforeAcceptance, editor.CreateDraft()),
            "One Undo failed to restore the pre-acceptance head with its intervening scalar edit.");
        editor.UndoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(before, editor.CreateDraft()) && !editor.IsDirty,
            "The pending pairing absorbed the earlier scalar edit.");
        editor.RedoCommand.Execute(null);
        editor.RedoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(accepted, editor.CreateDraft()),
            "One Redo failed to restore the complete CC Mohawk pairing.");

        editor.HairMesh.IsRandomisationLocked = true;
        editor.RandomiseMorphs = false;
        editor.RandomiseMaterials = true;
        editor.MaterialRandomisationStrength = 100;
        var scalarBeforeRoll = editor.Material.Scalars.Single(value => value.Name == "HED_Norm_Blend").Value;
        editor.RandomiseAsync().GetAwaiter().GetResult();
        var rolled = editor.CreateDraft();
        TestAssert.True(editor.Material.Scalars.Single(value => value.Name == "HED_Norm_Blend").Value != scalarBeforeRoll,
            "The locked-hair validation did not perform its available material roll.");
        TestAssert.True(rolled.HairMeshReference == choice.Identity &&
                        accepted.MaterialOverrides.Textures.SequenceEqual(rolled.MaterialOverrides.Textures),
            "The global roll replaced the locked CC Mohawk's matched scalp with the available Sar scalp.");
        editor.CursedMode = true;
        editor.RandomiseMorphs = false;
        editor.RandomiseAsync().GetAwaiter().GetResult();
        TestAssert.True(fixture.Material.HasCursedRandomisation, "The setup did not create Cursed material state.");
        editor.CursedMode = false;
        editor.RandomiseAsync().GetAwaiter().GetResult();
        TestAssert.True(accepted.MaterialOverrides.Textures.SequenceEqual(editor.CreateDraft().MaterialOverrides.Textures),
            "Normal cleanup reset the locked authored scalp pair to its unrelated material defaults.");
    }

    private static void MatchingHairPairIsIdempotent()
    {
        using var fixture = UiRandomisationTests.CreateHairScalpFixture(inheritedPair: true);
        var editor = fixture.Editor;
        var secondMesh = fixture.Style.MeshVariants[1];
        TestAssert.True(editor.PrepareManualHairScalpPairAsync(secondMesh).GetAwaiter().GetResult() is null,
            "Switching Ssk mesh variants requested a scalp change despite the already-matching inherited maps.");
        var wrongClass = fixture.Style.ScalpTextures.ToDictionary(value => value.Key,
            value => (AssetIdentity?)new AssetIdentity("other-package.pcc", value.Value, 7, "TextureCube"),
            StringComparer.OrdinalIgnoreCase);
        TestAssert.True(!PlayerHairStyleCatalog.MatchesScalpTextures(fixture.Style, wrongClass),
            "A same-path asset of another class was treated as the matching scalp texture.");

        foreach (var (name, path) in fixture.Style.ScalpTextures)
        {
            var texture = CreateTestTexture("manual-donor.pcc", path, name);
            fixture.Material.SetTextureReference(name,
                texture with { Source = texture.Source with { UIndex = 987, IsExplicitTextureDonor = true } });
        }
        var authored = editor.CreateDraft();
        TestAssert.True(editor.PrepareManualHairScalpPairAsync(secondMesh).GetAwaiter().GetResult() is null &&
                        MorphFaceEditorStateComparer.Equals(authored, editor.CreateDraft()),
            "Canonical matching replaced or re-prompted for the deliberate physical texture donors.");
        var normal = fixture.Material.GetSelectedTexture("HED_Scalp_Norm")!;
        fixture.Material.SetTextureReference("HED_Scalp_Norm",
            normal with { Source = normal.Source with { InstancedPath = "Unrelated.Hair.HMM_HIR_Sar_Norm" } });
        fixture.Material.PreviewTexture("HED_Scalp_Norm", normal);
        TestAssert.True(editor.PrepareManualHairScalpPairAsync(secondMesh).GetAwaiter().GetResult() is not null,
            "A matching hover hid the committed unrelated same-named normal map.");
    }

    private static void ManualHairChoiceRejectsStalePairing() => RunWithDispatcher(async () =>
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        var original = editor.CreateDraft();
        var first = new HairMeshOption("A", TestFixtures.CreateIdentity("Hair.A", "SkeletalMesh"));
        var second = new HairMeshOption("B", TestFixtures.CreateIdentity("Hair.B", "SkeletalMesh"));
        var requests = new List<ManualAttachmentSelectionEventArgs>();
        editor.HairMesh.ManualSelectionRequested += (_, args) => requests.Add(args);
        editor.HairMesh.Commit(first);
        editor.HairMesh.Commit(second);
        editor.HairMesh.Commit(first);
        TestAssert.Equal(3, requests.Count);
        editor.CommitManualHairSelection(first, requests[0].Generation, null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(original, editor.CreateDraft()) &&
                        !editor.UndoCommand.CanExecute(null),
            "An older A result was accepted after the picker moved A to B to A.");

        editor.CommitManualHairSelection(first, requests[2].Generation, null);
        var declined = editor.CreateDraft();
        TestAssert.True(declined.HairMeshReference == first.Identity &&
                        MorphFaceEditorStateComparer.Equals(original,
                            declined with { HairMeshReference = original.HairMeshReference }),
            "Declining the paired change failed to retain only the chosen mesh.");
        editor.UndoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(original, editor.CreateDraft()) && !editor.IsDirty,
            "Declining the pair added extra history or retained dirty state after Undo.");
        editor.RedoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(declined, editor.CreateDraft()),
            "Redo failed to restore the declined-pair mesh choice.");

        editor.HairMesh.Commit(second);
        var importedPending = requests[^1];
        editor.HairMesh.SetImportedPreview(first.Identity);
        editor.CommitManualHairSelection(second, importedPending.Generation, null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(declined, editor.CreateDraft()) &&
                        editor.HairMesh.PreviewSelection is null,
            "A same-reference import left the earlier picker choice eligible to commit.");
        editor.HairMesh.Commit(second);
        var randomisedPending = requests[^1];
        editor.HairMesh.TrySetRandomisedSelection(first.Identity);
        editor.CommitManualHairSelection(second, randomisedPending.Generation, null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(declined, editor.CreateDraft()),
            "A same-reference randomisation left the earlier picker choice eligible to commit.");

        editor.HairMesh.Commit(second);
        var pending = requests[^1];
        editor.Dispose();
        TestAssert.True(!editor.HairMesh.CommitManualSelection(second, pending.Generation),
            "A disposed picker accepted an outstanding manual choice.");

        using var fixture = UiRandomisationTests.CreateHairScalpFixture(ccMohawk: true);
        using var shellReader = new MorphFacePackageReader();
        using var shell = CreateMainWindowViewModel(shellReader);
        await shell.OpenDroppedFileAsync(Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE3 GlobalMorphs.pcc")));
        shell.SelectedFace = shell.Faces.First(face => face.ProfileKey == "le3-human-male");
        await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
        TestAssert.True(shell.Editor is not null, shell.ErrorMessage ?? "The real clipboard target did not load.");
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var clipboard = new MorphFaceEditor.QA.QaClipboard();
        typeof(MainWindowViewModel).GetField("_clipboard", flags)!.SetValue(shell, clipboard);
        // Keep real workspace/selection/command gates; replace only the loaded editor's decode boundary.
        typeof(MainWindowViewModel).GetMethod("SetEditor", flags)!.Invoke(shell, [fixture.Editor, null]);
        var delayed = new ControlledTextureLoader();
        typeof(MaterialTextureEditorViewModel).GetField("_references", flags)!.SetValue(
            fixture.Editor.Material.Textures.Single(value => value.Name == "HED_Scalp_Diff"), delayed);
        var mesh = fixture.Editor.HairMesh.Options.Single(value => value.Identity == fixture.Style.MeshVariants.Single());
        fixture.Editor.HairMesh.Commit(mesh);
        TestAssert.True(fixture.Editor.HairMesh.PreviewSelection == mesh && fixture.Editor.HairMesh.Value is null,
            "The scalp preparation did not retain its pending mesh choice.");
        clipboard.Set(MorphFaceClipboardPayload.Material(shell.SelectedFace!.ProfileKey, "ClipboardSource",
            new([new ScalarMaterialOverride("HED_Norm_Blend", 7)], [], [])));
        TestAssert.True(shell.PasteMaterialDataCommand.CanExecute(null), "The real Paste Materials command was disabled.");
        await ((AsyncRelayCommand)shell.PasteMaterialDataCommand).ExecuteAsync();
        TestAssert.True(!shell.HasError && fixture.Material.GetScalar("HED_Norm_Blend") == 7,
            shell.ErrorMessage ?? "The clipboard command did not apply the material payload.");
        delayed.Complete(fixture.Style.ScalpTextures["HED_Scalp_Diff"]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        TestAssert.True(fixture.Editor.HairMesh.Value is null && fixture.Editor.HairMesh.PreviewSelection is null,
            $"The pre-paste scalp result survived Paste Materials. Authored={fixture.Editor.HairMesh.Value?.InstancedPath ?? "None"}; " +
            $"pending={fixture.Editor.HairMesh.PreviewSelection?.Identity?.InstancedPath ?? "None"}; " +
            $"pasted scalar={fixture.Material.GetScalar("HED_Norm_Blend")}.");
    });

    private static void AttachmentPreparationPublication() => RunWithDispatcher(async () =>
    {
        using var reader = new MorphFacePackageReader();
        var registry = TestFixtures.CreateMissingTextureCatalogService();
        var registryGate = (SemaphoreSlim)registry.GetType().GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(registry)!;
        using var shell = CreateMainWindowViewModel(reader, textureCatalog: registry);
        // Keep the real catalogue read pending so this fixture always exercises
        // the initial refresh ordering, regardless of suite load or disk speed.
        await registryGate.WaitAsync();
        try
        {
            await shell.OpenDroppedFileAsync(Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE2 GlobalMorphs.pcc")));
            shell.SelectedFace = shell.Faces.First(face => face.ProfileKey == "le2-human-female");
            await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
            TestAssert.True(shell.Editor is { IsMeshDatabaseUnavailable: false },
                "The fixture did not retain its initial pending database read.");
        }
        finally { registryGate.Release(); }
        var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "Initial face did not load.");
        var scenePublications = 0;
        var expectedSceneMesh = "<database ready>";
        var lastSceneMeshes = "<no scene>";
        async Task<T> WaitForStageAsync<T>(Task<T> pending, string stage)
        {
            try { return await pending.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"Attachment {stage} timed out. Scenes={scenePublications}; " +
                    $"expected={expectedSceneMesh}; last={lastSceneMeshes}; " +
                    $"selected={editor.HairMesh.Value?.InstancedPath ?? "<none>"}; " +
                    $"hover={editor.HairMesh.PreviewSelection?.Identity?.InstancedPath ?? "<none>"}; " +
                    $"databaseUnavailable={editor.IsMeshDatabaseUnavailable}; " +
                    $"HasError={shell.HasError}; error={shell.ErrorMessage ?? "<none>"}.", exception);
            }
        }
        async Task<bool> WaitForInitialRegistryAsync()
        {
            while (!editor.IsMeshDatabaseUnavailable) await Task.Delay(10);
            // The availability notification precedes attachment candidate refresh.
            // Resume on a later UI turn, after that callback can finish cancelling previews.
            await Dispatcher.Yield(DispatcherPriority.Background);
            return true;
        }
        await WaitForStageAsync(WaitForInitialRegistryAsync(), "initial database load");
        var choice = editor.HairMesh.Options.First(option => option.Identity is { } identity &&
            identity != editor.HairMesh.Value && identity.InstancedPath.Contains("_HIR", StringComparison.OrdinalIgnoreCase));
        var colour = new Vector4(0.7f, 0.1f, 0.3f, 1);
        const string colourName = "HED_Hair_Colour_Vector";
        TestAssert.True(editor.Material.Vectors.Any(value => value.Name == colourName), "Fixture has no shared hair colour.");
        await editor.MergeMaterialDataAsync(new([], [new(colourName, colour)], []));
        var before = editor.CreateDraft();
        var materialState = editor.Material.Materials;
        var nextScene = new TaskCompletionSource<HeadPreviewScene>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedMesh = reader.LoadDetachedAttachment(choice.Identity!.PackagePath,
            choice.Identity.InstancedPath).Mesh.Source.InstancedPath;
        expectedSceneMesh = expectedMesh;
        var previousMesh = before.HairMeshReference is { } previousHair
            ? reader.LoadDetachedAttachment(previousHair.PackagePath, previousHair.InstancedPath).Mesh.Source.InstancedPath
            : null;
        Func<HeadPreviewScene, bool> acceptsScene = scene => scene.Meshes.Any(mesh =>
            mesh.IsAttachment && mesh.Name.Equals(expectedMesh, StringComparison.OrdinalIgnoreCase));
        shell.PreviewSceneReady += (scene, _) =>
        {
            scenePublications++;
            lastSceneMeshes = string.Join(", ", scene.Meshes.Where(mesh => mesh.IsAttachment).Select(mesh => mesh.Name));
            if (acceptsScene(scene)) nextScene.TrySetResult(scene);
        };
        editor.HairMesh.Preview(choice);
        var hovered = await WaitForStageAsync(nextScene.Task, "hover");
        TestAssert.True(MorphFaceEditorStateComparer.Equals(before, editor.CreateDraft()) &&
                        ReferenceEquals(materialState, editor.Material.Materials),
            "Hover published material controls or authored edits.");
        var hoverHair = hovered.Meshes.Where(mesh => mesh.Name.Equals(expectedMesh, StringComparison.OrdinalIgnoreCase))
            .SelectMany(mesh => mesh.Sections)
            .Select(section => section.Material).First(material => material.Vectors.ContainsKey(colourName));
        TestAssert.Equal(colour, hoverHair.Vectors[colourName]);
        editor.HairMesh.CancelPreview();

        nextScene = new TaskCompletionSource<HeadPreviewScene>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.HairMesh.Commit(choice);
        var committed = await WaitForStageAsync(nextScene.Task, "commit");
        TestAssert.True(!shell.HasError, shell.ErrorMessage ?? "Attachment commit failed.");
        TestAssert.Equal(choice.Identity, editor.HairMesh.Value);
        var committedHair = committed.Meshes.Where(mesh => mesh.Name.Equals(expectedMesh, StringComparison.OrdinalIgnoreCase))
            .SelectMany(mesh => mesh.Sections)
            .Select(section => section.Material).First(material => material.Vectors.ContainsKey(colourName));
        TestAssert.Equal(hoverHair.Key, committedHair.Key);
        TestAssert.Equal(colour, committedHair.Vectors[colourName]);
        nextScene = new TaskCompletionSource<HeadPreviewScene>(TaskCreationOptions.RunContinuationsAsynchronously);
        expectedSceneMesh = previousMesh ?? "<requested mesh absent>";
        acceptsScene = scene => previousMesh is not null
            ? scene.Meshes.Any(mesh => mesh.IsAttachment && mesh.Name.Equals(previousMesh, StringComparison.OrdinalIgnoreCase))
            : scene.Meshes.All(mesh => !mesh.Name.Equals(expectedMesh, StringComparison.OrdinalIgnoreCase));
        editor.UndoCommand.Execute(null);
        await WaitForStageAsync(nextScene.Task, "Undo");
        TestAssert.True(MorphFaceEditorStateComparer.Equals(before, editor.CreateDraft()),
            "One Undo failed to restore the attachment while retaining the earlier colour edit.");

        // A picker source may disappear after its candidates were enumerated.
        var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(shell.ErrorMessage) && shell.HasError) failed.TrySetResult(true); };
        nextScene = new TaskCompletionSource<HeadPreviewScene>(TaskCreationOptions.RunContinuationsAsynchronously);
        expectedSceneMesh = "<no publication>";
        materialState = editor.Material.Materials;
        var beforeFailure = scenePublications;
        editor.HairMesh.Commit(new HairMeshOption("Missing source", choice.Identity! with
            { PackagePath = Path.Combine(Path.GetTempPath(), $"MFE-Missing-{Guid.NewGuid():N}.pcc") }));
        await WaitForStageAsync(failed.Task, "missing-source error");
        TestAssert.True(ReferenceEquals(materialState, editor.Material.Materials) && scenePublications == beforeFailure,
            "An attachment preparation failure changed the live materials or scene.");
    });

    private static void RegistryDeduplicatesAutomaticOccurrences()
    {
        const string canonical = "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Custom_MDL";
        var local = new AttachmentMeshOccurrence(
            "C:\\Custom\\OpenedHair.pcc", canonical, 11, 0, TextureCatalogOrigin.Manual, 31);
        var officialWinner = new AttachmentMeshOccurrence(
            "BIOG_HMF_HIR_PRO.pcc", canonical, 20, 100, TextureCatalogOrigin.BaseGame, 42);
        var officialShadowed = new AttachmentMeshOccurrence(
            "DLC_CON_Hair.pcc", canonical, 21, 80, TextureCatalogOrigin.OfficialDlc, 43);
        var mod = new AttachmentMeshOccurrence(
            "DLC_MOD_Hair\\ModHair.pcc", canonical, 22, 9000, TextureCatalogOrigin.Mod, 47);
        var secondMod = new AttachmentMeshOccurrence(
            "DLC_MOD_Hair\\ModHairAlt.pcc", canonical, 25, 8500, TextureCatalogOrigin.Mod, 46);
        var manual = new AttachmentMeshOccurrence(
            "C:\\Custom\\AddedHair.pcc", canonical, 23, 0, TextureCatalogOrigin.Manual, 48);
        var excludedNonBiog = new AttachmentMeshOccurrence(
            "C:\\Custom\\OtherMesh.pcc", "Custom_Hair.Mesh.Custom_MDL", 24, 0,
            TextureCatalogOrigin.Manual, 49);
        var provenNonBiog = new AttachmentMeshOccurrence(
            "C:\\Custom\\RuntimeHair.pcc", "Custom_RuntimeHair.Mesh.Custom_MDL", 26, 0,
            TextureCatalogOrigin.Manual, 50);
        var localAsset = new PackageAssetListItem(new AssetIdentity(
            local.PackagePath, canonical, local.ExportUIndex, "SkeletalMesh"));
        using var editor = new HairMeshEditorViewModel(
            new AssetReferenceEditingSession(localAsset.Identity), [localAsset], "Hair", 0);
        var availability = new PlayerRuntimeAssetAvailability(MorphFaceGame.LE1,
            new[] { localAsset.Identity,
                new AssetIdentity("BIOG_HMF_HIR_PRO.pcc", "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Other_MDL", 30, "SkeletalMesh"),
                new AssetIdentity(provenNonBiog.PackagePath, provenNonBiog.InstancedPath, provenNonBiog.ExportUIndex, "SkeletalMesh") }
                .Select(PlayerRuntimeAssetAvailability.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        editor.RestrictPlayerReferences(availability);

        editor.UpdateRegistryCandidates(
            [
                new AttachmentMeshCandidate(canonical, officialWinner,
                    [officialWinner, officialShadowed, mod, secondMod, manual, local])
                {
                    IsEligibleOfficialDefault = true
                },
                new AttachmentMeshCandidate(
                    "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Other_MDL",
                    new AttachmentMeshOccurrence("BIOG_HMF_HIR_PRO.pcc",
                        "Hair.HMF_HIR_Other_MDL", 30, 100, TextureCatalogOrigin.BaseGame, 45),
                    [
                        new AttachmentMeshOccurrence("BIOG_HMF_HIR_PRO.pcc",
                            "Hair.HMF_HIR_Other_MDL", 30, 100, TextureCatalogOrigin.BaseGame, 45),
                        new AttachmentMeshOccurrence("DLC_CON_Hair.pcc",
                            "Hair.HMF_HIR_Other_MDL", 31, 80, TextureCatalogOrigin.OfficialDlc, 46)
                    ])
                {
                    IsEligibleOfficialDefault = true
                },
                new AttachmentMeshCandidate(excludedNonBiog.InstancedPath, excludedNonBiog, [excludedNonBiog]),
                new AttachmentMeshCandidate(provenNonBiog.InstancedPath, provenNonBiog, [provenNonBiog])
            ],
            isPlayerWorkspace: true);

        var options = editor.Candidates.Skip(1).ToArray();
        TestAssert.Equal(local.PackagePath, options[0].Identity?.PackagePath);
        TestAssert.True(!options.Any(option => option.Identity?.InstancedPath == canonical &&
                option.Identity.PackagePath == officialWinner.PackagePath),
            "A local authored source was duplicated by its official canonical donor row.");
        TestAssert.Equal("BIOG_HMF_HIR_PRO.pcc", options.Single(option =>
            option.Identity?.InstancedPath == "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Other_MDL").Identity?.PackagePath);
        TestAssert.True(!options.Any(option => option.Identity?.PackagePath == officialShadowed.PackagePath),
            "A second official donor row duplicated the same canonical mesh.");
        TestAssert.True(!options.Any(option => option.Identity?.PackagePath == mod.PackagePath ||
                option.Identity?.PackagePath == secondMod.PackagePath),
            "Automatic mod copies duplicated the local authored mesh identity.");
        foreach (var occurrence in new[] { manual })
        {
            TestAssert.True(options.Any(option =>
                    string.Equals(option.Identity?.PackagePath, occurrence.PackagePath, StringComparison.OrdinalIgnoreCase) &&
                    option.Identity?.UIndex == occurrence.ExportUIndex),
                $"The picker omitted physical mesh occurrence '{occurrence.PackagePath}' #{occurrence.ExportUIndex}.");
        }
        TestAssert.Equal(options.Length, options
            .Where(option => option.Identity is not null)
            .Select(option => $"{Path.GetFullPath(option.Identity!.PackagePath)}|{option.Identity.UIndex}")
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        TestAssert.True(options.All(option => option.Identity is { } identity && availability.Allows(identity)),
            "The player workspace exposed a mesh without proven runtime backing.");
        TestAssert.True(options.Any(option => option.Identity?.InstancedPath == provenNonBiog.InstancedPath),
            "A proven runtime mesh was rejected because its path lacks the BIOG prefix.");
        TestAssert.Equal(localAsset.Identity, editor.Selected.Identity);
    }

    private static void TexturePickerDeduplicatesPhysicalExports()
    {
        var session = MaterialTestFixtures.CreateSession();
        const string path = "BIOG_MFE_Test.Textures.Shared_Diff";
        var local = new PackageAssetListItem(new AssetIdentity("opened.pcc", path, 12, "Texture2D"));
        var localOnly = new PackageAssetListItem(new AssetIdentity(
            "opened.pcc", "LocalOnly.Textures.Local_Diff", 13, "Texture2D"));
        var official = new TextureCatalogOccurrence(
            "BIOG_MFE_Test.pcc", 41, 100, TextureCatalogOrigin.BaseGame, 128, 128,
            "PF_DXT1", "TEXTUREGROUP_Character", false, null);
        // The same physical export can arrive through both the occurrence list and
        // effective default with slightly different registry metadata.
        var duplicate = official with { MountPriority = 99 };
        var mod = official with
        {
            PackagePath = "DLC_MOD_Test\\CookedPCConsole\\BIOG_MFE_Test.pcc",
            ExportUIndex = 42,
            Origin = TextureCatalogOrigin.Mod,
            MountPriority = 900
        };
        var manual = new TextureCatalogOccurrence(
            "C:\\Manual\\ManualTexture.pcc", 88, 0, TextureCatalogOrigin.Manual, 128, 128,
            "PF_DXT1", "TEXTUREGROUP_Character", false, null);
        var manualOnly = new TextureCatalogCandidate(MorphFaceGame.LE3,
            "ManualOnly.Textures.Manual_Diff", manual, [manual]);
        var candidate = new TextureCatalogCandidate(MorphFaceGame.LE3, path, official,
            [duplicate, mod]) { IsEligibleOfficialDefault = true };
        using var editor = new MaterialTextureEditorViewModel(
            session,
            HumanMaterialProfiles.Describe("HED_Diff", MaterialParameterKind.Texture, HeadMaterialFamily.Skin),
            new ImmediateTextureLoader(),
            "opened.pcc",
            [local, localOnly],
            _ => { },
            [candidate, manualOnly],
            isRegistryAvailable: true);

        var defaultChoices = editor.Candidates.Where(option => option.InstancedPath.Equals(
            path, StringComparison.OrdinalIgnoreCase)).ToArray();
        TestAssert.True(defaultChoices.Length == 1,
            "The default picker should collapse a path to its eligible official occurrence.");
        TestAssert.True(defaultChoices[0].RegistryCandidate?.EffectiveOccurrence.PackagePath == official.PackagePath,
            "The collapsed row did not use the eligible official donor.");
        TestAssert.True(editor.Candidates.Any(option => option.InstancedPath == localOnly.Identity.InstancedPath),
            "A unique local-only texture disappeared from the default picker.");
        TestAssert.True(editor.Candidates.Any(option => option.InstancedPath == manualOnly.InstancedPath),
            "A unique manual texture disappeared from the default picker.");

        editor.ShowAllPhysicalSources = true;
        var choices = editor.Candidates.Where(option => option.InstancedPath.Equals(
            path, StringComparison.OrdinalIgnoreCase)).ToArray();
        TestAssert.True(choices.Length == 3,
            "The expanded picker should show the local export, one official export, and one distinct mod export.");
        TestAssert.True(choices.Any(option => option.Asset?.Identity == local.Identity),
            "The local physical texture choice disappeared.");
        TestAssert.True(choices.Count(option => option.RegistryCandidate?.EffectiveOccurrence.PackagePath == official.PackagePath) == 1,
            "The same official package/UIndex was shown twice.");
        TestAssert.True(choices.Any(option => option.RegistryCandidate?.EffectiveOccurrence.PackagePath == mod.PackagePath),
            "The distinct mod physical texture choice disappeared.");

        var modOption = choices.Single(option => option.RegistryCandidate?.EffectiveOccurrence.PackagePath == mod.PackagePath);
        editor.CommitTexture(modOption);
        TestAssert.True(session.GetTextureReference("HED_Diff")?.IsExplicitTextureDonor == true,
            "Committing a physical source did not record explicit picker intent.");
        editor.ShowAllPhysicalSources = false;
        TestAssert.True(editor.SelectedTexture?.RegistryCandidate?.EffectiveOccurrence.PackagePath == mod.PackagePath,
            "Collapsing physical sources discarded the user's explicitly selected donor.");
    }

    private static void TexturePickerBoundsPreviewCacheAndPreservesHistory()
    {
        const string packagePath = "hover-donors.pcc";
        var session = MaterialTestFixtures.CreateSession();
        var loader = new SyntheticTextureLoader();
        var candidates = CreateSyntheticTextureCandidates(packagePath, 4);
        using var editor = new MaterialTextureEditorViewModel(
            session,
            HumanMaterialProfiles.Describe("HED_Diff", MaterialParameterKind.Texture, HeadMaterialFamily.Skin),
            loader,
            "opened.pcc",
            candidates,
            _ => { });
        var options = editor.Candidates.Where(option => option.Asset?.Identity.PackagePath == packagePath)
            .OrderBy(option => option.InstancedPath, StringComparer.OrdinalIgnoreCase).ToArray();
        TestAssert.Equal(4, options.Length);

        foreach (var option in options)
        {
            editor.PreviewTextureAsync(option).GetAwaiter().GetResult();
            var cached = GetResolvedCandidateTextures(editor);
            TestAssert.True(cached.Count <= 1,
                "Hovering another donor left older full-mip previews pinned in the picker cache.");
            TestAssert.True(cached.Values.Single().Mips is { Count: 3 },
                "The cache optimization dropped authored mips needed by the live preview.");
        }

        TestAssert.Equal(4, loader.Calls);
        var lastOption = options[^1];
        var preview = session.GetPreviewTexture("HED_Diff");
        TestAssert.Equal(lastOption.InstancedPath, preview?.Source.InstancedPath);
        editor.CancelTexturePreview();
        editor.CommitTexture(lastOption);

        TestAssert.True(loader.Calls == 4,
            "Committing the last hovered option decoded it again after the transient preview was cancelled.");
        TestAssert.Equal(lastOption.InstancedPath,
            session.GetTextureReference("HED_Diff")?.InstancedPath);
        TestAssert.True(session.CanUndo, "Committing a preview did not create normal material history.");
        session.Undo();
        TestAssert.True(session.GetTextureReference("HED_Diff")?.InstancedPath != lastOption.InstancedPath,
            "Undo did not restore the previously committed texture reference.");
        session.Redo();
        TestAssert.Equal(lastOption.InstancedPath,
            session.GetTextureReference("HED_Diff")?.InstancedPath);
        TestAssert.True(session.GetSelectedTexture("HED_Diff")?.Mips is { Count: 3 },
            "Committing the bounded preview changed the full-mip texture used by the material.");
    }

    private static void TexturePickerRejectsStalePreviewCacheResults()
    {
        const string packagePath = "hover-race-donors.pcc";
        var session = MaterialTestFixtures.CreateSession();
        var loader = new ControlledTextureLoader();
        using var editor = new MaterialTextureEditorViewModel(
            session,
            HumanMaterialProfiles.Describe("HED_Diff", MaterialParameterKind.Texture, HeadMaterialFamily.Skin),
            loader,
            "opened.pcc",
            CreateSyntheticTextureCandidates(packagePath, 3),
            _ => { });
        var options = editor.Candidates.Where(option => option.Asset?.Identity.PackagePath == packagePath)
            .OrderBy(option => option.InstancedPath, StringComparer.OrdinalIgnoreCase).ToArray();
        TestAssert.Equal(3, options.Length);

        var staleHover = editor.PreviewTextureAsync(options[0]);
        var winningHover = editor.PreviewTextureAsync(options[1]);
        loader.Complete(options[1].InstancedPath);
        winningHover.GetAwaiter().GetResult();
        var cache = GetResolvedCandidateTextures(editor);
        TestAssert.Equal(options[1].InstancedPath, cache.Values.Single().Source.InstancedPath);

        // Model a decode already in progress: it ignores cancellation but must not overwrite the newer hover.
        loader.Complete(options[0].InstancedPath);
        staleHover.GetAwaiter().GetResult();
        TestAssert.Equal(1, cache.Count);
        TestAssert.True(options[1].InstancedPath == cache.Values.Single().Source.InstancedPath,
            "A late cancelled hover evicted the newer successful preview.");
        TestAssert.True(options[1].InstancedPath == session.GetPreviewTexture("HED_Diff")?.Source.InstancedPath,
            "A late cancelled hover replaced the newer live preview.");

        var disposedHover = editor.PreviewTextureAsync(options[2]);
        editor.Dispose();
        loader.Complete(options[2].InstancedPath);
        disposedHover.GetAwaiter().GetResult();
        TestAssert.True(cache.Count == 0,
            "A pending preview repopulated the cache after its editor was disposed.");
    }

    private static PackageAssetListItem[] CreateSyntheticTextureCandidates(string packagePath, int count) =>
        Enumerable.Range(1, count).Select(index => new PackageAssetListItem(new AssetIdentity(
            packagePath, $"BIOG_MFE_Test.Textures.Hover{index:D2}_Diff", 100 + index, "Texture2D"))).ToArray();

    private static IReadOnlyDictionary<string, DecodedTextureAsset> GetResolvedCandidateTextures(
        MaterialTextureEditorViewModel editor)
    {
        var field = typeof(MaterialTextureEditorViewModel).GetField(
            "_resolvedCandidateTextures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The texture picker resolved-preview cache was not found.");
        return (IReadOnlyDictionary<string, DecodedTextureAsset>)(field.GetValue(editor)
            ?? throw new InvalidOperationException("The texture picker resolved-preview cache was null."));
    }

    private static DecodedTextureAsset CreateSyntheticFullMipTexture(
        string packagePath,
        string texturePath,
        MaterialParameterDefinition definition)
    {
        var top = Enumerable.Repeat((byte)128, 4 * 4 * 4).ToArray();
        var mid = Enumerable.Repeat((byte)128, 2 * 2 * 4).ToArray();
        var bottom = Enumerable.Repeat((byte)128, 4).ToArray();
        return new DecodedTextureAsset(
            new AssetIdentity(packagePath, texturePath, 42, "Texture2D"),
            4, 4, top, "PF_B8G8R8A8", definition.TextureRole, definition.ColorSpace,
            definition.AlphaPolicy, false, texturePath, 4, 4,
            [new DecodedTextureMip(4, 4, top), new DecodedTextureMip(2, 2, mid), new DecodedTextureMip(1, 1, bottom)]);
    }

    private sealed class SyntheticTextureLoader : ITextureReferenceLoader
    {
        public int Calls { get; private set; }

        public Task<DecodedTextureAsset> LoadTextureAsync(
            string packagePath,
            string texturePath,
            MaterialParameterDefinition definition,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(CreateSyntheticFullMipTexture(packagePath, texturePath, definition));
        }
    }

    private sealed class ControlledTextureLoader : ITextureReferenceLoader
    {
        private readonly Dictionary<string, TaskCompletionSource<DecodedTextureAsset>> _pending =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<DecodedTextureAsset> LoadTextureAsync(
            string packagePath,
            string texturePath,
            MaterialParameterDefinition definition,
            CancellationToken cancellationToken = default)
        {
            var source = new TaskCompletionSource<DecodedTextureAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(texturePath, source);
            // Deliberately do not observe cancellation: an already-running package decode may finish later.
            _ = cancellationToken;
            return source.Task;
        }

        public void Complete(string texturePath)
        {
            if (!_pending.TryGetValue(texturePath, out var source))
                throw new InvalidOperationException($"No pending texture decode exists for '{texturePath}'.");
            var definition = HumanMaterialProfiles.Describe(
                "HED_Diff", MaterialParameterKind.Texture, HeadMaterialFamily.Skin);
            source.TrySetResult(CreateSyntheticFullMipTexture("hover-race-donors.pcc", texturePath, definition));
        }
    }
}
