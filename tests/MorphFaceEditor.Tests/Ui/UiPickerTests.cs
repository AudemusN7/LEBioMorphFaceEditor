using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Models;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
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
        new("mesh picker preserves manual sources and requires proven Player runtime backing", RegistryDeduplicatesAutomaticOccurrences),
        new("physical texture donors remain distinct and explicit choices survive collapsing", TexturePickerDeduplicatesPhysicalExports),
        new("texture hover cache is bounded and reused by an undoable commit", TexturePickerBoundsPreviewCacheAndPreservesHistory),
        new("late texture decode cannot replace a newer preview or revive a disposed picker", TexturePickerRejectsStalePreviewCacheResults)
    ];

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
        editor.Material.SetNumericValues(new Dictionary<string, float>(), new Dictionary<string, Vector4> { [colourName] = colour });
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
