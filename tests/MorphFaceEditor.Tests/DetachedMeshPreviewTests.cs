using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Services;

namespace MorphFaceEditor.Tests;

/// <summary>Focused tests for the package-independent imported-mesh boundary.</summary>
public static class DetachedMeshPreviewTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("detached unrigged meshes receive safe zero-weight streams", UnriggedMeshIsRenderable),
        new("detached malformed rigs disable fixed-bake bone editing", MalformedRigIsRejected),
        new("detached valid rigs preserve weights and expose fixed-bake bones", ValidRigEnablesBonePreview)
        ,new("detached glTF preview preserves the decoder's canonical basis", GltfCanonicalBasisIsPreserved)
    ];

    private static void UnriggedMeshIsRenderable()
    {
        var positions = new[]
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)
        };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var tangents = new[] { Vector4.UnitX, Vector4.UnitX, Vector4.UnitX };
        var uvs = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY };
        var indices = new[] { 0, 1, 2 };
        var sections = new[] { new ImportedMeshSection(0, "Authored Skin", 0, 3) };
        var imported = CreateAsset(positions, normals, tangents, uvs, indices, sections);

        var preview = new DetachedMeshPreviewService().Create(imported, DetachedMeshUpAxis.ZUp);
        TestAssert.True(preview.Mesh.Positions.SequenceEqual(positions), "Positions changed at the detached boundary.");
        TestAssert.True(preview.Mesh.Normals.SequenceEqual(normals), "Normals changed at the detached boundary.");
        TestAssert.True(preview.Mesh.RenderData!.Tangents.SequenceEqual(tangents), "Tangents changed at the detached boundary.");
        TestAssert.True(preview.Mesh.RenderData.TextureCoordinates.SequenceEqual(uvs), "UVs changed at the detached boundary.");
        TestAssert.True(preview.Mesh.RenderData.Indices.SequenceEqual(indices), "Indices changed at the detached boundary.");
        TestAssert.Equal(3, preview.Mesh.RenderData.BoneIndices.Count);
        TestAssert.True(preview.Mesh.RenderData.BoneWeights.All(value => value == Vector4.Zero), "Unrigged weights were not zeroed.");
        TestAssert.True(!preview.Editing.CanEditBones && !preview.Editing.CanEditMorphFeatures, "Unrigged mesh exposed an editing capability.");
        TestAssert.Equal(MorphFaceGeometryMode.FixedBake, preview.Editing.GeometryMode);
        TestAssert.True(preview.Mesh.RenderData.MaterialSlots.All(value => value is null), "Detached materials were inferred during Stage B.");
    }

    private static void MalformedRigIsRejected()
    {
        var imported = CreateAsset(
            [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            null,
            null,
            [0, 1, 2],
            [new ImportedMeshSection(0, "Mesh", 0, 3)],
            bones: [new ImportedMeshBone("root", -1, Vector3.Zero, Quaternion.Identity)],
            boneIndices: [new BoneIndex4(3, 0, 0, 0), default, default],
            boneWeights: [new Vector4(1, 0, 0, 0), new Vector4(1, 0, 0, 0), new Vector4(1, 0, 0, 0)]);

        var preview = new DetachedMeshPreviewService().Create(imported);
        TestAssert.True(!preview.Editing.Rig.IsValid && !preview.Editing.CanEditBones, "Malformed rig enabled bone editing.");
        TestAssert.True(preview.Editing.Rig.Issues.Count > 0, "Malformed rig did not report a reason.");
        TestAssert.True(preview.Mesh.RenderData!.BoneWeights.All(value => value == Vector4.Zero), "Malformed rig reached renderer weights.");
        TestAssert.Equal(0, preview.Mesh.Topology.ReferenceSkeleton.Count);
    }

    private static void ValidRigEnablesBonePreview()
    {
        var weights = new[]
        {
            new Vector4(1, 0, 0, 0), new Vector4(0.25f, 0.75f, 0, 0), new Vector4(0, 1, 0, 0)
        };
        var imported = CreateAsset(
            [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [0, 1, 2],
            [new ImportedMeshSection(0, "Mesh", 0, 3)],
            bones:
            [
                new ImportedMeshBone("root", -1, Vector3.Zero, Quaternion.Identity),
                new ImportedMeshBone("jaw", 0, new Vector3(0, 1, 0), Quaternion.Identity)
            ],
            boneIndices:
            [
                new BoneIndex4(0, 0, 0, 0), new BoneIndex4(0, 1, 0, 0), new BoneIndex4(1, 0, 0, 0)
            ],
            boneWeights: weights);

        var preview = new DetachedMeshPreviewService().Create(imported);
        TestAssert.True(preview.Editing.Rig.IsValid && preview.Editing.CanEditBones, "Valid rig did not expose bone editing.");
        TestAssert.True(preview.Mesh.RenderData!.BoneWeights.SequenceEqual(weights), "Valid weights were altered.");
        TestAssert.Equal(imported.Bones.Count, preview.Mesh.Topology.ReferenceSkeleton.Count);
        TestAssert.True(preview.Mesh.RenderData.MaterialSlots.All(value => value is null), "Detached materials were inferred during Stage B.");
        TestAssert.True(!preview.Editing.CanEditMorphFeatures, "Imported mesh unexpectedly exposed morph controls.");
    }

    private static void GltfCanonicalBasisIsPreserved()
    {
        var sourceOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);
        var imported = CreateAsset(
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [0, 1, 2],
            [new ImportedMeshSection(0, "Mesh", 0, 3)],
            bones: [new ImportedMeshBone("root", -1, Vector3.UnitY, sourceOrientation)],
            boneIndices: [default, default, default],
            boneWeights: [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX]);

        var preview = new DetachedMeshPreviewService().Create(imported);
        TestAssert.Equal(DetachedMeshUpAxis.Auto, preview.UpAxis);
        TestAssert.Equal(DetachedMeshUpAxis.ZUp, preview.EffectiveUpAxis);
        TestAssert.True(preview.Source.Positions.SequenceEqual(imported.Positions), "Retained glTF source positions changed.");
        TestAssert.True(preview.Mesh.Positions.SequenceEqual(imported.Positions),
            "Canonical glTF preview positions were rotated a second time.");
        TestAssert.True(preview.Mesh.Topology.ReferenceSkeleton[0].Position == Vector3.UnitY,
            "Canonical glTF preview bone translation was rotated a second time.");
        TestAssert.True(Quaternion.Dot(
                preview.Mesh.Topology.ReferenceSkeleton[0].Orientation,
                sourceOrientation) > 0.99999f,
            "Canonical glTF preview bone orientation changed at the detached boundary.");

        var zUp = new DetachedMeshPreviewService().Create(imported, DetachedMeshUpAxis.ZUp);
        TestAssert.True(zUp.Mesh.Positions.SequenceEqual(imported.Positions), "Explicit Z-up preview changed positions.");
    }

    private static ImportedMeshAsset CreateAsset(
        Vector3[] positions,
        Vector3[]? normals,
        Vector4[]? tangents,
        Vector2[]? uvs,
        int[] indices,
        IReadOnlyList<ImportedMeshSection> sections,
        IReadOnlyList<ImportedMeshBone>? bones = null,
        BoneIndex4[]? boneIndices = null,
        Vector4[]? boneWeights = null) =>
        new(
            "detached.fixture.gltf",
            positions,
            normals,
            tangents,
            uvs,
            indices,
            sections,
            bones ?? [],
            boneIndices,
            boneWeights,
            Enumerable.Range(0, positions.Length).ToArray());
}
