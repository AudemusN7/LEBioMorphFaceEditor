using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

public sealed record EditorCategoryDefinition(
    string Key,
    string Label,
    string Description,
    IReadOnlyList<EditorSubcategoryDefinition> SliderGroups);

public sealed record EditorSubcategoryDefinition(string Key, string Label);

public sealed record MorphFeatureMetadata(
    string Name,
    string Label,
    string CategoryKey,
    string SubcategoryKey,
    bool IsVisible,
    int SortOrder,
    float Minimum,
    float Maximum,
    float Step,
    bool IsEditable,
    string Description);

/// <summary>
/// Supplies the presentation taxonomy for a particular head/morph system. The editor
/// consumes this contract rather than assuming that every species has human anatomy.
/// </summary>
public interface IHeadEditorUiProfile
{
    IReadOnlyList<EditorCategoryDefinition> Categories { get; }
    MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit);
    MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition);
    bool IsMaterialVisible(string parameterName, MaterialParameterKind kind) => true;
    string GetMaterialCategory(string parameterName, MaterialParameterKind kind);
    string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind);
    int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind);
}

public class HumanMaleFeatureMetadataCatalog : IHeadEditorUiProfile
{
    protected virtual string TextArchetype => MorphFaceMetadataCatalogRegistry.HumanMale;
    protected virtual MorphFaceGame? TextGame => null;

    public const string FacialStructure = "facial-structure";
    public const string Head = "head";
    public const string Eyes = "eyes";
    public const string NeckJaw = "neck-jaw";
    public const string Mouth = "mouth";
    public const string Nose = "nose";
    public const string Additions = "additions";

    private const string Character = "character";
    private const string Race = "race";
    private const string Hair = "hair";
    private const string Shape = "shape";
    private const string Cheeks = "cheeks";
    private const string Ears = "ears";
    private const string Brows = "brows";
    private const string Sockets = "sockets";
    private const string EyeShape = "eye-shape";
    private const string Eyelids = "eyelids";
    private const string Lashes = "lashes";
    private const string Surface = "surface";
    private const string Bridge = "bridge";
    private const string Tip = "tip";
    private const string Nostrils = "nostrils";
    private const string MouthShape = "mouth-shape";
    private const string Lips = "lips";
    private const string Teeth = "teeth";
    private const string Chin = "chin";
    private const string Jaw = "jaw";
    private const string Neck = "neck";
    private const string Addition = "addition";
    private const string Face = "face";
    private const string Scalp = "scalp";

