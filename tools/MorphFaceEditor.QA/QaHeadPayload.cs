using MorphFaceEditor.Core.Domain;
using System.Numerics;

namespace MorphFaceEditor.QA;

// Raw payload comparisons remain ordered. Native PCSAV merges retain the career's
// original map order, which can differ from the editor projection used for edits.
internal static class QaHeadPayload
{
    internal static IReadOnlyList<string> GeometryDifferences(MorphFaceMorphData expected, MorphFaceMorphData actual,
        bool compareNameOrder = true, bool equivalentBoneZeroSigns = false)
    {
        var differences = new List<string>();
        IEnumerable<(string Name, int Value)> Features(MorphFaceMorphData data) => data.MorphFeatures
            .Select(value => (value.Name.ToUpperInvariant(), BitConverter.SingleToInt32Bits(value.Offset)));
        IEnumerable<(string Name, int X, int Y, int Z)> Bones(MorphFaceMorphData data) => data.FinalSkeleton
            .Select(value => (value.BoneName.ToUpperInvariant(), BoneBits(value.Translation.X),
                BoneBits(value.Translation.Y), BoneBits(value.Translation.Z)));
        int BoneBits(float value) => equivalentBoneZeroSigns && value == 0f ? 0 : BitConverter.SingleToInt32Bits(value);
        var featuresBefore = Features(expected);
        var featuresAfter = Features(actual);
        var bonesBefore = Bones(expected);
        var bonesAfter = Bones(actual);
        if (!compareNameOrder)
        {
            featuresBefore = featuresBefore.OrderBy(value => value.Name).ThenBy(value => value.Value);
            featuresAfter = featuresAfter.OrderBy(value => value.Name).ThenBy(value => value.Value);
            bonesBefore = bonesBefore.OrderBy(value => value.Name).ThenBy(value => value.X).ThenBy(value => value.Y).ThenBy(value => value.Z);
            bonesAfter = bonesAfter.OrderBy(value => value.Name).ThenBy(value => value.X).ThenBy(value => value.Y).ThenBy(value => value.Z);
        }
        if (!featuresBefore.SequenceEqual(featuresAfter)) differences.Add($"feature names/values/count/order differ ({expected.MorphFeatures.Count} vs {actual.MorphFeatures.Count})");
        var beforeBones = bonesBefore.ToArray();
        var afterBones = bonesAfter.ToArray();
        if (!beforeBones.SequenceEqual(afterBones))
        {
            var index = Enumerable.Range(0, Math.Min(beforeBones.Length, afterBones.Length))
                .FirstOrDefault(i => beforeBones[i] != afterBones[i], -1);
            var detail = index < 0 ? "" : $"; first difference at {index}: {beforeBones[index]} vs {afterBones[index]} (float bits)";
            differences.Add($"bone names/translations/count/order differ ({beforeBones.Length} vs {afterBones.Length}){detail}");
        }
        if (expected.BakedLods.Count != actual.BakedLods.Count) differences.Add($"LOD count differs ({expected.BakedLods.Count} vs {actual.BakedLods.Count})");
        for (var lod = 0; lod < Math.Min(expected.BakedLods.Count, actual.BakedLods.Count); lod++)
        {
            var before = expected.BakedLods[lod];
            var after = actual.BakedLods[lod];
            if (before.Length != after.Length) differences.Add($"LOD {lod} vertex count differs ({before.Length} vs {after.Length})");
            else
            {
                var index = Enumerable.Range(0, before.Length).FirstOrDefault(i => !SameBits(before[i], after[i]), -1);
                if (index >= 0) differences.Add($"LOD {lod} vertex {index} differs: {before[index]} vs {after[index]}");
            }
        }
        return differences;
    }

    private static bool SameBits(Vector3 before, Vector3 after) =>
        BitConverter.SingleToInt32Bits(before.X) == BitConverter.SingleToInt32Bits(after.X) &&
        BitConverter.SingleToInt32Bits(before.Y) == BitConverter.SingleToInt32Bits(after.Y) &&
        BitConverter.SingleToInt32Bits(before.Z) == BitConverter.SingleToInt32Bits(after.Z);

    internal static bool RetainsOrder(IEnumerable<string> original, IEnumerable<string> saved)
    {
        var source = original.ToArray();
        var names = source.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return source.SequenceEqual(saved.Where(names.Contains), StringComparer.OrdinalIgnoreCase);
    }
}
