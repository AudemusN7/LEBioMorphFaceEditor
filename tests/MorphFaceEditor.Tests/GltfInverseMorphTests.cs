using System.Numerics;
using System.Text.Json;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

public static class GltfInverseMorphTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("glTF inverse morph raw basis matches the Custom decoder", CanonicalBasis),
        new("glTF inverse morph direct sidecar uses only numerical corrections", DirectSidecar),
        new("glTF inverse morph MD5 fallback proves differently ordered vertices", ReorderedFallback),
        new("glTF inverse morph MD5 fallback rejects unrelated same-count vertices", RejectUnrelatedFallback)
    ];

    // Asymmetric coordinates distinguish the canonical basis from every reflected candidate.
    private static readonly Vector3[] Baked = [new(2, 3, 5), new(7, 11, 13), new(17, 19, 23)];
    private static MorphMeshFitPrior Prior => new("Fixture.BaseHead", [], [], [Baked]);
    private static SkeletalMeshAsset BaseHead => new(
        new AssetIdentity("", "Fixture.BaseHead", 0, "SkeletalMesh"), Baked, new Vector3[3],
        new SkeletalMeshTopology(0, 3, 0, 0, [], [], [], [], [], ""));

    private static void CanonicalBasis() => WithFixture((gltf, _) =>
    {
        WriteGltf(gltf, [2, 0, 1]);
        var service = new MorphFaceInterchangeService();
        var raw = service.ReadMeshPositions(gltf, BaseHead).Single(value =>
            value.CoordinateSystem.EndsWith("XZY, file order", StringComparison.Ordinal));
        var custom = service.ReadMesh(gltf);
        for (var index = 0; index < 3; index++)
        {
            TestAssert.Near(Baked[new[] { 2, 0, 1 }[index]], raw.Positions[index], 0.0001f);
            TestAssert.Near(custom.Positions[index], raw.Positions[index], 0.0001f);
        }
    });

    private static void DirectSidecar() => WithFixture((gltf, _) =>
    {
        WriteGltf(gltf, [2, 0, 1]);
        var sidecar = MorphFaceInterchangeService.TryWriteVertexMapSidecar(gltf, MorphMeshFormat.Gltf, Baked, Prior);
        AssertSidecar(gltf, sidecar, [2, 0, 1]);
    });

    private static void ReorderedFallback() => WithFixture((gltf, md5) =>
    {
        WriteGltf(gltf, [2, 0, 1]);
        WriteMd5(md5, [1, 2, 0]);
        var sidecar = MorphFaceInterchangeService.TryWriteGltfVertexMapSidecar(gltf, md5, Baked, Prior);
        AssertSidecar(gltf, sidecar, [2, 0, 1]);
    });

    private static void RejectUnrelatedFallback() => WithFixture((gltf, md5) =>
    {
        WriteGltf(gltf, [2, 0, 1], offset: new Vector3(10, 20, 30));
        WriteMd5(md5, [1, 2, 0]);
        var sidecar = MorphFaceInterchangeService.TryWriteGltfVertexMapSidecar(gltf, md5, Baked, Prior);
        TestAssert.True(sidecar is null && !File.Exists(gltf + ".mfe.json"),
            "Equal vertex counts must not authorize an unrelated map or large recovery corrections.");
    });

    private static void AssertSidecar(string gltf, string? sidecar, int[] expectedMap)
    {
        TestAssert.True(sidecar is not null, "Expected a proven vertex map sidecar.");
        using var json = JsonDocument.Parse(File.ReadAllText(sidecar!));
        var root = json.RootElement;
        TestAssert.Equal("glTF stream 1, XZY, file order", root.GetProperty("CoordinateSystem").GetString());
        var mapping = root.GetProperty("ExportedToBase").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        TestAssert.True(mapping.SequenceEqual(expectedMap), "The glTF map borrowed the MD5 export order.");
        var corrections = root.GetProperty("Corrections").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        for (var index = 0; index < 3; index++)
            TestAssert.True(new Vector3(corrections[index * 3], corrections[index * 3 + 1], corrections[index * 3 + 2]).Length() <= 0.001f,
                "Sidecar corrections concealed a basis or correspondence mismatch.");
        var positions = new MorphFaceInterchangeService().ReadMeshPositions(gltf, BaseHead)[0];
        TestAssert.True(positions.CoordinateSystem.EndsWith("MFE vertex map", StringComparison.Ordinal), "The sidecar was not consumed.");
        for (var index = 0; index < 3; index++) TestAssert.Near(Baked[index], positions.Positions[index], 0.000001f);
    }

    private static void WithFixture(Action<string, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-GltfInverse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, "fixture.gltf"), Path.Combine(directory, "fixture.md5mesh")); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void WriteGltf(string path, int[] order, Vector3 offset = default)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (var index in order)
            {
                var value = (Baked[index] + offset) / 100;
                writer.Write(value.X); writer.Write(value.Z); writer.Write(value.Y);
            }
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = 36, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer.ToArray()) } },
            bufferViews = new[] { new { buffer = 0, byteLength = 36 } },
            accessors = new[] { new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3" } },
            meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 } } } } },
            nodes = new[] { new { mesh = 0 } },
            scenes = new[] { new { nodes = new[] { 0 } } }, scene = 0
        }));
    }

    private static void WriteMd5(string path, int[] order)
    {
        var lines = new List<string> { "MD5Version 10", "numJoints 1", "numMeshes 1", "joints {", "\"root\" -1 ( 0 0 0 ) ( 0 0 0 )", "}", "mesh {", "shader \"fixture\"", "numverts 3" };
        for (var index = 0; index < 3; index++) lines.Add($"vert {index} ( 0 0 ) {index} 1");
        lines.Add("numtris 1"); lines.Add("tri 0 0 1 2"); lines.Add("numweights 3");
        for (var index = 0; index < 3; index++)
        {
            var value = Baked[order[index]];
            lines.Add(FormattableString.Invariant($"weight {index} 0 1 ( {value.X} {value.Y} {value.Z} )"));
        }
        lines.Add("}"); File.WriteAllLines(path, lines);
    }
}
