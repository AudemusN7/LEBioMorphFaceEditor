using System.Numerics;
using LegendaryExplorerCore.Helpers;
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
        new("head export applies rotated chunk-mapped pose exactly once across LODs", ExportPoseOnce),
        new("attachment staging preserves source and applies pose across LODs", AttachmentStaging),
        new("geometry-only export load preserves head and attachment geometry", ExportGeometryLoad)
    ];

    private static void ExportGeometryLoad()
    {
        var sourcePath = Path.GetFullPath(Path.Combine("fixtures", "Global Morphs", "LE2 GlobalMorphs.pcc"));
        var originalBytes = File.ReadAllBytes(sourcePath);
        using var reader = new MorphFacePackageReader();
        var geometry = reader.LoadExportGeometry(sourcePath, "HMM.BioFace_Security2");
        var normal = reader.Load(sourcePath, "HMM.BioFace_Security2", decodeTextures: false);
        // Check again after a material load to catch stale metrics from the previous operation.
        var afterMaterialLoad = reader.LoadExportGeometry(sourcePath, "HMM.BioFace_Security2");
        var metrics = reader.LastLoadMetrics!;
        TestAssert.True(metrics.Materials == TimeSpan.Zero && metrics.MaterialOverrides == TimeSpan.Zero &&
            metrics.MaterialResolution == TimeSpan.Zero && metrics.MaterialDefaults == TimeSpan.Zero &&
            metrics.MaterialShaderLookup == TimeSpan.Zero && metrics.MaterialTextureDecode == TimeSpan.Zero &&
            metrics.MaterialCacheHits == 0 && metrics.MaterialCacheMisses == 0,
            "Geometry-only load reported material work from an earlier read.");
        TestAssert.True(geometry.Materials.Materials.Count == 0 && afterMaterialLoad.Materials.Materials.Count == 0,
            "Geometry-only load returned preview materials.");
        TestAssert.True(normal.Document.FinalSkeleton.SequenceEqual(geometry.Document.FinalSkeleton) &&
            normal.Document.MorphFeatures.SequenceEqual(geometry.Document.MorphFeatures) &&
            normal.Document.BakedLods.Count == geometry.Document.BakedLods.Count &&
            normal.Document.BakedLods.Zip(geometry.Document.BakedLods).All(pair => pair.First.SequenceEqual(pair.Second)),
            "Geometry-only load changed the authored morph or pose.");
        CompareMesh(normal.BaseHead, geometry.BaseHead);
        TestAssert.True((normal.HairMesh is null) == (geometry.HairMesh is null), "Hair presence changed.");
        if (normal.HairMesh is not null) CompareMesh(normal.HairMesh, geometry.HairMesh!);
        TestAssert.Equal(normal.OtherMeshes.Count, geometry.OtherMeshes.Count);
        TestAssert.True(normal.OtherMeshes.Count > 0, "The export fixture must exercise an accessory.");
        for (var i = 0; i < normal.OtherMeshes.Count; i++) CompareMesh(normal.OtherMeshes[i], geometry.OtherMeshes[i]);
        TestAssert.True(originalBytes.SequenceEqual(File.ReadAllBytes(sourcePath)), "Loading changed the source PCC.");

        static void CompareMesh(SkeletalMeshAsset expected, SkeletalMeshAsset actual)
        {
            TestAssert.Equal(expected.Source, actual.Source);
            TestAssert.Equal(expected.Lods.Count, actual.Lods.Count);
            for (var i = 0; i < expected.Lods.Count; i++)
            {
                var a = expected.Lods[i];
                var b = actual.Lods[i];
                TestAssert.True(a.LodIndex == b.LodIndex && a.Positions.SequenceEqual(b.Positions) &&
                    a.Normals.SequenceEqual(b.Normals) && a.Topology.SignatureSha256 == b.Topology.SignatureSha256 &&
                    a.Topology.Sections.SequenceEqual(b.Topology.Sections) &&
                    a.Topology.ReferenceSkeleton.SequenceEqual(b.Topology.ReferenceSkeleton),
                    "Geometry-only load changed LOD geometry, sections or rig.");
                var ar = a.RenderData!;
                var br = b.RenderData!;
                TestAssert.True(ar.Tangents.SequenceEqual(br.Tangents) && ar.TextureCoordinates.SequenceEqual(br.TextureCoordinates) &&
                    ar.BoneIndices.SequenceEqual(br.BoneIndices) && ar.BoneWeights.SequenceEqual(br.BoneWeights) &&
                    ar.Indices.SequenceEqual(br.Indices) && ar.MaterialSlots.SequenceEqual(br.MaterialSlots),
                    "Geometry-only load changed vertex streams or material slots.");
            }
        }
    }

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
                out _,
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

    private static void AttachmentStaging()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-AttachmentStaging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var imported = new ImportedMeshAsset(
                "attachment.gltf", [new Vector3(1, 2, 3), new Vector3(4, 5, 6), new Vector3(7, 8, 9)],
                Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(), Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), 3).ToArray(),
                [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2],
                [new ImportedMeshSection(0, "Attachment", 0, 3)], [], null, null, [0, 1, 2])
            {
                Bones = [new ImportedMeshBone("root", -1, Vector3.Zero, Quaternion.Identity),
                    new ImportedMeshBone("jaw", 0, Vector3.UnitX, Quaternion.Identity)],
                BoneIndices = Enumerable.Repeat(new BoneIndex4(1, 0, 0, 0), 3).ToArray(),
                BoneWeights = Enumerable.Repeat(new Vector4(1, 0, 0, 0), 3).ToArray()
            };
            var binary = ImportedSkeletalMeshWriter.Build(imported, MEGame.LE2, [1, 1]).Binary;
            binary.LODModels = [binary.LODModels[0], ImportedSkeletalMeshWriter.Build(imported with
            {
                Positions = imported.Positions.Select(p => p + Vector3.UnitZ).ToArray()
            }, MEGame.LE2, [1, 1]).Binary.LODModels[0]];
            // Stock attachments can pack extra high-word data beside the 16-bit count.
            foreach (var lod in binary.LODModels) lod.Sections[0].NumTriangles = (5 << 16) | 1;
            var sourcePath = Path.Combine(directory, "source.pcc");
            MEPackageHandler.CreateAndSavePackage(sourcePath, MEGame.LE2);
            using (var package = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
            {
                var export = ExportCreator.CreateExport(package, "Hair", "SkeletalMesh", indexed: false);
                export.WriteProperty(MeshHelper.GetLodInfoForSkeletalMesh(binary, MEGame.LE2));
                export.WriteBinary(binary);
                package.Save(sourcePath);
            }
            var originalBytes = File.ReadAllBytes(sourcePath);
            var lods = binary.LODModels.Select((lod, index) => new SkeletalMeshLod(index,
                lod.VertexBufferGPUSkin.VertexData.Select(v => v.Position).ToArray(), [],
                new SkeletalMeshTopology(index, 3, 3, 2,
                    [new MeshSectionTopology(1, 0, 0, 1)], [], [], [], [], ""))).ToArray();
            var asset = new SkeletalMeshAsset(new AssetIdentity(sourcePath, "Hair", 1, "SkeletalMesh"), lods);
            foreach (var edited in new[] { false, true })
            {
                var output = Path.Combine(directory, edited ? "posed" : "native");
                Directory.CreateDirectory(output);
                var stagedPath = MorphFaceInterchangeService.CreateAttachmentMeshPackage(asset,
                    edited ? [new BoneTranslation("jaw", new Vector3(2, 0, 0))] : [], output, "ExportHair");
                using var staged = MEPackageHandler.OpenMEPackage(stagedPath, forceLoadFromDisk: true);
                var mesh = staged.Exports.Single().GetBinaryData<SkeletalMesh>();
                TestAssert.True(mesh.Materials.All(value => value == 0), "Staged material references must be null.");
                TestAssert.Equal(2, mesh.RefSkeleton.Length);
                TestAssert.Near(edited ? new Vector3(2, 0, 0) : Vector3.UnitX, mesh.RefSkeleton[1].Position, 0.0001f);
                for (var i = 0; i < lods.Length; i++)
                {
                    TestAssert.Equal((ushort)1, mesh.LODModels[i].Sections[0].MaterialIndex);
                    TestAssert.Equal(1, mesh.LODModels[i].Sections[0].NumTriangles);
                    for (var j = 0; j < lods[i].Positions.Length; j++)
                        TestAssert.Near(lods[i].Positions[j] + (edited ? Vector3.UnitX : Vector3.Zero),
                            mesh.LODModels[i].VertexBufferGPUSkin.VertexData[j].Position, 0.0001f);
                }
            }
            TestAssert.True(originalBytes.SequenceEqual(File.ReadAllBytes(sourcePath)), "Staging changed the source PCC.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
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
