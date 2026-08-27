using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.DataCompiler;

/// <summary>Projects runtime profile evidence into the smaller contract required by corpus compilation.</summary>
public static class RandomisationProfileDefinitionFactory
{
    public static IReadOnlyList<RandomisationProfileDefinition> CreateDefault(
        MorphFaceProfileRegistry registry,
        MorphTargetCatalog targetCatalog)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(targetCatalog);
        return registry.Profiles.Select(profile =>
        {
            var targets = targetCatalog.Load(profile, profile.Game);
            var available = targets.Select(target =>
                {
                    var name = ObjectName(target.Source.InstancedPath);
                    var resolved = new ResolvedMorphFeature(
                        new MorphFeatureValue(name, 0), target,
                        MorphFeatureResolutionKind.DirectTarget, "Compiler target inventory.");
                    return (Name: name, Metadata: profile.UiProfile.Describe(resolved, true));
                })
                .Where(value => value.Metadata.IsVisible && value.Metadata.IsEditable)
                .Select(value => value.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new RandomisationProfileDefinition(
                profile.Key,
                profile.Game,
                MorphRandomisationPoolRouter.Resolve(profile.Key),
                profile.Matches,
                available,
                profile.FeatureAliases);
        }).ToArray();
    }

    private static string ObjectName(string instancedPath)
    {
        var separator = instancedPath.LastIndexOf('.');
        return separator < 0 ? instancedPath : instancedPath[(separator + 1)..];
    }
}

/// <summary>Reviewed exact-path exclusions remain visible and attributable in generated audit output.</summary>
public static class ReviewedRandomisationExclusions
{
    public static IReadOnlyList<RandomisationDonorExclusion> All { get; } =
    [
        new(
            LegendaryExplorer.MorphFaceGame.LE3,
            "Human Male.LE3_HMM_Morphs.Broke",
            "Known broken HMM donor requires a stored final-skeleton residual and cannot be reconstructed safely from morph sliders alone.")
    ];
}
