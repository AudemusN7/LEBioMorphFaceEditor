using System.Text.Json;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.Tests;

internal static class M3toTextureRegistryTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("texture registry: M3TO declarations retain only supported replacement textures through full and mod scans",
            OverridesSurviveRegistryBuilds)
    ];

    private static void OverridesSurviveRegistryBuilds()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var root = Path.Combine(Path.GetTempPath(), $"MFE-M3TO-{Guid.NewGuid():N}");
        var bioGame = Path.Combine(root, "BioGame");
        var modRoot = Path.Combine(bioGame, "DLC", "DLC_MOD_M3toTest");
        var officialPath = Path.Combine(bioGame, "CookedPCConsole", "BIOG_Test.pcc");
        var overridePath = Path.Combine(modRoot, "CookedPCConsole", "TO_Test.pcc");
        var copyPath = Path.Combine(modRoot, "CookedPCConsole", "OrdinaryCopy.pcc");
        const string groupPath = "BIOG_HMF_HED_PROMorph_R.PROSheppard";
        const string texturePath = groupPath + ".HMF_HED_PROSheppard_Face_Diff_Stack";
        const string ordinaryPath = groupPath + ".HMF_HED_Ordinary_Diff";
        const string meshPath = "BIOG_HMF_HIR_PRO.Hair.HMF_HIR_Test_MDL";
        const string excludedPath = "BIOG_HMF_HED_PROMorph_R.GUI_Excluded";
        const string bodyPath = "BIOG_Armor.Body_Diff";
        var paths = new[] { officialPath, overridePath, copyPath };
        try
        {
            foreach (var path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE1);
                foreach (var texture in new[] { texturePath, ordinaryPath, excludedPath, bodyPath })
                {
                    var parent = package.CreatePackageExport(texture[..texture.LastIndexOf('.')]);
                    package.CreateExport(texture.Split('.').Last(), "Texture2D", parent, indexed: false)
                        .WriteBinary(UTexture2D.Create());
                }
                var meshParent = package.CreatePackageExport(meshPath[..meshPath.LastIndexOf('.')]);
                package.CreateExport(meshPath.Split('.').Last(), "SkeletalMesh", meshParent, indexed: false);
                package.Save(path);
            }
            File.WriteAllText(Path.Combine(modRoot, "AutoLoad.ini"), "[ME1DLCMOUNT]\nModMount=9000\n");
            var manifestPath = Path.Combine(Path.GetDirectoryName(overridePath)!, "TextureOverride-Test.m3to");
            void WriteManifest(string game) => File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                game,
                textures = new[] { texturePath, meshPath, excludedPath, bodyPath }.Select(path => new
                {
                    sourcepackage = Path.GetFileName(overridePath), textureifp = path
                })
            }));
            WriteManifest("LE1");
            var store = new TextureRegistryStore(new TextureRegistryPaths(Path.Combine(root, "Registry")));
            var builder = new TextureRegistryBuilder(store, new LecTextureRegistryPackageScanner(), _ => paths);
            builder.RebuildAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
            var officialBytes = File.ReadAllBytes(storePath());
            CheckReplacement();
            builder.RebuildModsAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
            CheckReplacement();
            TestAssert.True(officialBytes.SequenceEqual(File.ReadAllBytes(storePath())),
                "A mod-only scan changed the official registry.");

            WriteManifest("LE2");
            builder.RebuildModsAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
            TestAssert.Equal(0, store.ReadMods(MorphFaceGame.LE1).Candidates.Count);
            File.WriteAllText(manifestPath, "{invalid");
            builder.RebuildModsAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
            TestAssert.Equal(0, store.ReadMods(MorphFaceGame.LE1).Candidates.Count);

            string storePath() => Path.Combine(root, "Registry", "LE1.mftr");

            void CheckReplacement()
            {
                var mods = store.ReadMods(MorphFaceGame.LE1);
                TestAssert.Equal(1, mods.Candidates.Count);
                var replacement = mods.Candidates.Single();
                TestAssert.Equal(texturePath, replacement.InstancedPath);
                TestAssert.Equal(overridePath, replacement.EffectiveOccurrence.PackagePath);
                TestAssert.True(replacement.EffectiveOccurrence.IsM3toOverride,
                    "The M3TO source marker was lost during publication/readback.");
                TestAssert.Equal(1, replacement.Occurrences.Count);
                TestAssert.Equal(0, mods.AttachmentMeshes.Count);
                TestAssert.Equal(0, mods.Assets.Count);
                var catalogue = new TextureCatalogService(store).ReadAsync(MorphFaceGame.LE1).GetAwaiter().GetResult();
                var merged = catalogue.Candidates.Single(value => value.InstancedPath == texturePath);
                TestAssert.Equal(2, merged.Occurrences.Count);
                TestAssert.Equal(officialPath, merged.EffectiveOccurrence.PackagePath);
                TestAssert.True(merged.IsEligibleOfficialDefault,
                    "The replacement displaced the official automatic donor.");
                TestAssert.True(catalogue.Candidates.All(value => value.InstancedPath != excludedPath &&
                    value.InstancedPath != bodyPath), "M3TO declarations bypassed texture discovery exclusions.");
                TestAssert.True(catalogue.Candidates.Single(value => value.InstancedPath == ordinaryPath)
                    .Occurrences.All(value => value.Origin != TextureCatalogOrigin.Mod),
                    "An undeclared stock-path copy entered the mod catalogue.");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
