using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Services;
using System.Windows.Input;

namespace MorphFaceEditor.ViewModels;

/// <summary>Identifies the morph and material controls affected by one randomisation command.</summary>
public sealed record EditorRandomisationScope(
    IReadOnlyList<MorphFeatureEditorViewModel> MorphFeatures,
    IReadOnlyList<MaterialScalarEditorViewModel> Scalars,
    IReadOnlyList<MaterialVectorEditorViewModel> Vectors,
    IReadOnlyList<MaterialTextureEditorViewModel> Textures)
{
    public bool HasValues => MorphFeatures.Count + Scalars.Count + Vectors.Count + Textures.Count > 0;
}

public sealed record EditorSliderGroupViewModel(
    string Key,
    string Label,
    IReadOnlyList<MorphFeatureEditorViewModel> MorphFeatures,
    IReadOnlyList<MaterialScalarEditorViewModel> Scalars,
    ICommand? RandomiseCommand,
    RandomisationInclusionViewModel? Inclusion)
{
    public bool HasRandomisableValues => RandomiseCommand is not null;
}

public sealed record EditorVectorGroupViewModel(
    string Key,
    string Label,
    IReadOnlyList<MaterialVectorEditorViewModel> Values,
    ICommand? RandomiseCommand,
    RandomisationInclusionViewModel? Inclusion)
{
    public bool HasRandomisableValues => RandomiseCommand is not null;
}

/// <summary>Stores per-subcategory opt-ins for global randomisation without persisting UI state.</summary>
public sealed class RandomisationInclusionState
{
    private readonly Dictionary<string, bool> _values = new(StringComparer.OrdinalIgnoreCase);

    public bool IsIncluded(string key) => !_values.TryGetValue(key, out var included) || included;

    public bool SetIncluded(string key, bool included)
    {
        if (IsIncluded(key) == included) return false;
        _values[key] = included;
        return true;
    }
}

public sealed class RandomisationInclusionViewModel(
    string key,
    RandomisationInclusionState state,
    Action changed) : ObservableObject
{
    public bool IsIncluded
    {
        get => state.IsIncluded(key);
        set
        {
            if (!state.SetIncluded(key, value)) return;
            OnPropertyChanged();
            changed();
        }
    }
}

