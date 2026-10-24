using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using MorphFaceEditor.Core.Domain;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace MorphFaceEditor.LegendaryExplorer.Interchange;

/// <summary>Repairs UModel's generated handedness after proving its section/index vertex order.</summary>
internal static class GltfExportTangentRestorer
{
    internal sealed record SourceLod(int LodIndex, Vector3[] Positions, Vector2[] Uvs,
        float[] Handedness, int[] Indices, IReadOnlyList<MeshSectionTopology> Sections);

    internal static void Restore(SkeletalMesh source, string meshName, IReadOnlyList<string> paths)
    {
        var lods = source.LODModels.Select((lod, index) =>
        {
            var gpu = lod.VertexBufferGPUSkin?.VertexData;
            var positions = gpu?.Select(vertex => vertex.Position).ToArray()
                ?? lod.ME1VertexBufferGPUSkin.Select(vertex => vertex.Position).ToArray();
            var uvs = gpu?.Select(vertex => new Vector2(vertex.UV.X, vertex.UV.Y)).ToArray()
                ?? lod.ME1VertexBufferGPUSkin.Select(vertex => new Vector2(vertex.UV.X, vertex.UV.Y)).ToArray();
            var signs = gpu?.Select(vertex => SkeletalMeshTangentBasis.ToRenderTangent(
                    (Vector4)vertex.TangentX, (Vector4)vertex.TangentZ).W).ToArray()
                ?? lod.ME1VertexBufferGPUSkin.Select(vertex => SkeletalMeshTangentBasis.ToRenderTangent(
                    (Vector4)vertex.TangentX, (Vector4)vertex.TangentZ).W).ToArray();
            return new SourceLod(index, positions, uvs, signs,
                lod.IndexBuffer.Select(value => (int)value).ToArray(),
                lod.Sections.Select(section => new MeshSectionTopology(section.MaterialIndex,
                    section.ChunkIndex, checked((int)section.BaseIndex), section.NumTriangles)).ToArray());
        }).ToArray();
        Restore(lods, meshName, paths);
    }

