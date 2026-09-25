using System.Numerics;
using System.Text;
using System.Threading;
using System.Windows;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Views;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Core.Randomisation;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Rendering;
using System.Windows.Threading;

namespace MorphFaceEditor.Tests;

// Binding-safe UI checks; these avoid launching the full window or loading game packages.
public static class UiSmokeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("combo-box models display their labels", ComboModelsDisplayLabels),
        new("LOD-specific morph controls are explicitly marked", LodSpecificMorphControlsAreMarked),
        new("morph controls disable when the selected LOD has no target geometry", MorphControlsDisableForTargetlessLod),
        new("Fix Morph undo restores baked preview and UI state", FixMorphUndoRestoresBakedPreview),
        new("texture thumbnails discard alpha", TextureThumbnailsDiscardAlpha),
        new("editor file drops recognise every supported format", EditorFileDropsRecogniseSupportedFormats),
        new("recent files persist, deduplicate, and cap at ten", RecentFilesPersistDeduplicateAndCap),
        new("startup welcome preference persists", StartupWelcomePreferencePersists),
        new("standalone morph import is available before opening a PCC", StandaloneImportIsAvailableWithoutPackage),
        new("unrecognised mesh import publishes a detached preview workspace", UnrecognisedMeshPublishesDetachedWorkspace),
        new("glTF and GLB drops publish detached mesh workspaces", GltfAndGlbDropsPublishDetachedWorkspaces),
        new("morph import asks for its source game with an open PCC", ImportAsksForSourceGameWithOpenPcc),
        new("NPC RON donor choices include supported game-local archetypes", NpcRonArchetypesAreGameLocal),
        new("NPC RON resolver accepts static ALN donors and rejects player donors", RonResolverAcceptsStaticAlnDonor),
        new("empty editor imports a tagged native NPC RON", EmptyEditorImportsTaggedNpcRon),
        new("NPC import cancellation command cancels an active request", NpcImportCancellationCommandCancelsActiveRequest),
        new("tagged human RON follows the user's NPC or Player destination", TaggedHumanRonFollowsDestination),
        new("standalone Gibbed import rejects the wrong selected game before mutation", StandaloneGibbedRejectsWrongGame),
        new("standalone fixed-bake workspaces allow material clipboard commands", StandaloneFixedBakeMaterialClipboardCommands),
        new("numeric wheel increments are finite and crash-safe", NumericWheelIncrementsAreSafe),
        new("extended sliders are optional and preserve edited values", ExtendedSlidersAreOptional),
        new("each face editor owns a valid selected bone transform", BoneTransformSelectionIsPerEditor),
        new("bone controls tolerate bones removed by zeroed morphs", BoneControlsTolerateRemovedMorphBones),
        new("attachment editor exposes two slots and preserves extras", AttachmentEditorUsesTwoSlots),
        new("morph clipboard codec round-trips typed versioned data", MorphClipboardCodecRoundTrips),
        new("pasted morph and material data remain live unsaved edits", PastedDataRemainsLiveAndDirty),
        new("face editor category selection can be restored by key", FaceEditorCategoryRestoresByKey),
        new("randomisation commands honour global and subcategory morph scopes", RandomisationCommandsHonorScopes),
        new("locked Player hair morph group cannot randomise without another eligible scope", LockedPlayerHairMorphGroupCannotRandomise),
        new("Player Character morph randomisation keeps only scalp-safe exceptions", PlayerCharacterMorphScopeKeepsExceptions),
        new("HMF mesh rolls clear only unlocked hair morphs", FemaleMeshRollClearsUnlockedHairMorphs),
        new("Player colour randomisation applies linked 2DA rows and Cursed variation", PlayerColourRandomisationUses2Da),
        new("Player hair and addition colours randomise for HMM in all games", PlayerHairAndAdditionColoursRandomiseAcrossGames),
        new("Set to Defaults restores stock morph and material values atomically", SetToDefaultsRestoresStockState),
        new("global morph and material randomisation uses separate donors", GlobalRandomisationUsesSeparateDonors),
        new("subcategory padlocks protect randomisation and persist in-session", SubcategoryInclusionsFilterGlobalScope),
        new("normal material randomisation obeys its independent toggle and undo", MaterialRandomisationObeysToggle),
        new("detached material randomisation uses its compatible human donor profile", DetachedMaterialRandomisationUsesCompatibleProfile),
        new("detached mixed-species materials randomise from each assigned profile", DetachedMixedSpeciesMaterialsRandomiseByProfile),
        new("material vector subcategories expose independent randomise commands", MaterialVectorSubcategoriesRandomiseIndependently),
        new("exhausted texture randomisation is reported without discarding numeric values", ExhaustedTextureRandomisationIsReported),
        new("LE3 HMM scalp randomisation preserves its required texture pair", Le3HmmScalpRandomisationAppliesCorePair),
        new("manual hair scalp prompt survives an optional Mask decode failure", ManualHairScalpPromptKeepsRequiredPairWhenOptionalMapFails),
        new("cursed mode randomises morph bones and materials as one undo step", CursedModeRandomisesOneUndoStep),
        new("Cursed material-only rolls leave morphs and bones unchanged", CursedMaterialOnlyLeavesBonesUnchanged),
        new("fixed-bake Cursed mode cannot mutate morph sliders", FixedBakeCursedModePreservesMorphs),
        new("relative-bake Player RON exposes live morph controls", RelativeBakeExposesMorphControls),
        new("cursed mode respects global randomisation exclusions", CursedModeRespectsGlobalExclusions),
        new("cursed mode can be enabled without a donor corpus", CursedModeBypassesDonorAvailability),
        new("failed cursed randomisation rolls back its partial edit", FailedCursedRandomisationRollsBack),
        new("repeated cursed randomisation does not compound", RepeatedCursedRandomisationDoesNotCompound),
        new("embedded randomisation corpus loads all pools and excludes Broke", EmbeddedRandomisationCorpusLoads),
        new("mesh attachment picker filters candidates by name and path", MeshAttachmentPickerFiltersCandidates),
        new("mesh attachment picker merges installed HIR meshes and preserves mod override", MeshAttachmentPickerMergesRegistry),
        new("mesh attachment picker previews without committing history", MeshAttachmentPickerPreviewDoesNotCommit),
        new("editor error banners can be dismissed", ErrorBannerCanBeDismissed),
        new("texture registry settings command opens the settings dialog", TextureRegistrySettingsCommandOpensDialog),
        new("actor assignment chooser filters evidence and scopes eligibility by operation", ActorChooserFiltersAndScopesEligibility),
        new("WPF resources, nested menus, and HDR colour controls work", HdrPickerConstructs),
        new("Human Male UI profile orders, groups, and filters features", HumanMaleProfileOrganizesFeatures),
        new("material editor hides Unreal selection colour parameters", MaterialEditorHidesSelectionColor),
        new("LE3 Human Male UI hides inert eye metadata and marks vestigial pupils", Le3HumanMaleProfileOrganizesFeatures),
        new("Human Female UI profile exposes female morph and makeup controls", HumanFemaleProfileOrganizesFeatures),
        new("LE3 Human Female UI exposes character targets and hides malformed hair targets", Le3HumanFemaleProfileOrganizesFeatures),
        new("Asari UI profile exposes head-crest and species material controls", AsariProfileOrganizesFeatures),
        new("Salarian UI profile exposes cranial-ring and species material controls", SalarianProfileOrganizesFeatures),
        new("Turian UI profile exposes mandibles, head spikes, and species material controls", TurianProfileOrganizesFeatures),
        new("Batarian UI profile groups racial structure and species material controls", BatarianProfileOrganizesFeatures),
        new("Krogan UI profile separates head plates and Wrex character controls", KroganProfileOrganizesFeatures),
        new("Vorcha UI keeps reconstructed morphs hidden and bones editable", VorchaProfileIsMaterialAndBoneOnly),
        new("Female Turian UI exposes Turian materials without inherited geometry controls", FemaleTurianProfileIsMaterialOnly),
        new("detached mesh UI reuses racial material presentation", DetachedMeshProfileUsesRacialMaterialPresentation),
        new("metadata text inherits shared archetype and game variants", MetadataTextInheritsAcrossProfiles)
    ];

    private static void EditorFileDropsRecogniseSupportedFormats()
    {
        TestAssert.Equal(EditorFileDropKind.Package, EditorFileDrop.Classify("BioD_Test.PCC"));
        TestAssert.True(EditorFileDrop.WorkspaceOpenFilter.Contains("*.pcc", StringComparison.OrdinalIgnoreCase),
            "The unified Open Package picker omitted PCC files.");
        foreach (var extension in new[]
                 {
                     ".ron", ".psk", ".pskx", ".gltf", ".glb", ".md5", ".md5mesh",
                     ".me2headmorph", ".me3headmorph"
                 })
        {
            TestAssert.Equal(EditorFileDropKind.MorphImport, EditorFileDrop.Classify($"face{extension}"));
            TestAssert.True(EditorFileDrop.WorkspaceOpenFilter.Contains($"*{extension}", StringComparison.OrdinalIgnoreCase),
                $"The unified Open Package picker omitted {extension} files.");
        }
        TestAssert.Equal(EditorFileDropKind.Unsupported, EditorFileDrop.Classify("notes.txt"));
        TestAssert.Equal(EditorFileDropKind.Unsupported, EditorFileDrop.Classify("folder.with.pcc\\face.txt"));
    }

    private static void RecentFilesPersistDeduplicateAndCap()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"MFE-Recent-{Guid.NewGuid():N}");
        var storagePath = Path.Combine(folder, "recent-files.json");
        try
        {
            var service = new RecentFileService(storagePath);
            var paths = Enumerable.Range(0, 12)
                .Select(index => Path.Combine(folder, $"Face-{index}.pcc"))
                .ToArray();
            foreach (var path in paths)
            {
                service.Add(path);
            }
            service.Add(paths[5]);

            TestAssert.Equal(RecentFileService.MaximumFiles, service.Paths.Count);
            TestAssert.Equal(Path.GetFullPath(paths[5]), service.Paths[0]);
            TestAssert.Equal(1, service.Paths.Count(path =>
                string.Equals(path, paths[5], StringComparison.OrdinalIgnoreCase)));

            var reloaded = new RecentFileService(storagePath);
            TestAssert.True(service.Paths.SequenceEqual(reloaded.Paths, StringComparer.OrdinalIgnoreCase),
                "The recent-file order did not survive reloading the persisted list.");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static void StartupWelcomePreferencePersists()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"MFE-Startup-{Guid.NewGuid():N}");
        var storagePath = Path.Combine(folder, "startup-preferences.json");
        try
        {
            var service = new StartupPreferencesService(storagePath);
            TestAssert.True(!service.Current.SuppressWelcome,
                "A new preferences store suppressed the startup welcome by default.");
            service.SetSuppressWelcome(true);

            var reloaded = new StartupPreferencesService(storagePath);
            TestAssert.True(reloaded.Current.SuppressWelcome,
                "The Don't Show Again preference did not survive reloading.");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static void StandaloneImportIsAvailableWithoutPackage()
    {
        using var reader = new MorphFacePackageReader();
        var dialogs = new StubEditorDialogs();
        using var viewModel = CreateMainWindowViewModel(reader, dialogs);

        TestAssert.True(viewModel.ImportMorphCommand.CanExecute(null),
            "Import Morph remained coupled to an open PCC and selected face.");
        TestAssert.True(viewModel.CanOpenDroppedFile("player.ron"),
            "An empty editor rejected a dropped player RON before game selection.");
        TestAssert.True(!viewModel.SaveCommand.CanExecute(null),
            "An empty editor exposed Save PCC.");
        viewModel.ImportMorphCommand.Execute(null);
        TestAssert.Equal(1, dialogs.StandaloneGameChoiceCount);
        TestAssert.Equal(0, dialogs.MorphImportFileChoiceCount);
    }

    private static void UnrecognisedMeshPublishesDetachedWorkspace()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"MFE-Detached-{Guid.NewGuid():N}.psk");
        try
        {
            new PSK
            {
                Points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
                Wedges =
                [
                    new() { PointIndex = 0, U = 0, V = 0 },
                    new() { PointIndex = 1, U = 1, V = 0 },
                    new() { PointIndex = 2, U = 0, V = 1 }
                ],
                Faces = [new() { WedgeIdx0 = 0, WedgeIdx1 = 1, WedgeIdx2 = 2, MatIndex = 0 }],
                Materials = [new() { Name = "Unknown" }],
                Bones = [],
                Weights = [],
                VertexNormals = []
            }.ToFile(sourcePath);
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs
            {
                StandaloneGameChoiceResult = MorphFaceGame.LE2,
                StandaloneNameChoiceResult = "DetachedFixture"
            };
            using var viewModel = CreateMainWindowViewModel(reader, dialogs);
            HeadPreviewScene? scene = null;
            viewModel.PreviewSceneReady += (value, _) => scene = value;

            RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(sourcePath));

            TestAssert.True(viewModel.ErrorMessage is null, viewModel.ErrorMessage ?? "Detached mesh import failed.");
            TestAssert.True(viewModel.RecentFiles.FirstOrDefault()?.FilePath == Path.GetFullPath(sourcePath),
                "Opening a detached workspace did not add it to Recent.");
            TestAssert.True(viewModel.PackageName.Contains("Detached Mesh Workspace", StringComparison.Ordinal),
                "Unrecognised mesh was not published as a detached workspace.");
            TestAssert.True(viewModel.Editor is { CanEditMorphFeatures: false, CanEditBones: false, HasMorphControls: false },
                "Unrigged detached mesh exposed morph or bone controls.");
            TestAssert.Equal(0, viewModel.Editor?.Features.Count ?? -1);
            TestAssert.True(viewModel.Editor?.AllowsMorphRandomisation == false,
                "Detached custom mesh exposed morph randomisation.");
            TestAssert.True(viewModel.Editor?.CanEditAttachments == true,
                "Detached mesh did not expose its preview-only attachment selectors.");
            TestAssert.True(scene?.Meshes.Single().Vertices.Count == 3 && scene.Meshes.Single().ApplySkinning == false,
                "Detached mesh did not reach the renderer with its authored LOD0.");
            TestAssert.True(!viewModel.SaveCommand.CanExecute(null),
                "Detached mesh exposed Save PCC for a package it does not own.");
            TestAssert.True(viewModel.SaveMorphToPccCommand.CanExecute(null),
                "Detached mesh did not expose Save to PCC for a new package.");
            TestAssert.True(viewModel.IsDetachedMeshWorkspace,
                "Detached mesh was not identified as a detached workspace.");
            TestAssert.True(viewModel.ExportMaterialsCommand.CanExecute(null) && viewModel.ImportMaterialsCommand.CanExecute(null),
                "Detached mesh did not expose its MFE material file actions.");
            TestAssert.True(!viewModel.ExportTseMaterialsCommand.CanExecute(null) && !viewModel.ImportTseMaterialsCommand.CanExecute(null),
                "Unassigned mesh exposed TSE material actions.");
            var editor = viewModel.Editor!;
            // This shell fixture deliberately has no installed catalogue; seed only the assignment needed for routing.
            var template = MaterialTestFixtures.CreateSession().Materials.Materials.Values.First();
            editor.CustomMaterials!.Assign(0, new CustomMaterialAssignmentOption(
                "human-test", "Human test", "le2-human-male", "head", HeadMaterialFamily.Skin, template)
                { ParameterScopeKey = "human", ParameterScopeLabel = "Human" });
            TestAssert.True(viewModel.ExportTseMaterialsCommand.CanExecute(null) && viewModel.ImportTseMaterialsCommand.CanExecute(null),
                "Human assignment did not enable TSE material actions.");
            var gamePrompts = dialogs.StandaloneGameChoiceCount;
            var namePrompts = dialogs.StandaloneNameChoiceCount;
            viewModel.ImportTseMaterialsCommand.Execute(null);
            viewModel.ImportMaterialsCommand.Execute(null);
            TestAssert.Equal(2, dialogs.MaterialImportFileChoiceCount);
            TestAssert.Equal(gamePrompts, dialogs.StandaloneGameChoiceCount);
            TestAssert.Equal(namePrompts, dialogs.StandaloneNameChoiceCount);
            TestAssert.True(ReferenceEquals(editor, viewModel.Editor) && viewModel.IsDetachedMeshWorkspace,
                "Cancelling a context material import changed the current workspace.");

            var previewWasCleared = false;
            viewModel.PreviewCleared += (_, _) => previewWasCleared = true;
            TestAssert.True(viewModel.CloseWorkspaceCommand.CanExecute(null),
                "An active detached workspace did not expose Close.");
            RunWithDispatcher(viewModel.CloseWorkspaceAsync);
            TestAssert.True(viewModel.HasWorkspace && viewModel.Editor is not null && !previewWasCleared,
                "Cancelling Close discarded the active workspace.");
            dialogs.ConfirmUnsavedChangesResult = UnsavedChangesChoice.Discard;
            RunWithDispatcher(viewModel.CloseWorkspaceAsync);
            TestAssert.True(previewWasCleared && !viewModel.HasPreview,
                "Closing the workspace did not clear its preview.");
            TestAssert.True(viewModel.PackagePath is null && viewModel.Editor is null && viewModel.Faces.Count == 0,
                "Closing the workspace did not restore the empty editor state.");
            TestAssert.True(viewModel.PackageName == "No package open" &&
                            viewModel.Status == "Open a package to begin." &&
                            !viewModel.CloseWorkspaceCommand.CanExecute(null),
                "Closing the workspace did not restore the startup shell state.");
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
        }
    }

    private static void GltfAndGlbDropsPublishDetachedWorkspaces()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"MFE-UiGltf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var binary in new[] { false, true })
            {
                var extension = binary ? ".glb" : ".gltf";
                var sourcePath = Path.Combine(folder, "DetachedFixture" + extension);
                File.WriteAllBytes(sourcePath, CreateMinimalTriangleGltf(binary));
                using var reader = new MorphFacePackageReader();
                var dialogs = new StubEditorDialogs
                {
                    StandaloneGameChoiceResult = MorphFaceGame.LE2,
                    StandaloneNameChoiceResult = "DetachedGltfFixture"
                };
                using var viewModel = CreateMainWindowViewModel(reader, dialogs);

                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(sourcePath));

                TestAssert.True(viewModel.ErrorMessage is null,
                    viewModel.ErrorMessage ?? $"{extension} drop failed.");
                TestAssert.True(viewModel.IsDetachedMeshWorkspace &&
                                viewModel.PackageName.Contains("Detached Mesh Workspace", StringComparison.Ordinal),
                    $"{extension} did not publish a detached mesh workspace.");
                TestAssert.True(viewModel.Editor is { CanEditMorphFeatures: false, CanEditBones: false },
                    $"{extension} detached mesh exposed unsupported geometry capabilities.");
                TestAssert.True(viewModel.SaveMorphToPccCommand.CanExecute(null),
                    $"{extension} detached mesh did not expose Save to PCC.");
            }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static byte[] CreateMinimalTriangleGltf(bool binary)
    {
        var buffer = new byte[42];
        var positions = new[]
        {
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f
        };
        for (var index = 0; index < positions.Length; index++)
            Array.Copy(BitConverter.GetBytes(positions[index]), 0, buffer, index * sizeof(float), sizeof(float));
        for (var index = 0; index < 3; index++)
            Array.Copy(BitConverter.GetBytes((ushort)index), 0, buffer, 36 + index * sizeof(ushort), sizeof(ushort));

        var uri = binary
            ? string.Empty
            : $",\"uri\":\"data:application/octet-stream;base64,{Convert.ToBase64String(buffer)}\"";
        var json = $"{{\"asset\":{{\"version\":\"2.0\"}},\"scene\":0,\"scenes\":[{{\"nodes\":[0]}}],\"nodes\":[{{\"mesh\":0}}],\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":0}},\"indices\":1}}]}}],\"buffers\":[{{\"byteLength\":42{uri}}}],\"bufferViews\":[{{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36}},{{\"buffer\":0,\"byteOffset\":36,\"byteLength\":6}}],\"accessors\":[{{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}},{{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}}]}}";
        if (!binary) return Encoding.UTF8.GetBytes(json);

        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var jsonLength = (jsonBytes.Length + 3) & ~3;
        var binaryLength = (buffer.Length + 3) & ~3;
        var output = new byte[12 + 8 + jsonLength + 8 + binaryLength];
        WriteUInt32(output, 0, 0x46546C67);
        WriteUInt32(output, 4, 2);
        WriteUInt32(output, 8, (uint)output.Length);
        WriteUInt32(output, 12, (uint)jsonLength);
        WriteUInt32(output, 16, 0x4E4F534A);
        Array.Copy(jsonBytes, 0, output, 20, jsonBytes.Length);
        for (var index = 20 + jsonBytes.Length; index < 20 + jsonLength; index++) output[index] = 0x20;
        var binaryChunk = 20 + jsonLength;
        WriteUInt32(output, binaryChunk, (uint)binaryLength);
        WriteUInt32(output, binaryChunk + 4, 0x004E4942);
        Array.Copy(buffer, 0, output, binaryChunk + 8, buffer.Length);
        return output;
    }

    private static void WriteUInt32(byte[] destination, int offset, uint value) =>
        Array.Copy(BitConverter.GetBytes(value), 0, destination, offset, sizeof(uint));

    private static void RunWithDispatcher(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = action();
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void ImportAsksForSourceGameWithOpenPcc()
    {
        using var reader = new MorphFacePackageReader();
        var dialogs = new StubEditorDialogs();
        using var viewModel = CreateMainWindowViewModel(reader, dialogs);
        var packagePath = Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE1 GlobalMorphs.pcc"));

        viewModel.OpenDroppedFileAsync(packagePath).GetAwaiter().GetResult();
        TestAssert.True(viewModel.PackagePath is not null,
            $"The PCC fixture did not establish the package context needed by the import regression: {viewModel.ErrorMessage}");
        viewModel.ImportMorphCommand.Execute(null);

        TestAssert.Equal(1, dialogs.StandaloneGameChoiceCount);
        TestAssert.Equal(0, dialogs.MorphImportFileChoiceCount);
    }

    private static void StandaloneGibbedRejectsWrongGame()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"MFE-WrongGame-{Guid.NewGuid():N}.me2headmorph");
        try
        {
            File.WriteAllBytes(sourcePath, [0]);
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs
            {
                StandaloneGameChoiceResult = MorphFaceGame.LE3
            };
            using var viewModel = CreateMainWindowViewModel(reader, dialogs);

            viewModel.OpenDroppedFileAsync(sourcePath).GetAwaiter().GetResult();

            TestAssert.Equal(1, dialogs.StandaloneGameChoiceCount);
            TestAssert.Equal(0, dialogs.StandaloneNameChoiceCount);
            TestAssert.True(viewModel.ErrorMessage?.Contains("LE2", StringComparison.OrdinalIgnoreCase) == true &&
                            viewModel.ErrorMessage.Contains("LE3", StringComparison.OrdinalIgnoreCase),
                "The mismatched standalone Gibbed import did not identify both the file and selected games.");
            TestAssert.Equal<string?>(null, viewModel.PackagePath);
        }
        finally
        {
            if (File.Exists(sourcePath))
            {
                File.Delete(sourcePath);
            }
        }
    }

    private static void NpcRonArchetypesAreGameLocal()
    {
        var resolver = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault());
        var le2 = resolver.Archetypes(MorphFaceGame.LE2);
        TestAssert.True(le2.Any(value => value.Key == "HMM") &&
                        le2.Any(value => value.Key == "HMF") &&
                        le2.Any(value => value.Key == "SAL"),
            "LE2 NPC RON import did not offer the supported human and Salarian archetypes.");
        TestAssert.True(le2.Any(value => value.Key == "ALN") &&
                        !le2.Any(value => value.Key == "TUF"),
            "ALN static NPC donors should be offered while geometry-ignored TUF donors remain hidden.");
    }

    private static void RonResolverAcceptsStaticAlnDonor()
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"MFE-ALN-Donor-{Guid.NewGuid():N}.pcc");
        var playerPackagePath = Path.Combine(Path.GetTempPath(), $"MFE-ALN-Player-{Guid.NewGuid():N}.pcc");
        try
        {
            File.WriteAllBytes(packagePath, []);
            File.WriteAllBytes(playerPackagePath, []);
            var resolver = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault());
            var selected = resolver.Resolve(
                MorphFaceGame.LE2,
                "ALN",
                [
                    new MorphFaceTemplateCandidate(
                        playerPackagePath,
                        2,
                        "BIOG_Player_Base_ALN.BioFace_Player",
                        "BIOG_ALN_HED_PROMorph_R.ALN_HED_PROBase_MDL",
                        0,
                        TextureCatalogOrigin.BaseGame),
                    new MorphFaceTemplateCandidate(
                        packagePath,
                        1,
                        "BIOG_ALN_HED_PROMorph_R.BioFace_LyingVorcha1",
                        "BIOG_ALN_HED_PROMorph_R.ALN_HED_PROBase_MDL",
                        0,
                        TextureCatalogOrigin.BaseGame)
                ]);

            TestAssert.True(string.Equals(packagePath, selected.PackagePath, StringComparison.OrdinalIgnoreCase),
                "The ALN resolver did not select the exact native static donor package.");
            TestAssert.True(string.Equals(
                    "BIOG_ALN_HED_PROMorph_R.BioFace_LyingVorcha1",
                    selected.FacePath,
                    StringComparison.OrdinalIgnoreCase),
                "The ALN resolver did not select the exact native static donor face.");
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
            if (File.Exists(playerPackagePath)) File.Delete(playerPackagePath);
        }
    }

    private static void EmptyEditorImportsTaggedNpcRon()
    {
        var textureCatalog = new MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureCatalogService(
            new MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureRegistryStore(
                MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureRegistryPaths.CreateDefault()));
        var snapshot = textureCatalog.ReadAsync(MorphFaceGame.LE2).GetAwaiter().GetResult();
        TestAssert.True(snapshot.IsAvailable, "The installed LE2 texture database is needed for NPC import.");
        var donor = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault())
            .Resolve(MorphFaceGame.LE2, "SAL", snapshot.MorphFaceTemplates);
        var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-UiNpc-{Guid.NewGuid():N}.ron");
        try
        {
            new MorphFacePackageContextService().ExportRon(
                donor.PackagePath,
                donor.FacePath,
                ronPath,
                new RonExportProvenance(RonExportProducer.MFE, "0.1.0", MorphFaceGame.LE2, "SAL", false));
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs
            {
                StandaloneGameChoiceResult = MorphFaceGame.LE2,
                StandaloneNameChoiceResult = "MFE_UiNpc"
            };
            using var viewModel = CreateMainWindowViewModel(reader, dialogs, textureCatalog: textureCatalog);
            TestAssert.Equal<string?>(null, viewModel.PackagePath);
            RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(ronPath));
            TestAssert.True(viewModel.ErrorMessage is null, viewModel.ErrorMessage ?? "NPC import failed.");
            TestAssert.True(viewModel.PackageName.Contains("NPC Workspace", StringComparison.Ordinal),
                "The NPC import was not published as a standalone NPC workspace.");
            TestAssert.True(viewModel.Editor?.CanEditMorphFeatures == true,
                "The imported native Salarian did not expose its verified morph controls.");
            TestAssert.Equal(0, dialogs.RonImportDestinationChoiceCount);
            TestAssert.True(viewModel.SaveMorphToPccCommand.CanExecute(null),
                "The NPC workspace cannot export through the accepted PCC workflow.");
        }
        finally
        {
            if (File.Exists(ronPath)) File.Delete(ronPath);
        }
    }

    private static void NpcImportCancellationCommandCancelsActiveRequest()
    {
        using var reader = new MorphFacePackageReader();
        using var viewModel = CreateMainWindowViewModel(reader);
        var cancellation = new CancellationTokenSource();
        var field = typeof(MainWindowViewModel).GetField(
            "_npcImportCancellation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("MainWindowViewModel NPC import cancellation field was not found.");
        field.SetValue(viewModel, cancellation);
        try
        {
            TestAssert.True(viewModel.CanCancelNpcImport && viewModel.CancelNpcImportCommand.CanExecute(null),
                "The active NPC import did not expose its cancellation command.");
            viewModel.CancelNpcImportCommand.Execute(null);
            TestAssert.True(cancellation.IsCancellationRequested,
                "The NPC import cancellation command did not signal the active request.");
            TestAssert.True(!viewModel.CanCancelNpcImport && !viewModel.CancelNpcImportCommand.CanExecute(null),
                "The NPC import cancellation command remained available after cancellation.");
        }
        finally
        {
            field.SetValue(viewModel, null);
            cancellation.Dispose();
        }
    }

    private static void TaggedHumanRonFollowsDestination()
    {
        var textureCatalog = new MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureCatalogService(
            new MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureRegistryStore(
                MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureRegistryPaths.CreateDefault()));
        var snapshot = textureCatalog.ReadAsync(MorphFaceGame.LE2).GetAwaiter().GetResult();
        TestAssert.True(snapshot.IsAvailable, "The installed LE2 texture database is needed for human RON routing.");
        var donor = new RonNpcDonorResolver(MorphFaceProfileRegistry.CreateDefault())
            .Resolve(MorphFaceGame.LE2, "HMM", snapshot.MorphFaceTemplates);
        var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-UiHumanNpc-{Guid.NewGuid():N}.ron");
        var incompatiblePath = Path.Combine(Path.GetTempPath(), $"MFE-UiHumanNpc-Bad-{Guid.NewGuid():N}.ron");
        try
        {
            new MorphFacePackageContextService().ExportRon(
                donor.PackagePath,
                donor.FacePath,
                ronPath,
                new RonExportProvenance(RonExportProducer.MFE, "0.1.0", MorphFaceGame.LE2, "HMM", false));
            foreach (var (choice, expectedWorkspace) in new[]
                     {
                         (new RonImportDestinationChoice(RonImportDestination.NpcFace, "HMM"), "NPC Workspace"),
                         (new RonImportDestinationChoice(RonImportDestination.PlayerWorkspace, null), "Player Workspace")
                     })
            {
                using var reader = new MorphFacePackageReader();
                var dialogs = new StubEditorDialogs
                {
                    StandaloneGameChoiceResult = MorphFaceGame.LE2,
                    StandaloneNameChoiceResult = "MFE_UiHuman",
                    RonImportDestinationChoiceResult = choice
                };
                using var viewModel = CreateMainWindowViewModel(
                    reader, dialogs, textureCatalog: textureCatalog);
                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(ronPath));
                TestAssert.True(viewModel.ErrorMessage is null, viewModel.ErrorMessage ?? "Human RON import failed.");
                TestAssert.True(viewModel.PackageName.Contains(expectedWorkspace, StringComparison.Ordinal),
                    $"The selected {choice.Destination} route did not publish a {expectedWorkspace}.");
                TestAssert.Equal(1, dialogs.RonImportDestinationChoiceCount);
                AssertStandaloneFaceTransitionCapabilities(viewModel, expectedWorkspace);
            }

            // Exercise the actual Player -> NPC -> Player edge on one editor, including
            // dirty-workspace cancellation. This catches stale route flags and command
            // projections that independent fresh-editor imports cannot observe.
            using (var reader = new MorphFacePackageReader())
            {
                var dialogs = new StubEditorDialogs
                {
                    StandaloneGameChoiceResult = MorphFaceGame.LE2,
                    StandaloneNameChoiceResult = "MFE_UiHumanTransition",
                    RonImportDestinationChoiceResult = new RonImportDestinationChoice(
                        RonImportDestination.PlayerWorkspace, null)
                };
                using var viewModel = CreateMainWindowViewModel(
                    reader, dialogs, textureCatalog: textureCatalog);

                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(ronPath));
                TestAssert.True(viewModel.ErrorMessage is null, viewModel.ErrorMessage ?? "Player transition setup failed.");
                AssertStandaloneFaceTransitionCapabilities(viewModel, "Player Workspace");

                var scalar = viewModel.Editor?.Material.Scalars.FirstOrDefault();
                TestAssert.True(scalar is not null, "The transition fixture exposed no material scalar to dirty the workspace.");
                viewModel.Editor!.Material.SetNumericValues(
                    new Dictionary<string, float>
                    {
                        [scalar!.Name] = scalar.Value >= scalar.Maximum
                            ? scalar.Value - MathF.Max(0.01f, (scalar.Value - scalar.Minimum) / 2f)
                            : scalar.Value + MathF.Max(0.01f, (scalar.Maximum - scalar.Value) / 2f)
                    },
                    new Dictionary<string, Vector4>());
                TestAssert.True(viewModel.Editor.IsDirty, "The Player workspace did not become dirty for transition prompting.");

                dialogs.ConfirmUnsavedChangesResult = UnsavedChangesChoice.Cancel;
                var priorEditor = viewModel.Editor;
                var priorPath = viewModel.PackagePath;
                dialogs.RonImportDestinationChoiceResult = new RonImportDestinationChoice(
                    RonImportDestination.NpcFace, "HMM");
                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(ronPath));
                TestAssert.True(ReferenceEquals(priorEditor, viewModel.Editor),
                    "Cancelling the dirty Player -> NPC transition replaced the active editor.");
                TestAssert.Equal(priorPath, viewModel.PackagePath);
                TestAssert.True(dialogs.ConfirmUnsavedChangesCount > 0,
                    "The dirty Player -> NPC transition did not show an unsaved-changes prompt.");

                dialogs.ConfirmUnsavedChangesResult = UnsavedChangesChoice.Discard;
                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(ronPath));
                TestAssert.True(viewModel.ErrorMessage is null, viewModel.ErrorMessage ?? "Player -> NPC transition failed.");
                AssertStandaloneFaceTransitionCapabilities(viewModel, "NPC Workspace");

                priorEditor = viewModel.Editor;
                dialogs.RonImportDestinationChoiceResult = new RonImportDestinationChoice(
                    RonImportDestination.PlayerWorkspace, null);
                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(ronPath));
                TestAssert.True(viewModel.ErrorMessage is null, viewModel.ErrorMessage ?? "NPC -> Player transition failed.");
                TestAssert.True(!ReferenceEquals(priorEditor, viewModel.Editor),
                    "The NPC -> Player transition retained the previous editor instance.");
                AssertStandaloneFaceTransitionCapabilities(viewModel, "Player Workspace");
            }

            using (var reader = new MorphFacePackageReader())
            {
                var dialogs = new StubEditorDialogs
                {
                    StandaloneGameChoiceResult = MorphFaceGame.LE2,
                    RonImportDestinationChoiceResult = null
                };
                using var viewModel = CreateMainWindowViewModel(
                    reader, dialogs, textureCatalog: textureCatalog);
                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(donor.PackagePath));
                TestAssert.True(viewModel.Editor is not null,
                    viewModel.ErrorMessage ?? "The pre-existing PCC workspace did not load.");
                var priorEditor = viewModel.Editor;
                var priorPath = viewModel.PackagePath;
                var priorFace = viewModel.LoadedFacePath;
                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(ronPath));
                TestAssert.Equal(1, dialogs.RonImportDestinationChoiceCount);
                TestAssert.Equal(priorPath, viewModel.PackagePath);
                TestAssert.Equal(priorFace, viewModel.LoadedFacePath);
                TestAssert.True(ReferenceEquals(priorEditor, viewModel.Editor),
                    "Cancelling the HMM destination replaced the previous editor.");
            }

            var authored = TseHeadMorphRon.Read(ronPath);
            var shortenedLods = authored.MorphData.BakedLods.Select(lod => lod.ToArray()).ToArray();
            shortenedLods[0] = shortenedLods[0][..^1];
            TseHeadMorphRon.Write(
                incompatiblePath,
                authored with { MorphData = authored.MorphData with { BakedLods = shortenedLods } },
                new RonExportProvenance(RonExportProducer.MFE, "0.1.0", MorphFaceGame.LE2, "HMM", false));
            using (var reader = new MorphFacePackageReader())
            {
                var dialogs = new StubEditorDialogs
                {
                    StandaloneGameChoiceResult = MorphFaceGame.LE2,
                    StandaloneNameChoiceResult = "MFE_BadNpc",
                    RonImportDestinationChoiceResult =
                        new RonImportDestinationChoice(RonImportDestination.NpcFace, "HMM")
                };
                using var viewModel = CreateMainWindowViewModel(
                    reader, dialogs, textureCatalog: textureCatalog);
                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(donor.PackagePath));
                var priorEditor = viewModel.Editor;
                var priorPath = viewModel.PackagePath;
                var priorFace = viewModel.LoadedFacePath;
                RunWithDispatcher(() => viewModel.OpenDroppedFileAsync(incompatiblePath));
                TestAssert.True(viewModel.ErrorMessage?.Contains("LOD 0", StringComparison.Ordinal) == true,
                    "The incompatible NPC RON did not report its LOD0 mismatch.");
                TestAssert.Equal(priorPath, viewModel.PackagePath);
                TestAssert.Equal(priorFace, viewModel.LoadedFacePath);
                TestAssert.True(ReferenceEquals(priorEditor, viewModel.Editor),
                    "A failed NPC import replaced the previous editor.");
            }
        }
        finally
        {
            if (File.Exists(ronPath)) File.Delete(ronPath);
            if (File.Exists(incompatiblePath)) File.Delete(incompatiblePath);
        }
    }

    private static void AssertStandaloneFaceTransitionCapabilities(
        MainWindowViewModel viewModel,
        string workspaceLabel)
    {
        TestAssert.True(viewModel.PackageName.Contains(workspaceLabel, StringComparison.Ordinal),
            $"The route did not publish the expected {workspaceLabel} workspace.");
        TestAssert.True(viewModel.Editor is { CanEditMorphFeatures: true, CanEditBones: true },
            $"The {workspaceLabel} route did not project independent morph and bone capabilities.");
        TestAssert.True(viewModel.SaveMorphToPccCommand.CanExecute(null),
            $"The {workspaceLabel} route did not expose PCC save.");
        TestAssert.True(viewModel.ExportMorphRonCommand.CanExecute(null),
            $"The {workspaceLabel} route did not expose RON export.");
        TestAssert.True(viewModel.ExportMaterialsCommand.CanExecute(null) &&
                        viewModel.ImportMaterialsCommand.CanExecute(null),
            $"The {workspaceLabel} route did not expose face material interchange.");
        TestAssert.True(!viewModel.SaveCommand.CanExecute(null),
            $"The non-committable {workspaceLabel} route inherited package Save.");
        TestAssert.True(!viewModel.CanCommitFace && !viewModel.CommitCommand.CanExecute(null),
            $"The single-head {workspaceLabel} route exposed ordinary-package Commit.");
        TestAssert.True(!viewModel.AssignMorphToActorCommand.CanExecute(null) &&
                        !viewModel.AssignMaterialsToActorCommand.CanExecute(null),
            $"The standalone {workspaceLabel} route inherited actor-assignment commands.");
    }

    private static void StandaloneFixedBakeMaterialClipboardCommands()
    {
        using var reader = new MorphFacePackageReader();
        var packagePath = Path.GetFullPath(Path.Combine("tests", "Global Morphs", "LE1 GlobalMorphs.pcc"));
        var clipboard = new StubClipboard(MorphFaceClipboardKind.Material);
        using var viewModel = CreateMainWindowViewModel(reader, clipboard: clipboard);
        var selectedFace = new BioMorphFaceListItem(
            1,
            "Fixture.Face",
            "Face",
            ProfileKey: "le1-human-male");

        var workspaceField = typeof(MainWindowViewModel).GetField(
            "_packageWorkspace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("MainWindowViewModel._packageWorkspace was not found.");
        var detachedWorkspace = new MorphFacePackageWorkspace(packagePath, canCommit: false);
        workspaceField.SetValue(viewModel, detachedWorkspace);
        viewModel.SelectedFace = selectedFace;

        TestAssert.True(viewModel.IsMaterialFileWorkspace &&
                        viewModel.ExportMaterialsCommand.CanExecute(null) &&
                        viewModel.ImportMaterialsCommand.CanExecute(null),
            "A selected PCC face did not expose material file actions before it was loaded.");

        var standaloneGameField = typeof(MainWindowViewModel).GetField(
            "_standaloneGame",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("MainWindowViewModel._standaloneGame was not found.");

        TestAssert.True(!viewModel.CopyMaterialDataCommand.CanExecute(null) &&
                        !viewModel.PasteMaterialDataCommand.CanExecute(null),
            "A non-committable non-standalone workspace exposed material clipboard commands.");

        standaloneGameField.SetValue(viewModel, MorphFaceGame.LE1);

        var fixedBakePathsField = typeof(MainWindowViewModel).GetField(
            "_fixedBakeFacePaths",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("MainWindowViewModel._fixedBakeFacePaths was not found.");
        var fixedBakePaths = (HashSet<string>?)fixedBakePathsField.GetValue(viewModel)
                             ?? throw new Exception("MainWindowViewModel fixed-bake path set was null.");

        TestAssert.True(!viewModel.CopyMaterialDataCommand.CanExecute(null) &&
                        !viewModel.PasteMaterialDataCommand.CanExecute(null),
            "A non-fixed-bake detached workspace exposed material clipboard commands.");

        fixedBakePaths.Add(selectedFace.InstancedPath);
        viewModel.RefreshClipboardCommandAvailability();

        TestAssert.True(viewModel.CopyMaterialDataCommand.CanExecute(null),
            "A standalone fixed-bake player face did not expose Copy Material Data.");
        TestAssert.True(viewModel.PasteMaterialDataCommand.CanExecute(null),
            "A standalone fixed-bake player face did not expose Paste Material Data.");
        TestAssert.True(!viewModel.PasteMorphDataCommand.CanExecute(null),
            "Standalone fixed-bake material support incorrectly enabled morph paste.");
    }

    private static void FemaleTurianProfileIsMaterialOnly()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateEditor(
            reader,
            new FemaleTurianFeatureMetadataCatalog(),
            "le2-female-turian",
            ignoresAuthoredGeometry: true);

        TestAssert.Equal(0, editor.Features.Count);
        TestAssert.Equal(0, editor.Bones.Count);
        TestAssert.True(!editor.CanEdit,
            "Female Turian test session did not reproduce its geometry-edit block.");
        TestAssert.True(!editor.CanEditAttachments,
            "Female Turian material-only mode exposed authored hair or accessory references.");
        TestAssert.True(!editor.AllowsMorphRandomisation && !editor.RandomiseMorphs,
            "Female Turian exposed inherited TUR geometry randomisation.");
        TestAssert.True(editor.AllowsMaterialRandomisation && editor.AllowsCursedRandomisation,
            "Female Turian material-only mode disabled its randomisation controls.");
        editor.RandomiseMaterials = true;
        TestAssert.True(editor.RandomiseMaterials && editor.CanRandomise,
            "Female Turian material randomisation was disabled with geometry randomisation.");
        editor.CursedMode = true;
        TestAssert.True(editor.CursedMode && editor.RandomiseMaterials && !editor.RandomiseMorphs && editor.CanRandomise,
            "Female Turian Cursed mode did not remain available as material-only randomisation.");
        var textures = TextureCatalogProfiles.For(MorphFaceProfileRegistry.CreateDefault().Profiles.Single(value =>
            value.Key == "le2-female-turian"));
        TestAssert.True(textures.PreferredPathFragments.Contains("TUF_HED") &&
                        textures.SharedPathFragments.Contains("TUF_EYE"),
            "Female Turian texture discovery did not receive TUF path signals.");
    }

    private static void VorchaProfileIsMaterialAndBoneOnly()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateEditor(reader, new VorchaFeatureMetadataCatalog(), "le2-vorcha");

        TestAssert.Equal(0, editor.Features.Count);
        TestAssert.True(editor.Bones.Count > 0, "Vorcha bone controls were hidden with morph controls.");
        TestAssert.True(!editor.AllowsMorphRandomisation && !editor.RandomiseMorphs,
            "Vorcha exposed global morph randomisation.");
        editor.RandomiseMorphs = true;
        TestAssert.True(!editor.RandomiseMorphs,
            "Vorcha accepted an attempted programmatic morph-randomisation enable.");
        editor.RandomiseMaterials = true;
        TestAssert.True(editor.RandomiseMaterials,
            "Vorcha material randomisation was disabled with morph randomisation.");
    }

    private static void ErrorBannerCanBeDismissed()
    {
        using var reader = new MorphFacePackageReader();
        var sceneFactory = new HeadPreviewSceneFactory();
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var targets = new MorphTargetCatalog();
        var writer = new MorphFacePackageWriter();
        var context = new MorphFacePackageContextService();
        using var viewModel = new MainWindowViewModel(
            new StubEditorDialogs(),
            new MorphFaceCatalogService(profiles),
            new MorphFacePreviewLoadService(sceneFactory, targets, profiles, reader),
            sceneFactory,
            new StubColorDialog(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            writer,
            context,
            new MorphFaceConversionService(
                profiles, targets, context, TestFixtures.CreateMissingTextureCatalogService()),
            new MorphFaceInterchangeService(),
            new StubClipboard());

        viewModel.SetPreviewError("Preview failed.");
        TestAssert.True(viewModel.HasError, "The test error did not show the banner.");
        viewModel.DismissErrorCommand.Execute(null);
        TestAssert.True(!viewModel.HasError, "Dismissing the error left the banner visible.");
        TestAssert.Equal<string?>(null, viewModel.ErrorMessage);
    }

    private static void TextureRegistrySettingsCommandOpensDialog()
    {
        using var reader = new MorphFacePackageReader();
        var dialogs = new StubEditorDialogs();
        using var viewModel = CreateMainWindowViewModel(reader, dialogs);

        viewModel.TextureRegistrySettingsCommand.Execute(null);

        TestAssert.True(dialogs.TextureRegistrySettingsWasShown,
            "The Texture Registry Settings command did not open the settings dialog.");
    }

    private static void ActorChooserFiltersAndScopesEligibility()
    {
        var morphReady = ActorChoiceFixture(
            10, "NormandyGarrus", "BioPawn_10", canMorph: true, canMaterials: false);
        var materialReady = ActorChoiceFixture(
            20, "CitadelKeeper", "BioPawn_20", canMorph: false, canMaterials: true);
        var mismatchedPlaced = ActorChoiceFixture(
            5, "AardvarkMismatch", "BioPawn_5", canMorph: true, canMaterials: true) with
        {
            HeadMeshPath = "ASA_HED_PROBase_MDL",
            HeadMeshProfileKey = "le2-asari"
        };
        var matchingSpawn = ActorChoiceFixture(
            30, "ZuluMatchingSpawn", "SpawnType_30", canMorph: true, canMaterials: true) with
        {
            TargetKind = ActorAssignmentTargetKind.SpawnTemplate
        };
        var mismatchedSpawn = ActorChoiceFixture(
            40, "AlphaMismatchedSpawn", "SpawnType_40", canMorph: true, canMaterials: true) with
        {
            TargetKind = ActorAssignmentTargetKind.SpawnTemplate,
            HeadMeshPath = "ASA_HED_PROBase_MDL",
            HeadMeshProfileKey = "le2-asari"
        };
        var inventory = new ActorAssignmentInventory(
            MorphFaceGame.LE2, 1, "SelectedFace", "le2-human-male",
            [mismatchedPlaced, mismatchedSpawn, morphReady, matchingSpawn, materialReady]);

        var morphChooser = new ActorAssignmentChooserViewModel(inventory, ActorAssignmentMode.Morph);
        TestAssert.Equal(morphReady.UIndex, morphChooser.SelectedChoice!.Candidate.UIndex);
        TestAssert.True(morphChooser.CanConfirm, "Morph chooser did not select its first eligible actor.");
        TestAssert.True(!morphChooser.SelectedChoice.Details.Contains("SAFE MATERIAL TARGETS", StringComparison.Ordinal),
            "Morph chooser retained unrelated material-debug sections.");
        var sorted = morphChooser.FilteredChoices.Cast<ActorAssignmentChoice>().ToArray();
        TestAssert.True(Array.IndexOf(sorted, sorted.Single(value => value.Candidate == morphReady)) <
                        Array.IndexOf(sorted, sorted.Single(value => value.Candidate == mismatchedPlaced)) &&
                        Array.IndexOf(sorted, sorted.Single(value => value.Candidate == matchingSpawn)) <
                        Array.IndexOf(sorted, sorted.Single(value => value.Candidate == mismatchedSpawn)),
            "Exact head-profile matches were not promoted within both actor groups.");
        morphChooser.SearchText = "CitadelKeeper";
        TestAssert.Equal(1, morphChooser.FilteredChoices.Cast<ActorAssignmentChoice>().Count());
        TestAssert.True(!morphChooser.CanConfirm,
            "Operation-specific morph confirmation remained enabled for a material-only candidate.");

        var materialChooser = new ActorAssignmentChooserViewModel(inventory, ActorAssignmentMode.Materials);
        TestAssert.Equal(materialReady.UIndex, materialChooser.SelectedChoice!.Candidate.UIndex);
        TestAssert.True(materialChooser.CanConfirm,
            "Material chooser did not select its first actor with a safe MIC target.");
        var choice = materialChooser.SelectedChoice;
        var normalizedDetails = choice.Details.Replace("\r\n", "\n", StringComparison.Ordinal);
        TestAssert.True(!choice.TechnicalIdentity.Contains("TheWorld.PersistentLevel", StringComparison.Ordinal) &&
                        !normalizedDetails.Contains("TheWorld.PersistentLevel", StringComparison.Ordinal),
            "Chooser retained the redundant persistent-level prefix.");
        TestAssert.True(!normalizedDetails.Contains("IDENTITY EVIDENCE", StringComparison.Ordinal) &&
                        !normalizedDetails.Contains("EXACT MORPH OWNER", StringComparison.Ordinal) &&
                        !normalizedDetails.Contains("SkeletalMeshComponent_937", StringComparison.Ordinal) &&
                        !normalizedDetails.Contains("MASTER_FACE_MAT_USER", StringComparison.Ordinal) &&
                        normalizedDetails.Contains("Head\nSlot 0 · Skin\nBioMaterialInstanceConstant_2540\n" +
                                                   "HMF_HED_PRO_Face_Mat_1a → HMF_HED_PRO_MASTER_FACE_MAT",
                            StringComparison.Ordinal),
            $"Chooser details did not reduce identity noise and material chains to authoring shorthand.\n{normalizedDetails}");
    }

    private static ActorAssignmentCandidate ActorChoiceFixture(
        int uIndex,
        string tag,
        string objectName,
        bool canMorph,
        bool canMaterials)
    {
        var material = new ActorAssignmentMaterialTarget(
            30, "HMM_HED_PRO_Face_Mat", "HMM_HED_PRO_Face_Mat",
            ActorComponentRole.Head, $"TheWorld.PersistentLevel.{objectName}.SkeletalMeshComponent_937",
            0, HeadMaterialFamily.Skin, "le2-human-male",
            [
                new ActorMaterialChainEntry(30, "MaterialInstanceConstant",
                    $"TheWorld.PersistentLevel.{objectName}.BioMaterialInstanceConstant_2540", true, true),
                new ActorMaterialChainEntry(-1, "MaterialInstanceConstant",
                    "BIOG_HMF_HED_PROMorph_R.Average.HMF_HED_PRO_Face_Mat_1a", false, true),
                new ActorMaterialChainEntry(-2, "RvrEffectsMaterialUser",
                    "EffectsMaterials.Users.HMF_HED_PRO_MASTER_FACE_MAT_USER", false, true),
                new ActorMaterialChainEntry(-3, "Material",
                    "BIOG_Humanoid_MASTER_MTR_R.Human.HMF_HED_PRO_MASTER_FACE_MAT", false, true)
            ], false);
        return new ActorAssignmentCandidate(
            uIndex, "BioPawn", objectName, $"TheWorld.PersistentLevel.{objectName}",
            ActorAssignmentTargetKind.PlacedActor, tag,
            [new ActorIdentityEvidence(ActorIdentityEvidenceKind.LocalTag, "Local tag", tag, $"TheWorld.PersistentLevel.{objectName}")],
            $"{tag}\n{objectName}\nBioPawn\n#{uIndex}",
            "le2-human-male", "HMM_HED_PROBase_MDL", "le2-human-male",
            canMorph, canMorph ? null : "No safe morph owner.",
            canMorph ? new ActorAssignmentMorphTarget(uIndex, "BioPawn", $"TheWorld.PersistentLevel.{objectName}", "MorphHead", 0, null, null) : null,
            canMaterials, canMaterials ? null : "No safe local MICs.",
            [], canMaterials ? [material] : [], [], [], []);
    }

    private static void ComboModelsDisplayLabels()
    {
        var identity = new AssetIdentity("fixture.pcc", "Package.Texture", 1, "Texture2D");
        TestAssert.Equal("Package.Texture", new PackageAssetListItem(identity).ToString());
        TestAssert.Equal("None", new HairMeshOption("None", null).ToString());
        TestAssert.Equal("None", new MaterialTextureOption(null).ToString());
    }

    private static void MorphClipboardCodecRoundTrips()
    {
        var texture = new AssetIdentity("fixture.pcc", "Package.Texture", 7, "Texture2D");
        var morph = MorphFaceClipboardPayload.Morph(
            "le3-human-male",
            "Package.Face",
            new MorphFaceMorphData(
                [new MorphFeatureValue("nose_Wide", 0.75f)],
                [new BoneTranslation("Jaw", new Vector3(1, 2, 3))],
                [[new Vector3(4, 5, 6)]]));
        var morphRoundTrip = MorphFaceClipboardCodec.Deserialize(
            MorphFaceClipboardCodec.Serialize(morph),
            MorphFaceClipboardKind.Morph);
        TestAssert.Equal("le3-human-male", morphRoundTrip.ProfileKey);
        TestAssert.Near(0.75f, morphRoundTrip.MorphData!.MorphFeatures[0].Offset, 0);
        TestAssert.Near(new Vector3(1, 2, 3), morphRoundTrip.MorphData.FinalSkeleton[0].Translation, 0);
        TestAssert.Near(new Vector3(4, 5, 6), morphRoundTrip.MorphData.BakedLods[0][0], 0);

        var material = MorphFaceClipboardPayload.Material(
            "le3-human-male",
            "Package.Face",
            new MorphFaceMaterialData(
                [new ScalarMaterialOverride("Roughness", 0.25f)],
                [new VectorMaterialOverride("SkinTone", new Vector4(1, 0.5f, 0.25f, 1))],
                [new TextureMaterialOverride("Diffuse", texture)]));
        var materialRoundTrip = MorphFaceClipboardCodec.Deserialize(
            MorphFaceClipboardCodec.Serialize(material),
            MorphFaceClipboardKind.Material);
        TestAssert.Near(0.25f, materialRoundTrip.MaterialData!.Scalars[0].Value, 0);
        TestAssert.Equal(texture, materialRoundTrip.MaterialData.Textures[0].TextureReference);

        try
        {
            _ = MorphFaceClipboardCodec.Deserialize(
                MorphFaceClipboardCodec.Serialize(material),
                MorphFaceClipboardKind.Morph);
            throw new Exception("A material payload was accepted as morph data.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void PastedDataRemainsLiveAndDirty()
    {
        using var reader = new MorphFacePackageReader();
        using (var morphEditor = CreateEditor(reader))
        {
            morphEditor.ApplyMorphData(new MorphFaceMorphData(
                [new MorphFeatureValue("Target", 0.5f)],
                [new BoneTranslation("root", Vector3.Zero)],
                [[Vector3.Zero, Vector3.UnitX, Vector3.UnitY]]));
            TestAssert.True(morphEditor.IsDirty,
                "Pasted morph data was treated as an already-saved package edit.");
            TestAssert.Near(0.5f, morphEditor.CreateDraft().GetFeatureOffset("Target"), 0);
        }

        using (var materialEditor = CreateEditor(reader))
        {
            materialEditor.ApplyMaterialDataAsync(new MorphFaceMaterialData(
                    [new ScalarMaterialOverride("PastedScalar", 0.75f)],
                    [new VectorMaterialOverride("PastedVector", Vector4.One)],
                    []))
                .GetAwaiter().GetResult();
            TestAssert.True(materialEditor.IsDirty,
                "Pasted material data was treated as an already-saved package edit.");
            TestAssert.Near(0.75f,
                materialEditor.CreateDraft().MaterialOverrides.Scalars.Single().Value,
                0);
        }

    }

    private static void FaceEditorCategoryRestoresByKey()
    {
        using var reader = new MorphFacePackageReader();
        using var first = CreateEditor(reader);
        var expected = first.Categories.Skip(1).First();
        first.SelectCategory(expected.Key);
        TestAssert.Equal(expected, first.SelectedCategory);

        using var second = CreateEditor(reader);
        second.SelectCategory(first.SelectedCategory?.Key);
        TestAssert.Equal(expected.Key, second.SelectedCategory?.Key);
    }

    private static void RandomisationCommandsHonorScopes()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        TestAssert.Equal(50, editor.MorphRandomisationStrength);
        TestAssert.Equal(50, editor.MaterialRandomisationStrength);
        TestAssert.True(editor.RandomiseMorphs && !editor.RandomiseMaterials,
            "The safe backwards-compatible randomisation defaults changed.");
        editor.RandomisationStrength = 0;
        TestAssert.True(editor.RandomiseCommand.CanExecute(null),
            "Global randomisation was disabled for an editable profile with donors.");

        var bridge = editor.Categories.Single(value => value.Key == "nose")
            .SliderGroups.Single(value => value.Key == "bridge");
        TestAssert.True(bridge.RandomiseCommand is not null,
            "A morph-containing subcategory exposed no randomisation command.");
        bridge.RandomiseCommand!.Execute(null);
        TestAssert.Near(0.6f, editor.CreateDraft().GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.Near(0, editor.CreateDraft().GetFeatureOffset("eyes_Big"), 0);
        TestAssert.True(editor.IsDirty, "Subcategory randomisation did not dirty the morph document.");

        editor.UndoCommand.Execute(null);
        TestAssert.Near(0, editor.CreateDraft().GetFeatureOffset("nose_BridgeIn"), 0);
        editor.RandomiseCommand.Execute(null);
        TestAssert.Near(0.6f, editor.CreateDraft().GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.Near(0.7f, editor.CreateDraft().GetFeatureOffset("eyes_Big"), 0);

        editor.RandomisationStrength = 100;
        TestAssert.Equal(100, editor.RandomisationStrength);
        editor.RandomisationStrength = -1;
        TestAssert.Equal(0, editor.RandomisationStrength);
    }

    private static void LockedPlayerHairMorphGroupCannotRandomise()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader, includePlayerHairMorph: true);
        var hairGroup = editor.Categories.SelectMany(category => category.SliderGroups)
            .Single(group => group.MorphFeatures.Any(feature => feature.Name == "Afro"));
        TestAssert.True(hairGroup.RandomiseCommand?.CanExecute(null) == true,
            "An unlocked Player hair morph group was not eligible for randomisation.");

        editor.HairMesh.IsRandomisationLocked = true;
        TestAssert.True(hairGroup.RandomiseCommand?.CanExecute(null) == false,
            "A locked Player hair morph group remained enabled without another eligible value.");
        TestAssert.True(editor.RandomiseCommand.CanExecute(null),
            "Locking Player hair disabled unrelated global morph randomisation.");

        editor.CursedMode = true;
        TestAssert.True(hairGroup.RandomiseCommand?.CanExecute(null) == false,
            "Cursed mode enabled a locked, hair-only targeted roll.");
    }

    private static void PlayerCharacterMorphScopeKeepsExceptions()
    {
        foreach (var name in new[] { "anderson", "joker", "kaiden", "jacob", "shepard" })
            TestAssert.True(!FaceEditorViewModel.IsPlayerRandomisableMorph(
                    name, "facial-structure", "character", PlayerHairSex.Male),
                $"HMM Character target '{name}' still enters Player randomisation.");
        TestAssert.True(FaceEditorViewModel.IsPlayerRandomisableMorph(
                "eastwood", "facial-structure", "character", PlayerHairSex.Male),
            "HMM Eastwood was excluded from Player randomisation.");

        foreach (var name in new[]
                 { "ashley", "jack", "kasumi", "miranda", "race_ashley", "race_liara" })
            TestAssert.True(!FaceEditorViewModel.IsPlayerRandomisableMorph(
                    name, "facial-structure", "character", PlayerHairSex.Female),
                $"HMF Character target '{name}' still enters Player randomisation.");
        foreach (var name in new[] { "iconic", "race_iconic" })
            TestAssert.True(FaceEditorViewModel.IsPlayerRandomisableMorph(
                    name, "facial-structure", "character", PlayerHairSex.Female),
                $"HMF Iconic Shepard target '{name}' was excluded from Player randomisation.");
        TestAssert.True(FaceEditorViewModel.IsPlayerRandomisableMorph(
                "Afro", "facial-structure", "hair", PlayerHairSex.Male),
            "The Character exclusion also removed the separate hair morph group.");

        var bothProposed = FaceEditorViewModel.ResolveExclusivePlayerIconicMorphs(
            new Dictionary<string, float> { ["iconic"] = 1f, ["race_iconic"] = 1f },
            0f, 0f, 41);
        TestAssert.True((bothProposed["iconic"] > 0f) !=
                        (bothProposed["race_iconic"] > 0f),
            "The two additive Iconic Shepard morphs were enabled together.");
        var oneLockedIn = FaceEditorViewModel.ResolveExclusivePlayerIconicMorphs(
            new Dictionary<string, float> { ["iconic"] = 1f }, 0f, 1f, 41);
        TestAssert.True(oneLockedIn["iconic"] == 0f,
            "An Iconic roll doubled a previously selected race_iconic value.");
    }

    private static void PlayerColourRandomisationUses2Da()
    {
        const int seed = 781;
        var paletteRequest = new Player2DaRandomisationRequest(
            Player2DaSex.Male,
            new HashSet<Player2DaPalette> { Player2DaPalette.SkinTone },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SkinTone" },
            new Dictionary<string, Player2DaValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["SkinTone"] = Player2DaValue.FromVector(Vector4.One)
            });
        var palette = Player2DaRandomisationPolicy.Roll(paletteRequest,
            new Random(unchecked(seed ^ 0x32444150))).Values["SkinTone"].Vector;

        using var reader = new MorphFacePackageReader();
        using (var editor = CreateRandomisationEditor(reader,
                   randomSeedFactory: () => seed, includePlayerHairMorph: true))
        {
            editor.RandomiseMorphs = false;
            editor.RandomiseMaterials = true;
            editor.RandomiseCommand.Execute(null);
            var actual = editor.Material.Vectors.Single(value => value.Name == "SkinTone").Value;
            TestAssert.True(actual == palette,
                "Normal Player material randomisation did not use the selected 2DA skin row.");
        }

        using (var editor = CreateRandomisationEditor(reader,
                   randomSeedFactory: () => seed, includePlayerHairMorph: true))
        {
            editor.RandomiseMorphs = false;
            editor.RandomiseMaterials = true;
            editor.CursedMode = true;
            editor.MaterialRandomisationStrength = 100;
            editor.RandomiseCommand.Execute(null);
            var variation = CursedMorphRandomiser.CreateExtrasProposal(
                [], new Dictionary<string, float>(),
                new Dictionary<string, Vector4> { ["SkinTone"] = palette },
                100, unchecked(seed ^ 0x32444143)).VectorValues["SkinTone"];
            var expected = new Vector4(variation.X, variation.Y, variation.Z, palette.W);
            var actual = editor.Material.Vectors.Single(value => value.Name == "SkinTone").Value;
            TestAssert.True(actual == expected,
                "Cursed Player colour lost either its 2DA base row or its variation.");
        }
    }

    private static void PlayerHairAndAdditionColoursRandomiseAcrossGames()
    {
        using var reader = new MorphFacePackageReader();
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            using var editor = CreateRandomisationEditor(reader,
                randomSeedFactory: () => 123,
                includePlayerHairColours: true,
                playerGame: game);
            editor.RandomiseMorphs = false;
            editor.RandomiseMaterials = true;
            var hairGroup = editor.Categories.SelectMany(category => category.ColourGroups)
                .Single(group => group.Values.Any(value => value.Name == "HED_Hair_Colour_Vector"));
            hairGroup.RandomiseCommand!.Execute(null);
            var additionGroup = editor.Categories.SelectMany(category => category.ColourGroups)
                .Single(group => group.Values.Any(value => value.Name == "HED_Addn_Colour_Vector"));
            additionGroup.RandomiseCommand!.Execute(null);

            var hair = editor.Material.Vectors.Single(value => value.Name == "HED_Hair_Colour_Vector").Value;
            var addition = editor.Material.Vectors.Single(value => value.Name == "HED_Addn_Colour_Vector").Value;
            TestAssert.True(hair != new Vector4(0.13f, 0.27f, 0.39f, 0.7f),
                $"Player HMM hair colour did not randomise for {game}.");
            TestAssert.True(addition != new Vector4(0.17f, 0.31f, 0.43f, 0.6f),
                $"Player HMM addition colour did not randomise for {game}.");
        }
    }

    private static void FemaleMeshRollClearsUnlockedHairMorphs()
    {
        var proposed = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["hairShape"] = 1f,
            ["otherFeature"] = 0.6f
        };
        var unlocked = FaceEditorViewModel.ClearFemaleHairMorphsForMesh(
            proposed, ["hairShape"]);
        TestAssert.True(unlocked["hairShape"] == 0f && unlocked["otherFeature"] == 0.6f,
            "An HMF hair mesh retained an unlocked hair morph or changed an unrelated feature.");
        var locked = FaceEditorViewModel.ClearFemaleHairMorphsForMesh(proposed, []);
        TestAssert.True(locked["hairShape"] == 1f,
            "An HMF hair mesh overrode the locked hair-morph subcategory.");
    }

    private static void MaterialRandomisationObeysToggle()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        editor.RandomiseMorphs = false;
        editor.RandomiseMaterials = true;
        editor.MaterialRandomisationStrength = 0;

        editor.RandomiseCommand.Execute(null);

        var randomised = editor.CreateDraft();
        TestAssert.Near(0, randomised.GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.Near(4, randomised.MaterialOverrides.Scalars
            .Single(value => value.Name == "HED_Norm_Blend").Value, 0);
        TestAssert.Equal(new Vector4(0.4f, 0.2f, 0.1f, 1), randomised.MaterialOverrides.Vectors
            .Single(value => value.Name == "SkinTone").Value);

        editor.UndoCommand.Execute(null);
        var restored = editor.CreateDraft();
        TestAssert.True(restored.MaterialOverrides.Scalars.Count == 0 &&
                        restored.MaterialOverrides.Vectors.Count == 0,
            "Material randomisation was not restored by one Undo.");
    }

    private static void SetToDefaultsRestoresStockState()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        editor.RandomiseMorphs = true;
        editor.RandomiseMaterials = true;
        editor.MorphRandomisationStrength = 0;
        editor.MaterialRandomisationStrength = 0;
        editor.RandomiseCommand.Execute(null);
        var editedBone = editor.Bones.Single(value =>
            value.BoneName == "nose_tip" && value.Axis == 0);
        var stockBoneValue = editedBone.Value;
        editedBone.Value = stockBoneValue + 1.25f;
        var randomised = editor.CreateDraft();

        editor.SetToDefaultsCommand.Execute(null);

        var defaults = editor.CreateDraft();
        TestAssert.True(defaults.MorphFeatures.All(value => value.Offset == 0),
            "Set to Defaults retained a non-zero morph slider.");
        TestAssert.True(defaults.MaterialOverrides.Scalars.Count == 0 &&
                        defaults.MaterialOverrides.Vectors.Count == 0 &&
                        defaults.MaterialOverrides.Textures.Count == 0,
            "Set to Defaults retained authored material overrides.");
        TestAssert.Near(stockBoneValue, defaults.FinalSkeleton
            .Single(value => value.BoneName == "nose_tip").Translation.X, 0);
        TestAssert.Near(2, editor.Material.Scalars.Single(value => value.Name == "HED_Norm_Blend").Value, 0);
        TestAssert.Equal(Vector4.One, editor.Material.Vectors.Single(value => value.Name == "SkinTone").Value);

        editor.UndoCommand.Execute(null);
        TestAssert.True(editor.CreateDraft().MorphFeatures.SequenceEqual(randomised.MorphFeatures),
            "Undo did not restore the randomised morph values.");
        TestAssert.True(editor.CreateDraft().MaterialOverrides.Scalars.SequenceEqual(randomised.MaterialOverrides.Scalars) &&
                        editor.CreateDraft().MaterialOverrides.Vectors.SequenceEqual(randomised.MaterialOverrides.Vectors),
            "Undo did not restore the randomised material values.");
        TestAssert.Near(stockBoneValue + 1.25f, editor.CreateDraft().FinalSkeleton
            .Single(value => value.BoneName == "nose_tip").Translation.X, 0);

        editor.RedoCommand.Execute(null);
        TestAssert.True(editor.CreateDraft().MorphFeatures.All(value => value.Offset == 0) &&
                        editor.CreateDraft().MaterialOverrides.Scalars.Count == 0 &&
                        editor.CreateDraft().MaterialOverrides.Vectors.Count == 0,
            "Redo did not restore the stock state.");
        TestAssert.Near(stockBoneValue, editor.CreateDraft().FinalSkeleton
            .Single(value => value.BoneName == "nose_tip").Translation.X, 0);
    }

    private static void GlobalRandomisationUsesSeparateDonors()
    {
        using var reader = new MorphFacePackageReader();
        var seeds = new Queue<int>([101, 202]);
        using var editor = CreateRandomisationEditor(
            reader,
            randomSeedFactory: () => seeds.Dequeue(),
            includeSecondDonor: true);
        editor.RandomiseMorphs = true;
        editor.RandomiseMaterials = true;
        editor.MorphRandomisationStrength = 0;
        editor.MaterialRandomisationStrength = 0;

        editor.RandomiseCommand.Execute(null);

        TestAssert.Equal(0, seeds.Count);
        var draft = editor.CreateDraft();
        var morphDonorValues = new[] { draft.GetFeatureOffset("nose_BridgeIn"), draft.GetFeatureOffset("eyes_Big") };
        var materialScalar = draft.MaterialOverrides.Scalars.Single(value => value.Name == "HED_Norm_Blend").Value;
        TestAssert.True(
            (morphDonorValues.SequenceEqual([0.6f, 0.7f]) && materialScalar == 5) ||
            (morphDonorValues.SequenceEqual([0.2f, 0.3f]) && materialScalar == 4),
            "Morphs and materials were not sourced from two different heads.");
    }

    private static void SubcategoryInclusionsFilterGlobalScope()
    {
        using var reader = new MorphFacePackageReader();
        var inclusionState = new RandomisationInclusionState();
        using (var first = CreateRandomisationEditor(reader, randomisationInclusionState: inclusionState))
        {
            TestAssert.True(first.Categories.SelectMany(value => value.SliderGroups)
                    .Where(value => value.HasRandomisableValues)
                    .All(value => value.Inclusion?.IsIncluded == true),
                "A morph/scalar randomisation group was not included by default.");
            TestAssert.True(first.Categories.SelectMany(value => value.ColourGroups)
                    .Where(value => value.HasRandomisableValues)
                    .All(value => value.Inclusion?.IsIncluded == true),
                "A colour randomisation group was not included by default.");
            var bridge = first.Categories.Single(value => value.Key == "nose")
                .SliderGroups.Single(value => value.Key == "bridge");
            TestAssert.True(bridge.Inclusion?.IsIncluded == true,
                "A randomisable subcategory was not included by default.");
            TestAssert.True(bridge.Inclusion!.IsLocked == false,
                "A randomisable subcategory was locked by default.");
            bridge.Inclusion.IsLocked = true;
            TestAssert.True(bridge.Inclusion.IsIncluded == false,
                "Locking a subcategory did not exclude it from global randomisation.");
        }

        using var second = CreateRandomisationEditor(reader, randomisationInclusionState: inclusionState);
        second.RandomisationStrength = 0;
        var restoredBridge = second.Categories.Single(value => value.Key == "nose")
            .SliderGroups.Single(value => value.Key == "bridge");
        TestAssert.True(restoredBridge.Inclusion?.IsIncluded == false,
            "The subcategory exclusion was lost when the editor was recreated.");
        TestAssert.True(restoredBridge.Inclusion?.IsLocked == true,
            "The subcategory padlock was lost when the editor was recreated.");

        second.RandomiseCommand.Execute(null);
        TestAssert.Near(0, second.CreateDraft().GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.Near(0.7f, second.CreateDraft().GetFeatureOffset("eyes_Big"), 0);

        restoredBridge.RandomiseCommand!.Execute(null);
        TestAssert.Near(0, second.CreateDraft().GetFeatureOffset("nose_BridgeIn"), 0);
        restoredBridge.Inclusion!.IsLocked = false;
        restoredBridge.RandomiseCommand.Execute(null);
        TestAssert.Near(0.6f, second.CreateDraft().GetFeatureOffset("nose_BridgeIn"), 0);
    }

    private static void CursedModeRandomisesOneUndoStep()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        editor.CursedMode = true;
        editor.RandomisationStrength = 100;

        editor.RandomiseCommand.Execute(null);

        var cursed = editor.CreateDraft();
        TestAssert.True(cursed.GetFeatureOffset("nose_BridgeIn") != 0,
            "Cursed mode retained the zero morph mask.");
        TestAssert.True(cursed.FinalSkeleton.Single(value => value.BoneName == "nose_tip").Translation !=
                        new Vector3(2, 4, 6),
            "Cursed mode did not fuzz a facial bone.");
        TestAssert.True(cursed.MaterialOverrides.Scalars.Single(value => value.Name == "HED_Norm_Blend").Value != 2,
            "Cursed mode did not randomise a material scalar.");
        TestAssert.True(cursed.MaterialOverrides.Vectors.Single(value => value.Name == "SkinTone").Value != Vector4.One,
            "Cursed mode did not randomise a material vector.");
        var composedNoseY = 4 + (cursed.GetFeatureOffset("nose_BridgeIn") * 100);
        var fuzzedNoseY = cursed.FinalSkeleton.Single(value => value.BoneName == "nose_tip").Translation.Y;
        var minimumFuzzedY = Math.Min(composedNoseY * 0.5f, composedNoseY * 2);
        var maximumFuzzedY = Math.Max(composedNoseY * 0.5f, composedNoseY * 2);
        TestAssert.True(fuzzedNoseY >= minimumFuzzedY && fuzzedNoseY <= maximumFuzzedY,
            "Facial bones were fuzzed from the stale pre-morph skeleton.");
        TestAssert.True(editor.Features.All(value => value.Value >= value.Minimum && value.Value <= value.Maximum),
            "Cursed morph values escaped the displayed slider ranges.");
        TestAssert.True(editor.Material.Scalars.All(value => value.Value >= value.Minimum && value.Value <= value.Maximum),
            "Cursed material values escaped the displayed slider ranges.");
        var composedEyeY = 3 + (cursed.GetFeatureOffset("eyes_Big") * 100);
        var fuzzedEyeY = cursed.FinalSkeleton.Single(value => value.BoneName == "eye_new").Translation.Y;
        TestAssert.True(fuzzedEyeY >= Math.Min(composedEyeY * 0.5f, composedEyeY * 2) &&
                        fuzzedEyeY <= Math.Max(composedEyeY * 0.5f, composedEyeY * 2),
            "A qualifying bone introduced by a morph was not fuzzed.");

        editor.UndoCommand.Execute(null);
        var restored = editor.CreateDraft();
        TestAssert.Near(0, restored.GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.Equal(new Vector3(2, 4, 6),
            restored.FinalSkeleton.Single(value => value.BoneName == "nose_tip").Translation);
        TestAssert.True(restored.MaterialOverrides.Scalars.Count == 0 && restored.MaterialOverrides.Vectors.Count == 0,
            "One Undo did not restore all cursed material values.");

        editor.RedoCommand.Execute(null);
        var redone = editor.CreateDraft();
        TestAssert.Near(cursed.GetFeatureOffset("nose_BridgeIn"), redone.GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.Equal(cursed.FinalSkeleton.Single(value => value.BoneName == "nose_tip").Translation,
            redone.FinalSkeleton.Single(value => value.BoneName == "nose_tip").Translation);
        TestAssert.True(cursed.MaterialOverrides.Scalars.SequenceEqual(redone.MaterialOverrides.Scalars),
            "Redo did not restore every cursed material scalar.");
        TestAssert.True(cursed.MaterialOverrides.Vectors.SequenceEqual(redone.MaterialOverrides.Vectors),
            "Redo did not restore every cursed material vector.");

        editor.SetToDefaultsCommand.Execute(null);
        var defaults = editor.CreateDraft();
        TestAssert.Equal(new Vector3(2, 4, 6),
            defaults.FinalSkeleton.Single(value => value.BoneName == "nose_tip").Translation);
        TestAssert.True(defaults.MorphFeatures.All(value => value.Offset == 0) &&
                        defaults.MaterialOverrides.Scalars.Count == 0 &&
                        defaults.MaterialOverrides.Vectors.Count == 0,
            "Set to Defaults did not remove the complete cursed state.");
    }

    private static void CursedMaterialOnlyLeavesBonesUnchanged()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        editor.CursedMode = true;
        editor.RandomiseMorphs = false;
        editor.RandomiseMaterials = true;
        editor.MaterialRandomisationStrength = 100;

        var before = editor.CreateDraft();
        TestAssert.True(editor.RandomiseCommand.CanExecute(null),
            "Material-only Cursed randomisation was disabled.");
        editor.RandomiseCommand.Execute(null);
        var after = editor.CreateDraft();
        TestAssert.True(after.MorphFeatures.SequenceEqual(before.MorphFeatures) &&
                        after.FinalSkeleton.SequenceEqual(before.FinalSkeleton),
            "Cursed material-only randomisation changed morphs or bones.");
        TestAssert.True(after.MaterialOverrides.Scalars.Count + after.MaterialOverrides.Vectors.Count > 0,
            "Cursed material-only randomisation did not change materials.");

        editor.RandomiseMaterials = false;
        TestAssert.True(!editor.RandomiseCommand.CanExecute(null),
            "Bone-only Cursed randomisation remained enabled while Morph was off.");
    }

    private static void DetachedMaterialRandomisationUsesCompatibleProfile()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(
            reader,
            profileKey: "le1-detached-mesh",
            materialRandomisationProfileKey: "le1-human-male");
        editor.RandomiseMorphs = false;
        editor.RandomiseMaterials = true;
        editor.MaterialRandomisationStrength = 0;

        TestAssert.True(editor.RandomiseCommand.CanExecute(null),
            "Detached material randomisation remained disabled despite a compatible donor profile.");
        editor.RandomiseCommand.Execute(null);
        TestAssert.Near(4, editor.CreateDraft().MaterialOverrides.Scalars
            .Single(value => value.Name == "HED_Norm_Blend").Value, 0);
    }

    private static void DetachedMixedSpeciesMaterialsRandomiseByProfile()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(
            reader,
            profileKey: "le1-detached-mesh",
            mixedCustomMaterials: true);
        editor.RandomiseMorphs = false;
        editor.RandomiseMaterials = true;
        editor.MaterialRandomisationStrength = 0;

        var humanSkinTone = editor.Material.Vectors.Single(value =>
            value.Name == MaterialParameterControlKey.Create("human", "SkinTone"));
        var kroganSkinTone = editor.Material.Vectors.Single(value =>
            value.Name == MaterialParameterControlKey.Create("krogan", "SkinTone"));
        TestAssert.True(humanSkinTone.Label == "Human - Skin Tone" &&
                        kroganSkinTone.Label == "Krogan - Skin Tone",
            "MESH controls did not expose their racial parameter prefixes.");
        editor.Material.SetNumericValues(
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4> { [kroganSkinTone.Name] = new(0.25f) });
        TestAssert.True(editor.Material.Materials.Materials.Values
                            .Single(value => value.Family == HeadMaterialFamily.Skin)
                            .Vectors["SkinTone"] == Vector4.One &&
                        editor.Material.Materials.Materials.Values
                            .Single(value => value.Family == HeadMaterialFamily.KroganSkin)
                            .Vectors["SkinTone"] == new Vector4(0.25f),
            "Editing Krogan Skin Tone also changed the Human material parameter.");

        TestAssert.True(editor.RandomiseCommand.CanExecute(null),
            "A mixed Custom material surface did not expose material randomisation.");
        editor.RandomiseCommand.Execute(null);
        var scalars = editor.CreateDraft().MaterialOverrides.Scalars
            .ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
        TestAssert.Near(4, scalars[MaterialParameterControlKey.Create("human", "HED_Norm_Blend")], 0);
        TestAssert.Near(0.8f,
            scalars[MaterialParameterControlKey.Create("krogan", "KRO_HED_Spec_Scalar")], 0);
        var shellGradient = editor.Material.Scalars.Single(value =>
            value.Name == MaterialParameterControlKey.Create("krogan", "KRO_HED_Shell_Grad_Scalar"));
        TestAssert.True(shellGradient.CategoryKey == "facial-structure" &&
                        shellGradient.Label == "Krogan - Head Plate Gradient Strength",
            "The mixed Custom workspace did not use Krogan PCC presentation metadata.");
        TestAssert.True(editor.Categories.Single(value => value.Key == "facial-structure")
                .SliderGroups.Any(group => group.Scalars.Contains(shellGradient)),
            "The mixed Custom workspace did not place the Krogan shell control in its racial category.");
    }

    private static void FixedBakeCursedModePreservesMorphs()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateEditor(
            reader,
            geometryMode: MorphFaceGeometryMode.FixedBake);
        var before = editor.CreateDraft();

        TestAssert.True(editor.CanEditAttachments,
            "Fixed-bake mode disabled its independently editable hair and accessory references.");

        editor.CursedMode = true;
        TestAssert.True(!editor.RandomiseMorphs && !editor.AllowsCursedRandomisation && !editor.CanRandomise,
            "Fixed-bake mode exposed bone-only Cursed randomisation without morph or material controls.");
        editor.RandomisationStrength = 100;
        editor.RandomiseCommand.Execute(null);

        var after = editor.CreateDraft();
        TestAssert.True(before.MorphFeatures.SequenceEqual(after.MorphFeatures),
            "Fixed-bake Cursed mode changed authored morph slider values.");
        TestAssert.True(before.FinalSkeleton.SequenceEqual(after.FinalSkeleton),
            "Fixed-bake Cursed mode changed bones while morph randomisation was unavailable.");
        TestAssert.True(before.BakedLods.SelectMany(value => value)
                .SequenceEqual(after.BakedLods.SelectMany(value => value)),
            "Fixed-bake Cursed mode replaced the imported baked geometry.");
    }

    private static void RelativeBakeExposesMorphControls()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateEditor(
            reader,
            geometryMode: MorphFaceGeometryMode.RelativeBake);

        TestAssert.True(editor.CanEditMorphFeatures && editor.HasMorphControls,
            "A canonical Player RON relative bake did not expose its morph controls.");
        var feature = editor.Features.Single(value =>
            string.Equals(value.Name, "Target", StringComparison.OrdinalIgnoreCase));
        TestAssert.True(feature.IsEditable,
            "The canonical Player RON morph control was visible but disabled.");
        feature.Value = 0.5f;
        TestAssert.Near(0.5f, editor.CreateDraft().GetFeatureOffset("Target"), 0);
    }

    private static void CursedModeBypassesDonorAvailability()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader, includeDonor: false);
        var groupCommand = editor.Categories.Single(value => value.Key == "nose")
            .SliderGroups.Single(value => value.Key == "bridge").RandomiseCommand!;
        var notifications = 0;
        groupCommand.CanExecuteChanged += (_, _) => notifications++;

        TestAssert.True(!editor.CanRandomise && !groupCommand.CanExecute(null),
            "An empty donor corpus unexpectedly enabled normal randomisation.");
        editor.CursedMode = true;

        TestAssert.True(editor.CanRandomise && editor.RandomiseCommand.CanExecute(null),
            "Cursed mode remained unavailable without donors.");
        TestAssert.True(groupCommand.CanExecute(null) && notifications >= 1,
            "Subcategory commands were not notified when cursed mode became available.");
    }

    private static void CursedModeRespectsGlobalExclusions()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        editor.Categories.Single(value => value.Key == "nose")
            .SliderGroups.Single(value => value.Key == "bridge").Inclusion!.IsIncluded = false;
        editor.Categories.SelectMany(value => value.SliderGroups)
            .Single(value => value.Scalars.Any(scalar => scalar.Name == "HED_Norm_Blend"))
            .Inclusion!.IsIncluded = false;
        editor.Categories.SelectMany(value => value.ColourGroups)
            .Single(value => value.Values.Any(vector => vector.Name == "SkinTone"))
            .Inclusion!.IsIncluded = false;
        editor.CursedMode = true;
        editor.MorphRandomisationStrength = 100;
        editor.MaterialRandomisationStrength = 100;

        editor.RandomiseCommand.Execute(null);

        var cursed = editor.CreateDraft();
        TestAssert.Near(0, cursed.GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.True(cursed.GetFeatureOffset("eyes_Big") != 0,
            "Cursed Mode skipped an included morph category.");
        TestAssert.True(cursed.MaterialOverrides.Scalars.All(value => value.Name != "HED_Norm_Blend") &&
                        cursed.MaterialOverrides.Vectors.All(value => value.Name != "SkinTone"),
            "Cursed Mode changed excluded material subcategories.");
        TestAssert.True(cursed.MaterialOverrides.Scalars.Any(value => value.Name == "Emis_Scalar") &&
                        cursed.MaterialOverrides.Vectors.Any(value => value.Name == "Emis_Color"),
            "Cursed Mode stopped randomising included material subcategories.");

        using var bonesOnly = CreateRandomisationEditor(reader);
        foreach (var group in bonesOnly.Categories.SelectMany(value => value.SliderGroups))
            if (group.Inclusion is { } inclusion) inclusion.IsIncluded = false;
        bonesOnly.CursedMode = true;
        bonesOnly.RandomiseMaterials = false;
        bonesOnly.MorphRandomisationStrength = 100;
        TestAssert.True(bonesOnly.RandomiseCommand.CanExecute(null),
            "Excluding every morph subcategory incorrectly disabled global Cursed bones.");
        var beforeBones = bonesOnly.CreateDraft();
        bonesOnly.RandomiseCommand.Execute(null);
        var afterBones = bonesOnly.CreateDraft();
        TestAssert.True(beforeBones.MorphFeatures.SequenceEqual(afterBones.MorphFeatures) &&
                        !beforeBones.FinalSkeleton.SequenceEqual(afterBones.FinalSkeleton),
            "Excluded morph sliders changed or global Cursed bones stopped randomising.");
    }

    private static void FailedCursedRandomisationRollsBack()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader, extremeBoneOffset: true);
        editor.CursedMode = true;
        editor.RandomisationStrength = 100;

        editor.RandomiseCommand.Execute(null);

        var draft = editor.CreateDraft();
        TestAssert.Near(0, draft.GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.True(draft.FinalSkeleton.All(value =>
                float.IsFinite(value.Translation.X) && float.IsFinite(value.Translation.Y) &&
                float.IsFinite(value.Translation.Z)),
            "A failed cursed click left a non-finite skeleton behind.");
        TestAssert.True(!editor.UndoCommand.CanExecute(null),
            "A failed cursed click committed a partial history step.");
    }

    private static void RepeatedCursedRandomisationDoesNotCompound()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        editor.CursedMode = true;
        editor.RandomisationStrength = 100;

        editor.RandomiseCommand.Execute(null);
        var first = editor.CreateDraft();
        editor.RandomiseCommand.Execute(null);
        var second = editor.CreateDraft();

        TestAssert.True(first.MorphFeatures.SequenceEqual(second.MorphFeatures),
            "Repeated cursed morph values compounded.");
        TestAssert.True(first.FinalSkeleton.SequenceEqual(second.FinalSkeleton),
            $"Repeated cursed bone values compounded. First={string.Join(";", first.FinalSkeleton)} Second={string.Join(";", second.FinalSkeleton)}");
        TestAssert.True(first.MaterialOverrides.Scalars.SequenceEqual(second.MaterialOverrides.Scalars),
            "Repeated cursed material scalars compounded.");
        TestAssert.True(first.MaterialOverrides.Vectors.SequenceEqual(second.MaterialOverrides.Vectors),
            "Repeated cursed material vectors compounded.");
    }

    private static void EmbeddedRandomisationCorpusLoads()
    {
        var catalog = MorphRandomisationCatalog.LoadEmbedded();
        TestAssert.Equal(10, catalog.Corpus.Pools.Count(value => value.Value.Count > 0));
        TestAssert.Equal(4418, catalog.Corpus.Pools.Sum(value => value.Value.Count));
        TestAssert.True(catalog.Corpus.Pools.Values.SelectMany(value => value).All(donor =>
                !donor.Id.EndsWith(".Broke", StringComparison.OrdinalIgnoreCase)),
            "The known broken LE3 HMM donor remained in the embedded corpus.");
    }

    private static MainWindowViewModel CreateMainWindowViewModel(
        MorphFacePackageReader reader,
        StubEditorDialogs? dialogs = null,
        StubClipboard? clipboard = null,
        MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureCatalogService? textureCatalog = null)
    {
        var sceneFactory = new HeadPreviewSceneFactory();
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var targets = new MorphTargetCatalog();
        var writer = new MorphFacePackageWriter();
        var context = new MorphFacePackageContextService();
        textureCatalog ??= TestFixtures.CreateMissingTextureCatalogService();
        return new MainWindowViewModel(
            dialogs ?? new StubEditorDialogs(),
            new MorphFaceCatalogService(profiles),
            new MorphFacePreviewLoadService(sceneFactory, targets, profiles, reader),
            sceneFactory,
            new StubColorDialog(),
            new PackageReferenceService(reader, textureCatalog),
            writer,
            context,
            new MorphFaceConversionService(
                profiles, targets, context, textureCatalog),
            new MorphFaceInterchangeService(),
            clipboard ?? new StubClipboard());
    }

    private static FaceEditorViewModel CreateEditor(
        MorphFacePackageReader reader,
        IHeadEditorUiProfile? metadataCatalog = null,
        string profileKey = "le1-human-male",
        bool ignoresAuthoredGeometry = false,
        MorphFaceGeometryMode geometryMode = MorphFaceGeometryMode.MorphEvaluated)
    {
        var mesh = geometryMode is MorphFaceGeometryMode.FixedBake or MorphFaceGeometryMode.RelativeBake
            ? TestFixtures.CreateRenderableTwoLodMesh()
            : TestFixtures.CreateMesh();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            null,
            null,
            [new MorphFeatureValue("Target", 0)],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var morphSession = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(
            document,
            mesh,
            ignoresAuthoredGeometry ? [] : [TestFixtures.CreateTarget()],
            metadataOnlyFeatures: ignoresAuthoredGeometry
                ? FemaleTurianFeatureMetadataCatalog.MetadataOnlyFeatures
                : null,
            geometryEditBlockReason: ignoresAuthoredGeometry ? "Material-only test profile." : null,
            ignoreAuthoredGeometry: ignoresAuthoredGeometry,
            geometryMode: geometryMode);
        var materialIdentity = TestFixtures.CreateIdentity("TurianHeadMaterial", "MaterialInstanceConstant");
        var resolvedMaterials = ignoresAuthoredGeometry
            ? new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [MaterialIdentityKey.Create(materialIdentity)] = new ResolvedHeadMaterial(
                    MaterialIdentityKey.Create(materialIdentity),
                    materialIdentity,
                    "ALN_HED_PRO_MASTER_MAT",
                    HeadMaterialFamily.TurianSkin,
                    HeadMaterialBlendMode.Opaque,
                    false,
                    new Dictionary<string, float> { ["TUR_HED_Diffuse02_Scalar"] = 0.5f },
                    new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.One },
                    new Dictionary<string, MaterialTextureBinding>())
            })
            : ResolvedHeadMaterialSet.Empty;
        var materialSession = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            resolvedMaterials);
        return new FaceEditorViewModel(
            morphSession,
            metadataCatalog ?? new HumanMaleFeatureMetadataCatalog(),
            materialSession,
            new StubColorDialog(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "fixture.pcc",
            [],
            [],
            null,
            [],
            _ => { },
            profileKey,
            randomisationCatalog: ignoresAuthoredGeometry ? MorphRandomisationCatalog.LoadEmbedded() : null,
            ignoresAuthoredGeometry: ignoresAuthoredGeometry);
    }

    private static FaceEditorViewModel CreateRandomisationEditor(
        MorphFacePackageReader reader,
        bool includeDonor = true,
        bool extremeBoneOffset = false,
        RandomisationInclusionState? randomisationInclusionState = null,
        Func<int>? randomSeedFactory = null,
        bool includeSecondDonor = false,
        string profileKey = "le1-human-male",
        string? materialRandomisationProfileKey = null,
        bool mixedCustomMaterials = false,
        bool includePlayerHairMorph = false,
        bool includePlayerHairColours = false,
        MorphFaceGame? playerGame = null)
    {
        var sourceMesh = TestFixtures.CreateMesh();
        var mesh = sourceMesh with
        {
            Topology = sourceMesh.Topology with
            {
                ReferenceSkeleton =
                [
                    new ReferenceBone("root", 0, Vector3.Zero, Quaternion.Identity),
                    new ReferenceBone("nose_tip", 0, new Vector3(2, 4, 6), Quaternion.Identity),
                    new ReferenceBone("eye_new", 0, new Vector3(1, 3, 5), Quaternion.Identity)
                ],
                ActiveBones = [0, 1, 2],
                RequiredBones = [0, 1, 2]
            }
        };
        MorphTargetAsset Target(string name) => new(
            TestFixtures.CreateIdentity($"Set.{name}", "MorphTarget"),
            [new MorphTargetLod(0, 3, [])],
            name.Equals("nose_BridgeIn", StringComparison.OrdinalIgnoreCase)
                ? [new MorphTargetBoneOffset("nose_tip", new Vector3(0, extremeBoneOffset ? float.MaxValue : 100, 0))]
                : name.Equals("eyes_Big", StringComparison.OrdinalIgnoreCase)
                    ? [new MorphTargetBoneOffset("eye_new", new Vector3(0, 100, 0))]
                    : []);
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            includePlayerHairMorph
                ? [new MorphFeatureValue("nose_BridgeIn", 0), new MorphFeatureValue("eyes_Big", 0),
                    new MorphFeatureValue("Afro", 0)]
                : [new MorphFeatureValue("nose_BridgeIn", 0), new MorphFeatureValue("eyes_Big", 0)],
            [new BoneTranslation("root", Vector3.Zero), new BoneTranslation("nose_tip", new Vector3(2, 4, 6))],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var session = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(
            document, mesh, includePlayerHairMorph
                ? [Target("nose_BridgeIn"), Target("eyes_Big"), Target("Afro")]
                : [Target("nose_BridgeIn"), Target("eyes_Big")]);
        var materialIdentity = TestFixtures.CreateIdentity("HeadMaterial", "MaterialInstanceConstant");
        var materialVectors = new Dictionary<string, Vector4>
        {
            ["SkinTone"] = Vector4.One,
            ["Emis_Color"] = Vector4.One
        };
        var materialTextures = new Dictionary<string, MaterialTextureBinding>();
        if (includePlayerHairColours)
        {
            materialVectors["HED_Hair_Colour_Vector"] = new Vector4(0.13f, 0.27f, 0.39f, 0.7f);
            materialVectors["HED_Addn_Colour_Vector"] = new Vector4(0.17f, 0.31f, 0.43f, 0.6f);
            materialVectors["blonde"] = new Vector4(0.19f, 0.33f, 0.47f, 0.5f);
            const string texturePackagePath = "fixture.pcc";
            materialTextures["HED_Addn"] = new MaterialTextureBinding("HED_Addn",
                CreateTestTexture(texturePackagePath, "Working.PlayerBeard", "HED_Addn"));
        }
        var resolvedMaterial = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(materialIdentity), materialIdentity, "BIOG_HMM_HED_PROMorph",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float> { ["HED_Norm_Blend"] = 2, ["Emis_Scalar"] = 1 },
            materialVectors,
            materialTextures);
        var kroganIdentity = TestFixtures.CreateIdentity("KroganMaterial", "MaterialInstanceConstant");
        var kroganMaterial = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(kroganIdentity), kroganIdentity, "BIOG_KRO_HED_PROMorph",
            HeadMaterialFamily.KroganSkin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>
            {
                ["KRO_HED_Spec_Scalar"] = 0.2f,
                ["KRO_HED_Shell_Grad_Scalar"] = 0.5f
            },
            new Dictionary<string, Vector4> { ["SkinTone"] = new(0.2f) },
            new Dictionary<string, MaterialTextureBinding>());
        CustomMaterialWorkspace? customWorkspace = null;
        IReadOnlyList<CustomMaterialAssignmentOption> customOptions = [];
        if (mixedCustomMaterials)
        {
            var source = new ImportedMeshAsset(
                "mixed-custom.psk",
                [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                null, null, null,
                [0, 1, 2],
                [new ImportedMeshSection(0, "Human", 0, 3), new ImportedMeshSection(1, "Krogan", 0, 3)],
                [], null, null, [0, 1, 2]);
            customWorkspace = new CustomMaterialWorkspace(source);
            customOptions =
            [
                new CustomMaterialAssignmentOption(
                    "human", "Human", "human-male", "head", HeadMaterialFamily.Skin, resolvedMaterial)
                    { RandomisationProfileKey = "le1-human-male" },
                new CustomMaterialAssignmentOption(
                    "krogan", "Krogan", "krogan", "head", HeadMaterialFamily.KroganSkin, kroganMaterial)
                    { RandomisationProfileKey = "le1-krogan" }
            ];
            customWorkspace.Assign(0, customOptions[0]);
            customWorkspace.Assign(1, customOptions[1]);
        }
        var materials = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            customWorkspace?.ActiveMaterials ?? new ResolvedHeadMaterialSet(
                new Dictionary<string, ResolvedHeadMaterial> { [resolvedMaterial.Key] = resolvedMaterial }));
        var donor = new MorphRandomisationDonor(
            "LE1:Seed", "le1-human-male",
            new HashSet<string>(includePlayerHairMorph
                ? ["nose_BridgeIn", "eyes_Big", "Afro"]
                : ["nose_BridgeIn", "eyes_Big"], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["nose_BridgeIn"] = 0.6f,
                ["eyes_Big"] = 0.7f
            })
        {
            MaterialScalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["HED_Norm_Blend"] = 4,
                ["Emis_Scalar"] = 2
            },
            MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["SkinTone"] = new(0.4f, 0.2f, 0.1f, 1),
                ["Emis_Color"] = new(0.4f, 0.3f, 0.2f, 1)
            }
        };
        var secondDonor = new MorphRandomisationDonor(
            "LE1:SecondSeed", "le1-human-male",
            new HashSet<string>(["nose_BridgeIn", "eyes_Big"], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["nose_BridgeIn"] = 0.2f,
                ["eyes_Big"] = 0.3f
            })
        {
            MaterialScalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["HED_Norm_Blend"] = 5,
                ["Emis_Scalar"] = 3
            },
            MaterialVectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["SkinTone"] = new(0.2f, 0.3f, 0.4f, 1),
                ["Emis_Color"] = new(0.2f, 0.3f, 0.4f, 1)
            }
        };
        var kroganDonor = new MorphRandomisationDonor(
            "LE1:KroganSeed", "le1-krogan",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase))
        {
            MaterialScalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["KRO_HED_Spec_Scalar"] = 0.8f
            }
        };
        var pools = new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
        {
            [MorphRandomisationPoolKey.HumanMaleLe12] = includeDonor
                ? includeSecondDonor ? [donor, secondDonor] : [donor]
                : []
        };
        if (mixedCustomMaterials)
        {
            pools[MorphRandomisationPoolKey.Krogan] = [kroganDonor];
        }
        var profiles = new Dictionary<string, MaterialRandomisationProfile>(StringComparer.OrdinalIgnoreCase);
        if (includeDonor)
        {
            profiles["le1-human-male"] = new MaterialRandomisationProfile(
                "le1-human-male",
                new Dictionary<string, MaterialScalarStatistics>(StringComparer.OrdinalIgnoreCase)
                {
                    ["HED_Norm_Blend"] = new("HED_Norm_Blend", 1, 6, 2, 5),
                    ["Emis_Scalar"] = new("Emis_Scalar", 0, 4, 1, 3)
                },
                new Dictionary<string, MaterialVectorStatistics>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SkinTone"] = new("SkinTone", MaterialVectorRandomisationKind.PerceptualColour,
                        new Vector4(0.1f, 0.05f, 0.02f, 1), new Vector4(0.8f, 0.6f, 0.4f, 1), []),
                    ["Emis_Color"] = new("Emis_Color", MaterialVectorRandomisationKind.PerceptualColour,
                        Vector4.Zero, new Vector4(4), [])
                },
                new HashSet<string>());
        }
        if (mixedCustomMaterials)
        {
            profiles["le1-krogan"] = new MaterialRandomisationProfile(
                "le1-krogan",
                new Dictionary<string, MaterialScalarStatistics>(StringComparer.OrdinalIgnoreCase)
                {
                    ["KRO_HED_Spec_Scalar"] = new("KRO_HED_Spec_Scalar", 0, 1, 0.2f, 0.8f)
                },
                new Dictionary<string, MaterialVectorStatistics>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>());
        }
        var corpus = new MorphRandomisationCorpus(
            MorphRandomisationCorpus.CurrentFormatVersion,
            pools)
        {
            MaterialProfiles = profiles
        };
        return new FaceEditorViewModel(
            session,
            mixedCustomMaterials
                ? new DetachedMeshFeatureMetadataCatalog(customWorkspace)
                : new HumanMaleFeatureMetadataCatalog(),
            materials,
            new StubColorDialog(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "fixture.pcc",
            [], [], null, [], _ => { },
            profileKey,
            new MorphRandomisationCatalog(corpus),
            randomSeedFactory ?? (() => 123),
            randomisationInclusionState,
            customMaterialWorkspace: customWorkspace,
            customMaterialOptions: customOptions,
            materialRandomisationProfileKey: materialRandomisationProfileKey,
            playerRandomisationGame: playerGame ?? (includePlayerHairMorph ? MorphFaceGame.LE1 : null));
    }

    private static void NumericWheelIncrementsAreSafe()
    {
        TestAssert.Near(0.1f, NumericWheelValue.Adjust(0, 0.1f, 120), 0);
        TestAssert.Near(1f, NumericWheelValue.Adjust(0, 1f, 120), 0);
        TestAssert.Near(-10f, NumericWheelValue.Adjust(0, 10f, -120), 0);
        TestAssert.Near(123456.22f, NumericWheelValue.Adjust(123456.12f, 0.1f, 120), 0.01f);
    }

    private static void AttachmentEditorUsesTwoSlots()
    {
        var mesh = TestFixtures.CreateMesh();
        var target = TestFixtures.CreateTarget();
        var firstOther = TestFixtures.CreateIdentity("FirstOther", "SkeletalMesh");
        var preservedOther = TestFixtures.CreateIdentity("PreservedOther", "SkeletalMesh");
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            null,
            null,
            [new MorphFeatureValue("Target", 0)],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            [])
        {
            OtherMeshReferences = [firstOther, preservedOther]
        };
        var editing = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(face, mesh, [target]);
        var materials = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(
                new Dictionary<string, ResolvedHeadMaterial>(StringComparer.OrdinalIgnoreCase)));
        using var reader = new MorphFacePackageReader();
        using var editor = new FaceEditorViewModel(
            editing,
            new HumanMaleFeatureMetadataCatalog(),
            materials,
            new StubColorDialog(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "fixture.pcc",
            [],
            [],
            null,
            [firstOther, preservedOther],
            _ => { });

        TestAssert.Equal(2, editor.AttachmentMeshes.Count);
        TestAssert.Equal(1, editor.OtherMeshes.Count);
        TestAssert.Equal(2, editor.CreateDraft().OtherMeshReferences.Count);
        TestAssert.Equal(preservedOther, editor.CreateDraft().OtherMeshReferences[1]);
    }

    private static void MeshAttachmentPickerFiltersCandidates()
    {
        var hair = new PackageAssetListItem(new AssetIdentity(
            "BioA_Hair.pcc", "BIOG_HED_Hair.Meshes.HairA", 1, "SkeletalMesh")) { BoneCount = 42 };
        var helmet = new PackageAssetListItem(new AssetIdentity(
            "BioB_Armour.pcc", "BIOG_HED_Helmet.Meshes.HelmetB", 2, "SkeletalMesh"));
        using var editor = new HairMeshEditorViewModel(
            new AssetReferenceEditingSession(null), [hair, helmet], "Hair", 0);

        TestAssert.True(!editor.IsRandomisationLocked,
            "Attachment randomisation was locked by default.");
        editor.IsRandomisationLocked = true;
        TestAssert.Equal(3, editor.Candidates.Count);
        TestAssert.Equal("Open package · 42 bones",
            editor.Candidates.Single(option => option.Identity == hair.Identity).SourceDescription);
        editor.Selected = editor.Options.Single(option => option.Identity == hair.Identity);
        TestAssert.True(editor.IsRandomisationLocked,
            "Manually choosing an attachment unexpectedly released its randomisation lock.");
        TestAssert.True(!editor.TrySetRandomisedSelection(helmet.Identity),
            "Attachment randomisation bypassed the locked selection.");
        TestAssert.Equal(hair.Identity, editor.Value);
        editor.IsRandomisationLocked = false;
        TestAssert.True(editor.TrySetRandomisedSelection(helmet.Identity),
            "An unlocked attachment did not accept an eligible randomised mesh.");
        TestAssert.Equal(helmet.Identity, editor.Value);
        editor.Selected = editor.Options.Single(option => option.Identity == hair.Identity);
        editor.SearchText = "helmet";
        TestAssert.Equal(2, editor.Candidates.Count);
        editor.Selected = null!;
        TestAssert.Equal(hair.Identity, editor.Selected.Identity);
        TestAssert.True(editor.Candidates.Any(option => option.Identity == helmet.Identity),
            "Mesh search did not retain the matching object name.");

        editor.SearchText = "BIOG_HED_Hair.Meshes";
        TestAssert.Equal(2, editor.Candidates.Count);
        TestAssert.True(editor.Candidates.Any(option => option.Identity == hair.Identity),
            "Mesh search did not match the instanced object path.");

        editor.SearchText = "BioA_Hair.pcc";
        TestAssert.Equal(2, editor.Candidates.Count);
        TestAssert.True(editor.Candidates.Any(option => option.Identity == hair.Identity),
            "Mesh search did not match the source package path.");

        editor.SearchText = "does-not-exist";
        TestAssert.Equal(1, editor.Candidates.Count);
        TestAssert.True(editor.Candidates[0].Identity is null,
            "The None option should remain available when filtering mesh candidates.");
    }

    private static void MeshAttachmentPickerMergesRegistry()
    {
        const string canonical = "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Custom_MDL";
        var biog = new AttachmentMeshOccurrence("BIOG_HMF_HIR_PRO.pcc",
            "Hair.HMF_HIR_Custom_MDL", 7, 0, TextureCatalogOrigin.BaseGame, 42);
        var mod = new AttachmentMeshOccurrence("DLC_MOD_Test/ModHair.pcc",
            canonical, 8, 9000, TextureCatalogOrigin.Mod, 47);
        using var editor = new HairMeshEditorViewModel(
            new AssetReferenceEditingSession(null), [], "Hair", 0);

        editor.UpdateRegistryCandidates([new AttachmentMeshCandidate(canonical, biog, [biog, mod])],
            isPlayerWorkspace: false);
        TestAssert.Equal(3, editor.Candidates.Count);
        TestAssert.Equal(biog.PackagePath, editor.Candidates[1].Identity?.PackagePath);
        TestAssert.Equal(mod.PackagePath, editor.Candidates[2].Identity?.PackagePath);
        TestAssert.Equal(canonical, editor.Candidates[2].DisplayName);
        TestAssert.True(editor.Candidates[1].SourceDescription.Contains("Base game · BIOG_HMF_HIR_PRO.pcc · 42 bones"),
            "The BIOG mesh row omitted its source and bone count.");
        TestAssert.True(editor.Candidates[2].SourceDescription.Contains("Mod · ModHair.pcc · 47 bones"),
            "The mod mesh row omitted its source and bone count.");
        editor.Selected = editor.Candidates[2];
        TestAssert.Equal(mod.PackagePath, editor.Selected.Identity?.PackagePath);
        var manual = new AttachmentMeshOccurrence("C:\\Custom\\BIOG_HMF_HIR_Custom.pcc",
            "Hair.HMF_HIR_Custom_MDL", 2, 0, TextureCatalogOrigin.Manual, 48);
        editor.UpdateRegistryCandidates(
            [new AttachmentMeshCandidate(canonical, biog, [biog, mod]),
             new AttachmentMeshCandidate("BIOG_HMF_HIR_Custom.Hair.HMF_HIR_Custom_MDL", manual, [manual])],
            isPlayerWorkspace: true);
        TestAssert.True(editor.Candidates.Any(option => option.Identity?.PackagePath == manual.PackagePath &&
                option.SourceDescription.Contains("Custom · BIOG_HMF_HIR_Custom.pcc · 48 bones")),
            "A manually added seek-free BIOG HIR mesh was hidden from the player workspace.");
    }

    private static void MeshAttachmentPickerPreviewDoesNotCommit()
    {
        var first = new PackageAssetListItem(new AssetIdentity(
            "BioA_Hair.pcc", "BIOG_HED_Hair.Meshes.HairA", 1, "SkeletalMesh"));
        var second = new PackageAssetListItem(new AssetIdentity(
            "BioB_Hair.pcc", "BIOG_HED_Hair.Meshes.HairB", 2, "SkeletalMesh"));
        var session = new AssetReferenceEditingSession(first.Identity);
        using var editor = new HairMeshEditorViewModel(session, [first, second], "Hair", 0);
        var previewEvents = 0;
        editor.PreviewChanged += (_, _) => previewEvents++;
        var candidate = editor.Options.Single(option => option.Identity == second.Identity);

        editor.Preview(candidate);
        TestAssert.Equal(first.Identity, editor.Value);
        TestAssert.Equal(candidate, editor.PreviewSelection);
        TestAssert.True(!session.CanUndo, "A mesh hover preview entered attachment history.");

        editor.CancelPreview();
        TestAssert.Equal(first.Identity, editor.Value);
        TestAssert.True(editor.PreviewSelection is null && previewEvents == 2,
            "Cancelling a mesh preview did not restore the committed selection.");

        editor.Commit(candidate);
        TestAssert.Equal(second.Identity, editor.Value);
        TestAssert.True(session.CanUndo, "A committed mesh choice did not enter attachment history.");
    }

    private static void ExtendedSlidersAreOptional()
    {
        var metadata = new MorphFeatureMetadata(
            "Target", "Target", "Face", "Face", true, 0, 0, 1, 0.01f, true, "");
        var mesh = TestFixtures.CreateMesh();
        var target = TestFixtures.CreateTarget();
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            null,
            null,
            [new MorphFeatureValue("Target", 0)],
            [new BoneTranslation("root", new Vector3(3, 0, 0))],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var editing = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(face, mesh, [target]);
        var feature = new MorphFeatureEditorViewModel(editing, metadata, null, _ => { });
        var bone = new BoneAxisEditorViewModel(editing, "root", 0);

        TestAssert.Near(0, feature.Minimum, 0);
        TestAssert.Near(1, feature.Maximum, 0);
        TestAssert.Near(1, bone.Minimum, 0);
        TestAssert.Near(5, bone.Maximum, 0);

        feature.SetExtendedSliders(true);
        bone.SetExtendedSliders(true);
        feature.Value = -0.5f;
        TestAssert.Near(-1, feature.Minimum, 0);
        TestAssert.Near(1, feature.Maximum, 0);
        TestAssert.Near(-5, bone.Minimum, 0);
        TestAssert.Near(5, bone.Maximum, 0);

        feature.SetExtendedSliders(false);
        TestAssert.Near(-0.5f, feature.Value, 0);
        TestAssert.Near(-0.5f, feature.Minimum, 0);
    }

    private static void BoneTransformSelectionIsPerEditor()
    {
        using var reader = new MorphFacePackageReader();
        using var first = CreateRandomisationEditor(reader);
        TestAssert.True(first.BoneTransforms.Count > 1 && first.SelectedBoneTransform is not null,
            "The first face editor did not select an available bone transform.");

        first.SelectedBoneTransform = first.BoneTransforms[1];
        TestAssert.Equal(first.BoneTransforms[1], first.SelectedBoneTransform);

        using var second = CreateRandomisationEditor(reader);
        TestAssert.True(second.SelectedBoneTransform is not null,
            "A replacement face editor did not establish its own selected bone transform.");
        TestAssert.Equal(second.BoneTransforms[0], second.SelectedBoneTransform);
    }

    private static void BoneControlsTolerateRemovedMorphBones()
    {
        var source = TestFixtures.CreateMesh();
        var mesh = source with
        {
            Topology = source.Topology with
            {
                ReferenceSkeleton =
                [
                    new ReferenceBone("root", 0, Vector3.Zero, Quaternion.Identity),
                    new ReferenceBone("eye_transient", 0, new Vector3(1, 2, 3), Quaternion.Identity)
                ],
                ActiveBones = [0, 1],
                RequiredBones = [0, 1]
            }
        };
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.eyes_wide", "MorphTarget"),
            [new MorphTargetLod(0, mesh.Positions.Length, [])],
            [new MorphTargetBoneOffset("eye_transient", Vector3.One)]);
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("SalarianFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [new MorphFeatureValue("eyes_wide", 1)],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var session = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(
            face, mesh, [target], profileName: "Salarian");
        var control = new BoneAxisEditorViewModel(session, "eye_transient", 0);

        session.SetFeatures(new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["eyes_wide"] = 0
        });
        control.Refresh();

        TestAssert.True(!control.IsAvailable,
            "A bone control remained active after its morph-introduced bone left the evaluated skeleton.");
        session.Undo();
        control.Refresh();
        TestAssert.True(control.IsAvailable,
            "Undo did not reactivate a bone control when its morph-introduced bone returned.");
    }

    private sealed class StubColorDialog : IHdrColorDialogService
    {
        public bool ExtendedSliders { get; set; }
        public Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview) => null;
        public Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview) => null;
    }

    private sealed class StubClipboard : IMorphFaceClipboardService
    {
        private readonly HashSet<MorphFaceClipboardKind> _availableKinds;

        public StubClipboard(params MorphFaceClipboardKind[] availableKinds) =>
            _availableKinds = availableKinds.ToHashSet();

        public void Set(MorphFaceClipboardPayload payload) => throw new NotSupportedException();
        public MorphFaceClipboardPayload Get(MorphFaceClipboardKind expectedKind) => throw new NotSupportedException();
        public bool Contains(MorphFaceClipboardKind expectedKind) => _availableKinds.Contains(expectedKind);
    }

    private sealed class StubEditorDialogs : IEditorDialogService
    {
        public bool TextureRegistrySettingsWasShown { get; private set; }
        public int StandaloneGameChoiceCount { get; private set; }
        public int StandaloneNameChoiceCount { get; private set; }
        public int RonImportDestinationChoiceCount { get; private set; }
        public int MorphImportFileChoiceCount { get; private set; }
        public int MaterialImportFileChoiceCount { get; private set; }
        public MorphFaceGame? StandaloneGameChoiceResult { get; init; }
        public string? StandaloneNameChoiceResult { get; init; }
        public RonImportDestinationChoice? RonImportDestinationChoiceResult { get; set; }
        public UnsavedChangesChoice ConfirmUnsavedChangesResult { get; set; } = UnsavedChangesChoice.Cancel;
        public int ConfirmUnsavedChangesCount { get; private set; }
        public string? ChoosePackage(string? initialDirectory = null) => null;
        public MorphPackageSaveRequest? ChooseMorphPackageDestination(string suggestedFileName, string sourcePackagePath) => null;
        public MorphConversionSaveRequest? ChooseMorphConversionDestination(MorphFaceGame sourceGame, string suggestedFileName, string sourcePackagePath) => null;
        public string? ChooseCloneName(string suggestedName, IReadOnlyCollection<string> existingObjectNames) => null;
        public string? ChooseMorphImportFile(string? initialDirectory = null)
        {
            MorphImportFileChoiceCount++;
            return null;
        }
        public string? ChooseMaterialImportFile(bool tse, string? initialDirectory = null)
        {
            MaterialImportFileChoiceCount++;
            return null;
        }
        public MorphFaceGame? ChooseStandaloneImportGame()
        {
            StandaloneGameChoiceCount++;
            return StandaloneGameChoiceResult;
        }
        public RonImportDestinationChoice? ChooseRonImportDestination(
            MorphFaceGame targetGame,
            IReadOnlyList<RonNpcArchetypeOption> archetypes,
            bool allowPlayer)
        {
            RonImportDestinationChoiceCount++;
            return RonImportDestinationChoiceResult;
        }
        public string? ChooseStandaloneMorphName(
            string suggestedName,
            IReadOnlyCollection<string> existingObjectNames)
        {
            StandaloneNameChoiceCount++;
            return StandaloneNameChoiceResult;
        }
        public string? ChooseRonExportFile(string suggestedFileName, string? initialDirectory = null) => null;
        public string? ChooseMeshExportDirectory(string? initialDirectory = null) => null;
        public ActorAssignmentCandidate? ChooseActorAssignment(
            ActorAssignmentInventory inventory,
            ActorAssignmentMode mode) => null;
        public bool ConfirmDeleteMorph(string facePath) => false;
        public UnsavedChangesChoice ConfirmUnsavedChanges(string assetPath, UnsavedChangesScope scope = UnsavedChangesScope.Package)
        {
            ConfirmUnsavedChangesCount++;
            return ConfirmUnsavedChangesResult;
        }
        public void ShowInformation(string title, string message) { }
        public void ShowTextureRegistrySettings() => TextureRegistrySettingsWasShown = true;
    }

    private static void LodSpecificMorphControlsAreMarked()
    {
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("HAIR_Test", "MorphTarget"),
            [
                new MorphTargetLod(0, 3, []),
                new MorphTargetLod(1, 3, [new MorphVertexDelta(0, Vector3.UnitX, Vector3.Zero)])
            ],
            []);
        var feature = new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("HAIR_Test", 0),
            target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget,
            null);
        var source = new HumanFemaleFeatureMetadataCatalog().Describe(feature, true);

        var marked = MorphFeatureLodMetadata.MarkLodCoverage(source, feature, [0, 1, 2]);

        TestAssert.True(marked.Label.EndsWith("LOD 1 ONLY", StringComparison.Ordinal),
            "LOD-only target label did not identify its functional LOD.");
        TestAssert.True(marked.Description.Contains("only in LOD 1", StringComparison.Ordinal),
            "LOD-only target tooltip did not identify its functional LOD.");

        var commonTarget = target with
        {
            Lods =
            [
                new MorphTargetLod(0, 3, [new MorphVertexDelta(0, Vector3.UnitX, Vector3.Zero)]),
                new MorphTargetLod(1, 3, [new MorphVertexDelta(0, Vector3.UnitY, Vector3.Zero)])
            ]
        };
        var commonFeature = feature with { Target = commonTarget };
        var common = MorphFeatureLodMetadata.MarkLodCoverage(source, commonFeature, [0, 1, 2]);
        TestAssert.True(!common.Label.Contains("LOD 0/1 ONLY", StringComparison.Ordinal),
            "The redundant LOD0/1 coverage tag cluttered the slider label.");
    }

    private static void MorphControlsDisableForTargetlessLod()
    {
        var mesh = TestFixtures.CreateMesh();
        var target = TestFixtures.CreateTarget(new MorphVertexDelta(1, Vector3.UnitX, Vector3.Zero));
        var face = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            null,
            null,
            [new MorphFeatureValue("Target", 0)],
            [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var session = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(face, mesh, [target]);
        var resolved = session.Evaluation.Resolution.Features.Single(feature => feature.Feature.Name == "Target");
        var metadata = new HumanMaleFeatureMetadataCatalog().Describe(resolved, session.CanEdit);
        var control = new MorphFeatureEditorViewModel(session, metadata, new HashSet<int> { 0, 1 }, _ => { });
        TestAssert.Near(0, control.Minimum, 0);
        TestAssert.Near(1, control.Maximum, 0);
        control.SetExtendedSliders(true);
        TestAssert.Near(-control.Maximum, control.Minimum, 0);
        TestAssert.True(control.Minimum < 0 && control.Maximum > 0,
            "Extended mode did not centre the morph slider on zero with a signed range.");

        control.SetPreviewLod(1, lodHasMorphGeometry: true);
        TestAssert.True(control.IsEditable, "A supported LOD disabled its morph control.");
        control.SetPreviewLod(2, lodHasMorphGeometry: false);
        TestAssert.True(!control.IsEditable, "A targetless LOD left its morph control enabled.");
    }

    private static void FixMorphUndoRestoresBakedPreview()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh(lowerLodIndex: 2);
        var target = new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.Shape", "MorphTarget"),
            [
                new MorphTargetLod(0, 3, [new MorphVertexDelta(1, new Vector3(2, 0, 0), Vector3.Zero)]),
                new MorphTargetLod(2, 3, [new MorphVertexDelta(1, new Vector3(0, 0, 2), Vector3.Zero)])
            ],
            []);
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [new MorphFeatureValue("Shape", 0.5f)],
            [],
            MorphFaceMaterialOverrides.Empty,
            [
                [Vector3.Zero, new Vector3(3, 0, 0), Vector3.UnitY],
                [new Vector3(10, 0, 0), new Vector3(12.5f, 0, 0), new Vector3(10, 2, 0)]
            ],
            []);
        var session = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(document, mesh, [target]);
        var loaded = new LoadedMorphFace(
            document,
            mesh,
            null,
            ResolvedHeadMaterialSet.Empty,
            MorphFaceEditor.Core.Diagnostics.TopologyDiagnostics.Analyze(mesh, document));
        using var reader = new MorphFacePackageReader();
        var editor = new FaceEditorViewModel(
            session,
            new HumanMaleFeatureMetadataCatalog(),
            new MorphFaceEditor.Core.Editing.MaterialEditingSession(
                MorphFaceMaterialOverrides.Empty,
                ResolvedHeadMaterialSet.Empty),
            new StubColorDialog(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "fixture.pcc",
            [],
            [],
            null,
            [],
            _ => { });
        using var viewModel = CreateMainWindowViewModel(reader);
        var setEditor = typeof(MainWindowViewModel).GetMethod(
            "SetEditor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("MainWindowViewModel.SetEditor was not found.");
        setEditor.Invoke(viewModel, [editor, loaded]);
        viewModel.PreviewLod = 2;
        var feature = editor.Features.Single(value => value.Name == "Shape");

        TestAssert.True(viewModel.FixMorphCommand.CanExecute(null),
            "The repairable face did not expose the Fix Morph command.");
        TestAssert.True(!feature.IsEditable, "The blocked face exposed an editable slider before repair.");

        viewModel.FixMorphCommand.Execute(null);

        TestAssert.True(editor.IsDirty, "Repair did not mark the face dirty.");
        TestAssert.True(feature.IsEditable, "Repair did not enable the morph slider.");
        TestAssert.True(!viewModel.FixMorphCommand.CanExecute(null),
            "Fix Morph remained available after repair.");
        TestAssert.True(viewModel.FaceDetails?.Contains("oracle repaired", StringComparison.Ordinal) == true,
            "Repair did not update the face details.");

        MorphFaceEditor.Rendering.HeadPreviewScene? undoScene = null;
        var undoDeformationCount = 0;
        viewModel.PreviewSceneReady += (scene, _) => undoScene = scene;
        viewModel.PreviewDeformationReady += _ => undoDeformationCount++;
        editor.UndoCommand.Execute(null);

        TestAssert.True(undoScene is not null, "Undo did not rebuild the fallback preview scene.");
        TestAssert.Equal(0, undoDeformationCount);
        TestAssert.Near(new Vector3(0, 0, 12.5f), undoScene!.Meshes[0].Vertices[1].Position, 0.0001f);
        TestAssert.True(!editor.IsDirty, "Undo left the repaired face dirty.");
        TestAssert.True(!feature.IsEditable, "Undo left the morph slider enabled.");
        TestAssert.True(viewModel.FixMorphCommand.CanExecute(null),
            "Undo did not restore Fix Morph availability.");
        TestAssert.True(viewModel.FaceDetails?.Contains("oracle repaired", StringComparison.Ordinal) == false &&
                        viewModel.Status.Contains("undone", StringComparison.OrdinalIgnoreCase),
            "Undo left stale repair messaging in the status pane.");

        MorphFaceEditor.Rendering.HeadPreviewDeformationUpdate? redoUpdate = null;
        viewModel.PreviewDeformationReady += update => redoUpdate = update;
        editor.RedoCommand.Execute(null);

        TestAssert.True(redoUpdate is not null, "Redo did not restore the editable deformation preview.");
        TestAssert.Near(new Vector3(0, 1, 12), redoUpdate!.Vertices[1].Position, 0.0001f);
        TestAssert.True(editor.IsDirty && feature.IsEditable,
            "Redo did not restore the repaired editor state.");
        TestAssert.True(viewModel.FaceDetails?.Contains("oracle repaired", StringComparison.Ordinal) == true,
            "Redo did not restore the repair messaging.");

        editor.UndoCommand.Execute(null);
        viewModel.PreviewDeformationReady += _ => throw new InvalidOperationException("preview callback failed");
        viewModel.FixMorphCommand.Execute(null);

        TestAssert.True(editor.IsDirty && feature.IsEditable &&
                        !viewModel.FixMorphCommand.CanExecute(null),
            "A throwing preview subscriber rolled back the committed repair state.");
        TestAssert.True(viewModel.ErrorMessage?.Contains("preview", StringComparison.OrdinalIgnoreCase) == true &&
                        viewModel.Status.Contains("retained", StringComparison.OrdinalIgnoreCase),
            "A post-repair preview failure was reported as an uncommitted repair failure.");
    }

    private static void TextureThumbnailsDiscardAlpha()
    {
        var identity = new AssetIdentity("fixture.pcc", "Package.Texture", 1, "Texture2D");
        var texture = new DecodedTextureAsset(
            identity,
            1,
            1,
            [10, 20, 30, 0],
            "PF_DXT5",
            TextureRole.Diffuse,
            TextureColorSpace.Srgb,
            TextureAlphaPolicy.Translucency,
            true,
            "fixture");
        var bitmap = (BitmapSource)TextureThumbnailFactory.Create(texture);
        var pixel = new byte[4];
        bitmap.CopyPixels(pixel, 4, 0);
        TestAssert.Equal((byte)30, pixel[0]);
        TestAssert.Equal((byte)20, pixel[1]);
        TestAssert.Equal((byte)10, pixel[2]);
        TestAssert.Equal(byte.MaxValue, pixel[3]);
    }

    private static void HdrPickerConstructs()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            App? application = null;
            try
            {
                application = new App();
                application.InitializeComponent();
                HdrPickerControlsPreserveAndScale();
                using var reader = new MorphFacePackageReader();
                using var viewModel = CreateMainWindowViewModel(reader);
                var mainWindow = new MainWindow(viewModel);
                mainWindow.ShowInTaskbar = false;
                mainWindow.Opacity = 0;
                mainWindow.Show();
                var faceList = mainWindow.FindName("FaceList") as ListBox;
                var commit = mainWindow.FindName("CommitFaceButton") as Button;
                var randomise = mainWindow.FindName("GlobalRandomiseButton") as Button;
                var setToDefaults = mainWindow.FindName("SetToDefaultsButton") as Button;
                var morphStrength = mainWindow.FindName("MorphRandomisationStrengthSlider") as Slider;
                var materialStrength = mainWindow.FindName("MaterialRandomisationStrengthSlider") as Slider;
                var morphToggle = mainWindow.FindName("RandomiseMorphsCheckBox") as CheckBox;
                var materialToggle = mainWindow.FindName("RandomiseMaterialsCheckBox") as CheckBox;
                var cursedMode = mainWindow.FindName("CursedModeCheckBox") as CheckBox;
                var attachmentPanel = mainWindow.FindName("AttachmentMeshPanel") as StackPanel;
                var morphStrengthLabel = mainWindow.FindName("MorphRandomisationStrengthLabel") as TextBlock;
                var morphStrengthValue = mainWindow.FindName("MorphRandomisationStrengthValue") as TextBlock;
                var materialStrengthLabel = mainWindow.FindName("MaterialRandomisationStrengthLabel") as TextBlock;
                var materialStrengthValue = mainWindow.FindName("MaterialRandomisationStrengthValue") as TextBlock;
                var hairLabel = mainWindow.FindName("HairAccessoryMeshesLabel") as TextBlock;
                var inclusionStyle = application.TryFindResource("RandomisationPadlock") as Style;
                var enterBinding = faceList?.InputBindings
                    .OfType<System.Windows.Input.KeyBinding>()
                    .SingleOrDefault(binding => binding.Key == System.Windows.Input.Key.Enter);
                TestAssert.True(enterBinding?.Command is not null,
                    "The BioMorphFace export list does not load its selection when Enter is pressed.");
                TestAssert.True(commit?.GetBindingExpression(Button.CommandProperty) is not null,
                    "The face editor has no bound Commit button for temporary-workspace writes.");
                TestAssert.True(inclusionStyle is not null && inclusionStyle.TargetType == typeof(CheckBox),
                    "The randomisation padlock checkbox style is missing.");
                TestAssert.True(inclusionStyle!.Setters.OfType<Setter>().Any(value =>
                                        value.Property == FrameworkElement.WidthProperty && Equals(value.Value, 20d)) &&
                                    inclusionStyle.Setters.OfType<Setter>().Any(value =>
                                        value.Property == FrameworkElement.HeightProperty && Equals(value.Value, 20d)),
                    "The randomisation padlock checkbox is not sized alongside its Randomise button.");
                TestAssert.True(randomise is not null,
                    "The main editor header has no named global Randomise button.");
                TestAssert.True(randomise!.GetBindingExpression(Button.CommandProperty) is not null,
                    "The global Randomise button has no command binding.");
                TestAssert.True(setToDefaults?.GetBindingExpression(Button.CommandProperty) is not null,
                    "The main editor header has no bound Set to Defaults button.");
                TestAssert.True(morphStrength is not null && materialStrength is not null,
                    "The main editor header is missing a split randomisation-strength slider.");
                TestAssert.Near(0, (float)morphStrength!.Minimum, 0);
                TestAssert.Near(100, (float)morphStrength.Maximum, 0);
                TestAssert.Near(10, (float)morphStrength.SmallChange, 0);
                TestAssert.Near(10, (float)morphStrength.LargeChange, 0);
                TestAssert.Near(0, (float)materialStrength!.Minimum, 0);
                TestAssert.Near(100, (float)materialStrength.Maximum, 0);
                TestAssert.Near(10, (float)materialStrength.SmallChange, 0);
                TestAssert.Near(10, (float)materialStrength.LargeChange, 0);
                TestAssert.True(morphStrength.GetBindingExpression(Slider.ValueProperty) is not null &&
                                materialStrength.GetBindingExpression(Slider.ValueProperty) is not null,
                    "A split randomisation-strength slider has no value binding.");
                morphStrength.GetBindingExpression(Slider.ValueProperty)!.UpdateTarget();
                materialStrength.GetBindingExpression(Slider.ValueProperty)!.UpdateTarget();
                morphStrengthValue?.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                materialStrengthValue?.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                mainWindow.UpdateLayout();
                TestAssert.True(morphStrength.Value == 50,
                    $"Unloaded morph strength was {morphStrength.Value}; source={viewModel.MorphRandomisationStrength}; " +
                    $"binding={morphStrength.GetBindingExpression(Slider.ValueProperty)!.Status}.");
                TestAssert.True(materialStrength.Value == 50,
                    $"Unloaded material strength was {materialStrength.Value}; source={viewModel.MaterialRandomisationStrength}; " +
                    $"binding={materialStrength.GetBindingExpression(Slider.ValueProperty)!.Status}.");
                TestAssert.Equal("50%", morphStrengthValue?.Text);
                TestAssert.Equal("50%", materialStrengthValue?.Text);
                morphStrength.Value = 73;
                materialStrength.Value = 26;
                morphStrength.GetBindingExpression(Slider.ValueProperty)!.UpdateSource();
                materialStrength.GetBindingExpression(Slider.ValueProperty)!.UpdateSource();
                morphStrengthValue?.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                materialStrengthValue?.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                mainWindow.UpdateLayout();
                TestAssert.Equal(73, viewModel.MorphRandomisationStrength);
                TestAssert.Equal(26, viewModel.MaterialRandomisationStrength);
                TestAssert.Equal("73%", morphStrengthValue?.Text);
                TestAssert.Equal("26%", materialStrengthValue?.Text);
                TestAssert.True(hairLabel is not null && morphStrengthLabel is not null &&
                                materialStrengthLabel is not null && morphStrengthValue is not null &&
                                materialStrengthValue is not null,
                    "The randomisation strength typography could not be inspected.");
                TestAssert.Near((float)hairLabel!.FontSize, (float)morphStrengthLabel!.FontSize, 0);
                TestAssert.Near((float)hairLabel.FontSize, (float)materialStrengthLabel!.FontSize, 0);
                TestAssert.Near((float)hairLabel.FontSize, (float)morphStrengthValue!.FontSize, 0);
                TestAssert.Near((float)hairLabel.FontSize, (float)materialStrengthValue!.FontSize, 0);
                TestAssert.True(morphToggle?.GetBindingExpression(ToggleButton.IsCheckedProperty) is not null &&
                                materialToggle?.GetBindingExpression(ToggleButton.IsCheckedProperty) is not null,
                    "Independent morph/material randomisation toggles were not bound.");
                TestAssert.Equal(true, morphToggle!.IsChecked);
                TestAssert.Equal(false, materialToggle!.IsChecked);
                morphToggle.IsChecked = false;
                materialToggle.IsChecked = true;
                morphToggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                materialToggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestAssert.True(!viewModel.RandomiseMorphs && viewModel.RandomiseMaterials,
                    "Randomisation toggles were not retained by window-session state.");
                TestAssert.True(cursedMode is not null &&
                                cursedMode.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty) is not null,
                    "The editor header has no bound Cursed Mode checkbox.");
                cursedMode!.IsChecked = true;
                cursedMode.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestAssert.True(viewModel.CursedMode,
                    "Cursed Mode was not retained by window-session state.");
                TestAssert.True(cursedMode!.GetBindingExpression(System.Windows.UIElement.IsEnabledProperty) is not null,
                    "The Cursed Mode checkbox cannot be enabled independently of donor availability.");
                TestAssert.Equal("Editor.CanEditAttachments",
                    attachmentPanel?.GetBindingExpression(UIElement.IsEnabledProperty)?.ParentBinding.Path.Path);
                TestAssert.Equal("Enable this only if you are mentally disturbed and/or are Mgamerz",
                    cursedMode!.ToolTip?.ToString());
                mainWindow.Close();
                var meshExportMenu = new MenuItem { Header = "Export Morph as Mesh" };
                meshExportMenu.Items.Add(new MenuItem { Header = "PSK…" });
                var contextMenu = new ContextMenu();
                contextMenu.Items.Add(meshExportMenu);
                meshExportMenu.ApplyTemplate();
                var submenuPopup = meshExportMenu.Template.FindName("PART_Popup", meshExportMenu) as Popup;
                TestAssert.True(
                    submenuPopup is not null,
                    "The application MenuItem template cannot display nested export formats.");
                TestAssert.True(
                    submenuPopup!.GetBindingExpression(Popup.IsOpenProperty) is not null,
                    "The nested menu popup does not follow the MenuItem open state.");

                var picker = new HdrColorPickerWindow("Smoke test", Vector4.One);
                TestAssert.Equal(Vector4.One, picker.Value);
                TestAssert.True(HdrColorPickerWindow.TryParseHex("#336699CC", out var parsed),
                    "RGBA HEX input was rejected.");
                TestAssert.Near(new Vector3(0.2f, 0.4f, 0.6f), new Vector3(parsed.X, parsed.Y, parsed.Z), 0.0001f);
                TestAssert.Near(0.8f, parsed.W, 0.0001f);
                picker.Close();
                var welcome = new FirstRunWelcomeWindow();
                var programIcon = welcome.FindName("ProgramIconImage") as Image;
                var databaseConsequence = welcome.FindName("DatabaseConsequenceText") as TextBlock;
                TestAssert.True(programIcon?.Source is not null,
                    "The startup welcome does not use the packaged program icon.");
                TestAssert.True(programIcon!.Source.ToString()?.Contains("ico_256.png", StringComparison.Ordinal) == true &&
                                programIcon.Width == 64 &&
                                programIcon.Height == 64 &&
                                programIcon.Parent is Grid,
                    "The startup welcome does not use the unboxed high-resolution 64px program icon.");
                TestAssert.Equal("https://github.com/AudemusN7/LEBioMorphFaceEditor",
                    FirstRunWelcomeWindow.TutorialUri.AbsoluteUri.TrimEnd('/'));
                TestAssert.True(databaseConsequence?.Text.Contains(
                        "installed game texture choices", StringComparison.Ordinal) == true &&
                    databaseConsequence.Text.Contains("index game assets", StringComparison.Ordinal) == true,
                    "The startup welcome does not explain operation without a Texture Database.");
                welcome.Close();
                var signedPicker = new HdrColorPickerWindow(
                    "Signed colour smoke test",
                    new Vector4(-0.5f, 0.25f, 1.5f, -0.25f));
                TestAssert.Equal(new Vector4(-0.5f, 0.25f, 1.5f, -0.25f), signedPicker.Value);
                signedPicker.Close();
                var backgroundPicker = new HdrColorPickerWindow(
                    "Background smoke test",
                    new Vector4(0.2f, 0.4f, 0.6f, 0.25f),
                    allowHdr: false);
                TestAssert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1), backgroundPicker.Value);
                backgroundPicker.Close();
                var message = new EditorMessageWindow(
                    "Delete Morph",
                    "Themed confirmation smoke test.",
                    confirmation: true,
                    primaryLabel: "Delete Morph");
                message.Close();
                var standaloneGame = new StandaloneImportGameWindow();
                standaloneGame.Close();
                var ronDestination = new RonImportDestinationWindow(
                    MorphFaceGame.LE2,
                    [new RonNpcArchetypeOption("ASA", "Asari")],
                    allowPlayer: true);
                TestAssert.Equal(SizeToContent.Height, ronDestination.SizeToContent);
                TestAssert.Equal("Asari", ((TextBlock)ronDestination.FindName("ArchetypeText")).Text);
                TestAssert.True(ronDestination.FindName("ArchetypeBox") is null,
                    "The NPC archetype should be presented as a read-only label.");
                ronDestination.Close();
                var standaloneName = new CloneMorphWindow(
                    "Imported_Player",
                    ["Existing_Player"],
                    importMode: true);
                TestAssert.Equal("Name Imported BioMorphFace", standaloneName.Title);
                standaloneName.Close();
                var standaloneUnsaved = new UnsavedChangesWindow(
                    "BIOG_MORPH_FACE.Imported_Player",
                    UnsavedChangesScope.StandaloneFace);
                TestAssert.Equal("Export…", ((Button)standaloneUnsaved.FindName("SaveButton")).Content);
                TestAssert.True(((TextBlock)standaloneUnsaved.FindName("SaveExplanationText")).Text
                        .Contains("read-only", StringComparison.OrdinalIgnoreCase),
                    "The standalone warning does not explain that the installed template is protected.");
                standaloneUnsaved.Close();
                var faceUnsaved = new UnsavedChangesWindow(
                    "BIOG_MORPH_FACE.SelectedFace",
                    UnsavedChangesScope.Face);
                TestAssert.Equal("Commit", ((Button)faceUnsaved.FindName("SaveButton")).Content);
                TestAssert.Equal("Don't Commit", ((Button)faceUnsaved.FindName("DiscardButton")).Content);
                TestAssert.True(((TextBlock)faceUnsaved.FindName("SaveExplanationText")).Text
                        .Contains("without saving the source PCC", StringComparison.OrdinalIgnoreCase),
                    "The face warning does not distinguish Commit from saving the package.");
                faceUnsaved.Close();
                var morphPackage = new SaveMorphToPccWindow(
                    "Face.pcc",
                    Path.GetFullPath("fixture.pcc"));
                morphPackage.Close();
                var le3Conversion = new ConvertMorphWindow(
                    MorphFaceGame.LE3,
                    "Face_Converted.pcc",
                    Path.GetFullPath("fixture.pcc"));
                le3Conversion.Close();
                var le2Conversion = new ConvertMorphWindow(
                    MorphFaceGame.LE2,
                    "Face_Converted.pcc",
                    Path.GetFullPath("fixture.pcc"));
                le2Conversion.Close();
                var actorInventory = new ActorAssignmentInventory(
                    MorphFaceGame.LE2, 1, "SelectedFace", "le2-human-male",
                    [ActorChoiceFixture(10, "NormandyGarrus", "BioPawn_10", true, true)]);
                var actorChooser = new ActorAssignmentWindow(actorInventory, ActorAssignmentMode.Morph);
                actorChooser.ShowInTaskbar = false;
                actorChooser.Opacity = 0;
                actorChooser.Show();
                actorChooser.UpdateLayout();
                actorChooser.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                application?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("HDR picker construction did not finish within ten seconds.");
        }
        if (failure is not null)
        {
            throw new Exception($"HDR picker failed during construction: {failure.Message}", failure);
        }
    }

    private static void HdrPickerControlsPreserveAndScale()
    {
        var picker = new HdrColorPickerWindow("HDR colour", new Vector4(2, 0.5f, 0.25f, 1));
        TestAssert.Equal(new Vector4(2, 0.5f, 0.25f, 1), picker.Value);
        var red = (Slider)picker.FindName("RedSlider")!;
        var brightness = (Slider)picker.FindName("BrightnessSlider")!;
        var intensity = (Slider)picker.FindName("IntensitySlider")!;
        var wheel = (MorphFaceEditor.Controls.ColorWheelControl)picker.FindName("Wheel")!;
        var hdrLabel = (TextBlock)picker.FindName("HdrIntensityLabel")!;
        var hdrPanel = (Grid)picker.FindName("HdrIntensityPanel")!;
        picker.ShowInTaskbar = false;
        picker.Opacity = 0;
        picker.Show();
        picker.UpdateLayout();
        var labelBounds = hdrLabel.TransformToAncestor(hdrPanel)
            .TransformBounds(new Rect(new Point(0, 0), hdrLabel.RenderSize));
        var sliderBounds = intensity.TransformToAncestor(hdrPanel)
            .TransformBounds(new Rect(new Point(0, 0), intensity.RenderSize));
        TestAssert.True(labelBounds.Left >= 0 && labelBounds.Right <= hdrPanel.ActualWidth,
            "The HDR label extends into the wheel or preview column.");
        TestAssert.Near((float)(sliderBounds.Left + sliderBounds.Width / 2),
            (float)(labelBounds.Left + labelBounds.Width / 2), 0.5f);
        TestAssert.Equal(HorizontalAlignment.Center, intensity.HorizontalAlignment);
        TestAssert.Equal(HorizontalAlignment.Center, hdrLabel.HorizontalAlignment);
        TestAssert.Equal(1d, red.Maximum);
        TestAssert.Equal(1d, brightness.Maximum);
        TestAssert.Equal(8d, intensity.Maximum);
        TestAssert.Near(2, (float)intensity.Value, 0.0001f);
        intensity.Value = 4;
        hdrLabel.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
        TestAssert.Equal("HDR: 4.000x", hdrLabel.Text);
        TestAssert.Near(new Vector3(4, 1, 0.5f),
            new Vector3(picker.Value.X, picker.Value.Y, picker.Value.Z), 0.0001f);
        brightness.Value = 0.5;
        TestAssert.Near(1, (float)wheel.Brightness, 0.0001f);
        TestAssert.Near(new Vector3(2, 0.5f, 0.25f),
            new Vector3(picker.Value.X, picker.Value.Y, picker.Value.Z), 0.0001f);
        brightness.Value = 0.1;
        intensity.Value = 8;
        TestAssert.Near(0.8f, (float)wheel.Brightness, 0.0001f);
        TestAssert.Near(new Vector3(0.8f, 0.2f, 0.1f),
            new Vector3(picker.Value.X, picker.Value.Y, picker.Value.Z), 0.0001f);
        brightness.Value = 0;
        TestAssert.Near(0, (float)wheel.Brightness, 0.0001f);
        picker.Close();

        var extended = new HdrColorPickerWindow("Signed colour",
            new Vector4(-0.5f, 0.25f, 1.5f, -0.25f), extendedSliders: true);
        TestAssert.Equal(new Vector4(-0.5f, 0.25f, 1.5f, -0.25f), extended.Value);
        TestAssert.Equal(-1d, ((Slider)extended.FindName("RedSlider")!).Minimum);
        var signedBrightness = (Slider)extended.FindName("BrightnessSlider")!;
        TestAssert.Equal(-1d, signedBrightness.Minimum);
        signedBrightness.Value = -1;
        TestAssert.Near(new Vector3(0.5f, -0.25f, -1.5f),
            new Vector3(extended.Value.X, extended.Value.Y, extended.Value.Z), 0.0001f);
        TestAssert.Equal(-0.25f, extended.Value.W);
        extended.Close();

        var overRange = new HdrColorPickerWindow("Older vector", new Vector4(9, -2, 1, 1.5f));
        TestAssert.Equal(new Vector4(9, -2, 1, 1.5f), overRange.Value);
        ((Slider)overRange.FindName("AlphaSlider")!).Value = 0.5;
        TestAssert.Equal(new Vector4(9, -2, 1, 0.5f), overRange.Value);
        overRange.Close();

        var background = new HdrColorPickerWindow("Background",
            new Vector4(0.2f, 0.4f, 0.6f, 0.25f), allowHdr: false);
        TestAssert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1), background.Value);
        TestAssert.Equal(Visibility.Collapsed,
            ((FrameworkElement)background.FindName("HdrIntensityPanel")!).Visibility);
        background.Close();
    }

    private static void MaterialVectorSubcategoriesRandomiseIndependently()
    {
        AssertGroups(
            new AsariFeatureMetadataCatalog(),
            ["ASA_HED_Addn_Colour", "ASA_HED_MakeUp_Eyes", "ASA_HED_Tatt_Colour"],
            "COMPLEXION COLOURS,MAKEUP COLOURS,TATTOOS COLOURS");
        AssertGroups(
            new SalarianFeatureMetadataCatalog(),
            ["SAL_HED_Addn_Colour", "SAL_HED_Tatt_Colour"],
            "COMPLEXION COLOURS,TATTOOS COLOURS");
        AssertGroups(
            new TurianFeatureMetadataCatalog(),
            ["TUR_HED_Addn_Colour", "TUR_HED_Tatt_Colour"],
            "COMPLEXION COLOURS,TATTOOS COLOURS");

        static void AssertGroups(IHeadEditorUiProfile profile, string[] names, string expected)
        {
            var session = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
                MorphFaceMaterialOverrides.Empty, ResolvedHeadMaterialSet.Empty);
            var vectors = names.Select(name => new MaterialVectorEditorViewModel(
                session,
                new MaterialParameterDefinition(name, name, "markings", MaterialParameterKind.Vector,
                    HeadMaterialFamily.Unknown),
                new StubColorDialog())).ToArray();
            var definition = profile.Categories.Single(value => value.Key == "markings");
            var category = new EditorFeatureCategoryViewModel(
                definition, [], [], vectors, [], profile,
                _ => new RelayCommand(() => { }));
            TestAssert.Equal(expected, string.Join(',', category.ColourGroups.Select(value => value.Label)));
            TestAssert.True(category.ColourGroups.All(value => value.RandomiseCommand is not null),
                $"{profile.GetType().Name} did not create a command for every vector subgroup.");
        }
    }

    private static void Le3HmmScalpRandomisationAppliesCorePair()
    {
        var family = MorphRandomisationCatalog.LoadEmbedded()
            .CompatibleMaterialDonors("le3-human-male")
            .Select(value => value.MaterialTextureFamilies.GetValueOrDefault("human-scalp"))
            .First(value => value is not null && value.TryGetValue("HED_Scalp_Spec", out var spec) &&
                            spec.Contains("GBL_ARM_ALL_Black", StringComparison.OrdinalIgnoreCase))!;
        const string packagePath = "working.pcc";
        var currentTextures = family.Keys.ToDictionary(
            name => name,
            name => new MaterialTextureBinding(name, CreateTestTexture(packagePath, $"Current.{name}", name)),
            StringComparer.OrdinalIgnoreCase);
        var materialIdentity = new AssetIdentity(packagePath, "Working.ScalpMaterial", 1, "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(materialIdentity), materialIdentity, "HMM_HED_PRONPC_MASTER_FACE_MAT",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(), new Dictionary<string, Vector4>(), currentTextures)
        {
            SupportedTextures = family.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        var session = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material }));
        var candidates = family.Values.Select(path =>
        {
            var occurrence = new TextureCatalogOccurrence(
                "installed.pcc", 42, 0, TextureCatalogOrigin.BaseGame, 1, 1,
                "PF_B8G8R8A8", "TEXTUREGROUP_Character", false, null);
            return new TextureCatalogCandidate(TextureCatalogGame.LE3, path, occurrence, [occurrence]);
        }).ToArray();
        using var editor = new MaterialEditorViewModel(
            session, new StubColorDialog(), new ImmediateTextureLoader(), packagePath, [],
            message => throw new Exception(message), new HumanMaleFeatureMetadataCatalog(),
            candidates, TextureCatalogProfile.Empty, true);

        var prepared = editor.PrepareRandomisationAsync(
                new Dictionary<string, float>(),
                new Dictionary<string, Vector4>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                    { ["human-scalp"] = family })
            .GetAwaiter().GetResult();

        TestAssert.Equal(1, prepared.AppliedTextureFamilies);
        TestAssert.True(prepared.Textures.ContainsKey("HED_Scalp_Diff") &&
                        prepared.Textures.ContainsKey("HED_Scalp_Norm"),
            "LE3 HMM scalp randomisation discarded its required Diff/Norm pair.");
    }

    private static void ManualHairScalpPromptKeepsRequiredPairWhenOptionalMapFails()
    {
        var family = MorphRandomisationCatalog.LoadEmbedded()
            .CompatibleMaterialDonors("le3-human-male")
            .Select(value => value.MaterialTextureFamilies.GetValueOrDefault("human-scalp"))
            .First(value => value is not null && value.TryGetValue("HED_Scalp_Spec", out var spec) &&
                            spec.Contains("GBL_ARM_ALL_Black", StringComparison.OrdinalIgnoreCase))!;
        const string packagePath = "working.pcc";
        var currentTextures = family.Keys.ToDictionary(
            name => name,
            name => new MaterialTextureBinding(name, CreateTestTexture(packagePath, $"Current.{name}", name)),
            StringComparer.OrdinalIgnoreCase);
        var materialIdentity = new AssetIdentity(packagePath, "Working.ScalpMaterial", 1, "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(materialIdentity), materialIdentity, "HMM_HED_PRONPC_MASTER_FACE_MAT",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(), new Dictionary<string, Vector4>(), currentTextures)
        {
            SupportedTextures = family.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        var session = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material }));
        var candidates = family.Values.Select(path =>
        {
            var occurrence = new TextureCatalogOccurrence(
                "installed.pcc", 42, 0, TextureCatalogOrigin.BaseGame, 1, 1,
                "PF_B8G8R8A8", "TEXTUREGROUP_Character", false, null);
            return new TextureCatalogCandidate(TextureCatalogGame.LE3, path, occurrence, [occurrence]);
        }).ToArray();
        using var editor = new MaterialEditorViewModel(
            session, new StubColorDialog(), new ImmediateTextureLoader("GBL_ARM_ALL_Black"), packagePath, [],
            message => throw new Exception(message), new HumanMaleFeatureMetadataCatalog(),
            candidates, TextureCatalogProfile.Empty, true);

        var prepared = editor.PrepareRandomisationAsync(
                new Dictionary<string, float>(),
                new Dictionary<string, Vector4>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                    { ["manual|human-scalp"] = family })
            .GetAwaiter().GetResult();

        TestAssert.Equal(1, prepared.AppliedTextureFamilies);
        TestAssert.True(prepared.FailedTextureFamilySignatures.Count == 0,
            "An unresolved optional scalp map rejected the manual prompt family.");
        TestAssert.True(prepared.Textures.ContainsKey("HED_Scalp_Diff") &&
                        prepared.Textures.ContainsKey("HED_Scalp_Norm"),
            "The manual prompt must still apply the required Diff/Norm pair.");
        TestAssert.True(!prepared.Textures.ContainsKey("HED_Scalp_Spec"),
            "The unresolved optional Mask should be omitted from the prompt result.");
    }

    private static void ExhaustedTextureRandomisationIsReported()
    {
        TestAssert.True(FaceEditorViewModel.WereAllTextureFamiliesRejected(
                eligibleSignatureCount: 2,
                excludedSignatureCount: 2,
                appliedTextureFamilies: 0),
            "Exhausting every eligible texture family was silently treated as a complete randomisation.");
        TestAssert.True(!FaceEditorViewModel.WereAllTextureFamiliesRejected(2, 1, 1),
            "A successful fallback texture family was incorrectly reported as exhausted.");
        TestAssert.True(!FaceEditorViewModel.WereAllTextureFamiliesRejected(0, 0, 0),
            "A numeric-only material randomisation was incorrectly reported as a texture failure.");
    }

    private static DecodedTextureAsset CreateTestTexture(string packagePath, string path, string parameterName) =>
        new(new AssetIdentity(packagePath, path, 42, "Texture2D"), 1, 1, [128, 128, 128, 255],
            "PF_B8G8R8A8", parameterName.EndsWith("_Diff", StringComparison.OrdinalIgnoreCase)
                ? TextureRole.Diffuse
                : TextureRole.Normal,
            TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, false, path);

    private sealed class ImmediateTextureLoader(string? failInstancedPathSubstring = null) : ITextureReferenceLoader
    {
        public Task<DecodedTextureAsset> LoadTextureAsync(
            string packagePath,
            string texturePath,
            MaterialParameterDefinition definition,
            CancellationToken cancellationToken = default)
        {
            if (failInstancedPathSubstring is not null && texturePath.Contains(
                    failInstancedPathSubstring, StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException($"Test texture '{texturePath}' is unavailable.");
            return Task.FromResult(CreateTestTexture(packagePath, texturePath, definition.Name));
        }
    }

    private static void MaterialEditorHidesSelectionColor()
    {
        var identity = TestFixtures.CreateIdentity("SelectionColorMaterial", "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "SelectionColorMaterial",
            HeadMaterialFamily.Eyes, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>
            {
                ["EyeColour"] = Vector4.One,
                ["SelectionColor"] = new(1, 0, 1, 1)
            },
            new Dictionary<string, MaterialTextureBinding>())
        {
            SupportedVectors = new HashSet<string>(["EyeColour", "SelectionColor"],
                StringComparer.OrdinalIgnoreCase)
        };
        var session = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [material.Key] = material
            }));
        using var editor = new MaterialEditorViewModel(
            session, new StubColorDialog(), new ImmediateTextureLoader(), string.Empty, [],
            message => throw new Exception(message), new HumanMaleFeatureMetadataCatalog());

        TestAssert.True(editor.Vectors.Any(value => value.Name == "EyeColour"),
            "The ordinary eye colour control was hidden with Selection Color.");
        TestAssert.True(editor.Vectors.All(value => value.Name != "SelectionColor"),
            "The Unreal Selection Color property remained visible in the material editor.");
        TestAssert.True(MaterialEditorViewModel.IsEngineEditorOnlyParameter("Selection Color") &&
                        MaterialEditorViewModel.IsEngineEditorOnlyParameter("Selection_Color"),
            "Selection Color spelling variants were not recognized as engine-only properties.");
    }

    private static void HumanMaleProfileOrganizesFeatures()
    {
        var profile = new HumanMaleFeatureMetadataCatalog();
        TestAssert.Equal(
            "Facial Structure,Head,Eyes,Nose,Mouth,Neck / Jaw,Additions",
            string.Join(',', profile.Categories.Select(category => category.Label)));
        TestAssert.Equal("SHAPE,EARS,CHEEKS", string.Join(',', profile.Categories
            .Single(category => category.Key == "head").SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("SHAPE,BROWS,SOCKETS,EYELIDS,LASHES,SURFACE", string.Join(',', profile.Categories
            .Single(category => category.Key == "eyes").SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("JAW,CHIN,NECK", string.Join(',', profile.Categories
            .Single(category => category.Key == "neck-jaw").SliderGroups.Select(group => group.Label)));

        var target = TestFixtures.CreateTarget();
        var kaidan = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("Kaiden", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Kaidan", kaidan.Label);
        TestAssert.Equal("facial-structure", kaidan.CategoryKey);
        TestAssert.Equal("character", kaidan.SubcategoryKey);

        var jacob = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("Jacob", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Jacob", jacob.Label);
        TestAssert.Equal("facial-structure", jacob.CategoryKey);
        TestAssert.Equal("character", jacob.SubcategoryKey);

        var baseHead = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("baseHead", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.True(!baseHead.IsVisible, "The proven zero-effect baseHead target remained visible.");

        var moustache = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("HIR_TimSelect", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Moustache", moustache.Label);
        TestAssert.Equal("mouth", moustache.CategoryKey);
        TestAssert.Equal("mouth-shape", moustache.SubcategoryKey);

        TestAssert.Equal("scalp", profile.GetMaterialSubcategory(
            "HED_Scalp_Mask_Scalar", MaterialParameterKind.Scalar));
        TestAssert.Equal("scalp", profile.GetMaterialSubcategory(
            "Highlight1Color", MaterialParameterKind.Vector));
        TestAssert.Equal("scalp", profile.GetMaterialSubcategory(
            "Highlight2Colour_Vector", MaterialParameterKind.Vector));
        var highlightSession = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty, ResolvedHeadMaterialSet.Empty);
        var highlightVector = new MaterialVectorEditorViewModel(
            highlightSession,
            new MaterialParameterDefinition("Highlight1Color", "Hair Highlight 1", "Additions",
                MaterialParameterKind.Vector, HeadMaterialFamily.Hair),
            new StubColorDialog());
        var additionsCategory = new EditorFeatureCategoryViewModel(
            profile.Categories.Single(value => value.Key == HumanMaleFeatureMetadataCatalog.Additions),
            [], [], [highlightVector], [], profile,
            _ => new RelayCommand(() => { }));
        TestAssert.Equal("HAIR COLOURS", additionsCategory.ColourGroups.Single().Label);
        TestAssert.Equal("additions", profile.GetMaterialCategory(
            "HED_Mask", MaterialParameterKind.Texture));
        TestAssert.Equal("additions", profile.GetMaterialCategory(
            "HED_Mask_Vector", MaterialParameterKind.Vector));
        TestAssert.Equal("addition", profile.GetMaterialSubcategory(
            "HED_Mask_Vector", MaterialParameterKind.Vector));
        TestAssert.True(!profile.IsMaterialVisible("Diffuseuse", MaterialParameterKind.Texture),
            "The HMM Diffuseuse texture remained visible.");
        TestAssert.True(!profile.IsMaterialVisible("CubeMap", MaterialParameterKind.Texture),
            "The human-eye reflection cube remained exposed as an editable 2D texture.");
        TestAssert.True(!profile.IsMaterialVisible("__PROShort01_Diffuse", MaterialParameterKind.Texture),
            "A fixed compiled PROShort01 sampler leaked into the editable material controls.");

        foreach (var hiddenName in new[] { "Formal", "Sarge", "Slick", "lashes" })
        {
            var hidden = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(hiddenName, 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
            TestAssert.True(!hidden.IsVisible, $"{hiddenName} remained visible in the Human Male profile.");
        }

        var browBack = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyes_browBack", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var browForward = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyes_browForward", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal(browBack.SortOrder + 1, browForward.SortOrder);

        var teethScalar = profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_Teeth_Scalar", MaterialParameterKind.Scalar));
        TestAssert.Equal("Teeth Material Strength", teethScalar.Label);

        MorphFeatureMetadata Describe(string name) => profile.Describe(
            new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(name, 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);

        var inertDroop = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyeShape_droop", 0), null,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.MetadataOnly, null), true);
        TestAssert.True(!inertDroop.IsVisible, "The inert HMM eyeShape_droop metadata remained visible.");
        TestAssert.Equal("Eyes Wide", Describe("eyes_Wide").Label);
        TestAssert.Equal("Shape Wide", Describe("eyes_Shape_wide").Label);
        TestAssert.Equal("Eye Corners Up", Describe("eyes_SlantUp").Label);
        TestAssert.Equal("mouth-shape", Describe("mouthShape_thin").SubcategoryKey);
        TestAssert.Equal("Mouth Thin", Describe("mouthShape_thin").Label);
        TestAssert.Equal(Describe("mouthShape_thin").SortOrder + 1, Describe("mouth_CornersDown").SortOrder);
        foreach (var name in new[] { "mouthShape_overBite", "mouthShape_underBite" })
        {
            TestAssert.Equal("lips", Describe(name).SubcategoryKey);
            TestAssert.True(Describe(name).SortOrder < Describe("mouth_LowerLipFat").SortOrder,
                $"{name} was not placed before Lower Lip Full.");
        }

        var faceMask = profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_Mask_Scalar", MaterialParameterKind.Scalar));
        var scalpMask = profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "Mask", MaterialParameterKind.Scalar));
        TestAssert.Equal("Face Mask Alpha Strength", faceMask.Label);
        TestAssert.Equal("Scalp Opacity", scalpMask.Label);
        TestAssert.Equal("scalp", profile.GetMaterialSubcategory("Mask", MaterialParameterKind.Scalar));
        TestAssert.Equal("Hair Diffuse Texture", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HAIR_ADDN_Diff", MaterialParameterKind.Texture)).Label);
        TestAssert.Equal("surface", profile.GetMaterialSubcategory(
            "Iris_Colour_Multiplier", MaterialParameterKind.Scalar));
        TestAssert.Equal("lashes", profile.GetMaterialSubcategory(
            "HED_Lash_Opac_Scalar", MaterialParameterKind.Scalar));
        TestAssert.Equal("Adam's Apple", Describe("neck_apple").Label);
        TestAssert.Equal("Pupil Small (Vestigial)", Describe("pupil_Small").Label);
        TestAssert.Equal("Pupil Large (Vestigial)", Describe("pupil_Large").Label);
        TestAssert.Equal("Ears Up", Describe("ears_Up").Label);
        TestAssert.Equal("Cheeks Forward", Describe("cheeks_Forward").Label);
        TestAssert.Equal("Nostrils Narrow", Describe("nose_nostrilsnarrow").Label);
        TestAssert.Equal("Lips Thin", Describe("mouth_lipsThin").Label);
        TestAssert.Equal("Teeth Separate", Describe("teeth_Seperate").Label);
        TestAssert.Equal("Jaw Wide", Describe("jaw_wide").Label);
        TestAssert.Equal("Neck Thin", Describe("neck_Thin").Label);
    }

    private static void HumanFemaleProfileOrganizesFeatures()
    {
        var profile = new HumanFemaleFeatureMetadataCatalog();
        var target = TestFixtures.CreateTarget();
        var hair = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("HAIR_pulledBackSlick", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Pulled Back · Slick", hair.Label);
        TestAssert.Equal("facial-structure", hair.CategoryKey);
        TestAssert.Equal("hair", hair.SubcategoryKey);
        TestAssert.True(hair.IsVisible, "The HMF pulled-back hair morph was hidden.");

        var iconic = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("race_iconic", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Iconic Shepard · Blend", iconic.Label);
        TestAssert.Equal("character", iconic.SubcategoryKey);

        var headIconic = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("Iconic", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Iconic Shepard · Head", headIconic.Label);
        TestAssert.Equal("character", headIconic.SubcategoryKey);

        var sleepy = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyeShape_sleepy", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Eye Shape Sleepy", sleepy.Label);
        TestAssert.Equal("shape-eye-shape", sleepy.SubcategoryKey);
        TestAssert.Equal("character-eye-shape",
            profile.Categories.Single(category => category.Key == "eyes").SliderGroups[0].Key);

        var ashleyShape = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyes_ashleyShape", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("character-eye-shape", ashleyShape.SubcategoryKey);
        TestAssert.Equal("CHARACTER SHAPES,RACE SHAPES,SHAPE / ROTATION",
            string.Join(',', profile.Categories.Single(category => category.Key == "eyes")
                .SliderGroups.Take(3).Select(group => group.Label)));
        TestAssert.Equal("CHARACTER SHAPES,RACE SHAPES,SHAPE / ROTATION,BROWS,SOCKETS,EYELIDS,LASHES,SURFACE",
            string.Join(',', profile.Categories.Single(category => category.Key == "eyes")
                .SliderGroups.Select(group => group.Label)));

        var mouthRace = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("mouthShape_yngAsn", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Asian · Young", mouthRace.Label);
        TestAssert.Equal("race-mouth", mouthRace.SubcategoryKey);
        TestAssert.Equal("CHARACTER SHAPES,RACE SHAPES,SHAPE",
            string.Join(',', profile.Categories.Single(category => category.Key == "mouth")
                .SliderGroups.Take(3).Select(group => group.Label)));

        foreach (var hiddenName in new[]
                 {
                     "Eastwood", "rollins", "HAIR_splitSide", "None", "mouth_cheekMass", "cheeks_gaunt",
                     "eyeShape_droop", "eyes_Shape_sleepy", "eyes_Shape_wide", "pupil_Large", "eyes_bagOut",
                     "eyes_lashAngle", "eyes_lashLength", "teeth_close", "teeth_Seperate",
                     "teeth_canineExtend", "teeth_Narrow", "neck_apple"
                 })
        {
            var hidden = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(hiddenName, 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
            TestAssert.True(!hidden.IsVisible, $"{hiddenName} remained visible in the Human Female profile.");
        }

        var makeup = profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_Makeup_Mask", MaterialParameterKind.Texture));
        TestAssert.Equal("Makeup Mask Texture", makeup.Label);
        TestAssert.Equal("additions", makeup.Group);
        TestAssert.Equal("makeup", profile.GetMaterialSubcategory("HED_Makeup_Mask", MaterialParameterKind.Texture));
        TestAssert.Equal("additions", profile.GetMaterialCategory("HED_Brow_Tint_Scalar", MaterialParameterKind.Scalar));
        TestAssert.Equal("makeup", profile.GetMaterialSubcategory("HED_Lips_Tint_Vector", MaterialParameterKind.Vector));
        TestAssert.Equal("material-hair", profile.GetMaterialSubcategory("Hightlight1Intensity", MaterialParameterKind.Scalar));
        TestAssert.Equal("scalp", profile.GetMaterialSubcategory("Highlight2Color", MaterialParameterKind.Vector));
        TestAssert.Equal("scalp", profile.GetMaterialSubcategory("Highlight1Colour_Vector", MaterialParameterKind.Vector));
        TestAssert.Equal("material-hair", profile.GetMaterialSubcategory("Highlight2Intensity", MaterialParameterKind.Scalar));
        TestAssert.Equal("material-hair", profile.GetMaterialSubcategory("Highlight1SpecExp_Scalar", MaterialParameterKind.Scalar));
        TestAssert.Equal("Eyeshadow Strength", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_Brow_Tint_Scalar", MaterialParameterKind.Scalar)).Label);
        TestAssert.Equal("Eyeshadow Colour", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_Brow_Tint_Vector", MaterialParameterKind.Vector)).Label);
        TestAssert.Equal("Makeup Strength", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_EyeShadow_Tint_Scalar", MaterialParameterKind.Scalar)).Label);
        TestAssert.Equal("Makeup Colour", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_EyeShadow_Tint_Vector", MaterialParameterKind.Vector)).Label);
        TestAssert.Equal("Primary Brow Colour", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_Addn_Colour_Vector", MaterialParameterKind.Vector)).Label);
        TestAssert.Equal("Secondary Brow Colour", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "blonde", MaterialParameterKind.Vector)).Label);
        var cornerSlant = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyes_SlantUp", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var shapeSlant = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyeShape_SlantUp", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Eye Corners Up", cornerSlant.Label);
        TestAssert.Equal("Eye Shape Slant Up", shapeSlant.Label);
        TestAssert.Equal("surface", profile.GetMaterialSubcategory(
            "HED_Lash_Opac_Scalar", MaterialParameterKind.Scalar));
        TestAssert.True(
            profile.GetMaterialSortOrder("HED_Lash_Opac_Scalar", MaterialParameterKind.Scalar) >
            profile.GetMaterialSortOrder("HED_EYE_FX_Scalar", MaterialParameterKind.Scalar),
            "HMF Lash Opacity was not moved to the bottom of Eye Surface.");
        TestAssert.True(
            profile.GetMaterialSortOrder("HED_EyeShadow_Tint_Scalar", MaterialParameterKind.Scalar) <
            profile.GetMaterialSortOrder("HED_Brow_Tint_Scalar", MaterialParameterKind.Scalar),
            "Makeup Strength was not placed first in the Makeup category.");
        var eyeballsNarrow = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyes_RotateIn", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var eyeballsWide = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyes_RotateOut", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Eyeballs Narrow", eyeballsNarrow.Label);
        TestAssert.Equal("Eyeballs Wide", eyeballsWide.Label);
        TestAssert.True(eyeballsNarrow.SortOrder < profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue("eyes_small", 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true).SortOrder &&
            eyeballsWide.SortOrder < profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue("eyes_small", 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true).SortOrder,
            "HMF eyeball width controls were not moved ahead of eye-shape controls.");
    }

    private static void Le3HumanMaleProfileOrganizesFeatures()
    {
        var profile = MorphFaceProfileRegistry.CreateDefault().Profiles
            .Single(value => value.Key == "le3-human-male").UiProfile;
        var target = TestFixtures.CreateTarget();
        foreach (var (name, label) in new[]
                 {
                     ("pupil_Small", "Pupil Small (Vestigial)"),
                     ("pupil_Large", "Pupil Large (Vestigial)")
                 })
        {
            var metadata = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(name, 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
            TestAssert.Equal(label, metadata.Label);
            TestAssert.Equal("eyes", metadata.CategoryKey);
            TestAssert.Equal("eye-shape", metadata.SubcategoryKey);
            TestAssert.True(metadata.IsVisible, $"{name} was removed instead of marked vestigial.");
        }

        foreach (var name in new[] { "eyes_Large", "eyeShape_droop", "eyeShape_sleepy" })
        {
            var metadata = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(name, 0), null,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.MetadataOnly, null), true);
            TestAssert.True(!metadata.IsVisible, $"Inert LE3 metadata '{name}' remained visible.");
        }

        TestAssert.Equal("Addition Specular Power", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_Addn_SPwr_Add_Scalar", MaterialParameterKind.Scalar)).Label);
        TestAssert.Equal("Primary Addition Colour", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "HED_Addn_Colour_Vector", MaterialParameterKind.Vector)).Label);
        TestAssert.Equal("Eye Transmission Colour", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "Tmission_Color", MaterialParameterKind.Vector)).Label);
    }

    private static void Le3HumanFemaleProfileOrganizesFeatures()
    {
        var profile = MorphFaceProfileRegistry.CreateDefault().Profiles
            .Single(value => value.Key == "le3-human-female").UiProfile;
        var target = TestFixtures.CreateTarget();
        foreach (var name in new[] { "Jack", "Kasumi", "Miranda" })
        {
            var metadata = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(name, 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
            TestAssert.Equal("facial-structure", metadata.CategoryKey);
            TestAssert.Equal("character", metadata.SubcategoryKey);
            TestAssert.True(metadata.IsVisible, $"LE3 HMF character target '{name}' was hidden.");
        }

        var hairTarget = new MorphTargetAsset(
            target.Source,
            [
                new MorphTargetLod(0, 3, []),
                new MorphTargetLod(1, 3, [new MorphVertexDelta(0, Vector3.UnitX * 0.02f, Vector3.Zero)])
            ],
            []);
        var splitSide = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("HAIR_splitSide", 0), hairTarget,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.True(!splitSide.IsVisible, "The malformed LE3 HMF split-side target remained visible.");
        TestAssert.Equal("hair", splitSide.SubcategoryKey);
        foreach (var name in new[]
                 {
                     "HAIR_centerPart", "HAIR_pulledBackBig", "HAIR_pulledBackSlick",
                     "HAIR_sidePart", "HAIR_slickWidowsPeak", "HAIR_splitSide"
                 })
        {
            var hidden = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(name, 0), hairTarget,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
            TestAssert.True(!hidden.IsVisible, $"Malformed LE3 HMF target '{name}' remained visible.");
        }
        TestAssert.Equal("HAIR", profile.Categories
            .Single(category => category.Key == "facial-structure").SliderGroups
            .Single(group => group.Key == "hair").Label);
    }

    private static void MetadataTextInheritsAcrossProfiles()
    {
        var skinTone = HumanMaterialProfiles.Describe("SkinTone", MaterialParameterKind.Vector);
        TestAssert.Equal("Skin Tone", new HumanMaleFeatureMetadataCatalog().DescribeMaterial(skinTone).Label);
        TestAssert.Equal("Skin Tone", new HumanFemaleFeatureMetadataCatalog().DescribeMaterial(skinTone).Label);
        TestAssert.Equal("Skin Colour", new VorchaFeatureMetadataCatalog().DescribeMaterial(skinTone).Label);

        var detachedVorcha = new DetachedMeshFeatureMetadataCatalog().DescribeMaterial(
            skinTone with { Name = MaterialParameterControlKey.Create("vorcha", "SkinTone") });
        TestAssert.Equal("Skin Colour", detachedVorcha.Label);

        var profiles = MorphFaceProfileRegistry.CreateDefault().Profiles;
        MorphFeatureMetadata Describe(string profileKey, string name) =>
            profiles.Single(profile => profile.Key == profileKey).UiProfile.Describe(
                new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                    new MorphFeatureValue(name, 0), TestFixtures.CreateTarget(),
                    MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);

        TestAssert.Equal("mouth_Forward", Describe("le2-asari", "mouth_Forward").Description);
        TestAssert.Equal("mouth_Forward · Has almost no visible effect, recommended to leave this value alone",
            Describe("le3-asari", "mouth_Forward").Description);
        TestAssert.Equal("HAIR_centerPart", Describe("le2-human-female", "HAIR_centerPart").Description);
        TestAssert.Equal("HAIR_centerPart · LE3 slider hidden due to this target being broken",
            Describe("le3-human-female", "HAIR_centerPart").Description);
    }

    private static void DetachedMeshProfileUsesRacialMaterialPresentation()
    {
        var detached = new DetachedMeshFeatureMetadataCatalog();
        var cases = new (string Name, MaterialParameterKind Kind, IHeadEditorUiProfile Species)[]
        {
            ("ASA_HED_MakeUp_Eyes", MaterialParameterKind.Vector, new AsariFeatureMetadataCatalog()),
            ("SAL_HED_Tatt", MaterialParameterKind.Texture, new SalarianFeatureMetadataCatalog()),
            ("TUR_HED_Diff_Tint_Teeth", MaterialParameterKind.Vector, new TurianFeatureMetadataCatalog()),
            ("KRO_HED_Shell_Grad_Vector", MaterialParameterKind.Vector, new KroganFeatureMetadataCatalog()),
            ("BAT_HED_Neck_Grad_Vector", MaterialParameterKind.Vector, new BatarianFeatureMetadataCatalog()),
            ("Tattoo_Color", MaterialParameterKind.Vector, new VorchaFeatureMetadataCatalog())
        };

        foreach (var (name, kind, species) in cases)
        {
            var definition = HumanMaterialProfiles.Describe(name, kind);
            var expected = species.DescribeMaterial(definition);
            var actual = detached.DescribeMaterial(definition);
            TestAssert.True(actual.Label == expected.Label && actual.Group == expected.Group,
                $"Detached presentation for '{name}' did not reuse its PCC label/category.");
            TestAssert.Equal(species.GetMaterialSubcategory(name, kind),
                detached.GetMaterialSubcategory(name, kind));
            TestAssert.Equal(species.GetMaterialSortOrder(name, kind),
                detached.GetMaterialSortOrder(name, kind));

            var category = detached.Categories.Single(value => value.Key == actual.Group);
            var subcategory = detached.GetMaterialSubcategory(name, kind);
            TestAssert.True(category.SliderGroups.Any(value => value.Key == subcategory),
                $"Detached category '{actual.Group}' omitted subcategory '{subcategory}'.");
        }
    }

    private static void AsariProfileOrganizesFeatures()
    {
        var profile = new AsariFeatureMetadataCatalog();
        TestAssert.Equal(
            "Facial Structure,Head,Eyes,Nose,Mouth / Jaw,Markings",
            string.Join(',', profile.Categories.Select(category => category.Label)));

        var target = TestFixtures.CreateTarget();
        var crest = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("tentacle_long", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Head Crest Long", crest.Label);
        TestAssert.Equal("facial-structure", crest.CategoryKey);
        TestAssert.Equal("head-crest", crest.SubcategoryKey);
        TestAssert.Equal(0f, crest.Minimum);
        TestAssert.Equal(1f, crest.Maximum);

        var cheek = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("cheeks_gaunt", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("head", cheek.CategoryKey);
        TestAssert.Equal("cheeks", cheek.SubcategoryKey);

        var jaw = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("jaw_wide", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("mouth", jaw.CategoryKey);
        TestAssert.Equal("jaw", jaw.SubcategoryKey);

        var nose = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("nose_forward", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("shape", nose.SubcategoryKey);
        TestAssert.Equal("SHAPE", profile.Categories.Single(category => category.Key == "nose")
            .SliderGroups[0].Label);
        TestAssert.Equal("CHEEKS,SURFACE", string.Join(',', profile.Categories
            .Single(category => category.Key == "head").SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("MOUTH,LIPS,TEETH,JAW", string.Join(',', profile.Categories
            .Single(category => category.Key == "mouth").SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("SHAPE,BROWS,SOCKETS,SURFACE", string.Join(',', profile.Categories
            .Single(category => category.Key == "eyes").SliderGroups.Select(group => group.Label)));

        TestAssert.Equal("Cheeks Back,Cheeks Forward,Cheeks Down,Cheeks Up,Cheeks Narrow,Cheeks Wide,Cheeks Gaunt", OrderedLabels(
            "cheeks_Back", "cheeks_Down", "cheeks_Narrow", "cheeks_Forward",
            "cheeks_gaunt", "cheeks_Up", "cheeks_Wide"));
        TestAssert.Equal("Head Crest Short,Head Crest Long,Head Crest Large", OrderedLabels(
            "tentacle_Large", "tentacle_long", "tentacle_Short"));
        TestAssert.Equal("Eyes Back,Eyes Forward,Eyes Down,Eyes Up", OrderedLabels(
            "eyes_Back", "eyes_Down", "eyes_Forward", "eyes_Up"));
        TestAssert.Equal("Brows Back,Brows Forward,Brows Down,Brows Up", OrderedLabels(
            "eyes_browBack", "eyes_browDown", "eyes_browForward", "eyes_browUp"));
        TestAssert.Equal("Eyes Small,Eyes Large,Eyes Narrow,Eyes Wide", OrderedLabels(
            "eyes_Large", "eyes_narrow", "eyes_small", "eyes_Wide"));
        TestAssert.Equal("Nose Down,Nose Up,Nose Bottom In,Nose Bottom Out,Nose Top In,Nose Top Out", OrderedLabels(
            "nose_BottomIn", "nose_BottomOut", "nose_Down", "nose_topIn", "nose_topOut", "nose_Up"));
        TestAssert.Equal("Nose Bridge In,Nose Bridge Out,Nose Bridge Narrow,Nose Bridge Wide", OrderedLabels(
            "nose_BridgeIn", "nose_BridgeNarrow", "nose_BridgeOut", "nose_BridgeWide"));
        TestAssert.Equal("Nose Tip Down,Nose Tip Narrow,Nose Tip Wide", OrderedLabels(
            "nose_TipDown", "nose_tipNarrow", "nose_tipWide"));
        TestAssert.Equal("Nostrils Narrow,Nostrils Wide", OrderedLabels(
            "nose_nostrilsnarrow", "nose_nostrilsWide"));
        TestAssert.Equal("Mouth Back,Mouth Forward,Mouth Down,Mouth Up,Mouth Narrow,Mouth Wide,Mouth Corners Up", OrderedLabels(
            "mouth_Back", "mouth_CornersUp", "mouth_Down", "mouth_Forward",
            "mouth_Narrow", "mouth_Up", "mouth_Wide"));
        TestAssert.Equal("Chin Back,Chin Forward,Jaw Forward,Jaw Wide", OrderedLabels(
            "jaw_chinBack", "jaw_chinForward", "jaw_Forward", "jaw_wide"));
        TestAssert.Equal("Upper Lip Full,Lower Lip Full", OrderedLabels(
            "mouth_LowerLipFat", "mouth_upperLipFat"));
        TestAssert.Equal("Teeth Down,Teeth Up", OrderedLabels("teeth_Down", "teeth_Up"));

        foreach (var name in new[] { "nose_BottomIn", "nose_BottomOut", "nose_topIn", "nose_topOut" })
        {
            var metadata = Describe(name);
            TestAssert.Equal("shape", metadata.SubcategoryKey);
        }
        TestAssert.Equal("Nostrils Narrow", Describe("nose_nostrilsnarrow").Label);

        foreach (var hiddenName in new[]
                 {
                     "baseHead", "lashes", "jaw_narrow", "mouth_CornersDown",
                     "nose_TipUp", "eyes_Big", "race_iconic"
                 })
        {
            var hidden = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(hiddenName, 0), null,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.MetadataOnly, null), true);
            TestAssert.True(!hidden.IsVisible, $"{hiddenName} remained visible in the Asari profile.");
        }

        var tattoo = profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "ASA_HED_Tatt_Colour", MaterialParameterKind.Vector));
        TestAssert.Equal("Tattoo Colour", tattoo.Label);
        TestAssert.Equal("markings", tattoo.Group);
        TestAssert.Equal("tattoos", profile.GetMaterialSubcategory(
            "ASA_HED_Tatt_Colour", MaterialParameterKind.Vector));
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "ASA_HED_MakeUp_Eyes", MaterialParameterKind.Vector));
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "ASA_HED_MakeUp_Lips", MaterialParameterKind.Vector));
        TestAssert.Equal("Makeup Region",
            profile.DescribeMaterial(HumanMaterialProfiles.Describe(
                "ASA_HED_Makeup_Blender_Vector", MaterialParameterKind.Vector)).Label);
        TestAssert.Equal("eyes", profile.GetMaterialCategory("U_Offset", MaterialParameterKind.Scalar));
        TestAssert.Equal("eyes", profile.GetMaterialCategory("V_Offset", MaterialParameterKind.Scalar));
        TestAssert.Equal("surface", profile.GetMaterialSubcategory("U_Offset", MaterialParameterKind.Scalar));
        TestAssert.Equal("surface", profile.GetMaterialSubcategory(
            "HED_Lash_Opac_Scalar", MaterialParameterKind.Scalar));
        TestAssert.True(
            profile.GetMaterialSortOrder("HED_Lash_Opac_Scalar", MaterialParameterKind.Scalar) >
            profile.GetMaterialSortOrder("HED_EYE_FX_Scalar", MaterialParameterKind.Scalar),
            "ASA Lash Opacity was not moved to the bottom of Eye Surface.");
        var asariProfiles = MorphFaceProfileRegistry.CreateDefault().Profiles
            .Where(value => value.Key.EndsWith("-asari", StringComparison.Ordinal))
            .ToArray();
        foreach (var workingProfile in asariProfiles.Where(value => value.Game != MorphFaceGame.LE3))
        {
            var workingMouthForward = workingProfile.UiProfile.Describe(
                new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                    new MorphFeatureValue("mouth_Forward", 0), target,
                    MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
            TestAssert.Equal("Mouth Forward", workingMouthForward.Label);
        }
        var le3Profile = asariProfiles
            .Single(value => value.Key == "le3-asari").UiProfile;
        var le3MouthForward = le3Profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("mouth_Forward", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Mouth Forward (Vestigial)", le3MouthForward.Label);
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "ASA_HED_Addn", MaterialParameterKind.Texture));
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "ASA_HED_Mask", MaterialParameterKind.Texture));
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "ASA_HED_Addn_Mask_Scalar", MaterialParameterKind.Scalar));
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "ASA_HED_Addn_Colour_Scalar", MaterialParameterKind.Scalar));
        var teethMask = profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "Mask", MaterialParameterKind.Scalar));
        TestAssert.Equal("Teeth Opacity", teethMask.Label);
        TestAssert.Equal("mouth", profile.GetMaterialCategory("Mask", MaterialParameterKind.Scalar));
        TestAssert.Equal("teeth", profile.GetMaterialSubcategory("Mask", MaterialParameterKind.Scalar));
        TestAssert.True(teethMask.Description.Contains("below 0.33", StringComparison.Ordinal),
            "The Asari teeth-mask tooltip did not explain its threshold.");

        var humanUi = new HumanMaleFeatureMetadataCatalog();
        foreach (var definition in HumanMaterialProfiles.Definitions.Where(value =>
                     value.Family is HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes))
        {
            TestAssert.Equal(humanUi.DescribeMaterial(definition).Label,
                profile.DescribeMaterial(definition).Label);
            TestAssert.Equal(humanUi.GetMaterialSortOrder(definition.Name, definition.Kind),
                profile.GetMaterialSortOrder(definition.Name, definition.Kind));
        }

        var specular = profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "ASA_HED_SPwr_Add_Scalar", MaterialParameterKind.Scalar));
        TestAssert.Equal(0f, specular.Minimum);
        TestAssert.Equal(10f, specular.Maximum);
        TestAssert.Equal("surface", profile.GetMaterialSubcategory(
            "ASA_HED_SPwr_Add_Scalar", MaterialParameterKind.Scalar));

        foreach (var definition in HumanMaterialProfiles.Definitions
                     .Where(value => value.Family == HeadMaterialFamily.AsariSkin))
        {
            var label = profile.DescribeMaterial(definition).Label;
            foreach (var shorthand in new[] { "ASA", "HED", "Addn", "TMis", "TClr", "SPwr" })
            {
                TestAssert.True(!label.Contains(shorthand, StringComparison.OrdinalIgnoreCase),
                    $"Asari label '{label}' retained shorthand '{shorthand}'.");
            }
        }

        MorphFeatureMetadata Describe(string name) => profile.Describe(
            new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(name, 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);

        string OrderedLabels(params string[] names) => string.Join(',', names
            .Select(Describe)
            .OrderBy(metadata => metadata.SortOrder)
            .ThenBy(metadata => metadata.Label, StringComparer.OrdinalIgnoreCase)
            .Select(metadata => metadata.Label));
    }

    private static void SalarianProfileOrganizesFeatures()
    {
        var profile = new SalarianFeatureMetadataCatalog();
        TestAssert.Equal(
            "Facial Structure,Head,Eyes,Mouth / Jaw,Markings",
            string.Join(',', profile.Categories.Select(category => category.Label)));

        var target = TestFixtures.CreateTarget();
        var ring = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("ring_forward", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Cranial Ring Forward", ring.Label);
        TestAssert.Equal("facial-structure", ring.CategoryKey);
        TestAssert.Equal("cranial-ring", ring.SubcategoryKey);

        var cheek = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("face_thin", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("head", cheek.CategoryKey);
        TestAssert.Equal("cheeks", cheek.SubcategoryKey);
        TestAssert.Equal("CHEEKS,SURFACE", string.Join(',', profile.Categories
            .Single(category => category.Key == "head").SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("SHAPE,POSITION", string.Join(',', profile.Categories
            .Single(category => category.Key == "eyes").SliderGroups.Select(group => group.Label)));

        var eyes = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyes_narrow", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("eyes", eyes.CategoryKey);
        TestAssert.Equal("shape", eyes.SubcategoryKey);

        TestAssert.Equal("Eyes Back,Eyes Forward,Eyes Down,Eyes Up", OrderedLabels(
            "eyes_Up", "eyes_Down", "eyes_Back", "eyes_Forward"));
        TestAssert.Equal("Eyes Narrow,Eyes Wide", OrderedLabels("eyes_narrow", "eyes_Wide"));
        TestAssert.Equal("Cranial Ring Back,Cranial Ring Forward,Cranial Ring Small,Cranial Ring Large", OrderedLabels(
            "ring_Back", "ring_Forward", "ring_Large", "ring_Small"));
        TestAssert.Equal("Face Thin,Face Slim", OrderedLabels("face_thin", "shape_skinny"));
        TestAssert.Equal("Mouth Back,Mouth Forward,Mouth Down,Mouth Up,Mouth Narrow,Mouth Wide", OrderedLabels(
            "mouth_Up", "mouth_Down", "mouth_Back", "mouth_Forward", "mouth_Narrow", "mouth_Wide"));
        TestAssert.Equal("Lips Thin,Lips Full,Lips Small,Lips Large,Lips Forward", OrderedLabels(
            "mouth_lipsFat", "mouth_lipsThin", "mouth_lipsLarge", "mouth_lipsSmall", "mouth_lipsForward"));
        TestAssert.Equal("Overbite,Underbite", OrderedLabels("mouth_overBite", "mouth_underBite"));
        TestAssert.Equal(
            "Jaw Back,Jaw Forward,Jaw Lower,Jaw Raise,Chin Down,Chin Up,Chin In,Chin Out,Jowls",
            OrderedLabels(
                "mouth_jawBack", "mouth_jawForward", "mouth_jawRaise", "mouth_jawLower",
                "jaw_chinUp", "jaw_chinDown", "jaw_chinIn", "jaw_chinOut", "jaw_Jowls"));

        var lips = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("mouth_lipsFat", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Lips Full", lips.Label);
        TestAssert.Equal("mouth", lips.CategoryKey);
        TestAssert.Equal("lips", lips.SubcategoryKey);

        foreach (var hiddenName in new[] { "shape_chubby", "neck_correction" })
        {
            var hidden = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(hiddenName, 0), null,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.MetadataOnly, null), true);
            TestAssert.True(!hidden.IsVisible, $"{hiddenName} remained visible in the Salarian profile.");
        }

        TestAssert.Equal("Complexion Strength", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "SAL_HED_Addn_Blend_Scalar", MaterialParameterKind.Scalar)).Label);
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "SAL_HED_Mask", MaterialParameterKind.Texture));
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "SAL_HED_Tatt_Colour", MaterialParameterKind.Vector));
        TestAssert.Equal("tattoos", profile.GetMaterialSubcategory(
            "SAL_HED_Tatt_Colour", MaterialParameterKind.Vector));
        TestAssert.Equal("eyes", profile.GetMaterialCategory(
            "SAL_HED_EYE_Iris_Vector", MaterialParameterKind.Vector));

        foreach (var definition in HumanMaterialProfiles.Definitions.Where(value =>
                     value.Family is HeadMaterialFamily.SalarianSkin or HeadMaterialFamily.SalarianEyes))
        {
            var label = profile.DescribeMaterial(definition).Label;
            foreach (var shorthand in new[] { "SAL", "HED", "Addn", "TMis" })
            {
                TestAssert.True(!label.Contains(shorthand, StringComparison.OrdinalIgnoreCase),
                    $"Salarian label '{label}' retained shorthand '{shorthand}'.");
            }
        }

        string OrderedLabels(params string[] names) => string.Join(',', names
            .Select(name => profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(name, 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true))
            .OrderBy(metadata => metadata.SortOrder)
            .ThenBy(metadata => metadata.Label, StringComparer.OrdinalIgnoreCase)
            .Select(metadata => metadata.Label));
    }

    private static void TurianProfileOrganizesFeatures()
    {
        var profile = new TurianFeatureMetadataCatalog();
        TestAssert.Equal("Facial Structure,Head,Eyes,Nose,Mouth,Markings",
            string.Join(',', profile.Categories.Select(category => category.Label)));
        TestAssert.Equal("HEAD SPIKES,MANDIBLES", string.Join(',', profile.Categories
            .Single(category => category.Key == "facial-structure").SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("SHAPE,BROWS", string.Join(',', profile.Categories
            .Single(category => category.Key == "eyes").SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("SHAPE,TEETH", string.Join(',', profile.Categories
            .Single(category => category.Key == "mouth").SliderGroups.Select(group => group.Label)));
        var target = TestFixtures.CreateTarget();
        var targetNames = new[]
        {
            "brow_Back", "brow_Down", "brow_Forward", "brow_Up",
            "cheeks_Down", "cheeks_Up",
            "eyes_Back", "eyes_Big", "eyes_Forward", "eyes_narrow", "eyes_small", "eyes_Wide",
            "headSpikes_Long", "headSpikes_Short", "head_ScaleUp",
            "mandible_extend", "mandible_Flare", "mandible_long", "mandible_retract",
            "mandible_Rotate", "mandible_short", "mandible_Thick",
            "mouth_Back", "mouth_Down", "mouth_Forward", "mouth_Narrow", "mouth_Up", "mouth_Wide",
            "neck_Thin",
            "nose_Down", "nose_In", "nose_Long", "nose_Narrow", "nose_Out", "nose_Short",
            "nose_Up", "nose_Wide"
        };
        var controls = targetNames.Select(name => profile.Describe(
                new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                    new MorphFeatureValue(name, 0), target,
                    MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true))
            .ToArray();
        TestAssert.Equal(37, controls.Length);
        TestAssert.Equal(37, controls.Select(control => control.SortOrder).Distinct().Count());
        TestAssert.True(controls.All(control => control.IsVisible),
            "One or more authored Turian target controls were hidden.");

        AssertGroup("facial-structure", "mandibles", "Mandibles Short,Mandibles Long,Mandibles Retract,Mandibles Extend,Mandibles Flare,Mandibles Rotate,Mandibles Thick");
        AssertGroup("facial-structure", "head-spikes", "Head Scale Up,Head Spikes Short,Head Spikes Long");
        AssertGroup("head", "cheeks", "Cheeks Down,Cheeks Up");
        AssertGroup("head", "neck", "Neck Thin");
        AssertGroup("eyes", "brows", "Brows Back,Brows Forward,Brows Down,Brows Up");
        AssertGroup("eyes", "shape", "Eyes Back,Eyes Forward,Eyes Small,Eyes Large,Eyes Narrow,Eyes Wide");
        AssertGroup("nose", "shape", "Nose Down,Nose Up,Nose In,Nose Out,Nose Short,Nose Long,Nose Narrow,Nose Wide");
        AssertGroup("mouth", "shape", "Mouth Back,Mouth Forward,Mouth Down,Mouth Up,Mouth Narrow,Mouth Wide");
        var metadata = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("eyes_Large", 0), null,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.MetadataOnly, null), true);
        TestAssert.True(!metadata.IsVisible, "Turian eyes_Large metadata remained visible.");
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "TUR_HED_Mask", MaterialParameterKind.Texture));
        TestAssert.Equal("markings", profile.GetMaterialCategory("TUR_HED_Tatt_Colour", MaterialParameterKind.Vector));
        TestAssert.Equal("eyes", profile.GetMaterialCategory("TUR_EYE_Lens_Norm", MaterialParameterKind.Texture));
        TestAssert.Equal("mouth", profile.GetMaterialCategory("Mask", MaterialParameterKind.Scalar));
        TestAssert.Equal("shape", profile.GetMaterialSubcategory("Mask", MaterialParameterKind.Scalar));
        var teethColour = profile.DescribeMaterial(HumanMaterialProfiles.Describe(
            "TUR_HED_Diff_Tint_Teeth", MaterialParameterKind.Vector));
        TestAssert.Equal("Teeth Colour", teethColour.Label);
        TestAssert.Equal("mouth", teethColour.Group);
        TestAssert.Equal("teeth", profile.GetMaterialSubcategory(
            "TUR_HED_Diff_Tint_Teeth", MaterialParameterKind.Vector));
        var teethOpacity = profile.DescribeMaterial(HumanMaterialProfiles.Definitions.Single(definition =>
            definition.Family == HeadMaterialFamily.TurianSkin && definition.Name == "Mask"));
        TestAssert.Equal("Teeth Opacity", teethOpacity.Label);
        foreach (var definition in HumanMaterialProfiles.Definitions.Where(value =>
                     value.Family is HeadMaterialFamily.TurianSkin or HeadMaterialFamily.TurianEyes))
        {
            var label = profile.DescribeMaterial(definition).Label;
            foreach (var shorthand in new[] { "HED", "Addn", "TMis", "SPwr" })
            {
                TestAssert.True(!label.Contains(shorthand, StringComparison.OrdinalIgnoreCase),
                    $"Turian label '{label}' retained shorthand '{shorthand}'.");
            }
        }

        void AssertGroup(string category, string subcategory, string expected)
        {
            var actual = string.Join(',', controls
                .Where(control => control.CategoryKey == category && control.SubcategoryKey == subcategory)
                .OrderBy(control => control.SortOrder)
                .ThenBy(control => control.Label, StringComparer.OrdinalIgnoreCase)
                .Select(control => control.Label));
            TestAssert.Equal(expected, actual);
        }
    }

    private static void KroganProfileOrganizesFeatures()
    {
        var profile = new KroganFeatureMetadataCatalog();
        TestAssert.Equal("Facial Structure,Head,Eyes,Mouth / Jaw,Markings",
            string.Join(',', profile.Categories.Select(category => category.Label)));
        TestAssert.Equal("CHARACTER,HEAD PLATES", string.Join(',', profile.Categories
            .Single(category => category.Key == KroganFeatureMetadataCatalog.FacialStructure)
            .SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("SHAPE,NOSE,NECK,SURFACE", string.Join(',', profile.Categories
            .Single(category => category.Key == KroganFeatureMetadataCatalog.Head)
            .SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("SHAPE,POSITION", string.Join(',', profile.Categories
            .Single(category => category.Key == KroganFeatureMetadataCatalog.Eyes)
            .SliderGroups.Select(group => group.Label)));

        var target = TestFixtures.CreateTarget();
        var shell = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("shell_forwardSlant", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var spike = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("spike_flare", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var wrex = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("Wrex", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("head-plates", shell.SubcategoryKey);
        TestAssert.Equal("head-plates", spike.SubcategoryKey);
        TestAssert.Equal("character", wrex.SubcategoryKey);
        TestAssert.Equal("facial-structure", wrex.CategoryKey);

        var outerThin = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("head_thin", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var innerThin = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("shell_thin", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var neck = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("shape_thin", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var nose = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("nose_Narrow", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Outer Plates · Thin", outerThin.Label);
        TestAssert.Equal("Inner Plates · Thin", innerThin.Label);
        TestAssert.Equal("head-plates", outerThin.SubcategoryKey);
        TestAssert.Equal("head-plates", innerThin.SubcategoryKey);
        TestAssert.Equal("shape", neck.SubcategoryKey);
        TestAssert.Equal("head", nose.CategoryKey);
        TestAssert.Equal("nose", nose.SubcategoryKey);

        var controls = new[]
        {
            "Wrex", "head_thin", "shell_thin", "shell_Down", "shell_Up", "shell_forwardSlant",
            "spike_erode", "spike_Smooth", "spike_flare", "shape_chubby", "shape_thin", "nose_Narrow",
            "eyes_Back", "eyes_Forward", "eyes_Down", "eyes_Up", "eyes_Big", "eyes_small",
            "eyes_narrow", "eyes_Wide", "mouth_Back", "mouth_Forward", "mouth_Down", "mouth_Up",
            "jaw_chinBack"
        }.Select(name => profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue(name, 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true)).ToArray();
        AssertGroup("facial-structure", "character", "Wrex");
        AssertGroup("facial-structure", "head-plates",
            "Outer Plates · Thin,Inner Plates · Thin,Plate Rim Down,Plate Rim Up,Plate Rim Forward Slant,Plate Erode,Spikes Smooth,Spikes Flare");
        AssertGroup("head", "shape", "Face Thin,Face Full");
        AssertGroup("head", "nose", "Nose Narrow");
        AssertGroup("eyes", "position", "Eyes Back,Eyes Forward,Eyes Down,Eyes Up");
        AssertGroup("eyes", "shape", "Eyes Small,Eyes Large,Eyes Narrow,Eyes Wide");
        AssertGroup("mouth", "position", "Mouth Back,Mouth Forward,Mouth Down,Mouth Up");
        AssertGroup("mouth", "jaw", "Chin Back");

        TestAssert.Equal("head", profile.GetMaterialCategory(
            "Wrex_Spec_Scalar", MaterialParameterKind.Scalar));
        TestAssert.Equal("surface", profile.GetMaterialSubcategory(
            "Wrex_Spec_Scalar", MaterialParameterKind.Scalar));
        TestAssert.Equal("head-plates", profile.GetMaterialSubcategory(
            "KRO_HED_Shell_Grad_Vector", MaterialParameterKind.Vector));
        TestAssert.Equal("eyes", profile.GetMaterialCategory(
            "Krogan_Pupil", MaterialParameterKind.Scalar));

        foreach (var definition in HumanMaterialProfiles.Definitions.Where(value =>
                     value.Family is HeadMaterialFamily.KroganSkin or HeadMaterialFamily.KroganEyes))
        {
            var label = profile.DescribeMaterial(definition).Label;
            foreach (var shorthand in new[] { "HED", "Addn", "TMis", "SPwr" })
            {
                TestAssert.True(!label.Contains(shorthand, StringComparison.OrdinalIgnoreCase),
                    $"Krogan label '{label}' retained shorthand '{shorthand}'.");
            }
        }

        void AssertGroup(string category, string subcategory, string expected)
        {
            var actual = string.Join(',', controls
                .Where(control => control.CategoryKey == category && control.SubcategoryKey == subcategory)
                .OrderBy(control => control.SortOrder)
                .ThenBy(control => control.Label, StringComparer.OrdinalIgnoreCase)
                .Select(control => control.Label));
            TestAssert.Equal(expected, actual);
        }
    }

    private static void BatarianProfileOrganizesFeatures()
    {
        var profile = new BatarianFeatureMetadataCatalog();
        TestAssert.Equal("Facial Structure,Head,Eyes,Nose,Mouth / Jaw,Markings",
            string.Join(',', profile.Categories.Select(category => category.Label)));
        TestAssert.Equal("CRANIUM,REGIONAL COLOUR", string.Join(',', profile.Categories
            .Single(category => category.Key == BatarianFeatureMetadataCatalog.FacialStructure)
            .SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("SHAPE,EARS,CHEEKS,NECK,SURFACE", string.Join(',', profile.Categories
            .Single(category => category.Key == BatarianFeatureMetadataCatalog.Head)
            .SliderGroups.Select(group => group.Label)));
        TestAssert.Equal("SHAPE,POSITION", string.Join(',', profile.Categories
            .Single(category => category.Key == BatarianFeatureMetadataCatalog.Eyes)
            .SliderGroups.Select(group => group.Label)));

        var target = TestFixtures.CreateTarget();
        var rearHead = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("back head", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var ears = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("ears_large", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var chin = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("jaw_chinOut", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var correction = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("teeth_correction", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("facial-structure", rearHead.CategoryKey);
        TestAssert.Equal("cranium", rearHead.SubcategoryKey);
        TestAssert.Equal("Rear Profile", rearHead.Label);
        TestAssert.Equal("head", ears.CategoryKey);
        TestAssert.Equal("ears", ears.SubcategoryKey);
        TestAssert.Equal("chin", chin.SubcategoryKey);
        TestAssert.True(!correction.IsVisible, "Zero-effect Batarian teeth correction remained visible.");
        TestAssert.True(BatarianFeatureMetadataCatalog.MetadataOnlyFeatures.SetEquals(
            new[] { "jaw_narrow", "shape_skinny" }),
            "The Batarian metadata-only feature set changed.");

        TestAssert.Equal("facial-structure", profile.GetMaterialCategory(
            "BAT_HED_TopHead_Grad_Vector", MaterialParameterKind.Vector));
        TestAssert.Equal("markings", profile.GetMaterialCategory(
            "BAT_HED_Addn", MaterialParameterKind.Texture));

        TestAssert.Equal("Forehead In,Forehead Out", OrderedLabels("foreHead_In", "foreHead_Out"));
        var headFull = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("shape_chubby", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        var foreheadIn = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("foreHead_In", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.True(headFull.SortOrder < foreheadIn.SortOrder,
            "Batarian Head Full was not placed at the top of Shape.");
        TestAssert.Equal("head-shape", profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("foreHead_In", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true).SubcategoryKey);
        TestAssert.Equal("Scale Up", profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("head_ScaleUp", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true).Label);
        TestAssert.Equal("Jaw Wide", profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("jaw_wide", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true).Label);
        TestAssert.Equal("Head Full,Neck Thin,Neck Wide", OrderedLabels("neck_Thin", "neck_wide", "shape_chubby"));
        TestAssert.Equal("Eyes Back,Eyes Forward,Eyes Down,Eyes Up", OrderedLabels(
            "eyes_Back", "eyes_Down", "eyes_Forward", "eyes_Up"));
        TestAssert.Equal("Eyes Small,Eyes Large,Eye Shape Narrow,Eyes Narrow,Eyes Wide", OrderedLabels(
            "eyes_Big", "eyes_Narow", "eyes_narrow", "eyes_small", "eyes_Wide"));
        TestAssert.Equal("Nose Down,Nose Up,Nose Short,Nose Long,Nose Out", OrderedLabels(
            "nose_Down", "nose_Long", "nose_Out", "nose_Short", "nose_Up"));
        TestAssert.Equal("Mouth Back,Mouth Forward,Mouth Down,Mouth Up,Mouth Narrow,Mouth Wide", OrderedLabels(
            "mouth_Back", "mouth_Down", "mouth_Forward", "mouth_Narrow", "mouth_Up", "mouth_Wide"));
        TestAssert.Equal("Teeth Back,Teeth Forward,Teeth Down,Teeth Up", OrderedLabels(
            "teeth_Back", "teeth_Down", "teeth_Forward", "teeth_Up"));

        foreach (var definition in HumanMaterialProfiles.Definitions.Where(value =>
                     value.Family == HeadMaterialFamily.BatarianSkin))
        {
            var label = profile.DescribeMaterial(definition).Label;
            foreach (var shorthand in new[] { "HED", "Addn", "TMis", "SPwr" })
            {
                TestAssert.True(!label.Contains(shorthand, StringComparison.OrdinalIgnoreCase),
                    $"Batarian label '{label}' retained shorthand '{shorthand}'.");
            }
        }

        string OrderedLabels(params string[] names) => string.Join(',', names
            .Select(name => profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
                new MorphFeatureValue(name, 0), target,
                MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true))
            .OrderBy(metadata => metadata.SortOrder)
            .ThenBy(metadata => metadata.Label, StringComparer.OrdinalIgnoreCase)
            .Select(metadata => metadata.Label));
    }
}
