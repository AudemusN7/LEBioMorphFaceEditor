using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Core.Randomisation;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiRandomisationTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("normal roll clears the Cursed state atomically and undo restores it", NormalRollClearsCursedState),
        new("scoped material rolls preserve unrelated selectors and authored rows", ScopedMaterialRollsKeepUnselectedState),
        new("awaitable randomisation propagates seed failures without partial edits", AwaitableRandomisationPropagatesFailure),
        new("failed Cursed randomisation rolls back its partial edit", FailedCursedRandomisationRollsBack),
        new("manual scalp preparation keeps its required pair when an optional map fails", ManualHairScalpPromptKeepsRequiredPairWhenOptionalMapFails),
        new("a hair mesh lock permits deliberately unpaired scalp maps to roll", HairLockAllowsUnpairedScalpRoll),
        new("exhausted texture randomisation retains numeric values", ExhaustedTextureRandomisationIsReported)
    ];

    private static void HairLockAllowsUnpairedScalpRoll()
    {
        using var fixture = CreateHairScalpFixture();
        var editor = fixture.Editor;
        var before = editor.CreateDraft();
        editor.HairMesh.IsRandomisationLocked = true;
        editor.RandomiseMorphs = false;
        editor.RandomiseMaterials = true;
        editor.MaterialRandomisationStrength = 100;
        editor.RandomiseAsync().GetAwaiter().GetResult();
        TestAssert.Equal(before.HairMeshReference, editor.CreateDraft().HairMeshReference);
        TestAssert.True(PlayerHairStyleCatalog.MatchesScalpTextures(fixture.Style,
                fixture.Style.ScalpTextures.Keys.ToDictionary(name => name,
                    name => fixture.Material.GetTextureReference(name), StringComparer.OrdinalIgnoreCase)),
            "The mesh lock suppressed scalp randomisation despite deliberately unpaired maps.");
    }

    internal static HairScalpFixture CreateHairScalpFixture(bool inheritedPair = false, bool ccMohawk = false)
    {
        var sourceFile = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        var reader = new MorphFacePackageReader();
        var mesh = TestFixtures.CreateMesh();
        var game = ccMohawk ? MorphFaceGame.LE3 : MorphFaceGame.LE1;
        var textureGroup = ccMohawk ? "Hair_Mohawk" : "Hair_Sarge";
        var names = ccMohawk
            ? new[] { "HMM_HIR_Mhk_Scalp_Diff", "HMM_HIR_Mhk_Scalp_Norm", "HMM_HIR_Mhk_Scalp_Mask", "GBL_ARM_ALL_Norm",
                "HMM_HIR_Sar_Diff", "HMM_HIR_Sar_Norm", "HMM_HIR_Sar_Mask", "HMM_HIR_Sar_Tang" }
            : new[] { "HMM_HIR_Sar_Diff", "HMM_HIR_Sar_Norm", "HMM_HIR_Sar_Mask", "HMM_HIR_Sar_Tang" };
        var candidates = names.Select((name, index) =>
        {
            var occurrence = new TextureCatalogOccurrence(sourceFile.Name, 20 + index, 0,
                TextureCatalogOrigin.BaseGame, 1, 1, "PF_B8G8R8A8", "Character", false, null);
            var group = name.StartsWith("HMM_HIR_Sar_", StringComparison.Ordinal) ? "Hair_Sarge" : textureGroup;
            return new TextureCatalogCandidate(game, $"BIOG_HMM_HIR_PRO_R.{group}.{name}",
                occurrence, [occurrence]) { IsEligibleOfficialDefault = true };
        }).ToArray();
        var meshNames = ccMohawk ? new[] { "HMM_HIR_Mhk_MDL_CC" } : new[] { "HMM_HIR_Ssk_01_MDL", "HMM_HIR_Ssk_02_MDL" };
        var meshes = meshNames.Select((name, index) =>
        {
            var path = $"BIOG_HMM_HIR_PRO_R.{(ccMohawk ? "Hair_Mohawk" : "Hair_SargeSpike")}.{name}";
            var occurrence = new AttachmentMeshOccurrence(sourceFile.Name, path, 40 + index, 0,
                TextureCatalogOrigin.BaseGame, 1);
            return new AttachmentMeshCandidate(path, occurrence, [occurrence]) { IsEligibleOfficialDefault = true };
        }).ToArray();
        var style = PlayerHairStyleCatalog.Resolve(game, PlayerHairSex.Male, candidates, meshes,
                includeManualPairedVariants: ccMohawk)
            .Single(value => value.Id == (ccMohawk ? "hmm-mhk" : "hmm-sar-ssk"));
        var defaultTextures = style.ScalpTextures.Keys.ToDictionary(name => name,
            name => new MaterialTextureBinding(name, CreateTestTexture(sourceFile.Name,
                inheritedPair ? style.ScalpTextures[name] : $"Custom.Default.{name}", name)),
            StringComparer.OrdinalIgnoreCase);
        var materialIdentity = TestFixtures.CreateIdentity("Scalp", "MaterialInstanceConstant");
        var resolved = new ResolvedHeadMaterial(MaterialIdentityKey.Create(materialIdentity), materialIdentity,
            "HMM_HED_PRO_MASTER_SCALP_MAT", HeadMaterialFamily.Scalp, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float> { ["HED_Norm_Blend"] = 2 }, new Dictionary<string, Vector4>(), defaultTextures)
        {
            DefaultScalars = new Dictionary<string, float> { ["HED_Norm_Blend"] = 2 },
            DefaultTextures = defaultTextures,
            SupportedTextures = style.ScalpTextures.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        var material = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial> { [resolved.Key] = resolved }));
        var features = new[] { "Afro", "flatTop" };
        var initialHair = ccMohawk ? null : style.MeshVariants[0];
        var document = new MorphFaceDocument(TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)), mesh.Source, initialHair,
            features.Select(name => new MorphFeatureValue(name, 0.5f)).ToArray(),
            [new BoneTranslation("root", Vector3.Zero)], MorphFaceMaterialOverrides.Empty, [mesh.Positions.ToArray()], []);
        var session = new MorphFaceEditingSession(document, mesh, features.Select(name => new MorphTargetAsset(
            TestFixtures.CreateIdentity($"Set.{name}", "MorphTarget"), [new MorphTargetLod(0, 3, [])], [])).ToArray());
        var profileKey = ccMohawk ? "le3-human-male" : "le1-human-male";
        var donorScalp = ccMohawk
            ? PlayerHairStyleCatalog.Resolve(game, PlayerHairSex.Male, candidates, meshes, meshLocked: true)
                .Single(value => value.Id == "hmm-sar").ScalpTextures
            : style.ScalpTextures;
        var donor = new MorphRandomisationDonor("paired-scalp", profileKey, new HashSet<string>(), new Dictionary<string, float>())
        {
            MaterialScalars = new Dictionary<string, float> { ["HED_Norm_Blend"] = 4 },
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>
                { ["human-scalp"] = donorScalp }
        };
        var corpus = new MorphRandomisationCorpus(MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
                { [ccMohawk ? MorphRandomisationPoolKey.HumanMaleLe3 : MorphRandomisationPoolKey.HumanMaleLe12] = [donor] })
        {
            MaterialProfiles = new Dictionary<string, MaterialRandomisationProfile>
            {
                [profileKey] = new(profileKey,
                    new Dictionary<string, MaterialScalarStatistics> { ["HED_Norm_Blend"] = new("HED_Norm_Blend", 1, 6, 2, 5) },
                    new Dictionary<string, MaterialVectorStatistics>(), new HashSet<string> { "human-scalp" })
            }
        };
        var editor = new FaceEditorViewModel(session, new HumanMaleFeatureMetadataCatalog(), material,
            new StubColorDialog(), new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "working.pcc", [], meshes.Select(value => new MorphFaceEditor.Models.PackageAssetListItem(
                new AssetIdentity(value.EffectiveOccurrence.PackagePath, value.CanonicalPath,
                    value.EffectiveOccurrence.ExportUIndex, "SkeletalMesh"))).ToArray(), initialHair, [],
            message => throw new Exception(message), profileKey, new MorphRandomisationCatalog(corpus), () => 771,
            registryTextureCandidates: candidates, textureCatalogProfile: TextureCatalogProfile.Empty, isTextureRegistryAvailable: true);
        editor.UpdateRegistryAttachmentMeshes(meshes, isPlayerWorkspace: false);
        foreach (var texture in editor.Material.Textures)
            typeof(MaterialTextureEditorViewModel).GetField("_references", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.SetValue(texture, new ImmediateTextureLoader());
        return new HairScalpFixture(editor, material, style, reader, sourceFile);
    }

    internal sealed class HairScalpFixture(FaceEditorViewModel editor, MaterialEditingSession material,
        PlayerHairStyleOption style, MorphFacePackageReader reader, FileStream sourceFile) : IDisposable
    {
        public FaceEditorViewModel Editor { get; } = editor;
        public MaterialEditingSession Material { get; } = material;
        public PlayerHairStyleOption Style { get; } = style;
        public MorphFacePackageReader Reader { get; } = reader;
        public void Dispose() { Editor.Dispose(); reader.Dispose(); sourceFile.Dispose(); }
    }

    private static void NormalRollClearsCursedState()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader, randomSeedFactory: () => 771,
            includeUnusedMaterialParameters: true);
        using var clean = CreateRandomisationEditor(reader, randomSeedFactory: () => 771,
            includeUnusedMaterialParameters: true);
        editor.CursedMode = true;
        editor.RandomisationStrength = 100;
        editor.RandomiseAsync().GetAwaiter().GetResult();
        var rolled = editor.CreateDraft();
        var manualBone = editor.Bones.First(value => value.IsAvailable);
        manualBone.Value += 0.125f;
        editor.UndoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(rolled, editor.CreateDraft()),
            "Undoing a manual bone edit changed the preceding Cursed roll.");
        editor.RedoCommand.Execute(null);
        var cursed = editor.CreateDraft();
        TestAssert.True(editor.Material.Scalars.Single(value => value.Name == "HED_Scalp_Mask_OverlayKill_Scalar").Value != 1,
            "The Cursed setup did not affect a parameter absent from the normal corpus.");
        editor.CursedMode = false;
        TestAssert.True(MorphFaceEditorStateComparer.Equals(cursed, editor.CreateDraft()),
            "Changing modes rewrote the authored head before another roll.");

        clean.RandomisationStrength = 100;
        clean.RandomiseMaterials = true;
        clean.MaterialRandomisationStrength = editor.MaterialRandomisationStrength = 37;
        clean.SetToDefaultsCommand.Execute(null);
        clean.Bones.Single(value => value.BoneName == manualBone.BoneName && value.Axis == manualBone.Axis).Value += 0.125f;
        clean.RandomiseAsync().GetAwaiter().GetResult();
        editor.RandomiseAsync().GetAwaiter().GetResult();
        var normal = editor.CreateDraft();
        var expected = clean.CreateDraft();
        TestAssert.True(expected.MorphFeatures.SequenceEqual(normal.MorphFeatures), "Normal morph values retained Cursed state.");
        foreach (var bone in expected.FinalSkeleton)
            TestAssert.Near(bone.Translation, normal.FinalSkeleton.Single(value => value.BoneName == bone.BoneName).Translation, 0.0001f);
        foreach (var scalar in clean.Material.Scalars)
            TestAssert.Near(scalar.Value, editor.Material.Scalars.Single(value => value.Name == scalar.Name).Value, 0);
        foreach (var vector in clean.Material.Vectors)
            TestAssert.Equal(vector.Value, editor.Material.Vectors.Single(value => value.Name == vector.Name).Value);
        editor.UndoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(cursed, editor.CreateDraft()), "Undo did not restore the complete Cursed state.");
        editor.RedoCommand.Execute(null);
        TestAssert.True(MorphFaceEditorStateComparer.Equals(normal, editor.CreateDraft()), "Redo did not restore the normal state.");
        editor.RandomiseAsync().GetAwaiter().GetResult();
        TestAssert.True(MorphFaceEditorStateComparer.Equals(normal, editor.CreateDraft()),
            "Replaying normal history left a Cursed contribution for a later roll to subtract twice.");

        using var sourceFile = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        using var player = CreateMaskDependencyEditor(reader, sourceFile.Name, false, out var playerMaterials, protectedEye: true);
        var nativeEye = player.Material.GetEffectiveTextureReference("EYE_Iris_Norm");
        player.CursedMode = true;
        player.MaterialRandomisationStrength = 100;
        player.RandomiseCommand.Execute(null);
        TestAssert.True(player.Material.GetEffectiveTextureReference("EYE_Iris_Norm") != nativeEye,
            "The Cursed setup did not replace the selection-protected Player eye map.");
        player.CursedMode = false;
        player.RandomiseCommand.Execute(null);
        TestAssert.True(!playerMaterials.HasCursedRandomisation, "A global normal roll with protected Player eyes left Cursed cleanup pending.");
        TestAssert.Equal(nativeEye, player.Material.GetEffectiveTextureReference("EYE_Iris_Norm"));
    }

    private static void ScopedMaterialRollsKeepUnselectedState()
    {
        using var reader = new MorphFacePackageReader();
        using (var editor = CreateRandomisationEditor(reader, randomSeedFactory: () => 771, includeUnusedMaterialParameters: true))
        {
            editor.CursedMode = true;
            editor.RandomiseMorphs = false;
            editor.MaterialRandomisationStrength = 100;
            editor.RandomiseCommand.Execute(null);
            editor.CursedMode = false;
            var group = editor.Categories.SelectMany(category => category.SliderGroups)
                .Single(value => value.Scalars.Any(scalar => scalar.Name == "HED_Norm_Blend"));
            var before = editor.CreateDraft();
            TestAssert.True(group.RandomiseCommand?.CanExecute(null) == true, "The selected numeric subcategory was unavailable.");
            group.RandomiseCommand!.Execute(null);
            var after = editor.CreateDraft();
            TestAssert.True(before.MaterialOverrides.Vectors.SequenceEqual(after.MaterialOverrides.Vectors) &&
                before.MaterialOverrides.Textures.SequenceEqual(after.MaterialOverrides.Textures) &&
                before.MaterialOverrides.Scalars.Where(value => value.Name != "HED_Norm_Blend")
                    .SequenceEqual(after.MaterialOverrides.Scalars.Where(value => value.Name != "HED_Norm_Blend")),
                "The first scoped normal roll reset unselected Cursed material values.");
            editor.UndoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(before, editor.CreateDraft()), "Scoped cleanup did not remain one Undo action.");
            editor.RedoCommand.Execute(null);
            TestAssert.True(MorphFaceEditorStateComparer.Equals(after, editor.CreateDraft()), "Scoped cleanup Redo lost authored state.");
        }

        foreach (var failParent in new[] { false, true })
        {
            using var sourceFile = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
            using var editor = CreateMaskDependencyEditor(reader, sourceFile.Name, failParent, out var material);
            editor.CursedMode = true;
            editor.MorphRandomisationStrength = editor.MaterialRandomisationStrength = 100;
            editor.RandomiseCommand.Execute(null);
            editor.CursedMode = false;
            editor.RandomiseCommand.Execute(null);
            TestAssert.True(!material.HasCursedRandomisation, "A fully permitted normal global roll retained pending Cursed cleanup.");
            var eyes = editor.Categories.Single(category => category.Key == "eyes").ColourGroups.Single(group => group.Key == "surface");
            var command = failParent
                ? editor.Categories.Single(category => category.Key == HumanMaleFeatureMetadataCatalog.Additions).TextureRandomiseCommand
                : eyes.RandomiseCommand;
            TestAssert.True(command?.CanExecute(null) == true, "The normal selected material command was unavailable.");
            var changedEyes = false;
            for (var roll = 0; roll < 6; roll++)
            {
                var before = editor.CreateDraft();
                var selector = material.GetVector("HED_Mask_Vector");
                var texture = editor.Material.GetEffectiveTextureReference("HED_Mask");
                command!.Execute(null);
                var after = editor.CreateDraft();
                changedEyes |= !before.MaterialOverrides.Vectors.SequenceEqual(after.MaterialOverrides.Vectors);
                TestAssert.Equal(selector, material.GetVector("HED_Mask_Vector"));
                TestAssert.Equal(texture, editor.Material.GetEffectiveTextureReference("HED_Mask"));
                TestAssert.True(before.MaterialOverrides.Scalars.SequenceEqual(after.MaterialOverrides.Scalars) &&
                    before.MaterialOverrides.Textures.SequenceEqual(after.MaterialOverrides.Textures),
                    "A colour-only or failed-parent roll changed unrelated authored material rows.");
                if (MorphFaceEditorStateComparer.Equals(before, after)) continue;
                editor.UndoCommand.Execute(null);
                TestAssert.True(MorphFaceEditorStateComparer.Equals(before, editor.CreateDraft()), "A scoped material roll split its Undo action.");
                editor.RedoCommand.Execute(null);
                TestAssert.True(MorphFaceEditorStateComparer.Equals(after, editor.CreateDraft()), "A scoped material roll lost its Redo state.");
            }
            if (!failParent) TestAssert.True(changedEyes, "The Eye subcategory scenario never changed its permitted colour.");
        }
    }

    private static FaceEditorViewModel CreateMaskDependencyEditor(MorphFacePackageReader reader, string sourcePath,
        bool failParent, out MaterialEditingSession material, bool protectedEye = false)
    {
        const string key = "le3-human-male";
        const string parent = "BIOG_HMM_HED_PROMorph_R.Masks.Donor_Mask3";
        var mesh = TestFixtures.CreateMesh();
        var document = new MorphFaceDocument(TestFixtures.CreateIdentity("Face", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)), mesh.Source, null,
            [new MorphFeatureValue("eyes_Big", 0)], [new BoneTranslation("root", Vector3.Zero)],
            MorphFaceMaterialOverrides.Empty, [mesh.Positions.ToArray()], []);
        var session = new MorphFaceEditingSession(document, mesh, [new MorphTargetAsset(
            TestFixtures.CreateIdentity("Set.eyes_Big", "MorphTarget"), [new MorphTargetLod(0, 3, [])], [])]);
        var identity = TestFixtures.CreateIdentity("Material", "MaterialInstanceConstant");
        var vectors = new Dictionary<string, Vector4>
        {
            ["HED_Mask_Vector"] = new(0, 0, 0, 1), ["EYE_Iris_Colour_Vector"] = Vector4.One, ["SkinTone"] = Vector4.One
        };
        if (protectedEye)
        {
            vectors.Remove("EYE_Iris_Colour_Vector");
            vectors.Remove("SkinTone");
        }
        var textures = new Dictionary<string, MaterialTextureBinding>
            { ["HED_Mask"] = new("HED_Mask", CreateTestTexture(sourcePath, "Current_Mask3", "HED_Mask")) };
        if (protectedEye) textures["EYE_Iris_Norm"] = new("EYE_Iris_Norm", CreateTestTexture(sourcePath, "CurrentEye_Norm", "EYE_Iris_Norm"));
        var surface = new ResolvedHeadMaterial(MaterialIdentityKey.Create(identity), identity,
            "HMM_HED_PRONPC_MASTER_FACE_MAT", HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float> { ["HED_Norm_Blend"] = 0.5f }, vectors,
            textures);
        material = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial> { [surface.Key] = surface }));
        var donor = new MorphRandomisationDonor("LE3:Mask3", key, new HashSet<string> { "eyes_Big" },
            new Dictionary<string, float> { ["eyes_Big"] = 0.2f })
        {
            MaterialScalars = new Dictionary<string, float> { ["HED_Norm_Blend"] = 0.4f },
            MaterialVectors = new Dictionary<string, Vector4>
            {
                ["HED_Mask_Vector"] = Vector4.One, ["EYE_Iris_Colour_Vector"] = new(0.2f, 0.4f, 0.6f, 1), ["SkinTone"] = Vector4.One
            },
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>
                { ["human-face-mask"] = new Dictionary<string, string> { ["HED_Mask"] = parent } }
        };
        if (protectedEye) donor = donor with
        {
            MaterialTextureFamilies = new Dictionary<string, IReadOnlyDictionary<string, string>>(donor.MaterialTextureFamilies)
                { ["human-eye"] = new Dictionary<string, string> { ["EYE_Iris_Norm"] = "BIOG_EYE.DonorEye_Norm" } }
        };
        var profile = new MaterialRandomisationProfile(key,
            new Dictionary<string, MaterialScalarStatistics> { ["HED_Norm_Blend"] = new("HED_Norm_Blend", 0, 1, 0.2f, 0.8f) },
            vectors.ToDictionary(value => value.Key, value => new MaterialVectorStatistics(value.Key,
                value.Key == "HED_Mask_Vector" ? MaterialVectorRandomisationKind.Selector : MaterialVectorRandomisationKind.PerceptualColour,
                Vector4.Zero, Vector4.One, [])), donor.MaterialTextureFamilies.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var corpus = new MorphRandomisationCorpus(MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
            {
                [MorphRandomisationPoolKey.HumanMaleLe3] = [donor, donor with
                {
                    Id = "LE3:Other",
                    MaterialVectors = new Dictionary<string, Vector4>(donor.MaterialVectors)
                        { ["EYE_Iris_Colour_Vector"] = new(0.7f, 0.1f, 0.2f, 1) }
                }]
            })
            { MaterialProfiles = new Dictionary<string, MaterialRandomisationProfile> { [key] = profile } };
        var occurrence = new TextureCatalogOccurrence(sourcePath, 42, 0, TextureCatalogOrigin.BaseGame,
            1, 1, "PF_B8G8R8A8", "Character", false, null);
        var candidate = new TextureCatalogCandidate(MorphFaceGame.LE3, parent, occurrence, [occurrence]) { IsEligibleOfficialDefault = true };
        TextureCatalogCandidate[] candidates = protectedEye
            ? [candidate, new TextureCatalogCandidate(MorphFaceGame.LE3, "BIOG_EYE.DonorEye_Norm", occurrence, [occurrence]) { IsEligibleOfficialDefault = true }]
            : [candidate];
        var seed = 770;
        var editor = new FaceEditorViewModel(session, new HumanMaleFeatureMetadataCatalog(), material, new StubColorDialog(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()), sourcePath, [], [], null, [],
            _ => { }, key, new MorphRandomisationCatalog(corpus), () => ++seed,
            registryTextureCandidates: candidates, textureCatalogProfile: TextureCatalogProfile.Empty, isTextureRegistryAvailable: true,
            playerRandomisationGame: protectedEye ? MorphFaceGame.LE3 : null);
        foreach (var texture in editor.Material.Textures)
            typeof(MaterialTextureEditorViewModel).GetField("_references", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.SetValue(texture, new ImmediateTextureLoader(failParent ? "Donor_Mask3" : null));
        return editor;
    }

    private static void AwaitableRandomisationPropagatesFailure()
    {
        using var reader = new MorphFacePackageReader();
        var expected = new InvalidOperationException("QA seed factory failure");
        using var editor = CreateRandomisationEditor(reader, randomSeedFactory: () => throw expected);
        editor.CursedMode = true;
        var before = editor.CreateDraft();
        try
        {
            editor.RandomiseAsync().GetAwaiter().GetResult();
            throw new Exception("Awaited randomisation swallowed its failure.");
        }
        catch (InvalidOperationException actual)
        {
            TestAssert.True(ReferenceEquals(actual, expected), "Automation lost the original randomisation error.");
        }
        TestAssert.True(MorphFaceEditorStateComparer.Equals(before, editor.CreateDraft()) && !editor.UndoCommand.CanExecute(null),
            "A failed awaited randomisation committed a partial edit.");
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

    private static void ManualHairScalpPromptKeepsRequiredPairWhenOptionalMapFails()
    {
        using var sourceFile = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
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
        var candidates = family.Values.Select((path, index) =>
        {
            var occurrence = new TextureCatalogOccurrence(
                sourceFile.Name, 42 + index, 0, TextureCatalogOrigin.BaseGame, 1, 1,
                "PF_B8G8R8A8", "TEXTUREGROUP_Character", false, null);
            return new TextureCatalogCandidate(MorphFaceGame.LE3, path, occurrence, [occurrence])
            { IsEligibleOfficialDefault = true };
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
        TestAssert.True(EditorRandomisationService.WereAllTextureFamiliesRejected(
                eligibleSignatureCount: 2,
                excludedSignatureCount: 2,
                appliedTextureFamilies: 0),
            "Exhausting every eligible texture family was silently treated as a complete randomisation.");
        TestAssert.True(!EditorRandomisationService.WereAllTextureFamiliesRejected(2, 1, 1),
            "A successful fallback texture family was incorrectly reported as exhausted.");
        TestAssert.True(!EditorRandomisationService.WereAllTextureFamiliesRejected(0, 0, 0),
            "A numeric-only material randomisation was incorrectly reported as a texture failure.");
    }
}