public sealed class EditorFeatureCategoryViewModel
{
    public EditorFeatureCategoryViewModel(
        EditorCategoryDefinition definition,
        IReadOnlyList<MorphFeatureEditorViewModel> morphFeatures,
        IReadOnlyList<MaterialScalarEditorViewModel> scalars,
        IReadOnlyList<MaterialVectorEditorViewModel> colours,
        IReadOnlyList<MaterialTextureEditorViewModel> textures,
        IHeadEditorUiProfile uiProfile,
        Func<EditorRandomisationScope, ICommand?>? createRandomiseCommand = null,
        RandomisationInclusionState? inclusionState = null,
        Action? inclusionChanged = null)
    {
        Key = definition.Key;
        Label = definition.Label;
        Description = definition.Description;
        var sliderGroups = definition.SliderGroups
            .Select(group =>
            {
                var groupMorphs = morphFeatures
                    .Where(value => value.SubcategoryKey == group.Key)
                    .OrderBy(value => value.SortOrder)
                    .ThenBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var groupScalars = scalars
                    .Where(value => uiProfile.GetMaterialSubcategory(value.Name, MaterialParameterKind.Scalar) == group.Key)
                    .OrderBy(value => uiProfile.GetMaterialSortOrder(value.Name, MaterialParameterKind.Scalar))
                    .ThenBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var command = createRandomiseCommand?.Invoke(new EditorRandomisationScope(
                    groupMorphs, groupScalars, [], []));
                return new EditorSliderGroupViewModel(
                    group.Key,
                    group.Label,
                    groupMorphs,
                    groupScalars,
                    command,
                    CreateInclusion($"{definition.Key}:values:{group.Key}", command,
                        inclusionState, inclusionChanged));
            })
            .Where(group => group.MorphFeatures.Count > 0 || group.Scalars.Count > 0)
            .ToList();

        var assignedScalarNames = sliderGroups
            .SelectMany(group => group.Scalars)
            .Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remainingScalars = scalars
            .Where(value => !assignedScalarNames.Contains(value.Name))
            .OrderBy(value => uiProfile.GetMaterialSortOrder(value.Name, MaterialParameterKind.Scalar))
            .ThenBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (remainingScalars.Length > 0)
        {
            var command = createRandomiseCommand?.Invoke(new EditorRandomisationScope(
                [], remainingScalars, [], []));
            sliderGroups.Add(new EditorSliderGroupViewModel(
                "surface", "SURFACE", [], remainingScalars, command,
                CreateInclusion($"{definition.Key}:values:surface", command,
                    inclusionState, inclusionChanged)));
        }

        SliderGroups = sliderGroups;
        Colours = colours
            .OrderBy(value => uiProfile.GetMaterialSortOrder(value.Name, MaterialParameterKind.Vector))
            .ThenBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var vectorGroups = definition.SliderGroups.Select(group =>
            {
                var values = Colours.Where(value =>
                        uiProfile.GetMaterialSubcategory(value.Name, MaterialParameterKind.Vector) == group.Key)
                    .ToArray();
                var command = createRandomiseCommand?.Invoke(new EditorRandomisationScope([], [], values, []));
                return new EditorVectorGroupViewModel(
                    group.Key, $"{group.Label} COLOURS", values, command,
                    CreateInclusion($"{definition.Key}:colours:{group.Key}", command,
                        inclusionState, inclusionChanged));
            })
            .Where(group => group.Values.Count > 0)
            .ToList();
        var assignedVectorNames = vectorGroups.SelectMany(group => group.Values).Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remainingVectors = Colours.Where(value => !assignedVectorNames.Contains(value.Name)).ToArray();
        if (remainingVectors.Length > 0)
        {
            var command = createRandomiseCommand?.Invoke(new EditorRandomisationScope([], [], remainingVectors, []));
            vectorGroups.Add(new EditorVectorGroupViewModel(
                "colours", "COLOURS", remainingVectors, command,
                CreateInclusion($"{definition.Key}:colours:colours", command,
                    inclusionState, inclusionChanged)));
        }
        ColourGroups = vectorGroups;
        Textures = textures
            .OrderBy(value => uiProfile.GetMaterialSortOrder(value.Name, MaterialParameterKind.Texture))
            .ThenBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ColourRandomiseCommand = createRandomiseCommand?.Invoke(new EditorRandomisationScope(
            [], [], Colours, []));
        TextureRandomiseCommand = createRandomiseCommand?.Invoke(new EditorRandomisationScope(
            [], [], [], Textures));
        TextureInclusion = CreateInclusion($"{definition.Key}:textures", TextureRandomiseCommand,
            inclusionState, inclusionChanged);
    }

    public string Key { get; }
    public string Label { get; }
    public string Description { get; }
    public IReadOnlyList<EditorSliderGroupViewModel> SliderGroups { get; }
    public IReadOnlyList<MaterialVectorEditorViewModel> Colours { get; }
    public IReadOnlyList<EditorVectorGroupViewModel> ColourGroups { get; }
    public IReadOnlyList<MaterialTextureEditorViewModel> Textures { get; }
    public ICommand? ColourRandomiseCommand { get; }
    public ICommand? TextureRandomiseCommand { get; }
    public RandomisationInclusionViewModel? TextureInclusion { get; }
    public bool CanRandomiseColours => ColourRandomiseCommand is not null;
    public bool CanRandomiseTextures => TextureRandomiseCommand is not null;
    public bool HasSliders => SliderGroups.Count > 0;
    public bool HasColours => ColourGroups.Count > 0;
    public bool HasTextures => Textures.Count > 0;
    public int SettingCount => SliderGroups.Sum(group => group.MorphFeatures.Count + group.Scalars.Count) + Colours.Count + Textures.Count;
    public string CountLabel => $"{SettingCount} setting{(SettingCount == 1 ? string.Empty : "s")}";

    private static RandomisationInclusionViewModel? CreateInclusion(
        string key,
        ICommand? command,
        RandomisationInclusionState? state,
        Action? changed) => command is null || state is null
        ? null
        : new RandomisationInclusionViewModel(key, state, changed ?? (() => { }));
}
