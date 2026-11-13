using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.Interchange;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

namespace MorphFaceEditor.Tests;

public static class MorphMeshMaterialExporterTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("mesh material glTF keeps geometry and exports current maps", GltfMaterials),
        new("mesh material PSK names follow each LOD section slot", PskMaterials)
    ];

    private static void GltfMaterials()
    {
        InTemporaryDirectory(dir =>
        {
            var (mesh, materials) = Fixture();
            var path = Path.Combine(dir, "Head.gltf");
            var bin = Path.Combine(dir, "Head.bin");
            var buffer = new byte[68];
            float[] values = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1];
            for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(i * 4, 4), values[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(60, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(62, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(64, 2), 2);
            File.WriteAllBytes(bin, buffer);
            File.WriteAllText(path, """
                {"asset":{"version":"2.0"},"buffers":[{"uri":"Head.bin","byteLength":68}],
                 "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36,"target":34962},
                                {"buffer":0,"byteOffset":36,"byteLength":24,"target":34962},
                                {"buffer":0,"byteOffset":60,"byteLength":6,"target":34963}],
                 "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},
                              {"bufferView":1,"componentType":5126,"count":3,"type":"VEC2"},
                              {"bufferView":2,"componentType":5123,"count":3,"type":"SCALAR"}],
                 "meshes":[{"primitives":[{"attributes":{"POSITION":0,"TEXCOORD_0":1},"indices":2}]}],
                 "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
                """);
            using var original = JsonDocument.Parse(File.ReadAllText(path));
            var originalPrimitive = original.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0];
            var attributes = originalPrimitive.GetProperty("attributes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
            var indices = originalPrimitive.GetProperty("indices").GetInt32();
            var textureCache = new Dictionary<string, string>();
            var output = MorphMeshMaterialExporter.Write("Head", mesh, materials, [path, bin], MorphMeshFormat.Gltf, textureCache);
            _ = ModelRoot.Load(path, new ReadSettings { Validation = ValidationMode.Strict });
            using var gltf = JsonDocument.Parse(File.ReadAllText(path));
            var primitive = gltf.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0];
            var exportedAttributes = primitive.GetProperty("attributes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
            TestAssert.True(attributes.Count == exportedAttributes.Count && attributes.All(p => exportedAttributes.TryGetValue(p.Key, out var value) && value == p.Value), "glTF geometry attributes changed.");
            TestAssert.Equal(indices, primitive.GetProperty("indices").GetInt32());
            TestAssert.True(File.ReadAllBytes(bin).SequenceEqual(buffer), "glTF buffer changed.");
            var root = gltf.RootElement;
            TestAssert.True(root.GetProperty("materials")[0].TryGetProperty("normalTexture", out _), "Normal map not linked.");
            using var metadata = JsonDocument.Parse(File.ReadAllText(output.Single(p => p.EndsWith(".materials.json"))));
            var material = metadata.RootElement.GetProperty("materials")[0];
            var normalIndex = root.GetProperty("materials")[0].GetProperty("normalTexture").GetProperty("index").GetInt32();
            var normalImageIndex = root.GetProperty("textures")[normalIndex].GetProperty("source").GetInt32();
            var normalPath = Uri.UnescapeDataString(root.GetProperty("images")[normalImageIndex].GetProperty("uri").GetString()!);
            using var normalImage = Image.Load<Rgba32>(Path.Combine(dir, normalPath));
            TestAssert.Equal(new Rgba32(128, 127, 255, 255), normalImage[0, 0]);
            TestAssert.True(material.GetProperty("textures").TryGetProperty("HED_Norm_02", out _), "Secondary normal map missing from metadata.");
            TestAssert.Equal(0.25f, material.GetProperty("scalars").GetProperty("Gloss").GetSingle());
            TestAssert.Equal(4f, material.GetProperty("vectors").GetProperty("Tint")[3].GetSingle());
            var map = material.GetProperty("textures").GetProperty("DiffuseOverride").GetProperty("file").GetString()!;
            var textureIndex = root.GetProperty("materials")[0].GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture").GetProperty("index").GetInt32();
            var imageIndex = root.GetProperty("textures")[textureIndex].GetProperty("source").GetInt32();
            TestAssert.Equal(map, Uri.UnescapeDataString(root.GetProperty("images")[imageIndex].GetProperty("uri").GetString()!));
            using var image = Image.Load<Rgba32>(Path.Combine(dir, map));
            TestAssert.Equal(new Rgba32(10, 20, 30, 40), image[0, 0]);
            TestAssert.True(material.GetProperty("textures").TryGetProperty("Extra", out _), "Additional map missing.");
            TestAssert.True(material.GetProperty("textures").GetProperty("Extra").GetProperty("file").GetString()!.EndsWith(".tga"), "Extra maps should use fast lossless TGA.");
            TestAssert.Equal(6, material.GetProperty("fixedCubeTexture").GetProperty("faces").EnumerateObject().Count());
            var sharedDirectory = Path.Combine(dir, "attachment");
            Directory.CreateDirectory(sharedDirectory);
            var sharedPath = Path.Combine(sharedDirectory, "Head.gltf");
            File.Copy(path, sharedPath); File.Copy(bin, Path.Combine(sharedDirectory, "Head.bin"));
            var encodedCount = textureCache.Count;
            var shared = MorphMeshMaterialExporter.Write("Head", mesh, materials, [sharedPath], MorphMeshFormat.Gltf, textureCache);
            _ = ModelRoot.Load(sharedPath, new ReadSettings { Validation = ValidationMode.Strict });
            TestAssert.Equal(encodedCount, textureCache.Count);
            foreach (var file in output.Where(p => p.EndsWith(".png") || p.EndsWith(".tga")))
                TestAssert.True(shared.Any(p => Path.GetFileName(p) == Path.GetFileName(file) &&
                    File.ReadAllBytes(p).SequenceEqual(File.ReadAllBytes(file))), "Shared texture was not retained beside the attachment.");
        });
    }

    private static void PskMaterials()
    {
        InTemporaryDirectory(dir =>
        {
            var (mesh, materials) = Fixture();
            var lod0 = Path.Combine(dir, "Head.psk"); var lod1 = Path.Combine(dir, "Head_Lod1.psk");
            WritePsk(lod0); WritePsk(lod1);
            var output = MorphMeshMaterialExporter.Write("Head", mesh, materials, [lod1, lod0], MorphMeshFormat.Psk);
            TestAssert.True(!output.Any(p => p.EndsWith(".png")) && output.Any(p => p.EndsWith(".tga")), "PSK maps should use TGA.");
            string Name(string path) => Encoding.ASCII.GetString(File.ReadAllBytes(path), 32, 64).TrimEnd('\0');
            var first = Name(lod0); var second = Name(lod1);
            TestAssert.True(first != second && first.Length <= 63 && second.Length <= 63, "LOD material names were not mapped safely.");
            TestAssert.True(output.Contains(Path.Combine(dir, first + ".mat")) && output.Contains(Path.Combine(dir, second + ".mat")), "Matching material companions missing.");
            TestAssert.True(File.ReadAllText(Path.Combine(dir, first + ".mat")).Contains("Other[0]="), "Additional texture not listed in .mat.");
            using var metadata = JsonDocument.Parse(File.ReadAllText(output.Single(p => p.EndsWith(".materials.json"))));
            var textures = metadata.RootElement.GetProperty("materials")[0].GetProperty("textures");
            var normalFile = textures.GetProperty("HED_Norm").GetProperty("file").GetString()!;
            TestAssert.True(textures.TryGetProperty("HED_Norm_02", out _), "Secondary normal map missing from metadata.");
            TestAssert.True(File.ReadAllLines(Path.Combine(dir, first + ".mat"))
                .Contains("Normal=" + Path.GetFileNameWithoutExtension(normalFile)), "PSK material selected the wrong normal map.");
            using var normalImage = Image.Load<Rgba32>(Path.Combine(dir, normalFile));
            TestAssert.Equal(new Rgba32(128, 128, 0, 255), normalImage[0, 0]);
            var lods = metadata.RootElement.GetProperty("lods").EnumerateArray().ToDictionary(lod => lod.GetProperty("lod").GetInt32());
            TestAssert.Equal(0, lods[0].GetProperty("sections")[0].GetProperty("materialSlot").GetInt32());
            TestAssert.Equal(1, lods[1].GetProperty("sections")[0].GetProperty("materialSlot").GetInt32());
            TestAssert.Equal(first, lods[0].GetProperty("sections")[0].GetProperty("material").GetString());
            TestAssert.Equal(second, lods[1].GetProperty("sections")[0].GetProperty("material").GetString());
        });
    }

    private static (SkeletalMeshAsset, ResolvedHeadMaterialSet) Fixture()
    {
        var a = new AssetIdentity("fixture.pcc", "Test.Skin", 1, "MaterialInstanceConstant");
        var b = new AssetIdentity("fixture.pcc", "Test.Hair", 2, "MaterialInstanceConstant");
        DecodedTextureAsset Texture(string name, TextureRole role, byte[] pixel) => new(new AssetIdentity("fixture.pcc", name, 3, "Texture2D"), 1, 1, pixel, "RGBA", role, TextureColorSpace.Linear, TextureAlphaPolicy.Mask, true, name);
        var maps = new Dictionary<string, MaterialTextureBinding>
        {
            ["DiffuseOverride"] = new("DiffuseOverride", Texture("Selected", TextureRole.Diffuse, [10, 20, 30, 40])),
            ["HED_Norm_02"] = new("HED_Norm_02", Texture("SecondaryNormal", TextureRole.Normal, [32, 200, 0, 255])),
            ["HED_Norm"] = new("HED_Norm", Texture("PrimaryNormal", TextureRole.Normal, [128, 128, 0, 255])),
            ["Extra"] = new("Extra", Texture("Extra", TextureRole.Mask, [2, 3, 4, 5]))
        };
        ResolvedHeadMaterial Material(AssetIdentity id) => new(id.InstancedPath, id, "Master", HeadMaterialFamily.Skin, HeadMaterialBlendMode.Masked, true,
            new Dictionary<string, float> { ["Gloss"] = .25f }, new Dictionary<string, Vector4> { ["Tint"] = new(1, 2, 3, 4) }, maps)
        {
            DefaultScalars = new Dictionary<string, float> { ["Gloss"] = .9f },
            DefaultTextures = new Dictionary<string, MaterialTextureBinding> { ["DiffuseOverride"] = new("DiffuseOverride", Texture("Default", TextureRole.Diffuse, [200, 201, 202, 203])) },
            FixedCubeTexture = new DecodedTextureCubeAsset(new AssetIdentity("fixture.pcc", "Cube", 4, "TextureCube"), 1, Enumerable.Range(0, 6).Select(i => new byte[] { (byte)i, 0, 0, 255 }).ToArray(), TextureColorSpace.Linear, "cube")
        };
        SkeletalMeshLod Lod(int index, int slot) => new(index, [], [], new SkeletalMeshTopology(index, 0, 0, 2, [new MeshSectionTopology(slot, 0, 0, 0)], [], [], [], [], ""), new SkeletalMeshRenderData([], [], [], [], [], [a, b]));
        var mesh = new SkeletalMeshAsset(new AssetIdentity("fixture.pcc", "Head", 5, "SkeletalMesh"), [Lod(0, 0), Lod(1, 1)]);
        return (mesh, new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial>(StringComparer.OrdinalIgnoreCase) { [MaterialIdentityKey.Create(a)] = Material(a), [MaterialIdentityKey.Create(b)] = Material(b) }));
    }

    private static void WritePsk(string path)
    {
        var bytes = new byte[32 + 88]; Encoding.ASCII.GetBytes("MATT0000").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), 88); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), 1); File.WriteAllBytes(path, bytes);
    }
    private static void InTemporaryDirectory(Action<string> action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "MFE-MaterialExport-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try { action(dir); } finally { Directory.Delete(dir, true); }
    }
}
