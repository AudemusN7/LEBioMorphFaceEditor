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
        new("glTF and GLB import convert supplied green-up tangent bases to native", ImportsNativeBasis),
        new("legacy MFE glTF tangent compatibility requires proven source and consistent UV evidence", LegacyTangentCompatibility),
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
                var decoded = new MorphFaceInterchangeService().ReadMesh(paths[index]);
                var nativeSigns = mesh[index].Sections.SelectMany(section => mesh[index].Indices
                    .Skip(section.BaseIndex).Take(3).Distinct()).Select(vertex => mesh[index].Handedness[vertex]);
                TestAssert.True(decoded.Tangents!.Select(t => t.W).SequenceEqual(nativeSigns),
                    "Restored glTF did not import back to its original native signs.");
            }
        });
    }

    private static void ImportsNativeBasis()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFE-GltfBasis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var extension in new[] { ".gltf", ".glb" })
            foreach (var mirrored in new[] { false, true })
            {
                var suppliedPath = Path.Combine(directory, "supplied" + extension);
                var generatedPath = Path.Combine(directory, "generated" + extension);
                WriteBasisFixture(suppliedPath, mirrored, suppliedTangents: true);
                WriteBasisFixture(generatedPath, mirrored, suppliedTangents: false);
                var originalBytes = File.ReadAllBytes(suppliedPath);
                var imported = new MorphFaceInterchangeService().ReadMesh(suppliedPath);
                var generated = new MorphFaceInterchangeService().ReadMesh(generatedPath);
                TestAssert.True(originalBytes.SequenceEqual(File.ReadAllBytes(suppliedPath)), "Import changed source file bytes.");
                TestAssert.True(!imported.TangentsWereGenerated && generated.TangentsWereGenerated,
                    "Fixture did not exercise supplied and generated tangent paths.");
                TestAssert.True(imported.Positions.SequenceEqual(generated.Positions) &&
                    imported.TextureCoordinates!.SequenceEqual(generated.TextureCoordinates!), "Import changed geometry or UVs.");
                for (var i = 0; i < 6; i++)
                {
                    var tangent = imported.Tangents![i];
                    var expectedSign = (i < 3 ? -1f : 1f) * (mirrored ? -1f : 1f);
                    TestAssert.Equal(expectedSign, tangent.W);
                    TestAssert.Equal(new Vector3(mirrored ? -1 : 1, 0, 0), new Vector3(tangent.X, tangent.Y, tangent.Z));
                    TestAssert.Equal(Vector3.UnitY, imported.Normals![i]);
                    // The renderer reconstructs B as cross(N, T) * W. ME green-down
                    // therefore points toward +Z on the first UV island and -Z on the second.
                    var bitangent = Vector3.Cross(imported.Normals[i], new Vector3(tangent.X, tangent.Y, tangent.Z)) * tangent.W;
                    TestAssert.Equal(i < 3 ? Vector3.UnitZ : -Vector3.UnitZ, bitangent);
                    TestAssert.Equal(generated.Tangents![i], tangent);
                }
                TestAssert.Equal(new Vector3(mirrored ? -100 : 100, 0, 0), imported.Positions[1]);
                TestAssert.Equal(new Vector2(0, -1), imported.TextureCoordinates![5]);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void LegacyTangentCompatibility()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFE-LegacyGltfBasis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var mirrored in new[] { false, true })
            {
                var currentPath = Path.Combine(directory, $"current-{mirrored}.gltf");
                var legacyPath = Path.Combine(directory, $"legacy-{mirrored}.gltf");
                WriteBasisFixture(currentPath, mirrored, suppliedTangents: true);
                WriteBasisFixture(legacyPath, mirrored, suppliedTangents: true,
                    generator: "UE Viewer (umodel) build 1589", legacySigns: true);
                var current = new MorphFaceInterchangeService().ReadMesh(currentPath);
                var baseline = new MorphFaceInterchangeService().ReadMesh(legacyPath);
                TestAssert.True(!current.Tangents!.Select(t => t.W).SequenceEqual(baseline.Tangents!.Select(t => t.W)),
                    "The legacy fixture did not contain opposite supplied signs.");
                WriteLegacySidecar(legacyPath, baseline.Positions);
                var original = File.ReadAllBytes(legacyPath);
                var sidecar = File.ReadAllBytes(legacyPath + ".mfe.json");
                var restored = new MorphFaceInterchangeService().ReadMesh(legacyPath);
                TestAssert.True(current.Tangents!.Select(t => t.W).SequenceEqual(restored.Tangents!.Select(t => t.W)),
                    "A proven legacy export did not regain the native tangent basis.");
                TestAssert.True(current.Positions.SequenceEqual(restored.Positions) &&
                    current.TextureCoordinates!.SequenceEqual(restored.TextureCoordinates!) &&
                    current.Tangents!.Select(t => new Vector3(t.X, t.Y, t.Z)).SequenceEqual(
                        restored.Tangents!.Select(t => new Vector3(t.X, t.Y, t.Z))),
                    "Legacy compatibility changed authored geometry, UVs or tangent directions.");
                TestAssert.True(original.SequenceEqual(File.ReadAllBytes(legacyPath)) &&
                    sidecar.SequenceEqual(File.ReadAllBytes(legacyPath + ".mfe.json")),
                    "Legacy compatibility modified the source files.");
            }

            foreach (var scenario in new[] { "no-sidecar", "current-marker", "unknown-marker", "stale-geometry",
                         "bad-mapping", "wrong-generator", "degenerate-uv", "mixed-signs", "missing-tangents",
                         "invalid-sign", "malformed-sidecar" })
            {
                var path = Path.Combine(directory, scenario + ".gltf");
                WriteBasisFixture(path, mirrored: false, suppliedTangents: scenario != "missing-tangents",
                    generator: scenario == "wrong-generator" ? "Other exporter" : "UE Viewer (umodel) build 1589",
                    legacySigns: true, mixedSigns: scenario == "mixed-signs", degenerateUv: scenario == "degenerate-uv",
                    invalidSign: scenario == "invalid-sign");
                var before = new MorphFaceInterchangeService().ReadMesh(path);
                if (scenario != "no-sidecar")
                    WriteLegacySidecar(path, before.Positions,
                        marker: scenario switch { "current-marker" => "GltfGreenUp", "unknown-marker" => "Other", _ => null },
                        staleGeometry: scenario == "stale-geometry", badMapping: scenario == "bad-mapping");
                if (scenario == "malformed-sidecar") File.WriteAllText(path + ".mfe.json", "{");
                var after = new MorphFaceInterchangeService().ReadMesh(path);
                TestAssert.Equal(before.TangentsWereGenerated, after.TangentsWereGenerated);
                TestAssert.True((before.Tangents is null && after.Tangents is null) ||
                    (before.Tangents is not null && after.Tangents is not null && before.Tangents.SequenceEqual(after.Tangents)),
                    $"Legacy compatibility changed tangents for {scenario}.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void WriteLegacySidecar(string meshPath, IReadOnlyList<Vector3> positions,
        string? marker = null, bool staleGeometry = false, bool badMapping = false)
    {
        var mapping = Enumerable.Range(0, positions.Count).ToArray();
        if (badMapping) mapping[^1] = mapping[0];
        var bake = positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        if (staleGeometry) bake[0] += 5;
        var sidecar = new Dictionary<string, object>
        {
            ["Version"] = 3,
            ["CoordinateSystem"] = "glTF stream 1, XZY, file order",
            ["ExportedToBase"] = mapping,
            ["Corrections"] = new float[positions.Count * 3],
            ["BaseHeadPath"] = "BIOG_Test.Head",
            ["MorphFeatures"] = Array.Empty<object>(),
            ["FinalSkeleton"] = Array.Empty<object>(),
            ["BakedLods"] = new[] { bake }
        };
        if (marker is not null) sidecar["TangentBasis"] = marker;
        File.WriteAllText(meshPath + ".mfe.json", JsonSerializer.Serialize(sidecar));
    }

    private static void WriteBasisFixture(string path, bool mirrored, bool suppliedTangents,
        string? generator = null, bool legacySigns = false, bool mixedSigns = false, bool degenerateUv = false,
        bool invalidSign = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var views = new List<object>();
        var accessors = new List<object>();
        var position = Add("VEC3", 5126, 6, () =>
        {
            for (var island = 0; island < 2; island++)
                foreach (var p in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY })
                { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
        });
        var normal = Add("VEC3", 5126, 6, () =>
        {
            for (var i = 0; i < 6; i++) { writer.Write(0f); writer.Write(0f); writer.Write(1f); }
        });
        var uv = Add("VEC2", 5126, 6, () =>
        {
            foreach (var value in degenerateUv
                         ? Enumerable.Repeat(Vector2.Zero, 6)
                         : new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.Zero, Vector2.UnitX, -Vector2.UnitY })
            { writer.Write(value.X); writer.Write(value.Y); }
        });
        var attributes = new Dictionary<string, int> { ["POSITION"] = position, ["NORMAL"] = normal, ["TEXCOORD_0"] = uv };
        if (suppliedTangents)
            attributes["TANGENT"] = Add("VEC4", 5126, 6, () =>
            {
                for (var i = 0; i < 6; i++)
                {
                    writer.Write(1f); writer.Write(0f); writer.Write(0f);
                    var sign = i < 3 ? -1f : 1f;
                    if (legacySigns && (!mixedSigns || i < 3)) sign = -sign;
                    if (invalidSign && i == 0) sign = 0.5f;
                    writer.Write(sign);
                }
            });
        var indices = Add("SCALAR", 5123, 6, () => { for (ushort i = 0; i < 6; i++) writer.Write(i); });
        var bytes = stream.ToArray();
        var glb = Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        object buffer = glb ? new { byteLength = bytes.Length } : new { uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes), byteLength = bytes.Length };
        var asset = new Dictionary<string, string> { ["version"] = "2.0" };
        if (generator is not null) asset["generator"] = generator;
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            asset, buffers = new[] { buffer }, bufferViews = views, accessors,
            materials = new[] { new { name = "Skin" } },
            meshes = new[] { new { primitives = new[] { new { attributes, indices, material = 0 } } } },
            nodes = new[] { new { mesh = 0, scale = new[] { mirrored ? -1 : 1, 1, 1 } } },
            scenes = new[] { new { nodes = new[] { 0 } } }, scene = 0
        });
        if (!glb) { File.WriteAllBytes(path, json); return; }
        using var output = File.Create(path);
        using var binary = new BinaryWriter(output);
        var jsonLength = (json.Length + 3) & ~3;
        var bufferLength = (bytes.Length + 3) & ~3;
        binary.Write(0x46546C67); binary.Write(2); binary.Write(12 + 8 + jsonLength + 8 + bufferLength);
        binary.Write(jsonLength); binary.Write(0x4E4F534A); binary.Write(json);
        for (var i = json.Length; i < jsonLength; i++) binary.Write((byte)0x20);
        binary.Write(bufferLength); binary.Write(0x004E4942); binary.Write(bytes);
        for (var i = bytes.Length; i < bufferLength; i++) binary.Write((byte)0);

        int Add(string type, int componentType, int count, Action write)
        {
            while (stream.Position % 4 != 0) writer.Write((byte)0);
            var offset = (int)stream.Position;
            write();
            var index = views.Count;
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = (int)stream.Position - offset });
            accessors.Add(new { bufferView = index, componentType, count, type });
            return index;
        }
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
                        MorphFaceInterchangeService.PublishMeshFiles(names.Select(name => Path.Combine(source, name)).ToArray(), source, output);
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
            var normal = Add("VEC3", 5126, vertices.Length, () =>
            {
                foreach (var vertex in vertices) { writer.Write(0f); writer.Write(0f); writer.Write(1f); }
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
                    expected[(int)stream.Position] = lod.Handedness[vertex];
                    writer.Write(0f);
                }
            });
            var indices = Add("SCALAR", 5123, 3, () => { writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2); });
            primitives.Add(new { attributes = new Dictionary<string, int> { ["POSITION"] = positions, ["NORMAL"] = normal, ["TEXCOORD_0"] = uv, ["TANGENT"] = tangent }, indices, material = sectionIndex });
        }
        var bufferPath = Path.ChangeExtension(path, ".bin");
        var bytes = stream.ToArray();
        File.WriteAllBytes(bufferPath, bytes);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0", extras = "retain this metadata" },
            buffers = new[] { new { uri = Path.GetFileName(bufferPath), byteLength = bytes.Length } },
            bufferViews = views, accessors, materials = new[] { new { name = "Skin" }, new { name = "Detail" } }, meshes = new[] { new { primitives } }
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
