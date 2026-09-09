using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.GameFilesystem;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.Tests;

/// <summary>Installed texture import regressions exercise canonical paths through PCC and RON readback.</summary>
internal static class StandaloneTextureImportTests
{
    internal static void HumanTextureDonorMatrix()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var registry = new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));
        var totalLocal = 0;
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            var catalog = registry.ReadAsync(game).GetAwaiter().GetResult();
            var candidates = catalog.Candidates.Where(value => IsHumanHeadPath(value.InstancedPath)).ToArray();
            TestAssert.True(candidates.Length > 0, $"No human texture candidates were available for {game}.");
            var qualified = 0;
            var local = 0;
            var materialisedLayouts = new HashSet<string>();
            // Check every effective human-head donor, grouped to open each PCC once.
            foreach (var group in candidates.GroupBy(value => value.EffectiveOccurrence.PackagePath,
                         StringComparer.OrdinalIgnoreCase))
            {
                using var package = MEPackageHandler.OpenMEPackage(group.Key, forceLoadFromDisk: true);
                foreach (var candidate in group)
                {
                    var identity = new AssetIdentity(group.Key, candidate.InstancedPath,
                        candidate.EffectiveOccurrence.ExportUIndex, "Texture2D");
                    var resolved = ExternalTextureMaterializer.ResolveSourceExport(package, identity);
                    TestAssert.Equal(candidate.EffectiveOccurrence.ExportUIndex, resolved.UIndex);
                    // The same exact path must resolve even after a registry index goes stale.
                    TestAssert.Equal(resolved, ExternalTextureMaterializer.ResolveSourceExport(package,
                        identity with { UIndex = 0 }));
                    var isLocal = !resolved.InstancedFullPath.Equals(candidate.InstancedPath, StringComparison.OrdinalIgnoreCase);
                    if (isLocal) local++; else qualified++;
                    var sex = candidate.InstancedPath.StartsWith("BIOG_HMM", StringComparison.OrdinalIgnoreCase) ? "HMM" : "HMF";
                    if (materialisedLayouts.Add($"{sex}/{isLocal}"))
                    {
                        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("HumanTextureMatrix.pcc", package.Game);
                        var texture = ExternalTextureMaterializer.Materialize(destination, identity);
                        TestAssert.Equal(candidate.InstancedPath, texture.InstancedFullPath);
                        TestAssert.True(new LegendaryExplorerCore.Unreal.Classes.Texture2D(texture).GetTopMip().IsPackageStored,
                            "A matrix texture retained its donor TFC dependency.");
                        PackageIntegrity.Verify(destination);
                    }
                }
            }
            // The registry can omit package-local exports. Exercise the same
            // named installed-package fallback used by standalone RON lookup.
            var meGame = game switch { MorphFaceGame.LE1 => MEGame.LE1, MorphFaceGame.LE2 => MEGame.LE2, _ => MEGame.LE3 };
            var installed = MELoadedFiles.GetFilesLoadedInGame(meGame, forceUseCached: true);
            foreach (var root in new[] { "BIOG_HMM_HED_PROMorph", "BIOG_HMF_HED_PROMorph_R" })
            {
                using var package = MEPackageHandler.OpenMEPackage(installed[root + ".pcc"], forceLoadFromDisk: true);
                foreach (var texture in package.Exports.Where(value => value.ClassName == "Texture2D"))
                {
                    var canonical = IsHumanHeadPath(texture.InstancedFullPath)
                        ? texture.InstancedFullPath : $"{root}.{texture.InstancedFullPath}";
                    var identity = new AssetIdentity(package.FilePath, canonical, 0, "Texture2D");
                    TestAssert.Equal(texture, ExternalTextureMaterializer.ResolveSourceExport(package, identity));
                    var isLocal = canonical != texture.InstancedFullPath;
                    if (isLocal) local++; else qualified++;
                    var sex = root.Contains("HMM", StringComparison.Ordinal) ? "HMM" : "HMF";
                    if (materialisedLayouts.Add($"{sex}/{isLocal}"))
                    {
                        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("HumanTextureMatrix.pcc", package.Game);
                        var result = ExternalTextureMaterializer.Materialize(destination, identity);
                        TestAssert.Equal(canonical, result.InstancedFullPath);
                        TestAssert.True(new LegendaryExplorerCore.Unreal.Classes.Texture2D(result).GetTopMip().IsPackageStored,
                            "A local-path matrix texture retained its donor TFC dependency.");
                        PackageIntegrity.Verify(destination);
                    }
                }
            }
            // Player seed exports supply full seekfree paths even when all effective
            // registry donors for a particular install come from PROMorph packages.
            using var seed = MEPackageHandler.OpenMEPackage(
                StandalonePlayerMorphImportService.ResolveInstalledSeed(game), forceLoadFromDisk: true);
            var seedTextures = seed.Exports.Where(value => value.ClassName == "Texture2D" &&
                IsHumanHeadPath(value.InstancedFullPath)).ToArray();
            foreach (var texture in seedTextures)
            {
                var identity = new AssetIdentity(seed.FilePath, texture.InstancedFullPath, 0, "Texture2D");
                TestAssert.Equal(texture, ExternalTextureMaterializer.ResolveSourceExport(seed, identity));
            }
            totalLocal += local;
            TestAssert.True(seedTextures.Length > 0, $"{game} did not exercise seekfree texture paths.");
            Console.WriteLine($"{game}: {local} local, {qualified} qualified donor checks; {seedTextures.Length} seekfree seed textures; {materialisedLayouts.Count} materialisation layouts.");
        }
        TestAssert.True(totalLocal > 0, "The installed matrix did not exercise package-local texture paths.");
    }

    private static bool IsHumanHeadPath(string path) =>
        path.StartsWith("BIOG_HMM_HED_PROMorph.", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("BIOG_HMF_HED_PROMorph_R.", StringComparison.OrdinalIgnoreCase);

    internal static void Le1RonImportsLe2Scar()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var seed = StandalonePlayerMorphImportService.ResolveInstalledSeed(MorphFaceGame.LE1);
        var destinationSeed = StandalonePlayerMorphImportService.ResolveInstalledSeed(MorphFaceGame.LE2);
        var before = PackageFingerprint.Capture(destinationSeed);
        var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-Scar10-{Guid.NewGuid():N}.ron");
        const string scarPath = "BIOG_HMM_HED_PROMorph.Scars.HMM_HED_PROCustom_Scr10";
        var context = new MorphFacePackageContextService();
        try
        {
            context.ExportRon(seed, "BIOG_MORPH_FACE.Player_Base_Male", ronPath);
            var original = TseHeadMorphRon.Read(ronPath);
            var expected = original with
            {
                HairMesh = "None",
                AccessoryMeshes = [],
                MaterialData = new MorphFaceMaterialData([], [],
                    [new TextureMaterialOverride("HED_Scar", new AssetIdentity(string.Empty, scarPath, 0, "Texture2D"))])
            };
            TseHeadMorphRon.Write(ronPath, expected);
            var registry = new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));
            var textures = registry.ReadAsync(MorphFaceGame.LE2).GetAwaiter().GetResult();
            var catalog = StandalonePlayerAssetCatalog.ForRon(MorphFaceGame.LE2, ronPath, textures.Candidates);
            var donor = catalog.Textures.Single(value => value.InstancedPath == scarPath);
            using (var package = MEPackageHandler.OpenMEPackage(donor.PackagePath, forceLoadFromDisk: true))
            {
                Console.WriteLine($"Scr10 donor: {donor.PackagePath} :: {package.GetUExport(donor.UIndex).InstancedFullPath}");
            }
            using var imported = new StandalonePlayerMorphImportService().ImportPlayerRon(
                MorphFaceGame.LE2, ronPath, "MFE_Scar10", catalog);
            using (var package = MEPackageHandler.OpenMEPackage(imported.Workspace.WorkingPath, forceLoadFromDisk: true))
            {
                var texture = package.FindExport(scarPath, "Texture2D");
                TestAssert.True(texture is not null, "The canonical scar path was not materialised.");
                TestAssert.True(new LegendaryExplorerCore.Unreal.Classes.Texture2D(texture!).GetTopMip().IsPackageStored,
                    "The scar retained an external texture-cache dependency.");
            }
            context.ExportRon(imported.Workspace.WorkingPath, imported.ImportedFacePath, ronPath);
            var readback = TseHeadMorphRon.Read(ronPath);
            TestAssert.Equal(scarPath, readback.MaterialData.Textures.Single().TextureReference!.InstancedPath);
            TestAssert.Equal(expected.MorphData.BakedLods[0].Length, readback.MorphData.BakedLods[0].Length);
            TestAssert.True(expected.MorphData.BakedLods[0].SequenceEqual(readback.MorphData.BakedLods[0]),
                "Importing the scar changed the authored geometry.");
            TestAssert.Equal(before, PackageFingerprint.Capture(destinationSeed));
        }
        finally
        {
            File.Delete(ronPath);
        }
    }
}
