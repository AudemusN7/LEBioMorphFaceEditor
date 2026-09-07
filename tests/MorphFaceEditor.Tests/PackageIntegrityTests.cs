using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Tests;

/// <summary>Synthetic corruption cases and corpus transfers verify package integrity and optional-asset policy.</summary>
internal static class PackageIntegrityTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("import ancestor collision preserves original PCC bytes on save", () => SaveFailurePreservesOriginal(true)),
        new("required relink failure preserves original PCC bytes on save", () => SaveFailurePreservesOriginal(false)),
        new("relink rejects a valid destination index pointing at the wrong asset", WrongReferenceRejected),
        new("relink rejects retained binary failure reports even with valid references", BinaryReportRejected),
        new("package integrity rejects duplicate identities and imported parents", InvalidHierarchyRejected),
        new("package integrity permits only pre-existing structural issues", ExistingIssuesAreBaselined),
        new("package integrity rejects parent cycles before formatting entry paths", ParentCyclesRejected),
        new("import ancestor preflight reuses export ancestors and rejects nested imports", AncestorPreflight),
        new("same-game skeletal materialisation verifies retained references", MaterialiseValidMesh),
        new("relink preserves PROShort01 target donors and optional attachment omissions", HairDonorsAndOmissions),
        new("relink preserves every LE1 Add and Tat fallback policy", TextureFallbacks)
    ];

    private static IMEPackage Empty(string name = "Integrity")
    {
        LegendaryExplorerCoreRuntime.Initialize();
        return MEPackageHandler.CreateMemoryEmptyPackage(name + ".pcc", MEGame.LE1);
    }

    private static void AncestorPreflight()
    {
        using var package = Empty();
        var root = package.CreatePackageExport("Root");
        var group = package.CreateImport("Package", "Group", root);
        var count = package.ExportCount;
        Reject(() => PackageIntegrity.EnsurePackagePath(package, "Root.Group.Asset"), "Root.Group");
        TestAssert.Equal(count, package.ExportCount);
        TestAssert.Equal(root, PackageIntegrity.EnsurePackagePath(package, "Root.Asset"));
        TestAssert.Equal(group, package.FindEntry("Root.Group", "Package"));
    }

    private static void InvalidHierarchyRejected()
    {
        using var package = Empty();
        var import = package.CreatePackageImport("Root");
        var export = package.CreateExport("Asset", "Object", import, indexed: false);
        Reject(() => PackageIntegrity.Verify(package), "parent");
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

    private static void ExistingIssuesAreBaselined()
    {
        using var package = Empty();
        var importedRoot = package.CreatePackageImport("ExistingRoot");
        package.CreateExport("ExistingAsset", "Object", importedRoot, indexed: false);
        var baseline = PackageIntegrity.CaptureIssues(package);
        PackageIntegrity.Verify(package, baseline);

        var newImportedRoot = package.CreatePackageImport("NewRoot");
        package.CreateExport("NewAsset", "Object", newImportedRoot, indexed: false);
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

    private static void SaveFailurePreservesOriginal(bool collision)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"MFE-Integrity-{Guid.NewGuid():N}.pcc");
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-Donor-{Guid.NewGuid():N}.pcc");
        try
        {
            File.Copy(Path.GetFullPath("tests/Global Morphs/LE1 GlobalMorphs.pcc"), sourcePath);
            CreateDonor(donorPath, broken: !collision);
            if (collision)
            {
                using var package = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
                package.CreatePackageImport("MFE_Donor");
                package.Save();
            }
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
            Reject(() => new MorphFacePackageWriter().SaveExisting(draft), collision ? "import ancestor" : "relink");
            TestAssert.True(before.SequenceEqual(File.ReadAllBytes(sourcePath)), "A failed save changed the original PCC.");
            using var reopened = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
            TestAssert.True(reopened.FindExport(facePath, "BioMorphFace") is not null, "The original PCC could not be reopened.");
        }
        finally { File.Delete(sourcePath); File.Delete(donorPath); }
    }

    private static void CreateDonor(string path, bool broken)
    {
        using var donor = Empty("Donor");
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

    private static void TextureFallbacks()
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
