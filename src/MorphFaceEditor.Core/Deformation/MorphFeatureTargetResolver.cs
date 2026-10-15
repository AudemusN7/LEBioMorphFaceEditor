using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

public enum MorphFeatureResolutionKind
{
    DirectTarget,
    AliasTarget,
    MetadataOnly,
    Unresolved
}

public sealed record ResolvedMorphFeature(
    MorphFeatureValue Feature,
    MorphTargetAsset? Target,
    MorphFeatureResolutionKind Kind,
    string? ResolutionNote)
{
    public bool IsResolved => Kind is not MorphFeatureResolutionKind.Unresolved;
    public bool ContributesGeometry => Target is not null;
}

public sealed record MorphTargetResolution(IReadOnlyList<ResolvedMorphFeature> Features)
{
    public IReadOnlyList<WeightedMorphTarget> WeightedTargets => Features
        .Where(feature => feature.Target is not null && feature.Feature.Offset != 0)
        .Select(feature => new WeightedMorphTarget(
            feature.Feature.Name,
            feature.Target!,
            feature.Feature.Offset))
        .ToArray();

    public IReadOnlyList<string> UnresolvedFeatureNames => Features
        .Where(feature => !feature.IsResolved)
        .Select(feature => feature.Feature.Name)
        .ToArray();
}

public sealed class MorphFeatureTargetResolver
{
    public MorphTargetResolution Resolve(
        IReadOnlyList<MorphFeatureValue> features,
        IReadOnlyList<MorphTargetAsset> targets,
        IReadOnlySet<string>? metadataOnlyFeatures = null,
        string profileName = "Human Male",
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(targets);
        metadataOnlyFeatures ??= MorphFaceProfileFeatures.HumanMaleMetadataOnlyFeatures;
        var byName = targets
            .GroupBy(target => GetObjectName(target.Source.InstancedPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var results = new List<ResolvedMorphFeature>(features.Count);
        foreach (var feature in features)
        {
            if (metadataOnlyFeatures.Contains(feature.Name))
            {
                results.Add(new ResolvedMorphFeature(
                    feature,
                    null,
                    MorphFeatureResolutionKind.MetadataOnly,
                    $"{profileName} front-end metadata; the baked-vertex oracle confirms that it contributes no direct target delta."));
                continue;
            }
            var targetName = aliases?.GetValueOrDefault(feature.Name) ?? feature.Name;
            if (!byName.TryGetValue(targetName, out var candidates))
            {
                results.Add(new ResolvedMorphFeature(
                    feature,
                    null,
                    MorphFeatureResolutionKind.Unresolved,
                    "No same-name profile target."));
                continue;
            }
            if (candidates.Length != 1)
            {
                results.Add(new ResolvedMorphFeature(
                    feature,
                    null,
                    MorphFeatureResolutionKind.Unresolved,
                    $"Target name is ambiguous ({candidates.Length} matches)."));
                continue;
            }
            results.Add(new ResolvedMorphFeature(
                feature,
                candidates[0],
                string.Equals(targetName, feature.Name, StringComparison.OrdinalIgnoreCase)
                    ? MorphFeatureResolutionKind.DirectTarget
                    : MorphFeatureResolutionKind.AliasTarget,
                string.Equals(targetName, feature.Name, StringComparison.OrdinalIgnoreCase)
                    ? "Exact object-name match."
                    : $"Profile alias to '{targetName}'."));
        }
        return new MorphTargetResolution(results);
    }

    private static string GetObjectName(string instancedPath)
    {
        var separator = instancedPath.LastIndexOf('.');
        return separator < 0 ? instancedPath : instancedPath[(separator + 1)..];
    }
}
