using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class CompletedPccCloneTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("completed PCC clone keeps referenced cycles and mixed ancestry but excludes disconnected islands", CloneClosure),
        new("completed PCC clone preserves retained texture intent and rejects a lost saved cycle edge", CloneTextureIntent)
    ];

    private static void CloneClosure()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var stage = MEPackageHandler.CreateMemoryEmptyPackage("CompletedStage.pcc", MEGame.LE3);
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("CompletedOutput.pcc", MEGame.LE3);
        var native = stage.CreatePackageImport("Native");
        var group = stage.CreatePackageExport("Group", native);
        var first = stage.CreateExport("First", "Object", group, indexed: false);
        var second = stage.CreateExport("Second", "Object", group, indexed: false);
        first.WriteProperties([new ObjectProperty(second, "Next"), new ObjectProperty(group, "Group")]);
        second.WriteProperty(new ObjectProperty(first, "Next"));
        var unused = stage.CreateExport("Unused", "Object", group, indexed: false);
        var unusedChild = stage.CreateExport("UnusedChild", "Object", unused, indexed: false);
        unused.WriteProperty(new ObjectProperty(unusedChild, "Next"));
        unusedChild.WriteProperty(new ObjectProperty(unused, "Next"));

        var root = PccPackageWorkflow.CloneCompletedRoot(destination, first);
        TestAssert.Equal("Native.Group.First", root.InstancedFullPath);
        TestAssert.True(destination.FindEntry("Native", "Package") is ImportEntry, "Imported parent was promoted.");
        var copiedSecond = (ExportEntry)root.GetProperty<ObjectProperty>("Next")!.ResolveToEntry(destination)!;
        TestAssert.Equal(root.UIndex, copiedSecond.GetProperty<ObjectProperty>("Next")!.Value);
        TestAssert.True(destination.FindEntry("Native.Group.Unused") is null, "Disconnected package siblings were cloned.");
        TestAssert.True(destination.FindEntry("Native.Group.Unused.UnusedChild") is null, "Disconnected cycle was cloned.");
        TestAssert.Equal(3, destination.ExportCount); // Group plus the two referenced objects.
    }

    private static void CloneTextureIntent()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var output = Path.Combine(Path.GetTempPath(), $"MFE-CompletedClone-{Guid.NewGuid():N}.pcc");
        PccDependencyGraphSnapshot snapshot;
        try
        {
            using (var stage = MEPackageHandler.CreateMemoryEmptyPackage("CompletedStage.pcc", MEGame.LE1))
            using (var destination = MEPackageHandler.CreateMemoryEmptyPackage(output, MEGame.LE1))
            {
                var group = stage.CreatePackageExport("NativeTextures");
                var selected = stage.CreateExport("Selected", "Texture2D", group, indexed: false);
                selected.WriteBinary(UTexture2D.Create());
                var discarded = stage.CreateExport("Discarded", "Texture2D", group, indexed: false);
                discarded.WriteBinary(UTexture2D.Create());
                var root = stage.CreateExport("Root", "Object", group, indexed: false);
                root.WriteProperty(new ObjectProperty(selected, "Texture"));
                root.WriteProperty(new ObjectProperty(root, "Next"));
                PccPackageWorkflow.StageExplicitTextureDonors(stage, root, [selected, discarded]);
                var copied = PccPackageWorkflow.CloneCompletedRoot(destination, root);
                snapshot = PccPackageWorkflow.CaptureDependencyGraph(copied);
                destination.Save(output);
            }
            using var reopened = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            PccPackageWorkflow.VerifyDependencyGraph(reopened, snapshot);
            TestAssert.True(PccPackageWorkflow.HasPersistedExplicitTextureLeaf(reopened, "NativeTextures.Selected"),
                "The retained selection lost its persisted marker.");
            TestAssert.True(!PccPackageWorkflow.HasPersistedExplicitTextureLeaf(reopened, "NativeTextures.Discarded"),
                "A discarded texture selection leaked into the final PCC metadata.");
            TestAssert.True(reopened.FindEntry("NativeTextures.Discarded") is null, "Discarded texture was copied.");
            var savedRoot = reopened.FindExport("NativeTextures.Root", "Object")!;
            var properties = savedRoot.GetProperties();
            properties.RemoveNamedProperty("Next");
            savedRoot.WriteProperties(properties);
            var rejected = false;
            try { PccPackageWorkflow.VerifyDependencyGraph(reopened, snapshot); }
            catch (InvalidDataException) { rejected = true; }
            TestAssert.True(rejected, "Graph verification accepted a lost saved cycle edge.");
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
}
