using System.Numerics;
using System.Threading;
using System.Windows;
using MorphFaceEditor.Core.Domain;
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

namespace MorphFaceEditor.Tests;

// Binding-safe UI checks; these avoid launching the full window or loading game packages.
public static class UiSmokeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("combo-box models display their labels", ComboModelsDisplayLabels),
        new("LOD-specific morph controls are explicitly marked", LodSpecificMorphControlsAreMarked),
        new("morph controls disable when the selected LOD has no target geometry", MorphControlsDisableForTargetlessLod),
        new("texture thumbnails discard alpha", TextureThumbnailsDiscardAlpha),
        new("numeric wheel increments are finite and crash-safe", NumericWheelIncrementsAreSafe),
        new("extended sliders are optional and preserve edited values", ExtendedSlidersAreOptional),
        new("bone controls tolerate bones removed by zeroed morphs", BoneControlsTolerateRemovedMorphBones),
        new("attachment editor exposes two slots and preserves extras", AttachmentEditorUsesTwoSlots),
        new("morph clipboard codec round-trips typed versioned data", MorphClipboardCodecRoundTrips),
        new("pasted morph and material data remain live unsaved edits", PastedDataRemainsLiveAndDirty),
        new("face editor category selection can be restored by key", FaceEditorCategoryRestoresByKey),
        new("randomisation commands honour global and subcategory morph scopes", RandomisationCommandsHonorScopes),
        new("Set to Defaults restores stock morph and material values atomically", SetToDefaultsRestoresStockState),
        new("global morph and material randomisation uses separate donors", GlobalRandomisationUsesSeparateDonors),
        new("subcategory inclusion toggles filter only global randomisation and persist in-session", SubcategoryInclusionsFilterGlobalScope),
        new("normal material randomisation obeys its independent toggle and undo", MaterialRandomisationObeysToggle),
        new("material vector subcategories expose independent randomise commands", MaterialVectorSubcategoriesRandomiseIndependently),
        new("exhausted texture randomisation is reported without discarding numeric values", ExhaustedTextureRandomisationIsReported),
        new("LE3 HMM scalp randomisation preserves its required texture pair", Le3HmmScalpRandomisationAppliesCorePair),
        new("cursed mode randomises morph bones and materials as one undo step", CursedModeRandomisesOneUndoStep),
        new("cursed mode ignores global randomisation exclusions", CursedModeIgnoresGlobalExclusions),
        new("cursed mode can be enabled without a donor corpus", CursedModeBypassesDonorAvailability),
        new("failed cursed randomisation rolls back its partial edit", FailedCursedRandomisationRollsBack),
        new("repeated cursed randomisation does not compound", RepeatedCursedRandomisationDoesNotCompound),
        new("embedded randomisation corpus loads all pools and excludes Broke", EmbeddedRandomisationCorpusLoads),
        new("editor error banners can be dismissed", ErrorBannerCanBeDismissed),
        new("texture registry settings command opens the settings dialog", TextureRegistrySettingsCommandOpensDialog),
        new("WPF resources construct and nested menus expose their popup", HdrPickerConstructs),
        new("Human Male UI profile orders, groups, and filters features", HumanMaleProfileOrganizesFeatures),
        new("LE3 Human Male UI hides inert eye metadata and marks vestigial pupils", Le3HumanMaleProfileOrganizesFeatures),
        new("Human Female UI profile exposes female morph and makeup controls", HumanFemaleProfileOrganizesFeatures),
        new("LE3 Human Female UI exposes character targets and hides malformed hair targets", Le3HumanFemaleProfileOrganizesFeatures),
        new("Asari UI profile exposes head-crest and species material controls", AsariProfileOrganizesFeatures),
        new("Salarian UI profile exposes cranial-ring and species material controls", SalarianProfileOrganizesFeatures),
        new("Turian UI profile exposes mandibles, head spikes, and species material controls", TurianProfileOrganizesFeatures),
        new("Batarian UI profile groups racial structure and species material controls", BatarianProfileOrganizesFeatures),
        new("Krogan UI profile separates head plates and Wrex character controls", KroganProfileOrganizesFeatures)
    ];

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
            new PackageReferenceService(reader),
            writer,
            context,
            new MorphFaceConversionService(profiles, targets, writer, context),
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
            bridge.Inclusion!.IsIncluded = false;
        }

        using var second = CreateRandomisationEditor(reader, randomisationInclusionState: inclusionState);
        second.RandomisationStrength = 0;
        var restoredBridge = second.Categories.Single(value => value.Key == "nose")
            .SliderGroups.Single(value => value.Key == "bridge");
        TestAssert.True(restoredBridge.Inclusion?.IsIncluded == false,
            "The subcategory exclusion was lost when the editor was recreated.");

        second.RandomiseCommand.Execute(null);
        TestAssert.Near(0, second.CreateDraft().GetFeatureOffset("nose_BridgeIn"), 0);
        TestAssert.Near(0.7f, second.CreateDraft().GetFeatureOffset("eyes_Big"), 0);

        restoredBridge.RandomiseCommand!.Execute(null);
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

    private static void CursedModeIgnoresGlobalExclusions()
    {
        using var reader = new MorphFacePackageReader();
        using var editor = CreateRandomisationEditor(reader);
        foreach (var inclusion in editor.Categories.SelectMany(category =>
                     category.SliderGroups.Select(group => group.Inclusion)
                         .Concat(category.ColourGroups.Select(group => group.Inclusion))
                         .Append(category.TextureInclusion))
                     .Where(value => value is not null))
        {
            inclusion!.IsIncluded = false;
        }
        editor.CursedMode = true;
        editor.MorphRandomisationStrength = 100;
        editor.MaterialRandomisationStrength = 100;

        editor.RandomiseCommand.Execute(null);

        var cursed = editor.CreateDraft();
        TestAssert.True(cursed.MorphFeatures.All(value => value.Offset != 0),
            "Cursed Mode respected an excluded morph category.");
        TestAssert.True(cursed.MaterialOverrides.Scalars.Count > 0 && cursed.MaterialOverrides.Vectors.Count > 0,
            "Cursed Mode respected an excluded material category.");
        TestAssert.True(cursed.MaterialOverrides.Scalars.Any(value => value.Name == "Emis_Scalar") &&
                        cursed.MaterialOverrides.Vectors.Any(value => value.Name == "Emis_Color"),
            "Cursed Mode retained the normal randomiser's visual-safety exclusions.");
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
        TestAssert.Equal(9, catalog.Corpus.Pools.Count(value => value.Value.Count > 0));
        TestAssert.Equal(1387, catalog.Corpus.Pools.Sum(value => value.Value.Count));
        TestAssert.True(catalog.Corpus.Pools.Values.SelectMany(value => value).All(donor =>
                !donor.Id.EndsWith("Human Male.LE3_HMM_Morphs.Broke", StringComparison.OrdinalIgnoreCase)),
            "The known broken LE3 HMM donor remained in the embedded corpus.");
    }

    private static MainWindowViewModel CreateMainWindowViewModel(
        MorphFacePackageReader reader,
        StubEditorDialogs? dialogs = null)
    {
        var sceneFactory = new HeadPreviewSceneFactory();
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var targets = new MorphTargetCatalog();
        var writer = new MorphFacePackageWriter();
        var context = new MorphFacePackageContextService();
        return new MainWindowViewModel(
            dialogs ?? new StubEditorDialogs(),
            new MorphFaceCatalogService(profiles),
            new MorphFacePreviewLoadService(sceneFactory, targets, profiles, reader),
            sceneFactory,
            new StubColorDialog(),
            new PackageReferenceService(reader),
            writer,
            context,
            new MorphFaceConversionService(profiles, targets, writer, context),
            new MorphFaceInterchangeService(),
            new StubClipboard());
    }

    private static FaceEditorViewModel CreateEditor(MorphFacePackageReader reader)
    {
        var mesh = TestFixtures.CreateMesh();
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
            [TestFixtures.CreateTarget()]);
        var materialSession = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            ResolvedHeadMaterialSet.Empty);
        return new FaceEditorViewModel(
            morphSession,
            new HumanMaleFeatureMetadataCatalog(),
            materialSession,
            new StubColorDialog(),
            new PackageReferenceService(reader),
            "fixture.pcc",
            [],
            [],
            null,
            [],
            _ => { });
    }

    private static FaceEditorViewModel CreateRandomisationEditor(
        MorphFacePackageReader reader,
        bool includeDonor = true,
        bool extremeBoneOffset = false,
        RandomisationInclusionState? randomisationInclusionState = null,
        Func<int>? randomSeedFactory = null,
        bool includeSecondDonor = false)
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
            [new MorphFeatureValue("nose_BridgeIn", 0), new MorphFeatureValue("eyes_Big", 0)],
            [new BoneTranslation("root", Vector3.Zero), new BoneTranslation("nose_tip", new Vector3(2, 4, 6))],
            MorphFaceMaterialOverrides.Empty,
            [mesh.Positions.ToArray()],
            []);
        var session = new MorphFaceEditor.Core.Editing.MorphFaceEditingSession(
            document, mesh, [Target("nose_BridgeIn"), Target("eyes_Big")]);
        var materialIdentity = TestFixtures.CreateIdentity("HeadMaterial", "MaterialInstanceConstant");
        var resolvedMaterial = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(materialIdentity), materialIdentity, "BIOG_HMM_HED_PROMorph",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float> { ["HED_Norm_Blend"] = 2, ["Emis_Scalar"] = 1 },
            new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.One, ["Emis_Color"] = Vector4.One },
            new Dictionary<string, MaterialTextureBinding>());
        var materials = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [resolvedMaterial.Key] = resolvedMaterial
            }));
        var donor = new MorphRandomisationDonor(
            "LE1:Seed", "le1-human-male",
            new HashSet<string>(["nose_BridgeIn", "eyes_Big"], StringComparer.OrdinalIgnoreCase),
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
        var corpus = new MorphRandomisationCorpus(
            MorphRandomisationCorpus.CurrentFormatVersion,
            new Dictionary<MorphRandomisationPoolKey, IReadOnlyList<MorphRandomisationDonor>>
            {
                [MorphRandomisationPoolKey.HumanMaleLe12] = includeDonor
                    ? includeSecondDonor ? [donor, secondDonor] : [donor]
                    : []
            })
        {
            MaterialProfiles = includeDonor
                ? new Dictionary<string, MaterialRandomisationProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    ["le1-human-male"] = new MaterialRandomisationProfile(
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
                        new HashSet<string>())
                }
                : new Dictionary<string, MaterialRandomisationProfile>()
        };
        return new FaceEditorViewModel(
            session,
            new HumanMaleFeatureMetadataCatalog(),
            materials,
            new StubColorDialog(),
            new PackageReferenceService(reader),
            "fixture.pcc",
            [], [], null, [], _ => { },
            "le1-human-male",
            new MorphRandomisationCatalog(corpus),
            randomSeedFactory ?? (() => 123),
            randomisationInclusionState);
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
            new PackageReferenceService(reader),
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
        public void Set(MorphFaceClipboardPayload payload) => throw new NotSupportedException();
        public MorphFaceClipboardPayload Get(MorphFaceClipboardKind expectedKind) => throw new NotSupportedException();
        public bool Contains(MorphFaceClipboardKind expectedKind) => false;
    }

    private sealed class StubEditorDialogs : IEditorDialogService
    {
        public bool TextureRegistrySettingsWasShown { get; private set; }
        public string? ChoosePackage(string? initialDirectory = null) => null;
        public MorphPackageSaveRequest? ChooseMorphPackageDestination(string suggestedFileName, string sourcePackagePath) => null;
        public MorphConversionSaveRequest? ChooseMorphConversionDestination(MorphFaceGame sourceGame, string suggestedFileName, string sourcePackagePath) => null;
        public string? ChooseCloneName(string suggestedName, IReadOnlyCollection<string> existingObjectNames) => null;
        public string? ChooseMorphImportFile(string? initialDirectory = null) => null;
        public string? ChooseRonExportFile(string suggestedFileName, string? initialDirectory = null) => null;
        public string? ChooseMeshExportDirectory(string? initialDirectory = null) => null;
        public bool ConfirmDeleteMorph(string facePath) => false;
        public UnsavedChangesChoice ConfirmUnsavedChanges(string assetPath, UnsavedChangesScope scope = UnsavedChangesScope.Package) => UnsavedChangesChoice.Cancel;
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
                using var reader = new MorphFacePackageReader();
                using var viewModel = CreateMainWindowViewModel(reader);
                var mainWindow = new MainWindow(viewModel);
                mainWindow.ShowInTaskbar = false;
                mainWindow.Opacity = 0;
                mainWindow.Show();
                var randomise = mainWindow.FindName("GlobalRandomiseButton") as Button;
                var setToDefaults = mainWindow.FindName("SetToDefaultsButton") as Button;
                var morphStrength = mainWindow.FindName("MorphRandomisationStrengthSlider") as Slider;
                var materialStrength = mainWindow.FindName("MaterialRandomisationStrengthSlider") as Slider;
                var morphToggle = mainWindow.FindName("RandomiseMorphsCheckBox") as CheckBox;
                var materialToggle = mainWindow.FindName("RandomiseMaterialsCheckBox") as CheckBox;
                var cursedMode = mainWindow.FindName("CursedModeCheckBox") as CheckBox;
                var morphStrengthLabel = mainWindow.FindName("MorphRandomisationStrengthLabel") as TextBlock;
                var morphStrengthValue = mainWindow.FindName("MorphRandomisationStrengthValue") as TextBlock;
                var materialStrengthLabel = mainWindow.FindName("MaterialRandomisationStrengthLabel") as TextBlock;
                var materialStrengthValue = mainWindow.FindName("MaterialRandomisationStrengthValue") as TextBlock;
                var hairLabel = mainWindow.FindName("HairAccessoryMeshesLabel") as TextBlock;
                var inclusionStyle = application.TryFindResource("RandomisationIncludeToggle") as Style;
                TestAssert.True(inclusionStyle is not null && inclusionStyle.TargetType == typeof(CheckBox),
                    "The filled subcategory-inclusion checkbox style is missing.");
                TestAssert.True(inclusionStyle!.Setters.OfType<Setter>().Any(value =>
                                        value.Property == FrameworkElement.WidthProperty && Equals(value.Value, 20d)) &&
                                    inclusionStyle.Setters.OfType<Setter>().Any(value =>
                                        value.Property == FrameworkElement.HeightProperty && Equals(value.Value, 20d)),
                    "The filled subcategory-inclusion checkbox is not sized alongside its Randomise button.");
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

    private sealed class ImmediateTextureLoader : ITextureReferenceLoader
    {
        public Task<DecodedTextureAsset> LoadTextureAsync(
            string packagePath,
            string texturePath,
            MaterialParameterDefinition definition,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateTestTexture(packagePath, texturePath, definition.Name));
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
        TestAssert.Equal("additions", profile.GetMaterialCategory(
            "HED_Mask", MaterialParameterKind.Texture));
        TestAssert.Equal("additions", profile.GetMaterialCategory(
            "HED_Mask_Vector", MaterialParameterKind.Vector));
        TestAssert.Equal("addition", profile.GetMaterialSubcategory(
            "HED_Mask_Vector", MaterialParameterKind.Vector));
        TestAssert.True(!profile.IsMaterialVisible("Diffuseuse", MaterialParameterKind.Texture),
            "The HMM Diffuseuse texture remained visible.");
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
        TestAssert.Equal("Hair Diffuse", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
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
        TestAssert.Equal("Pulled Back — Slick", hair.Label);
        TestAssert.Equal("facial-structure", hair.CategoryKey);
        TestAssert.Equal("hair", hair.SubcategoryKey);
        TestAssert.True(hair.IsVisible, "The HMF pulled-back hair morph was hidden.");

        var iconic = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("race_iconic", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Iconic Shepard — Blend", iconic.Label);
        TestAssert.Equal("character", iconic.SubcategoryKey);

        var headIconic = profile.Describe(new MorphFaceEditor.Core.Deformation.ResolvedMorphFeature(
            new MorphFeatureValue("Iconic", 0), target,
            MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.DirectTarget, null), true);
        TestAssert.Equal("Iconic Shepard — Head", headIconic.Label);
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
        TestAssert.Equal("Asian — Young", mouthRace.Label);
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
        TestAssert.Equal("material-hair", profile.GetMaterialSubcategory("Highlight2Color", MaterialParameterKind.Vector));
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
        TestAssert.Equal("Makeup Region Weights (R Lips, G Eyes, B Teeth)",
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
        TestAssert.Equal("Teeth Opacity Mask", teethMask.Label);
        TestAssert.Equal("mouth", profile.GetMaterialCategory("Mask", MaterialParameterKind.Scalar));
        TestAssert.Equal("teeth", profile.GetMaterialSubcategory("Mask", MaterialParameterKind.Scalar));
        TestAssert.True(teethMask.Description.Contains("under 0.33", StringComparison.Ordinal),
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
        TestAssert.Equal("Teeth Opacity Mask", teethOpacity.Label);
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
        TestAssert.Equal("Outer Plates — Thin", outerThin.Label);
        TestAssert.Equal("Inner Plates — Thin", innerThin.Label);
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
            "Outer Plates — Thin,Inner Plates — Thin,Plate Rim Down,Plate Rim Up,Plate Rim Forward Slant,Plate Erode,Spikes Smooth,Spikes Flare");
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
