using System.Numerics;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

public static class MorphBoneOffsetComposer
{
    public static IReadOnlyList<BoneTranslation> Compose(
        IReadOnlyList<ReferenceBone> referenceSkeleton,
        IReadOnlyList<BoneTranslation> templateBones,
        IReadOnlyList<WeightedMorphTarget> weightedTargets,
        IReadOnlyDictionary<string, Vector3>? additiveOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(referenceSkeleton);
        ArgumentNullException.ThrowIfNull(templateBones);
        ArgumentNullException.ThrowIfNull(weightedTargets);
        var references = referenceSkeleton.ToDictionary(bone => bone.Name, StringComparer.OrdinalIgnoreCase);
        var offsets = weightedTargets
            .SelectMany(weighted => weighted.Target.BoneOffsets.Select(offset =>
                (offset.BoneName, Offset: offset.Offset * weighted.Weight)))
            .GroupBy(item => item.BoneName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(Vector3.Zero, (sum, item) => sum + item.Offset),
                StringComparer.OrdinalIgnoreCase);
        var templateNames = templateBones
            .Select(bone => bone.BoneName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var composed = templateBones.Select(template =>
        {
            if (!references.TryGetValue(template.BoneName, out var reference))
            {
                throw new InvalidDataException($"Final-skeleton bone '{template.BoneName}' does not exist in the reference skeleton.");
            }
            var translation = reference.Position + offsets.GetValueOrDefault(template.BoneName);
            if (additiveOverrides is not null)
            {
                translation += additiveOverrides.GetValueOrDefault(template.BoneName);
            }
            return new BoneTranslation(template.BoneName, translation);
        }).ToList();

        // A target may animate a bone which the source face did not need to
        // serialize. LE adds that bone from its bind local position when the
        // target first contributes; retaining reference-skeleton order keeps
        // the resulting property array deterministic.
        foreach (var reference in referenceSkeleton.Where(reference =>
                     !templateNames.Contains(reference.Name) && offsets.ContainsKey(reference.Name)))
        {
            var translation = reference.Position + offsets[reference.Name];
            if (additiveOverrides is not null)
            {
                translation += additiveOverrides.GetValueOrDefault(reference.Name);
            }
            composed.Add(new BoneTranslation(reference.Name, translation));
        }

        return composed;
    }
}
