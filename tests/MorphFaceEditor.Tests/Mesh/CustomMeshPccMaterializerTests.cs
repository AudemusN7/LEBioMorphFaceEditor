using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class CustomMeshPccMaterializerTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("custom PCC retains eight shared slots and authored export flags after reopen", EightSlotsSurvivePccSave),
    ];

    private static void EightSlotsSurvivePccSave()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const MorphFaceGame game = MorphFaceGame.LE1;
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        using var reader = new MorphFacePackageReader();
        var option = new CustomMaterialTemplateCatalogService(reader, store).Load(game).Options
            .First(value => value.Family == HeadMaterialFamily.Skin &&
                value.Id.Contains("human-female", StringComparison.OrdinalIgnoreCase));
        var source = CreateSlotMesh("eight-slots.gltf", 8);
        var workspace = CustomMaterialWorkspace.Create(source).Workspace!;
        foreach (var slot in workspace.UsedSlots) workspace.Assign(slot, option);
        var meshWriter = new FlagPreservingMeshWriter();
        var output = Path.Combine(Path.GetTempPath(), $"MFE-EightSlots-{Guid.NewGuid():N}.pcc");
        try
        {
            var saved = new CustomMeshPccMaterializer().Save(new(source, workspace,
                new MorphFaceMaterialData([], [], []), game, output, "EightSlots",
                meshWriter, store.Read(game).Candidates));
            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            PackageIntegrity.Verify(package);
            var meshExport = package.FindExport(saved.MeshPath, "SkeletalMesh");
            var root = package.FindExport("MorphFaceEditor", "Package");
            TestAssert.True(meshExport is not null && root is not null &&
                meshExport.IsForcedExport && root.IsForcedExport,
                "The authored Custom package root or mesh lost its identity or ForcedExport flag.");
            TestAssert.Equal("MorphFaceEditor.EightSlots", meshExport!.InstancedFullPath);
            TestAssert.Equal(meshWriter.ExpectedRootFlags, root!.ExportFlags);
            TestAssert.Equal(meshWriter.ExpectedMeshFlags, meshExport.ExportFlags);
            var mesh = meshExport.GetBinaryData<SkeletalMesh>();
            TestAssert.Equal(1, saved.Materials.Count);
            TestAssert.Equal(8, mesh.Materials.Length);
            foreach (var material in mesh.Materials)
                TestAssert.Equal(saved.Materials.Single().MaterialPath, package.GetUExport(material).InstancedFullPath);
            TestAssert.True(mesh.LODModels[0].Sections.Select(section => (int)section.MaterialIndex)
                .Order().SequenceEqual(Enumerable.Range(0, 8)), "Saved mesh lost an assigned section.");
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private static ImportedMeshAsset CreateSlotMesh(string sourcePath, int slotCount)
    {
        var vertexCount = slotCount * 3;
        return new ImportedMeshAsset(
            sourcePath,
            Enumerable.Range(0, vertexCount).Select(index => new Vector3(index % 3, index / 3, 0)).ToArray(),
            Enumerable.Repeat(Vector3.UnitZ, vertexCount).ToArray(),
            Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), vertexCount).ToArray(),
            Enumerable.Repeat(Vector2.Zero, vertexCount).ToArray(),
            Enumerable.Range(0, vertexCount).ToArray(),
            Enumerable.Range(0, slotCount)
                .Select(index => new ImportedMeshSection(index, $"Material {index}", index * 3, 3))
                .ToArray(),
            [],
            null,
            null,
            Enumerable.Range(0, vertexCount).ToArray());
    }

    private sealed class FlagPreservingMeshWriter : ICustomMeshBinaryWriter
    {
        private readonly ImportedSkeletalMeshPackageWriter _inner = new();

        public UnrealFlags.EExportFlags ExpectedRootFlags { get; private set; }
        public UnrealFlags.EExportFlags ExpectedMeshFlags { get; private set; }

        public ExportEntry Write(
            IMEPackage package,
            ExportEntry packageRoot,
            ImportedMeshAsset source,
            string meshName,
            IReadOnlyList<CustomMeshMaterialReference> materials)
        {
            packageRoot.ExportFlags |= UnrealFlags.EExportFlags.ScriptPatcherExport;
            var mesh = _inner.Write(package, packageRoot, source, meshName, materials);
            mesh.ExportFlags |= UnrealFlags.EExportFlags.ScriptPatcherExport;
            ExpectedRootFlags = packageRoot.ExportFlags;
            ExpectedMeshFlags = mesh.ExportFlags;
            return mesh;
        }
    }
}
