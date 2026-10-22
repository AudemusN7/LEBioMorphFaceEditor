using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

public static class ImportedSkeletalMeshWriterTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("C4 writer quantises rigged influences to 255", RiggedInfluencesSumTo255),
        new("mesh writer preserves coordinates sections tangents and minimal rig through binary reopen", BinarySurvivesReopen)
    ];

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

    private static void BinarySurvivesReopen()
    {
        var source = CreateTriangle() with
        {
            Positions = [new(2, 3, 5), new(-7, 11, 13), new(17, -19, 23)],
            Tangents = Enumerable.Repeat(new Vector4(Vector3.UnitX, -1), 3).ToArray(),
            SourceVertexIndices = [10, 11, 12]
        };
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
                    var build = ImportedSkeletalMeshWriter.Build(source, game, [77]);
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
                TestAssert.True(lod.IndexBuffer.SequenceEqual(new ushort[] { 0, 1, 2 }) &&
                    lod.RawPointIndices.SequenceEqual(new ushort[] { 10, 11, 12 }),
                    "The writer changed triangle winding or source vertex correspondence.");
                TestAssert.Equal((ushort)0, lod.Sections.Single().MaterialIndex);
                TestAssert.Equal(1, binary.RefSkeleton.Length);
                TestAssert.Equal(-1, binary.RefSkeleton[0].ParentIndex);
                // All LE games reopen with the shared GPU buffer, including converted LE1 writer data.
                var vertices = lod.VertexBufferGPUSkin?.VertexData
                               ?? throw new Exception($"The reopened {game} mesh has no GPU skin vertex buffer.");
                TestAssert.True(source.Positions.SequenceEqual(vertices.Select(value => value.Position)),
                    "The writer changed canonical vertex coordinates.");
                var tangent = vertices[0].TangentZ;
                TestAssert.True(((Vector4)tangent).W < 0, "The writer lost the authored tangent handedness.");
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
