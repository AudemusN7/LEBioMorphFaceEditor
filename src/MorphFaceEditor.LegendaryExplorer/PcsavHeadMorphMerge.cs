using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Apply only authored changes from MFE's workspace projection to the complete native head.</summary>
internal static class PcsavHeadMorphMerge
{
    internal static HeadMorphPayload ApplyChanges(HeadMorphPayload original, HeadMorphPayload baseline, HeadMorphPayload edited)
    {
        var lods = Enumerable.Range(0, Math.Max(original.MorphData.BakedLods.Count, edited.MorphData.BakedLods.Count))
            .Select(i => (baseline.MorphData.BakedLods.ElementAtOrDefault(i) ?? [])
                .SequenceEqual(edited.MorphData.BakedLods.ElementAtOrDefault(i) ?? [])
                ? original.MorphData.BakedLods.ElementAtOrDefault(i) ?? []
                : edited.MorphData.BakedLods.ElementAtOrDefault(i) ?? []).ToArray();
        var hair = baseline.HairMesh == edited.HairMesh ? original.HairMesh : edited.HairMesh;
        var textures = Merge(original.MaterialData.Textures, baseline.MaterialData.Textures, edited.MaterialData.Textures,
            v => v.Name, (a,b) => (a.TextureReference?.InstancedPath ?? "None") == (b.TextureReference?.InstancedPath ?? "None"));
        if (!string.Equals(hair, original.HairMesh, StringComparison.OrdinalIgnoreCase))
            textures = textures.Where(v => !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(v.Name)).ToArray();
        return new(
            hair,
            baseline.AccessoryMeshes.SequenceEqual(edited.AccessoryMeshes) ? original.AccessoryMeshes : edited.AccessoryMeshes,
            new MorphFaceMorphData(
                Merge(original.MorphData.MorphFeatures, baseline.MorphData.MorphFeatures, edited.MorphData.MorphFeatures,
                    v => v.Name, (a,b) => a.Offset == b.Offset),
                Merge(original.MorphData.FinalSkeleton, baseline.MorphData.FinalSkeleton, edited.MorphData.FinalSkeleton,
                    v => v.BoneName, (a,b) => a.Translation == b.Translation), lods),
            new MorphFaceMaterialData(
                Merge(original.MaterialData.Scalars, baseline.MaterialData.Scalars, edited.MaterialData.Scalars,
                    v => v.Name, (a,b) => a.Value == b.Value),
                Merge(original.MaterialData.Vectors, baseline.MaterialData.Vectors, edited.MaterialData.Vectors,
                    v => v.Name, (a,b) => a.Value == b.Value),
                textures));
    }

    private static IReadOnlyList<T> Merge<T>(IReadOnlyList<T> original, IReadOnlyList<T> baseline, IReadOnlyList<T> edited,
        Func<T,string> key, Func<T,T,bool> equals)
    {
        var before = baseline.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        var after = edited.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        var changes = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Where(name =>
            !before.TryGetValue(name, out var a) || !after.TryGetValue(name, out var b) || !equals(a,b))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<T>();
        foreach (var value in original)
        {
            var name = key(value);
            if (!changes.Remove(name)) result.Add(value);
            else if (after.TryGetValue(name, out var replacement)) result.Add(replacement);
        }
        foreach (var name in changes) if (after.TryGetValue(name, out var value)) result.Add(value);
        return result;
    }
}
