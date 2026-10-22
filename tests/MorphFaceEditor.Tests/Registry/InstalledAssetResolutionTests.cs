using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class InstalledAssetResolutionTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("official asset donors: explicit texture selection retains its physical donor", ExplicitTextureWins),
        new("official asset donors: unavailable selected texture cannot switch donors", UnavailableSelectedTextureFails),
        new("official asset donors: shadowed official package cannot supply a removed asset", ShadowedOfficialIsExcluded),
        new("official asset donors: changed requested donor reloads without registry rescan", ChangedDonorReloads),
        new("selected textures: unavailable donor omits override and preserves other edits", MissingSelectionFallsBack),
        new("missing optional assets: PCC export omits missing texture and hair references", MissingOptionalAssetsExportWithDefaults),
        new("explicit stock-path texture survives PCC reopen and a second export", ExplicitTextureSurvivesSecondExport),
        new("seekfree PCC assets preserve memory paths and flags after reopen", SeekfreeAssetsRoundTrip),
        new("installed PROMorph and HIR assets materialise paths and flags across games", InstalledSeekfreeAssetsRoundTrip),
    ];

    private const string AssetPath = "BIOG_MFE_Policy.Head.Asset";

    private static void MissingOptionalAssetsExportWithDefaults()
    {
        var sourcePath = Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc");
        var destination = Path.Combine(Path.GetTempPath(), $"MFE-Missing-Optional-{Guid.NewGuid():N}.pcc");
        try
        {
            using var reader = new MorphFacePackageReader();
            var loaded = reader.Load(sourcePath, "HMF.BIOA_PRC2_HMF_Guard01");
            var missingPath = Path.Combine(Path.GetTempPath(), $"MFE-Missing-{Guid.NewGuid():N}.pcc");
            var draft = loaded.Document with
            {
                HairMeshReference = new AssetIdentity(missingPath, "BIOG_Missing.Hair.Mesh", 1, "SkeletalMesh"),
                MaterialOverrides = loaded.Document.MaterialOverrides with
                {
                    Textures = loaded.Document.MaterialOverrides.Textures
                        .Where(value => value.Name != "HED_Diff")
                        .Append(new TextureMaterialOverride("HED_Diff", new AssetIdentity(
                            missingPath, "BIOG_Missing.Head.Diff", 2, "Texture2D")
                            { IsExplicitTextureDonor = true })).ToArray()
                }
            };
            var result = new MorphFacePackageWriter().SaveMorphToPackage(
                draft, sourcePath, destination, createNewPackage: true);
            TestAssert.True(result.Warnings.Any(value => value.Contains("material default")),
                "The saved fallback texture was not reported.");
            TestAssert.True(result.Warnings.Any(value => value.Contains("Optional attachment")),
                "The omitted attachment was not reported.");
            var reopened = reader.Load(destination, result.FaceInstancedPath);
            TestAssert.True(reopened.Document.HairMeshReference is null, "The export retained an unavailable hair reference.");
            TestAssert.True(reopened.Document.MaterialOverrides.Textures.All(value => value.Name != "HED_Diff"),
                "The export retained an unavailable texture override instead of inheriting its default.");
            TestAssert.True(reopened.Materials.Materials.Values.Any(material => material.Textures.ContainsKey("HED_Diff")),
                "The exported head did not recover a default diffuse texture.");
            var existingDraft = reopened.Document with
            {
                HairMeshReference = draft.HairMeshReference ?? new AssetIdentity(missingPath, "BIOG_Missing.Hair.Mesh", 1, "SkeletalMesh"),
                MaterialOverrides = reopened.Document.MaterialOverrides with
                { Textures = draft.MaterialOverrides.Textures }
            };
            var saved = new MorphFacePackageWriter().SaveExisting(existingDraft);
            TestAssert.True(saved.Warnings.Count >= 2, "An existing-package fallback was not reported.");
            var savedFace = reader.Load(destination, result.FaceInstancedPath);
            TestAssert.True(savedFace.Document.HairMeshReference is null &&
                savedFace.Document.MaterialOverrides.Textures.All(value => value.Name != "HED_Diff"),
                "Saving an existing PCC retained unavailable optional references.");
        }
        finally { if (File.Exists(destination)) File.Delete(destination); }
    }

    private static void ChangedDonorReloads() => WithDonors((root, lower, official, mod) =>
    {
        var snapshot = Snapshot(lower, official, mod) with
        { MountedPackages = [MountedPackageFingerprint.Read(official.Path)] };
        using var cache = new PackageCache();
        var resolver = Resolver(snapshot);
        TestAssert.Equal(100, resolver.Resolve(MEGame.LE1, AssetPath, "Texture2D", cache)!
            .GetProperty<IntProperty>("DonorMarker")!.Value);
        official.Asset.WriteProperty(new IntProperty(12345, "DonorMarker"));
        official.Package.Save(official.Path);
        File.SetLastWriteTimeUtc(official.Path, DateTime.UtcNow.AddSeconds(2));
        TestAssert.Equal(12345, resolver.Resolve(MEGame.LE1, AssetPath, "Texture2D", cache)!
            .GetProperty<IntProperty>("DonorMarker")!.Value);
    });

    private static void MissingSelectionFallsBack() => WithDonors((root, lower, official, mod) =>
    {
        using var cache = new PackageCache();
        var missing = new AssetIdentity(Path.Combine(root, "Missing.pcc"), AssetPath, 1, "Texture2D")
        { IsExplicitTextureDonor = true };
        var retained = new AssetIdentity(mod.Path, AssetPath, mod.Asset.UIndex, "Texture2D")
        { IsExplicitTextureDonor = true };
        var input = new MorphFaceMaterialOverrides(null, [new("Scalar", 0.5f)], [],
            [new("HED_Diff", missing), new("HED_Norm", retained)]);
        var warnings = new List<string>();
        var result = PccPackageWorkflow.OmitUnavailableSelectedTextures(input, cache, warnings);
        TestAssert.Equal(1, result.Textures.Count);
        TestAssert.Equal("HED_Norm", result.Textures[0].Name);
        TestAssert.Equal(0.5f, result.Scalars[0].Value);
        TestAssert.True(warnings.Single().Contains("material default", StringComparison.OrdinalIgnoreCase),
            "An unavailable texture was omitted without explaining its fallback.");
    });

    private static void ExplicitTextureWins() => WithDonors((root, lower, official, mod) =>
    {
        var snapshot = Snapshot(lower, official, mod);
        using var cache = new PackageCache();
        var resolver = Resolver(snapshot);
        var texture = resolver.Resolve(MEGame.LE1,
            new AssetIdentity(mod.Path, AssetPath, mod.Asset.UIndex, "Texture2D")
            { IsExplicitTextureDonor = true }, cache)!;
        TestAssert.Equal(mod.Path, texture.FileRef.FilePath);
        TestAssert.Equal(9000, texture.GetProperty<IntProperty>("DonorMarker")!.Value);
        var defaultTexture = resolver.Resolve(MEGame.LE1, AssetPath, "Texture2D", cache)!;
        TestAssert.Equal(official.Path, defaultTexture.FileRef.FilePath);
    });

    private static void UnavailableSelectedTextureFails() => WithDonors((root, lower, official, mod) =>
    {
        using var cache = new PackageCache();
        try
        {
            Resolver(Snapshot(lower, official, mod)).Resolve(MEGame.LE1,
                new AssetIdentity(Path.Combine(root, "Missing.pcc"), AssetPath, mod.Asset.UIndex, "Texture2D")
                { IsExplicitTextureDonor = true }, cache);
        }
        catch (InvalidDataException exception) when (exception.Message.Contains("not found in donor", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new Exception("An unavailable explicit selection silently switched to the official donor.");
    });

    private static void ShadowedOfficialIsExcluded() => WithDonors((root, lower, official, mod) =>
    {
        var snapshot = Snapshot(lower, official, mod) with
        {
            Assets = [Occurrence(lower, 0, TextureCatalogOrigin.BaseGame)],
            OfficialPackagePaths = [official.Path]
        };
        using var cache = new PackageCache();
        TestAssert.True(Resolver(snapshot).Resolve(MEGame.LE1, AssetPath, "Texture2D", cache) is null,
            "An excluded lower official package supplied an asset absent from the mounted official winner.");
    });

    private static void ExplicitTextureSurvivesSecondExport()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const string texturePath = "BIOG_Humanoid_MASTER_MTR_R.Skin_AsariHED_SpecMulitplier_Mask";
        var sourcePath = Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc");
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-SelectedDonor-{Guid.NewGuid():N}.pcc");
        var firstPath = Path.Combine(Path.GetTempPath(), $"MFE-SelectedFirst-{Guid.NewGuid():N}.pcc");
        var secondPath = Path.Combine(Path.GetTempPath(), $"MFE-SelectedSecond-{Guid.NewGuid():N}.pcc");
        try
        {
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage("BIOG_Humanoid_MASTER_MTR_R.pcc", MEGame.LE1))
            using (var corpus = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
            {
                var root = donor.CreatePackageExport("BIOG_Humanoid_MASTER_MTR_R");
                var sourceTexture = corpus.Exports.First(export =>
                    export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                    new Texture2D(export).Mips.Any(mip => mip.storageType != StorageTypes.empty));
                var selected = EntryImporter.ImportExport(donor, sourceTexture, root.UIndex,
                    new RelinkerOptionsPackage
                    {
                        ImportExportDependencies = false,
                        GenerateImportsForGlobalFiles = false
                    }) as ExportEntry ?? throw new InvalidDataException("Synthetic selected texture was not cloned.");
                selected.ObjectName = new NameReference("Skin_AsariHED_SpecMulitplier_Mask");
                selected.WriteProperty(new IntProperty(717171, "MFESelectedDonorMarker"));
                donor.Save(donorPath);
            }

            using var donorPackage = MEPackageHandler.OpenMEPackage(donorPath, forceLoadFromDisk: true);
            var selectedTexture = donorPackage.FindExport(texturePath, "Texture2D")
                                  ?? throw new InvalidDataException("Synthetic selected texture path changed.");
            using var reader = new MorphFacePackageReader();
            string facePath;
            using (var sourcePackage = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
                facePath = sourcePackage.Exports.First(entry => entry.ClassName == "BioMorphFace").InstancedFullPath;
            var source = reader.Load(sourcePath, facePath).Document;
            var draft = source with
            {
                MaterialOverrides = new MorphFaceMaterialOverrides(source.MaterialOverrides.Source, [], [],
                [new TextureMaterialOverride("HED_Diff",
                    new AssetIdentity(donorPath, texturePath, selectedTexture.UIndex, "Texture2D")
                    { IsExplicitTextureDonor = true })])
            };
            _ = new MorphFacePackageWriter().SaveMorphToPackage(
                draft, sourcePath, firstPath, createNewPackage: true);

            var loaded = reader.Load(firstPath, facePath);
            var reopened = loaded.Document;
            var reference = reopened.MaterialOverrides.Textures.Single().TextureReference!;
            TestAssert.True(reference.IsExplicitTextureDonor,
                "The deliberately embedded texture lost its selection intent on PCC reopen.");
            TestAssert.Equal(Path.GetFullPath(firstPath), reference.PackagePath);
            var preview = loaded.Materials.Materials.Values
                .SelectMany(material => material.Textures.Values)
                .FirstOrDefault(binding => binding.ParameterName.Equals("HED_Diff", StringComparison.OrdinalIgnoreCase));
            TestAssert.True(preview is not null,
                "The selected texture parameter was not available in the reopened face preview.");
            TestAssert.Equal(Path.GetFullPath(firstPath), preview!.Texture.Source.PackagePath);
            _ = new MorphFacePackageWriter().SaveMorphToPackage(
                reopened, firstPath, secondPath, createNewPackage: true);
            using var second = MEPackageHandler.OpenMEPackage(secondPath, forceLoadFromDisk: true);
            var embedded = second.FindExport(texturePath, "Texture2D")
                           ?? throw new InvalidDataException("The second export lost the selected texture payload.");
            TestAssert.Equal(717171, embedded.GetProperty<IntProperty>("MFESelectedDonorMarker")?.Value ?? 0);
            TestAssert.True(second.FindEntry(texturePath, "Texture2D") is ExportEntry,
                "The second export reverted the selected texture to a stock import.");
        }
        finally
        {
            foreach (var path in new[] { donorPath, firstPath, secondPath })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static InstalledGameAssetResolver Resolver(TextureRegistrySnapshot snapshot) =>
        new(_ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), _ => snapshot);

    private static InstalledAssetOccurrence Occurrence(Donor donor, int priority, TextureCatalogOrigin origin) =>
        new(AssetPath, "Texture2D", donor.Path, donor.Asset.UIndex, priority, origin);

    private static TextureRegistrySnapshot Snapshot(Donor lower, Donor official, Donor mod) =>
        new(TextureRegistrySnapshot.CurrentSchemaVersion, MorphFaceGame.LE1, DateTimeOffset.UtcNow, 3, [])
        {
            Assets = [Occurrence(lower, 0, TextureCatalogOrigin.BaseGame),
                Occurrence(official, 100, TextureCatalogOrigin.OfficialDlc),
                Occurrence(mod, 9000, TextureCatalogOrigin.Mod)],
            OfficialPackagePaths = [lower.Path, official.Path]
        };

    private static void WithDonors(Action<string, Donor, Donor, Donor> test)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var root = Path.Combine(Path.GetTempPath(), $"MFE-Official-Donors-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var lower = CreateDonor(Path.Combine(root, "Lower.pcc"), 0);
            using var official = CreateDonor(Path.Combine(root, "Official.pcc"), 100);
            using var mod = CreateDonor(Path.Combine(root, "Mod.pcc"), 9000);
            test(root, lower, official, mod);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Donor CreateDonor(string path, int marker)
    {
        var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE1);
        var root = package.CreatePackageExport("BIOG_MFE_Policy");
        root.ExportFlags |= UnrealFlags.EExportFlags.ForcedExport;
        var group = package.CreatePackageExport("Head", root);
        var asset = package.CreateExport("Asset", "Texture2D", group, indexed: false);
        asset.WriteProperty(new IntProperty(marker, "DonorMarker"));
        package.Save(path);
        return new(path, package, asset);
    }

    private static void SeekfreeAssetsRoundTrip()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var path = Path.Combine(Path.GetTempPath(), $"MFE-Seekfree-{Guid.NewGuid():N}.pcc");
        try
        {
            using (var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE1))
            {
                foreach (var className in new[] { "Texture2D", "SkeletalMesh" })
                {
                    var parent = PackageIntegrity.EnsurePackagePath(package, $"BIOG_MFE_Seekfree.Head.{className}", className);
                    var export = package.CreateExport(className, className, parent, indexed: false);
                    export.ExportFlags |= UnrealFlags.EExportFlags.ScriptPatcherExport;
                    PccAssetPathPolicy.NormalizeMaterializedAsset(export);
                    TestAssert.True(export.IsForcedExport, "Materialised asset is not ForcedExport.");
                    TestAssert.Equal(export.InstancedFullPath, export.MemoryFullPath);
                }
                package.Save(path);
            }
            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            foreach (var export in reopened.Exports.Where(entry => entry.ClassName is "Texture2D" or "SkeletalMesh"))
            {
                TestAssert.True(export.IsForcedExport, "ForcedExport did not survive save/reopen.");
                TestAssert.Equal(export.InstancedFullPath, export.MemoryFullPath);
                TestAssert.True((export.ExportFlags & UnrealFlags.EExportFlags.ScriptPatcherExport) != 0,
                    "An unrelated export flag was lost.");
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void InstalledSeekfreeAssetsRoundTrip()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        foreach (var fileName in new[] { "BIOG_HMM_HED_PROMorph.pcc", "BIOG_HMM_HIR_PRO_R.pcc" })
        {
            using var donor = MEPackageHandler.OpenMEPackage(
                InstalledGameAssetResolver.ResolvePackage(game, fileName), forceLoadFromDisk: true);
            foreach (var className in new[] { "SkeletalMesh", "Texture2D" })
            {
                var asset = donor.Exports.First(entry => entry.ClassName == className && !entry.IsDefaultObject);
                var canonical = PccAssetPathPolicy.FromExport(asset);
                var output = Path.Combine(Path.GetTempPath(), $"MFE-RealSeekfree-{Guid.NewGuid():N}.pcc");
                try
                {
                    using var destination = MEPackageHandler.CreateMemoryEmptyPackage(output, donor.Game);
                    var identity = new AssetIdentity(donor.FilePath, canonical, asset.UIndex, className);
                    var result = className == "Texture2D"
                        ? PccTextureDependencyResolver.MaterializeDirect(destination, identity)
                        : ExternalSkeletalMeshMaterializer.Materialize(destination, identity);
                    TestAssert.Equal(canonical, result.InstancedFullPath);
                    TestAssert.Equal(asset.ExportFlags | UnrealFlags.EExportFlags.ForcedExport, result.ExportFlags);
                    var graph = PccPackageWorkflow.CaptureDependencyGraph(result);
                    destination.Save(output);
                    using var reopened = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
                    graph.Verify(reopened);
                    var reopenedAsset = reopened.FindExport(canonical, className)!;
                    TestAssert.Equal(result.ExportFlags, reopenedAsset.ExportFlags);
                    foreach (var embedded in reopened.Exports.Where(entry => InstalledAssetOccurrence.IsSupportedClass(entry.ClassName)))
                    {
                        TestAssert.True(embedded.IsForcedExport, $"{game}/{fileName}: {embedded.InstancedFullPath} lost ForcedExport.");
                        TestAssert.Equal(embedded.InstancedFullPath, embedded.MemoryFullPath);
                    }
                    PackageIntegrity.Verify(reopened);
                }
                finally { if (File.Exists(output)) File.Delete(output); }
            }
        }
    }

    private sealed record Donor(string Path, IMEPackage Package, ExportEntry Asset) : IDisposable
    {
        public void Dispose() => Package.Dispose();
    }
}
