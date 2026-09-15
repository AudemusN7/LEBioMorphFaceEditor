using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.Interchange;

namespace MorphFaceEditor.Tests;

public static class ImportedSkeletalMeshWriterTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("C4 writer builds the same unrigged mesh shape for LE1 LE2 and LE3", UnriggedMeshBuildsForAllGames),
        new("C4 writer preserves sections and material slot indices", SectionsAndMaterialsArePreserved),
        new("C4 writer preserves tangent handedness", TangentSignIsPreserved),
        new("C4 writer preserves canonical coordinates at the UE3 binary boundary", CanonicalCoordinatesArePreserved),
        new("C4 writer does not double-convert decoded PSK coordinates", DecodedPskCoordinatesArePreserved),
        new("C4 glTF decoder restores Legendary Explorer's exported axis basis", GltfBasisIsCanonicalized),
        new("C4 writer quantises rigged influences to 255", RiggedInfluencesSumTo255),
        new("C4 writer supplies a minimal root for unrigged meshes", UnriggedMeshesUseMinimalRoot),
        new("C4 writer binary survives package save and reopen", BinarySurvivesReopen)
    ];

    private static void UnriggedMeshBuildsForAllGames()
    {
        var asset = CreateTriangle();
        foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            var build = ImportedSkeletalMeshWriter.Build(asset, game, [41]);
            var lod = build.Binary.LODModels.Single();

            TestAssert.Equal(1, build.Binary.Materials.Length);
            TestAssert.Equal(41, build.Binary.Materials[0]);
            TestAssert.Equal(3u, lod.NumVertices);
            TestAssert.Equal(3, lod.IndexBuffer.Length);
            TestAssert.Equal(1, lod.Sections.Length);
            TestAssert.Equal(1, (int)lod.Sections[0].NumTriangles);
            TestAssert.Equal(1, build.Binary.RefSkeleton.Length);
            TestAssert.Equal("root", build.Binary.RefSkeleton[0].Name.Instanced);
            TestAssert.True(game == MEGame.LE1
                ? lod.ME1VertexBufferGPUSkin?.Length == 3 && lod.VertexBufferGPUSkin is null
                : lod.VertexBufferGPUSkin?.VertexData.Length == 3 && lod.ME1VertexBufferGPUSkin is null,
                $"The {game} vertex buffer variant was not selected.");
        }
    }

    private static void SectionsAndMaterialsArePreserved()
    {
        var asset = new ImportedMeshAsset(
            "sections.glb",
            [
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
                new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(0, 1, 1)
            ],
            Enumerable.Repeat(Vector3.UnitZ, 6).ToArray(),
            Enumerable.Repeat(new Vector4(1, 0, 0, 1), 6).ToArray(),
            Enumerable.Repeat(Vector2.Zero, 6).ToArray(),
            [0, 1, 2, 3, 4, 5],
            [
                new ImportedMeshSection(0, "Skin", 0, 3),
                new ImportedMeshSection(1, "Eyes", 3, 3)
            ],
            [], null, null, [10, 11, 12, 13, 14, 15]);

        var build = ImportedSkeletalMeshWriter.Build(asset, MEGame.LE3, [101, 202]);
        var lod = build.Binary.LODModels.Single();

        TestAssert.True(build.Binary.Materials.SequenceEqual([101, 202]),
            "The writer changed the exact material UIndexes supplied by the materializer.");
        TestAssert.Equal(0u, lod.Sections[0].BaseIndex);
        TestAssert.Equal((ushort)0, lod.Sections[0].MaterialIndex);
        TestAssert.Equal((ushort)1, lod.Sections[1].MaterialIndex);
        TestAssert.Equal(3u, lod.Sections[1].BaseIndex);
        TestAssert.True(lod.IndexBuffer.SequenceEqual(new ushort[] { 0, 1, 2, 3, 4, 5 }),
            "The writer changed canonical section index ordering.");
        TestAssert.True(lod.RawPointIndices.SequenceEqual(new ushort[] { 10, 11, 12, 13, 14, 15 }),
            "The writer changed source vertex mapping.");
    }

    private static void TangentSignIsPreserved()
    {
        var source = CreateTriangle() with
        {
            Tangents =
            [
                new Vector4(Vector3.UnitX, -1),
                new Vector4(Vector3.UnitX, -1),
                new Vector4(Vector3.UnitX, -1)
            ]
        };
        var build = ImportedSkeletalMeshWriter.Build(source, MEGame.LE3, [1]);
        var tangent = build.Binary.LODModels.Single().VertexBufferGPUSkin.VertexData[0].TangentZ;
        var tangentVector = (Vector4)tangent;

        TestAssert.True(tangentVector.W < 0,
            "A negative imported bitangent sign was not preserved in TangentZ.W.");
    }

    private static void CanonicalCoordinatesArePreserved()
    {
        var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f);
        var source = new ImportedMeshAsset(
            "transform-sensitive.gltf",
            [
                new Vector3(2, 3, 5), new Vector3(-7, 11, 13), new Vector3(17, -19, 23)
            ],
            [Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ],
            [new Vector4(Vector3.UnitX, 1), new Vector4(Vector3.UnitY, -1), new Vector4(Vector3.UnitZ, 1)],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [0, 1, 2],
            [new ImportedMeshSection(0, "Skin", 0, 3)],
            [new ImportedMeshBone("root", -1, new Vector3(29, 31, 37), orientation)],
            [new BoneIndex4(0, 0, 0, 0), new BoneIndex4(0, 0, 0, 0), new BoneIndex4(0, 0, 0, 0)],
            [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX],
            [0, 1, 2]);

        foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            var build = ImportedSkeletalMeshWriter.Build(source, game, [1]);
            var binary = build.Binary;
            var lod = binary.LODModels.Single();
            var positions = game == MEGame.LE1
                ? lod.ME1VertexBufferGPUSkin!.Select(value => value.Position).ToArray()
                : lod.VertexBufferGPUSkin!.VertexData.Select(value => value.Position).ToArray();

            TestAssert.Equal(new Vector3(2, 3, 5), positions[0]);
            TestAssert.Equal(new Vector3(-7, 11, 13), positions[1]);
            TestAssert.Equal(new Vector3(17, -19, 23), positions[2]);
            TestAssert.True(lod.IndexBuffer.SequenceEqual(new ushort[] { 0, 1, 2 }),
                $"The {game} writer altered canonical triangle winding.");
            TestAssert.Equal(new Vector3(5, -4, 14), binary.Bounds.Origin);
            TestAssert.Equal(new Vector3(29, 31, 37), binary.RefSkeleton[0].Position);

            var expectedOrientation = Quaternion.Normalize(orientation);
            var actualOrientation = binary.RefSkeleton[0].Orientation;
            TestAssert.True(Quaternion.Dot(expectedOrientation, actualOrientation) > 0.99999f,
                $"The {game} writer did not reflect the authored bone orientation into UE3 space.");

            var normal = game == MEGame.LE1
                ? (Vector3)lod.ME1VertexBufferGPUSkin![0].TangentZ
                : (Vector3)lod.VertexBufferGPUSkin!.VertexData[0].TangentZ;
            TestAssert.True(Vector3.Distance(normal, new Vector3(0, 1, 0)) < 0.01f,
                $"The {game} writer did not reflect the normal into UE3 space ({normal}).");
        }
    }

    private static void DecodedPskCoordinatesArePreserved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mfe-c4-psk-{Guid.NewGuid():N}.psk");
        try
        {
            var raw = new PSK
            {
                Points = [new(2, 3, 5), new(-7, 11, 13), new(17, -19, 23)],
                Wedges =
                [
                    new() { PointIndex = 0, U = 0, V = 0 },
                    new() { PointIndex = 1, U = 1, V = 0 },
                    new() { PointIndex = 2, U = 0, V = 1 }
                ],
                Faces = [new() { WedgeIdx0 = 0, WedgeIdx1 = 1, WedgeIdx2 = 2, MatIndex = 0 }],
                Materials = [new() { Name = "Skin" }],
                Bones = [],
                Weights = [],
                VertexNormals = []
            };
            raw.ToFile(path);
            var decoded = new MorphFaceInterchangeService().ReadMesh(path);
            var build = ImportedSkeletalMeshWriter.Build(decoded, MEGame.LE3, [1]);
            var vertices = build.Binary.LODModels.Single().VertexBufferGPUSkin.VertexData;

            TestAssert.True(decoded.Positions[0] == new Vector3(2, -3, 5),
                "The PSK decoder fixture did not establish its documented Y-reflected basis.");
            TestAssert.Equal(decoded.Positions[0], vertices[0].Position);
            TestAssert.Equal(decoded.Positions[1], vertices[1].Position);
            TestAssert.Equal(decoded.Positions[2], vertices[2].Position);
            TestAssert.True(decoded.Indices.SequenceEqual(build.Binary.LODModels.Single().IndexBuffer.Select(value => (int)value)),
                "The writer changed indices after PSK decoding.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void GltfBasisIsCanonicalized()
    {
        TestAssert.Equal(
            new Vector3(2, 5, 3),
            ImportedMeshDecoder.GltfToCanonicalVector(new Vector3(2, 3, 5)));

        var gltfRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f);
        var expectedGameRotation = Quaternion.CreateFromAxisAngle(-Vector3.UnitZ, 0.6f);
        var actualGameRotation = ImportedMeshDecoder.GltfToCanonicalQuaternion(gltfRotation);
        TestAssert.True(MathF.Abs(Quaternion.Dot(expectedGameRotation, actualGameRotation)) > 0.99999f,
            "The glTF Y-axis rotation was not restored as the equivalent game-space -Z rotation.");
    }

    private static void RiggedInfluencesSumTo255()
    {
        var source = CreateTriangle() with
        {
            Bones =
            [
                new ImportedMeshBone("root", -1, Vector3.Zero, Quaternion.Identity),
                new ImportedMeshBone("jaw", 0, new Vector3(0, 1, 0), Quaternion.Identity)
            ],
            BoneIndices =
            [
                new BoneIndex4(0, 1, 0, 0),
                new BoneIndex4(0, 1, 0, 0),
                new BoneIndex4(0, 1, 0, 0)
            ],
            BoneWeights =
            [
                new Vector4(0.10f, 0.20f, 0, 0),
                new Vector4(0.25f, 0.50f, 0, 0),
                new Vector4(0.90f, 0.10f, 0, 0)
            ]
        };
        var build = ImportedSkeletalMeshWriter.Build(source, MEGame.LE2, [1]);
        var vertex = build.Binary.LODModels.Single().VertexBufferGPUSkin.VertexData[0];
        var weights = vertex.InfluenceWeights;
        var sum = weights[0] + weights[1] + weights[2] + weights[3];

        TestAssert.Equal(255, sum);
        TestAssert.Equal((byte)0, vertex.InfluenceBones[2]);
        TestAssert.Equal((byte)0, vertex.InfluenceBones[3]);
        TestAssert.True(vertex.InfluenceWeights[0] > 0 && vertex.InfluenceWeights[1] > 0,
            "Positive source influences were lost during quantisation.");
    }

    private static void UnriggedMeshesUseMinimalRoot()
    {
        var build = ImportedSkeletalMeshWriter.Build(CreateTriangle(), MEGame.LE1, [1]);
        var lod = build.Binary.LODModels.Single();

        TestAssert.Equal(1, build.Binary.RefSkeleton.Length);
        TestAssert.Equal(-1, build.Binary.RefSkeleton[0].ParentIndex);
        TestAssert.Equal(3, lod.Chunks.Single().NumRigidVertices);
        TestAssert.Equal(0, lod.Chunks.Single().NumSoftVertices);
        TestAssert.Equal((byte)255, lod.ME1VertexBufferGPUSkin![0].InfluenceWeights[0]);
    }

    private static void BinarySurvivesReopen()
    {
        foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            var path = Path.Combine(Path.GetTempPath(), $"mfe-c4-writer-{game}-{Guid.NewGuid():N}.pcc");
            try
            {
                LegendaryExplorerCoreRuntime.Initialize();
                using (var package = MEPackageHandler.CreateMemoryEmptyPackage(path, game))
                {
                    var root = package.CreatePackageExport("MorphFaceEditor");
                    var export = package.CreateExport("FixtureMesh", "SkeletalMesh", root, indexed: false);
                    var build = ImportedSkeletalMeshWriter.Build(CreateTriangle(), game, [77]);
                    export.WriteBinary(build.Binary);
                    export.WriteProperty(build.LodInfo);
                    package.Save(path);
                }

                using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
                var meshExport = reopened.FindExport("MorphFaceEditor.FixtureMesh", "SkeletalMesh")
                                ?? throw new Exception("The writer fixture export could not be reopened.");
                var binary = ObjectBinary.From<SkeletalMesh>(meshExport)
                             ?? throw new Exception("The reopened SkeletalMesh binary was unreadable.");
                var lod = binary.LODModels.Single();
                TestAssert.True(binary.Materials.SequenceEqual([77]), "Material UIndexes did not survive reopen.");
                TestAssert.Equal(3u, lod.NumVertices);
                TestAssert.Equal(3, lod.IndexBuffer.Length);
                TestAssert.Equal(1, meshExport.GetProperty<ArrayProperty<StructProperty>>("LODInfo")?.Count);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private static ImportedMeshAsset CreateTriangle() => new(
        "triangle.gltf",
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(),
        Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), 3).ToArray(),
        [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
        [0, 1, 2],
        [new ImportedMeshSection(0, "Skin", 0, 3)],
        [], null, null, [0, 1, 2]);
}
