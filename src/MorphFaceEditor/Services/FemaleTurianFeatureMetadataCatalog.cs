using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>
/// Uses the shared Turian labels for the authored female target set.
/// </summary>
public sealed class FemaleTurianFeatureMetadataCatalog : IHeadEditorUiProfile
{
    private readonly TurianFeatureMetadataCatalog _turian;

    public FemaleTurianFeatureMetadataCatalog(TurianFeatureMetadataCatalog? turian = null) =>
        _turian = turian ?? MorphFaceMetadataCatalogRegistry.TurianParent;

    public IReadOnlyList<EditorCategoryDefinition> Categories => _turian.Categories.Select(category =>
        category.Key == TurianFeatureMetadataCatalog.FacialStructure
            ? category with { Description = string.Empty, SliderGroups = category.SliderGroups.Where(g => g.Key != "head-spikes").ToArray() }
            : category.Key == TurianFeatureMetadataCatalog.Head
                ? category with
                {
                    Description = string.Empty,
                    SliderGroups = [new("shape", Loc.Text("loc_Services_Taxonomy_shape_cfb25fc2")),
                        .. category.SliderGroups.Where(g => g.Key != "neck")]
                }
                : category).ToArray();

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var eligible = MorphFaceProfileFeatures.FemaleTurianTargetNames.Contains(feature.Feature.Name);
        var metadata = eligible
            ? _turian.Describe(feature, sessionCanEdit)
            : new MorphFeatureMetadata(
                feature.Feature.Name,
                feature.Feature.Name,
                TurianFeatureMetadataCatalog.Head,
                "surface",
                false,
                int.MaxValue,
                0,
                1,
                0.01f,
                false,
                string.Empty);
        if (feature.Feature.Name.Equals("head_ScaleUp", StringComparison.OrdinalIgnoreCase))
            metadata = metadata with { CategoryKey = TurianFeatureMetadataCatalog.Head, SubcategoryKey = "shape" };
        return metadata with
        {
            IsVisible = eligible && metadata.IsVisible,
            IsEditable = eligible && metadata.IsEditable
        };
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) =>
        MetadataTextCatalog.Apply(MorphFaceMetadataCatalogRegistry.FemaleTurian,
            _turian.DescribeMaterial(definition));

    public string GetMaterialCategory(string parameterName, MaterialParameterKind kind) =>
        _turian.GetMaterialCategory(parameterName, kind);

    public string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind) =>
        _turian.GetMaterialSubcategory(parameterName, kind);

    public int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind) =>
        _turian.GetMaterialSortOrder(parameterName, kind);
}