    private static readonly IReadOnlySet<string> HiddenFeatureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Proven zero-effect or internal construction/corrective targets.
        "averageHead", "baseHead", "BONE_blinkFix", "DEBUG_EyeLidCorrector", "DEBUG_InnerEyeCorrector",
        "eyeShape_droop", "eye_Shape_droop",
        // Composite/duplicate variants superseded by the explicit controls in the profile.
        "BuzzCut", "BuzzCut_WidowsPeak", "flatTop_WidowsPeak", "objobjWillis", "Willis01",
        // Character-creator metadata with no live geometry. Formal/Sarge/Slick are retained
        // because they are explicitly part of the requested hairstyle surface.
        "None", "Formal", "Sarge", "Slick", "lashes", "eyes_bagOut", "cheeks_gaunt", "eyeShape_liara", "eyeShape_iconic",
        "eyeShape_oldBlk", "eyeShape_yngAsn", "nose_BottomThin", "nose_BottomWide",
        "race_asnOld", "race_asnYoung", "race_blackOld", "race_Blackyng", "race_cauOld", "race_cauYng",
        "teeth_canineExtend", "teeth_Narrow", "teeth_Wide"
    };

    public virtual IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
    [
        new(FacialStructure, "Facial Structure", "Character likeness, race, and geometry-based hairstyles.",
            [new(Character, "CHARACTER"), new(Race, "RACE"), new(Hair, "HAIR")]),
        new(Head, "Head", "Face shape, cheeks, ears, skin, and base head surface settings.",
            [new(Shape, "SHAPE"), new(Ears, "EARS"), new(Cheeks, "CHEEKS")]),
        new(Eyes, "Eyes", "Brows, sockets, eyes, eyelids, lashes, pupils, iris colour, and eye textures.",
            [new(EyeShape, "SHAPE"), new(Brows, "BROWS"), new(Sockets, "SOCKETS"), new(Eyelids, "EYELIDS"), new(Lashes, "LASHES"), new(Surface, "SURFACE")]),
        new(Nose, "Nose", "Nose shape, bridge, tip, and nostrils.",
            [new(Shape, "SHAPE"), new(Bridge, "BRIDGE"), new(Tip, "TIP"), new(Nostrils, "NOSTRILS")]),
        new(Mouth, "Mouth", "Mouth shape, lips, bite, teeth, and philtrum.",
            [new(MouthShape, "MOUTH"), new(Lips, "LIPS"), new(Teeth, "TEETH")]),
        new(NeckJaw, "Neck / Jaw", "Chin, jaw, neck, Adam's apple, and lower-face structure.",
            [new(Jaw, "JAW"), new(Chin, "CHIN"), new(Neck, "NECK")]),
        new(Additions, "Additions", "Surface additions, face overlays, freckles, scars, and scalp settings.",
            [new(Addition, "ADDITION"), new(Face, "FACE"), new(Scalp, "SCALP")])
    ];

    public virtual MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var (category, subcategory) = GetFeaturePlacement(feature.Feature.Name);
        var editable = sessionCanEdit && feature.IsResolved;
        var description = feature.Kind == MorphFeatureResolutionKind.MetadataOnly
            ? "Stored character-creator metadata with no direct vertex or bone delta."
            : string.Empty;
        var metadata = new MorphFeatureMetadata(
            feature.Feature.Name,
            HumaniseFeatureName(feature.Feature.Name),
            category,
            subcategory,
            !HiddenFeatureNames.Contains(feature.Feature.Name),
            GetFeatureSortOrder(feature.Feature.Name),
            0,
            1,
            0.01f,
            editable,
            description);
        return MetadataTextCatalog.Apply(TextArchetype, metadata, TextGame);
    }

    public virtual MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition)
    {
        var category = GetMaterialCategory(definition.Name, definition.Kind);
        var described = definition with
        {
            Label = HumaniseMaterialIdentifier(definition.Name, definition.Label),
            Group = category,
            Description = definition.Description
        };
        return MetadataTextCatalog.Apply(TextArchetype, described, TextGame);
    }

    public virtual bool IsMaterialVisible(string parameterName, MaterialParameterKind kind)
    {
        if (kind != MaterialParameterKind.Texture) return true;
        // Player scar alignment textures are selected by Paragon/Renegade at runtime.
        return !parameterName.Equals("Diffuseuse", StringComparison.OrdinalIgnoreCase) &&
               !parameterName.Equals("CubeMap", StringComparison.OrdinalIgnoreCase) &&
               !parameterName.Equals("HED_Face_Alignment_Emis", StringComparison.OrdinalIgnoreCase) &&
               !parameterName.Equals("HED_Face_Alignment_Norm", StringComparison.OrdinalIgnoreCase) &&
               !parameterName.StartsWith("__", StringComparison.Ordinal);
    }

    public virtual string GetMaterialCategory(string parameterName, MaterialParameterKind kind)
    {
        var definition = HumanMaterialProfiles.Describe(parameterName, kind);
        var name = parameterName.ToLowerInvariant();
        // The packed teeth selector belongs in Mouth despite using the scalp material family.
        if (kind == MaterialParameterKind.Texture && name == "hed_teeth_diff")
        {
            return Mouth;
        }
        // Human scalp textures belong with the base head surface. Other scalp
        // controls (colours and scalars) retain their existing taxonomy.
        if (kind == MaterialParameterKind.Texture &&
            definition.Family == HeadMaterialFamily.Scalp)
        {
            return Head;
        }
        if (name == "mask" && kind == MaterialParameterKind.Scalar)
        {
            return Additions;
        }
        if ((name == "hed_mask" && kind == MaterialParameterKind.Texture) ||
            (name == "hed_mask_vector" && kind == MaterialParameterKind.Vector))
        {
            return Additions;
        }
        if (definition.Family is HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes ||
            name.Contains("eye") || name.Contains("iris") || name.Contains("lash"))
        {
            return Eyes;
        }
        if (name.Contains("teeth"))
        {
            return Mouth;
        }
        if (definition.Family is HeadMaterialFamily.Hair ||
            name.Contains("hair") || name.Contains("scalp") || name.Contains("frek") ||
            name.Contains("scar") || name.Contains("addn") || name.Contains("brow") ||
            name.Contains("blonde"))
        {
            return Additions;
        }
        return Head;
    }

    public virtual string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind)
    {
        var definition = HumanMaterialProfiles.Describe(parameterName, kind);
        var name = parameterName.ToLowerInvariant();
        if (GetMaterialCategory(parameterName, kind) == Additions)
        {
            if (kind == MaterialParameterKind.Vector && IsHairHighlightColourVector(name))
            {
                return Scalp;
            }
            if (name == "mask" && kind == MaterialParameterKind.Scalar)
            {
                return Scalp;
            }
            if (definition.Family == HeadMaterialFamily.Scalp || name.Contains("scalp"))
            {
                return Scalp;
            }
            if (name.Contains("addn") || name.Contains("hair") || name.Contains("blonde"))
            {
                return Addition;
            }
            if (name == "hed_mask_vector")
            {
                return Addition;
            }
            return Face;
        }
        if (GetMaterialCategory(parameterName, kind) == Mouth && name.Contains("teeth"))
        {
            return Teeth;
        }
        if (GetMaterialCategory(parameterName, kind) == Eyes)
        {
            return name == "hed_lash_opac_scalar" ? Lashes : Surface;
        }
        return Face;
    }

    private static bool IsHairHighlightColourVector(string name) => name is
        "highlight1color" or "highlight2color" or
        "highlight1colour_vector" or "highlight2colour_vector";

    public virtual int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        if (kind == MaterialParameterKind.Vector)
        {
            return name switch
            {
                "eye_iris_colour_vector" => 0,
                "eye_white_colour_vector" => 1,
                "tmission_color" => 2,
                "eyelightscattering" => 3,
                "emis_color" => 4,
                "hed_eye_fx_vector" => 5,
                "hed_lash_diff_vector" => 0,
                "hed_teeth_vector" => 0,
                "skintone" => 0,
                "hed_mask_vector" => 10,
                "hed_addn_colour_vector" => 20,
                "blonde" => 21,
                "hed_scar_colour_vector" => 30,
                "hed_frek_redchannel_vector" => 40,
                "hed_frek_greenchannel_vector" => 41,
                "hed_frek_bluechannel_vector" => 42,
                "hed_spec_add_vector" => 50,
                "hed_tclr_vector" => 60,
                "skinlightscattering" => 61,
                "hed_hair_colour_vector" => 0,
                _ => 1000
            };
        }
        if (kind == MaterialParameterKind.Texture)
        {
            return name switch
            {
                "eye_diff" => 0,
                "eye_iris_norm" => 1,
                "eye_lens_norm" => 2,
                "eye_mask" => 3,
                "hed_lash_diff" => 0,
                "hed_teeth_diff" => 0,
                "hed_diff" => 0,
                "hed_mask" => 10,
                "hed_norm" => 20,
                "hed_norm_02" => 21,
                "hed_addn" => 30,
                "hed_frek" => 40,
                "hed_scalp_diff" => 0,
                "hed_scalp_spec" => 10,
                "hed_scalp_norm" => 20,
                "hed_tang" => 30,
                "hed_scalp_specshift" => 40,
                "hed_scalp_specshift2" => 41,
                "hair_diff" or "hair_addn_diff" => 0,
                "hair_mask" => 10,
                "hair_norm" => 20,
                "hair_tang" => 30,
                "hair_specshift" => 40,
                "hair_specshift2" => 41,
                _ => 1000
            };
        }
        var order = name switch
        {
            "u_offset" => 0,
            "v_offset" => 1,
            "x_tile" => 2,
            "y_tile" => 3,
            "iris_colour_multiplier" => 10,
            "sclera_darken" => 11,
            "primary_reflection_multiplier" => 20,
            "secondary_reflection_multiplier" => 21,
            "emis_scalar" or "emis_color" => 30,
            "hed_eye_fx_scalar" => 40,
            "hed_lash_opac_scalar" => 100,
            "hed_lash_spec_scalar" => 1,
            "hed_teeth_scalar" => 0,
            "hed_addn_blend_scalar" => 0,
            "hed_addn_add_scalar" => 1,
            "hed_addn_multiply_scalar" => 2,
            "hed_addn_blowout_scalar" => 3,
            "hed_addn_colour_02_scalar" => 4,
            "hed_addn_spwr_add_scalar" => 5,
            "hed_addn_spec_add_scalar" => 6,
            "hed_mask_scalar" => 0,
            "hed_norm_blend" => 10,
            "hed_frek_redchannel_scalar" => 10,
            "hed_frek_greenchannel_scalar" => 11,
            "hed_frek_bluechannel_scalar" => 12,
            "hed_scar_scalar" => 20,
            "hed_brow_fadeout_scalar" => 21,
            "hed_tmis_scalar" => 30,
            "hed_spwr_scalar" => 31,
            "hed_scalp_mask_scalar" => 0,
            "hed_scalp_mask_overlaykill_scalar" => 1,
            "hair_mask_alpha_scalar" => 2,
            "hed_scalp_buzzcut_alpha_scalar" => 3,
            "hair_shine_desaturate_scalar" => 4,
            "mask" => 5,
            "hed_scalp_phongspec_scalar" => 10,
            "hed_spec_aniso_exp_scalar" => 11,
            _ => 1000
        };
        return order;
    }

    private static (string Category, string Subcategory) GetFeaturePlacement(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is "anderson" or "joker" or "kaiden" or "eastwood" or "jacob" or "shepard")
        {
            return (FacialStructure, Character);
        }
        if (lower.StartsWith("race_"))
        {
            return (FacialStructure, Race);
        }
        if (lower is "afro" or "buzzcut" or "deiter" or "flattop" or "formal" or "geezer" or
            "rollins" or "sarge" or "debug_capcorrector" or "slick" or "straighthairline" or
            "widowspeak" or "willis")
        {
            return (FacialStructure, Hair);
        }
        if (lower.StartsWith("shape_")) return (Head, Shape);
        if (lower.StartsWith("cheek") || lower == "mouth_cheekmass") return (Head, Cheeks);
        if (lower.StartsWith("ear")) return (Head, Ears);

        if (lower.StartsWith("eye") || lower.StartsWith("pupil") || lower == "lashes")
        {
            if (lower.Contains("brow")) return (Eyes, Brows);
            if (lower.Contains("socket") || lower is "eyes_back" or "eyes_forward" or "eyes_posdown" or "eyes_posup") return (Eyes, Sockets);
            if (lower.Contains("lid") || lower.Contains("bags")) return (Eyes, Eyelids);
            if (lower.Contains("lash") || lower == "lashes") return (Eyes, Lashes);
            return (Eyes, EyeShape);
        }

        if (lower.StartsWith("nose"))
        {
            if (lower.Contains("bridge")) return (Nose, Bridge);
            if (lower.Contains("tip")) return (Nose, Tip);
            if (lower.Contains("nostril")) return (Nose, Nostrils);
            return (Nose, Shape);
        }

        if (lower == "hir_timselect") return (Mouth, MouthShape);
        if (lower.StartsWith("teeth")) return (Mouth, Teeth);
        if (lower.StartsWith("mouth"))
        {
            if (lower.Contains("lip") || lower is "mouthshape_fatlips" or
                "mouthshape_overbite" or "mouthshape_underbite") return (Mouth, Lips);
            return (Mouth, MouthShape);
        }

        if (lower is "hir_beard" or "hir_beardtipmorph" || lower.StartsWith("jaw_chin") || lower == "jaw_doublechin") return (NeckJaw, Chin);
        if (lower.StartsWith("jaw") || lower == "jawlower") return (NeckJaw, Jaw);
        if (lower.StartsWith("neck")) return (NeckJaw, Neck);
        return (FacialStructure, Hair);
    }

    private static int GetFeatureSortOrder(string name) => name.ToLowerInvariant() switch
    {
        // Facial Structure — Character / Race / Hair
        "anderson" => 0, "joker" => 1, "kaiden" => 2, "eastwood" => 3, "jacob" => 4, "shepard" => 5,
        "race_oldasn" => 0, "race_yngasn" => 1,
        "race_oldblk" => 10, "race_yngblk" => 11,
        "race_oldcauc" => 20, "race_yngcauc" => 21,
        "afro" => 0, "deiter" => 1, "flattop" => 2, "geezer" => 3,
        "rollins" => 4, "debug_capcorrector" => 5, "straighthairline" => 6,
        "widowspeak" => 7, "willis" => 8,

        // Head — Shape / Cheeks / Ears
        "shape_skinny" => 0, "shape_chubby" => 1,
        "cheek_back" => 0, "cheek_forward" => 1, "cheek_up" => 2,
        "cheek_bonesin" => 10, "cheek_bonesout" => 11,
        "cheek_depthback" => 20, "cheek_depthfront" => 21,
        "cheek_gaunt" => 30, "mouth_cheekmass" => 31,
        "ears_down" => 0, "ears_up" => 1, "ears_in" => 10, "ears_out" => 11,
        "ears_small" => 20, "ears_large" => 21,

        // Eyes — Brows / Sockets / Eyes / Eyelids / Lashes
        "eyes_browback" => 0, "eyes_browforward" => 1,
        "eyes_browdown" => 10, "eyes_browup" => 11,
        "eyes_back" => 0, "eyes_forward" => 1,
        "eyes_posdown" => 10, "eyes_posup" => 11, "eyes_socketshift" => 20,
        "eyes_ballback" => 0, "eyes_ballforward" => 1,
        "eyes_balldown" => 10, "eyes_ballup" => 11,
        "eyes_small" => 20, "eyes_big" => 21, "eyes_narrow" => 30, "eyes_wide" => 31,
        "eyes_rotatein" => 40, "eyes_rotateout" => 41,
        "eyes_slantdown" => 50, "eyes_slantup" => 51,
        "eyes_shape_droop" => 60, "eyes_shape_flattop" => 61,
        "eyes_shape_outerpoint" => 62, "eyes_shape_sleepy" => 63,
        "eyes_shape_squint" => 64, "eyes_shape_wide" => 65,
        "pupil_small" => 80, "pupil_large" => 81,
        "eyes_bagsin" => 0, "eyes_bagsout" => 1,
        "eyes_lidlower" => 10, "eyes_lidupper" => 11,
        "eyes_lashangle" => 0, "eyes_lashlength" => 1,

        // Nose — Shape / Bridge / Tip / Nostrils
        "nose_bendleft" => 0, "nose_bendright" => 1,
        "nose_bottomin" => 10, "nose_bottomout" => 11,
        "nose_topin" => 20, "nose_topout" => 21,
        "nose_down" => 30, "nose_up" => 31,
        "nose_bridgein" => 0, "nose_bridgeout" => 1,
        "nose_bridgethin" => 10, "nose_bridgewide" => 11,
        "nose_tipdown" => 0, "nose_tipup" => 1, "nose_tipin" => 10,
        "nose_tipnarrow" => 20, "nose_tipwide" => 21,
        "nose_nostrilsnarrow" => 0, "nose_nostrilswide" => 1,

        // Mouth — Mouth / Lips / Teeth
        "mouth_back" => 0, "mouth_forward" => 1,
        "mouth_down" => 10, "mouth_up" => 11,
        "mouth_narrow" => 20, "mouth_wide" => 21,
        "mouthshape_thin" => 29,
        "mouth_cornersdown" => 30, "mouth_cornersup" => 31,
        "mouth_overbite" => 40, "mouth_underbite" => 41,
        "mouthshape_centerkleft" => 50, "mouthshape_diddy" => 51,
        "mouthshape_philtrum" => 52, "mouthshape_pinchedsides" => 53,
        "hir_timselect" => 60,
        "mouth_lipsthin" => 0, "mouth_lipsfat" => 1,
        "mouthshape_fatlips" => 10,
        "mouthshape_overbite" => 18, "mouthshape_underbite" => 19,
        "mouth_lowerlipfat" => 20, "mouth_upperlipfat" => 21,
        "mouth_lowerlipup" => 30, "mouth_upperlipdown" => 31,
        "mouth_upperlip" => 40,
        "teeth_back" => 0, "teeth_forward" => 1,
        "teeth_down" => 10, "teeth_up" => 11,
        "teeth_close" => 20, "teeth_seperate" => 21,
        "teeth_canine" => 30, "teeth_frontteeth" => 31,
        "teeth_chiptoothleft" => 40, "teeth_chiptoothright" => 41,
        "teeth_missingleft" => 50, "teeth_nofront" => 51,

        // Neck / Jaw — Chin / Jaw / Neck
        "jaw_chindown" => 0, "jaw_chinup" => 1,
        "jaw_chinin" => 10, "jaw_chinout" => 11,
        "jaw_chinthin" => 20, "jaw_chinwide" => 21,
        "jaw_doublechin" => 30, "hir_beard" => 40, "hir_beardtipmorph" => 41,
        "jaw_narrow" => 0, "jaw_wide" => 1, "jawlower" => 10,
        "neck_thin" => 0, "neck_wide" => 1, "neck_apple" => 10,
        _ => 1000
    };

    private static string HumaniseFeatureName(string name)
    {
        var text = Regex.Replace(name, "^(mouthShape|eyeShape|eyes|eye|cheeks|cheek|ears|jaw|neck|nose|pupil|race|shape|teeth|HIR)_?", string.Empty, RegexOptions.IgnoreCase);
        text = SplitIdentifier(text);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.ToLowerInvariant() switch
            {
                "pos" => "Position",
                "yng" => "Young",
                "asn" => "Asian",
                "blk" => "Black",
                "cauc" => "Caucasian",
                "norm" => "Normal",
                "diff" => "Diffuse",
                "kleft" => "Cleft",
                _ => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
            });
        return AddAnatomyPrefix(name, string.Join(' ', words));
    }

    private static string AddAnatomyPrefix(string name, string label)
    {
        var lower = name.ToLowerInvariant();
        if (lower == "mouth_cheekmass") return "Cheeks Mass";
        if (lower.StartsWith("cheek")) return PrefixUnlessPresent("Cheeks", label, "Cheek");
        if (lower.StartsWith("ear")) return PrefixUnlessPresent("Ears", label, "Ear");
        if (lower.Contains("nostril")) return PrefixUnlessPresent("Nostrils", label, "Nostril");
        if (lower == "mouthshape_fatlips") return "Lip Shape Full";
        if (lower.Contains("lip")) return label.Contains("Lip", StringComparison.OrdinalIgnoreCase)
            ? label
            : $"Lips {label}";
        if (lower.StartsWith("teeth")) return PrefixUnlessPresent("Teeth", label, "Teeth");
        if ((lower.StartsWith("jaw_") || lower == "jawlower") &&
            !lower.StartsWith("jaw_chin") && lower != "jaw_doublechin")
        {
            return PrefixUnlessPresent("Jaw", label, "Jaw");
        }
        if (lower == "neck_apple") return label;
        if (lower.StartsWith("neck")) return PrefixUnlessPresent("Neck", label, "Neck");
        return label;
    }

    private static string PrefixUnlessPresent(string prefix, string label, string existingPrefix) =>
        label.StartsWith(existingPrefix, StringComparison.OrdinalIgnoreCase) ? label : $"{prefix} {label}";

    private static string HumaniseMaterialIdentifier(string name, string fallback)
    {
        var text = Regex.Replace(name, "^(HED|EYE|HAIR)_", string.Empty, RegexOptions.IgnoreCase);
        text = SplitIdentifier(text)
            .Replace("Frek", "Freckles", StringComparison.OrdinalIgnoreCase)
            .Replace("Addn", "Addition", StringComparison.OrdinalIgnoreCase)
            .Replace("SPwr", "Specular Power", StringComparison.OrdinalIgnoreCase)
            .Replace("Opac", "Opacity", StringComparison.OrdinalIgnoreCase)
            .Replace("Emis", "Emissive", StringComparison.OrdinalIgnoreCase)
            .Replace("Norm", "Normal", StringComparison.OrdinalIgnoreCase)
            .Replace("Diff", "Diffuse", StringComparison.OrdinalIgnoreCase)
            .Replace("Scalar", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Vector", "Colour", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, "\\bSpec\\b", "Specular", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "\\s+", " ").Trim();
        text = Regex.Replace(text, "\\bColour Colour\\b", "Colour", RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string SplitIdentifier(string value)
    {
        var text = value.Replace('_', ' ');
        text = Regex.Replace(text, "(?<=[a-z0-9])(?=[A-Z])", " ");
        text = Regex.Replace(text, "(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return text;
    }
}
