using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class CustomMeshForcedExportTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Custom mesh PCC root and mesh retain ForcedExport and paths after reopen", SaveReopenRetainsAuthoredExportFlagsAndPaths)
    ];

    private static void SaveReopenRetainsAuthoredExportFlagsAndPaths()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(MorphFaceGame.LE1).State != TextureRegistryState.Ready)
        {
            throw new InvalidOperationException("The LE1 texture database is required for the Custom mesh ForcedExport regression.");
        }

        var option = new CustomMaterialTemplateCatalogService(new MorphFacePackageReader(), store)
            .Load(MorphFaceGame.LE1)
            .Options
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The LE1 Custom mesh material catalogue has no assignable options.");
        var source = CreateTriangleMesh();
        var workspace = new CustomMaterialWorkspace(source);
        workspace.Assign(0, option);
        var meshWriter = new FlagPreservingMeshWriter();
        var output = Path.Combine(Path.GetTempPath(), $"MFE-CustomForcedExport-{Guid.NewGuid():N}.pcc");
        try
        {
            _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source,
                workspace,
                new MorphFaceMaterialData([], [], []),
                MorphFaceGame.LE1,
                output,
                "ForcedExportRegressionMesh",
                meshWriter,
                store.Read(MorphFaceGame.LE1).Candidates));

            using var reopened = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            var root = reopened.FindExport("MorphFaceEditor", "Package");
            var mesh = reopened.FindExport("MorphFaceEditor.ForcedExportRegressionMesh", "SkeletalMesh");
            TestAssert.True(root is not null, "The authored MorphFaceEditor package root lost its intended path.");
            TestAssert.True(mesh is not null, "The authored SkeletalMesh lost its intended path.");
            TestAssert.True(root!.IsForcedExport, "The authored package root lost ForcedExport after reopening.");
            TestAssert.True(mesh!.IsForcedExport, "The authored SkeletalMesh lost ForcedExport after reopening.");
            TestAssert.Equal(meshWriter.ExpectedRootFlags, root.ExportFlags);
            TestAssert.Equal(meshWriter.ExpectedMeshFlags, mesh.ExportFlags);
            TestAssert.Equal("MorphFaceEditor", root.InstancedFullPath);
            TestAssert.Equal("MorphFaceEditor.ForcedExportRegressionMesh", mesh.InstancedFullPath);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static ImportedMeshAsset CreateTriangleMesh() => new(
        "custom-forced-export-regression.gltf",
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(),
        Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), 3).ToArray(),
        [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
        [0, 1, 2],
        [new ImportedMeshSection(0, "Material", 0, 3)],
        [], null, null, [0, 1, 2]);

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