    internal static void Restore(IReadOnlyList<SourceLod> source, string meshName, IReadOnlyList<string> paths)
    {
        try
        {
            var buffers = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var patches = new Dictionary<(string Path, int Offset), float>();
            var lods = source.ToDictionary(lod => lod.LodIndex);
            Require(paths.Count > 0);
            var seen = new HashSet<int>();
            foreach (var path in paths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var lodIndex = name.Equals(meshName, StringComparison.OrdinalIgnoreCase) ? 0 :
                    name.StartsWith(meshName + "_Lod", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(name[(meshName.Length + 4)..], out var parsed) ? parsed : -1;
                Require(seen.Add(lodIndex) && lods.ContainsKey(lodIndex));
                Validate(path, lods[lodIndex], buffers, patches);
            }
            // All LODs and every source/index/UV correspondence are proven before any bytes change.
            foreach (var ((path, offset), sign) in patches)
                BinaryPrimitives.WriteSingleLittleEndian(buffers[path].AsSpan(offset, 4), sign);
            foreach (var (path, bytes) in buffers)
                File.WriteAllBytes(path, bytes);
        }
        catch (Exception exception) when (UserFacingMessage.FromException(exception) is null)
        {
            throw UserFacingMessage.CreateError("loc_MeshExport_UnverifiedGltfTangents",
                $"Generated glTF tangent verification failed: {exception.Message}");
        }
    }

    private static void Validate(string path, SourceLod source,
        Dictionary<string, byte[]> buffers, Dictionary<(string Path, int Offset), float> patches)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var meshes = root.GetProperty("meshes");
        Require(meshes.GetArrayLength() == 1);
        var primitives = meshes[0].GetProperty("primitives");
        Require(primitives.GetArrayLength() == source.Sections.Count);
        for (var sectionIndex = 0; sectionIndex < source.Sections.Count; sectionIndex++)
        {
            var section = source.Sections[sectionIndex];
            var primitive = primitives[sectionIndex];
            Require(!primitive.TryGetProperty("mode", out var mode) || mode.GetInt32() == 4);
            var attributes = primitive.GetProperty("attributes");
            var positions = Accessor(attributes.GetProperty("POSITION").GetInt32(), "VEC3", 5126, 12);
            var uvs = Accessor(attributes.GetProperty("TEXCOORD_0").GetInt32(), "VEC2", 5126, 8);
            var tangents = Accessor(attributes.GetProperty("TANGENT").GetInt32(), "VEC4", 5126, 16);
            var indexAccessor = primitive.GetProperty("indices").GetInt32();
            var indexType = root.GetProperty("accessors")[indexAccessor].GetProperty("componentType").GetInt32();
            Require(indexType is 5123 or 5125);
            var indices = Accessor(indexAccessor, "SCALAR", indexType, indexType == 5123 ? 2 : 4);
            var sourceIndices = source.Indices.Skip(section.BaseIndex)
                .Take(checked(section.TriangleCount * 3)).ToArray();
            Require(sourceIndices.Length == section.TriangleCount * 3 && indices.Count == sourceIndices.Length);
            // UModel assigns local vertices in first-reference order, independently for each section.
            var sourceVertices = sourceIndices.Distinct().ToArray();
            var localIndices = sourceVertices.Select((vertex, index) => (vertex, index))
                .ToDictionary(value => value.vertex, value => value.index);
            Require(positions.Count == sourceVertices.Length && uvs.Count == positions.Count && tangents.Count == positions.Count);
            for (var index = 0; index < sourceIndices.Length; index++)
                Require(indices.Index(index, indexType) == localIndices[sourceIndices[index]]);
            for (var index = 0; index < sourceVertices.Length; index++)
            {
                var vertex = sourceVertices[index];
                Require((uint)vertex < (uint)source.Positions.Length);
                var exportedPosition = new Vector3(positions.Float(index, 0), positions.Float(index, 2), positions.Float(index, 1)) * 100f;
                var exportedUv = new Vector2(uvs.Float(index, 0), uvs.Float(index, 1));
                Require(Vector3.DistanceSquared(exportedPosition, source.Positions[vertex]) <= 0.0002f * 0.0002f &&
                        Vector2.DistanceSquared(exportedUv, source.Uvs[vertex]) <= 0.00001f * 0.00001f);
                // The native reader owns TangentZ.w -> render handedness. Reflecting Y/Z reverses it.
                var sign = -source.Handedness[vertex];
                Require(sign is -1f or 1f);
                var key = (tangents.Path, checked(tangents.Offset + index * tangents.Stride + 12));
                Require(!patches.TryGetValue(key, out var existing) || existing == sign);
                patches[key] = sign;
            }
        }
        return;

        BufferAccessor Accessor(int accessorIndex, string type, int componentType, int elementSize)
        {
            var accessor = root.GetProperty("accessors")[accessorIndex];
            Require(!accessor.TryGetProperty("sparse", out _) && accessor.GetProperty("type").GetString() == type &&
                    accessor.GetProperty("componentType").GetInt32() == componentType &&
                    (!accessor.TryGetProperty("normalized", out var normalized) || !normalized.GetBoolean()));
            var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
            var buffer = root.GetProperty("buffers")[view.GetProperty("buffer").GetInt32()];
            var uri = buffer.GetProperty("uri").GetString()!;
            Require(!Uri.TryCreate(uri, UriKind.Absolute, out _));
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var bufferPath = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(uri)));
            Require(bufferPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!buffers.TryGetValue(bufferPath, out var bytes))
                buffers[bufferPath] = bytes = File.ReadAllBytes(bufferPath);
            Require(bytes.Length == buffer.GetProperty("byteLength").GetInt32());
            var count = accessor.GetProperty("count").GetInt32();
            var viewOffset = OptionalInt(view, "byteOffset");
            var accessorOffset = OptionalInt(accessor, "byteOffset");
            var offset = checked(viewOffset + accessorOffset);
            var stride = view.TryGetProperty("byteStride", out var strideValue) ? strideValue.GetInt32() : elementSize;
            var viewLength = view.GetProperty("byteLength").GetInt32();
            var end = count == 0 ? accessorOffset : checked(accessorOffset + (count - 1) * stride + elementSize);
            Require(count >= 0 && viewOffset >= 0 && accessorOffset >= 0 && stride >= elementSize &&
                    viewLength >= 0 && end <= viewLength && (long)viewOffset + viewLength <= bytes.Length);
            return new BufferAccessor(bufferPath, bytes, offset, stride, count);
        }
    }

    private static int OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetInt32() : 0;

    private static void Require(bool condition)
    {
        if (!condition) throw UserFacingMessage.CreateError("loc_MeshExport_UnverifiedGltfTangents",
            "Generated glTF section, vertex or buffer correspondence could not be verified.");
    }

    private sealed record BufferAccessor(string Path, byte[] Bytes, int Offset, int Stride, int Count)
    {
        internal float Float(int index, int component) =>
            BinaryPrimitives.ReadSingleLittleEndian(Bytes.AsSpan(checked(Offset + index * Stride + component * 4), 4));
        internal int Index(int index, int componentType) => componentType == 5123
            ? BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(checked(Offset + index * Stride), 2))
            : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(checked(Offset + index * Stride), 4)));
    }
}
