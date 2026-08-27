using MorphFaceEditor.Core.Deformation;

namespace MorphFaceEditor.Services;

/// <summary>
/// LE3 retained several HMM front-end names whose stock targets or metadata no
/// longer describe useful eye controls. Keep that evidence out of the shared
/// LE1/LE2 presentation contract.
/// </summary>
public sealed class Le3HumanMaleFeatureMetadataCatalog : HumanMaleFeatureMetadataCatalog
{
    private static readonly IReadOnlySet<string> HiddenMetadata =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "eyes_Large",
            "eyeShape_droop",
            "eyeShape_sleepy"
        };

    public override MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var metadata = base.Describe(feature, sessionCanEdit);
        if (HiddenMetadata.Contains(feature.Feature.Name))
        {
            return metadata with { IsVisible = false };
        }

        return feature.Feature.Name.ToLowerInvariant() switch
        {
            "pupil_small" => DescribeVestigialPupil(metadata),
            "pupil_large" => DescribeVestigialPupil(metadata),
            _ => metadata
        };
    }

    private static MorphFeatureMetadata DescribeVestigialPupil(
        MorphFeatureMetadata metadata) => metadata with
    {
        Description = $"{metadata.Name} · stock LE3 target retained for package fidelity; it visibly affects the lower jaw rather than pupil size."
    };
}
