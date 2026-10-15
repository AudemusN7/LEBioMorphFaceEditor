using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Infrastructure;
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
        if (MorphFaceProfileFeatures.IsUnsupportedLe3FemaleHair(feature.Feature.Name, feature.Target))
        {
            metadata = metadata with
            {
                IsVisible = false,
                Description = Loc.Text("loc_Services_Taxonomy_hidden_stock_le3_target_the_six_nominal_hair_con_087d3ba8")
            };
        }
        return MetadataTextCatalog.Apply(TextArchetype, metadata, TextGame);
    }
}
