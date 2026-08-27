using System.Numerics;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Domain;

/// <summary>
/// Detached, clipboard-safe geometry authored by a BioMorphFace. Baked LODs
/// travel with the slider and skeleton values so a package paste remains a
/// valid game asset without requiring the destination face to be opened first.
/// </summary>
public sealed record MorphFaceMorphData(
    IReadOnlyList<MorphFeatureValue> MorphFeatures,
    IReadOnlyList<BoneTranslation> FinalSkeleton,
    IReadOnlyList<Vector3[]> BakedLods);

/// <summary>
/// Cross-profile authoring state. Bone values are additive residuals over the
/// profile's own bind pose and morph-target offsets, never absolute positions.
/// </summary>
public sealed record MorphFaceSemanticTransferData(
    IReadOnlyList<MorphFeatureValue> MorphFeatures,
    IReadOnlyList<BoneTranslation> AdditiveBoneOffsets);

/// <summary>Detached values owned by a BioMaterialOverride.</summary>
public sealed record MorphFaceMaterialData(
    IReadOnlyList<ScalarMaterialOverride> Scalars,
    IReadOnlyList<VectorMaterialOverride> Vectors,
    IReadOnlyList<TextureMaterialOverride> Textures);
