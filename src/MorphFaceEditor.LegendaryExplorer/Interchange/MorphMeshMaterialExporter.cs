using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;

namespace MorphFaceEditor.LegendaryExplorer.Interchange;

/// <summary>Writes material companions for already staged UModel meshes.</summary>
internal static class MorphMeshMaterialExporter
{
    internal static IReadOnlyList<string> Write(string meshName, SkeletalMeshAsset mesh,
        ResolvedHeadMaterialSet materials, IReadOnlyList<string> meshFiles, MorphMeshFormat format,
        Dictionary<string, string>? textureCache = null, MorphFaceGame game = MorphFaceGame.Unsupported)
    {
        var paths = meshFiles.Where(p => format == MorphMeshFormat.Gltf
            ? Path.GetExtension(p).Equals(".gltf", StringComparison.OrdinalIgnoreCase)
            : Path.GetExtension(p).Equals(".psk", StringComparison.OrdinalIgnoreCase) ||
              Path.GetExtension(p).Equals(".pskx", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length == 0) throw new InvalidDataException("No staged meshes were supplied.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(paths[0]))!;
        if (paths.Any(p => !Path.GetDirectoryName(Path.GetFullPath(p))!.Equals(directory, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Mesh files must share one directory.");
        var generated = new List<string>();
        var textures = textureCache ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var materialData = new Dictionary<string, MaterialData>(StringComparer.OrdinalIgnoreCase);
        var lodMappings = new List<object>();
        var seenLods = new HashSet<int>();
        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var lodIndex = name.Equals(meshName, StringComparison.OrdinalIgnoreCase) ? 0 :
                name.StartsWith(meshName + "_Lod", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name[(meshName.Length + 4)..], out var parsed) ? parsed : -1;
            var lod = mesh.FindLod(lodIndex);
            if (lod is null || !seenLods.Add(lodIndex) || lod.RenderData is null)
                throw new InvalidDataException("Cannot map exported mesh to its source LOD.");
            var sections = new List<MaterialData>();
            foreach (var section in lod.Topology.Sections)
            {
                if (section.MaterialIndex < 0 || section.MaterialIndex >= lod.RenderData.MaterialSlots.Count ||
                    lod.RenderData.MaterialSlots[section.MaterialIndex] is not { } identity || materials.Find(identity) is not { } material)
                    throw new InvalidDataException("Cannot resolve an exported mesh material slot.");
                var key = MaterialIdentityKey.Create(identity);
                if (!materialData.TryGetValue(key, out var data))
                {
                    var safeName = SafeName(identity.InstancedPath, key);
                    var diffuse = material.Textures.FirstOrDefault(p => p.Value.Texture.Role == TextureRole.Diffuse);
                    var preferredNormal = HeadMaterialCatalog.PrimaryNormalParameter(
                        material.Family, material.PreviewModel, game == MorphFaceGame.LE3);
                    bool Supported(string name) => name.StartsWith("__", StringComparison.Ordinal) ||
                        material.Supports(name, MaterialParameterKind.Texture);
                    var normal = material.Textures.FirstOrDefault(p =>
                        p.Key.Equals(preferredNormal, StringComparison.OrdinalIgnoreCase) && Supported(p.Key));
                    if (normal.Value is null)
                        normal = material.Textures.FirstOrDefault(p => p.Value.Texture.Role == TextureRole.Normal && Supported(p.Key));
                    var maps = new Dictionary<string, object>(StringComparer.Ordinal);
                    var textureFiles = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (parameter, binding) in material.Textures.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        var texture = binding.Texture;
                        var file = SaveTexture(texture.CacheKey, texture.Source.InstancedPath, texture.Width, texture.Height,
                            texture.Rgba8, png: format == MorphMeshFormat.Gltf && parameter == diffuse.Key);
                        textureFiles.Add(parameter, file);
                        maps.Add(parameter, new { file, source = texture.Source, role = texture.Role.ToString(), colorSpace = texture.ColorSpace.ToString(), alphaPolicy = texture.AlphaPolicy.ToString() });
                    }
                    object? Cube(DecodedTextureCubeAsset? cube)
                    {
                        if (cube is null) return null;
                        if (cube.Rgba8Faces.Count != 6) throw new InvalidDataException("Cube texture must have six faces.");
                        string[] names = ["posx", "negx", "posy", "negy", "posz", "negz"];
                        var faces = new Dictionary<string, string>();
                        for (var i = 0; i < 6; i++) faces.Add(names[i], SaveTexture(cube.CacheKey + "|" + names[i], cube.Source.InstancedPath + "_" + names[i], cube.Size, cube.Size, cube.Rgba8Faces[i]));
                        return new { source = cube.Source, faces };
                    }
                    var normalFile = normal.Value is null ? null : textureFiles[normal.Key];
                    if (format == MorphMeshFormat.Gltf && normal.Value is not null)
                    {
                        var texture = normal.Value.Texture;
                        normalFile = SaveTexture(texture.CacheKey + "|gltf-normal", texture.Source.InstancedPath + "_normal",
                            texture.Width, texture.Height, texture.Rgba8, png: true, convertNormal: true);
                    }
                    data = new MaterialData(safeName, material, textureFiles,
                        diffuse.Value is null ? null : textureFiles[diffuse.Key], normalFile,
                        new { name = safeName, source = material.Source, material.MasterMaterialName, family = material.Family.ToString(), blendMode = material.BlendMode.ToString(), material.TwoSided,
                            scalars = material.Scalars, vectors = material.Vectors.ToDictionary(p => p.Key, p => new[] { p.Value.X, p.Value.Y, p.Value.Z, p.Value.W }),
                            textures = maps, fixedCubeTexture = Cube(material.FixedCubeTexture), secondaryFixedCubeTexture = Cube(material.SecondaryFixedCubeTexture) });
                    materialData.Add(key, data);
                }
                sections.Add(data);
            }
            lodMappings.Add(new { lod = lodIndex, file = Path.GetFileName(path), sections = lod.Topology.Sections.Select((s, i) => new { section = i, materialSlot = s.MaterialIndex, material = sections[i].Name }).ToArray() });
            if (format == MorphMeshFormat.Gltf) PatchGltf(path, sections);
            else PatchPsk(path, sections);
        }
        if (format == MorphMeshFormat.Psk)
            foreach (var data in materialData.Values)
            {
                var lines = new List<string>();
                if (data.Diffuse is not null) lines.Add("Diffuse=" + Path.GetFileNameWithoutExtension(data.Diffuse));
                if (data.Normal is not null) lines.Add("Normal=" + Path.GetFileNameWithoutExtension(data.Normal));
                var other = data.TextureFiles.Where(p => p.Value != data.Diffuse && p.Value != data.Normal).ToArray();
                for (var i = 0; i < other.Length; i++) lines.Add($"Other[{i}]=" + Path.GetFileNameWithoutExtension(other[i].Value));
                var matPath = Path.Combine(directory, data.Name + ".mat");
                File.WriteAllLines(matPath, lines); generated.Add(matPath);
            }
        var metadataPath = Path.Combine(directory, meshName + ".materials.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new { mesh = mesh.Source, lods = lodMappings, materials = materialData.Values.Select(m => m.Metadata) }, JsonOptions));
        generated.Add(metadataPath);
        return generated;

        string SaveTexture(string key, string sourceName, int width, int height, byte[] pixels,
            bool png = false, bool convertNormal = false)
        {
            var cacheKey = key + (png ? ".png" : ".tga");
            if (textures.TryGetValue(cacheKey, out var existing))
            {
                var copy = Path.Combine(directory, Path.GetFileName(existing));
                if (!existing.Equals(copy, StringComparison.OrdinalIgnoreCase) && !generated.Contains(copy))
                {
                    File.Copy(existing, copy); generated.Add(copy);
                }
                return Path.GetFileName(existing);
            }
            if (width <= 0 || height <= 0 || pixels.LongLength != (long)width * height * 4) throw new InvalidDataException("Invalid decoded texture dimensions.");
            var filename = SafeName(sourceName, key) + (png ? ".png" : ".tga");
            var output = Path.Combine(directory, filename);
            if (convertNormal)
            {
                pixels = (byte[])pixels.Clone();
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var x = pixels[i] / 127.5f - 1f;
                    var y = pixels[i + 1] / 127.5f - 1f;
                    pixels[i + 1] = (byte)(255 - pixels[i + 1]);
                    pixels[i + 2] = (byte)Math.Clamp((int)MathF.Round((MathF.Sqrt(MathF.Max(0, 1 - x * x - y * y)) * .5f + .5f) * 255), 0, 255);
                    pixels[i + 3] = 255;
                }
            }
            using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
            // glTF requires PNG/JPEG. Other maps use fast, lossless 32-bit TGA.
            if (png) image.SaveAsPng(output, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed });
            else image.SaveAsTga(output, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel32, Compression = TgaCompression.None });
            generated.Add(output); textures.Add(cacheKey, output); return filename;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private sealed record MaterialData(string Name, ResolvedHeadMaterial Material, Dictionary<string, string> TextureFiles, string? Diffuse, string? Normal, object Metadata);
    private static string SafeName(string value, string identity)
    {
        var leaf = value.Split('.', '/', '\\').Last();
        var clean = new string(leaf.Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' ? c : '_').ToArray());
        if (clean.Length == 0) clean = "material";
        if (clean.Length > 45) clean = clean[..45];
        return clean + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
    }

    private static void PatchGltf(string path, IReadOnlyList<MaterialData> sections)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Invalid glTF JSON.");
        var meshes = root["meshes"]?.AsArray();
        if (meshes?.Count != 1 || meshes[0]?["primitives"] is not JsonArray primitives || primitives.Count != sections.Count)
            throw new InvalidDataException("glTF sections do not match source mesh.");
        var gltfMaterials = new JsonArray(); var images = new JsonArray(); var textures = new JsonArray();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        int Texture(string file)
        {
            var imageIndex = images.Count; images.Add(new JsonObject { ["uri"] = Uri.EscapeDataString(file) });
            var textureIndex = textures.Count; textures.Add(new JsonObject { ["source"] = imageIndex }); return textureIndex;
        }
        for (var i = 0; i < sections.Count; i++)
        {
            var data = sections[i];
            if (!byName.TryGetValue(data.Name, out var index))
            {
                index = gltfMaterials.Count; byName.Add(data.Name, index);
                var pbr = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 1 };
                if (data.Diffuse is not null) pbr["baseColorTexture"] = new JsonObject { ["index"] = Texture(data.Diffuse) };
                var mat = new JsonObject { ["name"] = data.Name, ["pbrMetallicRoughness"] = pbr, ["doubleSided"] = data.Material.TwoSided,
                    ["alphaMode"] = data.Material.BlendMode == HeadMaterialBlendMode.Masked ? "MASK" : data.Material.BlendMode is HeadMaterialBlendMode.Translucent or HeadMaterialBlendMode.Additive ? "BLEND" : "OPAQUE" };
                if (data.Normal is not null) mat["normalTexture"] = new JsonObject { ["index"] = Texture(data.Normal) };
                gltfMaterials.Add(mat);
            }
            primitives[i]!["material"] = index;
        }
        root["materials"] = gltfMaterials; root["images"] = images; root["textures"] = textures;
        File.WriteAllText(path, root.ToJsonString(JsonOptions));
    }

    private static void PatchPsk(string path, IReadOnlyList<MaterialData> sections)
    {
        var bytes = File.ReadAllBytes(path); var offset = 0; var found = false;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 32) throw new InvalidDataException("Truncated PSK chunk header.");
            var id = Encoding.ASCII.GetString(bytes, offset, 20).TrimEnd('\0');
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 24, 4));
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 28, 4));
            if (size < 0 || count < 0 || (long)offset + 32 + (long)size * count > bytes.Length) throw new InvalidDataException("Invalid PSK chunk size.");
            if (id == "MATT0000")
            {
                if (found || size < 64 || count != sections.Count) throw new InvalidDataException("PSK materials do not match source sections.");
                found = true;
                for (var i = 0; i < count; i++)
                {
                    var name = Encoding.ASCII.GetBytes(sections[i].Name);
                    if (name.Length > 63) throw new InvalidDataException("PSK material name too long.");
                    var field = bytes.AsSpan(offset + 32 + i * size, 64); field.Clear(); name.CopyTo(field);
                }
            }
            offset = checked(offset + 32 + size * count);
        }
        if (!found) throw new InvalidDataException("PSK material chunk missing.");
        File.WriteAllBytes(path, bytes);
    }
}
