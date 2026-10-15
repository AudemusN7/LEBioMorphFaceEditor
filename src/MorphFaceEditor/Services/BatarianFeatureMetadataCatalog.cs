using MorphFaceEditor.Infrastructure;
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

public IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
    [
        new(FacialStructure, Loc.Text("loc_Services_Taxonomy_facial_structure_812284cf"), Loc.Text("loc_Services_Taxonomy_batarian_specific_cranium_and_regional_colour_co_3ef2e9bb"),
            [new(Cranium, Loc.Text("loc_Services_Taxonomy_cranium_5f4bf434")), new(RegionalColour, Loc.Text("loc_Services_Taxonomy_regional_colour_199c8ed3"))]),
        new(Head, Loc.Text("loc_Services_Taxonomy_head_9bb0aacc"), Loc.Text("loc_Services_Taxonomy_overall_shape_forehead_ears_cheeks_neck_width_an_81ec7dba"),
            [new(HeadShape, Loc.Text("loc_Services_Taxonomy_shape_cfb25fc2")), new(Ears, Loc.Text("loc_Services_Taxonomy_ears_903a1aed")), new(Cheeks, Loc.Text("loc_Services_Taxonomy_cheeks_daeb3667")), new(Neck, Loc.Text("loc_Services_Taxonomy_neck_00020b4f")), new(Surface, Loc.Text("loc_Services_Taxonomy_surface_026aa612"))]),
        new(Eyes, Loc.Text("loc_Services_Taxonomy_eyes_a39e9d98"), Loc.Text("loc_Services_Taxonomy_eye_position_and_shape_controls_94ce5554"),
            [new(Shape, Loc.Text("loc_Services_Taxonomy_shape_cfb25fc2")), new(Position, Loc.Text("loc_Services_Taxonomy_position_aad6faa6"))]),
        new(Nose, Loc.Text("loc_Services_Taxonomy_nose_05c5d4db"), Loc.Text("loc_Services_Taxonomy_nose_position_and_profile_controls_cabe6812"),
            [new(NoseShape, Loc.Text("loc_Services_Taxonomy_shape_cfb25fc2"))]),
        new(Mouth, Loc.Text("loc_Services_Taxonomy_mouth_jaw_1b80f857"), Loc.Text("loc_Services_Taxonomy_mouth_teeth_jaw_and_chin_controls_a13bf17b"),
            [new(MouthShape, Loc.Text("loc_Services_Taxonomy_mouth_7feedf9a")), new(Teeth, Loc.Text("loc_Services_Taxonomy_teeth_0afde705")), new(Jaw, Loc.Text("loc_Services_Taxonomy_jaw_296d1292")), new(Chin, Loc.Text("loc_Services_Taxonomy_chin_cfa4ce51"))]),
        new(Markings, Loc.Text("loc_Services_Taxonomy_markings_86efda29"), Loc.Text("loc_Services_Taxonomy_complexion_texture_mask_colour_and_specular_cont_ce2b43f8"),
            [new(Complexion, Loc.Text("loc_Services_Taxonomy_complexion_0ee9bf54"))])
    ];

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var name = feature.Feature.Name;
        var (category, subcategory) = FeaturePlacement(name);
        var visible = MorphFaceProfileFeatures.IsControlEligible(
            MorphFaceMetadataCatalogRegistry.Batarian, Core.Domain.MorphFaceGame.LE1, name, feature.Target);
        var metadata = new MorphFeatureMetadata(
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
            feature.Kind == MorphFeatureResolutionKind.MetadataOnly
                ? Loc.Text("loc_Services_Taxonomy_stored_creator_metadata_with_no_direct_target_de_e78cd33a")
                : string.Empty);
        return MetadataTextCatalog.Apply(MorphFaceMetadataCatalogRegistry.Batarian, metadata);
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) =>
        MetadataTextCatalog.Apply(MorphFaceMetadataCatalogRegistry.Batarian, definition with
    {
        Label = definition.Label,
        Group = GetMaterialCategory(definition.Name, definition.Kind)
    });

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

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex WordBoundary();
}
