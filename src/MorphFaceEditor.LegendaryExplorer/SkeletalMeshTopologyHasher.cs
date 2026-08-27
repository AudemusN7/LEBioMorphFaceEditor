using System.Security.Cryptography;
using System.Text;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.LegendaryExplorer;

internal static class SkeletalMeshTopologyHasher
{
    public static string Hash(StaticLODModel lod, SkeletalMesh mesh)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(lod.VertexBufferGPUSkin?.VertexData?.Length ?? lod.ME1VertexBufferGPUSkin?.Length ?? 0);
            WriteArray(writer, lod.IndexBuffer, writer.Write);
            writer.Write(lod.Sections?.Length ?? 0);
            foreach (var section in lod.Sections ?? [])
            {
                writer.Write(section.MaterialIndex);
                writer.Write(section.ChunkIndex);
                writer.Write(section.BaseIndex);
                writer.Write(section.NumTriangles);
            }

            writer.Write(lod.Chunks?.Length ?? 0);
            foreach (var chunk in lod.Chunks ?? [])
            {
                writer.Write(chunk.BaseVertexIndex);
                writer.Write(chunk.NumRigidVertices);
                writer.Write(chunk.NumSoftVertices);
                writer.Write(chunk.MaxBoneInfluences);
                WriteArray(writer, chunk.BoneMap, writer.Write);
            }

            writer.Write(mesh.RefSkeleton?.Length ?? 0);
            foreach (var bone in mesh.RefSkeleton ?? [])
            {
                writer.Write(bone.Name.Instanced);
                writer.Write(bone.ParentIndex);
            }
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void WriteArray<T>(BinaryWriter writer, T[]? values, Action<T> write)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var value in values ?? [])
        {
            write(value);
        }
    }
}
