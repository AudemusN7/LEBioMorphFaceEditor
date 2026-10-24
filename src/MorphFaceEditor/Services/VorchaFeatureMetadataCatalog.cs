using MorphFaceEditor.Infrastructure;
using System.Text.RegularExpressions;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>Material presentation for native Vorcha faces; there are no morph authoring controls.</summary>
public sealed partial class VorchaFeatureMetadataCatalog : IHeadEditorUiProfile
{
    public const string Head = "head";
    public const string Eyes = "eyes";
    public const string Markings = "markings";
    private const string EyeShape = "eye-shape";
    private const string Surface = "surface";
    private const string Tattoo = "tattoo";

    public IReadOnlyList<EditorCategoryDefinition> Categories { get; } =
    [
        new(Head, Loc.Text("loc_Services_Taxonomy_head_9bb0aacc"), string.Empty,
            [new(Surface, Loc.Text("loc_Services_Taxonomy_surface_026aa612"))]),
        new(Eyes, Loc.Text("loc_Services_Taxonomy_eye_cee37055"), string.Empty, [new(EyeShape, Loc.Text("loc_Services_Taxonomy_shape_cfb25fc2"))]),
        new(Markings, Loc.Text("loc_Services_Taxonomy_markings_86efda29"), Loc.Text("loc_Services_Taxonomy_stripe_tattoo_and_surface_colour_settings_68179072"), [new(Tattoo, Loc.Text("loc_Services_Taxonomy_tattoo_600e952e")), new(Surface, Loc.Text("loc_Services_Taxonomy_surface_026aa612"))])
    ];

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit) =>
        new(feature.Feature.Name, feature.Feature.Name, string.Empty, string.Empty,
            false, 0, 0, 0, 0, false, string.Empty);

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

    private static string Label(string name)
    {
        var trimmed = name.Replace("ALN_HED_", string.Empty, StringComparison.OrdinalIgnoreCase)
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
