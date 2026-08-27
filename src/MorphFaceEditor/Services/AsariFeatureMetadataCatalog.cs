using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>Maps raw Asari morph/material names into stable editor categories, labels and ordering.</summary>
public sealed partial class AsariFeatureMetadataCatalog : IHeadEditorUiProfile
{
    private static readonly HumanMaleFeatureMetadataCatalog HumanUi = new();

    public const string FacialStructure = "facial-structure";
    public const string Head = "head";
    public const string Eyes = "eyes";
    public const string Nose = "nose";
    public const string Mouth = "mouth";
    public const string Markings = "markings";

    private const string HeadCrest = "head-crest";
    private const string Cheeks = "cheeks";
    private const string Jaw = "jaw";
    private const string Surface = "surface";
    private const string Brows = "brows";
    private const string Sockets = "sockets";
    private const string Shape = "shape";
    private const string Bridge = "bridge";
    private const string Tip = "tip";
    private const string Nostrils = "nostrils";
    private const string Lips = "lips";
    private const string Teeth = "teeth";
    private const string Complexion = "complexion";
    private const string Makeup = "makeup";
    private const string Tattoos = "tattoos";

    // Stored creator values without matching geometry must round-trip but never become sliders.
    public static IReadOnlySet<string> MetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "eyes_PosDown",
            "eyes_Big",
            "jaw_narrow",
            "mouth_CornersDown",
            "nose_BridgeThin",
            "nose_TipUp",
            "race_iconic",
            "race_oldAsn",
            "race_oldBlk",
            "race_oldCauc",
            "race_yngAsn",
            "race_yngBlk",
            "race_yngCauc"
        };

    public static IReadOnlyDictionary<string, string> FeatureAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> HiddenFeatures =
        new HashSet<string>(MetadataOnlyFeatures, StringComparer.OrdinalIgnoreCase)
        {
            "baseHead",
            "lashes"
        };

    private readonly bool _mouthForwardIsVestigial;

    public AsariFeatureMetadataCatalog(bool mouthForwardIsVestigial = false)
    {
        _mouthForwardIsVestigial = mouthForwardIsVestigial;
    }

    public IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
    [
        new(FacialStructure, "Facial Structure", "Asari head-crest proportions.",
            [new(HeadCrest, "HEAD CREST")]),
        new(Head, "Head", "Cheek, skin-colour, complexion, and facial-surface controls.",
            [new(Cheeks, "CHEEKS"), new(Surface, "SURFACE")]),
        new(Eyes, "Eyes", "Brow, eye-position, eye-shape, iris, and sclera controls.",
            [new(Shape, "SHAPE"), new(Brows, "BROWS"), new(Sockets, "SOCKETS"), new(Surface, "SURFACE")]),
        new(Nose, "Nose", "Nose shape, bridge, tip, and nostril controls.",
            [new(Shape, "SHAPE"), new(Bridge, "BRIDGE"), new(Tip, "TIP"), new(Nostrils, "NOSTRILS")]),
        new(Mouth, "Mouth / Jaw", "Mouth shape, lips, teeth, jaw, and gloss controls.",
            [new(Shape, "MOUTH"), new(Lips, "LIPS"), new(Teeth, "TEETH"), new(Jaw, "JAW")]),
        new(Markings, "Markings", "Complexion additions, makeup, and facial tattoos.",
            [new(Complexion, "COMPLEXION"), new(Makeup, "MAKEUP"), new(Tattoos, "TATTOOS")])
    ];

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var (category, subcategory) = FeaturePlacement(feature.Feature.Name);
        return new MorphFeatureMetadata(
            feature.Feature.Name,
            FeatureLabel(feature.Feature.Name),
            category,
            subcategory,
            !HiddenFeatures.Contains(feature.Feature.Name),
            FeatureSortOrder(feature.Feature.Name),
            0,
            1,
            0.01f,
            sessionCanEdit && feature.IsResolved,
            feature.Kind == MorphFeatureResolutionKind.MetadataOnly
                ? $"{feature.Feature.Name} · preserved front-end metadata with no ASA target delta."
                : $"{feature.Feature.Name} · {feature.ResolutionNote ?? "resolved Asari morph target."}");
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition)
    {
        var label = definition.Family is HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes
            ? HumanUi.DescribeMaterial(definition).Label
            : MaterialLabel(definition.Name, definition.Label);
        return definition with
        {
            Label = label,
            Group = GetMaterialCategory(definition.Name, definition.Kind),
            Description = MaterialDescription(definition.Name, definition.Description)
        };
    }

    public string GetMaterialCategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        var definition = HumanMaterialProfiles.Describe(parameterName, kind);
        if (name == "mask" && kind == MaterialParameterKind.Scalar) return Mouth;
        if (name is "asa_hed_makeup_eyes" or "asa_hed_makeup_lips") return Markings;
        if (name is "asa_hed_addn" or "asa_hed_mask") return Markings;
        if (definition.Family is HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes ||
            name.Contains("eye") || name.Contains("iris") || name.Contains("lash")) return Eyes;
        if (name.Contains("teeth") || name.Contains("lip")) return Mouth;
        if (name.Contains("tatt") || name.Contains("makeup") || name.Contains("make_up") || name.Contains("addn")) return Markings;
        return Head;
    }

    public string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        return GetMaterialCategory(parameterName, kind) switch
        {
            Eyes => Surface,
            Mouth when name == "mask" => Teeth,
            Mouth when name.Contains("teeth") => Teeth,
            Mouth => Lips,
            Markings when name.Contains("tatt") => Tattoos,
            Markings when name.Contains("makeup") || name.Contains("make_up") || name.Contains("lip") => Makeup,
            Markings => Complexion,
            Head when name.Contains("spec") || name.Contains("spwr") || name.Contains("fresnel") || name.Contains("scattering") => Surface,
            _ => Surface
        };
    }

    public int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind)
    {
        var lower = parameterName.ToLowerInvariant();
        var definition = HumanMaterialProfiles.Describe(parameterName, kind);
        if (definition.Family is HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes)
        {
            return HumanUi.GetMaterialSortOrder(parameterName, kind);
        }
        return lower switch
        {
            "u_offset" => 0,
            "v_offset" => 1,
            "x_tile" => 2,
            "y_tile" => 3,
            "skintone" or "asa_hed_diff" => 0,
            "asa_hed_norm" => 1,
            "asa_hed_mask" => 2,
            "asa_hed_diffuse_02_colour" or "asa_hed_diffuse_02_colour_scalar" => 10,
            "asa_hed_addn" or "asa_hed_addn_mask_scalar" or "asa_hed_addn_mask_vector" => 20,
            "asa_hed_addn_colour" or "asa_hed_addn_colour_scalar" => 21,
            "asa_hed_makeup" or "asa_hed_makeup_switch_scalar" => 30,
            "asa_hed_makeup_blender_vector" => 31,
            "asa_hed_makeup_eyes" => 32,
            "asa_hed_makeup_lips" => 33,
            "asa_hed_tatt_01_scalar" or "asa_hed_tatt_01_vector" or "asa_hed_tatt_01" => 40,
            "asa_hed_tatt_02_scalar" or "asa_hed_tatt_02_vector" or "asa_hed_tatt_02" => 41,
            "asa_hed_tatt_blender_scalar" => 42,
            "asa_hed_tatt_colour" or "asa_hed_tatt" => 43,
            "asa_hed_lip_gloss_scalar" => 50,
            "mask" => 10,
            "asa_hed_face_fresnel_scalar" => 60,
            "asa_hed_spec_add" => 61,
            "asa_hed_spwr_add_scalar" => 62,
            "asa_hed_spwr_multiplier_scalar" => 63,
            "asa_hed_tmis_switch" or "asa_hed_tclr_tint" => 70,
            "skinlightscattering" => 71,
            _ => 1000
        };
    }

    private static (string Category, string Subcategory) FeaturePlacement(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("tentacle_")) return (FacialStructure, HeadCrest);
        if (lower.StartsWith("cheek")) return (Head, Cheeks);
        if (lower.StartsWith("jaw_")) return (Mouth, Jaw);
        if (lower.StartsWith("eye"))
        {
            if (lower.Contains("brow")) return (Eyes, Brows);
            if (lower is "eyes_back" or "eyes_forward" or "eyes_down" or "eyes_up" or "eyes_posdown") return (Eyes, Sockets);
            return (Eyes, Shape);
        }
        if (lower.StartsWith("nose_"))
        {
            if (lower.Contains("bridge")) return (Nose, Bridge);
            if (lower.Contains("tip")) return (Nose, Tip);
            if (lower.Contains("nostril")) return (Nose, Nostrils);
            return (Nose, Shape);
        }
        if (lower.StartsWith("teeth_")) return (Mouth, Teeth);
        if (lower.StartsWith("mouth_"))
        {
            if (lower.Contains("lip")) return (Mouth, Lips);
            return (Mouth, Shape);
        }
        return (Head, Surface);
    }

    private static int FeatureSortOrder(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower switch
        {
            "tentacle_short" => 0,
            "tentacle_long" => 1,
            "tentacle_large" => 2,

            "cheeks_back" => 0,
            "cheeks_forward" => 1,
            "cheeks_down" => 10,
            "cheeks_up" => 11,
            "cheeks_narrow" => 20,
            "cheeks_wide" => 21,
            "cheeks_gaunt" => 30,

            "eyes_browback" or "eyes_back" => 0,
            "eyes_browforward" or "eyes_forward" => 1,
            "eyes_browdown" or "eyes_down" => 10,
            "eyes_browup" or "eyes_up" => 11,
            "eyes_small" => 0,
            "eyes_large" => 1,
            "eyes_narrow" => 10,
            "eyes_wide" => 11,

            "nose_down" => 0,
            "nose_up" => 1,
            "nose_bottomin" => 10,
            "nose_bottomout" => 11,
            "nose_topin" => 20,
            "nose_topout" => 21,
            "nose_bridgein" => 0,
            "nose_bridgeout" => 1,
            "nose_bridgenarrow" => 10,
            "nose_bridgewide" => 11,
            "nose_tipdown" => 0,
            "nose_tipnarrow" => 10,
            "nose_tipwide" => 11,
            "nose_nostrilsnarrow" => 0,
            "nose_nostrilswide" => 1,

            "mouth_back" => 0,
            "mouth_forward" => 1,
            "mouth_down" => 10,
            "mouth_up" => 11,
            "mouth_narrow" => 20,
            "mouth_wide" => 21,
            "mouth_cornersup" => 30,
            "mouth_upperlipfat" => 0,
            "mouth_lowerlipfat" => 1,

            "jaw_chinback" => 0,
            "jaw_chinforward" => 1,
            "jaw_forward" => 10,
            "jaw_wide" => 11,
            "teeth_down" => 0,
            "teeth_up" => 1,
            _ => 1000
        };
    }

    private string FeatureLabel(string name)
    {
        var lower = name.ToLowerInvariant();
        var explicitLabel = lower switch
        {
            "nose_nostrilsnarrow" => "Nostrils Narrow",
            "nose_nostrilswide" => "Nostrils Wide",
            "mouth_forward" => _mouthForwardIsVestigial ? "Mouth Forward (Vestigial)" : "Mouth Forward",
            "mouth_upperlipfat" => "Upper Lip Full",
            "mouth_lowerlipfat" => "Lower Lip Full",
            "jaw_forward" => "Jaw Forward",
            "jaw_wide" => "Jaw Wide",
            _ => null
        };
        if (explicitLabel is not null)
        {
            return explicitLabel;
        }

        var leaf = name[(name.IndexOf('_') + 1)..];
        var label = WordBoundary().Replace(leaf.Replace('_', ' '), " $1");
        label = string.Join(' ', label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        var normalized = label switch
        {
            "Gaunt" => "Gaunt",
            "Big" or "Large" => "Large",
            "Long" => "Long",
            _ => label
        };
        return lower switch
        {
            _ when lower.StartsWith("tentacle_") => $"Head Crest {normalized}",
            _ when lower.StartsWith("cheek") => $"Cheeks {normalized}",
            _ when lower.StartsWith("eyes_brow") => $"Brows {normalized[4..].TrimStart()}",
            _ when lower.StartsWith("eyes_") => $"Eyes {normalized}",
            _ when lower.StartsWith("nose_") => $"Nose {normalized}",
            _ when lower.StartsWith("mouth_") => $"Mouth {normalized}",
            _ when lower.StartsWith("teeth_") => $"Teeth {normalized}",
            _ when lower.StartsWith("jaw_chin") => $"Chin {normalized[4..].TrimStart()}",
            _ => normalized
        };
    }

    private static string MaterialLabel(string name, string fallback) => name switch
    {
        "SkinTone" => "Skin Tone",
        "SkinLightScattering" => "Skin Light Scattering",
        "ASA_HED_Diff" => "Diffuse Texture",
        "ASA_HED_Norm" => "Normal Texture",
        "ASA_HED_Mask" => "Mask Texture",
        "Mask" => "Teeth Opacity Mask",
        "ASA_HED_Diffuse_02_Colour" => "Secondary Skin Colour",
        "ASA_HED_Diffuse_02_Colour_Scalar" => "Secondary Skin Blend",
        "ASA_HED_Addn" => "Complexion Texture",
        "ASA_HED_Addn_Mask_Vector" => "Addition Mask Channels",
        "ASA_HED_Addn_Mask_Scalar" => "Addition Mask Alpha",
        "ASA_HED_Addn_Colour" => "Addition Colour",
        "ASA_HED_Addn_Colour_Scalar" => "Addition Colour Strength",
        "ASA_HED_MakeUp" => "Makeup Texture",
        "ASA_HED_MakeUp_Switch_Scalar" => "Makeup Strength",
        "ASA_HED_MakeUp_Eyes" => "Makeup Eyes Colour",
        "ASA_HED_MakeUp_Lips" => "Makeup Lips Colour",
        "ASA_HED_Makeup_Blender_Vector" => "Makeup Region Weights (R Lips, G Eyes, B Teeth)",
        "ASA_HED_Tatt" => "Tattoo Texture",
        "ASA_HED_Tatt_Colour" => "Tattoo Colour",
        "ASA_HED_Tatt_01_Scalar" => "Tattoo 1 Region Alpha",
        "ASA_HED_Tatt_01_Vector" => "Tattoo 1 Region Channels",
        "ASA_HED_Tatt_01" => "Tattoo 1 Pattern Channels",
        "ASA_HED_Tatt_02_Scalar" => "Tattoo 2 Region Alpha",
        "ASA_HED_Tatt_02_Vector" => "Tattoo 2 Region Channels",
        "ASA_HED_Tatt_02" => "Tattoo 2 Pattern Channels",
        "ASA_HED_Tatt_Blender_Scalar" => "Tattoo Blend",
        "ASA_HED_Lip_Gloss_Scalar" or "ASA_HED_Lip_Gloss" => "Lip Gloss",
        "ASA_HED_Face_Fresnel_Scalar" => "Face Fresnel Strength",
        "ASA_HED_Spec_Add" => "Specular Addition Colour",
        "ASA_HED_SPwr_Add_Scalar" => "Specular Power Addition",
        "ASA_HED_SPwr_Multiplier_Scalar" => "Specular Power Multiplier",
        "ASA_HED_TMis_Switch" => "Transmission Strength",
        "ASA_HED_TClr_Tint" => "Transmission Colour",
        "ASA_HED_Teeth_Colour_Vector" => "Teeth Colour",
        _ => ExpandAsariMaterialLabel(name, fallback)
    };

    private static string MaterialDescription(string name, string fallback) => name switch
    {
        "Mask" => "Controls teeth visibility. Any value under 0.33 will hide the teeth.",
        "ASA_HED_MakeUp_Switch_Scalar" =>
            "Overall makeup strength. The relevant Makeup Region Weight must also be above zero.",
        "ASA_HED_MakeUp_Eyes" =>
            "Eye-makeup colour. Its effect is selected by the makeup texture's green channel and the green Region Weight.",
        "ASA_HED_MakeUp_Lips" =>
            "Lip-makeup colour. Its effect is selected by the makeup texture's red channel and the red Region Weight.",
        "ASA_HED_Makeup_Blender_Vector" =>
            "Per-region makeup weights: red enables lips, green enables eyes, and blue enables the teeth selector. These weights multiply Makeup Strength.",
        _ => fallback
    };

    private static string ExpandAsariMaterialLabel(string name, string fallback)
    {
        if (!name.StartsWith("ASA_HED_", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }
        var text = name[8..]
            .Replace("MakeUp", "Makeup", StringComparison.OrdinalIgnoreCase)
            .Replace("Addn", "Addition", StringComparison.OrdinalIgnoreCase)
            .Replace("TMis", "Transmission", StringComparison.OrdinalIgnoreCase)
            .Replace("TClr", "Transmission Colour", StringComparison.OrdinalIgnoreCase)
            .Replace("SPwr", "Specular Power", StringComparison.OrdinalIgnoreCase)
            .Replace("Tatt", "Tattoo", StringComparison.OrdinalIgnoreCase)
            .Replace("Norm", "Normal", StringComparison.OrdinalIgnoreCase)
            .Replace("Diff", "Diffuse", StringComparison.OrdinalIgnoreCase)
            .Replace("_Scalar", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_Vector", " Colour", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ');
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    [GeneratedRegex("([A-Z]+(?=$|[A-Z][a-z])|[A-Z]?[a-z]+|[0-9]+)")]
    private static partial Regex WordBoundary();
}
