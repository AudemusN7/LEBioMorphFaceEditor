using MorphFaceEditor.Core.Diagnostics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

public enum MorphFaceGame
{
    LE1,
    LE2,
    LE3,
    Unsupported
}

public sealed record LoadedMorphFace(
    MorphFaceDocument Document,
    SkeletalMeshAsset BaseHead,
    SkeletalMeshAsset? HairMesh,
    ResolvedHeadMaterialSet Materials,
    TopologyDiagnosticReport TopologyDiagnostics)
{
    public MorphFaceGame Game { get; init; } = MorphFaceGame.LE1;
    public bool UsesCustomBaseMesh { get; init; }
    public bool IgnoresAuthoredGeometry { get; init; }
    public IReadOnlyList<SkeletalMeshAsset> OtherMeshes { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record LoadedAttachment(
    AssetIdentity Source,
    SkeletalMeshAsset Mesh,
    ResolvedHeadMaterialSet Materials);
