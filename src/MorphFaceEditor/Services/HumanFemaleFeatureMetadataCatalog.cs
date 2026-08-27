using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>Extends the human taxonomy with female-only morph, makeup and hair semantics.</summary>
public class HumanFemaleFeatureMetadataCatalog : HumanMaleFeatureMetadataCatalog
{
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

    private static readonly IReadOnlyDictionary<string, string> FeatureLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ashley"] = "Ashley — Head",
            ["Iconic"] = "Iconic Shepard — Head",
            ["eyes_ashleyShape"] = "Ashley Shape",
            ["eyeShape_Ashley"] = "Ashley",
            ["eyeShape_flatTop"] = "Eye Shape Flat Top",
            ["eyeShape_highInside"] = "Eye Shape High Inner Corner",
            ["eyeShape_iconic"] = "Iconic Shepard",
            ["eyeShape_liara"] = "Liara",
            ["eyeShape_oldBlk"] = "Black — Old",
            ["eyeShape_SlantUp"] = "Eye Shape Slant Up",
            ["eyeShape_sleepy"] = "Eye Shape Sleepy",
            ["eyeShape_wide"] = "Eye Shape Wide",
            ["eyeShape_yngAsn"] = "Asian — Young",
            ["HAIR_centerPart"] = "Centre Part",
            ["HAIR_pulledBackBig"] = "Pulled Back — Full",
            ["HAIR_pulledBackSlick"] = "Pulled Back — Slick",
            ["HAIR_sidePart"] = "Side Part",
            ["HAIR_slickWidowsPeak"] = "Slick Widow's Peak",
            ["eyes_lashAngle"] = "Lash Angle",
            ["eyes_lashLength"] = "Lash Length",
            ["eyes_RotateIn"] = "Eyeballs Narrow",
            ["eyes_RotateOut"] = "Eyeballs Wide",
            ["mouth_cheekMass"] = "Cheeks Mass",
            ["pupil_Small"] = "Pupil Size",
            ["race_Ashley"] = "Ashley — Blend",
            ["race_iconic"] = "Iconic Shepard — Blend",
            ["race_liara"] = "Liara — Blend",
            ["mouthShape_ashley"] = "Ashley",
            ["mouthShape_iconic"] = "Iconic Shepard",
            ["mouthShape_liara"] = "Liara",
            ["mouthShape_oldAsn"] = "Asian — Old",
            ["mouthShape_yngAsn"] = "Asian — Young",
            ["mouthShape_oldBlk"] = "Black — Old",
            ["mouthShape_yngBlk"] = "Black — Young",
            ["mouthShape_oldCauc"] = "Caucasian — Old",
            ["mouthShape_yngCauc"] = "Caucasian — Young"
        };

    public override IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
        CreateFemaleCategories();

    public override MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var metadata = base.Describe(feature, sessionCanEdit);
        var name = feature.Feature.Name;
        var (category, subcategory) = GetFemalePlacement(name, metadata.CategoryKey, metadata.SubcategoryKey);
        return metadata with
        {
            Label = FeatureLabels.GetValueOrDefault(name) ?? HumaniseFemaleFeature(name, metadata.Label),
            CategoryKey = category,
            SubcategoryKey = subcategory,
            IsVisible = !HiddenFeatures.Contains(name),
            SortOrder = GetFemaleSortOrder(name, metadata.SortOrder),
            Description = feature.Kind == MorphFeatureResolutionKind.MetadataOnly
                ? $"{name} · stored Human Female creator metadata with no direct target delta."
                : metadata.Description
        };
    }

    public override MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition)
    {
        var described = base.DescribeMaterial(definition);
        var label = definition.Name switch
        {
            "HED_Makeup_Mask" => "Makeup Mask Texture",
            "HED_Blush_Scalar" => "Blush Strength",
            "HED_Blush_Vector" => "Blush Colour",
            "HED_Brow_Tint_Scalar" => "Eyeshadow Strength",
            "HED_Brow_Tint_Vector" => "Eyeshadow Colour",
            "HED_EyeShadow_Tint_Scalar" => "Makeup Strength",
            "HED_EyeShadow_Tint_Vector" => "Makeup Colour",
            "HED_Lips_Tint_Scalar" => "Lip Tint Strength",
            "HED_Lips_Tint_Vector" => "Lip Tint Colour",
            "HED_Addn_Blowout_Scalar" => "Primary Brow Colour Strength",
            "HED_Addn_Colour_02_Scalar" => "Secondary Brow Colour Strength",
            "HED_Addn_Colour_Vector" => "Primary Brow Colour",
            "blonde" => "Secondary Brow Colour",
            "HED_Spec_NoBrow" => "Addition Specular Suppression",
            "HED_Addn_Spec_Lips_Scalar" => "Lip Specular Strength",
            "HED_Addn_SPwr_Lips_Scalar" => "Lip Specular Power",
            "Highlight1SpecExp_Scalar" => "Highlight 1 Specular Exponent",
            "Highlight2SpecExp_Scalar" => "Highlight 2 Specular Exponent",
            "Highlight1Intensity" or "Hightlight1Intensity" => "Highlight 1 Intensity",
            "Highlight2Intensity" or "Hightlight2Intensity" => "Highlight 2 Intensity",
            "Highlight1Color" => "Highlight 1 Colour",
            "Highlight2Color" => "Highlight 2 Colour",
            _ => described.Label
        };
        var description = definition.Name switch
        {
            "HED_Brow_Tint_Scalar" or "HED_Brow_Tint_Vector" =>
                "The stock HMF makeup mask routes this nominal Brow parameter to the eyeshadow region.",
            "HED_EyeShadow_Tint_Scalar" or "HED_EyeShadow_Tint_Vector" =>
                "The stock HMF makeup mask uses this parameter for the broader eye and mouth makeup layer.",
            "HED_Addn_Blowout_Scalar" or "HED_Addn_Colour_02_Scalar" or
            "HED_Addn_Colour_Vector" or "blonde" =>
                "Colours the eyebrow layer selected by the packed HMF addition texture.",
            _ => described.Description
        };
        return described with
        {
            Label = label,
            Group = GetMaterialCategory(definition.Name, definition.Kind),
            Description = description
        };
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
                Description = "Brows, eye position, proportions, character shapes, and eyelids.",
                SliderGroups =
                [
                    new EditorSubcategoryDefinition(CharacterEyeShape, "CHARACTER SHAPES"),
                    new EditorSubcategoryDefinition(RaceEyeShape, "RACE SHAPES"),
                    new EditorSubcategoryDefinition(ShapeEyeShape, "SHAPE / ROTATION"),
                    new EditorSubcategoryDefinition(Brows, "BROWS"),
                    new EditorSubcategoryDefinition(Sockets, "SOCKETS"),
                    new EditorSubcategoryDefinition(Eyelids, "EYELIDS"),
                    new EditorSubcategoryDefinition("lashes", "LASHES"),
                    new EditorSubcategoryDefinition(Surface, "SURFACE")
                ]
            },
            Mouth => category with
            {
                SliderGroups =
                [
                    new EditorSubcategoryDefinition(CharacterMouth, "CHARACTER SHAPES"),
                    new EditorSubcategoryDefinition(RaceMouth, "RACE SHAPES"),
                    new EditorSubcategoryDefinition(ShapeMouth, "SHAPE"),
                    .. category.SliderGroups.Where(group => group.Key != "mouth-shape")
                ]
            },
            Additions => category with
            {
                Description = "Makeup, hair highlights, surface additions, face overlays, freckles, scars, and scalp settings.",
                SliderGroups =
                [
                    new EditorSubcategoryDefinition(Makeup, "MAKEUP"),
                    new EditorSubcategoryDefinition(MaterialHair, "HAIR"),
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
