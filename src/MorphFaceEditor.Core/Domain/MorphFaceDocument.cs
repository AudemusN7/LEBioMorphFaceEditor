using System.Numerics;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Domain;

public sealed record MorphFeatureValue(string Name, float Offset);

public sealed record BoneTranslation(string BoneName, Vector3 Translation);

/// <summary>
/// Package-detached data owned by a BioMorphFace. It deliberately contains no LEC types.
/// </summary>
public sealed record MorphFaceDocument(
    AssetIdentity Source,
    PackageFingerprint SourceFingerprint,
    AssetIdentity? BaseHeadReference,
    AssetIdentity? HairMeshReference,
    IReadOnlyList<MorphFeatureValue> MorphFeatures,
    IReadOnlyList<BoneTranslation> FinalSkeleton,
    MorphFaceMaterialOverrides MaterialOverrides,
    IReadOnlyList<Vector3[]> BakedLods,
    IReadOnlyList<string> PropertyNames)
{
    /// <summary>
    /// References stored in m_oOtherMeshes, kept distinct from m_oHairMesh.
    /// The editor exposes the first two slots and preserves any additional
    /// corpus-authored entries during a round trip.
    /// </summary>
    public IReadOnlyList<AssetIdentity?> OtherMeshReferences { get; init; } = [];

    public float GetFeatureOffset(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        var matches = MorphFeatures
            .Where(feature => string.Equals(feature.Name, featureName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            0 => throw new KeyNotFoundException($"Morph feature '{featureName}' is not present on {Source.InstancedPath}."),
            1 => matches[0].Offset,
            _ => throw new InvalidDataException($"Morph feature '{featureName}' occurs {matches.Length} times on {Source.InstancedPath}.")
        };
    }
}
