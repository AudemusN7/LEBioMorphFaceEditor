using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>
/// Retains the complete Turian control vocabulary as dormant metadata while
/// exposing the shared Turian material authoring surface for Female Turians.
/// </summary>
public sealed class FemaleTurianFeatureMetadataCatalog : IHeadEditorUiProfile
{
    private readonly TurianFeatureMetadataCatalog _turian;

    public FemaleTurianFeatureMetadataCatalog(TurianFeatureMetadataCatalog? turian = null) =>
        _turian = turian ?? MorphFaceMetadataCatalogRegistry.TurianParent;

    public static IReadOnlySet<string> MetadataOnlyFeatures { get; } =
        TurianFeatureMetadataCatalog.AllFeatureNames;

    public IReadOnlyList<EditorCategoryDefinition> Categories => _turian.Categories;

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var metadata = TurianFeatureMetadataCatalog.AllFeatureNames.Contains(feature.Feature.Name)
            ? _turian.Describe(feature, false)
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
        var described = metadata with
        {
            IsVisible = false,
            IsEditable = false,
            Description = "Reserved Female Turian placeholder; no TUF morph target is currently available."
        };
        return MetadataTextCatalog.Apply(MorphFaceMetadataCatalogRegistry.FemaleTurian, described);
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
