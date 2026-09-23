using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

/// <summary>
/// LE3 adds named Jack, Kasumi, and Miranda head targets. Its six nominal
/// HAIR targets are hidden because their authored deltas are near-identical
/// whole-face sag deformations rather than distinct hairline controls.
/// </summary>
public sealed class Le3HumanFemaleFeatureMetadataCatalog : HumanFemaleFeatureMetadataCatalog
{
    protected override MorphFaceGame? TextGame => MorphFaceGame.LE3;

    public override MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit)
    {
        var metadata = base.Describe(feature, sessionCanEdit);
        if (feature.Feature.Name.StartsWith("HAIR_", StringComparison.OrdinalIgnoreCase) &&
            feature.Target?.Lods.Any(lod => lod.LodIndex == 0 && lod.Vertices.Count == 0) == true &&
            feature.Target.Lods.Any(lod => lod.LodIndex > 0 && lod.Vertices.Count > 0))
        {
            metadata = metadata with
            {
                IsVisible = false,
                Description = "Hidden stock LE3 target; the six nominal hair controls contain near-identical whole-face sag deltas rather than useful hair geometry."
            };
        }
        return MetadataTextCatalog.Apply(TextArchetype, metadata, TextGame);
    }
}
