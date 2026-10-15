using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Infrastructure;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the material schema regression set.
public static class MaterialSchemaTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE1 human material schema contains male and female masters", SchemaContainsRecoveredFamilies),
        new("human hair shader intensity misspellings are described by the material schema", HairHighlightIntensityAliasesAreSchemaParameters),
        new("human material controls hide runtime alignment textures and place teeth in Mouth", HumanMaterialMetadataTaxonomy),
        new("human face and scalp textures use the requested head ordering", HumanFaceAndScalpTextureOrdering),
        new("LE3 recovered hair preview variants require exact terminal master identities", RecoveredPreviewModelsRequireExactMasters),
        new("Human Hair native controls are defined while CC controls reuse existing metadata", RecoveredHairControlMetadata),
        new("Human Hair metadata labels and descriptions resolve through LOC resources", RecoveredHairMetadataIsLocalised),
        new("human hair controls lead Additions and distinguish mesh-only from shared colours", HairControlPlacement)
    ];

    private static void RecoveredPreviewModelsRequireExactMasters()
    {
        var cases = new[]
        {
            ("BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HED_PRO_MASTER_HAIR_MAT_CC", HumanMaterialPreviewModel.CcHair),
            ("BIOG_Humanoid_MASTER_MTR_R.Human.HMM_HED_PRO_MASTER_SCALP_MAT_CC", HumanMaterialPreviewModel.CcScalp),
            ("BIOG_HMM_HED_PROMorph.Kaiden.Master.BIOG_HMM_HED_Kaiden_MASTER", HumanMaterialPreviewModel.Kaiden),
            ("BIOG_Humanoid_MASTER_MTR_R.Human.Hair.Human_Hair_MASTER", HumanMaterialPreviewModel.HumanHair)
        };
        foreach (var (path, expected) in cases)
        {
            var masterName = path[(path.LastIndexOf('.') + 1)..];
            TestAssert.Equal(expected, HeadMaterialCatalog.Find(MorphFaceGame.LE3, masterName)!.PreviewModel);
            TestAssert.Equal(expected, HeadMaterialCatalog.Find(MorphFaceGame.LE3, masterName.ToLowerInvariant())!.PreviewModel);
            TestAssert.True(HeadMaterialCatalog.Find(MorphFaceGame.LE1, masterName) is null,
                "An LE3-only master was recognised in LE1.");
            TestAssert.True(HeadMaterialCatalog.Find(MorphFaceGame.LE3, masterName + "_Copy") is null,
                "An approximate master name selected a dedicated preview shader.");
        }
    }

    private static void RecoveredHairControlMetadata()
    {
        var profile = new HumanMaleFeatureMetadataCatalog();
        foreach (var (name, nativeValue) in new[] { ("Aniso_Highlight_1", 15f), ("Aniso_Highlight_2", 1500f) })
        {
            var definition = HumanMaterialProfiles.Describe(name, MaterialParameterKind.Scalar);
            TestAssert.Equal(HeadMaterialFamily.Hair, definition.Family);
            TestAssert.True(definition.Minimum > 0 && definition.Maximum >= nativeValue,
                $"{name} does not allow its native authored power.");
            TestAssert.Near(1f, definition.Step, 0);
            TestAssert.Equal("hair", profile.GetMaterialSubcategory(name, MaterialParameterKind.Scalar));
            TestAssert.Equal(Loc.Format("loc_Services_Taxonomy_MeshOnlyMaterialLabel",
                    Loc.Text("loc_Services_MetadataText_Shared_" + name.ToLowerInvariant() + "_Label")),
                profile.DescribeMaterial(definition).Label);
        }
        foreach (var name in new[] { "Hair_Color", "Aniso_Color_1", "Aniso_Color_2" })
            TestAssert.Equal(HeadMaterialFamily.Hair, HumanMaterialProfiles.Describe(name, MaterialParameterKind.Vector).Family);
        TestAssert.Equal(Loc.Format("loc_Services_Taxonomy_MeshOnlyMaterialLabel",
            Loc.Text("loc_Services_MetadataText_Shared_hair_color_Label")), profile.DescribeMaterial(
            HumanMaterialProfiles.Describe("Hair_Color", MaterialParameterKind.Vector)).Label);
        TestAssert.Equal("hair",
            profile.GetMaterialSubcategory("Aniso_Color_1", MaterialParameterKind.Vector));
        foreach (var name in new[] { "HED_Scalp_Buzzcut_Alpha_Scalar", "HAIR_Mask_Alpha_Scalar" })
            TestAssert.Equal(HeadMaterialFamily.Scalp, HumanMaterialProfiles.Describe(name, MaterialParameterKind.Scalar).Family);
        TestAssert.Equal(HeadMaterialFamily.Scalp,
            HumanMaterialProfiles.Describe("HED_Hair_Colour_Vector", MaterialParameterKind.Vector).Family);
        TestAssert.True(HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture("Hair_Stack"),
            "The native Human Hair stack must remain MIC-owned.");
        TestAssert.Equal(TextureColorSpace.Linear,
            HumanMaterialProfiles.Describe("Hair_Stack", MaterialParameterKind.Texture).ColorSpace);
        TestAssert.True(!HumanMaterialProfiles.Definitions.Any(value =>
                value.Name is "HED_Spec_Scalar" or "HED_Spwr" or "Nm" or "Spec"),
            "Preview-only Kaidan parameters acquired editing metadata.");
    }

    private static void RecoveredHairMetadataIsLocalised()
    {
        var profile = new HumanMaleFeatureMetadataCatalog();
        var english = Loc.ReadEmbeddedEnglish();
        foreach (var (name, kind) in new[]
        {
            ("Aniso_Highlight_1", MaterialParameterKind.Scalar),
            ("Aniso_Highlight_2", MaterialParameterKind.Scalar),
            ("Aniso_Color_1", MaterialParameterKind.Vector),
            ("Aniso_Color_2", MaterialParameterKind.Vector),
            ("Hair_Color", MaterialParameterKind.Vector),
            ("Hair_Stack", MaterialParameterKind.Texture)
        })
        {
            var prefix = "loc_Services_MetadataText_Shared_" + name.ToLowerInvariant();
            TestAssert.True(english.ContainsKey(prefix + "_Label") && english.ContainsKey(prefix + "_Description"),
                $"{name} has no English localisation resources.");
            var metadata = profile.DescribeMaterial(HumanMaterialProfiles.Describe(name, kind));
            TestAssert.Equal(Loc.Format("loc_Services_Taxonomy_MeshOnlyMaterialLabel",
                Loc.Text(prefix + "_Label")), metadata.Label);
            TestAssert.Equal(Loc.Text(prefix + "_Description"), metadata.Description);
        }
    }

    private static void HairControlPlacement()
    {
        foreach (var profile in new IHeadEditorUiProfile[]
                 { new HumanMaleFeatureMetadataCatalog(), new HumanFemaleFeatureMetadataCatalog() })
        {
            var additions = profile.Categories.Single(category => category.Key == HumanMaleFeatureMetadataCatalog.Additions);
            TestAssert.Equal("hair", additions.SliderGroups[0].Key);
            foreach (var definition in HumanMaterialProfiles.Definitions.Where(definition =>
                         definition.Family == HeadMaterialFamily.Hair))
            {
                TestAssert.Equal(HumanMaleFeatureMetadataCatalog.Additions,
                    profile.GetMaterialCategory(definition.Name, definition.Kind));
                TestAssert.Equal("hair", profile.GetMaterialSubcategory(definition.Name, definition.Kind));
            }
            foreach (var (name, kind) in new[]
            {
                ("Aniso_Highlight_1", MaterialParameterKind.Scalar),
                ("Highlight2SpecExp_Scalar", MaterialParameterKind.Scalar),
                ("Hightlight1Intensity", MaterialParameterKind.Scalar),
                ("Hair_Color", MaterialParameterKind.Vector),
                ("Highlight2Color", MaterialParameterKind.Vector)
            })
                TestAssert.True(profile.DescribeMaterial(HumanMaterialProfiles.Describe(name, kind)).Label.EndsWith("(Mesh)"),
                    $"Mesh-only {name} lost its localized suffix.");
            var shared = profile.DescribeMaterial(HumanMaterialProfiles.Describe("HED_Hair_Colour_Vector", MaterialParameterKind.Vector));
            TestAssert.Equal("hair", profile.GetMaterialSubcategory(shared.Name, shared.Kind));
            TestAssert.True(!shared.Label.EndsWith("(Mesh)"), "The shared hair/scalp tint was labelled mesh-only.");
            foreach (var name in new[] { "HED_Scalp_Buzzcut_Alpha_Scalar", "HAIR_Mask_Alpha_Scalar", "HED_Spec_Aniso_Exp_Scalar" })
            {
                TestAssert.Equal("scalp", profile.GetMaterialSubcategory(name, MaterialParameterKind.Scalar));
                TestAssert.True(!profile.DescribeMaterial(HumanMaterialProfiles.Describe(name, MaterialParameterKind.Scalar))
                    .Label.EndsWith("(Mesh)"), $"Scalp control {name} was labelled mesh-only.");
            }
        }
    }

    private static void HairHighlightIntensityAliasesAreSchemaParameters()
    {
        foreach (var name in new[] { "Hightlight1Intensity", "Hightlight2Intensity" })
        {
            var definition = HumanMaterialProfiles.Describe(name, MaterialParameterKind.Scalar);
            TestAssert.Equal(HeadMaterialFamily.Hair, definition.Family);
            TestAssert.Near(0f, definition.Minimum, 1e-6f);
            TestAssert.Near(4f, definition.Maximum, 1e-6f);
            TestAssert.True(!definition.Description!.Contains("not in the recovered", StringComparison.OrdinalIgnoreCase),
                $"{name} fell through to the unknown-parameter fallback instead of its compiled hair shader schema.");
        }
    }

    private static void HumanFaceAndScalpTextureOrdering()
    {
        IHeadEditorUiProfile[] profiles =
        [
            new HumanMaleFeatureMetadataCatalog(),
            new HumanFemaleFeatureMetadataCatalog()
        ];
        var orderedTextures = new[]
        {
            "HED_Diff", "HED_Norm", "HED_Norm_02", "HED_Scalp_Diff", "HED_Scalp_Norm",
            "HED_Scalp_Spec", "HED_Tang", "HED_Scalp_SpecShift", "HED_Scalp_SpecShift2"
        };

        foreach (var profile in profiles)
        {
            for (var index = 0; index < orderedTextures.Length; index++)
            {
                TestAssert.Equal(index, profile.GetMaterialSortOrder(
                    orderedTextures[index], MaterialParameterKind.Texture));
            }
        }
    }

    private static void HumanMaterialMetadataTaxonomy()
    {
        var profile = new HumanMaleFeatureMetadataCatalog();
        foreach (var scarParameter in new[]
                 {
                     "HED_Scar_Colour_Vector", "Light_Scar_Color", "Light_Scar_Colour",
                     "LightScarColor"
                 })
        {
            TestAssert.Equal("Scar Emissive Color", profile.DescribeMaterial(HumanMaterialProfiles.Describe(
                scarParameter, MaterialParameterKind.Vector)).Label);
        }

        // These are the parameter names used by the LE2/LE3 HMM and HMF Player
        // face masters, rather than names inferred from the visible row labels.
        foreach (var alignmentTexture in new[] { "HED_Face_Alignment_Emis", "HED_Face_Alignment_Norm" })
        {
            TestAssert.True(!profile.IsMaterialVisible(alignmentTexture, MaterialParameterKind.Texture),
                $"{alignmentTexture} remained visible in the material UI.");
        }
        foreach (var morphTexture in new[] { "HED_Norm", "HED_Norm_02" })
        {
            TestAssert.True(profile.IsMaterialVisible(morphTexture, MaterialParameterKind.Texture),
                $"{morphTexture} was incorrectly hidden with the runtime Alignment controls.");
        }

        foreach (var scalpTexture in new[]
                 {
                     "HED_Scalp_Diff", "HED_Scalp_Spec", "HED_Scalp_Norm", "HED_Tang",
                     "HED_Scalp_SpecShift", "HED_Scalp_SpecShift2"
                 })
        {
            TestAssert.Equal("head", profile.GetMaterialCategory(
                scalpTexture, MaterialParameterKind.Texture));
        }
        TestAssert.Equal("mouth", profile.GetMaterialCategory("HED_Teeth_Diff", MaterialParameterKind.Texture));

        var profiles = MorphFaceProfileRegistry.CreateDefault().Profiles;
        foreach (var key in new[]
                 {
                     "le2-human-male", "le2-human-female", "le3-human-male", "le3-human-female"
                 })
        {
            AssertMaterialControlProjection(profiles.Single(value => value.Key == key).GetUiProfile(), key);
        }
        AssertMaterialControlProjection(new DetachedMeshFeatureMetadataCatalog(), "le3-mesh", scoped: true);
    }

    private static void AssertMaterialControlProjection(
        IHeadEditorUiProfile profile,
        string route,
        bool scoped = false)
    {
        var identity = TestFixtures.CreateIdentity($"{route}-face-material", "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(identity), identity, "HMM_HED_PROCustom_MASTER_FACE_MAT",
            HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4> { ["Light_Scar_Color"] = Vector4.One },
            new Dictionary<string, MaterialTextureBinding>())
        {
            SupportedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HED_Face_Alignment_Emis", "HED_Face_Alignment_Norm",
                "HED_Norm", "HED_Norm_02", "HED_Teeth_Diff", "HED_Scalp_Diff"
            },
            ParameterScopeKey = scoped ? "human" : null,
            ParameterScopeLabel = scoped ? "Human" : null
        };
        var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [material.Key] = material
            }));
        using var reader = new MorphFacePackageReader();
        using var editor = new MaterialEditorViewModel(
            session,
            new WpfHdrColorDialogService(),
            new PackageReferenceService(reader, TestFixtures.CreateMissingTextureCatalogService()),
            "fixture.pcc", [], _ => { }, profile);
        var textureNames = editor.Textures.Select(value => editor.SourceParameterName(value.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        TestAssert.True(!textureNames.Contains("HED_Face_Alignment_Emis") &&
                        !textureNames.Contains("HED_Face_Alignment_Norm"),
            $"{route} still projected runtime Alignment texture rows.");
        TestAssert.True(textureNames.Contains("HED_Norm") && textureNames.Contains("HED_Norm_02"),
            $"{route} hid the editable morph face-normal controls.");
        TestAssert.Equal("mouth", editor.Textures.Single(value =>
            editor.SourceParameterName(value.Name) == "HED_Teeth_Diff").CategoryKey);
        TestAssert.Equal("head", editor.Textures.Single(value =>
            editor.SourceParameterName(value.Name) == "HED_Scalp_Diff").CategoryKey);
        TestAssert.True(editor.Vectors.Any(value => value.Label.EndsWith(
                "Scar Emissive Color", StringComparison.Ordinal)),
            $"{route} lost the editable scar emissive control.");
    }

    private static void SchemaContainsRecoveredFamilies()
    {
        foreach (var family in new[]
                 {
                     HeadMaterialFamily.Skin,
                     HeadMaterialFamily.Scalp,
                     HeadMaterialFamily.Eyes,
                     HeadMaterialFamily.Lashes,
                     HeadMaterialFamily.Hair,
                     HeadMaterialFamily.AsariSkin,
                     HeadMaterialFamily.SalarianSkin,
                     HeadMaterialFamily.SalarianEyes,
                     HeadMaterialFamily.TurianSkin,
                     HeadMaterialFamily.TurianEyes,
                     HeadMaterialFamily.BatarianSkin,
                     HeadMaterialFamily.KroganSkin,
                     HeadMaterialFamily.KroganEyes
                 })
        {
            TestAssert.True(HumanMaterialProfiles.Definitions.Any(value => value.Family == family), $"No schema entries exist for {family}.");
        }
        TestAssert.Equal(HeadMaterialFamily.Eyes, (HeadMaterialCatalog.Find(MorphFaceGame.LE1, "HMM_EYE_MASTER_OVRD_MAT")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(HeadMaterialFamily.Eyes, (HeadMaterialCatalog.Find(MorphFaceGame.LE1, "HMF_EYE_MASTER_OVRD_MAT")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(HeadMaterialFamily.Skin, (HeadMaterialCatalog.Find(MorphFaceGame.LE1, "HMF_HED_PRO_MASTER_FACE_MAT")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(HeadMaterialFamily.Hair, (HeadMaterialCatalog.Find(MorphFaceGame.LE3, "HMN_HED_PRO_MASTER_ADDN_HAIR_MAT")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(HeadMaterialFamily.MaskedHair, (HeadMaterialCatalog.Find(MorphFaceGame.LE1, "HMM_HIR_PROShort01_MAT_1a")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(HeadMaterialFamily.SalarianSkin, (HeadMaterialCatalog.Find(MorphFaceGame.LE1, "SAL_HED_PRO_MASTER_MAT")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(HeadMaterialFamily.SalarianEyes, (HeadMaterialCatalog.Find(MorphFaceGame.LE1, "SAL_HED_EYE_MASTER_MAT")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(HeadMaterialFamily.BatarianSkin, (HeadMaterialCatalog.Find(MorphFaceGame.LE1, "BAT_HED_PRO_MASTER_MAT")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(HeadMaterialFamily.KroganSkin, (HeadMaterialCatalog.Find(MorphFaceGame.LE1, "KRO_HED_PRO_MASTER_MAT")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(HeadMaterialFamily.KroganEyes, (HeadMaterialCatalog.Find(MorphFaceGame.LE1, "KRO_HED_EYE_MASTER_MAT")?.Family ?? HeadMaterialFamily.Unknown));
        TestAssert.Equal(TextureRole.Other, HumanMaterialProfiles.Describe(
            "HED_Makeup_Mask", MaterialParameterKind.Texture).TextureRole);
        foreach (var customPlayerParameter in new[]
                 {
                     ("HED_Brow", MaterialParameterKind.Texture),
                     ("HED_Scar", MaterialParameterKind.Texture),
                     ("HED_Custom_Scar_Scalar", MaterialParameterKind.Scalar),
                     ("HED_Scar_Diffuse_Scalar", MaterialParameterKind.Scalar),
                     ("HED_Scar_Vector", MaterialParameterKind.Vector)
                 })
        {
            var definition = HumanMaterialProfiles.Describe(
                customPlayerParameter.Item1,
                customPlayerParameter.Item2);
            TestAssert.Equal(HeadMaterialFamily.Skin, definition.Family);
            TestAssert.True(!definition.Description!.Contains("not in the recovered", StringComparison.OrdinalIgnoreCase),
                $"{customPlayerParameter.Item1} still fell through to the unknown-parameter tooltip.");
        }
        var additionalHair = HumanMaterialProfiles.Describe("HAIR_ADDN_Diff", MaterialParameterKind.Texture);
        TestAssert.Equal(TextureRole.Diffuse, additionalHair.TextureRole);
        TestAssert.Equal(TextureColorSpace.Srgb, additionalHair.ColorSpace);
        TestAssert.Equal(HeadMaterialBlendMode.Translucent, HumanMaterialProfiles.BlendMode(HeadMaterialFamily.Hair));
        TestAssert.Equal(HeadMaterialBlendMode.Masked, HumanMaterialProfiles.BlendMode(HeadMaterialFamily.MaskedHair));
        TestAssert.True(!HumanMaterialProfiles.IsTwoSided(HeadMaterialFamily.MaskedHair),
            "PROShort01 was incorrectly classified as two-sided translucent-card hair.");
    }
}

