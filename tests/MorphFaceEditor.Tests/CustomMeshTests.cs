using System.Numerics;
using System.Text.Json;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class CustomMeshTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("custom mesh profile is inferred from its materials", ProfileIsInferredFromMaterials),
        new("material-only sessions accept an empty morph payload", EmptyMorphPayloadUsesBaseGeometry),
        new("detached PSK decoding expands seams and preserves sections", PskDecodeExpandsSeams),
        new("detached PSK decoding preserves optional rig weights", PskDecodePreservesRig),
        new("detached glTF decoding applies node transforms and derives streams", GltfDecodeAppliesTransforms),
        new("detached GLB decoding retains render sections", GlbDecodeRetainsSections),
        new("player topology recognition maps reordered vertices and rejects unrelated triangles", PlayerTopologyRecognitionIsStructural)
    ];

    private static void ProfileIsInferredFromMaterials()
    {
        var materialIdentity = new AssetIdentity(
            "fixture.pcc",
            "BIOA_PRC2_HMM_Ahern.Materials.Ahern_Custom_Head",
            7,
            "MaterialInstanceConstant");
        var material = new ResolvedHeadMaterial(
            MaterialIdentityKey.Create(materialIdentity),
            materialIdentity,
            "HMM_HED_PRO_MASTER_FACE_MAT",
            HeadMaterialFamily.Skin,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            new Dictionary<string, MaterialTextureBinding>());
        var overrides = new MorphFaceMaterialOverrides(
            TestFixtures.CreateIdentity("Ahern.MaterialOverride", "BioMaterialOverride"),
            [new ScalarMaterialOverride("HED_Norm_Blend", 0.5f)],
            [],
            []);

        var resolution = MorphFaceProfileRegistry.CreateDefault().Resolve(
            MorphFaceGame.LE1,
            "Ahern.CustomFace",
            "BIOA_PRC2_HMM_Ahern.Ahern_Custom_Head_MDL",
            overrides,
            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>
            {
                [material.Key] = material
            }));

        TestAssert.Equal("le1-human-male", resolution?.Profile.Key);
        TestAssert.True(resolution?.UsesCustomMesh == true,
            "The unknown base mesh was treated as a canonical PROMorph mesh.");
    }

    private static void EmptyMorphPayloadUsesBaseGeometry()
    {
        var mesh = TestFixtures.CreateMesh();
        var document = new MorphFaceDocument(
            TestFixtures.CreateIdentity("Ahern.CustomFace", "BioMorphFace"),
            new PackageFingerprint(1, DateTime.UnixEpoch, new string('0', 64)),
            mesh.Source,
            null,
            [],
            [],
            MorphFaceMaterialOverrides.Empty,
            [],
            []);

        var session = new MorphFaceEditingSession(
            document,
            mesh,
            [],
            profileName: "LE1 Human Male",
            geometryEditBlockReason: "Custom base mesh; geometry editing is unavailable.");

        TestAssert.True(!session.CanEdit, "A custom mesh unexpectedly enabled geometry editing.");
        TestAssert.Equal(mesh.Positions[0], session.Evaluation.Geometry.Positions[0]);
        var draft = session.CreateDraft(null, [], new MorphFaceMaterialOverrides(
            null,
            [new ScalarMaterialOverride("HED_Norm_Blend", 0.75f)],
            [],
            []));
        TestAssert.Equal(0, draft.MorphFeatures.Count);
        TestAssert.Equal(0, draft.FinalSkeleton.Count);
        TestAssert.Equal(0, draft.BakedLods.Count);
        TestAssert.Near(0.75f, draft.MaterialOverrides.Scalars.Single().Value, 0);
    }

    private static void PskDecodeExpandsSeams()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mfe-{Guid.NewGuid():N}.psk");
        try
        {
            var psk = new PSK
            {
                Points = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)],
                Wedges =
                [
                    new() { PointIndex = 0, U = 0, V = 0, MatIndex = 0 },
                    new() { PointIndex = 1, U = 1, V = 0, MatIndex = 0 },
                    new() { PointIndex = 2, U = 1, V = 1, MatIndex = 0 },
                    new() { PointIndex = 0, U = 0, V = 0.5f, MatIndex = 1 },
                    new() { PointIndex = 2, U = 1, V = 1, MatIndex = 1 },
                    new() { PointIndex = 3, U = 0, V = 1, MatIndex = 1 }
                ],
                Faces =
                [
                    new() { WedgeIdx0 = 0, WedgeIdx1 = 1, WedgeIdx2 = 2, MatIndex = 0 },
                    new() { WedgeIdx0 = 3, WedgeIdx1 = 4, WedgeIdx2 = 5, MatIndex = 1 }
                ],
                Materials = [new() { Name = "Skin" }, new() { Name = "Tattoo" }],
                Bones = [],
                Weights = [],
                VertexNormals = []
            };
            psk.ToFile(path);
            var mesh = new MorphFaceInterchangeService().ReadMesh(path);
            TestAssert.Equal(6, mesh.Positions.Length);
            TestAssert.Equal(new Vector3(0, 0, 0), mesh.Positions[0]);
            TestAssert.Equal(new Vector3(0, 0, 0), mesh.Positions[3]);
            TestAssert.Equal(new Vector2(0, 0), mesh.TextureCoordinates![0]);
            TestAssert.Equal(new Vector2(0, 0.5f), mesh.TextureCoordinates[3]);
            TestAssert.Equal(2, mesh.Sections.Count);
            TestAssert.Equal("Skin", mesh.Sections[0].MaterialName);
            TestAssert.Equal("Tattoo", mesh.Sections[1].MaterialName);
            TestAssert.Equal(6, mesh.Indices.Length);
            TestAssert.True(mesh.NormalsWereGenerated, "PSK fixture did not exercise normal generation.");
            TestAssert.True(mesh.Tangents is not null, "UV-bearing PSK did not receive tangents.");
            TestAssert.Equal(0, mesh.Bones.Count);
            TestAssert.True(!mesh.HasRig, "An unrigged PSK was reported as rigged.");
            var preview = new DetachedMeshPreviewLoadService(new HeadPreviewSceneFactory())
                .Load(MorphFaceGame.LE2, mesh);
            TestAssert.True(!preview.Preview.EditingSession.CanEditMorphFeatures,
                "Detached PSK exposed morph controls.");
            TestAssert.True(!preview.Preview.EditingSession.CanEditBones,
                "Unrigged detached PSK exposed bone controls.");
            TestAssert.True(!preview.Preview.Scene.Meshes.Single().ApplySkinning,
                "Unrigged detached PSK enabled renderer skinning.");
            TestAssert.True(preview.Detached.Source.Positions.SequenceEqual(mesh.Positions),
                "Detached PSK did not retain exact source positions.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void GltfDecodeAppliesTransforms()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mfe-{Guid.NewGuid():N}.gltf");
        try
        {
            WriteGltfFixture(path);
            var mesh = new MorphFaceInterchangeService().ReadMesh(path);
            TestAssert.Equal(6, mesh.Positions.Length);
            TestAssert.Equal(new Vector3(100, 200, 300), mesh.Positions[0]);
            TestAssert.Equal(new Vector3(-100, 200, 300), mesh.Positions[1]);
            TestAssert.Equal(new Vector3(100, 400, 300), mesh.Positions[2]);
            TestAssert.Equal(6, mesh.Indices.Length);
            TestAssert.Equal(2, mesh.Indices[1]); // negative determinant corrected the winding
            TestAssert.Equal(2, mesh.Sections.Count);
            TestAssert.Equal("Face", mesh.Sections[0].MaterialName);
            TestAssert.Equal("Detail", mesh.Sections[1].MaterialName);
            TestAssert.True(mesh.NormalsWereGenerated, "glTF fixture did not exercise normal generation.");
            TestAssert.True(mesh.Tangents is not null && mesh.TangentsWereGenerated,
                "UV-bearing glTF did not receive generated tangents.");
            TestAssert.True(mesh.TextureCoordinates is not null, "glTF UV0 was dropped.");
            TestAssert.Equal(0, mesh.Bones.Count);
            var preview = new DetachedMeshPreviewLoadService(new HeadPreviewSceneFactory())
                .Load(MorphFaceGame.LE3, mesh);
            TestAssert.Equal(2, preview.Preview.Scene.Meshes.Single().Sections.Count);
            TestAssert.True(preview.Detached.Source.Positions.SequenceEqual(mesh.Positions),
                "Detached glTF preview did not retain transformed source positions.");
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".bin")) File.Delete(path + ".bin"); }
    }

    private static void PskDecodePreservesRig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mfe-{Guid.NewGuid():N}.psk");
        try
        {
            var psk = new PSK
            {
                Points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
                Wedges =
                [
                    new() { PointIndex = 0, U = 0, V = 0 },
                    new() { PointIndex = 1, U = 1, V = 0 },
                    new() { PointIndex = 2, U = 0, V = 1 }
                ],
                Faces = [new() { WedgeIdx0 = 0, WedgeIdx1 = 1, WedgeIdx2 = 2, MatIndex = 0 }],
                Materials = [new() { Name = "Skin" }],
                Bones = [new LegendaryExplorerCore.Unreal.PSA.PSABone { Name = "root", ParentIndex = -1 }],
                Weights =
                [
                    new() { Point = 0, Bone = 0, Weight = 1 },
                    new() { Point = 1, Bone = 0, Weight = 1 },
                    new() { Point = 2, Bone = 0, Weight = 1 }
                ],
                VertexNormals = []
            };
            psk.ToFile(path);
            var mesh = new MorphFaceInterchangeService().ReadMesh(path);
            TestAssert.True(mesh.HasRig, "PSK skeleton and point weights were not retained.");
            TestAssert.Equal("root", mesh.Bones[0].Name);
            TestAssert.Equal(new BoneIndex4(0, 0, 0, 0), mesh.BoneIndices![0]);
            TestAssert.Near(1, mesh.BoneWeights![0].X, 0);
            var preview = new DetachedMeshPreviewLoadService(new HeadPreviewSceneFactory())
                .Load(MorphFaceGame.LE2, mesh);
            TestAssert.True(preview.Preview.EditingSession.CanEditBones,
                "Structurally valid detached PSK rig did not expose bone controls.");
            TestAssert.True(!preview.Preview.EditingSession.CanEditMorphFeatures,
                "Rigged detached PSK exposed morph controls.");
            TestAssert.True(preview.Preview.Scene.Meshes.Single().ApplySkinning,
                "Verified detached PSK rig did not enable renderer skinning.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void GlbDecodeRetainsSections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mfe-{Guid.NewGuid():N}.glb");
        try
        {
            WriteGlbFixture(path);
            var mesh = new MorphFaceInterchangeService().ReadMesh(path);
            TestAssert.Equal(3, mesh.Positions.Length);
            TestAssert.Equal(new Vector3(0, 0, 0), mesh.Positions[0]);
            TestAssert.Equal(3, mesh.Indices.Length);
            TestAssert.Equal("GLBFace", mesh.Sections.Single().MaterialName);
            TestAssert.True(mesh.TextureCoordinates is not null, "GLB TEXCOORD_0 was dropped.");
            TestAssert.True(mesh.NormalsWereGenerated, "GLB normal generation was not exercised.");
            TestAssert.True(!mesh.HasRig, "An unrigged GLB was reported as rigged.");
            var preview = new DetachedMeshPreviewLoadService(new HeadPreviewSceneFactory())
                .Load(MorphFaceGame.LE1, mesh);
            TestAssert.Equal(1, preview.Preview.Scene.Meshes.Single().Sections.Count);
            TestAssert.True(!preview.Preview.Scene.Meshes.Single().ApplySkinning,
                "Unrigged detached GLB enabled skinning.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void WriteGltfFixture(string path)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[]
                     {
                         0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f,
                         0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f
                     }) writer.Write(value);
            foreach (var value in new[]
                     {
                         0f, 0f, 1f, 0f, 0f, 1f,
                         0f, 0f, 0f, 1f, 1f, 0f
                     }) writer.Write(value);
            foreach (var value in new ushort[] { 0, 1, 2, 3, 4, 5 }) writer.Write(value);
        }
        var bytes = stream.ToArray();
        var document = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = bytes.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes) } },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 72 },
                new { buffer = 0, byteOffset = 72, byteLength = 48 },
                new { buffer = 0, byteOffset = 120, byteLength = 12 }
            },
            accessors = new[]
            {
                new { bufferView = 0, byteOffset = 0, componentType = 5126, count = 6, type = "VEC3" },
                new { bufferView = 1, byteOffset = 0, componentType = 5126, count = 6, type = "VEC2" },
                new { bufferView = 2, byteOffset = 0, componentType = 5123, count = 3, type = "SCALAR" },
                new { bufferView = 2, byteOffset = 6, componentType = 5123, count = 3, type = "SCALAR" }
            },
            materials = new[] { new { name = "Face" }, new { name = "Detail" } },
            meshes = new[] { new { primitives = new[]
            {
                new { attributes = new { POSITION = 0, TEXCOORD_0 = 1 }, indices = 2, material = 0 },
                new { attributes = new { POSITION = 0, TEXCOORD_0 = 1 }, indices = 3, material = 1 }
            } } },
            nodes = new[] { new { mesh = 0, translation = new[] { 1f, 2f, 3f }, scale = new[] { -2f, 2f, 2f } } },
            scenes = new[] { new { nodes = new[] { 0 } } },
            scene = 0
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document));
    }

    private static void WriteGlbFixture(string path)
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }) writer.Write(value);
            foreach (var value in new[] { 0f, 0f, 1f, 0f, 0f, 1f }) writer.Write(value);
            foreach (var value in new ushort[] { 0, 1, 2 }) writer.Write(value);
        }
        var json = JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = data.Length } },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 36 },
                new { buffer = 0, byteOffset = 36, byteLength = 24 },
                new { buffer = 0, byteOffset = 60, byteLength = 6 }
            },
            accessors = new[]
            {
                new { bufferView = 0, byteOffset = 0, componentType = 5126, count = 3, type = "VEC3" },
                new { bufferView = 1, byteOffset = 0, componentType = 5126, count = 3, type = "VEC2" },
                new { bufferView = 2, byteOffset = 0, componentType = 5123, count = 3, type = "SCALAR" }
            },
            materials = new[] { new { name = "GLBFace" } },
            meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0, TEXCOORD_0 = 1 }, indices = 2, material = 0 } } } },
            nodes = new[] { new { mesh = 0 } },
            scenes = new[] { new { nodes = new[] { 0 } } },
            scene = 0
        });
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        var jsonPadded = (jsonBytes.Length + 3) & ~3;
        var binBytes = data.ToArray();
        var binPadded = (binBytes.Length + 3) & ~3;
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x46546C67u); writer.Write(2u);
            writer.Write((uint)(12 + 8 + jsonPadded + 8 + binPadded));
            writer.Write((uint)jsonPadded); writer.Write(0x4E4F534Au);
            writer.Write(jsonBytes); for (var i = jsonBytes.Length; i < jsonPadded; i++) writer.Write((byte)0x20);
            writer.Write((uint)binPadded); writer.Write(0x004E4942u);
            writer.Write(binBytes); for (var i = binBytes.Length; i < binPadded; i++) writer.Write((byte)0);
        }
        File.WriteAllBytes(path, output.ToArray());
    }

    private static void PlayerTopologyRecognitionIsStructural()
    {
        var canonicalPositions = new[]
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            new Vector3(1, 1, 0), new Vector3(0, 1, 0)
        };
        var canonicalUvs = new[]
        {
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(1, 1), new Vector2(0, 1)
        };
        var canonicalSections = new[]
        {
            new MeshSectionTopology(0, 0, 0, 1),
            new MeshSectionTopology(1, 0, 3, 1)
        };
        var topology = new SkeletalMeshTopology(
            0, 4, 6, 2, canonicalSections, [], [], [], [], "fixture");
        var render = new SkeletalMeshRenderData(
            Enumerable.Repeat(Vector4.Zero, 4).ToArray(),
            canonicalUvs,
            Enumerable.Repeat(default(BoneIndex4), 4).ToArray(),
            Enumerable.Repeat(Vector4.Zero, 4).ToArray(),
            [0, 1, 2, 0, 2, 3],
            [null, null]);
        var canonical = new SkeletalMeshAsset(
            TestFixtures.CreateIdentity("Player.Base", "SkeletalMesh"),
            canonicalPositions,
            Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(),
            topology,
            render,
            Lods: [new SkeletalMeshLod(
                0,
                canonicalPositions,
                Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(),
                topology,
                render)]);

        // Imported order is canonical 2, 0, 3, 1. Unique UVs prove the
        // bijection without treating sculpted positions as identity evidence.
        var imported = new ImportedMeshAsset(
            "fixture.psk",
            [canonicalPositions[2], canonicalPositions[0], canonicalPositions[3], canonicalPositions[1]],
            null,
            null,
            [canonicalUvs[2], canonicalUvs[0], canonicalUvs[3], canonicalUvs[1]],
            [1, 3, 0, 1, 0, 2],
            [new ImportedMeshSection(0, "Skin", 0, 3), new ImportedMeshSection(1, "Eyes", 3, 3)],
            [],
            null,
            null,
            [2, 0, 3, 1]);
        var match = MorphMeshTopologyMatcher.Match(imported, canonical);
        TestAssert.True(match is not null, "A uniquely UV-mapped vertex reorder was not recognised.");
        TestAssert.True(match!.CanonicalOrderPositions.SequenceEqual(canonicalPositions),
            "Recognition did not restore canonical vertex order.");

        var unrelated = imported with { Indices = [1, 3, 0, 3, 0, 2] };
        TestAssert.Equal<MorphMeshTopologyMatch?>(null, MorphMeshTopologyMatcher.Match(unrelated, canonical));
    }
}
