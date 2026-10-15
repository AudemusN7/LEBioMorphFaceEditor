using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using System.Numerics;

namespace MorphFaceEditor.Tests;

public static class NpcHairRandomisationTests
{
    private static int _nextSyntheticTextureUIndex;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("NPC HMM Rollins keeps native flat-top compatibility without changing Player rules",
            NpcRollinsRetainsNativeFlatTop),
        new("NPC hair-mesh blocking sees retained and proposed Player-excluded Character sliders",
            NonzeroCharacterSlidersBlockNpcMeshChanges),
        new("shared HMM scalp styles target the human HED Tang parameter",
            HmmScalpStylesUseHumanTangParameter),
        new("NPC HMM editor rolls a reviewed scalp and matching hair morph as one action",
            NpcHmmEditorRollsScalpAndMorph),
        new("NPC HMM material-only hair rolls reconcile hair sliders without changing the face",
            () => NpcHmmEditorRollsScalpAndMorph(morphsEnabled: false)),
        new("NPC HMM Cursed material-only hair rolls reconcile hair sliders without changing the face",
            () => NpcHmmEditorRollsScalpAndMorph(morphsEnabled: false, cursed: true)),
        new("NPC HMM hair button reconciles hair sliders with morph randomisation disabled",
            () => NpcHmmEditorRollsScalpAndMorph(morphsEnabled: false, hairButton: true)),
        new("NPC HMM hair morph button follows material randomisation and keeps scalp compatibility",
            () => NpcHmmEditorRollsScalpAndMorph(morphsEnabled: false, hairMorphButton: true)),
        new("NPC HMM hair rolls reconcile sliders even when the selected scalp is unchanged",
            () => NpcHmmEditorRollsScalpAndMorph(morphsEnabled: false, sameScalp: true)),
        new("Player HMM material-only hair rolls reconcile hair sliders without changing the face",
            () => NpcHmmEditorRollsScalpAndMorph(morphsEnabled: false, playerWorkspace: true)),
        new("NPC HMM morph-only rolls preserve hair sliders and scalp textures",
            HmmMorphOnlyPreservesHair),
        new("NPC HMM material-only mesh rolls clear incompatible hair sliders",
            () => VerifyMaterialOnlyMeshRoll(PlayerHairSex.Male)),
        new("NPC HMF material-only mesh rolls clear incompatible hair sliders",
            () => VerifyMaterialOnlyMeshRoll(PlayerHairSex.Female)),
        new("NPC HMF targeted scalp roll assigns its mandatory braid mesh",
            NpcHmfTargetedScalpRollAssignsMesh),
        new("NPC hair button rolls a tuple without an accessory randomisation command",
            NpcHairButtonRollsTuple),
        new("NPC HMF mesh lock preserves paired scalp and hair morphs in normal and Cursed rolls",
            LockedHmfMeshPreservesScalpAndMorphs),
        new("NPC Cursed rolls accept newly loaded hair highlights without drifting their baseline",
            CursedHighlightsArriveAfterBaseline),
        new("Player hair mesh rolls keep mesh textures out of face overrides without a preview refresh",
            PlayerHairRollDropsOutgoingMaps)
    ];

    private static void NpcRollinsRetainsNativeFlatTop()
    {
        var rollins = Style("hmm-rol", ["rollins"]);
        var npc = HumanMaleHairScalpPolicy.ApplyNpcStyleMorphCompatibility(rollins);

        TestAssert.True(npc.HairMorphNames.Contains("rollins", StringComparer.OrdinalIgnoreCase) &&
                        npc.HairMorphNames.Contains("flatTop", StringComparer.OrdinalIgnoreCase),
            "NPC HMM Rollins did not retain the native Rollins/Flat Top pairing.");
        TestAssert.True(rollins.HairMorphNames.SequenceEqual(["rollins"], StringComparer.OrdinalIgnoreCase),
            "Adding native NPC compatibility mutated the reviewed Player Rollins pairing.");
        TestAssert.True(!HumanMaleHairScalpPolicy.ApplyNpcStyleMorphCompatibility(
                Style("hmm-sar", [])).HairMorphNames.Contains("flatTop",
                StringComparer.OrdinalIgnoreCase),
            "Generic Sar scalp incorrectly acquired the Ssk Flat Top morph.");
    }

    private static void NonzeroCharacterSlidersBlockNpcMeshChanges()
    {
        var maleFeatures = new[]
        {
            ("Ashley", "facial-structure", "character", 0.35f),
            ("eastwood", "facial-structure", "character", 0.2f),
            ("nose_BridgeIn", "nose", "bridge", 0.6f)
        };

        TestAssert.True(FaceEditorViewModel.HasNonzeroPlayerExcludedCharacterMorph(
                maleFeatures, new Dictionary<string, float>(), PlayerHairSex.Male),
            "A retained nonzero HMM Character morph did not protect the current mesh.");
        TestAssert.True(!FaceEditorViewModel.HasNonzeroPlayerExcludedCharacterMorph(
                maleFeatures, new Dictionary<string, float> { ["Ashley"] = 0f }, PlayerHairSex.Male),
            "A proposed zero value still protected the current mesh.");
        TestAssert.True(!FaceEditorViewModel.HasNonzeroPlayerExcludedCharacterMorph(
                maleFeatures, new Dictionary<string, float> { ["Ashley"] = 0f, ["eastwood"] = 0.4f },
                PlayerHairSex.Male),
            "The Player-approved HMM Eastwood exception incorrectly blocked a mesh change.");
        TestAssert.True(FaceEditorViewModel.HasNonzeroPlayerExcludedCharacterMorph(
                maleFeatures, new Dictionary<string, float> { ["Ashley"] = -0.1f }, PlayerHairSex.Male),
            "A nonzero negative Character morph did not protect the current mesh.");

        var femaleFeatures = new[]
        {
            ("iconic", "facial-structure", "character", 0.5f),
            ("race_iconic", "facial-structure", "character", 0.6f),
            ("Ashley", "facial-structure", "character", 0f)
        };
        TestAssert.True(!FaceEditorViewModel.HasNonzeroPlayerExcludedCharacterMorph(
                femaleFeatures, new Dictionary<string, float>(), PlayerHairSex.Female),
            "Player-approved HMF Iconic sliders incorrectly blocked a mesh change.");
        TestAssert.True(FaceEditorViewModel.HasNonzeroPlayerExcludedCharacterMorph(
                femaleFeatures, new Dictionary<string, float> { ["Ashley"] = 0.1f }, PlayerHairSex.Female),
            "A nonzero Player-excluded HMF Character slider did not protect the current mesh.");
    }

    private static void HmmScalpStylesUseHumanTangParameter()
    {
        var candidates = new[]
        {
            "HMM_HIR_Afr_Diff", "HMM_HIR_Afr_Norm", "HMM_HIR_Afr_Mask", "HMM_HIR_Afr_Tang"
        }.Select(Texture).ToArray();
        var style = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE1, PlayerHairSex.Male,
                candidates, [])
            .Single(value => value.Id == "hmm-afr");

        TestAssert.Equal("HMM_HIR_Afr_Tang", ObjectName(style.ScalpTextures["HED_Tang"]));
        TestAssert.True(!style.ScalpTextures.ContainsKey("HED_Scalp_Tang"),
            "The NPC scalp style emitted the non-existent HED_Scalp_Tang control key.");
    }

    private static void NpcHmmEditorRollsScalpAndMorph() =>
        NpcHmmEditorRollsScalpAndMorph(morphsEnabled: true);

    private static void NpcHmmEditorRollsScalpAndMorph(
        bool morphsEnabled, bool cursed = false, bool hairButton = false, bool hairMorphButton = false,
        bool sameScalp = false, bool playerWorkspace = false)
    {
        var registry = TestFixtures.GetCorpusTextureCatalogService();
        var catalog = registry.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        var style = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE1, PlayerHairSex.Male,
                catalog.Candidates, catalog.AttachmentMeshes)
            .Single(value => value.Id == "hmm-afr");
        using var reader = new MorphFacePackageReader();
        using var editor = CreateNpcEditor(reader, registry, catalog, "le1-human-male",
            new HumanMaleFeatureMetadataCatalog(), PlayerHairSex.Male, style,
            ["Afro", "deiter", "nose_BridgeIn"], playerWorkspace: playerWorkspace);
        if (sameScalp)
            editor.Material.ApplyRandomisation(editor.Material.PrepareRandomisationAsync(
                new Dictionary<string, float>(), new Dictionary<string, Vector4>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>> { ["player-hair"] = style.ScalpTextures })
                .GetAwaiter().GetResult());
        editor.Features.Single(value => value.Name == "nose_BridgeIn").Value = 0.37f;
        editor.CursedMode = cursed;
        editor.RandomiseMorphs = morphsEnabled;
        editor.RandomiseMaterials = true;
        editor.MorphRandomisationStrength = 100;
        editor.MaterialRandomisationStrength = 100;

        var command = hairButton ? editor.HairMesh.RandomiseCommand : hairMorphButton
            ? editor.Categories.SelectMany(category => category.SliderGroups)
                .Single(group => group.MorphFeatures.Any(feature => feature.Name == "Afro")).RandomiseCommand
            : editor.RandomiseCommand;
        TestAssert.True(command?.CanExecute(null) == true, "The HMM material hair roll is disabled.");
        command!.Execute(null);

        TestAssert.True(SpinWait.SpinUntil(() =>
        {
            var currentScalpPath = editor.Material.Textures
                .Single(value => value.Name == "HED_Scalp_Diff").SourceName;
            var currentDraft = editor.CreateDraft();
            return ObjectName(currentScalpPath).Equals("HMM_HIR_Afr_Diff",
                       StringComparison.OrdinalIgnoreCase) &&
                   (currentDraft.GetFeatureOffset("Afro") == 1f ||
                    currentDraft.GetFeatureOffset("deiter") == 1f);
        }, TimeSpan.FromSeconds(15)),
            "The NPC global randomisation command did not finish applying the matching scalp and hair morph.");
        var scalpPath = editor.Material.Textures.Single(value => value.Name == "HED_Scalp_Diff").SourceName;
        TestAssert.True(ObjectName(scalpPath).Equals("HMM_HIR_Afr_Diff", StringComparison.OrdinalIgnoreCase),
            "The NPC HMM hair catalog did not replace the scalp diffuse with its reviewed Afr style.");
        var draft = editor.CreateDraft();
        TestAssert.True(draft.GetFeatureOffset("Afro") == 1f || draft.GetFeatureOffset("deiter") == 1f,
            "The NPC HMM Afr scalp roll did not select a matching editable hair morph.");
        TestAssert.True(draft.HairMeshReference is null,
            "The scalp-only HMM Afr style unexpectedly assigned a hair mesh.");
        if (!morphsEnabled)
            TestAssert.Equal(0.37f, draft.GetFeatureOffset("nose_BridgeIn"));

        TestAssert.True(SpinWait.SpinUntil(() => editor.UndoCommand.CanExecute(null),
            TimeSpan.FromSeconds(15)), "The NPC aggregate randomisation did not create an undo step.");
        editor.UndoCommand.Execute(null);
        TestAssert.True(SpinWait.SpinUntil(() =>
                editor.CreateDraft().GetFeatureOffset("Afro") == 0f &&
                editor.CreateDraft().GetFeatureOffset("deiter") == 0f,
            TimeSpan.FromSeconds(15)),
            "Undo did not restore the NPC HMM hair sliders in one aggregate edit.");
    }

    private static void NpcHmfTargetedScalpRollAssignsMesh()
    {
        VerifyNpcHmfTargetedScalpRollAssignsMesh(cursed: false);
        VerifyNpcHmfTargetedScalpRollAssignsMesh(cursed: true);
    }

    private static void HmmMorphOnlyPreservesHair()
    {
        var registry = TestFixtures.GetCorpusTextureCatalogService();
        var catalog = registry.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        var style = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE1, PlayerHairSex.Male,
            catalog.Candidates, catalog.AttachmentMeshes).Single(value => value.Id == "hmm-afr");
        using var reader = new MorphFacePackageReader();
        foreach (var cursed in new[] { false, true })
        {
            using var editor = CreateNpcEditor(reader, registry, catalog, "le1-human-male",
                new HumanMaleFeatureMetadataCatalog(), PlayerHairSex.Male, style,
                ["Afro", "deiter", "nose_BridgeIn"]);
            editor.Features.Single(value => value.Name == "Afro").Value = 0.6f;
            editor.Features.Single(value => value.Name == "nose_BridgeIn").Value = 0.37f;
            editor.CursedMode = cursed;
            editor.RandomiseMaterials = false;
            editor.RandomiseMorphs = true;
            editor.MorphRandomisationStrength = 100;
            var scalp = editor.Material.Textures.Single(value => value.Name == "HED_Scalp_Diff").SourceName;
            editor.RandomiseCommand.Execute(null);
            var draft = editor.CreateDraft();
            TestAssert.Equal(0.6f, draft.GetFeatureOffset("Afro"));
            TestAssert.Equal(0f, draft.GetFeatureOffset("deiter"));
            TestAssert.Equal(scalp, editor.Material.Textures.Single(value => value.Name == "HED_Scalp_Diff").SourceName);
            TestAssert.True(draft.GetFeatureOffset("nose_BridgeIn") != 0.37f,
                "The morph-only regression did not exercise a facial morph change.");
        }
    }

    private static void VerifyMaterialOnlyMeshRoll(PlayerHairSex sex)
    {
        foreach (var cursed in new[] { false, true })
            VerifyNpcHmfTargetedScalpRollAssignsMesh(cursed, hairButton: true, morphsEnabled: false, sex: sex);
    }

    private static void NpcHairButtonRollsTuple()
    {
        VerifyNpcHmfTargetedScalpRollAssignsMesh(cursed: false, hairButton: true);
        VerifyNpcHmfTargetedScalpRollAssignsMesh(cursed: true, hairButton: true);
    }

    private static void LockedHmfMeshPreservesScalpAndMorphs()
    {
        var registry = new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));
        var catalog = registry.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        var style = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE1, PlayerHairSex.Female,
            catalog.Candidates, catalog.AttachmentMeshes).Single(value => value.Id == "hmf-mhk");
        using var reader = new MorphFacePackageReader();
        foreach (var cursed in new[] { false, true })
        {
            using var editor = CreateNpcEditor(reader, registry, catalog, "le1-human-female",
                new HumanFemaleFeatureMetadataCatalog(), PlayerHairSex.Female, style,
                ["HAIR_pulledBackSlick"], materialName: "BIOG_HMF_HED_PRONPC_MASTER_FACE_MAT");
            var hairFeature = editor.Features.First(value => value.Name == "HAIR_pulledBackSlick");
            TestAssert.True(hairFeature.IsEditable,
                "The lock regression must exercise an editable HMF hair slider.");
            hairFeature.Value = 0.35f;
            editor.HairMesh.TrySetRandomisedSelection(style.MeshVariants[0]);
            editor.HairMesh.IsRandomisationLocked = true;
            editor.RandomiseMorphs = true;
            editor.RandomiseMaterials = true;
            editor.MaterialRandomisationStrength = 100;
            editor.MorphRandomisationStrength = 100;
            editor.CursedMode = cursed;
            var before = editor.CreateDraft();
            var beforeScalp = editor.Material.Textures.ToDictionary(value => value.Name, value => value.SourceName);
            var hairGroup = editor.Categories.SelectMany(value => value.SliderGroups)
                .First(value => value.MorphFeatures.Any(feature => feature.Name == "HAIR_pulledBackSlick"));
            TestAssert.True(hairGroup.RandomiseCommand?.CanExecute(null) == false,
                "A locked mesh left the HMF hair-only morph roll enabled.");
            TestAssert.True(editor.HairMesh.RandomiseCommand?.CanExecute(null) == false,
                "The hair button did not honour the mesh lock.");
            // Await the command's workflow in both modes, including its scope and generation handling.
            editor.RandomiseAsync().GetAwaiter().GetResult();
            TestAssert.Equal(before.GetFeatureOffset("HAIR_pulledBackSlick"),
                editor.CreateDraft().GetFeatureOffset("HAIR_pulledBackSlick"));
            TestAssert.Equal(before.HairMeshReference!, editor.CreateDraft().HairMeshReference!);
            foreach (var texture in editor.Material.Textures)
                TestAssert.Equal(beforeScalp[texture.Name], texture.SourceName);
        }
    }

    private static void PlayerHairRollDropsOutgoingMaps()
    {
        var registry = new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));
        var catalog = registry.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        var style = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE1, PlayerHairSex.Female,
            catalog.Candidates, catalog.AttachmentMeshes).Single(value => value.Id == "hmf-braids");
        using var reader = new MorphFacePackageReader();
        foreach (var cursed in new[] { false, true })
        {
            var staleMaps = new MorphFaceMaterialOverrides(null, [], [],
                new[] { "HAIR_Diff", "HAIR_ADDN_Diff", "HAIR_Norm" }.Select(name =>
                    new TextureMaterialOverride(name, new AssetIdentity("old-hair.pcc",
                        "BIOG_HMF_HIR_PRO.Sxy.HIR_Sxy_Diff", 1, "Texture2D"))).ToArray());
            using var editor = CreateNpcEditor(reader, registry, catalog, "le1-human-female",
                new HumanFemaleFeatureMetadataCatalog(), PlayerHairSex.Female, style, [],
                materialName: "BIOG_HMF_HED_PROMorph_MASTER_FACE_MAT", playerWorkspace: true,
                overrides: staleMaps);
            editor.RandomiseMorphs = false;
            editor.RandomiseMaterials = true;
            editor.MaterialRandomisationStrength = 100;
            editor.CursedMode = cursed;
            TestAssert.True(!editor.Material.Textures.Any(value =>
                    HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(value.Name)),
                "Head editing exposes a mesh-owned hair map to randomisation.");
            TestAssert.True(!editor.Material.CaptureInterchangeData().Textures.Any(value =>
                    HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(value.Name)),
                "Head material interchange retained a mesh-owned hair map.");
            editor.RandomiseAsync().GetAwaiter().GetResult();
            TestAssert.True(editor.CreateDraft().HairMeshReference is not null,
                "The paired Player hair roll did not assign a mesh.");
            foreach (var map in staleMaps.Textures)
                TestAssert.True(!editor.CreateDraft().MaterialOverrides.Textures.Any(value =>
                        value.Name.Equals(map.Name, StringComparison.OrdinalIgnoreCase)),
                    $"The new mesh retained the outgoing {map.Name} face override.");
            editor.UndoCommand.Execute(null);
            TestAssert.True(editor.CreateDraft().HairMeshReference is null,
                "Undo did not restore the outgoing mesh selection.");
            TestAssert.True(!editor.CreateDraft().MaterialOverrides.Textures.Any(value =>
                    staleMaps.Textures.Any(map => map.Name == value.Name)),
                "Undo leaked a mesh-owned hair map into the BMO.");
            editor.RedoCommand.Execute(null);
            TestAssert.True(!editor.CreateDraft().MaterialOverrides.Textures.Any(value =>
                    staleMaps.Textures.Any(map => map.Name == value.Name)),
                "Redo restored outgoing hair maps onto the new mesh.");
        }
    }

    private static void CursedHighlightsArriveAfterBaseline()
    {
        var registry = TestFixtures.GetCorpusTextureCatalogService();
        var catalog = registry.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        var style = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE1, PlayerHairSex.Male,
            catalog.Candidates, catalog.AttachmentMeshes).Single(value => value.Id == "hmm-afr");
        using var reader = new MorphFacePackageReader();
        using var editor = CreateNpcEditor(reader, registry, catalog, "le1-human-male",
            new HumanMaleFeatureMetadataCatalog(), PlayerHairSex.Male, style, []);
        editor.RandomiseMaterials = true;
        editor.MaterialRandomisationStrength = 100;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var method = typeof(FaceEditorViewModel).GetMethod("RandomiseCoreAsync", flags)!;
        void Roll(EditorRandomisationScope scope) =>
            ((Task)method.Invoke(editor, [scope, false, true])!).GetAwaiter().GetResult();
        editor.CursedMode = true;
        Roll(new EditorRandomisationScope([], [], [], []));
        var identity = TestFixtures.CreateIdentity("NewHairMaterial", "MaterialInstanceConstant");
        var hair = new ResolvedHeadMaterial(MaterialIdentityKey.Create(identity), identity,
            "BIOG_HMM_HIR_HAIR_MASTER_MAT", HeadMaterialFamily.Hair,
            HeadMaterialBlendMode.Translucent, false,
            new Dictionary<string, float> { ["Highlight1Intensity"] = 0.2f },
            new Dictionary<string, Vector4> { ["Highlight1Color"] = new(0.2f, 0.3f, 0.4f, 1f) },
            new Dictionary<string, MaterialTextureBinding>());
        editor.Material.ReplaceAttachmentMaterials(new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [hair.Key] = hair }));
        var scalar = editor.Material.Scalars.Single(value => value.Name == "Highlight1Intensity");
        var vector = editor.Material.Vectors.Single(value => value.Name == "Highlight1Color");
        var scope = new EditorRandomisationScope([], [scalar], [vector], []);
        Roll(scope);
        var firstScalar = scalar.Value;
        var firstVector = editor.Material.Materials.Materials[hair.Key].Vectors["Highlight1Color"];
        TestAssert.True(firstScalar != 0.2f || firstVector != hair.Vectors["Highlight1Color"],
            "The newly loaded highlight controls did not participate in the Cursed roll.");
        Roll(scope);
        TestAssert.Equal(firstScalar, scalar.Value);
        TestAssert.Equal(firstVector, editor.Material.Materials.Materials[hair.Key].Vectors["Highlight1Color"]);
    }

    private static void VerifyNpcHmfTargetedScalpRollAssignsMesh(bool cursed, bool hairButton = false,
        bool morphsEnabled = true, PlayerHairSex sex = PlayerHairSex.Female)
    {
        var registry = new TextureCatalogService(new TextureRegistryStore(
            TextureRegistryPaths.CreateDefault()));
        var catalog = registry.ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
        var styles = PlayerHairStyleCatalog.Resolve(MorphFaceGame.LE1, sex,
            catalog.Candidates, catalog.AttachmentMeshes);
        var styleId = sex == PlayerHairSex.Female ? "hmf-braids" : "hmm-mhk";
        var style = styles.FirstOrDefault(value => value.Id == styleId);
        TestAssert.True(style is not null,
            $"The installed LE1 texture registry did not resolve {styleId}. Resolved styles: " +
            string.Join(", ", styles.Select(value => value.Id)));
        TestAssert.True(style!.RequiresMeshForScalp,
            $"{styleId} must remain a mandatory scalp/mesh pair.");
        TestAssert.True(style.MeshVariants.Count > 0,
            $"The installed LE1 texture registry did not resolve any {styleId} mesh variants.");
        using var reader = new MorphFacePackageReader();
        var hairMorph = sex == PlayerHairSex.Female ? "HAIR_pulledBackSlick" : "Afro";
        using var editor = CreateNpcEditor(reader, registry, catalog,
            sex == PlayerHairSex.Female ? "le1-human-female" : "le1-human-male",
            sex == PlayerHairSex.Female ? new HumanFemaleFeatureMetadataCatalog() : new HumanMaleFeatureMetadataCatalog(),
            sex, style!, [hairMorph, "nose_BridgeIn"],
            materialName: sex == PlayerHairSex.Female ? "BIOG_HMF_HED_PRONPC_MASTER_FACE_MAT" : "BIOG_HMM_HED_PRONPC_MASTER_FACE_MAT");
        editor.Features.Single(value => value.Name == hairMorph).Value = 0.35f;
        editor.Features.Single(value => value.Name == "nose_BridgeIn").Value = 0.37f;
        TestAssert.True(editor.CanEditAttachments,
            "The NPC hair test workspace does not permit attachment editing.");
        TestAssert.True(!editor.HairMesh.IsRandomisationLocked,
            "The NPC hair mesh unexpectedly starts locked.");
        editor.CursedMode = cursed;
        editor.RandomiseMaterials = true;
        editor.RandomiseMorphs = morphsEnabled;
        editor.MaterialRandomisationStrength = 100;
        var scalpCategory = editor.Categories.FirstOrDefault(value => value.Textures.Any(texture =>
            texture.Name.Equals("HED_Scalp_Diff", StringComparison.OrdinalIgnoreCase)));
        TestAssert.True(scalpCategory is not null,
            $"The NPC editor did not expose its scalp diffuse. Material textures: " +
            string.Join(", ", editor.Material.Textures.Select(value => value.Name)));
        TestAssert.True(scalpCategory!.TextureRandomiseCommand is not null,
            "The NPC scalp texture roll is unavailable.");
        TestAssert.True(scalpCategory!.TextureRandomiseCommand!.CanExecute(null),
            "The NPC targeted scalp roll is disabled at the current randomisation settings.");

        TestAssert.True(editor.OtherMeshes.All(value => !value.HasRandomiseCommand),
            "An accessory row received the hair randomisation command.");
        var command = hairButton ? editor.HairMesh.RandomiseCommand : scalpCategory.TextureRandomiseCommand;
        TestAssert.True(command?.CanExecute(null) == true, "The selected hair roll command is disabled.");
        command!.Execute(null);

        var expectedDiffuse = ObjectName(style!.ScalpTextures["HED_Scalp_Diff"]);
        TestAssert.True(SpinWait.SpinUntil(() =>
        {
            var currentScalp = editor.Material.Textures
                .Single(value => value.Name == "HED_Scalp_Diff").SourceName;
            return ObjectName(currentScalp).Equals(expectedDiffuse,
                       StringComparison.OrdinalIgnoreCase) &&
                   editor.HairMesh.Value is not null && editor.CreateDraft().GetFeatureOffset(hairMorph) == 0f;
        }, TimeSpan.FromSeconds(15)),
            "The NPC targeted scalp roll did not finish applying its paired scalp and mesh. Current diffuse: " +
            editor.Material.Textures.Single(value => value.Name == "HED_Scalp_Diff").SourceName +
            "; mesh: " + (editor.HairMesh.Value?.InstancedPath ?? "<null>"));
        TestAssert.True(editor.CreateDraft().HairMeshReference?.InstancedPath.Contains(
                sex == PlayerHairSex.Female ? "Braids01_MDL" : "Mhk_MDL", StringComparison.OrdinalIgnoreCase) == true,
            $"The selected {styleId} mesh was not included in the resulting face draft.");
        TestAssert.Equal(0.37f, editor.CreateDraft().GetFeatureOffset("nose_BridgeIn"));

    }

    private static FaceEditorViewModel CreateNpcEditor(
        MorphFacePackageReader reader,
        TextureCatalogService registry,
        TextureCatalogReadResult catalog,
        string profileKey,
        IHeadEditorUiProfile metadata,
        PlayerHairSex sex,
        PlayerHairStyleOption style,
        IReadOnlyList<string> hairMorphs,
        string? materialName = null, bool playerWorkspace = false,
        MorphFaceMaterialOverrides? overrides = null)
    {
        var textureNames = style.ScalpTextures.Values.Select(ObjectName)
            .Append("GBL_ARM_ALL_Black")
            .Append("GBL_ARM_ALL_Norm")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textures = catalog.Candidates.Where(value => textureNames.Contains(value.ObjectName)).ToArray();
        var parameterNames = new[] { "HED_Scalp_Diff", "HED_Scalp_Norm", "HED_Scalp_Spec", "HED_Tang" };
        var bindings = parameterNames.ToDictionary(name => name, name => new MaterialTextureBinding(
            name, new DecodedTextureAsset(
                new AssetIdentity("current.pcc", $"Current.{name}", 1, "Texture2D"),
                1, 1, [96, 96, 96, 255], "PF_B8G8R8A8", TextureRole.Normal,
                TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, false, $"Current.{name}")),
            StringComparer.OrdinalIgnoreCase);
        var materialIdentity = TestFixtures.CreateIdentity("NpcHairMaterial", "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(materialIdentity), materialIdentity,
            materialName ?? "BIOG_HMM_HED_PRONPC_MASTER_FACE_MAT", HeadMaterialFamily.Skin,
            HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(), new Dictionary<string, Vector4>(), bindings)
        {
            SupportedTextures = parameterNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        var mesh = TestFixtures.CreateMesh();
        var targetAssets = hairMorphs.Select(name => new MorphTargetAsset(
            TestFixtures.CreateIdentity($"Set.{name}", "MorphTarget"),
            [new MorphTargetLod(0, 3, [])], [])).ToArray();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("NpcFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            null, null,
            hairMorphs.Select(name => new MorphFeatureValue(name, 0f)).ToArray(),
            [new BoneTranslation("root", Vector3.Zero)],
            overrides ?? MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()], []);
        var session = new MorphFaceEditingSession(document, mesh, targetAssets);
        var materialSet = new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material };
        if (overrides is not null)
        {
            var hairIdentity = TestFixtures.CreateIdentity("OldHairMaterial", "MaterialInstanceConstant");
            var hair = new ResolvedHeadMaterial(MaterialIdentityKey.Create(hairIdentity), hairIdentity,
                "BIOG_HMF_HIR_HAIR_MASTER_MAT", HeadMaterialFamily.Hair,
                HeadMaterialBlendMode.Translucent, false,
                new Dictionary<string, float>(), new Dictionary<string, Vector4>(),
                overrides.Textures.ToDictionary(value => value.Name, value => new MaterialTextureBinding(
                    value.Name, new DecodedTextureAsset(value.TextureReference!, 1, 1,
                        [96, 96, 96, 255], "PF_B8G8R8A8", TextureRole.Diffuse,
                        TextureColorSpace.Srgb, TextureAlphaPolicy.Translucency, false,
                        value.TextureReference!.InstancedPath)), StringComparer.OrdinalIgnoreCase));
            materialSet.Add(hair.Key, hair);
        }
        var materials = new MaterialEditingSession(overrides ?? MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(materialSet));
        var sourcePackagePath = textures.First().SourcePackagePath;
        var editor = new FaceEditorViewModel(
            session, metadata, materials, new StubColorDialog(),
            new PackageReferenceService(reader, registry), sourcePackagePath,
            [], [], null, [], message => throw new InvalidOperationException(message),
            profileKey, MorphRandomisationCatalog.LoadEmbedded(), () => 107,
            registryTextureCandidates: textures, textureCatalogProfile: TextureCatalogProfile.Empty,
            isTextureRegistryAvailable: true,
            playerRandomisationGame: playerWorkspace ? MorphFaceGame.LE1 : null);
        var usedMeshPaths = style.MeshVariants.Select(value => TextureCatalogPicker.CanonicalPath(
                value.InstancedPath, value.PackagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        editor.UpdateRegistryAttachmentMeshes(catalog.AttachmentMeshes.Where(value =>
            usedMeshPaths.Contains(value.CanonicalPath)).ToArray(), isPlayerWorkspace: false);
        return editor;
    }

    private static PlayerHairStyleOption Style(string id, IReadOnlyList<string> morphs) => new(
        id, PlayerHairSex.Male, new Dictionary<string, string>(), PlayerHairMeshAction.Keep,
        [], morphs, morphs.Count > 0 ? 1f : 0f);

    private static TextureCatalogCandidate Texture(string objectName)
    {
        var path = $"BIOG_HMM_HIR_PRO_R.Randomisation.{objectName}";
        var occurrence = new TextureCatalogOccurrence(
            "BIOG_HMM_HIR_PRO_R.pcc", System.Threading.Interlocked.Increment(ref _nextSyntheticTextureUIndex),
            0, TextureCatalogOrigin.BaseGame,
            1, 1, "PF_B8G8R8A8", "TEXTUREGROUP_Character", false, null);
        return new TextureCatalogCandidate(MorphFaceGame.LE1, path, occurrence, [occurrence])
        { IsEligibleOfficialDefault = true };
    }

    private static string ObjectName(string path) => path[(path.LastIndexOf('.') + 1)..];

    private sealed class StubColorDialog : IHdrColorDialogService
    {
        public bool ExtendedSliders { get; set; }
        public Vector4? Edit(string title, Vector4 value, Action<Vector4> livePreview) => null;
        public Vector4? EditStandard(string title, Vector4 value, Action<Vector4> livePreview) => null;
    }
}
