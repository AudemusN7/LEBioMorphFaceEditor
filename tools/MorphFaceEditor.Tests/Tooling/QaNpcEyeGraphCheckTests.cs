using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.QA;

namespace MorphFaceEditor.Tests;

internal static class QaNpcEyeGraphCheckTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
        [new("QA NPC eye graph distinguishes native roles from Player and exported-master lookalikes", NativeRoles)];

    private static void NativeRoles()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var variant in new[] { "stock", "player", "exported-master", "imported-outers" })
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage("NpcEyeGraph.pcc", MEGame.LE2);
            var foreign = variant == "player";
            if (foreign)
            {
                // Keep native package roles valid so this variant isolates the Player parent identity.
                var nativeEffects = package.CreatePackageExport("EffectsMaterials");
                _ = package.CreatePackageExport("Users", nativeEffects);
            }
            var importedOuters = variant == "imported-outers" || foreign;
            IEntry effects = importedOuters
                ? package.CreatePackageImport(foreign ? "BIOG_Humanoid_EYE_OVRD" : "EffectsMaterials")
                : package.CreatePackageExport("EffectsMaterials");
            IEntry users = importedOuters
                ? package.CreateImport("Package", "Users", effects)
                : package.CreatePackageExport("Users", effects);
            IEntry parent = variant == "exported-master"
                ? package.CreateExport("HMM_EYE_MASTER_MAT_USER", "RvrEffectsMaterialUser", users, indexed: false)
                : package.CreateImport("RvrEffectsMaterialUser",
                    foreign ? "HMM_EYE_MASTER_OVRD_MAT_USER" : "HMM_EYE_MASTER_MAT_USER", users);
            var root = package.CreatePackageExport("BIOG_HMM_HED_PROMorph");
            var eye = package.CreatePackageExport("Eye", root);
            var mic = package.CreateExport("HMM_EYE_MAT_1a", "MaterialInstanceConstant", eye, indexed: false);
            mic.WriteProperty(new ObjectProperty(parent, "Parent"));
            var head = package.CreateExport("HMM_NPC_BaseHead", "SkeletalMesh", indexed: false);
            var binary = SkeletalMesh.Create();
            binary.Materials = [mic.UIndex];
            head.WriteBinary(binary);
            var evidence = QaNpcEyeGraphCheck.Inspect(package, head, "HMM");
            TestAssert.True(evidence.Passed == (variant == "stock"),
                $"QA misclassified the {variant} eye graph: {string.Join("; ", evidence.Failures)}");
        }
    }
}
