using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.Interchange;

namespace MorphFaceEditor.Tests;

public static class GltfTangentHandednessTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generated glTF restores native handedness across LODs without changing other bytes", RestoresOnlyHandedness),
        new("generated glTF rejects unproven lower LOD correspondence before changing any buffer", RejectsUnprovenMapping),
        new("generated glTF publication rolls back earlier files when a later output is locked", PublicationRollsBack)
    ];

    private static void RestoresOnlyHandedness()
    {
        WithFixture((mesh, paths, buffers, expected) =>
        {
            var json = paths.Select(File.ReadAllBytes).ToArray();
            GltfExportTangentRestorer.Restore(mesh, "Head", paths);
            for (var index = 0; index < paths.Length; index++)
            {
                TestAssert.True(json[index].SequenceEqual(File.ReadAllBytes(paths[index])), "glTF JSON changed.");
                var patched = File.ReadAllBytes(buffers[index].Path);
                var correct = buffers[index].Original.ToArray();
                foreach (var (offset, sign) in expected[index])
                    BinaryPrimitives.WriteSingleLittleEndian(correct.AsSpan(offset, 4), sign);
                TestAssert.True(correct.SequenceEqual(patched), "A non-handedness buffer byte changed or a native sign was lost.");
            }
        });
    }

    private static void RejectsUnprovenMapping()
    {
        foreach (var failure in new[] { "uv", "index", "duplicate-lod" })
        {
            WithFixture((mesh, paths, buffers, _) =>
            {
                if (failure != "duplicate-lod")
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(paths[1]));
                    var root = document.RootElement;
                    var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
                    var accessorIndex = failure == "uv"
                        ? primitive.GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32()
                        : primitive.GetProperty("indices").GetInt32();
                    var accessor = root.GetProperty("accessors")[accessorIndex];
                    var offset = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()]
                        .GetProperty("byteOffset").GetInt32();
                    var corrupt = buffers[1].Original.ToArray();
                    if (failure == "uv") BinaryPrimitives.WriteSingleLittleEndian(corrupt.AsSpan(offset, 4), 0.37f);
                    else BinaryPrimitives.WriteUInt16LittleEndian(corrupt.AsSpan(offset, 2), 2);
                    File.WriteAllBytes(buffers[1].Path, corrupt);
                    buffers[1] = (buffers[1].Path, corrupt);
                }
                try
                {
                    GltfExportTangentRestorer.Restore(mesh, "Head", failure == "duplicate-lod" ? [.. paths, paths[1]] : paths);
                    throw new InvalidOperationException("Unproven generated glTF was accepted.");
                }
                catch (InvalidDataException exception)
                {
                    TestAssert.Equal("loc_MeshExport_UnverifiedGltfTangents", UserFacingMessage.FromException(exception)!.LocalizationKey);
                }
                foreach (var (path, original) in buffers)
                    TestAssert.True(original.SequenceEqual(File.ReadAllBytes(path)), "A failed lower-LOD proof partially patched output.");
            });
        }
    }

    private static void PublicationRollsBack()
    {
        foreach (var existingFirst in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "MFE-GltfPublish-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(directory, "source");
            var output = Path.Combine(directory, "output");
            Directory.CreateDirectory(source); Directory.CreateDirectory(output);
            try
            {
                string[] names = ["Head.bin", "Head.gltf", "Head.gltf.mfe.json"];
                foreach (var name in names) File.WriteAllText(Path.Combine(source, name), "new " + name);
                if (existingFirst) File.WriteAllText(Path.Combine(output, names[0]), "old " + names[0]);
                foreach (var name in names[1..]) File.WriteAllText(Path.Combine(output, name), "old " + name);
                using (var locked = File.Open(Path.Combine(output, names[2]), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        MorphFaceInterchangeService.PublishGltfFiles(names.Select(name => Path.Combine(source, name)).ToArray(), source, output);
                        throw new InvalidOperationException("Publishing over a locked later output succeeded.");
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                TestAssert.Equal(existingFirst, File.Exists(Path.Combine(output, names[0])));
                if (existingFirst) TestAssert.Equal("old " + names[0], File.ReadAllText(Path.Combine(output, names[0])));
                foreach (var name in names[1..]) TestAssert.Equal("old " + name, File.ReadAllText(Path.Combine(output, name)));
                TestAssert.True(!Directory.EnumerateFiles(output).Any(path => path.EndsWith(".tmp") || path.EndsWith(".backup")),
                    "Publication rollback retained a candidate or backup.");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private static void WithFixture(Action<IReadOnlyList<GltfExportTangentRestorer.SourceLod>, string[], (string Path, byte[] Original)[], Dictionary<int, float>[]> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFE-GltfSigns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var lods = Enumerable.Range(0, 2).Select(CreateLod).ToArray();
            var mesh = lods;
            var paths = new string[2];
            var buffers = new (string Path, byte[] Original)[2];
            var expected = new Dictionary<int, float>[2];
            for (var lodIndex = 0; lodIndex < lods.Length; lodIndex++)
            {
                paths[lodIndex] = Path.Combine(directory, lodIndex == 0 ? "Head.gltf" : "Head_Lod1.gltf");
                (buffers[lodIndex], expected[lodIndex]) = WriteFixture(paths[lodIndex], lods[lodIndex]);
            }
            check(mesh, paths, buffers, expected);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static GltfExportTangentRestorer.SourceLod CreateLod(int index)
    {
        Vector3[] positions = [new(1, 2, 3), new(2, 2, 3), new(1, 3, 3), new(1, 2, 3)];
        // Coincident position AND UV, with opposing signs: geometry matching cannot choose the vertex.
        Vector2[] uvs = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.Zero];
        Vector4[] tangents = [new(1, 0, 0, 1), new(1, 0, 0, -1), new(1, 0, 0, 1), new(1, 0, 0, -1)];
        if (index == 1) tangents = tangents.Select(t => t with { W = -t.W }).ToArray();
        return new GltfExportTangentRestorer.SourceLod(index, positions, uvs, tangents.Select(t => t.W).ToArray(),
            [0, 1, 2, 3, 2, 1], [new(0, 0, 0, 1), new(1, 0, 3, 1)]);
    }

    private static ((string Path, byte[] Original), Dictionary<int, float>) WriteFixture(string path, GltfExportTangentRestorer.SourceLod lod)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var views = new List<object>();
        var accessors = new List<object>();
        var primitives = new List<object>();
        var expected = new Dictionary<int, float>();
        foreach (var (section, sectionIndex) in lod.Sections.Select((s, i) => (s, i)))
        {
            var vertices = lod.Indices.Skip(section.BaseIndex).Take(3).Distinct().ToArray();
            var positions = Add("VEC3", 5126, vertices.Length, () =>
            {
                foreach (var vertex in vertices)
                {
                    var p = lod.Positions[vertex] / 100f;
                    writer.Write(p.X); writer.Write(p.Z); writer.Write(p.Y);
                }
            });
            var uv = Add("VEC2", 5126, vertices.Length, () =>
            {
                foreach (var vertex in vertices) { var value = lod.Uvs[vertex]; writer.Write(value.X); writer.Write(value.Y); }
            });
            var tangent = Add("VEC4", 5126, vertices.Length, () =>
            {
                foreach (var vertex in vertices)
                {
                    writer.Write(1f); writer.Write(0f); writer.Write(0f);
                    expected[(int)stream.Position] = -lod.Handedness[vertex];
                    writer.Write(0f);
                }
            });
            var indices = Add("SCALAR", 5123, 3, () => { writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2); });
            primitives.Add(new { attributes = new Dictionary<string, int> { ["POSITION"] = positions, ["TEXCOORD_0"] = uv, ["TANGENT"] = tangent }, indices, material = sectionIndex });
        }
        var bufferPath = Path.ChangeExtension(path, ".bin");
        var bytes = stream.ToArray();
        File.WriteAllBytes(bufferPath, bytes);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0", extras = "retain this metadata" },
            buffers = new[] { new { uri = Path.GetFileName(bufferPath), byteLength = bytes.Length } },
            bufferViews = views, accessors, meshes = new[] { new { primitives } }
        }));
        return ((bufferPath, bytes), expected);

        int Add(string type, int componentType, int count, Action write)
        {
            while (stream.Position % 4 != 0) writer.Write((byte)0xAB);
            var offset = (int)stream.Position;
            write();
            var index = views.Count;
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = (int)stream.Position - offset });
            accessors.Add(new { bufferView = index, componentType, count, type });
            return index;
        }
    }
}
