using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.Interchange;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Tests;

public static class MorphFaceInterchangeExportTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("UModel staging preserves indexed names and isolates baked geometry", StagingPreservesIndexedMeshName),
        new("head export applies rotated chunk-mapped pose exactly once across LODs", ExportPoseOnce)
    ];

    private static void StagingPreservesIndexedMeshName()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var sourcePath = Path.GetFullPath(Path.Combine(
            "fixtures", "Global Morphs", "LE2 GlobalMorphs.pcc"));
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The LE2 GlobalMorphs fixture is required for the indexed mesh-name regression.", sourcePath);
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"MFE-IndexedMeshName-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var stagingPath = MorphFaceInterchangeService.CreateAppliedMeshPackage(
                sourcePath,
                "HMM.Skel_HMM_1",
                temporaryDirectory,
                out var umodelMeshName,
                out var bakedPositions,
                out _);

            using var stagingPackage = MEPackageHandler.OpenMEPackage(stagingPath, forceLoadFromDisk: true);
            var mesh = stagingPackage.Exports.Single();
            TestAssert.Equal("Skel_HMM_1", umodelMeshName);
            TestAssert.Equal(mesh.ObjectName.Instanced, umodelMeshName);
            TestAssert.Equal("Skel_HMM", mesh.ObjectNameString);
            TestAssert.True(bakedPositions.Length > 0, "The staged indexed BioMorphFace had no baked geometry.");
            var geometry = mesh.GetBinaryData<SkeletalMesh>();
            TestAssert.True(geometry.Materials.All(index => index == 0),
                "The UModel staging mesh retained a material dependency.");
            TestAssert.Equal(geometry.LODModels.Length,
                mesh.GetProperty<ArrayProperty<StructProperty>>("LODInfo")!.Count);
            TestAssert.Equal(bakedPositions.Length,
                geometry.LODModels[0].VertexBufferGPUSkin.VertexData.Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static void ExportPoseOnce()
    {
        var source = new ImportedMeshAsset(
            "fixture.gltf",
            [new Vector3(2, 3, 5), new Vector3(7, 11, 13), new Vector3(17, 19, 23)],
            Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(),
            Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), 3).ToArray(),
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2],
            [new ImportedMeshSection(0, "Skin", 0, 3)], [], null, null, [0, 1, 2])
        {
            Bones =
            [
                new ImportedMeshBone("root", -1, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)),
                new ImportedMeshBone("jaw", 0, new Vector3(2, 0, 0), Quaternion.Identity)
            ],
            BoneIndices = [new BoneIndex4(1, 0, 0, 0), new BoneIndex4(0, 0, 0, 0), new BoneIndex4(1, 0, 0, 0)],
            BoneWeights = [new Vector4(1, 0, 0, 0), new Vector4(1, 0, 0, 0), new Vector4(0.5f, 0.5f, 0, 0)]
        };

        foreach (var game in new[] { MEGame.LE1, MEGame.LE2 })
        {
            SkeletalMesh CreateMesh()
            {
                var mesh = ImportedSkeletalMeshWriter.Build(source, game, [0]).Binary;
                var lower = ImportedSkeletalMeshWriter.Build(source with
                {
                    Positions = source.Positions.Select(v => v + new Vector3(10, 0, 0)).ToArray()
                }, game, [0]).Binary.LODModels[0];
                mesh.LODModels = [mesh.LODModels[0], lower];
                foreach (var lod in mesh.LODModels)
                {
                    lod.Chunks[0].BoneMap = [1, 0];
                    if (game == MEGame.LE1)
                    {
                        for (var i = 0; i < lod.ME1VertexBufferGPUSkin.Length; i++)
                        {
                            var vertex = lod.ME1VertexBufferGPUSkin[i];
                            vertex.InfluenceBones = new Influences((byte)(1 - vertex.InfluenceBones[0]), (byte)(1 - vertex.InfluenceBones[1]), 1, 1);
                            lod.ME1VertexBufferGPUSkin[i] = vertex;
                        }
                    }
                    else
                    {
                        for (var i = 0; i < lod.VertexBufferGPUSkin.VertexData.Length; i++)
                        {
                            var vertex = lod.VertexBufferGPUSkin.VertexData[i];
                            vertex.InfluenceBones = new Influences((byte)(1 - vertex.InfluenceBones[0]), (byte)(1 - vertex.InfluenceBones[1]), 1, 1);
                            lod.VertexBufferGPUSkin.VertexData[i] = vertex;
                        }
                    }
                }
                return mesh;
            }

            Vector3[] Positions(StaticLODModel lod) => game == MEGame.LE1
                ? lod.ME1VertexBufferGPUSkin.Select(v => v.Position).ToArray()
                : lod.VertexBufferGPUSkin.VertexData.Select(v => v.Position).ToArray();
            var mesh = CreateMesh();
            var untouched = CreateMesh();
            Vector3[] bake = [new(3, 5, 7), new(11, 13, 17), new(19, 23, 29)];
            Vector3[] invalidLowerBake = [new(999, 999, 999)];
            var final = new[] { new BoneTranslation("jaw", new Vector3(3, 0, 0)) };
            var posed = MorphMeshExportPose.Apply(mesh, [bake, invalidLowerBake], final);
            Vector3[] expectedDelta = [Vector3.UnitY, Vector3.Zero, Vector3.UnitY * (128f / 255f)];
            var lowerOriginal = Positions(untouched.LODModels[1]);
            for (var i = 0; i < bake.Length; i++)
            {
                TestAssert.Near(bake[i] + expectedDelta[i], posed[i], 0.0001f);
                TestAssert.Near(posed[i], Positions(mesh.LODModels[0])[i], 0.0001f);
                TestAssert.Near(lowerOriginal[i] + expectedDelta[i], Positions(mesh.LODModels[1])[i], 0.0001f);
                TestAssert.Near(source.Positions[i], Positions(untouched.LODModels[0])[i], 0);
            }
            TestAssert.Near(new Vector3(3, 0, 0), mesh.RefSkeleton[1].Position, 0.0001f);
            var posedLower = Positions(mesh.LODModels[1]);
            var second = MorphMeshExportPose.Apply(mesh, [posed, posedLower], final);
            for (var i = 0; i < posed.Length; i++) TestAssert.Near(posed[i], second[i], 0.0001f);
        }
    }
}
