using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>
/// Retains the complete Turian control vocabulary as dormant metadata while
/// exposing the shared Turian material authoring surface for Female Turians.
/// </summary>
public sealed class FemaleTurianFeatureMetadataCatalog : IHeadEditorUiProfile
{
    private readonly TurianFeatureMetadataCatalog _turian = new();

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
        return metadata with
        {
            IsVisible = false,
            IsEditable = false,
            Description = $"{feature.Feature.Name} · reserved Female Turian placeholder; no TUF morph target is currently available."
        };
    }

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) =>
        _turian.DescribeMaterial(definition);

    public string GetMaterialCategory(string parameterName, MaterialParameterKind kind) =>
        _turian.GetMaterialCategory(parameterName, kind);

    public string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind) =>
        _turian.GetMaterialSubcategory(parameterName, kind);

    public int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind) =>
        _turian.GetMaterialSortOrder(parameterName, kind);
}
