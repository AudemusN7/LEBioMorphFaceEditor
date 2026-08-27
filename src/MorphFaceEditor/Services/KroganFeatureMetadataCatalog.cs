using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>Translates Krogan target/material identifiers into reviewable editor structure.</summary>
public sealed partial class KroganFeatureMetadataCatalog : IHeadEditorUiProfile
{
    public const string FacialStructure = "facial-structure";
    public const string Head = "head";
    public const string Eyes = "eyes";
    public const string Mouth = "mouth";
    public const string Markings = "markings";

    private const string HeadPlates = "head-plates";
    private const string Character = "character";
    private const string Shape = "shape";
    private const string Neck = "neck";
    private const string Nose = "nose";
    private const string Position = "position";
    private const string Jaw = "jaw";
    private const string Surface = "surface";
    private const string Complexion = "complexion";

    public static IReadOnlySet<string> MetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> FeatureAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
    [
        new(FacialStructure, "Facial Structure", "Krogan head-plate proportions and character shapes.",
            [new(Character, "CHARACTER"), new(HeadPlates, "HEAD PLATES")]),
        new(Head, "Head", "Overall head shape, skin colour, gradients, and surface response.",
            [new(Shape, "SHAPE"), new(Nose, "NOSE"), new(Neck, "NECK"), new(Surface, "SURFACE")]),
        new(Eyes, "Eyes", "Eye position, shape, pupil, tint, and surface controls.",
            [new(Shape, "SHAPE"), new(Position, "POSITION")]),
        new(Mouth, "Mouth / Jaw", "Mouth position and jaw controls.",
            [new(Position, "MOUTH"), new(Jaw, "JAW / CHIN")]),
        new(Markings, "Markings", "Complexion texture, mask, and colour controls.",
            [new(Complexion, "COMPLEXION")])
    ];

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var (category, subcategory) = FeaturePlacement(feature.Feature.Name);
        return new MorphFeatureMetadata(
            feature.Feature.Name,
            FeatureLabel(feature.Feature.Name),
            category,
            subcategory,
            true,
            FeatureSortOrder(feature.Feature.Name),
            0,
            1,
            0.01f,
            sessionCanEdit && feature.IsResolved,
            $"{feature.Feature.Name} · {feature.ResolutionNote ?? "resolved Krogan morph target."}");
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) => definition with
    {
        Label = MaterialLabel(definition.Name, definition.Label),
        Group = GetMaterialCategory(definition.Name, definition.Kind)
    };

    public string GetMaterialCategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        if (name.Contains("eye") || name.Contains("pupil")) return Eyes;
        if (name.Contains("teeth") || name.Contains("lips")) return Mouth;
        if (name.Contains("shell") || name.Contains("helmet")) return FacialStructure;
        if (name.Contains("addn") || name.Contains("mask")) return Markings;
        return Head;
    }

    public string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        return GetMaterialCategory(parameterName, kind) switch
        {
            FacialStructure => HeadPlates,
            Eyes => Shape,
            Mouth when name.Contains("teeth") => Jaw,
            Mouth => Position,
            Markings => Complexion,
            _ => Surface
        };
    }

    public int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind)
    {
        var lower = parameterName.ToLowerInvariant();
        return lower switch
        {
            "skintone" or "kro_hed_diff" => 0,
            "kro_hed_norm" => 1,
            "kro_hed_tint" => 2,
            "kro_hed_tnt2" => 3,
            "kro_hed_face_grad_vector" or "kro_hed_face_grad_scalar" => 10,
            "kro_hed_shell_grad_vector" or "kro_hed_shell_grad_scalar" => 11,
            "kro_hed_lips_grad_vector" or "kro_hed_lips_grad_scalar" => 12,
            "kro_hed_addn" or "kro_hed_mask" or "kro_hed_mask_vector" => 20,
            "kro_hed_addn_colour_vector" or "kro_hed_addn_colour_scalar" => 21,
            "kro_hed_teeth_vector" => 30,
            "kro_hed_spec_add" or "kro_hed_spec_scalar" or "kro_hed_spwr_scalar" => 40,
            "kro_hed_shell_spec_add" or "wrex_spec_scalar" => 41,
            "kro_hed_tmis_scalar" or "skinlightscattering" => 50,
            "kro_eye_diff" => 0,
            "kro_eye_mask" => 1,
            "kro_eye_spec" => 2,
            "kro_eye_iris_norm" => 3,
            "kro_eye_lens_norm" => 4,
            "eye_tint" => 5,
            "krogan_pupil" => 6,
            _ => 1000
        };
    }

    private static (string Category, string Subcategory) FeaturePlacement(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("shell_") || lower.StartsWith("spike_")) return (FacialStructure, HeadPlates);
        if (lower == "head_thin") return (FacialStructure, HeadPlates);
        if (lower == "wrex") return (FacialStructure, Character);
        if (lower.StartsWith("eyes_"))
        {
            return lower is "eyes_back" or "eyes_forward" or "eyes_down" or "eyes_up"
                ? (Eyes, Position)
                : (Eyes, Shape);
        }
        if (lower.StartsWith("nose_")) return (Head, Nose);
        if (lower.StartsWith("shape_")) return (Head, Shape);
        if (lower.StartsWith("jaw_")) return (Mouth, Jaw);
        if (lower.StartsWith("mouth_")) return (Mouth, Position);
        return (Head, Neck);
    }

    private static int FeatureSortOrder(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "wrex" => 0,
            "head_thin" => 0,
            "shell_thin" => 1,
            "shell_down" => 2,
            "shell_up" => 3,
            "shell_forwardslant" => 4,
            "spike_erode" => 5,
            "spike_smooth" => 6,
            "spike_flare" => 7,
            "shape_thin" => 0,
            "shape_chubby" => 1,
            "nose_narrow" => 0,
            "eyes_back" => 0,
            "eyes_forward" => 1,
            "eyes_down" => 2,
            "eyes_up" => 3,
            "eyes_small" => 0,
            "eyes_big" => 1,
            "eyes_narrow" => 2,
            "eyes_wide" => 3,
            "mouth_back" => 0,
            "mouth_forward" => 1,
            "mouth_down" => 2,
            "mouth_up" => 3,
            "jaw_chinback" => 0,
            _ => throw new InvalidOperationException(
                $"Krogan target '{name}' has no intentional UI sort position.")
        };
    }

    private static string FeatureLabel(string name)
    {
        if (name.Equals("Wrex", StringComparison.OrdinalIgnoreCase)) return "Wrex";
        if (name.Equals("head_thin", StringComparison.OrdinalIgnoreCase)) return "Outer Plates — Thin";
        if (name.Equals("shell_thin", StringComparison.OrdinalIgnoreCase)) return "Inner Plates — Thin";
        if (name.Equals("shell_Down", StringComparison.OrdinalIgnoreCase)) return "Plate Rim Down";
        if (name.Equals("shell_Up", StringComparison.OrdinalIgnoreCase)) return "Plate Rim Up";
        if (name.Equals("shell_forwardSlant", StringComparison.OrdinalIgnoreCase)) return "Plate Rim Forward Slant";
        if (name.Equals("spike_erode", StringComparison.OrdinalIgnoreCase)) return "Plate Erode";
        if (name.Equals("spike_Smooth", StringComparison.OrdinalIgnoreCase)) return "Spikes Smooth";
        if (name.Equals("spike_flare", StringComparison.OrdinalIgnoreCase)) return "Spikes Flare";
        var separator = name.IndexOf('_');
        var leaf = separator < 0 ? name : name[(separator + 1)..];
        var label = WordBoundary().Replace(leaf.Replace('_', ' '), " $1");
        var normalized = string.Join(' ', label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        var lower = name.ToLowerInvariant();
        return lower switch
        {
            _ when lower.StartsWith("shape_") => $"Face {normalized.Replace("Chubby", "Full", StringComparison.Ordinal)}",
            _ when lower.StartsWith("nose_") => $"Nose {normalized}",
            _ when lower.StartsWith("eyes_") => $"Eyes {normalized.Replace("Big", "Large", StringComparison.Ordinal)}",
            _ when lower.StartsWith("mouth_") => $"Mouth {normalized}",
            _ when lower.StartsWith("jaw_chin") => $"Chin {normalized[4..].TrimStart()}",
            _ => normalized
        };
    }

    private static string MaterialLabel(string name, string fallback) => name switch
    {
        "SkinTone" => "Skin Tone",
        "SkinLightScattering" => "Skin Light Scattering",
        "KRO_HED_Diff" => "Diffuse Texture",
        "KRO_HED_Norm" => "Normal Texture",
        "KRO_HED_Mask" => "Complexion Region Mask",
        "KRO_HED_Addn" => "Complexion Texture",
        "KRO_HED_Tint" => "Surface Selector Texture",
        "KRO_HED_Tnt2" => "Gradient Selector Texture",
        "KRO_HED_Mask_Vector" => "Complexion Mask Channels",
        "KRO_HED_Addn_Colour_Vector" => "Complexion Colour",
        "KRO_HED_Addn_Colour_Scalar" => "Complexion Strength",
        "KRO_HED_Face_Grad_Vector" => "Face Gradient Colour",
        "KRO_HED_Face_Grad_Scalar" => "Face Gradient Strength",
        "KRO_HED_Shell_Grad_Vector" => "Head Plate Gradient Colour",
        "KRO_HED_Shell_Grad_Scalar" => "Head Plate Gradient Strength",
        "KRO_HED_Lips_Grad_Vector" => "Lip Gradient Colour",
        "KRO_HED_Lips_Grad_Scalar" => "Lip Gradient Strength",
        "KRO_HED_Helmet_Tint" => "Head Plate Tint",
        "KRO_HED_Teeth_Vector" => "Teeth Colour",
        "KRO_HED_Shell_Spec_Add" => "Head Plate Specular Colour",
        "KRO_HED_Spec_Add" => "Skin Specular Colour",
        "KRO_HED_Spec_Scalar" => "Specular Strength",
        "KRO_HED_SPwr_Scalar" => "Specular Power",
        "Wrex_Spec_Scalar" => "Wrex Specular Blend",
        "KRO_HED_Tmis_Scalar" => "Transmission Strength",
        "KRO_EYE_Diff" => "Eye Diffuse Texture",
        "KRO_Eye_Mask" => "Eye and Pupil Mask",
        "KRO_Eye_Spec" => "Eye Specular Texture",
        "KRO_EYE_Iris_Norm" => "Iris Normal Texture",
        "KRO_EYE_Lens_Norm" => "Lens Normal Texture",
        "EYE_Tint" => "Eye Tint",
        "Krogan_Pupil" => "Pupil Scale",
        _ => ExpandKroganMaterialLabel(name, fallback)
    };

    private static string ExpandKroganMaterialLabel(string name, string fallback)
    {
        if (!name.StartsWith("KRO_", StringComparison.OrdinalIgnoreCase)) return fallback;
        var text = name.Replace("KRO_HED_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("KRO_EYE_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Addn", "Complexion", StringComparison.OrdinalIgnoreCase)
            .Replace("TMis", "Transmission", StringComparison.OrdinalIgnoreCase)
            .Replace("SPwr", "Specular Power", StringComparison.OrdinalIgnoreCase)
            .Replace("Norm", "Normal", StringComparison.OrdinalIgnoreCase)
            .Replace("Diff", "Diffuse", StringComparison.OrdinalIgnoreCase)
            .Replace("Grad", "Gradient", StringComparison.OrdinalIgnoreCase)
            .Replace("_Scalar", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_Vector", " Colour", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ');
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    [GeneratedRegex("([A-Z]+(?=$|[A-Z][a-z])|[A-Z]?[a-z]+|[0-9]+)")]
    private static partial Regex WordBoundary();
}
