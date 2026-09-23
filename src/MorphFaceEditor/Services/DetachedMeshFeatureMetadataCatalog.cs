using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

/// <summary>
/// Presents the union material surface used by detached meshes. Unlike a PCC
/// workspace, a detached mesh may assign several racial material families at
/// once, so presentation is delegated per parameter rather than per head.
/// </summary>
public sealed class DetachedMeshFeatureMetadataCatalog : IHeadEditorUiProfile
{
    private readonly CustomMaterialWorkspace? _workspace;
    private readonly HumanMaleFeatureMetadataCatalog _humanMale = MorphFaceMetadataCatalogRegistry.HumanMaleParent;
    private readonly HumanFemaleFeatureMetadataCatalog _human = MorphFaceMetadataCatalogRegistry.HumanFemaleParent;
    private readonly AsariFeatureMetadataCatalog _asari = MorphFaceMetadataCatalogRegistry.AsariParent;
    private readonly SalarianFeatureMetadataCatalog _salarian = MorphFaceMetadataCatalogRegistry.SalarianParent;
    private readonly TurianFeatureMetadataCatalog _turian = MorphFaceMetadataCatalogRegistry.TurianParent;
    private readonly FemaleTurianFeatureMetadataCatalog _femaleTurian = MorphFaceMetadataCatalogRegistry.FemaleTurianParent;
    private readonly KroganFeatureMetadataCatalog _krogan = MorphFaceMetadataCatalogRegistry.KroganParent;
    private readonly BatarianFeatureMetadataCatalog _batarian = MorphFaceMetadataCatalogRegistry.BatarianParent;
    private readonly VorchaFeatureMetadataCatalog _vorcha = MorphFaceMetadataCatalogRegistry.VorchaParent;

    public DetachedMeshFeatureMetadataCatalog(CustomMaterialWorkspace? workspace = null)
    {
        _workspace = workspace;
        Categories = MergeCategories(
            _human, _asari, _salarian, _turian, _krogan, _batarian, _vorcha);
    }

    public IReadOnlyList<EditorCategoryDefinition> Categories { get; }

    public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit) =>
        new(feature.Feature.Name, feature.Feature.Name, string.Empty, string.Empty, false, 0, 0, 0, 0, false,
            "Morph controls are unavailable for detached custom meshes.");

    public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition)
    {
        var controlName = definition.Name;
        var parameterName = MaterialParameterControlKey.ParameterName(controlName);
        var sourceDefinition = definition with { Name = parameterName };
        var described = CatalogFor(controlName, definition.Kind).DescribeMaterial(sourceDefinition);
        return described with { Name = controlName };
    }

    public bool IsMaterialVisible(string parameterName, MaterialParameterKind kind) =>
        _human.IsMaterialVisible(parameterName, kind) &&
        !parameterName.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase);

    public string GetMaterialCategory(string parameterName, MaterialParameterKind kind) =>
        CatalogFor(parameterName, kind).GetMaterialCategory(
            MaterialParameterControlKey.ParameterName(parameterName), kind);

    public string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind) =>
        CatalogFor(parameterName, kind).GetMaterialSubcategory(
            MaterialParameterControlKey.ParameterName(parameterName), kind);

    public int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind) =>
        CatalogFor(parameterName, kind).GetMaterialSortOrder(
            MaterialParameterControlKey.ParameterName(parameterName), kind);

    private IHeadEditorUiProfile CatalogFor(string controlName, MaterialParameterKind kind)
    {
        var parameterName = MaterialParameterControlKey.ParameterName(controlName);
        var encodedScope = MaterialParameterControlKey.ScopeKey(controlName);
        if (encodedScope is not null) return CatalogForScope(encodedScope, parameterName);
        return CatalogFor(parameterName, kind, HumanMaterialProfiles.Describe(parameterName, kind));
    }

    private IHeadEditorUiProfile CatalogFor(
        string parameterName,
        MaterialParameterKind kind,
        MaterialParameterDefinition fallback)
    {
        // Shared controls deliberately have one presentation. When assignments
        // are active, the lowest material slot that supports the parameter owns
        // that presentation, matching the workspace's default-value precedence.
        var assignedFamily = _workspace?.Assignments
            .Where(assignment => assignment.Option.Template.Supports(parameterName, kind))
            .Select(assignment => (HeadMaterialFamily?)assignment.Option.Family)
            .FirstOrDefault();
        return CatalogForFamily(assignedFamily ?? fallback.Family, parameterName);
    }

    private IHeadEditorUiProfile CatalogForFamily(HeadMaterialFamily family, string parameterName)
    {
        return family switch
        {
            HeadMaterialFamily.AsariSkin => _asari,
            HeadMaterialFamily.SalarianSkin or HeadMaterialFamily.SalarianEyes => _salarian,
            HeadMaterialFamily.TurianSkin or HeadMaterialFamily.TurianEyes
                when parameterName.StartsWith("TUF_", StringComparison.OrdinalIgnoreCase) => _femaleTurian,
            HeadMaterialFamily.TurianSkin or HeadMaterialFamily.TurianEyes => _turian,
            HeadMaterialFamily.KroganSkin or HeadMaterialFamily.KroganEyes => _krogan,
            HeadMaterialFamily.BatarianSkin => _batarian,
            HeadMaterialFamily.VorchaSkin or HeadMaterialFamily.VorchaEyes => _vorcha,
            HeadMaterialFamily.Unknown => CatalogForPrefix(parameterName),
            _ => _human
        };
    }

    private IHeadEditorUiProfile CatalogForScope(string scopeKey, string parameterName) =>
        scopeKey.ToLowerInvariant() switch
        {
            "asari" => _asari,
            "salarian" => _salarian,
            "female-turian" => _femaleTurian,
            "turian" => _turian,
            "krogan" => _krogan,
            "batarian" => _batarian,
            "vorcha" => _vorcha,
            "human-male" => _humanMale,
            "human-female" => _human,
            "human" => _human,
            _ => CatalogForPrefix(parameterName)
        };

    private IHeadEditorUiProfile CatalogForPrefix(string parameterName)
    {
        if (parameterName.StartsWith("ASA_", StringComparison.OrdinalIgnoreCase)) return _asari;
        if (parameterName.StartsWith("SAL_", StringComparison.OrdinalIgnoreCase)) return _salarian;
        if (parameterName.StartsWith("TUF_", StringComparison.OrdinalIgnoreCase)) return _femaleTurian;
        if (parameterName.StartsWith("TUR_", StringComparison.OrdinalIgnoreCase)) return _turian;
        if (parameterName.StartsWith("KRO_", StringComparison.OrdinalIgnoreCase) ||
            parameterName.StartsWith("Wrex_", StringComparison.OrdinalIgnoreCase)) return _krogan;
        if (parameterName.StartsWith("BAT_", StringComparison.OrdinalIgnoreCase)) return _batarian;
        if (parameterName.StartsWith("ALN_", StringComparison.OrdinalIgnoreCase)) return _vorcha;
        return _human;
    }

    private static IReadOnlyList<EditorCategoryDefinition> MergeCategories(
        params IHeadEditorUiProfile[] profiles)
    {
        var categories = new List<EditorCategoryDefinition>();
        foreach (var categoryGroup in profiles.SelectMany(profile => profile.Categories)
                     .GroupBy(category => category.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = categoryGroup.First();
            var groups = categoryGroup.SelectMany(category => category.SliderGroups)
                .DistinctBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            categories.Add(new EditorCategoryDefinition(
                first.Key,
                first.Label,
                "Material controls available for the assigned custom mesh slots.",
                groups));
        }
        return categories;
    }
}
