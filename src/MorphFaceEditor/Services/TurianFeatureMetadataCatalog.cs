using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

public sealed partial class TurianFeatureMetadataCatalog : IHeadEditorUiProfile
{
    public const string FacialStructure = "facial-structure";
    public const string Head = "head";
    public const string Eyes = "eyes";
    public const string Nose = "nose";
    public const string Mouth = "mouth";
    public const string Markings = "markings";

    private const string Mandibles = "mandibles";
    private const string HeadSpikes = "head-spikes";
    private const string Cheeks = "cheeks";
    private const string Neck = "neck";
    private const string Surface = "surface";
    private const string Brows = "brows";
    private const string Shape = "shape";
    private const string Teeth = "teeth";
    private const string Complexion = "complexion";
    private const string Tattoos = "tattoos";

    public static IReadOnlySet<string> MetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "eyes_Large" };

    public static IReadOnlyDictionary<string, string> FeatureAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, int> FeatureSortOrders =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Every one of the 37 authored TUR targets owns an explicit UI
            // position. Complementary directions remain adjacent by design.
            ["mandible_short"] = 0,
            ["mandible_long"] = 1,
            ["mandible_retract"] = 2,
            ["mandible_extend"] = 3,
            ["mandible_Flare"] = 4,
            ["mandible_Rotate"] = 5,
            ["mandible_Thick"] = 6,
            ["head_ScaleUp"] = 10,
            ["headSpikes_Short"] = 11,
            ["headSpikes_Long"] = 12,
            ["cheeks_Down"] = 20,
            ["cheeks_Up"] = 21,
            ["neck_Thin"] = 30,
            ["brow_Back"] = 40,
            ["brow_Forward"] = 41,
            ["brow_Down"] = 42,
            ["brow_Up"] = 43,
            ["eyes_Back"] = 50,
            ["eyes_Forward"] = 51,
            ["eyes_small"] = 60,
            ["eyes_Big"] = 61,
            ["eyes_narrow"] = 62,
            ["eyes_Wide"] = 63,
            ["nose_Down"] = 70,
            ["nose_Up"] = 71,
            ["nose_In"] = 72,
            ["nose_Out"] = 73,
            ["nose_Short"] = 74,
            ["nose_Long"] = 75,
            ["nose_Narrow"] = 76,
            ["nose_Wide"] = 77,
            ["mouth_Back"] = 80,
            ["mouth_Forward"] = 81,
            ["mouth_Down"] = 82,
            ["mouth_Up"] = 83,
            ["mouth_Narrow"] = 84,
            ["mouth_Wide"] = 85
        };

    public IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
    [
        new(FacialStructure, "Facial Structure", "Turian mandible and head-spike proportions.",
            [new(HeadSpikes, "HEAD SPIKES"), new(Mandibles, "MANDIBLES")]),
        new(Head, "Head", "Cheek, neck, skin-colour, tint, and surface controls.",
            [new(Cheeks, "CHEEKS"), new(Neck, "NECK"), new(Surface, "SURFACE")]),
        new(Eyes, "Eyes", "Brow, eye-position, eye-shape, iris, lens, and sclera controls.",
            [new(Shape, "SHAPE"), new(Brows, "BROWS")]),
        new(Nose, "Nose", "Nose position, length, and width controls.", [new(Shape, "SHAPE")]),
        new(Mouth, "Mouth", "Mouth position, width, and teeth controls.", [new(Shape, "SHAPE"), new(Teeth, "TEETH")]),
        new(Markings, "Markings", "Complexion additions and channel-selected facial tattoos.",
            [new(Complexion, "COMPLEXION"), new(Tattoos, "TATTOOS")])
    ];

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var (category, subcategory) = FeaturePlacement(feature.Feature.Name);
        return new MorphFeatureMetadata(
            feature.Feature.Name,
            FeatureLabel(feature.Feature.Name),
            category,
            subcategory,
            !MetadataOnlyFeatures.Contains(feature.Feature.Name),
            FeatureSortOrder(feature.Feature.Name),
            0,
            1,
            0.01f,
            sessionCanEdit && feature.IsResolved,
            feature.Kind == MorphFeatureResolutionKind.MetadataOnly
                ? $"{feature.Feature.Name} · preserved front-end metadata with no TUR target delta."
                : $"{feature.Feature.Name} · {feature.ResolutionNote ?? "resolved Turian morph target."}");
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) => definition with
    {
        Label = MaterialLabel(definition.Name, definition.Label),
        Group = GetMaterialCategory(definition.Name, definition.Kind)
    };

    public string GetMaterialCategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        if (name == "mask") return Mouth;
        if (name == "tur_hed_diff_tint_teeth") return Mouth;
        if (name.Contains("eye") || name.Contains("iris") || name.Contains("lens") || name.Contains("cubemap")) return Eyes;
        if (name == "tur_hed_mask") return Markings;
        if (name.Contains("tatt")) return Markings;
        if (name.Contains("addn")) return Markings;
        return Head;
    }

    public string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        return GetMaterialCategory(parameterName, kind) switch
        {
            Eyes => Shape,
            Mouth when name == "tur_hed_diff_tint_teeth" => Teeth,
            Mouth => Shape,
            Markings when name.Contains("tatt") => Tattoos,
            Markings => Complexion,
            _ => Surface
        };
    }

    public int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind)
    {
        var lower = parameterName.ToLowerInvariant();
        return lower switch
        {
            "skintone" or "tur_hed_diff" => 0,
            "tur_hed_norm" => 1,
            "tur_hed_mask" => 2,
            "tur_hed_tint" => 3,
            "mask" => 4,
            "tur_hed_diff_02_colour" or "tur_hed_diffuse02_scalar" => 10,
            "tur_hed_addn" or "tur_hed_addn_mask_vector" => 20,
            "tur_hed_addn_colour" => 21,
            "tur_hed_addn_spec_colour" => 22,
            "tur_hed_tatt_01" or "tur_hed_tatt_01_vector" or "tur_hed_tatt_01_scalar" => 30,
            "tur_hed_tatt_02" or "tur_hed_tatt_02_vector" or "tur_hed_tatt_02_scalar" => 31,
            "tur_hed_tatt_colour" or "tur_hed_tatt" => 32,
            "tur_hed_tatt_spec_colour" => 33,
            "tur_hed_spec_colour" or "tur_hed_spwr_skin_scalar" or "tur_hed_spwr_bone_scalar" => 40,
            "tur_hed_tmis_multiplier" or "skinlightscattering" => 41,
            "tur_eye_diff" => 0,
            "tur_eye_mask" => 1,
            "tur_eye_spec" => 2,
            "tur_eye_iris_norm" => 3,
            "tur_eye_lens_norm" => 4,
            "eye_tint" => 5,
            "eye_pupil" => 6,
            "tur_eye_iris_spec_colour_vector" => 7,
            "tur_eye_lens_spec_colour_vector" => 8,
            "tur_eye_white_spec_colour_vector" => 9,
            "tur_eye_iris_spwr_scalar" => 10,
            "tur_eye_lens_spwr_scalar" => 11,
            "tur_eye_white_spwr_scalar" => 12,
            "eye_diff" => 0,
            "eye_norm" => 1,
            "cubemap_intensity" => 3,
            "eye_specular" => 4,
            "eye_spec_power" => 5,
            _ => 1000
        };
    }

    private static (string Category, string Subcategory) FeaturePlacement(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("mandible_")) return (FacialStructure, Mandibles);
        if (lower.StartsWith("headspikes_") || lower == "head_scaleup") return (FacialStructure, HeadSpikes);
        if (lower.StartsWith("cheeks_")) return (Head, Cheeks);
        if (lower.StartsWith("neck_")) return (Head, Neck);
        if (lower.StartsWith("brow_")) return (Eyes, Brows);
        if (lower.StartsWith("eyes_")) return (Eyes, Shape);
        if (lower.StartsWith("nose_")) return (Nose, Shape);
        if (lower.StartsWith("mouth_")) return (Mouth, Shape);
        return (Head, Surface);
    }

    private static int FeatureSortOrder(string name)
    {
        if (FeatureSortOrders.TryGetValue(name, out var order)) return order;
        if (MetadataOnlyFeatures.Contains(name)) return int.MaxValue;
        throw new InvalidOperationException(
            $"Turian feature '{name}' has no explicit semantic UI position.");
    }

    private static string FeatureLabel(string name)
    {
        if (name.Equals("head_ScaleUp", StringComparison.OrdinalIgnoreCase)) return "Head Scale Up";
        var leaf = name[(name.IndexOf('_') + 1)..];
        var label = WordBoundary().Replace(leaf.Replace('_', ' '), " $1");
        var normalized = string.Join(' ', label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        var lower = name.ToLowerInvariant();
        return lower switch
        {
            _ when lower.StartsWith("mandible_") => $"Mandibles {normalized}",
            _ when lower.StartsWith("headspikes_") => $"Head Spikes {normalized}",
            _ when lower.StartsWith("cheeks_") => $"Cheeks {normalized}",
            _ when lower.StartsWith("neck_") => $"Neck {normalized}",
            _ when lower.StartsWith("brow_") => $"Brows {normalized}",
            _ when lower.StartsWith("eyes_") => $"Eyes {normalized.Replace("Big", "Large", StringComparison.Ordinal)}",
            _ when lower.StartsWith("nose_") => $"Nose {normalized}",
            _ when lower.StartsWith("mouth_") => $"Mouth {normalized}",
            _ => normalized
        };
    }

    private static string MaterialLabel(string name, string fallback) => name switch
    {
        "SkinTone" => "Skin Tone",
        "SkinLightScattering" => "Skin Light Scattering",
        "TUR_HED_Diff" => "Diffuse Texture",
        "TUR_HED_Norm" => "Normal Texture",
        "TUR_HED_Mask" => "Face Region Mask",
        "TUR_HED_Tint" => "Surface Tint Mask",
        "Mask" => "Teeth Opacity Mask",
        "TUR_HED_Diff_02_Colour" => "Secondary Skin Colour",
        "TUR_HED_Diffuse02_Scalar" => "Secondary Skin Blend",
        "TUR_HED_Addn" => "Complexion Texture",
        "TUR_HED_Addn_Mask_Vector" => "Complexion Region Channels",
        "TUR_HED_Addn_Colour" => "Complexion Colour",
        "TUR_HED_Addn_Spec_Colour" => "Complexion Specular Colour",
        "TUR_HED_Tatt" => "Tattoo Pattern Texture",
        "TUR_HED_Tatt_Colour" => "Tattoo Colour",
        "TUR_HED_Tatt_01" => "Tattoo 1 Pattern Channels",
        "TUR_HED_Tatt_01_Vector" => "Tattoo 1 Region Channels",
        "TUR_HED_Tatt_01_Scalar" => "Tattoo 1 Region Alpha",
        "TUR_HED_Tatt_02" => "Tattoo 2 Pattern Channels",
        "TUR_HED_Tatt_02_Vector" => "Tattoo 2 Region Channels",
        "TUR_HED_Tatt_02_Scalar" => "Tattoo 2 Region Alpha",
        "TUR_HED_Tatt_Spec_Colour" => "Tattoo Specular Colour",
        "TUR_HED_Diff_Tint_Bone" => "Bone Plate Tint",
        "TUR_HED_Diff_Tint_Socket" => "Eye Socket Tint",
        "TUR_HED_Diff_Tint_Teeth" => "Teeth Colour",
        "TUR_HED_Spec_Colour" => "Skin Specular Colour",
        "TUR_HED_Spwr_Skin_Scalar" => "Skin Specular Power",
        "TUR_HED_Spwr_Bone_Scalar" => "Bone Specular Power",
        "TUR_HED_TMis_Multiplier" => "Transmission Strength",
        "TUR_EYE_Diff" => "Eye Diffuse Texture",
        "TUR_EYE_Mask" => "Sclera and Iris Mask",
        "TUR_Eye_Spec" => "Eye Specular Texture",
        "TUR_EYE_Iris_Norm" => "Iris Normal Texture",
        "TUR_EYE_Lens_Norm" => "Lens Normal Texture",
        "EYE_Tint" => "Eye Tint",
        "Eye_Pupil" => "Pupil Scale",
        "TUR_EYE_Iris_Spec_Colour_Vector" => "Iris Specular Colour",
        "TUR_EYE_Lens_Spec_Colour_Vector" => "Lens Specular Colour",
        "TUR_EYE_White_Spec_Colour_Vector" => "Sclera Specular Colour",
        "TUR_EYE_Iris_SPwr_Scalar" => "Iris Specular Power",
        "TUR_EYE_Lens_SPwr_Scalar" => "Lens Specular Power",
        "TUR_EYE_White_SPwr_Scalar" => "Sclera Specular Power",
        "EYE_Diff" => "Eye Diffuse Texture",
        "Eye_Norm" => "Eye Normal Texture",
        "CubeMap_Intensity" => "Eye Reflection Strength",
        "Eye_Specular" => "Eye Specular Strength",
        "EYE_Spec_Power" => "Eye Specular Power",
        _ => ExpandTurianMaterialLabel(name, fallback)
    };

    private static string ExpandTurianMaterialLabel(string name, string fallback)
    {
        if (!name.StartsWith("TUR_", StringComparison.OrdinalIgnoreCase)) return fallback;
        var text = name.Replace("TUR_HED_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("TUR_EYE_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Addn", "Complexion", StringComparison.OrdinalIgnoreCase)
            .Replace("TMis", "Transmission", StringComparison.OrdinalIgnoreCase)
            .Replace("Tatt", "Tattoo", StringComparison.OrdinalIgnoreCase)
            .Replace("SPwr", "Specular Power", StringComparison.OrdinalIgnoreCase)
            .Replace("Norm", "Normal", StringComparison.OrdinalIgnoreCase)
            .Replace("Diff", "Diffuse", StringComparison.OrdinalIgnoreCase)
            .Replace("_Scalar", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_Vector", " Channels", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ');
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    [GeneratedRegex("([A-Z]+(?=$|[A-Z][a-z])|[A-Z]?[a-z]+|[0-9]+)")]
    private static partial Regex WordBoundary();
}
