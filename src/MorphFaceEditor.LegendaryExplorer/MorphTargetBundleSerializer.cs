using System.IO.Compression;
using System.Numerics;
using System.Text;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Compact, versioned storage for the morph targets shipped with the editor.</summary>
public static class MorphTargetBundleSerializer
{
    private static readonly byte[] Magic = "MFTB1"u8.ToArray();

    public static void Write(Stream destination, IReadOnlyList<MorphTargetAsset> targets)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(targets);
        using var compressed = new BrotliStream(destination, CompressionLevel.SmallestSize, leaveOpen: true);
        using var writer = new BinaryWriter(compressed, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(targets.Count);
        foreach (var target in targets)
        {
            writer.Write(target.Source.InstancedPath);
            writer.Write(target.Lods.Count);
            foreach (var lod in target.Lods)
            {
                writer.Write(lod.LodIndex);
                writer.Write(lod.BaseMeshVertexCount);
                writer.Write(lod.Vertices.Count);
                foreach (var vertex in lod.Vertices)
                {
                    writer.Write(vertex.SourceIndex);
                    Write(writer, vertex.PositionDelta);
                    Write(writer, vertex.NormalDelta);
                }
            }

            writer.Write(target.BoneOffsets.Count);
            foreach (var offset in target.BoneOffsets)
            {
                writer.Write(offset.BoneName);
                Write(writer, offset.Offset);
            }
        }
    }

    public static IReadOnlyList<MorphTargetAsset> Read(Stream source, string bundleName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleName);
        using var compressed = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(compressed, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException($"Morph-target bundle '{bundleName}' has an unsupported format.");
        }

        var targets = new MorphTargetAsset[ReadCount(reader, "target")];
        for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            var instancedPath = reader.ReadString();
            var lods = new MorphTargetLod[ReadCount(reader, "LOD")];
            for (var lodIndex = 0; lodIndex < lods.Length; lodIndex++)
            {
                var index = reader.ReadInt32();
                var baseMeshVertexCount = reader.ReadInt32();
                var vertices = new MorphVertexDelta[ReadCount(reader, "vertex")];
                for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                {
                    vertices[vertexIndex] = new MorphVertexDelta(
                        reader.ReadInt32(), ReadVector(reader), ReadVector(reader));
                }
                lods[lodIndex] = new MorphTargetLod(index, baseMeshVertexCount, vertices);
            }

            var offsets = new MorphTargetBoneOffset[ReadCount(reader, "bone offset")];
            for (var offsetIndex = 0; offsetIndex < offsets.Length; offsetIndex++)
            {
                offsets[offsetIndex] = new MorphTargetBoneOffset(reader.ReadString(), ReadVector(reader));
            }

            targets[targetIndex] = new MorphTargetAsset(
                new AssetIdentity($"embedded://{bundleName}", instancedPath, 0, "MorphTarget"),
                lods,
                offsets);
        }
        return targets;
    }

    private static int ReadCount(BinaryReader reader, string label)
    {
        var count = reader.ReadInt32();
        return count >= 0 ? count : throw new InvalidDataException($"Invalid {label} count in morph-target bundle.");
    }

    private static void Write(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
