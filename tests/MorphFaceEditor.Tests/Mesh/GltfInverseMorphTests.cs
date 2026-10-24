using System.Numerics;
using System.Text.Json;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using LegendaryExplorerCore.Unreal;

namespace MorphFaceEditor.Tests;

public static class GltfInverseMorphTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("glTF inverse morph PSK fallback proves differently ordered vertices", ReorderedFallback),
        new("glTF inverse morph PSK fallback expands shared points through wedges", WedgeExpandedFallback),
        new("glTF inverse morph PSK fallback rejects unrelated same-count vertices", RejectUnrelatedFallback)
    ];

    // Asymmetric coordinates distinguish the canonical basis from every reflected candidate.
    private static readonly Vector3[] Baked = [new(2, 3, 5), new(7, 11, 13), new(17, 19, 23)];
    private static MorphMeshFitPrior Prior => new("Fixture.BaseHead", [], [], [Baked]);
    private static SkeletalMeshAsset BaseHead => new(
        new AssetIdentity("", "Fixture.BaseHead", 0, "SkeletalMesh"),
        [new SkeletalMeshLod(0, Baked, new Vector3[3],
            new SkeletalMeshTopology(0, 3, 0, 0, [], [], [], [], [], ""))]);

    private static void ReorderedFallback() => WithFixture((gltf, psk) =>
    {
        WriteGltf(gltf, [2, 0, 1]);
        WritePsk(psk, [1, 2, 0]);
        var sidecar = MorphFaceInterchangeService.TryWriteGltfVertexMapSidecar(gltf, psk, Baked, Prior);
        AssertSidecar(gltf, sidecar, [2, 0, 1]);
    });

    private static void WedgeExpandedFallback() => WithFixture((gltf, psk) =>
    {
        Vector3[] baked = [.. Baked, Baked[0]];
        WriteGltf(gltf, [2, 0, 3, 1], baked: baked);
        WritePsk(psk, [1, 2, 3, 0], baked);
        var sidecar = MorphFaceInterchangeService.TryWriteGltfVertexMapSidecar(gltf, psk, baked,
            new MorphMeshFitPrior(Prior.BaseHeadPath, [], [], [baked]));
        AssertSidecar(gltf, sidecar, [2, 0, 3, 1], baked);
    });

    private static void RejectUnrelatedFallback() => WithFixture((gltf, psk) =>
    {
        WriteGltf(gltf, [2, 0, 1], offset: new Vector3(10, 20, 30));
        WritePsk(psk, [1, 2, 0]);
        var sidecar = MorphFaceInterchangeService.TryWriteGltfVertexMapSidecar(gltf, psk, Baked, Prior);
        TestAssert.True(sidecar is null && !File.Exists(gltf + ".mfe.json"),
            "Equal vertex counts must not authorize an unrelated map or large recovery corrections.");
    });

    private static void AssertSidecar(string gltf, string? sidecar, int[] expectedMap, Vector3[]? baked = null)
    {
        baked ??= Baked;
        TestAssert.True(sidecar is not null, "Expected a proven vertex map sidecar.");
        using var json = JsonDocument.Parse(File.ReadAllText(sidecar!));
        var root = json.RootElement;
        TestAssert.Equal("glTF stream 1, XZY, file order", root.GetProperty("CoordinateSystem").GetString());
        var mapping = root.GetProperty("ExportedToBase").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        TestAssert.True(mapping.SequenceEqual(expectedMap), "The glTF map borrowed the PSK export order.");
        var corrections = root.GetProperty("Corrections").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        for (var index = 0; index < baked.Length; index++)
            TestAssert.True(new Vector3(corrections[index * 3], corrections[index * 3 + 1], corrections[index * 3 + 2]).Length() <= 0.001f,
                "Sidecar corrections concealed a basis or correspondence mismatch.");
        var reference = BaseHead with { Lods = [BaseHead.Lods[0] with { Positions = baked }] };
        var positions = new MorphFaceInterchangeService().ReadMeshPositions(gltf, reference)[0];
        TestAssert.True(positions.HasValidatedSidecarMap, "The sidecar was not consumed.");
        TestAssert.True(positions.Prior is not null, "Sidecar correspondence lost its source payload.");
        for (var index = 0; index < baked.Length; index++) TestAssert.Near(baked[index], positions.Positions[index], 0.000001f);
    }

    private static void WithFixture(Action<string, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-GltfInverse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, "fixture.gltf"), Path.Combine(directory, "fixture.psk")); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void WriteGltf(string path, int[] order, Vector3 offset = default, Vector3[]? baked = null)
    {
        baked ??= Baked;
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (var index in order)
            {
                var value = (baked[index] + offset) / 100;
                writer.Write(value.X); writer.Write(value.Z); writer.Write(value.Y);
            }
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = order.Length * 12, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer.ToArray()) } },
            bufferViews = new[] { new { buffer = 0, byteLength = order.Length * 12 } },
            accessors = new[] { new { bufferView = 0, componentType = 5126, count = order.Length, type = "VEC3" } },
            meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 } } } } },
            nodes = new[] { new { mesh = 0 } },
            scenes = new[] { new { nodes = new[] { 0 } } }, scene = 0
        }));
    }

    private static void WritePsk(string path, int[] order, Vector3[]? baked = null)
    {
        baked ??= Baked;
        var points = order.Select(index => baked[index]).Distinct().ToArray();
        new PSK
        {
            Points = points.Select(value => new Vector3(value.X, -value.Y, value.Z)).ToList(),
            Wedges = order.Select(index => new PSK.PSKWedge
                { PointIndex = (ushort)Array.IndexOf(points, baked[index]) }).ToList(),
            Faces = [new() { WedgeIdx0 = 0, WedgeIdx1 = 1, WedgeIdx2 = 2 }],
            Materials = [new() { Name = "Fixture" }],
            Bones = [], Weights = [], VertexNormals = []
        }.ToFile(path);
    }
}
