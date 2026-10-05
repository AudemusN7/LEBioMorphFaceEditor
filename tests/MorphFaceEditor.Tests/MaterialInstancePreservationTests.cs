using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.Tests;

internal static class MaterialInstancePreservationTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("authored MIC keeps its values when an official MIC shares its path", AuthoredInstanceWins),
        new("edited stock MIC is renamed and relinked through PCC graph save reopen", () => RoundTrip(false)),
        new("edited stock MIC is renamed through PCC structure-preserving export", () => RoundTrip(true)),
        new("unchanged stock MIC copies keep their canonical identity despite parameter GUID differences", UnchangedInstanceKeepsName),
        new("cooked lighting GUID differences do not rename an unchanged stock MIC", LightingCacheDoesNotRename),
        new("installed LE1 Afro MIC keeps its name across official cooked copies", InstalledAfroKeepsName)
    ];

    private const string MicPath = "BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_MAT_1a";
    private const string BasePath = "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HED_PRO_MASTER_HAIR_MAT";
    private const string TexturePath = "BIOG_MFE_CustomTextures.Hair_Diff";
    private const string MeshPath = "BIOG_MFE_CustomMeshes.Hair";

    private static void AuthoredInstanceWins() => WithDonors((official, authored, resolver, _) =>
    {
        using var cache = new PackageCache();
        var actual = new GamePackageReferenceResolver(cache, resolver).Require(
            authored.FindExport(MicPath, "MaterialInstanceConstant")!, "authored MIC");
        TestAssert.Equal(0.75f, Scalar(actual));
        TestAssert.Equal(authored.FilePath, actual.FileRef.FilePath);
    });

    private static void UnchangedInstanceKeepsName() => WithDonors((official, authored, resolver, outputPath) =>
    {
        var mic = authored.FindExport(MicPath, "MaterialInstanceConstant")!;
        mic.WriteProperty(new FloatProperty(0.25f, "MFE_CustomScalar"));
        var stock = official.FindExport(MicPath, "MaterialInstanceConstant")!;
        WriteDynamicParameters(stock, 0);
        WriteDynamicParameters(mic, 1);
        official.Save(official.FilePath);
        var authoredData = mic.Data.ToArray();
        var officialData = stock.Data.ToArray();
        using var cache = new PackageCache();
        TestAssert.True(!MaterialInstanceSourcePolicy.OverridesOfficialInstance(mic, resolver, cache),
            "Different reference indices or parameter GUIDs made an unchanged stock MIC look edited.");
        TestAssert.True(mic.Data.SequenceEqual(authoredData) && stock.Data.SequenceEqual(officialData),
            "Donor comparison changed the original MIC data.");
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("MFE-UnchangedMic.pcc", MEGame.LE1);
        _ = PccPackageWorkflow.ImportDependencyGraph(destination,
            authored.FindExport(MeshPath, "SkeletalMesh")!, null, null, false,
            targetPathOverride: MeshPath, installedAssets: resolver);
        TestAssert.True(destination.FindExport(MicPath, "MaterialInstanceConstant") is not null,
            "An unchanged stock instance unnecessarily received a new export name.");
        var scalars = mic.GetProperty<ArrayProperty<StructProperty>>("ScalarParameterValues")!;
        scalars[0].GetProp<FloatProperty>("ParameterValue")!.Value = 0.75f;
        mic.WriteProperty(scalars);
        TestAssert.True(MaterialInstanceSourcePolicy.OverridesOfficialInstance(mic, resolver, cache),
            "Ignoring parameter GUIDs also hid a real scalar parameter edit.");
    });

    private static void WriteDynamicParameters(ExportEntry mic, int guidValue)
    {
        StructProperty Guid() => new("Guid", new PropertyCollection
        {
            new IntProperty(guidValue, "A"), new IntProperty(guidValue, "B"),
            new IntProperty(guidValue, "C"), new IntProperty(guidValue, "D")
        }, "ExpressionGUID", true);

        mic.WriteProperty(new ArrayProperty<StructProperty>(
        [new StructProperty("TextureParameterValue", false, Guid(),
            new NameProperty("Diffuse", "ParameterName"),
            new ObjectProperty(mic.FileRef.FindExport(TexturePath, "Texture2D")!, "ParameterValue"))],
            "TextureParameterValues"));
        mic.WriteProperty(new ArrayProperty<StructProperty>(
        [new StructProperty("VectorParameterValue", false, Guid(),
            new NameProperty("Tint", "ParameterName"),
            new StructProperty("LinearColor", new PropertyCollection
            {
                new FloatProperty(0.1f, "R"), new FloatProperty(0.2f, "G"),
                new FloatProperty(0.3f, "B"), new FloatProperty(1f, "A")
            }, "ParameterValue", true))], "VectorParameterValues"));
        mic.WriteProperty(new ArrayProperty<StructProperty>(
        [new StructProperty("ScalarParameterValue", false, Guid(),
            new NameProperty("Roughness", "ParameterName"), new FloatProperty(0.25f, "ParameterValue"))],
            "ScalarParameterValues"));
    }

    private static void RoundTrip(bool preserveStructure) => WithDonors((official, authored, resolver, output) =>
    {
        using (var destination = MEPackageHandler.CreateMemoryEmptyPackage(output, MEGame.LE1))
        {
            // A pre-existing name must survive; only the new MIC leaf is renamed.
            var occupiedParent = PackageIntegrity.EnsurePackagePath(destination, MicPath + "_MFE", "MaterialInstanceConstant");
            var occupied = destination.CreateExport("HMM_HAT_Cap_MAT_1a_MFE", "MaterialInstanceConstant", occupiedParent, indexed: false);
            occupied.WriteBinary(MaterialInstance.Create());
            occupied.WriteProperty(new FloatProperty(99f, "MFE_CustomScalar"));
            var sourceMesh = authored.FindExport(MeshPath, "SkeletalMesh")!;
            var mesh = preserveStructure
                ? PccPackageWorkflow.ImportRootPreservingStructure(destination, sourceMesh, installedAssets: resolver)
                : PccPackageWorkflow.ImportDependencyGraph(destination, sourceMesh, null, null, false,
                    targetPathOverride: MeshPath, installedAssets: resolver);
            var copiedMic = destination.GetUExport(mesh.GetBinaryData<SkeletalMesh>().Materials.Single());
            TestAssert.Equal(MicPath + "_MFE2", copiedMic.InstancedFullPath);
            TestAssert.Equal(0.75f, Scalar(copiedMic));
            TestAssert.Equal(BasePath, copiedMic.GetProperty<ObjectProperty>("Parent")!.ResolveToEntry(destination)!.InstancedFullPath);
            TestAssert.Equal(TexturePath, copiedMic.GetProperty<ObjectProperty>("MFE_CustomTexture")!.ResolveToEntry(destination)!.InstancedFullPath);
            destination.Save(output);
        }
        using var reopened = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
        PackageIntegrity.Verify(reopened);
        var savedMesh = reopened.FindExport(MeshPath, "SkeletalMesh")!;
        var savedMic = reopened.GetUExport(savedMesh.GetBinaryData<SkeletalMesh>().Materials.Single());
        TestAssert.Equal(MicPath + "_MFE2", savedMic.InstancedFullPath);
        TestAssert.Equal(0.75f, Scalar(savedMic));
        TestAssert.Equal(99f, Scalar(reopened.FindExport(MicPath + "_MFE", "MaterialInstanceConstant")!));
        TestAssert.True(reopened.FindExport(MicPath, "MaterialInstanceConstant") is null,
            "The authored edited instance was also copied at the stock name.");
        TestAssert.Equal(TexturePath, savedMic.GetProperty<ObjectProperty>("MFE_CustomTexture")!.ResolveToEntry(reopened)!.InstancedFullPath);
        using var second = MEPackageHandler.CreateMemoryEmptyPackage("MFE-MicSecondExport.pcc", MEGame.LE1);
        var secondMesh = PccPackageWorkflow.ImportDependencyGraph(second, savedMesh, null, null, false,
            targetPathOverride: MeshPath, installedAssets: resolver);
        var secondMic = second.GetUExport(secondMesh.GetBinaryData<SkeletalMesh>().Materials.Single());
        TestAssert.Equal(MicPath + "_MFE2", secondMic.InstancedFullPath);
        TestAssert.Equal(0.75f, Scalar(secondMic));
    });

    private static void LightingCacheDoesNotRename() => WithDonors((official, authored, resolver, _) =>
    {
        var mic = authored.FindExport(MicPath, "MaterialInstanceConstant")!;
        var stock = official.FindExport(MicPath, "MaterialInstanceConstant")!;
        mic.WriteProperty(new FloatProperty(0.25f, "MFE_CustomScalar"));
        foreach (var name in new[] { "LightingGuid", "ParentLightingGuid" })
        {
            stock.WriteProperty(new StructProperty("Guid", new PropertyCollection
                { new IntProperty(1, "A"), new IntProperty(2, "B"), new IntProperty(3, "C"), new IntProperty(4, "D") }, name, true));
            mic.WriteProperty(new StructProperty("Guid", new PropertyCollection
                { new IntProperty(5, "A"), new IntProperty(6, "B"), new IntProperty(7, "C"), new IntProperty(8, "D") }, name, true));
        }
        official.Save(official.FilePath);
        using var cache = new PackageCache();
        TestAssert.True(!MaterialInstanceSourcePolicy.OverridesOfficialInstance(mic, resolver, cache),
            "Baked-lighting bookkeeping was treated as an authored MIC parameter change.");
        mic.WriteProperty(new FloatProperty(0.8f, "MFE_CustomScalar"));
        TestAssert.True(MaterialInstanceSourcePolicy.OverridesOfficialInstance(mic, resolver, cache),
            "Ignoring cache GUIDs also hid a real MIC parameter edit.");
    });

    private static void InstalledAfroKeepsName()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.OpenMEPackage(
            InstalledGameAssetResolver.ResolvePackage(MorphFaceGame.LE1, "BIOG_HMF_HIR_PRO.pcc"), forceLoadFromDisk: true);
        var mic = package.FindExport("Afro.HMF_HIR_Afr_MAT_1a", "MaterialInstanceConstant")!;
        using var cache = new PackageCache();
        TestAssert.True(!MaterialInstanceSourcePolicy.OverridesOfficialInstance(mic, new InstalledGameAssetResolver(), cache),
            "Official Afro copies with equivalent qualified texture paths still look edited.");
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("MFE-AfroMic.pcc", MEGame.LE1);
        var copied = PccPackageWorkflow.ImportDependencyGraph(destination, mic, null, null, false,
            targetPathOverride: "BIOG_HMF_HIR_PRO.Afro.HMF_HIR_Afr_MAT_1a");
        TestAssert.Equal("BIOG_HMF_HIR_PRO.Afro.HMF_HIR_Afr_MAT_1a", copied.InstancedFullPath);
    }

    private static float Scalar(ExportEntry mic) => mic.GetProperty<FloatProperty>("MFE_CustomScalar")!.Value;

    private static void WithDonors(Action<IMEPackage, IMEPackage, InstalledGameAssetResolver, string> test)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-MicPreservation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var official = CreateDonor(Path.Combine(directory, "Official.pcc"), 0.25f, false);
            using var authored = CreateDonor(Path.Combine(directory, "Authored.pcc"), 0.75f, true);
            var snapshot = new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
                TextureCatalogGame.LE1, DateTimeOffset.UtcNow, 1, [])
            {
                Assets = [new InstalledAssetOccurrence(MicPath, "MaterialInstanceConstant",
                    official.FilePath, official.FindExport(MicPath, "MaterialInstanceConstant")!.UIndex,
                    0, TextureCatalogOrigin.BaseGame)],
                OfficialPackagePaths = [official.FilePath]
            };
            var resolver = new InstalledGameAssetResolver(_ => new Dictionary<string, string>(), _ => snapshot);
            test(official, authored, resolver, Path.Combine(directory, "Output.pcc"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IMEPackage CreateDonor(string path, float scalar, bool extraExport)
    {
        var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE1);
        if (extraExport) package.CreatePackageExport("Unrelated");
        var baseMaterial = CreateExport(package, BasePath, "Material");
        baseMaterial.WriteBinary(Material.Create());
        var texture = CreateExport(package, TexturePath, "Texture2D");
        texture.WriteBinary(UTexture2D.Create());
        var mic = CreateExport(package, MicPath, "MaterialInstanceConstant");
        mic.WriteBinary(MaterialInstance.Create());
        mic.WriteProperty(new ObjectProperty(baseMaterial, "Parent"));
        mic.WriteProperty(new FloatProperty(scalar, "MFE_CustomScalar"));
        mic.WriteProperty(new ObjectProperty(texture, "MFE_CustomTexture"));
        var mesh = CreateExport(package, MeshPath, "SkeletalMesh");
        var binary = SkeletalMesh.Create();
        binary.Materials = [mic.UIndex];
        mesh.WriteBinary(binary);
        package.Save(path);
        return package;
    }

    private static ExportEntry CreateExport(IMEPackage package, string path, string className)
    {
        var parent = PackageIntegrity.EnsurePackagePath(package, path, className);
        var export = package.CreateExport(path[(path.LastIndexOf('.') + 1)..], className, parent, indexed: false);
        PccAssetPathPolicy.NormalizeMaterializedAsset(export);
        return export;
    }
}
