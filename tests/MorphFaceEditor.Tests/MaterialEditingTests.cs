using System.Numerics;
using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the material editing regression set.
public static class MaterialEditingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("material scalar drag and HDR colour use semantic history", MaterialHistoryIsSemantic),
        new("material overrides are effective before the first edit", MaterialOverridesAreInitiallyEffective),
        new("material history tolerates redundant and reordered pointer completion", MaterialHistoryToleratesPointerCompletion),
        new("material randomisation batch is atomic and reversible", MaterialRandomisationBatchIsAtomic),
        new("material defaults clear loaded overrides and undo", MaterialDefaultsClearLoadedOverrides),
        new("LE2 and LE3 inherited eye emissive defaults stay disabled until authored", InheritedEyeEmissiveDefaultsToZero),
        new("installed LE2 LE3 player eye defaults cover HMM and HMF", PlayerMaterialRegressionTests.PlayerEyeDefaultsCoverBothSexes),
        new("material paste restores defaults for omitted values", MaterialPasteRestoresOmittedDefaults),
        new("HDR picker previews live and commits once on Apply", HdrPreviewCommitsOnce),
        new("package texture reference updates detached bindings and undoes", PackageTextureReferenceUpdatesBindings),
        new("package texture reference supports None and undo", PackageTextureReferenceSupportsNone),
        new("transient texture preview stays outside authored state and history", TransientTexturePreviewStaysOutsideAuthoredState),
        new("None restores the rendered material default after replacement", NoneRestoresRenderedDefault),
        new("attachment preparation leaves live materials and history untouched", AttachmentPreparationDoesNotPublish),
        new("attachment selection rebases live numeric material controls", AttachmentNumericControlsRebase),
        new("compiled empty hair support hides ineffective highlight controls and preserves authored overrides", EmptyCompiledHairSupportHidesHighlights),
        new("attachment numeric edits remain undoable after selection changes", AttachmentNumericEditsRemainUndoable),
        new("multi-slot attachment keeps each material's own hair diffuse", MultiSlotAttachmentKeepsOwnDiffuse),
        new("attachment Diffuseuse stays out of head material output", AttachmentTextureIsNotHeadOutput),
        new("saved Hat preview diffuse stays out of reloaded head controls and exports", SavedHatDiffuseIsPreviewOnly),
        new("duplicate face texture overrides remain loadable", DuplicateTextureOverridesRemainLoadable),
        new("preview-only Kaidan controls preserve authored data and shared head controls", KaidanPreviewDoesNotContributeControls),
        new("Human Hair numeric edits remain BioMaterialOverrides and preserve the preview model", HumanHairEditsRemainBioMaterialOverrides)
    ];

    private static void HumanHairEditsRemainBioMaterialOverrides()
    {
        var identity = TestFixtures.CreateIdentity("JamesHair", "MaterialInstanceConstant");
        var hair = new ResolvedHeadMaterial(MaterialIdentityKey.Create(identity), identity,
            "Human_Hair_MASTER", HeadMaterialFamily.Hair, HeadMaterialBlendMode.Translucent, true,
            new Dictionary<string, float> { ["Aniso_Highlight_1"] = 15, ["Aniso_Highlight_2"] = 1500 },
            new Dictionary<string, Vector4>
            {
                ["Hair_Color"] = new Vector4(0.1f, 0.08f, 0.02f, 1),
                ["Aniso_Color_1"] = Vector4.One,
                ["Aniso_Color_2"] = Vector4.One
            }, new Dictionary<string, MaterialTextureBinding>())
        {
            PreviewModel = HumanMaterialPreviewModel.HumanHair
        };
        var materials = new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial> { [hair.Key] = hair });
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, materials);
        session.SetScalar("Aniso_Highlight_1", 40);
        session.SetScalar("Aniso_Highlight_2", 300);
        foreach (var name in new[] { "Hair_Color", "Aniso_Color_1", "Aniso_Color_2" })
            session.SetVector(name, new Vector4(0.8f, 0.2f, 0.1f, 1));
        var saved = session.CreateOverrides();
        TestAssert.Equal(2, saved.Scalars.Count);
        TestAssert.Equal(3, saved.Vectors.Count);
        var reloaded = new MaterialEditingSession(saved, materials);
        foreach (var value in saved.Scalars)
            TestAssert.Near(value.Value, reloaded.Materials.Materials[hair.Key].Scalars[value.Name], 0);
        foreach (var value in saved.Vectors)
            TestAssert.Equal(value.Value, reloaded.Materials.Materials[hair.Key].Vectors[value.Name]);
        TestAssert.Equal(HumanMaterialPreviewModel.HumanHair, reloaded.Materials.Materials[hair.Key].PreviewModel);
        TestAssert.Equal(hair.Scalars["Aniso_Highlight_1"], 15f);
        TestAssert.Equal(hair.Vectors["Aniso_Color_1"], Vector4.One);
    }

    private static void KaidanPreviewDoesNotContributeControls()
    {
        var identity = TestFixtures.CreateIdentity("KaidanScalp", "MaterialInstanceConstant");
        var textureIdentity = TestFixtures.CreateIdentity("KaidanNormal", "Texture2D");
        var texture = new DecodedTextureAsset(textureIdentity, 1, 1, [128, 128, 255, 255],
            "PF_B8G8R8A8", TextureRole.Normal, TextureColorSpace.Linear, TextureAlphaPolicy.Ignore, false, "kaidan");
        var kaidan = new ResolvedHeadMaterial(MaterialIdentityKey.Create(identity), identity,
            "BIOG_HMM_HED_Kaiden_MASTER", HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float> { ["HED_Spec_Scalar"] = 1, ["HED_TMis_Scalar"] = 0.25f },
            new Dictionary<string, Vector4> { ["HED_TClr_Vector"] = Vector4.One },
            new Dictionary<string, MaterialTextureBinding> { ["Nm"] = new("Nm", texture) })
        {
            PreviewModel = HumanMaterialPreviewModel.Kaiden,
            ContributesFaceControls = false
        };
        var overrides = new MorphFaceMaterialOverrides(null,
            [new ScalarMaterialOverride("HED_Spec_Scalar", 2)],
            [new VectorMaterialOverride("HED_TClr_Vector", new Vector4(0.1f, 0.2f, 0.3f, 1))],
            [new TextureMaterialOverride("Nm", textureIdentity)]);
        var session = new MaterialEditingSession(overrides, new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [kaidan.Key] = kaidan }));
        TestAssert.Equal(0, session.ScalarNames.Count);
        TestAssert.Equal(0, session.VectorNames.Count);
        TestAssert.Equal(0, session.TextureParameters.Count);
        TestAssert.Equal(overrides.Scalars.Single(), session.CreateOverrides().Scalars.Single());
        TestAssert.Equal(overrides.Vectors.Single(), session.CreateOverrides().Vectors.Single());
        TestAssert.Equal(overrides.Textures.Single(), session.CreateOverrides().Textures.Single());
        TestAssert.Equal(overrides.Textures.Single(), session.CaptureInterchangeData().Textures.Single());
        TestAssert.True(session.Materials.Materials[kaidan.Key].Textures.ContainsKey("Nm"),
            "Hiding controls removed the authored preview normal.");
        TestAssert.Near(0.25f, session.Materials.Materials[kaidan.Key].Scalars["HED_TMis_Scalar"], 0);

        var head = CreateSession().Materials.Materials.Values.Single() with
        {
            Scalars = new Dictionary<string, float> { ["HED_TMis_Scalar"] = 0.75f },
            DefaultScalars = new Dictionary<string, float> { ["HED_TMis_Scalar"] = 0.75f }
        };
        var shared = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [head.Key] = head, [kaidan.Key] = kaidan
            }), [head.Key]);
        TestAssert.True(shared.ScalarNames.Contains("HED_TMis_Scalar"), "Kaidan hid a valid head control.");
        TestAssert.Near(0.75f, shared.GetScalar("HED_TMis_Scalar"), 0);
        shared.SetScalar("HED_TMis_Scalar", 0.5f);
        TestAssert.Near(0.5f, shared.Materials.Materials[kaidan.Key].Scalars["HED_TMis_Scalar"], 0);
        TestAssert.Near(1, shared.Materials.Materials[kaidan.Key].Scalars["HED_Spec_Scalar"], 0);
        shared.ApplyMaterialSurface(shared.PrepareAttachmentMaterials(new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [kaidan.Key] = kaidan })));
        TestAssert.True(!shared.ScalarNames.Contains("HED_Spec_Scalar"), "Replacement exposed a Kaidan-only control.");
    }

    private static void MaterialHistoryIsSemantic()
    {
        var session = CreateSession();
        session.BeginScalarEdit("HED_Norm_Blend");
        session.SetScalar("HED_Norm_Blend", 0.25f);
        session.SetScalar("HED_Norm_Blend", 0.75f);
        TestAssert.True(!session.CanUndo, "Material slider drag was committed before release.");
        session.EndScalarEdit("HED_Norm_Blend");
        TestAssert.True(session.CanUndo, "Material slider drag did not create history.");
        session.Undo();
        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 1e-6f);

        var edited = new Vector4(2, 0.2f, 0.1f, 1);
        session.SetVector("SkinTone", edited);
        TestAssert.Equal(edited, session.GetVector("SkinTone"));
        session.Undo();
        TestAssert.Equal(Vector4.One, session.GetVector("SkinTone"));
    }

    private static void MaterialOverridesAreInitiallyEffective()
    {
        var identity = TestFixtures.CreateIdentity("TurianHead", "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "TUR_HED_PRO_MASTER_MAT",
            HeadMaterialFamily.TurianSkin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float> { ["TUR_HED_Spwr_Skin_Scalar"] = 0 },
            new Dictionary<string, Vector4> { ["TUR_HED_Spec_Colour"] = Vector4.Zero },
            new Dictionary<string, MaterialTextureBinding>());
        var overrides = new MorphFaceMaterialOverrides(
            null,
            [new ScalarMaterialOverride("TUR_HED_Spwr_Skin_Scalar", 0.75f)],
            [new VectorMaterialOverride("TUR_HED_Spec_Colour", Vector4.One)],
            []);

        var session = new MaterialEditingSession(overrides, new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material }));
        var effective = session.Materials.Materials.Values.Single();

        TestAssert.Near(0.75f, effective.Scalars["TUR_HED_Spwr_Skin_Scalar"], 1e-6f);
        TestAssert.Equal(Vector4.One, effective.Vectors["TUR_HED_Spec_Colour"]);
    }

    private static void MaterialHistoryToleratesPointerCompletion()
    {
        var session = CreateSession();
        session.BeginScalarEdit("HED_Norm_Blend");
        session.SetScalar("HED_Norm_Blend", 0.75f);
        session.EndScalarEdit("HED_Norm_Blend");
        session.EndScalarEdit("HED_Norm_Blend");
        TestAssert.True(session.CanUndo, "A redundant pointer release discarded the edit.");
        session.Undo();
        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 1e-6f);

        session.BeginScalarEdit("HED_Norm_Blend");
        session.SetScalar("HED_Norm_Blend", 0.25f);
        session.BeginScalarEdit("HED_Addn_Blend_Scalar");
        session.SetScalar("HED_Addn_Blend_Scalar", 0.8f);
        session.EndScalarEdit("HED_Norm_Blend");
        session.EndScalarEdit("HED_Addn_Blend_Scalar");
        session.Undo();
        TestAssert.Near(0f, session.GetScalar("HED_Addn_Blend_Scalar"), 1e-6f);
        session.Undo();
        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
    }

    private static void HdrPreviewCommitsOnce()
    {
        var session = CreateSession();
        var commits = 0;
        session.EditCommitted += (_, _) => commits++;
        var before = session.GetVector("SkinTone");
        session.PreviewVector("SkinTone", new Vector4(0.8f, 0.2f, 0.1f, 1));
        session.PreviewVector("SkinTone", new Vector4(1.4f, 0.4f, 0.2f, 1));
        TestAssert.True(!session.CanUndo, "Live HDR preview created undo entries before Apply.");
        var after = session.GetVector("SkinTone");
        session.CompleteVectorPreview("SkinTone", before, after, apply: true);
        TestAssert.Equal(1, commits);
        session.Undo();
        TestAssert.Equal(before, session.GetVector("SkinTone"));

        session.PreviewVector("SkinTone", new Vector4(4, 3, 2, 1));
        session.CompleteVectorPreview("SkinTone", before, session.GetVector("SkinTone"), apply: false);
        TestAssert.Equal(before, session.GetVector("SkinTone"));
        TestAssert.Equal(1, commits);
    }

    private static void MaterialRandomisationBatchIsAtomic()
    {
        var session = CreateSession();
        var replacementIdentity = TestFixtures.CreateIdentity("RandomisedDiffuse", "Texture2D");
        var replacement = new DecodedTextureAsset(
            replacementIdentity, 1, 1, [48, 96, 144, 255], "PF_B8G8R8A8",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false,
            "randomised");
        var commits = 0;
        var refreshes = 0;
        session.EditCommitted += (_, _) => commits++;
        session.MaterialsChanged += (_, args) =>
        {
            if (args.Kind == MaterialChangeKind.Full) refreshes++;
        };

        session.SetValues(
            new Dictionary<string, float>
            {
                ["HED_Norm_Blend"] = 0.2f,
                ["HED_Addn_Blend_Scalar"] = 0.7f
            },
            new Dictionary<string, Vector4> { ["SkinTone"] = new(0.3f, 0.5f, 0.8f, 1) },
            new Dictionary<string, DecodedTextureAsset?> { ["HED_Diff"] = replacement });

        TestAssert.Equal(1, commits);
        TestAssert.Equal(1, refreshes);
        TestAssert.Near(0.2f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
        TestAssert.Equal("randomised", session.GetSelectedTexture("HED_Diff")?.CacheKey);

        session.Undo();
        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
        TestAssert.Near(0f, session.GetScalar("HED_Addn_Blend_Scalar"), 1e-6f);
        TestAssert.Equal(Vector4.One, session.GetVector("SkinTone"));
        TestAssert.Equal("original", session.GetSelectedTexture("HED_Diff")?.CacheKey);

        session.Redo();
        TestAssert.Near(0.2f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
        TestAssert.Equal("randomised", session.GetSelectedTexture("HED_Diff")?.CacheKey);

        try
        {
            session.SetValues(
                new Dictionary<string, float> { ["HED_Norm_Blend"] = 0.9f },
                new Dictionary<string, Vector4> { ["UnknownVector"] = Vector4.Zero },
                new Dictionary<string, DecodedTextureAsset?>());
            throw new InvalidOperationException("Invalid material batch was accepted.");
        }
        catch (ArgumentException)
        {
            TestAssert.Near(0.2f, session.GetScalar("HED_Norm_Blend"), 1e-6f);
        }
    }

    private static void MaterialDefaultsClearLoadedOverrides()
    {
        var identity = TestFixtures.CreateIdentity("DefaultableHead", "MaterialInstanceConstant");
        var defaultTexture = new DecodedTextureAsset(
            TestFixtures.CreateIdentity("DefaultDiffuse", "Texture2D"),
            1, 1, [32, 64, 96, 255], "PF_B8G8R8A8", TextureRole.Diffuse,
            TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "default-diffuse");
        var overrideTexture = new DecodedTextureAsset(
            TestFixtures.CreateIdentity("OverrideDiffuse", "Texture2D"),
            1, 1, [96, 64, 32, 255], "PF_B8G8R8A8", TextureRole.Diffuse,
            TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "override-diffuse");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "BIOG_HMM_HED_PROMorph",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float> { ["HED_Norm_Blend"] = 0.9f },
            new Dictionary<string, Vector4> { ["SkinTone"] = new(0.2f, 0.3f, 0.4f, 1) },
            new Dictionary<string, MaterialTextureBinding>
            {
                ["HED_Diff"] = new("HED_Diff", overrideTexture)
            })
        {
            DefaultScalars = new Dictionary<string, float> { ["HED_Norm_Blend"] = 0.5f },
            DefaultVectors = new Dictionary<string, Vector4> { ["SkinTone"] = Vector4.One },
            DefaultTextures = new Dictionary<string, MaterialTextureBinding>
            {
                ["HED_Diff"] = new("HED_Diff", defaultTexture)
            }
        };
        var overrides = new MorphFaceMaterialOverrides(
            TestFixtures.CreateIdentity("Overrides", "BioMaterialOverride"),
            [new ScalarMaterialOverride("HED_Norm_Blend", 0.9f)],
            [new VectorMaterialOverride("SkinTone", new Vector4(0.2f, 0.3f, 0.4f, 1))],
            [new TextureMaterialOverride("HED_Diff", overrideTexture.Source)]);
        var session = new MaterialEditingSession(overrides, new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material }));

        session.ResetToDefaults();

        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 0);
        TestAssert.Equal(Vector4.One, session.GetVector("SkinTone"));
        TestAssert.Equal("default-diffuse", session.GetPreviewTexture("HED_Diff")?.CacheKey);
        TestAssert.True(session.GetSelectedTexture("HED_Diff") is null,
            "Reset did not select the texture control's material-default option.");
        TestAssert.True(session.CreateOverrides().Scalars.Count == 0 && session.CreateOverrides().Vectors.Count == 0 &&
                        session.CreateOverrides().Textures.Count == 0,
            "Loaded material overrides survived ResetToDefaults.");
        session.Undo();
        TestAssert.Near(0.9f, session.GetScalar("HED_Norm_Blend"), 0);
        TestAssert.Equal(new Vector4(0.2f, 0.3f, 0.4f, 1), session.GetVector("SkinTone"));
        TestAssert.Equal("override-diffuse", session.GetPreviewTexture("HED_Diff")?.CacheKey);
        TestAssert.True(session.CreateOverrides().Scalars.Count == 1 && session.CreateOverrides().Vectors.Count == 1 &&
                        session.CreateOverrides().Textures.Count == 1,
            "Undo did not restore the loaded material overrides.");
    }

    private static void MaterialPasteRestoresOmittedDefaults()
    {
        var session = CreateSession();
        session.SetScalar("HED_Norm_Blend", 0.9f);
        session.SetVector("SkinTone", new Vector4(0.2f, 0.3f, 0.4f, 1));

        session.ApplyMaterialData(
            new MorphFaceMaterialData([], [], []),
            new Dictionary<string, DecodedTextureAsset?>());

        TestAssert.Near(0.5f, session.GetScalar("HED_Norm_Blend"), 0);
        TestAssert.Equal(Vector4.One, session.GetVector("SkinTone"));
        TestAssert.True(session.CreateOverrides().Scalars.Count == 0 &&
                        session.CreateOverrides().Vectors.Count == 0,
            "An empty material paste retained values that were omitted from its override payload.");
    }

    private static void AttachmentTextureIsNotHeadOutput()
    {
        var overrides = new MorphFaceMaterialOverrides(null, [], [],
            [new TextureMaterialOverride("Diffuseuse", null),
             new TextureMaterialOverride("HED_Diff", TestFixtures.CreateIdentity("HED_Diff", "Texture2D"))]);
        var session = new MaterialEditingSession(overrides, ResolvedHeadMaterialSet.Empty);

        TestAssert.True(session.CaptureInterchangeData().Textures.All(value =>
                !value.Name.Equals("Diffuseuse", StringComparison.OrdinalIgnoreCase)),
            "Attachment Diffuseuse leaked into a material RON payload.");
        TestAssert.True(session.CreateOverrides().Textures.All(value =>
                !value.Name.Equals("Diffuseuse", StringComparison.OrdinalIgnoreCase)),
            "Attachment Diffuseuse leaked into PCC head overrides.");
        TestAssert.True(session.CreateOverrides().Textures.Any(value => value.Name == "HED_Diff"),
            "The head texture was removed with the attachment texture.");
    }

    private static void SavedHatDiffuseIsPreviewOnly()
    {
        var face = CreateSession().Materials.Materials.Values.Single();
        var hatIdentity = TestFixtures.CreateIdentity("HMM_HAT_Test_MAT", "MaterialInstanceConstant");
        var hatTextureIdentity = TestFixtures.CreateIdentity("HatDiffuse", "Texture2D");
        var hatTexture = new DecodedTextureAsset(hatTextureIdentity, 1, 1,
            [255, 255, 255, 255], "PF_DXT1", TextureRole.Diffuse,
            TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "hat-preview");
        var hat = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(hatIdentity), hatIdentity, "HMM_HAT_Test_MAT",
            HeadMaterialFamily.Accessory, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(), new Dictionary<string, Vector4>(),
            new Dictionary<string, MaterialTextureBinding>
            {
                ["Diffuse"] = new("Diffuse", hatTexture)
            })
        {
            IsPreviewOnlyAttachment = true
        };
        var materials = new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
        {
            [face.Key] = face,
            [hat.Key] = hat
        });
        var overrides = new MorphFaceMaterialOverrides(null, [], [],
            [new TextureMaterialOverride("Diffuse", hatTextureIdentity)]);

        // Constructing a new session reproduces closing and reopening a saved Hat face.
        var reloaded = new MaterialEditingSession(overrides, materials, [face.Key]);
        TestAssert.True(reloaded.TextureParameters.All(value => value.Name != "Diffuse"),
            "The synthetic Hat diffuse returned as an editable head texture after reload.");
        TestAssert.True(reloaded.CaptureInterchangeData().Textures.All(value => value.Name != "Diffuse"),
            "The synthetic Hat diffuse leaked into material export after reload.");
        TestAssert.True(reloaded.CreateOverrides().Textures.All(value => value.Name != "Diffuse"),
            "The synthetic Hat diffuse leaked into PCC head overrides after reload.");
        TestAssert.True(reloaded.Materials.Materials[hat.Key].Textures.ContainsKey("Diffuse"),
            "Removing the head control also removed the Hat preview texture.");
        TestAssert.True(reloaded.TextureParameters.Any(value => value.Name == "HED_Diff"),
            "The Hat filter removed the actual head diffuse control.");
    }

    private static void DuplicateTextureOverridesRemainLoadable()
    {
        var first = TestFixtures.CreateIdentity("FirstMask", "Texture2D");
        var second = TestFixtures.CreateIdentity("SecondMask", "Texture2D");
        var session = new MaterialEditingSession(
            new MorphFaceMaterialOverrides(null, [], [],
                [new TextureMaterialOverride("HED_Mask", first),
                 new TextureMaterialOverride("HED_Mask", second)]),
            ResolvedHeadMaterialSet.Empty);

        var overrides = session.CreateOverrides().Textures;
        TestAssert.Equal(1, overrides.Count);
        TestAssert.Equal(first, overrides[0].TextureReference);
    }

    private static void InheritedEyeEmissiveDefaultsToZero()
    {
        foreach (var game in new[] { MEGame.LE2, MEGame.LE3 })
        {
            AssertInheritedEyeEmissiveDefaultsToZero(game);
        }
    }

    private static void AssertInheritedEyeEmissiveDefaultsToZero(MEGame game)
    {
        var materialScalars = new Dictionary<string, float>
        {
            ["Emis_Scalar"] = 1.5f
        };
        var defaults = MorphFaceMaterialReader.GetEditorDefaultScalars(
            game, HeadMaterialFamily.Eyes, materialScalars);

        TestAssert.True(defaults["Emis_Scalar"] == 0,
            "LE2 inherited eye emissive strength was exposed as an active editor default.");
        TestAssert.True(materialScalars["Emis_Scalar"] == 1.5f,
            "The LE2 material scalar was rewritten instead of preserving its authored graph value.");
        TestAssert.True(MorphFaceMaterialReader.GetEditorDefaultScalars(
                MEGame.LE1, HeadMaterialFamily.Eyes, materialScalars)["Emis_Scalar"] == 1.5f,
            "The LE2/LE3 editor default policy changed LE1.");

        var identity = TestFixtures.CreateIdentity("Le2Eye", "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "HMM_EYE_MASTER_OVRD_MAT",
            HeadMaterialFamily.Eyes, HeadMaterialBlendMode.Opaque, false,
            materialScalars,
            new Dictionary<string, Vector4> { ["Emis_Color"] = Vector4.UnitX },
            new Dictionary<string, MaterialTextureBinding>())
        {
            DefaultScalars = defaults
        };
        var session = new MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [material.Key] = material
            }));

        TestAssert.True(session.GetScalar("Emis_Scalar") == 0,
            "The editor session did not consume the safe LE2 eye default.");
        TestAssert.True(session.Materials.Materials.Single().Value.Scalars["Emis_Scalar"] == 0,
            "The preview still received inherited LE2 eye emissive strength.");
        TestAssert.True(session.CreateOverrides().Scalars.Count == 0,
            "An inherited LE2 emissive default became an authored face override.");

        session.SetScalar("Emis_Scalar", 1.5f);
        TestAssert.True(session.Materials.Materials.Single().Value.Scalars["Emis_Scalar"] == 1.5f,
            "An explicitly authored LE2 eye emissive value was not applied.");
        TestAssert.True(
            session.CreateOverrides().Scalars.Single(value => value.Name == "Emis_Scalar").Value == 1.5f,
            "An explicitly authored LE2 eye emissive value was not retained for save.");
    }

    private static void PackageTextureReferenceUpdatesBindings()
    {
        var session = CreateSession();
        var identity = TestFixtures.CreateIdentity("ReplacementDiffuse", "Texture2D");
        var replacement = new DecodedTextureAsset(
            identity, 2, 2, Enumerable.Repeat((byte)200, 16).ToArray(), "PF_DXT1",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "package:test");
        session.SetTextureReference("HED_Diff", replacement);
        TestAssert.Equal("package:test", session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey);
        TestAssert.Equal(1, session.ChangedTextureCount);
        session.Undo();
        TestAssert.Equal("original", session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey);
        TestAssert.Equal(0, session.ChangedTextureCount);
    }

    private static void PackageTextureReferenceSupportsNone()
    {
        var session = CreateSession();
        session.SetTextureReference("HED_Diff", null);

        TestAssert.True(
            session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey == "original",
            "Selecting None did not restore the material's default texture in the preview.");
        TestAssert.True(
            session.CreateOverrides().Textures.All(value => value.Name != "HED_Diff"),
            "Selecting None retained a package override instead of using the material default.");
        TestAssert.Equal(1, session.ChangedTextureCount);

        session.Undo();
        TestAssert.Equal("original", session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey);
        TestAssert.Equal(0, session.ChangedTextureCount);
        session.Redo();
        TestAssert.True(
            session.Materials.Materials.Values.Single().Textures["HED_Diff"].Texture.CacheKey == "original",
            "Redo did not restore the material default for None.");
    }

    private static void TransientTexturePreviewStaysOutsideAuthoredState()
    {
        var session = CreateSession();
        var replacementIdentity = TestFixtures.CreateIdentity("PreviewDiffuse", "Texture2D");
        var replacement = new DecodedTextureAsset(
            replacementIdentity, 1, 1, [24, 192, 72, 255], "PF_B8G8R8A8",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "preview");
        var commits = 0;
        var historyChanges = 0;
        session.EditCommitted += (_, _) => commits++;
        session.HistoryChanged += (_, _) => historyChanges++;

        session.PreviewTexture("HED_Diff", replacement);
        TestAssert.Equal("preview", session.GetPreviewTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal("original", session.GetSelectedTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal("FaceDiffuse", session.GetTextureReference("HED_Diff")?.InstancedPath);
        TestAssert.Equal("FaceDiffuse", session.CreateOverrides().Textures.Single().TextureReference?.InstancedPath);
        TestAssert.True(!session.CanUndo && commits == 0 && historyChanges == 0,
            "Transient texture preview changed authored history or dirty state.");

        session.PreviewTexture("HED_Diff", null);
        TestAssert.Equal("original", session.GetPreviewTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal("original", session.GetSelectedTexture("HED_Diff")?.CacheKey);

        session.PreviewTexture("HED_Diff", replacement);
        session.SetTextureReference("HED_Diff", replacement);
        TestAssert.Equal("preview", session.GetSelectedTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal(1, commits);
        TestAssert.Equal(1, historyChanges);
        TestAssert.True(session.CanUndo, "Committing a preview candidate did not create one normal edit.");

        session.PreviewTexture("HED_Diff", null);
        session.Undo();
        TestAssert.Equal("original", session.GetSelectedTexture("HED_Diff")?.CacheKey);
        TestAssert.Equal("original", session.GetPreviewTexture("HED_Diff")?.CacheKey);
        TestAssert.True(!session.CanUndo, "Undo did not return to the previous committed texture.");
    }

    private static void NoneRestoresRenderedDefault()
    {
        var session = CreateSession();
        var baseline = ToPreview(session.Materials.Materials.Values.Single());
        var identity = TestFixtures.CreateIdentity("ReplacementDiffuse", "Texture2D");
        var replacement = new DecodedTextureAsset(
            identity, 1, 1, [16, 220, 24, 255], "PF_B8G8R8A8",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "replacement");
        var (renderer, camera) = CreateTriangleRenderer(baseline);
        using (renderer)
        {
            var originalFrame = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            session.SetTextureReference("HED_Diff", replacement);
            var changed = ToPreview(session.Materials.Materials.Values.Single());
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [changed.Key] = changed });
            var changedFrame = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels.ToArray();
            TestAssert.True(!originalFrame.SequenceEqual(changedFrame), "The replacement texture did not render.");

            session.SetTextureReference("HED_Diff", null);
            var restored = ToPreview(session.Materials.Materials.Values.Single());
            renderer.UpdateMaterials(new Dictionary<string, HeadPreviewMaterial> { [restored.Key] = restored });
            var restoredFrame = renderer.Render(camera, new HeadPreviewOptions()).BgraPixels;
            TestAssert.True(originalFrame.SequenceEqual(restoredFrame),
                "Selecting None left the previously selected texture in the renderer.");
        }

        static HeadPreviewMaterial ToPreview(ResolvedHeadMaterial material) => new(
            material.Key,
            material.Source.InstancedPath,
            HeadMaterialFamily.Unknown,
            HeadMaterialBlendMode.Opaque,
            false,
            material.Scalars,
            material.Vectors,
            material.Textures.ToDictionary(
                value => value.Key,
                value => new HeadPreviewTexture(
                    value.Value.Texture.CacheKey,
                    value.Key,
                    value.Value.Texture.Width,
                    value.Value.Texture.Height,
                    value.Value.Texture.Rgba8,
                    value.Value.Texture.Role,
                    value.Value.Texture.ColorSpace,
                    value.Value.Texture.AlphaPolicy,
                    value.Value.Texture.HasMeaningfulAlpha),
                StringComparer.OrdinalIgnoreCase));
    }

    private static void AttachmentPreparationDoesNotPublish()
    {
        var session = CreateSession();
        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(HairMaterial("HairMaterialA", 1f, 2f, Vector4.One)));
        session.SetScalar("Hightlight1Intensity", 4f);
        var texture = session.GetSelectedTexture("HED_Diff")!;
        session.PreviewTexture("HED_Diff", texture with { CacheKey = "hover-only" });
        var original = session.Materials;
        var changes = 0;
        var historyChanges = 0;
        session.MaterialsChanged += (_, _) => changes++;
        session.HistoryChanged += (_, _) => historyChanges++;

        var empty = session.PrepareAttachmentMaterials(ResolvedHeadMaterialSet.Empty);
        TestAssert.True(!empty.Materials.Materials.Values.Any(material => material.Family == HeadMaterialFamily.Hair),
            "The candidate retained a removed attachment.");
        TestAssert.True(ReferenceEquals(original, session.Materials) &&
                        session.ScalarNames.Contains("Hightlight1Intensity"),
            "Preparing a candidate changed the live preview or controls.");
        TestAssert.Equal(0, changes);
        TestAssert.Equal(0, historyChanges);

        var replacement = session.PrepareAttachmentMaterials(HairMaterial("HairMaterialB", 3f, 8f, Vector4.Zero));
        var hair = replacement.Materials.Materials.Values.Single(material => material.Family == HeadMaterialFamily.Hair);
        TestAssert.Equal(4f, hair.Scalars["Hightlight1Intensity"]);
        TestAssert.Equal(8f, hair.Scalars["Hightlight2Intensity"]);
        session.ApplyMaterialSurface(replacement);
        TestAssert.True(ReferenceEquals(replacement.Materials, session.Materials), "Publication rebuilt the prepared materials.");
        TestAssert.Equal(texture.CacheKey, session.GetPreviewTexture("HED_Diff")?.CacheKey);
        session.ClearTexturePreview("HED_Diff");
        TestAssert.Equal(1, changes);
        TestAssert.Equal(1, historyChanges);
        session.Undo();
        TestAssert.Equal(1f, session.GetScalar("Hightlight1Intensity"));
        TestAssert.True(!session.CanUndo, "Surface publication added a material edit to history.");
    }

    private static void AttachmentNumericControlsRebase()
    {
        var baseSession = CreateSession();
        var session = new MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(baseSession.Materials.Materials.ToDictionary(
                value => value.Key, value => value.Value)));
        TestAssert.True(!session.ScalarNames.Contains("Hightlight1Intensity", StringComparer.OrdinalIgnoreCase),
            "Hair highlight scalar appeared before a supporting attachment was selected.");

        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(HairMaterial(
            "HairMaterialA", 1f, 2f, new Vector4(0.2f, 0.1f, 0.05f, 1))));
        TestAssert.True(session.ScalarNames.Contains("Hightlight1Intensity", StringComparer.OrdinalIgnoreCase),
            "Selecting a supporting NPC hair attachment did not add its authored highlight scalar.");
        TestAssert.True(session.ScalarNames.Contains("Highlight1SpecExp_Scalar", StringComparer.OrdinalIgnoreCase),
            "Selecting a supporting NPC hair attachment did not add its specular exponent scalar.");
        TestAssert.True(session.VectorNames.Contains("Highlight1Color", StringComparer.OrdinalIgnoreCase),
            "Selecting a supporting NPC hair attachment did not add its authored highlight colour.");
        TestAssert.Equal(1f, session.GetScalar("Hightlight1Intensity"));
        TestAssert.Equal(new Vector4(0.2f, 0.1f, 0.05f, 1), session.GetVector("Highlight1Color"));
        TestAssert.True(!session.CanUndo,
            "Changing the active attachment created an undo entry for authored material values.");

        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(HairMaterial(
            "HairMaterialB", 3f, 8f, new Vector4(0.4f, 0.3f, 0.2f, 1))));
        TestAssert.Equal(3f, session.GetScalar("Hightlight1Intensity"));
        TestAssert.Equal(8f, session.GetScalar("Hightlight2Intensity"));
        TestAssert.Equal(new Vector4(0.4f, 0.3f, 0.2f, 1), session.GetVector("Highlight1Color"));

        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial>(StringComparer.OrdinalIgnoreCase))));
        TestAssert.True(!session.ScalarNames.Contains("Hightlight1Intensity", StringComparer.OrdinalIgnoreCase) &&
                        !session.VectorNames.Contains("Highlight1Color", StringComparer.OrdinalIgnoreCase),
            "Clearing the hair attachment left unsupported highlight controls visible.");
    }

    private static void EmptyCompiledHairSupportHidesHighlights()
    {
        var identity = new AssetIdentity("C:\\game\\BioGame\\CookedPCConsole\\BioP_Char.pcc",
            "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HED_PRO_MASTER_HAIR_MAT", -1, "Material");
        var source = new ResolvedHeadMaterial(MaterialIdentityKey.Create(identity), identity,
            "HMN_HED_PRO_MASTER_HAIR_MAT", HeadMaterialFamily.Hair, HeadMaterialBlendMode.Translucent, true,
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["Highlight1SpecExp_Scalar"] = 80,
                ["Hightlight1Intensity"] = 1.5f
            },
            new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                ["Highlight1Color"] = new(0.3f, 0.2f, 0.1f, 1)
            },
            new Dictionary<string, MaterialTextureBinding>(StringComparer.OrdinalIgnoreCase))
        {
            SupportedScalars = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SupportedVectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            HasCompiledParameterSupport = true
        };
        var authored = new MorphFaceMaterialOverrides(null,
            [new ScalarMaterialOverride("Highlight1SpecExp_Scalar", 80),
             new ScalarMaterialOverride("Hightlight1Intensity", 1.5f)],
            [new VectorMaterialOverride("Highlight1Color", new(0.3f, 0.2f, 0.1f, 1))],
            []);
        var session = new MaterialEditingSession(authored, new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial>(StringComparer.OrdinalIgnoreCase) { [source.Key] = source }));

        TestAssert.True(!session.ScalarNames.Contains("Highlight1SpecExp_Scalar", StringComparer.OrdinalIgnoreCase) &&
                        !session.ScalarNames.Contains("Hightlight1Intensity", StringComparer.OrdinalIgnoreCase),
            "Scalars absent from the compiled Hair graph appeared as controls.");
        TestAssert.True(!session.VectorNames.Contains("Highlight1Color", StringComparer.OrdinalIgnoreCase),
            "Vectors absent from the compiled Hair graph appeared as controls.");
        var roundTrip = session.CreateOverrides();
        TestAssert.Equal(80f, roundTrip.Scalars.Single(value => value.Name == "Highlight1SpecExp_Scalar").Value);
        TestAssert.Equal(1.5f, roundTrip.Scalars.Single(value => value.Name == "Hightlight1Intensity").Value);
        TestAssert.Equal(new Vector4(0.3f, 0.2f, 0.1f, 1),
            roundTrip.Vectors.Single(value => value.Name == "Highlight1Color").Value);
    }

    private static void AttachmentNumericEditsRemainUndoable()
    {
        var baseSession = CreateSession();
        var session = new MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(baseSession.Materials.Materials.ToDictionary(
                value => value.Key, value => value.Value)));
        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(HairMaterial(
            "HairMaterialA", 1f, 2f, new Vector4(0.2f, 0.1f, 0.05f, 1))));

        session.BeginScalarEdit("Hightlight1Intensity");
        session.SetScalar("Hightlight1Intensity", 1.75f);
        session.EndScalarEdit("Hightlight1Intensity");
        TestAssert.True(session.CanUndo, "Editing an attachment highlight did not create semantic history.");

        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(HairMaterial(
            "HairMaterialB", 3f, 8f, new Vector4(0.4f, 0.3f, 0.2f, 1))));
        TestAssert.Equal(1.75f, session.GetScalar("Hightlight1Intensity"));
        TestAssert.Equal(8f, session.GetScalar("Hightlight2Intensity"));
        TestAssert.True(session.CanUndo,
            "Replacing the attachment discarded the existing numeric undo entry.");

        session.Undo();
        TestAssert.Equal(1f, session.GetScalar("Hightlight1Intensity"));
        TestAssert.Equal(8f, session.GetScalar("Hightlight2Intensity"));
    }

    private static ResolvedHeadMaterialSet HairMaterial(
        string name,
        float highlight1Intensity,
        float highlight2Intensity,
        Vector4 highlight1Color)
    {
        var identity = TestFixtures.CreateIdentity(name, "MaterialInstanceConstant");
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hightlight1Intensity"] = highlight1Intensity,
            ["Hightlight2Intensity"] = highlight2Intensity,
            ["Highlight1SpecExp_Scalar"] = 55,
            ["Highlight2SpecExp_Scalar"] = 85
        };
        var vectors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Highlight1Color"] = highlight1Color,
            ["Highlight2Color"] = Vector4.One
        };
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, name,
            HeadMaterialFamily.Hair, HeadMaterialBlendMode.Translucent, true,
            scalars, vectors, new Dictionary<string, MaterialTextureBinding>())
        {
            DefaultScalars = scalars,
            DefaultVectors = vectors,
            SupportedScalars = scalars.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            SupportedVectors = vectors.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        return new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
        {
            [material.Key] = material
        });
    }

    private static void MultiSlotAttachmentKeepsOwnDiffuse()
    {
        var firstTexture = new DecodedTextureAsset(TestFixtures.CreateIdentity("HairA", "Texture2D"),
            1, 1, [255, 0, 0, 255], "PF_B8G8R8A8", TextureRole.Diffuse,
            TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore, false, "hair-a");
        var secondTexture = firstTexture with
        {
            Source = TestFixtures.CreateIdentity("HairB", "Texture2D"),
            CacheKey = "hair-b"
        };
        ResolvedHeadMaterial Hair(string name, DecodedTextureAsset texture)
        {
            var identity = TestFixtures.CreateIdentity(name, "MaterialInstanceConstant");
            var binding = new MaterialTextureBinding("HAIR_Diff", texture);
            var textures = new Dictionary<string, MaterialTextureBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["HAIR_Diff"] = binding
            };
            return new ResolvedHeadMaterial(MaterialIdentityKey.Create(identity), identity, name,
                HeadMaterialFamily.Hair, HeadMaterialBlendMode.Masked, false,
                new Dictionary<string, float>(), new Dictionary<string, Vector4>(), textures)
            { DefaultTextures = textures };
        }
        var first = Hair("HairMaterialA", firstTexture);
        var second = Hair("HairMaterialB", secondTexture);
        var materials = new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
        {
            [first.Key] = first,
            [second.Key] = second
        });
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, ResolvedHeadMaterialSet.Empty);
        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(new ResolvedHeadMaterialSet(
            new Dictionary<string, ResolvedHeadMaterial> { [first.Key] = first })));
        session.ApplyMaterialSurface(session.PrepareAttachmentMaterials(materials));

        TestAssert.Equal(0, session.ChangedTextureCount);
        TestAssert.Equal(0, session.CreateOverrides().Textures.Count);
        TestAssert.Equal(firstTexture.Source,
            session.Materials.Materials[first.Key].Textures["HAIR_Diff"].Texture.Source);
        TestAssert.Equal(secondTexture.Source,
            session.Materials.Materials[second.Key].Textures["HAIR_Diff"].Texture.Source);

        ResolvedHeadMaterialSet Scoped(ResolvedHeadMaterial material) => new(
            new Dictionary<string, ResolvedHeadMaterial> { [material.Key] = material with { ParameterScopeKey = "human" } });
        var custom = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, Scoped(first));
        var name = MaterialParameterControlKey.Create("human", "HAIR_Diff");
        custom.SetTextureReference(name, secondTexture);
        custom.RebaseMaterialSurface(ResolvedHeadMaterialSet.Empty);
        custom.RebaseMaterialSurface(Scoped(first));
        TestAssert.Equal(secondTexture.Source, custom.Materials.Materials[first.Key].Textures["HAIR_Diff"].Texture.Source);
        TestAssert.Equal(secondTexture.Source, custom.CreateOverrides().Textures.Single().TextureReference);
        custom.Undo();
        TestAssert.Equal(firstTexture.Source, custom.Materials.Materials[first.Key].Textures["HAIR_Diff"].Texture.Source);

    }
}
