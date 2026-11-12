using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

/// <summary>Focused package-adapter checks for Custom material evidence.</summary>
public static class CustomMaterialEvidenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("material evidence refreshes MIC values and pixels while retaining per-use texture roles", MaterialValuesAndPixelsFollowSourceChanges),
        new("BC7 preview decoding preserves every block mode and authored mip level", Bc7PixelsAndMipsArePreserved),
        new("material evidence preserves custom defaults and empty versus missing compiled support", CustomDefaultsAndCompiledEvidence),
        new("LE1 backported hair resolves anonymous texture overrides and defaults", Le1AnonymousHairDiffuse),
        new("LE3 Ssk 02 preview preserves its unused null material slot", Ssk02RetainsUnusedNullMaterialSlot),
        new("used null attachment material slots remain invalid", UsedNullAttachmentMaterialSlotsRemainInvalid),
    ];

    private static void Le1AnonymousHairDiffuse()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const string anonymous = "MaterialExpressionTextureSampleParameter2D_62";
        var path = Path.Combine(Path.GetTempPath(), $"MFE-AnonymousHair-{Guid.NewGuid():N}.pcc");
        try
        {
            using (var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE1))
            {
                ExportEntry Texture(string name, byte[] bgra)
                {
                    var texture = package.CreateExport(name, "Texture2D", indexed: false);
                    texture.WriteProperty(new EnumProperty(new NameReference("PF_A8R8G8B8"),
                        new NameReference("EPixelFormat"), MEGame.LE1, "Format"));
                    texture.WriteBinary(new UTexture2D { Mips = [new UTexture2D.Texture2DMipMap(bgra, 1, 1)] });
                    return texture;
                }

                var defaultTexture = Texture("DefaultAtlas", [11, 22, 33, 100]);
                var overrideTexture = Texture("StrandAtlas", [44, 55, 66, 120]);
                var explicitTexture = Texture("ExplicitAtlas", [77, 88, 99, 140]);
                var master = package.CreateExport("HMN_HED_PRO_ME3_MASTER_Hair_MAT", "Material", indexed: false);
                var binary = Material.Create();
                binary.SM3MaterialResource.ID = Guid.Parse("e7ee3c26-bad1-45ef-a40a-390b62aedd72");
                master.WriteBinary(binary);
                var expression = package.CreateExport("StrandParameter", "MaterialExpressionTextureSampleParameter2D", master, indexed: false);
                expression.WriteProperty(new NameProperty(anonymous, "ParameterName"));
                expression.WriteProperty(new ObjectProperty(defaultTexture, "Texture"));
                master.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(expression)], "Expressions"));
                var effects = package.CreateExport("HairEffects", "RvrEffectsMaterialUser", indexed: false);
                effects.WriteProperty(new ObjectProperty(master, "m_pBaseMaterial"));

                void Mic(string name, params (string Name, ExportEntry Texture)[] overrides)
                {
                    var mic = package.CreateExport(name, "MaterialInstanceConstant", indexed: false);
                    mic.WriteProperty(new ObjectProperty(effects, "Parent"));
                    if (overrides.Length > 0)
                        mic.WriteProperty(new ArrayProperty<StructProperty>(overrides.Select(value =>
                            new StructProperty("TextureParameterValue", false,
                                new NameProperty(value.Name, "ParameterName"),
                                new ObjectProperty(value.Texture, "ParameterValue"))).ToList(), "TextureParameterValues"));
                }
                Mic("Default_MIC");
                Mic("Override_MIC", (anonymous, overrideTexture));
                Mic("Explicit_MIC", (anonymous, overrideTexture), ("HAIR_Diff", explicitTexture));
                package.Save(path);
            }

            using var packageCache = new PackageCache();
            var installed = new InstalledGameAssetResolver(_ => new Dictionary<string, string>(), _ => null);
            var reader = new MorphFaceMaterialReader(packageCache, new GamePackageReferenceResolver(packageCache, installed));
            var reopened = PackageSourceCache.GetPackage(packageCache, path)!;
            ResolvedHeadMaterial Read(string name) => reader.ReadFaceAttachmentEvidence(
                reopened.FindExport(name, "MaterialInstanceConstant")!);
            void AssertDiffuse(ResolvedHeadMaterial material, byte[] rgba)
            {
                TestAssert.Equal(HeadMaterialFamily.Hair, material.Family);
                TestAssert.True(material.Textures.TryGetValue("HAIR_Diff", out var binding),
                    "The anonymous hair texture was not made available to the preview as HAIR_Diff.");
                var texture = binding!.Texture;
                TestAssert.True(texture.Rgba8.SequenceEqual(rgba), "The hair diffuse resolved the wrong texture pixels.");
                TestAssert.Equal(TextureRole.Diffuse, texture.Role);
                TestAssert.Equal(TextureColorSpace.Srgb, texture.ColorSpace);
                TestAssert.Equal(TextureAlphaPolicy.Translucency, texture.AlphaPolicy);
                TestAssert.True(material.Supports("HAIR_Diff", MaterialParameterKind.Texture), "The preview diffuse is not supported.");
            }
            var overridden = Read("Override_MIC");
            AssertDiffuse(overridden, [66, 55, 44, 120]);
            TestAssert.True(overridden.Textures[anonymous].Texture.Rgba8.SequenceEqual(new byte[] { 66, 55, 44, 120 }),
                "The original anonymous binding was not retained.");
            AssertDiffuse(Read("Default_MIC"), [33, 22, 11, 100]);
            AssertDiffuse(Read("Explicit_MIC"), [99, 88, 77, 140]);
        }
        finally { File.Delete(path); }
    }

    private static void Bc7PixelsAndMipsArePreserved()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var random = new Random(4701);
        var mips = new[] { 32, 16, 8, 4, 2, 1 }.Select(size =>
        {
            var bytes = new byte[((size + 3) / 4) * ((size + 3) / 4) * 16];
            random.NextBytes(bytes);
            for (var block = 0; block < bytes.Length / 16; block++)
            {
                var mode = block % 8;
                bytes[block * 16] = (byte)((bytes[block * 16] & ~((1 << (mode + 1)) - 1)) | (1 << mode));
            }
            return (Size: size, Bytes: bytes);
        }).ToArray();
        foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            var path = Path.Combine(Path.GetTempPath(), $"MFE-Bc7-{Guid.NewGuid():N}.pcc");
            try
            {
                using (var package = MEPackageHandler.CreateMemoryEmptyPackage(path, game))
                {
                    var texture = package.CreateExport("Pixels", "Texture2D", indexed: false);
                    texture.WriteProperty(new EnumProperty(new NameReference("PF_BC7"), new NameReference("EPixelFormat"), game, "Format"));
                    texture.WriteBinary(new UTexture2D { Mips = mips.Select(mip =>
                        new UTexture2D.Texture2DMipMap(mip.Bytes, mip.Size, mip.Size)).ToList() });
                    package.Save(path);
                }
                using var reader = new MorphFacePackageReader();
                var actual = reader.LoadTexture(path, "Pixels", HumanMaterialProfiles.Describe(
                    "HED_Diff", MaterialParameterKind.Texture, HeadMaterialFamily.Skin));
                TestAssert.Equal(mips.Length, actual.Mips!.Count);
                foreach (var (expectedMip, decoded) in mips.Zip(actual.Mips))
                {
                    var width = expectedMip.Size;
                    var height = expectedMip.Size;
                    var expected = LegendaryExplorerCore.Textures.Image.convertRawToARGB(expectedMip.Bytes,
                        ref width, ref height, LegendaryExplorerCore.Textures.PixelFormat.BC7);
                    for (var pixel = 0; pixel < expected.Length; pixel += 4)
                        (expected[pixel], expected[pixel + 2]) = (expected[pixel + 2], expected[pixel]);
                    TestAssert.True(decoded.Width == width && decoded.Height == height && decoded.Rgba8.SequenceEqual(expected),
                        $"{game} BC7 mip {expectedMip.Size} changed decoded pixels or dimensions.");
                }
                TestAssert.True(actual.Rgba8.SequenceEqual(actual.Mips[0].Rgba8), "BC7 top mip differs from its authored chain.");
            }
            finally { File.Delete(path); }
        }
    }

    private static void CustomDefaultsAndCompiledEvidence()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var kind in new[] { "stock", "empty", "missing" })
        {
            var path = Path.Combine(Path.GetTempPath(), $"MFE-MaterialEvidence-{Guid.NewGuid():N}.pcc");
            try
            {
                using (var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE2))
                {
                    var master = package.CreateExport("HMN_HED_LASH_Unlit_MASTER_MAT", "Material", indexed: false);
                    var binary = Material.Create();
                    binary.SM3MaterialResource.ID = kind == "stock"
                        ? Guid.Parse("4371b470-440b-4b66-affa-f9c2ab41dc3d") : Guid.NewGuid();
                    master.WriteBinary(binary);
                    var expression = package.CreateExport("Opacity", "MaterialExpressionScalarParameter", master, indexed: false);
                    expression.WriteProperty(new NameProperty("HED_Lash_Opac_Scalar", "ParameterName"));
                    expression.WriteProperty(new FloatProperty(0.25f, "DefaultValue"));
                    master.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(expression)], "Expressions"));
                    var mic = package.CreateExport("Custom_MIC", "MaterialInstanceConstant", indexed: false);
                    mic.WriteProperty(new ObjectProperty(master, "Parent"));
                    mic.WriteProperty(new ArrayProperty<StructProperty>([new StructProperty("ScalarParameterValue", false,
                        new NameProperty("HED_Lash_Opac_Scalar", "ParameterName"), new FloatProperty(0.75f, "ParameterValue"))], "ScalarParameterValues"));
                    if (kind == "empty")
                    {
                        var key = (StaticParameterSet)binary.SM3MaterialResource.ID;
                        var cache = ShaderCache.Create();
                        cache.MaterialShaderMaps.Add(key, new MaterialShaderMap
                        {
                            ID = binary.SM3MaterialResource.ID, FriendlyName = "Empty", StaticParameters = key,
                            Shaders = [], MeshShaderMaps = [], UniformPixelScalarExpressions = [],
                            UniformPixelVectorExpressions = [], Uniform2DTextureExpressions = [], UniformCubeTextureExpressions = [],
                            UniformVertexVectorExpressions = [], UniformVertexScalarExpressions = []
                        });
                        package.CreateExport("SeekFreeShaderCache", "ShaderCache", indexed: false).WriteBinary(cache);
                    }
                    package.Save(path);
                }
                using var packageCache = new PackageCache();
                var installed = new InstalledGameAssetResolver(_ => new Dictionary<string, string>(), _ => null);
                var reader = new MorphFaceMaterialReader(packageCache, new GamePackageReferenceResolver(packageCache, installed));
                var reopened = PackageSourceCache.GetPackage(packageCache, path)!;
                var masterEvidence = reader.ReadMaterialEvidence(reopened.FindExport("HMN_HED_LASH_Unlit_MASTER_MAT", "Material")!, false);
                TestAssert.Near(0.25f, masterEvidence.Scalars["HED_Lash_Opac_Scalar"], 0);
                TestAssert.Equal(kind != "missing", masterEvidence.HasCompiledParameterSupport);
                TestAssert.Equal(kind != "empty", masterEvidence.SupportedScalars.Contains("HED_Lash_Opac_Scalar"));
                var instanceEvidence = reader.ReadMaterialEvidence(reopened.FindExport("Custom_MIC", "MaterialInstanceConstant")!, false);
                TestAssert.Near(0.75f, instanceEvidence.Scalars["HED_Lash_Opac_Scalar"], 0);
                TestAssert.Equal(masterEvidence.HasCompiledParameterSupport, instanceEvidence.HasCompiledParameterSupport);
            }
            finally { File.Delete(path); }
        }
    }

    private static void MaterialValuesAndPixelsFollowSourceChanges()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var path = Path.Combine(Path.GetTempPath(), $"MFE-MaterialRefresh-{Guid.NewGuid():N}.pcc");
        void WriteSource(float opacity, byte blue)
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE3);
            var master = package.CreateExport("HMN_HED_LASH_Unlit_MASTER_MAT", "Material", indexed: false);
            master.WriteBinary(Material.Create());
            var expression = package.CreateExport("Opacity", "MaterialExpressionScalarParameter", master, indexed: false);
            expression.WriteProperty(new NameProperty("HED_Lash_Opac_Scalar", "ParameterName"));
            expression.WriteProperty(new FloatProperty(0.25f, "DefaultValue"));
            master.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(expression)], "Expressions"));
            var pixels = package.CreateExport("Pixels", "Texture2D", indexed: false);
            pixels.WriteProperty(new EnumProperty(new NameReference("PF_A8R8G8B8"),
                new NameReference("EPixelFormat"), MEGame.LE3, "Format"));
            pixels.WriteBinary(new UTexture2D { Mips = [new UTexture2D.Texture2DMipMap([blue, 20, 30, 255], 1, 1)] });
            var mic = package.CreateExport("Custom_MIC", "MaterialInstanceConstant", indexed: false);
            mic.WriteProperty(new ObjectProperty(master, "Parent"));
            mic.WriteProperty(new ArrayProperty<StructProperty>([new StructProperty("ScalarParameterValue", false,
                new NameProperty("HED_Lash_Opac_Scalar", "ParameterName"), new FloatProperty(opacity, "ParameterValue"))], "ScalarParameterValues"));
            mic.WriteProperty(new ArrayProperty<StructProperty>([new StructProperty("TextureParameterValue", false,
                new NameProperty("HED_Lash_Diff", "ParameterName"), new ObjectProperty(pixels, "ParameterValue"))], "TextureParameterValues"));
            package.Save(path);
        }
        try
        {
            WriteSource(0.5f, 10);
            using var reader = new MorphFacePackageReader();
            var identity = new AssetIdentity(path, "Custom_MIC", 0, "MaterialInstanceConstant");
            ResolvedHeadMaterial Read() => reader.LoadMaterialEvidence(identity);
            var first = Read();
            var original = first.Textures["HED_Lash_Diff"].Texture;
            TestAssert.True(original.Rgba8.SequenceEqual(new byte[] { 30, 20, 10, 255 }), "The source pixels were decoded incorrectly.");
            var normal = HumanMaterialProfiles.Describe("HED_Norm", MaterialParameterKind.Texture, HeadMaterialFamily.Skin);
            var reused = reader.LoadTexture(path, "Pixels", normal);
            TestAssert.True(ReferenceEquals(original.Rgba8, reused.Rgba8), "Different texture roles decoded the same pixels twice.");
            TestAssert.Equal(TextureRole.Normal, reused.Role);
            TestAssert.Equal(TextureColorSpace.Linear, reused.ColorSpace);
            TestAssert.Equal(original.Role, Read().Textures["HED_Lash_Diff"].Texture.Role);

            WriteSource(0.75f, 40);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
            var refreshed = Read();
            TestAssert.Equal(0.75f, refreshed.Scalars["HED_Lash_Opac_Scalar"]);
            TestAssert.Equal((byte)40, refreshed.Textures["HED_Lash_Diff"].Texture.Rgba8[2]);
            TestAssert.True(original.CacheKey != refreshed.Textures["HED_Lash_Diff"].Texture.CacheKey,
                "Source changes retained the old renderer texture identity.");
            TestAssert.Equal((byte)10, original.Rgba8[2]);
            TestAssert.Equal(0.5f, first.Scalars["HED_Lash_Opac_Scalar"]);

            WriteSource(1f, 70);
            reader.InvalidatePackage(path);
            var invalidated = Read();
            TestAssert.Equal(1f, invalidated.Scalars["HED_Lash_Opac_Scalar"]);
            TestAssert.Equal((byte)70, invalidated.Textures["HED_Lash_Diff"].Texture.Rgba8[2]);
        }
        finally { File.Delete(path); }
    }

    private static void Ssk02RetainsUnusedNullMaterialSlot()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cookedPath = LegendaryExplorerCoreRuntime.DefaultLe3CookedPath;
        var packagePath = string.IsNullOrWhiteSpace(cookedPath)
            ? null
            : Path.Combine(cookedPath, "BIOG_HMM_HIR_PRO_R.pcc");
        if (packagePath is null || !File.Exists(packagePath)) return;

        using var reader = new MorphFacePackageReader();
        var attachment = reader.LoadDetachedAttachment(
            packagePath, "Hair_SargeSpike.HMM_HIR_Ssk_02_MDL");

        TestAssert.True(attachment.Mesh.RenderData!.MaterialSlots.Count == 2,
            "Ssk_02 material slots were compacted and section indices would shift.");
        TestAssert.True(attachment.Mesh.RenderData.MaterialSlots[0] is null,
            "The source null slot was not preserved at material index 0.");
        TestAssert.True(attachment.Mesh.RenderData.MaterialSlots[1] is not null,
            "Ssk_02's active material slot did not resolve.");
        TestAssert.True(attachment.Materials.Materials.Count > 0,
            "The valid Ssk_02 material evidence was not loaded.");
        TestAssert.True(attachment.Mesh.Lods.All(lod =>
                lod.Topology.Sections.All(section => section.MaterialIndex == 1)),
            "A Ssk_02 LOD section no longer points at the active slot 1 material.");
    }

    private static void UsedNullAttachmentMaterialSlotsRemainInvalid()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cookedPath = LegendaryExplorerCoreRuntime.DefaultLe2CookedPath;
        var packagePath = string.IsNullOrWhiteSpace(cookedPath)
            ? null
            : Path.Combine(cookedPath, "BIOG_HMF_HIR_PRO.pcc");
        if (packagePath is null || !File.Exists(packagePath)) return;

        using var reader = new MorphFacePackageReader();
        var rejected = false;
        try
        {
            reader.ValidateDetachedAttachment(packagePath, "Mira.HMF_HIR_MIR_LOD0");
        }
        catch (InvalidDataException exception)
        {
            rejected = exception.Message.Contains("references null material slot 0",
                StringComparison.OrdinalIgnoreCase);
        }

        TestAssert.True(rejected,
            "A section that uses a null material slot was accepted as a loadable attachment.");
    }

}
