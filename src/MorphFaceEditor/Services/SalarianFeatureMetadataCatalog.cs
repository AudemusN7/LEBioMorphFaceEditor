using MorphFaceEditor.Infrastructure;
using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

public sealed partial class SalarianFeatureMetadataCatalog : IHeadEditorUiProfile
{
    public const string FacialStructure = "facial-structure";
    public const string Head = "head";
    public const string Eyes = "eyes";
    public const string Mouth = "mouth";
    public const string Markings = "markings";

    private const string Cheeks = "cheeks";
    private const string CranialRing = "cranial-ring";
    private const string Surface = "surface";
    private const string Position = "position";
    private const string Shape = "shape";
    private const string Lips = "lips";
    private const string Bite = "bite";
    private const string Jaw = "jaw";
    private const string Complexion = "complexion";
    private const string Tattoos = "tattoos";

public IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
    [
        new(FacialStructure, Loc.Text("loc_Services_Taxonomy_facial_structure_812284cf"), Loc.Text("loc_Services_Taxonomy_salarian_cranial_ring_proportions_2d573b0d"),
            [new(CranialRing, Loc.Text("loc_Services_Taxonomy_cranial_ring_b34926d4"))]),
        new(Head, Loc.Text("loc_Services_Taxonomy_head_9bb0aacc"), Loc.Text("loc_Services_Taxonomy_cheek_shape_skin_colour_secondary_colour_surface_1656a493"),
            [new(Cheeks, Loc.Text("loc_Services_Taxonomy_cheeks_daeb3667")), new(Surface, Loc.Text("loc_Services_Taxonomy_surface_026aa612"))]),
        new(Eyes, Loc.Text("loc_Services_Taxonomy_eyes_a39e9d98"), Loc.Text("loc_Services_Taxonomy_eye_position_shape_iris_pupil_and_emissive_contr_90827edd"),
            [new(Shape, Loc.Text("loc_Services_Taxonomy_shape_cfb25fc2")), new(Position, Loc.Text("loc_Services_Taxonomy_position_aad6faa6"))]),
        new(Mouth, Loc.Text("loc_Services_Taxonomy_mouth_jaw_1b80f857"), Loc.Text("loc_Services_Taxonomy_mouth_lip_bite_chin_and_jaw_controls_aa7cb7cc"),
            [new(Shape, Loc.Text("loc_Services_Taxonomy_mouth_7feedf9a")), new(Lips, Loc.Text("loc_Services_Taxonomy_lips_43c004a7")), new(Bite, Loc.Text("loc_Services_Taxonomy_bite_196fedce")), new(Jaw, Loc.Text("loc_Services_Taxonomy_jaw_chin_66b310ff"))]),
        new(Markings, Loc.Text("loc_Services_Taxonomy_markings_86efda29"), Loc.Text("loc_Services_Taxonomy_complexion_additions_and_facial_tattoos_19506d3e"),
            [new(Complexion, Loc.Text("loc_Services_Taxonomy_complexion_0ee9bf54")), new(Tattoos, Loc.Text("loc_Services_Taxonomy_tattoos_b09f0d8b"))])
    ];

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var (category, subcategory) = FeaturePlacement(feature.Feature.Name);
        var metadata = new MorphFeatureMetadata(
            feature.Feature.Name,
            FeatureLabel(feature.Feature.Name),
            category,
            subcategory,
            MorphFaceProfileFeatures.IsControlEligible(MorphFaceMetadataCatalogRegistry.Salarian, Core.Domain.MorphFaceGame.LE1, feature.Feature.Name, feature.Target),
            FeatureSortOrder(feature.Feature.Name),
            0,
            1,
            0.01f,
            sessionCanEdit && feature.IsResolved,
            feature.Kind == MorphFeatureResolutionKind.MetadataOnly
                ? Loc.Text("loc_Services_Taxonomy_preserved_front_end_metadata_with_no_sal_target_20e94d45")
                : string.Empty);
        return MetadataTextCatalog.Apply(MorphFaceMetadataCatalogRegistry.Salarian, metadata);
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) =>
        MetadataTextCatalog.Apply(MorphFaceMetadataCatalogRegistry.Salarian, definition with
    {
        Label = ExpandSalarianMaterialLabel(definition.Name, definition.Label),
        Group = GetMaterialCategory(definition.Name, definition.Kind)
    });

    public string GetMaterialCategory(string parameterName, MaterialParameterKind kind)
    {
        var name = parameterName.ToLowerInvariant();
        if (name.Contains("eye") || name.Contains("iris") || name.Contains("pupil")) return Eyes;
        if (name == "sal_hed_mask") return Markings;
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
            "skintone" or "sal_hed_diff" => 0,
            "sal_hed_norm" => 1,
            "sal_hed_mask" => 2,
            "sal_hed_tint" => 3,
            "sal_hed_diff_02_colour" or "sal_hed_diffuse02_scalar" => 10,
            "sal_hed_addn" or "sal_hed_addn_mask_vector" or "sal_hed_addn_mask_scalar" => 20,
            "sal_hed_addn_colour" or "sal_hed_addn_blend_scalar" => 21,
            "sal_hed_addn_spec_colour" or "sal_hed_addn_spec_scalar" => 22,
            "sal_hed_tatt_01" or "sal_hed_tatt_01_vector" or "sal_hed_tatt_01_scalar" => 30,
            "sal_hed_tatt_02" or "sal_hed_tatt_02_vector" or "sal_hed_tatt_02_scalar" => 31,
            "sal_hed_tatt_colour" or "sal_hed_tatt" => 32,
            "sal_hed_spec_colour" or "sal_hed_spec_scalar" or "sal_hed_specmap" => 40,
            "sal_hed_tmis_colour" => 41,
            "skinlightscattering" => 42,
            "sal_hed_eye_diff" => 0,
            "sal_hed_eye_norm" => 1,
            "sal_hed_eye_spec" => 2,
            "sal_hed_eye_iris_vector" => 3,
            "sal_hed_eye_pupil_vector" => 4,
            "sal_hed_eye_emis" => 5,
            _ => 1000
        };
    }

    private static (string Category, string Subcategory) FeaturePlacement(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("ring_")) return (FacialStructure, CranialRing);
        if (lower.StartsWith("shape_") || lower.StartsWith("face_")) return (Head, Cheeks);
        if (lower.StartsWith("eyes_"))
        {
            return lower is "eyes_back" or "eyes_forward" or "eyes_down" or "eyes_up"
                ? (Eyes, Position)
                : (Eyes, Shape);
        }
        if (lower.StartsWith("jaw_") || lower.StartsWith("mouth_jaw")) return (Mouth, Jaw);
        if (lower.StartsWith("mouth_lips")) return (Mouth, Lips);
        if (lower.Contains("bite")) return (Mouth, Bite);
        if (lower.StartsWith("mouth_")) return (Mouth, Shape);
        return (Head, Surface);
    }

    private static int FeatureSortOrder(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower switch
        {
            // Every complementary pair is deliberately adjacent. Do not
            // replace this profile-owned order with alphabetical or generic
            // direction sorting: that separates the two halves of a control.
            "ring_back" => 0,
            "ring_forward" => 1,
            "ring_small" => 2,
            "ring_large" => 3,

            "face_thin" => 0,
            "shape_skinny" => 1,
            "shape_chubby" => 2,

            "eyes_back" => 0,
            "eyes_forward" => 1,
            "eyes_down" => 2,
            "eyes_up" => 3,
            "eyes_narrow" => 0,
            "eyes_wide" => 1,

            "mouth_back" => 0,
            "mouth_forward" => 1,
            "mouth_down" => 2,
            "mouth_up" => 3,
            "mouth_narrow" => 4,
            "mouth_wide" => 5,

            "mouth_lipsthin" => 0,
            "mouth_lipsfat" => 1,
            "mouth_lipssmall" => 2,
            "mouth_lipslarge" => 3,
            "mouth_lipsforward" => 4,

            "mouth_overbite" => 0,
            "mouth_underbite" => 1,

            "mouth_jawback" => 0,
            "mouth_jawforward" => 1,
            "mouth_jawlower" => 2,
            "mouth_jawraise" => 3,
            "jaw_chindown" => 4,
            "jaw_chinup" => 5,
            "jaw_chinin" => 6,
            "jaw_chinout" => 7,
            "jaw_jowls" => 8,
            "neck_correction" => 9,
            _ => 1000
        };
    }

    private static string FeatureLabel(string name)
    {
        var leaf = name[(name.IndexOf('_') + 1)..];
        var label = WordBoundary().Replace(leaf.Replace('_', ' '), " $1");
        label = string.Join(' ', label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        var normalized = label switch
        {
            "Jowls" => "Jowls",
            "Lips Fat" => "Full",
            "Lips Thin" => "Thin",
            "Lips Forward" => "Forward",
            "Lips Large" => "Large",
            "Lips Small" => "Small",
            "Jaw Back" => "Jaw Back",
            "Jaw Forward" => "Jaw Forward",
            "Jaw Lower" => "Jaw Lower",
            "Jaw Raise" => "Jaw Raise",
            "Over Bite" => "Overbite",
            "Under Bite" => "Underbite",
            "Skinny" => "Slim",
            _ => label
        };
        var lower = name.ToLowerInvariant();
        return lower switch
        {
            _ when lower.StartsWith("ring_") => $"Cranial Ring {normalized}",
            "face_thin" => "Face Thin",
            "shape_skinny" => "Face Slim",
            _ when lower.StartsWith("eyes_") => $"Eyes {normalized}",
            _ when lower.StartsWith("mouth_lips") => $"Lips {normalized}",
            _ when lower.StartsWith("mouth_") && !lower.Contains("jaw") && !lower.Contains("bite") => $"Mouth {normalized}",
            _ => normalized
        };
    }

    private static string ExpandSalarianMaterialLabel(string name, string fallback)
    {
        if (!name.StartsWith("SAL_HED_", StringComparison.OrdinalIgnoreCase)) return fallback;
        var text = name[8..]
            .Replace("Addn", "Complexion", StringComparison.OrdinalIgnoreCase)
            .Replace("TMis", "Transmission", StringComparison.OrdinalIgnoreCase)
            .Replace("Tatt", "Tattoo", StringComparison.OrdinalIgnoreCase)
            .Replace("Norm", "Normal", StringComparison.OrdinalIgnoreCase)
            .Replace("Diff", "Diffuse", StringComparison.OrdinalIgnoreCase)
            .Replace("Emis", "Emissive", StringComparison.OrdinalIgnoreCase)
            .Replace("_Scalar", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_Vector", " Colour", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ');
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    [GeneratedRegex("([A-Z]+(?=$|[A-Z][a-z])|[A-Z]?[a-z]+|[0-9]+)")]
    private static partial Regex WordBoundary();
}
