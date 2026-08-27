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
    public static IReadOnlySet<string> HumanMaleMetadataOnlyFeatures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Formal",
        "None",
        "Sarge",
        "Slick",
        "eyes_bagOut",
        "eyeShape_droop",
        "cheeks_gaunt",
        "eyeShape_liara",
        "eyeShape_iconic",
        "eyeShape_oldBlk",
        "eyeShape_yngAsn",
        "nose_BottomThin",
        "nose_BottomWide",
        "race_asnOld",
        "race_asnYoung",
        "race_blackOld",
        "race_Blackyng",
        "race_cauOld",
        "race_cauYng",
        "teeth_canineExtend",
        "teeth_Narrow",
        "teeth_Wide"
    };

    public static IReadOnlySet<string> HumanFemaleMetadataOnlyFeatures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Stored by LE1's female character-creator metadata, but absent from
        // HMF_BaseMorphSet. Representative baked-face oracles prove that these
        // values do not contribute a direct target delta.
        "eyes_lashAngle",
        "eyes_lashLength",
        "eyes_bagOut",
        "cheeks_gaunt",
        "Eastwood",
        "eyes_Shape_droop",
        "eyes_Shape_sleepy",
        "eyes_Shape_wide",
        "HAIR_splitSide\"",
        "mouth_cheekMass",
        "neck_apple",
        "None",
        "pupil_Large",
        "pupil_Small",
        "rollins",
        "teeth_canineExtend",
        "teeth_close",
        "teeth_Narrow",
        "teeth_Seperate"
    };

    public static IReadOnlySet<string> Le3HumanMaleMetadataOnlyFeatures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Present with non-zero weights across the 169-face LE3 HMM corpus,
        // absent from its 153-target HMM_BaseMorphSet, and proven inert by
        // exact reconstruction of every stored 2,392-vertex LOD0.
        "cheeks_gaunt",
        "eyes_bagOut",
        "eyes_Large",
        "eyeShape_droop",
        "eyeShape_sleepy",
        "Formal",
        "neck_thick",
        "None",
        "nose_BottomThin",
        "nose_BottomWide",
        "race_asnOld",
        "race_asnYoung",
        "race_cauOld",
        "race_cauYng",
        "Slick",
        "teeth_Narrow"
    };

    public static IReadOnlySet<string> Le3HumanFemaleMetadataOnlyFeatures { get; } =
        new HashSet<string>(HumanFemaleMetadataOnlyFeatures, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> HumanFemaleFeatureAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { };

    public MorphTargetResolution Resolve(
        IReadOnlyList<MorphFeatureValue> features,
        IReadOnlyList<MorphTargetAsset> targets,
        IReadOnlySet<string>? metadataOnlyFeatures = null,
        string profileName = "Human Male",
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(targets);
        metadataOnlyFeatures ??= HumanMaleMetadataOnlyFeatures;
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
