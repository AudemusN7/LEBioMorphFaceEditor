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
        new(Head, "Head", "Reconstructed Vorcha facial-shape controls.",
            [new(Shape, "SHAPE"), new(Nose, "NOSE"), new(Mouth, "MOUTH / JAW"), new(Spikes, "SPIKES")]),
        new(Eyes, "Eye", "Vorcha eye-shape controls and eye material settings.", [new(EyeShape, "SHAPE")]),
        new(Markings, "Markings", "Stripe/tattoo and surface-colour settings.", [new(Tattoo, "TATTOO"), new(Surface, "SURFACE")])
    ];

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var (category, group) = Placement(feature.Feature.Name);
        var source = feature.Kind == MorphFeatureResolutionKind.MetadataOnly
            ? "preserved inert LE3 metadata."
            : "reconstructed from the six available LE2 baked-face oracles.";
        return new MorphFeatureMetadata(feature.Feature.Name, Label(feature.Feature.Name), category, group,
            false, Sort(feature.Feature.Name), 0, 1, 0.01f, false,
            $"{feature.Feature.Name} · hidden dormant reconstruction; {source}");
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) => definition with
    {
        Label = MaterialLabel(definition.Name), Group = GetMaterialCategory(definition.Name, definition.Kind)
    };

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

    private static string MaterialLabel(string name) => name.ToLowerInvariant() switch
    {
        "tur_hed_diff" => "Diffuse Texture",
        "aln_hed_norm" => "Normal Texture",
        "aln_hed_tint" => "Region Mask",
        "aln_hed_tatt" => "Tattoo Texture",
        "aln_hed_diff" or "eye_diff" => "Eye Diffuse Texture",
        "eye_norm" => "Eye Normal Texture",
        "skintone" => "Skin Colour",
        "aln_hed_diff_tint_muzzle" => "Muzzle Colour",
        "aln_hed_diff_tint_muzzle2" => "Secondary Muzzle Colour",
        "aln_hed_diff_tint_teeth" => "Teeth / Bone Colour",
        "tattoo_chooser" => "Tattoo Pattern",
        "tattoo_color" => "Tattoo Colour",
        "aln_hed_spec_colour" => "Specular Colour",
        "tmissive" => "Transmission Colour",
        "skinlightscattering" => "Subsurface Scattering Colour",
        "aln_hed_spwr_skin_scalar" => "Skin Specular Power",
        "aln_hed_spwr_muzzle_scalar" => "Muzzle Specular Power",
        "eye_spec" or "eye_specular" => "Eye Specular Strength",
        "eye_spec_power" => "Eye Specular Power",
        "eye_glow_intensity" => "Eye Glow Intensity",
        "eye_tint_iris" or "eye_tint" => "Iris Colour",
        "eye_glow" => "Eye Glow Colour",
        "cubemap_intensity" => "Eye Reflection Strength",
        _ => Label(name)
    };

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
