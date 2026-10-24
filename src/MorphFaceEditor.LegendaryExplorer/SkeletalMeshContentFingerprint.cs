using System.Numerics;
using System.Security.Cryptography;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Exact native geometry/rig evidence, independent of package and material identities.</summary>
internal static class SkeletalMeshContentFingerprint
{
    internal static string? Compute(SkeletalMesh mesh, MEGame game, IReadOnlyList<int[]> materialMaps)
    {
        if (mesh.LODModels is not { Length: > 0 } ||
            mesh.LODModels.Any(lod => lod.NumTexCoords > 1 ||
                lod.VertexBufferGPUSkin is { NumTexCoords: > 1 } or { bUseFullPrecisionUVs: true }) ||
            mesh.ClothingAssets?.Any(index => index != 0) == true)
            return null; // The pinned decoder does not retain additional/full-precision UV streams or cloth content.

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((int)game);
        Vector(mesh.Origin);
        writer.Write(mesh.RotOrigin.Pitch); writer.Write(mesh.RotOrigin.Yaw); writer.Write(mesh.RotOrigin.Roll);
        writer.Write(mesh.Materials?.Length ?? 0); // Slot shape matters, referenced MIC identities do not.
        writer.Write(mesh.SkeletalDepth);
        Array(mesh.RefSkeleton, bone =>
        {
            writer.Write(bone.Name.Instanced); writer.Write(bone.ParentIndex);
            Vector(bone.Position); Quaternion(bone.Orientation);
            writer.Write(bone.Flags); writer.Write(bone.NumChildren);
        });
        Array(mesh.BoneBreakNames, writer.Write);
        writer.Write(mesh.LODModels.Length);
        for (var index = 0; index < mesh.LODModels.Length; index++)
        {
            var lod = mesh.LODModels[index];
            writer.Write(lod.NumVertices); writer.Write(lod.NumTexCoords);
            Array(materialMaps[index], writer.Write);
            Array(lod.IndexBuffer, writer.Write);
            Array(lod.RawPointIndices, writer.Write);
            Array(lod.ActiveBoneIndices, writer.Write);
            Array(lod.RequiredBones, writer.Write);
            Array(lod.Sections, section =>
            {
                writer.Write(section.MaterialIndex); writer.Write(section.ChunkIndex);
                writer.Write(section.BaseIndex); writer.Write(section.NumTriangles);
            });
            Array(lod.Chunks, chunk =>
            {
                writer.Write(chunk.BaseVertexIndex); writer.Write(chunk.NumRigidVertices);
                writer.Write(chunk.NumSoftVertices); writer.Write(chunk.MaxBoneInfluences);
                Array(chunk.BoneMap, writer.Write);
                Array(chunk.RigidVertices, vertex =>
                {
                    Vector(vertex.Position); Normal(vertex.TangentX); Normal(vertex.TangentY); Normal(vertex.TangentZ);
                    Uv(vertex.UV); writer.Write(vertex.Bone);
                });
                Array(chunk.SoftVertices, SoftVertex);
            });
            Array(lod.ME1VertexBufferGPUSkin, SoftVertex);
            writer.Write(lod.VertexBufferGPUSkin is not null);
            if (lod.VertexBufferGPUSkin is { } gpu)
            {
                writer.Write(gpu.NumTexCoords); writer.Write(gpu.bUsePackedPosition);
                Vector(gpu.MeshOrigin); Vector(gpu.MeshExtension);
                Array(gpu.VertexData, vertex =>
                {
                    Vector(vertex.Position); Normal(vertex.TangentX); Normal(vertex.TangentZ);
                    Uv(vertex.UV); Influences(vertex.InfluenceBones); Influences(vertex.InfluenceWeights);
                });
            }
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));

        void Vector(Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        void Quaternion(Quaternion value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W); }
        void Uv(Vector2 value) { writer.Write(value.X); writer.Write(value.Y); }
        void Normal(PackedNormal value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W); }
        void Influences(Influences value) { for (var i = 0; i < 4; i++) writer.Write(value[i]); }
        void SoftVertex(SoftSkinVertex vertex)
        {
            Vector(vertex.Position); Normal(vertex.TangentX); Normal(vertex.TangentY); Normal(vertex.TangentZ);
            Uv(vertex.UV); Influences(vertex.InfluenceBones); Influences(vertex.InfluenceWeights);
        }
        void Array<T>(IReadOnlyList<T>? values, Action<T> write)
        {
            writer.Write(values?.Count ?? 0);
            if (values is not null) foreach (var value in values) write(value);
        }
    }
}
