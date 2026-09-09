using System.Numerics;

namespace MorphFaceEditor.Core.Domain;

/// <summary>One contiguous range of triangle indices and its authored material slot.</summary>
public sealed record ImportedMeshSection(
    int MaterialIndex,
    string MaterialName,
    int IndexStart,
    int IndexCount);

/// <summary>A detached reference-skeleton joint read from an interchange file.</summary>
public sealed record ImportedMeshBone(
    string Name,
    int ParentIndex,
    Vector3 Position,
    Quaternion Orientation);

/// <summary>
/// Complete, package-independent render data for an imported mesh. Null optional
/// streams mean the source did not provide that data (or it could not be derived
/// safely); an empty bone list means that the mesh is unrigged.
/// </summary>
public sealed record ImportedMeshAsset(
    string SourcePath,
    Vector3[] Positions,
    Vector3[]? Normals,
    Vector4[]? Tangents,
    Vector2[]? TextureCoordinates,
    int[] Indices,
    IReadOnlyList<ImportedMeshSection> Sections,
    IReadOnlyList<ImportedMeshBone> Bones,
    BoneIndex4[]? BoneIndices,
    Vector4[]? BoneWeights,
    int[] SourceVertexIndices,
    bool NormalsWereGenerated = false,
    bool TangentsWereGenerated = false,
    bool TextureCoordinatesAreComplete = true)
{
    public bool HasCompleteTextureCoordinates =>
        TextureCoordinates?.Length == Positions.Length && TextureCoordinatesAreComplete;

    public bool HasRig => Bones.Count > 0 &&
                          BoneIndices?.Length == Positions.Length &&
                          BoneWeights?.Length == Positions.Length &&
                          BoneWeights.Any(value => value != Vector4.Zero);
}
