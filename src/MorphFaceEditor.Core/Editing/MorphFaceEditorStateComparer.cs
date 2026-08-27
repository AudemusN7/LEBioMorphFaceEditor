using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Editing;

/// <summary>
/// Compares only author-controlled BioMorphFace state. Baked LOD0 is derived
/// from the feature/bone state and is therefore deliberately not a second
/// source of truth for dirty tracking.
/// </summary>
public static class MorphFaceEditorStateComparer
{
    public static bool Equals(MorphFaceDocument left, MorphFaceDocument right) =>
        SameReference(left.HairMeshReference, right.HairMeshReference) &&
        SameReferences(left.OtherMeshReferences, right.OtherMeshReferences) &&
        left.MorphFeatures.SequenceEqual(right.MorphFeatures) &&
        left.FinalSkeleton.SequenceEqual(right.FinalSkeleton) &&
        SameMaterials(left.MaterialOverrides, right.MaterialOverrides);

    private static bool SameMaterials(MorphFaceMaterialOverrides left, MorphFaceMaterialOverrides right) =>
        left.Scalars.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.Scalars.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)) &&
        left.Vectors.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.Vectors.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)) &&
        left.Textures.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .Zip(right.Textures.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            .All(pair => string.Equals(pair.First.Name, pair.Second.Name, StringComparison.OrdinalIgnoreCase) &&
                         SameReference(pair.First.TextureReference, pair.Second.TextureReference)) &&
        left.Textures.Count == right.Textures.Count;

    private static bool SameReference(AssetIdentity? left, AssetIdentity? right) =>
        ReferenceEquals(left, right) ||
        left is not null && right is not null &&
        string.Equals(Path.GetFullPath(left.PackagePath), Path.GetFullPath(right.PackagePath), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.InstancedPath, right.InstancedPath, StringComparison.OrdinalIgnoreCase);

    private static bool SameReferences(
        IReadOnlyList<AssetIdentity?> left,
        IReadOnlyList<AssetIdentity?> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => SameReference(pair.First, pair.Second));
}
