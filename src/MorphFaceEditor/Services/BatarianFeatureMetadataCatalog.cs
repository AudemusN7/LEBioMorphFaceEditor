using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>Owns Batarian UI taxonomy and the distinction between editable targets and creator metadata.</summary>
public sealed partial class BatarianFeatureMetadataCatalog : IHeadEditorUiProfile
{
    public const string FacialStructure = "facial-structure";
    public const string Head = "head";
    public const string Eyes = "eyes";
    public const string Nose = "nose";
    public const string Mouth = "mouth";
    public const string Markings = "markings";

    private const string Cranium = "cranium";
    private const string Ears = "ears";
    private const string RegionalColour = "regional-colour";
    private const string Cheeks = "cheeks";
    private const string HeadShape = "head-shape";
    private const string Neck = "neck";
    private const string Surface = "surface";
    private const string Position = "position";
    private const string Shape = "shape";
    private const string NoseShape = "nose-shape";
    private const string MouthShape = "mouth-shape";
    private const string Teeth = "teeth";
    private const string Jaw = "jaw";
    private const string Chin = "chin";
    private const string Complexion = "complexion";

    // These values occur in recipes but have no geometry target in supported games.
    public static IReadOnlySet<string> MetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "jaw_narrow",
            "shape_skinny"
        };

    public static IReadOnlyDictionary<string, string> FeatureAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
    [
        new(FacialStructure, "Facial Structure", "Batarian-specific cranium and regional-colour controls.",
            [new(Cranium, "CRANIUM"), new(RegionalColour, "REGIONAL COLOUR")]),
        new(Head, "Head", "Overall shape, forehead, ears, cheeks, neck width, and surface controls.",
            [new(HeadShape, "SHAPE"), new(Ears, "EARS"), new(Cheeks, "CHEEKS"), new(Neck, "NECK"), new(Surface, "SURFACE")]),
        new(Eyes, "Eyes", "Eye position and shape controls.",
            [new(Shape, "SHAPE"), new(Position, "POSITION")]),
        new(Nose, "Nose", "Nose position and profile controls.",
            [new(NoseShape, "SHAPE")]),
        new(Mouth, "Mouth / Jaw", "Mouth, teeth, jaw, and chin controls.",
            [new(MouthShape, "MOUTH"), new(Teeth, "TEETH"), new(Jaw, "JAW"), new(Chin, "CHIN")]),
        new(Markings, "Markings", "Complexion texture, mask, colour, and specular controls.",
            [new(Complexion, "COMPLEXION")])
    ];

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var name = feature.Feature.Name;
        var (category, subcategory) = FeaturePlacement(name);
        var visible = !name.Equals("teeth_correction", StringComparison.OrdinalIgnoreCase) &&
                      !MetadataOnlyFeatures.Contains(name);
        return new MorphFeatureMetadata(
            name,
            FeatureLabel(name),
            category,
            subcategory,
            visible,
            FeatureSortOrder(name),
            0,
            1,
            0.01f,
            visible && sessionCanEdit && feature.IsResolved,
            $"{name} · {feature.ResolutionNote ?? "resolved Batarian morph target."}");
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) => definition with
    {
        Label = MaterialLabel(definition.Name, definition.Label),
        Group = GetMaterialCategory(definition.Name, definition.Kind)
    };

    public string GetMaterialCategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        if (name.Contains("neck_grad") || name.Contains("face_grad") || name.Contains("tophead_grad"))
            return FacialStructure;
        if (name.Contains("teeth")) return Mouth;
        if (name.Contains("addn") || name.Contains("mask")) return Markings;
        return Head;
    }

    public string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind)
    {
        var category = GetMaterialCategory(parameterName, kind);
        return category switch
        {
            FacialStructure => RegionalColour,
            Mouth => Teeth,
            Markings => Complexion,
            _ => Surface
        };
    }

    public int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind) =>
        parameterName.ToLowerInvariant() switch
        {
            "skintone" or "bat_hed_diff" => 0,
            "bat_hed_norm" => 1,
            "bat_hed_spec" => 2,
            "bat_hed_tint" => 3,
            "bat_hed_neck_grad_vector" or "bat_hed_neck_grad_scalar" => 10,
            "bat_hed_face_grad_vector" or "bat_hed_face_grad_scalar" => 11,
            "bat_hed_tophead_grad_vector" or "bat_hed_tophead_grad_scalar" => 12,
            "bat_hed_teeth_vector" => 20,
            "bat_hed_mask" or "bat_hed_mask_vector" => 30,
            "bat_hed_addn" => 31,
            "bat_hed_addn_colour_vector" or "bat_hed_addn_colour_scalar" => 32,
            "bat_hed_addn_diffuse_blend_scalar" => 33,
            "bat_hed_addn_spec_vector" => 34,
            "blowout_scalar" => 40,
            "bat_hed_spwr_scalar" => 41,
            "bat_hed_tmis_scalar" or "skinlightscattering" => 42,
            _ => 1000
        };

    private static (string Category, string Subcategory) FeaturePlacement(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is "back head" or "head_scaleup") return (FacialStructure, Cranium);
        if (lower.StartsWith("forehead_")) return (Head, HeadShape);
        if (lower.StartsWith("ears_")) return (Head, Ears);
        if (lower.StartsWith("cheek")) return (Head, Cheeks);
        if (lower.StartsWith("neck_")) return (Head, Neck);
        if (lower.StartsWith("eyes_"))
        {
            return lower is "eyes_back" or "eyes_forward" or "eyes_down" or "eyes_up"
                ? (Eyes, Position)
                : (Eyes, Shape);
        }
        if (lower.StartsWith("nose_")) return (Nose, NoseShape);
        if (lower.StartsWith("teeth_")) return (Mouth, Teeth);
        if (lower.StartsWith("mouth_")) return (Mouth, MouthShape);
        if (lower.StartsWith("jaw_chin") || lower == "jaw_doublechin") return (Mouth, Chin);
        if (lower.StartsWith("jaw_")) return (Mouth, Jaw);
        return (Head, HeadShape);
    }

    private static int FeatureSortOrder(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "back head" => 0,
            "head_scaleup" => 1,
            "forehead_in" => 10,
            "forehead_out" => 11,
            "cheeks_back" => 0,
            "cheeks_forward" => 1,
            "cheek_gaunt" => 2,
            "shape_chubby" => 0,
            "neck_thin" => 0,
            "neck_wide" => 1,
            "eyes_back" => 0,
            "eyes_forward" => 1,
            "eyes_down" => 2,
            "eyes_up" => 3,
            "eyes_small" => 0,
            "eyes_big" => 1,
            "eyes_narow" => 10,
            "eyes_narrow" => 20,
            "eyes_wide" => 21,
            "nose_down" => 0,
            "nose_up" => 1,
            "nose_short" => 2,
            "nose_long" => 3,
            "nose_out" => 4,
            "mouth_back" => 0,
            "mouth_forward" => 1,
            "mouth_down" => 2,
            "mouth_up" => 3,
            "mouth_narrow" => 4,
            "mouth_wide" => 5,
            "teeth_back" => 0,
            "teeth_forward" => 1,
            "teeth_down" => 2,
            "teeth_up" => 3,
            "jaw_chindown" => 0,
            "jaw_chinup" => 1,
            "jaw_chinin" => 2,
            "jaw_chinout" => 3,
            "jaw_chinthin" => 4,
            "jaw_doublechin" => 5,
            _ => 1000
        };
    }

    private static string FeatureLabel(string name)
    {
        if (name.Equals("back head", StringComparison.OrdinalIgnoreCase)) return "Rear Profile";
        if (name.Equals("eyes_Narow", StringComparison.Ordinal)) return "Eye Shape Narrow";
        if (name.Equals("eyes_narrow", StringComparison.Ordinal)) return "Eyes Narrow";
        if (name.Equals("eyes_Wide", StringComparison.Ordinal)) return "Eyes Wide";
        if (name.Equals("eyes_Big", StringComparison.OrdinalIgnoreCase)) return "Eyes Large";
        if (name.Equals("eyes_small", StringComparison.OrdinalIgnoreCase)) return "Eyes Small";
        if (name.Equals("head_ScaleUp", StringComparison.OrdinalIgnoreCase)) return "Scale Up";
        if (name.Equals("shape_chubby", StringComparison.OrdinalIgnoreCase)) return "Head Full";
        if (name.Equals("neck_wide", StringComparison.OrdinalIgnoreCase)) return "Neck Wide";
        if (name.Equals("neck_Thin", StringComparison.OrdinalIgnoreCase)) return "Neck Thin";
        if (name.Equals("jaw_doublechin", StringComparison.OrdinalIgnoreCase)) return "Double Chin";
        var separator = name.IndexOf('_');
        var leaf = separator < 0 ? name : name[(separator + 1)..];
        var label = WordBoundary().Replace(leaf.Replace('_', ' '), " $1");
        var normalized = string.Join(' ', label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        var lower = name.ToLowerInvariant();
        return lower switch
        {
            _ when lower.StartsWith("forehead_") => $"Forehead {normalized}",
            _ when lower.StartsWith("ears_") => $"Ears {normalized}",
            _ when lower.StartsWith("cheek") => $"Cheeks {normalized}",
            _ when lower.StartsWith("eyes_") => $"Eyes {normalized}",
            _ when lower.StartsWith("nose_") => $"Nose {normalized}",
            _ when lower.StartsWith("mouth_") => $"Mouth {normalized}",
            _ when lower.StartsWith("teeth_") => $"Teeth {normalized}",
            _ when lower.StartsWith("jaw_") && !lower.StartsWith("jaw_chin") => $"Jaw {normalized}",
            _ => normalized
        };
    }

    private static string MaterialLabel(string name, string fallback) => name switch
    {
        "SkinTone" => "Skin Tone",
        "SkinLightScattering" => "Skin Light Scattering",
        "Blowout_Scalar" => "Diffuse Gain",
        "BAT_HED_Diff" => "Diffuse Texture",
        "BAT_HED_Norm" => "Normal Texture",
        "BAT_HED_Spec" => "Specular and Teeth Selector",
        "BAT_HED_Tint" => "Gradient Selector Texture",
        "BAT_HED_Mask" => "Complexion Region Mask",
        "BAT_HED_Addn" => "Complexion Texture",
        "BAT_HED_Mask_Vector" => "Complexion Mask Channels",
        "BAT_HED_Neck_Grad_Vector" => "Neck Gradient Colour",
        "BAT_HED_Neck_Grad_Scalar" => "Neck Gradient Strength",
        "BAT_HED_Face_Grad_Vector" => "Face Gradient Colour",
        "BAT_HED_Face_Grad_Scalar" => "Face Gradient Strength",
        "BAT_HED_TopHead_Grad_Vector" => "Top Head Gradient Colour",
        "BAT_HED_TopHead_Grad_Scalar" => "Top Head Gradient Strength",
        "BAT_HED_Teeth_Vector" => "Teeth Colour",
        "BAT_HED_Addn_Colour_Vector" => "Complexion Colour",
        "BAT_HED_Addn_Colour_Scalar" => "Complexion Strength",
        "BAT_HED_Addn_Diffuse_Blend_Scalar" => "Complexion Diffuse Blend",
        "BAT_HED_Addn_Spec_Vector" => "Complexion Specular Colour",
        "BAT_HED_SPwr_Scalar" => "Specular Power",
        "BAT_HED_Tmis_Scalar" => "Transmission Strength",
        _ => fallback
    };

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex WordBoundary();
}
