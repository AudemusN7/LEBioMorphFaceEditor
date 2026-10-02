using MorphFaceEditor.Infrastructure;
using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>Extends the human taxonomy with female-only morph, makeup and hair semantics.</summary>
public class HumanFemaleFeatureMetadataCatalog : HumanMaleFeatureMetadataCatalog
{
    protected override string TextArchetype => MorphFaceMetadataCatalogRegistry.HumanFemale;

    private const string Character = "character";
    private const string Brows = "brows";
    private const string Sockets = "sockets";
    private const string EyeShape = "eye-shape";
    private const string CharacterEyeShape = "character-eye-shape";
    private const string RaceEyeShape = "race-eye-shape";
    private const string ShapeEyeShape = "shape-eye-shape";
    private const string Eyelids = "eyelids";
    private const string Surface = "surface";
    private const string CharacterMouth = "character-mouth";
    private const string RaceMouth = "race-mouth";
    private const string ShapeMouth = "shape-mouth";
    private const string Makeup = "makeup";
    private const string MaterialHair = "material-hair";

    // Internal, zero-effect and metadata controls remain preserved but are intentionally absent from the UI.
    private static readonly IReadOnlySet<string> HiddenFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "averageHead",
        "BONE_blinkFix",
        "Eastwood",
        "rollins",
        "HAIR_splitSide",
        "HAIR_splitSide\"",
        "None",
        "mouth_cheekMass",
        "cheeks_gaunt",
        "eyeShape_droop",
        "eyes_Shape_droop",
        "eyes_Shape_sleepy",
        "eyes_Shape_wide",
        "pupil_Small",
        "pupil_Large",
        "eyes_bagOut",
        "eyes_lashAngle",
        "eyes_lashLength",
        "teeth_close",
        "teeth_Seperate",
        "teeth_canineExtend",
        "teeth_Narrow",
        "neck_apple"
    };

    public override IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
        CreateFemaleCategories();

    public override MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var metadata = base.Describe(feature, sessionCanEdit);
        var name = feature.Feature.Name;
        var (category, subcategory) = GetFemalePlacement(name, metadata.CategoryKey, metadata.SubcategoryKey);
        var described = metadata with
        {
            Label = HumaniseFemaleFeature(name, metadata.Label),
            CategoryKey = category,
            SubcategoryKey = subcategory,
            IsVisible = !HiddenFeatures.Contains(name),
            SortOrder = GetFemaleSortOrder(name, metadata.SortOrder),
            Description = feature.Kind == MorphFeatureResolutionKind.MetadataOnly
                ? Loc.Text("loc_Services_Taxonomy_stored_human_female_creator_metadata_with_no_dir_27f9bda2")
                : metadata.Description
        };
        return MetadataTextCatalog.Apply(TextArchetype, described, TextGame);
    }

    public override MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition)
    {
        var described = base.DescribeMaterial(definition) with
        {
            Group = GetMaterialCategory(definition.Name, definition.Kind)
        };
        return MetadataTextCatalog.Apply(TextArchetype, described, TextGame);
    }

    public override string GetMaterialCategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        if (IsMakeupParameter(name) || IsHairHighlightParameter(name))
        {
            return Additions;
        }
        if (name.Contains("spec_lips") || name.Contains("spwr_lips"))
        {
            return Mouth;
        }
        return base.GetMaterialCategory(parameterName, kind);
    }

    public override string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        if (IsMakeupParameter(name)) return Makeup;
        if (IsHairHighlightColourVector(name, kind)) return "scalp";
        if (IsHairHighlightParameter(name)) return MaterialHair;
        if (name.Contains("spec_lips") || name.Contains("spwr_lips")) return "lips";
        if (name == "hed_lash_opac_scalar") return Surface;
        return base.GetMaterialSubcategory(parameterName, kind);
    }

    public override int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind) =>
        parameterName.ToLowerInvariant() switch
        {
            "hed_eyeshadow_tint_scalar" or "hed_eyeshadow_tint_vector" => 0,
            "hed_brow_tint_scalar" or "hed_brow_tint_vector" => 10,
            "hed_lips_tint_scalar" or "hed_lips_tint_vector" => 20,
            "hed_addn_spec_lips_scalar" => 10,
            "hed_addn_spwr_lips_scalar" => 11,
            "hed_blush_scalar" or "hed_blush_vector" => 30,
            "hed_makeup_mask" => 40,
            "highlight1specexp_scalar" => 0,
            "highlight1intensity" or "hightlight1intensity" => 1,
            "highlight1color" => 2,
            "highlight2specexp_scalar" => 10,
            "highlight2intensity" or "hightlight2intensity" => 11,
            "highlight2color" => 12,
            _ => base.GetMaterialSortOrder(parameterName, kind)
        };

    private static int GetFemaleSortOrder(string name, int fallback)
    {
        if (name.StartsWith("HAIR_", StringComparison.OrdinalIgnoreCase))
        {
            return name.ToLowerInvariant() switch
            {
                "hair_centerpart" => 0,
                "hair_sidepart" => 1,
                "hair_splitside" => 2,
                "hair_pulledbackbig" => 3,
                "hair_pulledbackslick" => 4,
                "hair_slickwidowspeak" => 5,
                _ => 100
            };
        }
        return name.ToLowerInvariant() switch
        {
            "ashley" => 0,
            "iconic" => 1,
            "jack" => 3,
            "kasumi" => 4,
            "miranda" => 5,
            "race_ashley" => 0,
            "race_iconic" => 1,
            "race_liara" => 2,
            "mouthshape_ashley" => 0,
            "mouthshape_iconic" => 1,
            "mouthshape_liara" => 2,
            "mouthshape_oldasn" => 10,
            "mouthshape_yngasn" => 11,
            "mouthshape_oldblk" => 20,
            "mouthshape_yngblk" => 21,
            "mouthshape_oldcauc" => 30,
            "mouthshape_yngcauc" => 31,
            "eyeshape_flattop" => 60,
            "eyeshape_highinside" => 61,
            "eyeshape_slantup" => 62,
            "eyeshape_sleepy" => 63,
            "eyeshape_wide" => 64,
            "eyes_rotatein" => 12,
            "eyes_rotateout" => 13,
            _ => fallback
        };
    }

    private static (string Category, string Subcategory) GetFemalePlacement(
        string name,
        string fallbackCategory,
        string fallbackSubcategory)
    {
        var lower = name.ToLowerInvariant();
        if (lower is "ashley" or "iconic" or "jack" or "kasumi" or "miranda" or
            "race_ashley" or "race_iconic" or "race_liara")
        {
            return (FacialStructure, Character);
        }
        if (lower is "eyes_ashleyshape" ||
            lower is "eyeshape_ashley" or "eyeshape_iconic" or "eyeshape_liara")
        {
            return (Eyes, CharacterEyeShape);
        }
        if (lower.StartsWith("eyeshape_") && IsRaceShape(lower))
        {
            return (Eyes, RaceEyeShape);
        }
        if (lower.StartsWith("eyeshape_") ||
            fallbackCategory == Eyes && fallbackSubcategory == EyeShape)
        {
            return (Eyes, ShapeEyeShape);
        }
        if (lower is "mouthshape_ashley" or "mouthshape_iconic" or "mouthshape_liara")
        {
            return (Mouth, CharacterMouth);
        }
        if (lower.StartsWith("mouthshape_") && IsRaceShape(lower))
        {
            return (Mouth, RaceMouth);
        }
        if (fallbackCategory == Mouth && fallbackSubcategory == "mouth-shape")
        {
            return (Mouth, ShapeMouth);
        }
        return (fallbackCategory, fallbackSubcategory);
    }

    private static IReadOnlyList<EditorCategoryDefinition> CreateFemaleCategories() =>
        new HumanMaleFeatureMetadataCatalog().Categories.Select(category => category.Key switch
        {
            Eyes => category with
            {
                Description = Loc.Text("loc_Services_Taxonomy_brows_eye_position_proportions_character_shapes_8c6daf9a"),
                SliderGroups =
                [
                    new EditorSubcategoryDefinition(CharacterEyeShape, Loc.Text("loc_Services_Taxonomy_character_shapes_0fab481f")),
                    new EditorSubcategoryDefinition(RaceEyeShape, Loc.Text("loc_Services_Taxonomy_race_shapes_0087193f")),
                    new EditorSubcategoryDefinition(ShapeEyeShape, Loc.Text("loc_Services_Taxonomy_shape_rotation_7a73993a")),
                    new EditorSubcategoryDefinition(Brows, Loc.Text("loc_Services_Taxonomy_brows_9cbe502a")),
                    new EditorSubcategoryDefinition(Sockets, Loc.Text("loc_Services_Taxonomy_sockets_1d56780a")),
                    new EditorSubcategoryDefinition(Eyelids, Loc.Text("loc_Services_Taxonomy_eyelids_601775d2")),
                    new EditorSubcategoryDefinition("lashes", Loc.Text("loc_Services_Taxonomy_lashes_6ad1e621")),
                    new EditorSubcategoryDefinition(Surface, Loc.Text("loc_Services_Taxonomy_surface_026aa612"))
                ]
            },
            Mouth => category with
            {
                SliderGroups =
                [
                    new EditorSubcategoryDefinition(CharacterMouth, Loc.Text("loc_Services_Taxonomy_character_shapes_0fab481f")),
                    new EditorSubcategoryDefinition(RaceMouth, Loc.Text("loc_Services_Taxonomy_race_shapes_0087193f")),
                    new EditorSubcategoryDefinition(ShapeMouth, Loc.Text("loc_Services_Taxonomy_shape_cfb25fc2")),
                    .. category.SliderGroups.Where(group => group.Key != "mouth-shape")
                ]
            },
            Additions => category with
            {
                Description = Loc.Text("loc_Services_Taxonomy_makeup_hair_highlights_surface_additions_face_ov_50a0f9b7"),
                SliderGroups =
                [
                    new EditorSubcategoryDefinition(Makeup, Loc.Text("loc_Services_Taxonomy_makeup_0be431f7")),
                    new EditorSubcategoryDefinition(MaterialHair, Loc.Text("loc_Services_Taxonomy_hair_3ba4bcee")),
                    .. category.SliderGroups
                ]
            },
            _ => category
        }).ToArray();

    private static bool IsMakeupParameter(string name) =>
        name.Contains("brow_tint") || name.Contains("eyeshadow") ||
        name.Contains("lips_tint") || name.Contains("makeup") || name.Contains("blush");

    private static bool IsHairHighlightParameter(string name) =>
        name.Contains("highlight") || name.Contains("hightlight");

    private static bool IsHairHighlightColourVector(string name, MaterialParameterKind kind) =>
        kind == MaterialParameterKind.Vector && name is
            "highlight1color" or "highlight2color" or
            "highlight1colour_vector" or "highlight2colour_vector";

    private static bool IsRaceShape(string name) =>
        name.Contains("asn") || name.Contains("blk") || name.Contains("cauc");

    private static string HumaniseFemaleFeature(string name, string fallback)
    {
        if (!name.StartsWith("HAIR_", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }
        var text = name[5..].Replace('_', ' ');
        text = Regex.Replace(text, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    }
}
