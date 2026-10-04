using System.Diagnostics;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

/// <summary>Synthetic corruption cases and corpus transfers verify package integrity and optional-asset policy.</summary>
internal static class PackageIntegrityTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("interrupted PCC save preserves the source and isolates its candidate", InterruptedPccSavePreservesSource),
        new("LE1 save materialises a mesh beneath an imported package parent", () => SaveWithImportedMeshParent(MEGame.LE1)),
        new("LE2 save materialises a mesh beneath an imported package parent", () => SaveWithImportedMeshParent(MEGame.LE2)),
        new("LE3 save materialises a mesh beneath an imported package parent", () => SaveWithImportedMeshParent(MEGame.LE3)),
        new("required relink failure preserves original PCC bytes on save", SaveFailurePreservesOriginal),
        new("relink rejects a valid destination index pointing at the wrong asset", WrongReferenceRejected),
        new("relink reuses an existing export without rewriting its destination references", ExistingExportIsNotRelinked),
        new("relink processes a shared destination export once when donor aliases differ", SharedDestinationAliasRelinkedOnce),
        new("relink rejects retained binary failure reports even with valid references", BinaryReportRejected),
        new("package integrity rejects duplicate identities and missing parents", InvalidHierarchyRejected),
        new("package integrity permits only pre-existing structural issues", ExistingIssuesAreBaselined),
        new("package integrity rejects parent cycles before formatting entry paths", ParentCyclesRejected),
        new("package path construction reuses imported and exported ancestors", AncestorPreflight),
        new("dependency materialisation preserves imported package ancestry through save reopen", () => ImportedAncestryRoundTrip(false)),
        new("root-preserving export preserves imported package ancestry through save reopen", () => ImportedAncestryRoundTrip(true)),
        new("same-game skeletal materialisation verifies retained references", MaterialiseValidMesh),
        new("root-preserving export maps overridden supported dependencies to the official donor", RootExportUsesOfficialDonor),
        new("graph verification checks selected donor content and validates source aliases", StagedDonorOwnsAliasVerification),
        new("root-preserving export embeds only an explicitly selected texture over a native import role", RootExportEmbedsExplicitTextureLeaf),
        new("new-PCC writer preserves native texture role unless an external donor was explicitly selected", NewPackageTextureDonorIntent),
        new("temporary edit round-trips a seekfree randomised texture beneath an imported package", TemporaryEditSeekfreeTextureRoundTrip),
        new("writer retains an exact Texture2D import instead of materialising a duplicate", () => WriterRetainsExactTextureImport(false)),
        new("explicit texture selection rejects an occupying import without changing the PCC", () => WriterRetainsExactTextureImport(true)),
        new("texture donor resolution qualifies local paths without trusting stale indices", TextureDonorResolution),
        new("Texture2D materialisation preserves a non-Package TextureCube parent", TextureCubeParentPreserved),
        new("relink preserves PROShort01 target donors and optional attachment omissions", HairDonorsAndOmissions),
        new("relink preserves every LE1 Add and Tat fallback policy", () => TextureFallbacks(false)),
        new("texture conversion embeds Add and Tat fallbacks beneath imported package parents", () => TextureFallbacks(true))
    ];

    private static IMEPackage Empty(string name = "Integrity", MEGame game = MEGame.LE1)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        return MEPackageHandler.CreateMemoryEmptyPackage(name + ".pcc", game);
    }

    private static void TextureDonorResolution()
    {
        using var package = Empty("BIOG_Test");
        var group = package.CreatePackageExport("Scars");
        var expected = package.CreateExport("Scar", "Texture2D", group, indexed: false);
        var otherGroup = package.CreatePackageExport("Other");
        var other = package.CreateExport("Scar", "Texture2D", otherGroup, indexed: false);
        var identity = new AssetIdentity(package.FilePath, "BIOG_Test.Scars.Scar", other.UIndex, "Texture2D");
        TestAssert.Equal(expected, ExternalTextureMaterializer.ResolveSourceExport(package, identity));
        TestAssert.Equal(expected, ExternalTextureMaterializer.ResolveSourceExport(package,
            identity with { InstancedPath = "Scars.Scar" }));
        Reject(() => ExternalTextureMaterializer.ResolveSourceExport(package,
            identity with { InstancedPath = "WrongPackage.Scars.Scar", UIndex = expected.UIndex }), "not found");
        Reject(() => ExternalTextureMaterializer.ResolveSourceExport(package,
            identity with { InstancedPath = "BIOG_Test.Missing.Scar", UIndex = expected.UIndex }), "not found");
    }

    private static void TextureCubeParentPreserved()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var donorPath = Path.Combine(Path.GetTempPath(), $"BIOG_CubeDonor-{Guid.NewGuid():N}.pcc");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"MFE-CubeParent-{Guid.NewGuid():N}.pcc");
        try
        {
            int faceUIndex;
            string facePath;
            const string cubePath = "BIOG_CubeDonor.HVYa.HMM_HGR_HVYa_CUBE";
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage("BIOG_CubeDonor.pcc", MEGame.LE1))
            {
                var packageRoot = donor.CreatePackageExport("BIOG_CubeDonor");
                var group = donor.CreatePackageExport("HVYa", packageRoot);
                var cube = donor.CreateExport("HMM_HGR_HVYa_CUBE", "TextureCube", group, indexed: false);
                cube.WriteBinary(new UTextureCube());
                var face = donor.CreateExport("CubemapFace0", "Texture2D", cube, indexed: false);
                face.WriteBinary(UTexture2D.Create());
                cube.WriteProperty(new ObjectProperty(face, "FacePosX") { StaticArrayIndex = 0 });
                faceUIndex = face.UIndex;
                facePath = face.InstancedFullPath;
                donor.Save(donorPath);
            }

            using var destination = Empty("CubeParentDestination");
            var imported = PccTextureDependencyResolver.MaterializeDirect(
                destination,
                new AssetIdentity(donorPath, facePath, faceUIndex, "Texture2D"));
            var cubeExport = destination.FindExport(cubePath, "TextureCube");
            TestAssert.True(cubeExport is not null, "The source TextureCube parent was not materialised as a TextureCube export.");
            TestAssert.Equal(cubeExport!.UIndex, imported.idxLink);
            TestAssert.Equal(1, destination.Exports.Count(export =>
                export.ClassName == "Texture2D" && export.InstancedFullPath == facePath));
            TestAssert.Equal(imported.UIndex,
                PackageIntegrity.References(cubeExport)["property.FacePosX[0]"]);
            TestAssert.True(destination.FindExport(cubePath, "Package") is null,
                "The TextureCube parent path was incorrectly created as a Package export.");
            var graph = PccPackageWorkflow.CaptureDependencyGraph(imported);

            destination.Save(destinationPath);
            using var reopened = MEPackageHandler.OpenMEPackage(destinationPath, forceLoadFromDisk: true);
            PccPackageWorkflow.VerifyDependencyGraph(reopened, graph);
            var reopenedCube = reopened.FindExport(cubePath, "TextureCube");
            var reopenedFace = reopened.FindExport(facePath, "Texture2D");
            TestAssert.True(reopenedCube is not null && reopenedFace is not null,
                "Saving and reopening lost the TextureCube parent or its Texture2D child.");
            TestAssert.Equal(reopenedCube!.UIndex, reopenedFace!.idxLink);
        }
        finally
        {
            File.Delete(donorPath);
            File.Delete(destinationPath);
        }
    }

    private static void AncestorPreflight()
    {
        using var package = Empty();
        var root = package.CreatePackageExport("Root");
        var group = package.CreateImport("Package", "Group", root);
        var count = package.ExportCount;
        TestAssert.Equal(group, PackageIntegrity.EnsurePackagePath(package, "Root.Group.Asset"));
        TestAssert.Equal(count, package.ExportCount);
        TestAssert.Equal(root, PackageIntegrity.EnsurePackagePath(package, "Root.Asset"));
        TestAssert.Equal(group, package.FindEntry("Root.Group", "Package"));
        var nested = PackageIntegrity.EnsurePackagePath(package, "Root.Group.Nested.Asset");
        TestAssert.True(nested is ExportEntry && nested.Parent == group,
            "A missing export package was not created beneath the existing imported group.");
        var occupied = package.CreateImport("Object", "Occupied", group);
        count = package.ExportCount;
        Reject(() => PackageIntegrity.EnsurePackagePath(package, occupied.InstancedFullPath, "Object"), "import occupies");
        TestAssert.Equal(count, package.ExportCount);
        PackageIntegrity.Verify(package);
    }

    private static void ImportedAncestryRoundTrip(bool preserveStructure)
    {
        foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"MFE-ImportedAncestry-{Guid.NewGuid():N}.pcc");
            try
            {
                using var source = Empty("ImportedAncestrySource", game);
                var root = source.CreatePackageImport("BIOG_MFE_ImportedAncestry");
                var group = source.CreatePackageImport("Group", root);
                var nested = source.CreatePackageExport("Nested", group);
                var dependency = source.CreateExport("Dependency", "Object", nested, indexed: false);
                var asset = source.CreateExport("Asset", "Object", group, indexed: false);
                asset.WriteProperty(new ObjectProperty(dependency, "Required"));
                using var destination = Empty("ImportedAncestryDestination", game);
                if (!preserveStructure)
                {
                    var existingRoot = destination.CreatePackageImport("BIOG_MFE_ImportedAncestry");
                    destination.CreatePackageImport("Group", existingRoot);
                }
                var imported = preserveStructure
                    ? PccPackageWorkflow.ImportRootPreservingStructure(destination, asset)
                    : PccPackageWorkflow.ImportDependencyGraph(destination, asset, null, null, false,
                        targetPathOverride: asset.InstancedFullPath);
                TestAssert.Equal(asset.InstancedFullPath, imported.InstancedFullPath);
                TestAssert.True(imported.Parent is ImportEntry,
                    $"{game} changed the asset's imported parent into an export.");
                var graph = PccPackageWorkflow.CaptureDependencyGraph(imported);
                PackageIntegrity.Verify(destination);
                destination.Save(outputPath);
                using var reopened = MEPackageHandler.OpenMEPackage(outputPath, forceLoadFromDisk: true);
                PackageIntegrity.Verify(reopened);
                PccPackageWorkflow.VerifyDependencyGraph(reopened, graph);
                var saved = reopened.FindExport(asset.InstancedFullPath, "Object")!;
                TestAssert.True(saved.Parent is ImportEntry && saved.Parent.Parent is ImportEntry,
                    $"{game} lost the imported package chain after reopening.");
                TestAssert.True(saved.IsForcedExport, $"{game} lost the export's native ForcedExport flag.");
                var savedDependency = saved.GetProperty<ObjectProperty>("Required")!.ResolveToEntry(reopened)!;
                TestAssert.Equal(dependency.InstancedFullPath, savedDependency.InstancedFullPath);
                TestAssert.True(savedDependency.Parent is ExportEntry && savedDependency.Parent.Parent is ImportEntry,
                    $"{game} lost the mixed import/export package chain after reopening.");
                TestAssert.Equal(0, reopened.Exports.Count(entry => entry.ClassName == "Package" &&
                    (entry.InstancedFullPath == root.InstancedFullPath || entry.InstancedFullPath == group.InstancedFullPath)));
            }
            finally { File.Delete(outputPath); }
        }
    }

    private static void InvalidHierarchyRejected()
    {
        using var package = Empty();
        var import = package.CreatePackageImport("Root");
        var export = package.CreateExport("Asset", "Object", import, indexed: false);
        PackageIntegrity.Verify(package);
        import.idxLink = -999999;
        Reject(() => PackageIntegrity.Verify(package), "missing parent");
        import.idxLink = 0;
        export.idxLink = 0;
        package.CreateExport("Asset", "Object", indexed: false);
        Reject(() => PackageIntegrity.Verify(package), "Duplicate");
    }

    private static void WrongReferenceRejected()
    {
        using var source = Empty("Donor");
        using var destination = Empty("Destination");
        var donorDependency = source.CreateExport("Dependency", "Object", indexed: false);
        var donorRoot = source.CreateExport("Asset", "Object", indexed: false);
        donorRoot.WriteProperty(new ObjectProperty(donorDependency, "Required"));
        destination.CreateExport("Wrong", "Object", indexed: false);
        var options = new RelinkerOptionsPackage();
        var root = (ExportEntry)EntryImporter.ImportExport(destination, donorRoot, 0, options);
        Relinker.RelinkAll(options);
        MaterialisationVerifier.Verify(root, source.Game, options);
        root.WriteProperty(new ObjectProperty(donorDependency.UIndex, "Required"));
        Reject(() => MaterialisationVerifier.Verify(root, source.Game, options), "Required relink failed");
    }

    private static void ExistingExportIsNotRelinked()
    {
        using var source = Empty("Donor");
        using var destination = Empty("Destination");
        var donorDependency = source.CreateExport("Dependency", "Object", indexed: false);
        donorDependency.WriteProperty(new ObjectProperty(donorDependency, "Marker"));
        var donorRoot = source.CreateExport("Asset", "Object", indexed: false);
        donorRoot.WriteProperty(new ObjectProperty(donorDependency, "Required"));

        for (var index = 0; index < 20; index++)
            destination.CreateExport($"Filler{index}", "Object", indexed: false);
        var existing = destination.CreateExport("Dependency", "Object", indexed: false);
        existing.WriteProperty(new ObjectProperty(existing, "Marker"));
        var preexisting = destination.Exports.ToHashSet();

        var options = new RelinkerOptionsPackage();
        var root = (ExportEntry)EntryImporter.ImportExport(destination, donorRoot, 0, options);
        options.CrossPackageMap[donorDependency] = existing;
        MaterialisationVerifier.Relink(options, preexisting);
        MaterialisationVerifier.Verify(root, source.Game, options);

        TestAssert.Equal(existing.UIndex, PackageIntegrity.References(root)["property.Required[0]"]);
        TestAssert.Equal(existing.UIndex, PackageIntegrity.References(existing)["property.Marker[0]"]);
        existing.WriteProperty(new ObjectProperty(destination.Exports[0], "Marker"));
        Reject(() => MaterialisationVerifier.Verify(root, source.Game, options), "Required relink failed");
    }

    private static void SharedDestinationAliasRelinkedOnce()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var destinationPath = Path.Combine(Path.GetTempPath(), $"MFE-RelinkAlias-{Guid.NewGuid():N}.pcc");
        try
        {
            using var firstDonor = Empty("AliasDonorA");
            var firstMaterial = firstDonor.CreateExport("Material", "Object", indexed: false);
            var firstMesh = firstDonor.CreateExport("Asset", "SkeletalMesh", indexed: false);
            var firstBinary = SkeletalMesh.Create();
            firstBinary.Materials = [firstMaterial.UIndex];
            firstMesh.WriteBinary(firstBinary);

            using var secondDonor = Empty("AliasDonorB");
            using var destination = Empty("AliasDestination");
            for (var index = 0; index < 12; index++)
                destination.CreateExport($"DestinationFiller{index}", "Object", indexed: false);
            var destinationMaterial = destination.CreateExport("Material", "Object", indexed: false);
            var wrongMaterial = destination.CreateExport("WrongMaterial", "Object", indexed: false);
            while (secondDonor.ExportCount < destinationMaterial.UIndex - 1)
                secondDonor.CreateExport($"DonorFiller{secondDonor.ExportCount}", "Object", indexed: false);
            var collidingNumericEntry = secondDonor.CreateExport("NoiseAtDestinationMaterialIndex", "Object", indexed: false);
            TestAssert.Equal(destinationMaterial.UIndex, collidingNumericEntry.UIndex);
            var secondMaterial = secondDonor.CreateExport("Material", "Object", indexed: false);
            var secondMesh = secondDonor.CreateExport("Asset", "SkeletalMesh", indexed: false);
            var secondBinary = SkeletalMesh.Create();
            secondBinary.Materials = [secondMaterial.UIndex];
            secondMesh.WriteBinary(secondBinary);
            TestAssert.Equal(firstMesh.InstancedFullPath, secondMesh.InstancedFullPath);
            TestAssert.True(firstMesh.UIndex != secondMesh.UIndex,
                "The donor aliases must use distinct UIndex namespaces.");

            var options = new RelinkerOptionsPackage();
            var destinationMesh = (ExportEntry)EntryImporter.ImportExport(destination, firstMesh, 0, options);
            options.CrossPackageMap[firstMesh] = destinationMesh;
            options.CrossPackageMap[firstMaterial] = destinationMaterial;
            options.CrossPackageMap[secondMesh] = destinationMesh;
            options.CrossPackageMap[secondMaterial] = destinationMaterial;
            options.CrossPackageMap[collidingNumericEntry] = wrongMaterial;

            MaterialisationVerifier.Relink(options);
            MaterialisationVerifier.Verify(destinationMesh, firstDonor.Game, options);
            TestAssert.Equal(destinationMaterial.UIndex,
                PackageIntegrity.References(destinationMesh)["binary.Materials[0]"]);

            var graph = PccPackageWorkflow.CaptureDependencyGraph(destinationMesh);
            destination.Save(destinationPath);
            using var reopened = MEPackageHandler.OpenMEPackage(destinationPath, forceLoadFromDisk: true);
            PccPackageWorkflow.VerifyDependencyGraph(reopened, graph);
            var reopenedMesh = reopened.FindExport("Asset", "SkeletalMesh");
            var reopenedMaterial = reopened.FindExport("Material", "Object");
            TestAssert.True(reopenedMesh is not null && reopenedMaterial is not null,
                "Saving and reopening lost the relinked mesh or its material.");
            TestAssert.Equal(reopenedMaterial!.UIndex,
                PackageIntegrity.References(reopenedMesh!)["binary.Materials[0]"]);
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    private static void ExistingIssuesAreBaselined()
    {
        using var package = Empty();
        var existing = package.CreateExport("ExistingAsset", "Object", indexed: false);
        existing.idxLink = -999999;
        var baseline = PackageIntegrity.CaptureIssues(package);
        PackageIntegrity.Verify(package, baseline);

        var added = package.CreateExport("NewAsset", "Object", indexed: false);
        added.idxLink = -999999;
        Reject(() => PackageIntegrity.Verify(package, baseline), "NewAsset");

        using var duplicates = Empty("Duplicates");
        duplicates.CreateExport("Asset", "Object", indexed: false);
        duplicates.CreateExport("Asset", "Object", indexed: false);
        var duplicateBaseline = PackageIntegrity.CaptureIssues(duplicates);
        PackageIntegrity.Verify(duplicates, duplicateBaseline);
        duplicates.CreateExport("Asset", "Object", indexed: false);
        Reject(() => PackageIntegrity.Verify(duplicates, duplicateBaseline), "Duplicate");
    }

    private static void ParentCyclesRejected()
    {
        using var exports = Empty("ExportCycle");
        var root = exports.CreatePackageExport("Root");
        var child = exports.CreateExport("Child", "Object", root, indexed: false);
        root.idxLink = child.UIndex;
        Reject(() => PackageIntegrity.Verify(exports), "circular parent");

        using var imports = Empty("ImportCycle");
        var import = imports.CreatePackageImport("Root");
        var otherImport = imports.CreatePackageImport("OtherRoot");
        import.idxLink = otherImport.UIndex;
        otherImport.idxLink = import.UIndex;
        Reject(() => PackageIntegrity.CaptureIssues(imports), "circular parent");
    }

    private static void BinaryReportRejected()
    {
        using var source = Empty("Donor");
        using var destination = Empty("Destination");
        var donor = source.CreateExport("Asset", "Object", indexed: false);
        var options = new RelinkerOptionsPackage();
        var root = (ExportEntry)EntryImporter.ImportExport(destination, donor, 0, options);
        Relinker.RelinkAll(options);
        options.RelinkReport.Add(new EntryStringPair(root, "Injected binary relinking failure"));
        Reject(() => MaterialisationVerifier.Verify(root, source.Game, options), "Injected binary");
    }

    private static void MaterialiseValidMesh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-Donor-{Guid.NewGuid():N}.pcc");
        try
        {
            CreateDonor(path, broken: false);
            using var destination = Empty("Destination");
            var root = ExternalSkeletalMeshMaterializer.Materialize(destination, path, "MFE_Donor.Asset", "SkeletalMesh");
            TestAssert.Equal("MFE_Donor.Asset", root.InstancedFullPath);
            PackageIntegrity.Verify(destination);
        }
        finally { File.Delete(path); }
    }

    private static void RootExportUsesOfficialDonor()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var officialPath = Path.Combine(Path.GetTempPath(), $"BIOG_Test-{Guid.NewGuid():N}.pcc");
        try
        {
            using (var official = MEPackageHandler.CreateMemoryEmptyPackage("BIOG_Test.pcc", MEGame.LE1))
            {
                var packageRoot = official.CreatePackageExport("BIOG_Test");
                var marker = official.CreateExport("OfficialMarker", "Object", packageRoot, indexed: false);
                var mesh = official.CreateExport("Shared_Mesh", "SkeletalMesh", packageRoot, indexed: false);
                mesh.WriteBinary(SkeletalMesh.Create());
                mesh.WriteProperty(new ObjectProperty(marker, "MarkerReference"));
                official.Save(officialPath);
            }

            using var source = MEPackageHandler.CreateMemoryEmptyPackage("BIOG_Test_Mod.pcc", MEGame.LE1);
            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("RootExportOfficialDonor.pcc", MEGame.LE1);
            var sourcePackage = source.CreatePackageExport("BIOG_Test");
            var modMarker = source.CreateExport("ModMarker", "Object", sourcePackage, indexed: false);
            var modMesh = source.CreateExport("Shared_Mesh", "SkeletalMesh", sourcePackage, indexed: false);
            modMesh.WriteBinary(SkeletalMesh.Create());
            modMesh.WriteProperty(new ObjectProperty(modMarker, "MarkerReference"));
            var root = source.CreateExport("Face", "Object", indexed: false);
            root.WriteProperty(new ObjectProperty(modMesh, "MeshReference"));
            var texturePath = "BIOG_Humanoid_MASTER_MTR_R.Skin_AsariHED_SpecMulitplier_Mask";
            TestAssert.Equal(MaterialOracleEntryKind.Import,
                MaterialDependencyOracle.Instance.GetKind(MEGame.LE1, "Texture2D", texturePath));
            var textureParent = source.CreatePackageImport("BIOG_Humanoid_MASTER_MTR_R");
            var nativeTexture = source.CreateImport("Texture2D", "Skin_AsariHED_SpecMulitplier_Mask", textureParent);
            root.WriteProperty(new ObjectProperty(nativeTexture, "NativeTexture"));

            var resolver = new InstalledGameAssetResolver(
                loadedFiles: _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BIOG_Test.pcc"] = officialPath
                },
                snapshot: _ => null);
            var staged = PccPackageWorkflow.ImportRootPreservingStructure(
                destination, root, installedAssets: resolver);

            const string meshPath = "BIOG_Test.Shared_Mesh";
            var installedMesh = destination.FindExport(meshPath, "SkeletalMesh");
            var actualInstalledMesh = installedMesh ?? throw new InvalidOperationException("The official mesh donor was not staged.");
            var meshReference = PackageIntegrity.References(staged)["property.MeshReference[0]"];
            TestAssert.Equal(actualInstalledMesh.UIndex, meshReference);
            var officialMarker = destination.FindExport("BIOG_Test.OfficialMarker", "Object");
            TestAssert.True(officialMarker is not null,
                "The selected official mesh's dependency marker was not staged.");
            TestAssert.Equal(officialMarker!.UIndex,
                PackageIntegrity.References(actualInstalledMesh)["property.MarkerReference[0]"]);
            TestAssert.True(destination.FindExport("BIOG_Test.ModMarker", "Object") is null,
                "The modded same-path mesh was staged instead of the official donor.");
            TestAssert.Equal(1, destination.Exports.Count(entry => entry.ClassName == "SkeletalMesh" &&
                entry.InstancedFullPath.Equals(meshPath, StringComparison.OrdinalIgnoreCase)));
            TestAssert.True(destination.FindEntry(texturePath, "Texture2D") is ImportEntry,
                "A native corpus Texture2D import was not preserved.");
            TestAssert.True(destination.FindExport(texturePath, "Texture2D") is null,
                "An official donor preseed replaced a native corpus Texture2D import.");
        }
        finally { File.Delete(officialPath); }
    }

    private static void StagedDonorOwnsAliasVerification()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        // Use a base Material: official graph substitution applies to it.
        // A changed MIC must instead retain its authored values under a new leaf.
        var officialPath = Path.Combine(Path.GetTempPath(), $"BIOG_Test-Official-{Guid.NewGuid():N}.pcc");
        var outputPath = Path.Combine(Path.GetTempPath(), $"MFE-OfficialGraph-{Guid.NewGuid():N}.pcc");
        try
        {
            using (var official = MEPackageHandler.CreateMemoryEmptyPackage("BIOG_Test.pcc", MEGame.LE1))
            {
                var packageRoot = official.CreatePackageExport("BIOG_Test");
                var marker = official.CreateExport("OfficialMarker", "Object", packageRoot, indexed: false);
                var material = official.CreateExport("SharedMaterial", "Material", packageRoot, indexed: false);
                material.WriteBinary(Material.Create());
                material.WriteProperty(new ObjectProperty(marker, "MarkerReference"));
                official.Save(officialPath);
            }

            using var source = MEPackageHandler.CreateMemoryEmptyPackage("BIOG_Test_Mod.pcc", MEGame.LE1);
            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("OfficialGraphDestination.pcc", MEGame.LE1);
            var sourcePackageRoot = source.CreatePackageExport("BIOG_Test");
            var modMarker = source.CreateExport("ModOnlyMarker", "Object", sourcePackageRoot, indexed: false);
            var modMaterial = source.CreateExport("SharedMaterial", "Material", sourcePackageRoot, indexed: false);
            modMaterial.WriteBinary(Material.Create());
            modMaterial.WriteProperty(new ObjectProperty(modMarker, "MarkerReference"));
            var authoredRoot = source.CreateExport("Face", "Object", indexed: false);
            authoredRoot.WriteProperty(new ObjectProperty(modMaterial, "MaterialReference"));
            var resolver = new InstalledGameAssetResolver(
                loadedFiles: game => game == MEGame.LE1
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BIOG_Test.pcc"] = officialPath }
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                snapshot: _ => null);

            var stagedRoot = PccPackageWorkflow.ImportDependencyGraph(
                destination, authoredRoot, destinationParent: null, textureCatalog: null,
                preferBiogTextures: false, installedAssets: resolver);
            var stagedMaterial = destination.FindExport("BIOG_Test.SharedMaterial", "Material");
            var officialMarker = destination.FindExport("BIOG_Test.OfficialMarker", "Object");
            TestAssert.True(stagedMaterial is not null && officialMarker is not null,
                "The official material graph was not staged.");
            TestAssert.True(destination.FindExport("BIOG_Test.ModOnlyMarker", "Object") is null,
                "A dependency used only by the ignored mod material was staged.");
            TestAssert.Equal(stagedMaterial!.UIndex,
                PackageIntegrity.References(stagedRoot)["property.MaterialReference[0]"]);
            TestAssert.Equal(officialMarker!.UIndex,
                PackageIntegrity.References(stagedMaterial)["property.MarkerReference[0]"]);
            PackageIntegrity.Verify(destination);
            destination.Save(outputPath);
            using (var reopened = MEPackageHandler.OpenMEPackage(outputPath, forceLoadFromDisk: true))
            {
                var savedMaterial = reopened.FindExport("BIOG_Test.SharedMaterial", "Material");
                TestAssert.True(savedMaterial is not null, "The official material was lost after reopening.");
                TestAssert.Equal("BIOG_Test.OfficialMarker",
                    reopened.GetEntry(PackageIntegrity.References(savedMaterial!)["property.MarkerReference[0]"])!.InstancedFullPath);
            }

            RejectDonorAlias(source, destination, officialPath, authoredRoot, modMaterial, stagedRoot,
                "does not identify the staged donor");
            RejectCorruptedSelectedDonor(source, officialPath, "Required relink failed");
        }
        finally
        {
            File.Delete(officialPath);
            File.Delete(outputPath);
        }
    }

    private static void RejectDonorAlias(
        IMEPackage source,
        IMEPackage destination,
        string officialPath,
        ExportEntry root,
        ExportEntry donorAlias,
        ExportEntry stagedRoot,
        string message)
    {
        using var official = MEPackageHandler.OpenMEPackage(officialPath, forceLoadFromDisk: true);
        var targetMaterial = destination.FindExport("BIOG_Test.SharedMaterial", "Material")!;
        var targetMarker = destination.FindExport("BIOG_Test.OfficialMarker", "Object")!;
        var unrelated = source.CreateExport("UnrelatedMaterial", "Material", indexed: false);
        unrelated.WriteBinary(Material.Create());
        var officialMaterial = official.Exports.First(entry =>
            entry.ClassName == "Material" && entry.ObjectName.Instanced == "SharedMaterial");
        var options = new RelinkerOptionsPackage();
        options.CrossPackageMap[root] = stagedRoot;
        options.CrossPackageMap[officialMaterial] = targetMaterial;
        options.CrossPackageMap[donorAlias] = targetMaterial;
        options.CrossPackageMap[unrelated] = targetMaterial;
        var officialMarker = officialMaterial.FileRef.GetEntry(
            PackageIntegrity.References(officialMaterial)["property.MarkerReference[0]"]) as ExportEntry
            ?? throw new InvalidDataException("The selected official material lost its marker donor.");
        options.CrossPackageMap[officialMarker] = targetMarker;
        var staged = new Dictionary<ExportEntry, (ExportEntry Source, string TargetPath)>
        {
            [targetMaterial] = (officialMaterial, targetMaterial.InstancedFullPath),
            [targetMarker] = (officialMarker, targetMarker.InstancedFullPath),
            [stagedRoot] = (root, stagedRoot.InstancedFullPath)
        };
        Reject(() => MaterialisationVerifier.Verify(stagedRoot, source.Game, options, stagedCopies: staged), message);
    }

    private static void RejectCorruptedSelectedDonor(IMEPackage source, string officialPath, string message)
    {
        using var official = MEPackageHandler.OpenMEPackage(officialPath, forceLoadFromDisk: true);
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("CorruptedOfficialGraph.pcc", MEGame.LE1);
        var officialMaterial = official.FindExport("BIOG_Test.SharedMaterial", "Material")!;
        var officialMarker = official.FindExport("BIOG_Test.OfficialMarker", "Object")!;
        var stagedMarker = destination.CreateExport("OfficialMarker", "Object",
            destination.CreatePackageExport("BIOG_Test"), indexed: false);
        var wrongMarker = destination.CreateExport("WrongMarker", "Object", indexed: false);
        var stagedMaterial = destination.CreateExport("SharedMaterial", "Material",
            destination.FindExport("BIOG_Test", "Package"), indexed: false);
        stagedMaterial.WriteBinary(Material.Create());
        stagedMaterial.WriteProperty(new ObjectProperty(wrongMarker, "MarkerReference"));
        var copiedRoot = destination.CreateExport("CorruptRoot", "Object", indexed: false);
        var options = new RelinkerOptionsPackage();
        options.CrossPackageMap[officialMaterial] = stagedMaterial;
        options.CrossPackageMap[officialMarker] = stagedMarker;
        options.CrossPackageMap[copiedRoot] = copiedRoot;
        var staged = new Dictionary<ExportEntry, (ExportEntry Source, string TargetPath)>
        {
            [stagedMaterial] = (officialMaterial, stagedMaterial.InstancedFullPath),
            [stagedMarker] = (officialMarker, stagedMarker.InstancedFullPath),
            [copiedRoot] = (copiedRoot, copiedRoot.InstancedFullPath)
        };
        Reject(() => MaterialisationVerifier.Verify(copiedRoot, source.Game, options, stagedCopies: staged), message);
    }

    private static void RootExportEmbedsExplicitTextureLeaf()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const string texturePath = "BIOG_Humanoid_MASTER_MTR_R.Skin_AsariHED_SpecMulitplier_Mask";
        TestAssert.Equal(MaterialOracleEntryKind.Import,
            MaterialDependencyOracle.Instance.GetKind(MEGame.LE1, "Texture2D", texturePath));
        var outputPath = Path.Combine(Path.GetTempPath(), $"MFE-ExplicitTextureLeaf-{Guid.NewGuid():N}.pcc");
        try
        {
            using var source = MEPackageHandler.CreateMemoryEmptyPackage("BIOG_ExplicitSource.pcc", MEGame.LE1);
            var sourceRoot = source.CreateExport("Face", "Object", indexed: false);
            var sourceParent = source.CreatePackageImport("BIOG_Humanoid_MASTER_MTR_R");
            var sourceTexture = source.CreateImport(
                "Texture2D", "Skin_AsariHED_SpecMulitplier_Mask", sourceParent);
            sourceRoot.WriteProperty(new ObjectProperty(sourceTexture, "TextureReference"));

            using var selectedDonor = MEPackageHandler.CreateMemoryEmptyPackage(
                "BIOG_Humanoid_MASTER_MTR_R.pcc", MEGame.LE1);
            var donorParent = selectedDonor.CreatePackageExport("BIOG_Humanoid_MASTER_MTR_R");
            using (var corpus = MEPackageHandler.OpenMEPackage(
                       Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc"), forceLoadFromDisk: true))
            {
                var textureSource = corpus.Exports.First(export =>
                    export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                    new Texture2D(export).Mips.Any(mip => mip.storageType != StorageTypes.empty));
                var donorTextureEntry = EntryImporter.ImportExport(
                    selectedDonor,
                    textureSource,
                    donorParent.UIndex,
                    new RelinkerOptionsPackage
                    {
                        ImportExportDependencies = false,
                        GenerateImportsForGlobalFiles = false
                    });
                if (donorTextureEntry is not ExportEntry importedTexture)
                    throw new InvalidDataException("The selected Texture2D donor could not be cloned.");
                importedTexture.ObjectName = new NameReference("Skin_AsariHED_SpecMulitplier_Mask");
            }
            var donorTexture = selectedDonor.FindExport(texturePath, "Texture2D")
                               ?? throw new InvalidDataException("The selected Texture2D donor is missing.");
            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("ExplicitTextureOutput.pcc", MEGame.LE1);

            using (var competingDonor = MEPackageHandler.CreateMemoryEmptyPackage("CompetingTexture.pcc", MEGame.LE1))
            {
                var competingParent = competingDonor.CreatePackageExport("BIOG_Humanoid_MASTER_MTR_R");
                var competingTexture = competingDonor.CreateExport(
                    "Skin_AsariHED_SpecMulitplier_Mask", "Texture2D", competingParent, indexed: false);
                using var rejectedDestination = MEPackageHandler.CreateMemoryEmptyPackage(
                    "RejectedTextureOutput.pcc", MEGame.LE1);
                Reject(() => PccPackageWorkflow.ImportRootPreservingStructure(
                        rejectedDestination, sourceRoot, explicitTextureDonors: [donorTexture, competingTexture]),
                    "Multiple explicit Texture2D donors");
                TestAssert.True(rejectedDestination.FindExport(texturePath, "Texture2D") is null,
                    "Conflicting explicit texture donors mutated the destination before rejection.");
            }

            var staged = PccPackageWorkflow.ImportRootPreservingStructure(
                destination, sourceRoot, explicitTextureDonors: [donorTexture]);
            var snapshot = PccPackageWorkflow.CaptureDependencyGraph(staged);
            TestAssert.Equal(1, destination.Exports.Count(entry => entry.ClassName == "Texture2D" &&
                entry.InstancedFullPath.Equals(texturePath, StringComparison.OrdinalIgnoreCase)));
            TestAssert.Equal(0, destination.Imports.Count(entry => entry.ClassName == "Texture2D" &&
                entry.InstancedFullPath.Equals(texturePath, StringComparison.OrdinalIgnoreCase)));
            PccTextureDependencyResolver.VerifyBulkDataPreserved(
                donorTexture, destination.FindExport(texturePath, "Texture2D")!);

            destination.Save(outputPath);
            using var reopened = MEPackageHandler.OpenMEPackage(outputPath, forceLoadFromDisk: true);
            PccPackageWorkflow.VerifyDependencyGraph(reopened, snapshot);
        }
        finally { File.Delete(outputPath); }
    }

    private static void NewPackageTextureDonorIntent()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const string texturePath = "BIOG_Humanoid_MASTER_MTR_R.Skin_AsariHED_SpecMulitplier_Mask";
        TestAssert.Equal(MaterialOracleEntryKind.Import,
            MaterialDependencyOracle.Instance.GetKind(MEGame.LE1, "Texture2D", texturePath));
        var sourcePath = Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc");
        var donorPath = Path.Combine(Path.GetTempPath(), $"BIOG_Humanoid_MASTER_MTR_R-{Guid.NewGuid():N}.pcc");
        var unflaggedOutput = Path.Combine(Path.GetTempPath(), $"MFE-UnflaggedTexture-{Guid.NewGuid():N}.pcc");
        var explicitOutput = Path.Combine(Path.GetTempPath(), $"MFE-ExplicitTexture-{Guid.NewGuid():N}.pcc");
        try
        {
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage(
                       "BIOG_Humanoid_MASTER_MTR_R.pcc", MEGame.LE1))
            using (var corpus = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
            {
                var donorRoot = donor.CreatePackageExport("BIOG_Humanoid_MASTER_MTR_R");
                var sourceTexture = corpus.Exports.First(export =>
                    export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                    new Texture2D(export).Mips.Any(mip => mip.storageType != StorageTypes.empty));
                var cloned = EntryImporter.ImportExport(donor, sourceTexture, donorRoot.UIndex,
                    new RelinkerOptionsPackage
                    {
                        ImportExportDependencies = false,
                        GenerateImportsForGlobalFiles = false
                    }) as ExportEntry ?? throw new InvalidDataException("Could not clone the synthetic selected donor.");
                cloned.ObjectName = new NameReference("Skin_AsariHED_SpecMulitplier_Mask");
                donor.Save(donorPath);
            }

            using var donorPackage = MEPackageHandler.OpenMEPackage(donorPath, forceLoadFromDisk: true);
            var selectedTexture = donorPackage.FindExport(texturePath, "Texture2D")
                                  ?? throw new InvalidDataException("The synthetic selected donor has the wrong canonical path.");
            using var reader = new MorphFacePackageReader();
            string facePath;
            using (var sourcePackage = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
            {
                facePath = sourcePackage.Exports.First(entry =>
                    entry.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase)).InstancedFullPath;
            }
            var document = reader.Load(sourcePath, facePath).Document;

            MorphFaceDocument Draft(bool explicitlySelected) => document with
            {
                MaterialOverrides = new MorphFaceMaterialOverrides(document.MaterialOverrides.Source, [], [],
                [new TextureMaterialOverride("SelectedTestTexture",
                    new AssetIdentity(donorPath, texturePath, selectedTexture.UIndex, "Texture2D")
                    { IsExplicitTextureDonor = explicitlySelected })])
            };

            _ = new MorphFacePackageWriter().SaveMorphToPackage(
                Draft(explicitlySelected: false), sourcePath, unflaggedOutput, createNewPackage: true);
            using (var saved = MEPackageHandler.OpenMEPackage(unflaggedOutput, forceLoadFromDisk: true))
            {
                TestAssert.True(saved.FindEntry(texturePath, "Texture2D") is ImportEntry,
                    "An unflagged external texture replaced the native stock import role.");
                TestAssert.True(saved.FindExport(texturePath, "Texture2D") is null,
                    "An unflagged external texture was embedded alongside the native import.");
            }

            _ = new MorphFacePackageWriter().SaveMorphToPackage(
                Draft(explicitlySelected: true), sourcePath, explicitOutput, createNewPackage: true);
            using (var saved = MEPackageHandler.OpenMEPackage(explicitOutput, forceLoadFromDisk: true))
            {
                var embedded = saved.FindExport(texturePath, "Texture2D");
                TestAssert.True(embedded is not null,
                    "The explicitly selected external texture did not replace the native import role.");
                TestAssert.True(embedded!.IsForcedExport,
                    "The explicitly selected leaf lost its package-qualified ForcedExport identity.");
                TestAssert.Equal(0, saved.Imports.Count(entry => entry.ClassName == "Texture2D" &&
                    entry.InstancedFullPath.Equals(texturePath, StringComparison.OrdinalIgnoreCase)));
                TestAssert.Equal(1, saved.Exports.Count(entry => entry.ClassName == "Texture2D" &&
                    entry.InstancedFullPath.Equals(texturePath, StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally
        {
            File.Delete(donorPath);
            File.Delete(unflaggedOutput);
            File.Delete(explicitOutput);
        }
    }

    private static void TemporaryEditSeekfreeTextureRoundTrip()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const string objectName = "HMF_HED_PROCustom_Military_Diff";
        var sourcePath = Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"MFE-SeekfreeRoundTrip-{Guid.NewGuid():N}.pcc");
        var donorPath = Path.Combine(Path.GetTempPath(), $"BIOG_MFE_SeekfreeRoundTrip_{Guid.NewGuid():N}.pcc");
        try
        {
            using (var source = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage(donorPath, MEGame.LE1))
            {
                var diffuse = donor.CreatePackageExport("Diffuse", forcedExport: false);
                var sourceTexture = source.Exports.First(export =>
                    export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                    new Texture2D(export).Mips.Any(mip => mip.storageType != StorageTypes.empty));
                var texture = EntryImporter.ImportExport(donor, sourceTexture, diffuse.UIndex,
                    new RelinkerOptionsPackage
                    {
                        ImportExportDependencies = false,
                        GenerateImportsForGlobalFiles = false
                    }) as ExportEntry ?? throw new InvalidDataException("Could not clone the seekfree texture fixture.");
                texture.ObjectName = new NameReference(objectName);
                texture.ExportFlags &= ~UnrealFlags.EExportFlags.ForcedExport;
                TestAssert.True(!texture.IsForcedExport,
                    "The seekfree fixture's texture was unexpectedly ForcedExport.");
                donor.Save(donorPath);
            }

            using var donorPackage = MEPackageHandler.OpenMEPackage(donorPath, forceLoadFromDisk: true);
            var sourceExport = donorPackage.Exports.Single(export =>
                export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                export.ObjectName.Instanced.Equals(objectName, StringComparison.OrdinalIgnoreCase));
            var canonicalPath = PccAssetPathPolicy.FromExport(sourceExport);
            TestAssert.True(!sourceExport.InstancedFullPath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase),
                "The fixture must model a package-local seekfree export path.");

            using var reader = new MorphFacePackageReader();
            var decoded = reader.LoadTexture(donorPath, canonicalPath,
                new MaterialParameterDefinition("HED_Diff", "Diffuse", "skin", MaterialParameterKind.Texture,
                    HeadMaterialFamily.Skin, TextureRole: TextureRole.Diffuse,
                    ColorSpace: TextureColorSpace.Srgb, AlphaPolicy: TextureAlphaPolicy.Ignore));
            TestAssert.True(canonicalPath.Equals(decoded.Source.InstancedPath, StringComparison.OrdinalIgnoreCase),
                $"The selected seekfree texture did not retain its PCC-facing canonical path: {decoded.Source.InstancedPath}.");

            File.Copy(sourcePath, destinationPath);
            string facePath;
            using (var package = MEPackageHandler.OpenMEPackage(destinationPath, forceLoadFromDisk: true))
            {
                TestAssert.True(PackageIntegrity.FindExactEntry(package, canonicalPath, "Texture2D") is null,
                    "The fixture unexpectedly contains an import or export at the selected texture identity.");
                package.CreatePackageImport(canonicalPath.Split('.')[0]);
                facePath = package.Exports.First(export => export.ClassName == "BioMorphFace").InstancedFullPath;
                package.Save();
            }
            var loaded = reader.Load(destinationPath, facePath);
            var draft = loaded.Document with
            {
                MaterialOverrides = loaded.Document.MaterialOverrides with
                {
                    Textures = [new TextureMaterialOverride("HED_Diff", decoded.Source)]
                }
            };

            _ = new MorphFacePackageWriter().SaveExisting(draft);
            using var reopened = MEPackageHandler.OpenMEPackage(destinationPath, forceLoadFromDisk: true);
            var savedFace = reopened.FindExport(facePath, "BioMorphFace")
                            ?? throw new InvalidDataException("The edited morph did not survive reopening.");
            var savedOverride = savedFace.GetProperty<ObjectProperty>("m_oMaterialOverrides")?.ResolveToEntry(reopened) as ExportEntry
                                ?? throw new InvalidDataException("The edited material override did not survive reopening as a local export.");
            var savedTexture = savedOverride.GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides")?
                .Single(item => item.GetProp<NameProperty>("nName")?.Value.Instanced == "HED_Diff")
                .GetProp<ObjectProperty>("m_pTexture")?.ResolveToEntry(reopened)
                ?? throw new InvalidDataException("HED_Diff did not survive reopening.");
            TestAssert.True(string.Equals(canonicalPath, savedTexture.InstancedFullPath, StringComparison.OrdinalIgnoreCase),
                $"The saved temporary edit changed the canonical selected texture path to '{savedTexture.InstancedFullPath}'.");
            TestAssert.True(savedTexture is ExportEntry { IsForcedExport: true } &&
                savedTexture.Parent is ExportEntry && savedTexture.Parent.Parent is ImportEntry,
                "The saved texture lost its ForcedExport flag or mixed import/export package ancestry.");
        }
        finally
        {
            File.Delete(destinationPath);
            File.Delete(donorPath);
        }
    }

    private static void WriterRetainsExactTextureImport(bool explicitSelection)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var destinationPath = Path.Combine(Path.GetTempPath(), $"MFE-ExactImport-{Guid.NewGuid():N}.pcc");
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-ExactImportDonor-{Guid.NewGuid():N}.pcc");
        const string texturePath = "MFE_ExactImport.Eye.EYE_Diff";
        try
        {
            File.Copy(Path.GetFullPath("tests/Global Morphs/LE3 GlobalMorphs.pcc"), destinationPath);
            CreateTextureImport(destinationPath, texturePath);
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage("MFE_ExactImportDonor.pcc", MEGame.LE3))
            {
                var root = donor.CreatePackageExport("MFE_ExactImport");
                var eye = donor.CreatePackageExport("Eye", root);
                donor.CreateExport("EYE_Diff", "Texture2D", eye, indexed: false);
                donor.Save(donorPath);
            }

            string facePath;
            using (var package = MEPackageHandler.OpenMEPackage(destinationPath, forceLoadFromDisk: true))
                facePath = package.Exports.First(entry => entry.ClassName == "BioMorphFace").InstancedFullPath;
            using var reader = new MorphFacePackageReader();
            var loaded = reader.Load(destinationPath, facePath);
            var draft = loaded.Document with
            {
                MaterialOverrides = loaded.Document.MaterialOverrides with
                {
                    Textures = [new TextureMaterialOverride(
                        "EYE_Diff",
                        new AssetIdentity(donorPath, texturePath, 0, "Texture2D")
                        {
                            IsExplicitTextureDonor = explicitSelection
                        })]
                }
            };

            if (explicitSelection)
            {
                var originalBytes = File.ReadAllBytes(destinationPath);
                Reject(() => new MorphFacePackageWriter().SaveExisting(draft), "import occupies");
                TestAssert.True(originalBytes.SequenceEqual(File.ReadAllBytes(destinationPath)),
                    "A rejected explicit texture selection changed the PCC bytes.");
                TestAssert.True(!Directory.EnumerateFiles(Path.GetDirectoryName(destinationPath)!,
                    $".{Path.GetFileName(destinationPath)}.*.tmp").Any(),
                    "A rejected texture collision retained a temporary PCC candidate.");
            }
            else new MorphFacePackageWriter().SaveExisting(draft);
            using var reopened = MEPackageHandler.OpenMEPackage(destinationPath, forceLoadFromDisk: true);
            var imports = reopened.Imports.Count(entry =>
                entry.ClassName == "Texture2D" && entry.InstancedFullPath == texturePath);
            var exports = reopened.Exports.Count(entry =>
                entry.ClassName == "Texture2D" && entry.InstancedFullPath == texturePath);
            TestAssert.Equal(1, imports);
            TestAssert.Equal(0, exports);
        }
        finally
        {
            File.Delete(destinationPath);
            File.Delete(donorPath);
        }
    }

    private static void CreateTextureImport(string packagePath, string texturePath)
    {
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var segments = texturePath.Split('.');
        IEntry? parent = null;
        var path = string.Empty;
        for (var index = 0; index < segments.Length; index++)
        {
            path = path.Length == 0 ? segments[index] : $"{path}.{segments[index]}";
            var className = index == segments.Length - 1 ? "Texture2D" : "Package";
            parent = package.FindEntry(path, className) ?? (index == 0
                ? package.CreatePackageImport(segments[index])
                : package.CreateImport(
                    className,
                    segments[index],
                    parent));
        }
        package.Save(packagePath);
    }

    private static void SaveWithImportedMeshParent(MEGame game)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"MFE-ImportedParentSave-{Guid.NewGuid():N}.pcc");
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-Donor-{Guid.NewGuid():N}.pcc");
        try
        {
            File.Copy(Path.GetFullPath($"tests/Global Morphs/{game} GlobalMorphs.pcc"), sourcePath);
            CreateDonor(donorPath, broken: false, game: game);
            int parentIndex;
            string facePath;
            using (var package = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
            {
                parentIndex = package.CreatePackageImport("MFE_Donor").UIndex;
                facePath = game == MEGame.LE2
                    ? package.FindExport("ASA.asari_face_01", "BioMorphFace")!.InstancedFullPath
                    : package.Exports.First(entry => entry.ClassName == "BioMorphFace").InstancedFullPath;
                package.Save();
            }
            using var reader = new MorphFacePackageReader();
            var draft = reader.Load(sourcePath, facePath).Document with
            {
                HairMeshReference = new(donorPath, "MFE_Donor.Asset", 2, "SkeletalMesh"),
                OtherMeshReferences = []
            };
            _ = new MorphFacePackageWriter().SaveExisting(draft);
            using var reopened = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
            var mesh = reopened.FindExport("MFE_Donor.Asset", "SkeletalMesh")!;
            TestAssert.True(mesh is not null && mesh.Parent is ImportEntry,
                "The saved mesh was not materialised beneath the existing imported package.");
            TestAssert.Equal(parentIndex, mesh!.idxLink);
            TestAssert.True(mesh.IsForcedExport, "The embedded mesh lost its canonical ForcedExport identity.");
            TestAssert.True(reopened.FindExport("MFE_Donor", "Package") is null,
                "Saving created a duplicate package export alongside the imported package.");
            var savedHair = reopened.FindExport(facePath, "BioMorphFace")!
                .GetProperty<ObjectProperty>("m_oHairMesh")!.ResolveToEntry(reopened);
            TestAssert.Equal(mesh.UIndex, savedHair!.UIndex);
        }
        finally { File.Delete(sourcePath); File.Delete(donorPath); }
    }

    private static void SaveFailurePreservesOriginal()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"MFE-Integrity-{Guid.NewGuid():N}.pcc");
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-Donor-{Guid.NewGuid():N}.pcc");
        try
        {
            File.Copy(Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc"), sourcePath);
            CreateDonor(donorPath, broken: true);
            string facePath;
            using (var package = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
                facePath = package.Exports.First(entry => entry.ClassName == "BioMorphFace").InstancedFullPath;
            using var reader = new MorphFacePackageReader();
            var draft = reader.Load(sourcePath, facePath).Document with
            {
                HairMeshReference = new(donorPath, "MFE_Donor.Asset", 2, "SkeletalMesh"),
                OtherMeshReferences = []
            };
            var before = File.ReadAllBytes(sourcePath);
            Reject(() => new MorphFacePackageWriter().SaveExisting(draft), "relink");
            TestAssert.True(before.SequenceEqual(File.ReadAllBytes(sourcePath)), "A failed save changed the original PCC.");
            using var reopened = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
            TestAssert.True(reopened.FindExport(facePath, "BioMorphFace") is not null, "The original PCC could not be reopened.");
        }
        finally { File.Delete(sourcePath); File.Delete(donorPath); }
    }

    private static void InterruptedPccSavePreservesSource()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"MFE-InterruptedSave-{Guid.NewGuid():N}.pcc");
        var markerPath = Path.Combine(Path.GetTempPath(), $"MFE-InterruptedSave-{Guid.NewGuid():N}.ready");
        Process? worker = null;
        try
        {
            LegendaryExplorerCoreRuntime.Initialize();
            File.Copy(Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc"), sourcePath);
            var before = File.ReadAllBytes(sourcePath);
            var candidatePrefix = $".{Path.GetFileName(sourcePath)}.";
            var directory = Path.GetDirectoryName(sourcePath)!;
            var candidatesBefore = Directory.EnumerateFiles(directory, $"{candidatePrefix}*.tmp")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var workerPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The test worker path was unavailable.");
            var startInfo = new ProcessStartInfo(workerPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add("--d4-interrupted-save-worker");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add(markerPath);
            worker = Process.Start(startInfo)
                     ?? throw new InvalidOperationException("The interrupted-save worker could not be started.");
            var child = worker;

            var started = Stopwatch.StartNew();
            while (!File.Exists(markerPath) && !child.HasExited && started.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(50);
            }

            TestAssert.True(File.Exists(markerPath) && !child.HasExited,
                "The save worker did not reach the pre-install interruption point.");
            var temporaryPath = File.ReadAllText(markerPath);
            TestAssert.True(File.Exists(temporaryPath),
                "The hard-stopped worker did not leave its adjacent candidate PCC.");
            child.Kill(entireProcessTree: true);
            child.WaitForExit(10_000);

            TestAssert.True(before.SequenceEqual(File.ReadAllBytes(sourcePath)),
                "An interrupted save changed the original PCC bytes.");
            string facePath;
            using (var package = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
                facePath = package.Exports.First(entry => entry.ClassName == "BioMorphFace").InstancedFullPath;
            using (var reopened = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
                TestAssert.True(reopened.FindExport(facePath, "BioMorphFace") is not null,
                    "The original PCC could not be reopened after an interrupted save.");
            TestAssert.True(Path.GetFileName(temporaryPath).StartsWith(candidatePrefix, StringComparison.OrdinalIgnoreCase),
                "The interrupted candidate did not use the adjacent temporary naming contract.");
            TestAssert.True(!string.Equals(temporaryPath, sourcePath, StringComparison.OrdinalIgnoreCase),
                "The interrupted candidate path could be mistaken for the installed PCC.");
            File.Delete(temporaryPath);
            var candidatesAfter = Directory.EnumerateFiles(directory, $"{candidatePrefix}*.tmp")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            TestAssert.True(candidatesAfter.SetEquals(candidatesBefore),
                "The interrupted save candidate could not be recovered and cleaned.");
        }
        finally
        {
            if (worker is { HasExited: false })
            {
                worker.Kill(entireProcessTree: true);
                worker.WaitForExit(10_000);
            }
            worker?.Dispose();
            PccPackageWorkflow.BeforeAtomicReplaceForTesting = null;
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(markerPath))
            {
                var orphanedCandidate = File.ReadAllText(markerPath);
                if (File.Exists(orphanedCandidate)) File.Delete(orphanedCandidate);
            }
            if (File.Exists(markerPath)) File.Delete(markerPath);
        }
    }

    internal static int RunInterruptedSaveWorker(string sourcePath, string markerPath)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        string facePath;
        using (var package = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
            facePath = package.Exports.First(entry => entry.ClassName == "BioMorphFace").InstancedFullPath;
        var draft = new MorphFacePackageReader().Load(sourcePath, facePath).Document;
        PccPackageWorkflow.BeforeAtomicReplaceForTesting = (temporaryPath, destination) =>
        {
            TestAssert.True(File.Exists(temporaryPath), "The atomic-save worker reached the hook before writing its candidate.");
            TestAssert.Equal(sourcePath, destination);
            File.WriteAllText(markerPath, temporaryPath);
            while (true)
            {
                Thread.Sleep(1000);
            }
        };
        try
        {
            _ = new MorphFacePackageWriter().SaveExisting(draft);
            return 1;
        }
        finally
        {
            PccPackageWorkflow.BeforeAtomicReplaceForTesting = null;
        }
    }

    private static void CreateDonor(string path, bool broken, MEGame game = MEGame.LE1)
    {
        using var donor = Empty("Donor", game);
        var parent = donor.CreatePackageExport("MFE_Donor");
        var mesh = donor.CreateExport("Asset", "SkeletalMesh", parent, indexed: false);
        var binary = SkeletalMesh.Create();
        mesh.WriteBinary(binary);
        if (broken) mesh.WriteProperty(new ObjectProperty(999999, "RequiredDependency"));
        donor.Save(path);
    }

    private static void HairDonorsAndOmissions()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var source = MEPackageHandler.OpenMEPackage(Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc"), forceLoadFromDisk: true);
        var hair = source.Exports.First(entry => entry.ObjectName.Instanced == "HMM_HIR_PROShort01_MDL");
        foreach (var game in new[] { MEGame.LE2, MEGame.LE3 })
        {
            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("HairIntegrity.pcc", game);
            var template = Path.GetFullPath($"tests/Global Morphs/{game} GlobalMorphs.pcc");
            var result = MorphFaceAttachmentTransferEngine.Transfer(destination,
                MorphFacePackageReader.ToIdentity(hair), [], source.Game, template);
            // The reviewed LE3 catalogue deliberately omits PROShort01; LE2 has a donor.
            TestAssert.True(game == MEGame.LE2 ? result.Hair is ExportEntry : result.Hair is null && result.Warnings.Count > 0,
                $"PROShort01 did not follow the reviewed policy for {game}.");
            PackageIntegrity.Verify(destination);
            var missing = MorphFaceAttachmentTransferEngine.Transfer(destination,
                new AssetIdentity("Missing.pcc", "MFE_Missing.NoSuchHair", 1, "SkeletalMesh"), [], source.Game, template);
            TestAssert.True(missing.Hair is null && missing.Warnings.Count > 0, "An optional missing donor did not remain an omission warning.");
        }
    }

    private static void TextureFallbacks(bool importedParents)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var paths = new[]
        {
            "BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add2",
            "BIOG_ASA_HED_PROMorph_R.Adds.ASA_HED_PRO_Add3",
            "BIOG_ASA_HED_PROMorph_R.Masks.ASA_HED_PRO_Tat2",
            "BIOG_BAT_HED_PROMorph_R.PROBase.BAT_HED_PROMorph_Add4",
            "BIOG_SAL_HED_PROMorph_R.Add.SAL_HED_PRO_Add6",
            "BIOG_TUR_HED_PROMorph_R.Add.TUR_HED_PRO_Add2"
        };
        foreach (var game in new[] { MEGame.LE2, MEGame.LE3 })
        {
            using var destination = Empty();
            if (importedParents)
                foreach (var root in paths.Select(path => path.Split('.')[0]).Distinct())
                    destination.CreatePackageImport(root);
            var textures = paths.Select((path, index) => new TextureMaterialOverride($"Integrity{index}",
                new AssetIdentity(string.Empty, path, 0, "Texture2D"))).ToArray();
            var result = MorphFaceTextureTransferEngine.Transfer(destination, new MorphFaceMaterialData([], [], textures),
                new HashSet<string>(), new HashSet<string>(), textures.Select(value => value.Name).ToHashSet(),
                game, game == MEGame.LE2 ? "le2-asari" : "le3-asari",
                Path.GetFullPath($"tests/Global Morphs/{game} GlobalMorphs.pcc"),
                Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc"), [], []);
            TestAssert.Equal(paths.Length, result.MaterialData.Textures.Count);
            foreach (var texture in result.MaterialData.Textures)
            {
                var entry = destination.FindExport(texture.TextureReference!.InstancedPath, "Texture2D");
                TestAssert.True(entry is not null, $"{game} {texture.TextureReference.InstancedPath} was not materialised.");
                // Existing LE1 stock donors remain preferred. Only missing stock assets need embedding.
                if (result.Warnings.Any(warning => warning.StartsWith($"Embedded '{entry!.InstancedFullPath}'", StringComparison.Ordinal)))
                    TestAssert.True(new LegendaryExplorerCore.Unreal.Classes.Texture2D(entry!).GetTopMip().IsPackageStored,
                        $"{game} {texture.TextureReference.InstancedPath} was not package-stored.");
            }
            TestAssert.True(result.Warnings.Count(warning => warning.StartsWith("Embedded '", StringComparison.Ordinal)) >= 3,
                "The allowlist cases did not exercise missing-stock embedding.");
            PackageIntegrity.Verify(destination);
            if (importedParents)
            {
                var outputPath = Path.Combine(Path.GetTempPath(), $"MFE-ImportedTextureConversion-{Guid.NewGuid():N}.pcc");
                try
                {
                    var graphs = result.MaterialData.Textures.Select(texture => PccPackageWorkflow.CaptureDependencyGraph(
                        destination.FindExport(texture.TextureReference!.InstancedPath, "Texture2D")!)).ToArray();
                    destination.Save(outputPath);
                    using var reopened = MEPackageHandler.OpenMEPackage(outputPath, forceLoadFromDisk: true);
                    PackageIntegrity.Verify(reopened);
                    foreach (var graph in graphs)
                        PccPackageWorkflow.VerifyDependencyGraph(reopened, graph, verifyCorpusRoles: false);
                    foreach (var root in paths.Select(path => path.Split('.')[0]).Distinct())
                        TestAssert.True(reopened.FindEntry(root, "Package") is ImportEntry,
                            $"Texture conversion changed imported parent '{root}' into an export.");
                }
                finally { File.Delete(outputPath); }
            }
        }
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException exception)
        {
            TestAssert.True(exception.Message.Contains(message, StringComparison.OrdinalIgnoreCase), exception.Message);
            return;
        }
        throw new Exception($"Expected failure containing '{message}'.");
    }
}
