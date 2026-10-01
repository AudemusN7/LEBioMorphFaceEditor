using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class HeadHairTextureOwnershipTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("hair mesh maps are absent from a saved BMO while scalp maps and MIC textures survive",
            () => RoundTrip(exportNew: false)),
        new("hair mesh maps are absent from an exported BMO while scalp maps and MIC textures survive",
            () => RoundTrip(exportNew: true))
    ];

    private static void RoundTrip(bool exportNew)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "MFE-HairOwnership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "Source.pcc");
            const string rootPath = "BIOG_MFE_HairOwnership";
            var scalpNames = new[] { "HED_Scalp_Diff", "HED_Scalp_Norm", "HED_Scalp_Spec", "HED_Tang" };
            var hairNames = new[] { "HAIR_Diff", "hair_addn_diff", "HAIR_Norm", "HAIR_CustomMask", "__PROShort01_Diffuse" };
            MorphFaceDocument draft;
            using (var source = MEPackageHandler.CreateMemoryEmptyPackage(sourcePath, MEGame.LE1))
            {
                var root = source.CreatePackageExport(rootPath);
                var texture = source.CreateExport("CustomHairTexture", "Texture2D", root, indexed: false);
                texture.WriteBinary(UTexture2D.Create());
                var mic = source.CreateExport("CustomHair_MIC", "MaterialInstanceConstant", root, indexed: false);
                mic.WriteBinary(MaterialInstance.Create());
                mic.WriteProperty(new ArrayProperty<StructProperty>([new StructProperty("TextureParameterValue",
                    new PropertyCollection { new NameProperty("HAIR_Diff", "ParameterName"),
                        new ObjectProperty(texture, "ParameterValue") }, "None", false)], "TextureParameterValues"));
                var mesh = source.CreateExport("BaseHead", "SkeletalMesh", root, indexed: false);
                var meshBinary = SkeletalMesh.Create();
                meshBinary.Materials = [mic.UIndex];
                mesh.WriteBinary(meshBinary);
                var face = source.CreateExport("Face", "BioMorphFace", root, indexed: false);
                var bmo = source.CreateExport("MaterialOverrides", "BioMaterialOverride", face, indexed: false);
                bmo.WriteProperty(new ArrayProperty<StructProperty>(scalpNames.Concat(hairNames).Select(name =>
                    new StructProperty("TextureParameter", new PropertyCollection {
                        new NameProperty(name, "nName"), new ObjectProperty(texture, "m_pTexture") }, "None", false)),
                    "m_aTextureOverrides"));
                face.WriteProperty(new ObjectProperty(bmo, "m_oMaterialOverrides"));
                face.WriteProperty(new ObjectProperty(mesh, "m_oBaseHead"));
                face.WriteBinary(new BioMorphFace { LODs = [[Vector3.Zero]] });
                source.Save(sourcePath);
                // The reader must ignore stale BMO hair maps before resolving the live materials.
                using var cache = new PackageCache();
                var read = new MorphFaceMaterialReader(cache, new GamePackageReferenceResolver(cache))
                    .Read(face, [], []).Overrides;
                TestAssert.Equal(scalpNames.Length, read.Textures.Count);
                var textureIdentity = MorphFacePackageReader.ToIdentity(texture)!;
                draft = new MorphFaceDocument(MorphFacePackageReader.ToIdentity(face)!,
                    PackageFingerprint.Capture(sourcePath), MorphFacePackageReader.ToIdentity(mesh), null, [], [],
                    read with { Textures = scalpNames.Concat(hairNames).Select(name =>
                        new TextureMaterialOverride(name, textureIdentity)).ToArray() }, [[Vector3.Zero]], []);
            }
            var output = exportNew ? Path.Combine(directory, "Export.pcc") : sourcePath;
            var writer = new MorphFacePackageWriter();
            if (exportNew) writer.SaveMorphToPackage(draft, sourcePath, output, createNewPackage: true);
            else writer.SaveExisting(draft);
            using var reopened = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            var bmoSaved = reopened.FindExport(rootPath + ".Face.MaterialOverrides", "BioMaterialOverride")!;
            var saved = bmoSaved.GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides")!;
            TestAssert.True(saved.Select(item => item.GetProp<NameProperty>("nName")!.Value.Instanced)
                .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(scalpNames),
                "BMO output lost scalp maps or retained a mesh-owned hair map.");
            var savedMic = reopened.FindExport(rootPath + ".CustomHair_MIC", "MaterialInstanceConstant")!;
            var micTexture = savedMic.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")!
                .Single().GetProp<ObjectProperty>("ParameterValue")!.ResolveToEntry(reopened)!;
            TestAssert.Equal(rootPath + ".CustomHairTexture", micTexture.InstancedFullPath);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
