using MorphFaceEditor.Infrastructure;
using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>Editor taxonomy for the reconstructed Vorcha ALN control surface.</summary>
public sealed partial class VorchaFeatureMetadataCatalog : IHeadEditorUiProfile
{
    public const string Head = "head";
    public const string Eyes = "eyes";
    public const string Markings = "markings";
    private const string Shape = "shape";
    private const string Nose = "nose";
    private const string Mouth = "mouth";
    private const string Spikes = "spikes";
    private const string EyeShape = "eye-shape";
    private const string Surface = "surface";
    private const string Tattoo = "tattoo";

    public static IReadOnlySet<string> MetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VorchaBase" };
    public static IReadOnlyDictionary<string, string> FeatureAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
    [
        new(Head, Loc.Text("loc_Services_Taxonomy_head_9bb0aacc"), Loc.Text("loc_Services_Taxonomy_reconstructed_vorcha_facial_shape_controls_8e9650d1"),
            [new(Shape, Loc.Text("loc_Services_Taxonomy_shape_cfb25fc2")), new(Nose, Loc.Text("loc_Services_Taxonomy_nose_3c3cb8fe")), new(Mouth, Loc.Text("loc_Services_Taxonomy_mouth_jaw_b57e4681")), new(Spikes, Loc.Text("loc_Services_Taxonomy_spikes_fe7e5996"))]),
        new(Eyes, Loc.Text("loc_Services_Taxonomy_eye_cee37055"), Loc.Text("loc_Services_Taxonomy_vorcha_eye_shape_controls_and_eye_material_setti_958690ef"), [new(EyeShape, Loc.Text("loc_Services_Taxonomy_shape_cfb25fc2"))]),
        new(Markings, Loc.Text("loc_Services_Taxonomy_markings_86efda29"), Loc.Text("loc_Services_Taxonomy_stripe_tattoo_and_surface_colour_settings_68179072"), [new(Tattoo, Loc.Text("loc_Services_Taxonomy_tattoo_600e952e")), new(Surface, Loc.Text("loc_Services_Taxonomy_surface_026aa612"))])
    ];

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var (category, group) = Placement(feature.Feature.Name);
        var source = feature.Kind == MorphFeatureResolutionKind.MetadataOnly
            ? Loc.Text("loc_Services_Taxonomy_preserved_inert_le3_metadata_fa7f4e2e")
            : "reconstructed from the six available LE2 baked-face oracles.";
        var metadata = new MorphFeatureMetadata(feature.Feature.Name, Label(feature.Feature.Name), category, group,
            false, Sort(feature.Feature.Name), 0, 1, 0.01f, false,
            $"Hidden dormant reconstruction; {source}");
        return MetadataTextCatalog.Apply(MorphFaceMetadataCatalogRegistry.Vorcha, metadata);
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) =>
        MetadataTextCatalog.Apply(MorphFaceMetadataCatalogRegistry.Vorcha, definition with
    {
        Label = Label(definition.Name), Group = GetMaterialCategory(definition.Name, definition.Kind)
    });

    public string GetMaterialCategory(string parameterName, MaterialParameterKind kind) =>
        IsEyeMaterialParameter(parameterName)
            ? Eyes : parameterName.Contains("Tatt", StringComparison.OrdinalIgnoreCase) || parameterName.Contains("Tattoo", StringComparison.OrdinalIgnoreCase)
                ? Markings : Head;

    public string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind) =>
        GetMaterialCategory(parameterName, kind) switch { Eyes => EyeShape, Markings when parameterName.Contains("Tatt", StringComparison.OrdinalIgnoreCase) || parameterName.Contains("Tattoo", StringComparison.OrdinalIgnoreCase) => Tattoo, _ => Surface };

    public int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind) => parameterName.ToLowerInvariant() switch
    {
        "tur_hed_diff" or "aln_hed_diff" => 0,
        "aln_hed_norm" => 1,
        "aln_hed_tint" => 2,
        "aln_hed_tatt" => 3,
        _ => 100
    };

    private static (string Category, string Group) Placement(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("eyes")) return (Eyes, EyeShape);
        if (lower.Contains("spike")) return (Head, Spikes);
        if (lower.Contains("nose")) return (Head, Nose);
        if (lower.Contains("mouth") || lower.Contains("jaw")) return (Head, Mouth);
        return (Head, Shape);
    }

    private static int Sort(string name) => name.ToLowerInvariant() switch
    {
        var value when value.Contains("brows") => 0, var value when value.Contains("cheeks") => 1,
        var value when value.Contains("jaw") => 2, var value when value.Contains("mouth") => 3,
        var value when value.Contains("nose") => 4, var value when value.Contains("spike") => 5, _ => 10
    };

    private static string Label(string name)
    {
        var trimmed = name.Replace("ALN_HED_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_MDL_LOD0", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ');
        return WordBoundary().Replace(trimmed, " $1").Trim();
    }

    private static bool IsEyeMaterialParameter(string name) => name.ToLowerInvariant() switch
    {
        // LE2's eye diffuse retains an ALN head-style parameter name, while
        // LE3 inherits the Turian cube parameter without an EYE prefix.
        "aln_hed_diff" or "cubemap_intensity" => true,
        _ => name.Contains("EYE", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Eye_", StringComparison.OrdinalIgnoreCase)
    };

    [GeneratedRegex("([A-Z]+(?=$|[A-Z][a-z])|[A-Z]?[a-z]+|[0-9]+)")]
    private static partial Regex WordBoundary();
}
