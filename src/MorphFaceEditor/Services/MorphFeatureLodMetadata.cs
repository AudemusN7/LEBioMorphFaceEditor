using MorphFaceEditor.Core.Deformation;

namespace MorphFaceEditor.Services;

/// <summary>Adds an explicit UI marker when a geometry-only control does not affect every available LOD.</summary>
public static class MorphFeatureLodMetadata
{
    public static MorphFeatureMetadata MarkLodCoverage(
        MorphFeatureMetadata metadata,
        ResolvedMorphFeature feature,
        IReadOnlyCollection<int> availableLods)
    {
        if (feature.Target is null || feature.Target.BoneOffsets.Count > 0 || availableLods.Count < 2)
        {
            return metadata;
        }

        var affectedLods = feature.Target.Lods
            .Where(lod => lod.Vertices.Count > 0 && availableLods.Contains(lod.LodIndex))
            .Select(lod => lod.LodIndex)
            .Distinct()
            .Order()
            .ToArray();
        // Broad LOD0/1 coverage is communicated by disabling the controls on
        // targetless LOD2; only a genuinely single-LOD control needs a label.
        if (affectedLods.Length != 1)
        {
            return metadata;
        }

        var lodList = string.Join('/', affectedLods);
        var marker = $"LOD {lodList} ONLY";
        return metadata with
        {
            Label = metadata.Label.Contains(marker, StringComparison.OrdinalIgnoreCase)
                ? metadata.Label
                : $"{metadata.Label} — {marker}",
            Description = $"{metadata.Description} Geometry from this control is present only in LOD {lodList}."
        };
    }
}
