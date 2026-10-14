using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Models;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LecTexture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;

namespace MorphFaceEditor.Tests;

// Explicit integration suite: each case mutates only a temporary PCC copy.
public static class PackageContextTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE2 BioD workspaces retain post-load import resolution", Le2BioDWorkspaceRetainsPostLoadResolution),
        new("LE1 context clone creates an independently named face and material child", CloneLe1Morph),
        new("LE1 context delete trashes a face only in its package workspace", DeleteLe1Morph),
        new("morph deletion detects incoming references without modifying the PCC", MorphDeleteReferences),
        new("LE1 morph export creates and extends a dependency-only PCC without changing paths", ExportLe1MorphPackage),
        new("morph re-export replaces only its face and override and imports missing dependencies", ReexportMorphPair),
        new("external hair and attachment meshes port their material chains on morph export", ExternalAttachmentsPortOnMorphExport),
        new("morph package export preserves paths across LE1, LE2, and LE3", ExportMorphPackagesAcrossGames),
        new("cross-game conversion rebakes LE2 to LE3 and LE3 to LE1 packages", ConvertMorphsAcrossGameBoundary),
        new("metadata-only face loads preserve conversion evidence without decoded pixels", MetadataOnlyFaceLoadsPreserveConversionEvidence),
        new("cross-game conversion requires current texture databases", ConversionRequiresTextureDatabases),
        new("all six cross-game directions preserve canonical hair donors", PreserveCanonicalHairAcrossAllDirections),
        new("LE2 human attachment to LE1 preserves material graph", Le2HumanAttachmentToLe1),
        new("unsupported optional attachment material omits its mesh with a warning", UnsupportedAttachmentMaterialIsOmitted),
        new("attachment lookup rejects a same-named mesh at a different path", AttachmentLookupRejectsSameNameWrongPath),
        new("canonical stock hair and headwear MICs resolve their supported parent graphs", CanonicalHatMaterialsRemainSupported),
        new("reviewed canonical hat donors resolve exact native paths in all game directions", CanonicalHatDonorsResolveExactPaths),
        new("reviewed hair proxies use exact directional donor identities", ReviewedHairProxiesAreDirectional),
        new("reviewed hair material graphs and destination donors survive save and reopen", ReviewedHairGraphsSurviveConversion),
        new("reviewed excluded native attachments are omitted before export with warnings", ReviewedExcludedAttachmentsAreOmitted),
        new("material cache retains identities rather than decoded mip chains", MaterialCacheRetainsMetadataOnly),
        new("reviewed Officer and Beret attachments survive their native-game conversions", StockHatAttachmentsSurviveReview),
        new("reviewed male Beanie01 in Other converts LE2 to LE3 and reopens with native donor identity", MaleBeanie01OtherConvertsToLe3),
        new("reviewed female Cap in Hair converts LE3 to LE2 and reopens with native donor identity", FemaleCapHairConvertsToLe2),
        new("MotoGlasses conversion preserves both authored MIC slots and visor graph after reopen", MotoGlassesConversionPreservesAuthoredMaterials),
        new("custom hair MIC is supported through its canonical hair parent", CustomHairMicUsesCanonicalParent),
        new("TUF and custom attachment MICs share the canonical Turian parent rule", CustomAttachmentsUseCanonicalTurianParent),
        new("installed LE1 Asari template survives a shadowing mod", InstalledLe1AsariTemplateSurvivesMod),
        new("installed conversion templates materialise required Asari Krogan and Human dependencies", InstalledConversionDependencies),
        new("D1 corpus conversion matrix preserves attachment semantics", D1CorpusConversionMatrix),
        new("reviewed cross-game texture policy aliases donors and omits inert overrides", ApplyReviewedTextureTransferPolicy),
        new("reviewed cross-game texture paths are admitted to the local registry", ReviewedTexturePathsAreRegistryEligible),
        new("later-game alien textures embed package-stored when LE1 has no stock equivalent", EmbedMissingAlienTextureIntoLe1),
        new("LE2 context morph paste round-trips matching-profile topology", PasteLe2MorphData),
        new("LE3 context material paste round-trips every override kind", PasteLe3MaterialData),
        new("bundled human and Asari eyes recover both fixed reflection cubes", HumanAndAsariEyeCubesResolve),
        new("TSE RON export and import round-trip a real BioMorphFace", RonRoundTripsMorph),
        new("unresolved RON assets fail atomically without same-name substitution", RonUnresolvedAssetsFailAtomically),
        new("unresolved standalone RON attachments are rejected before PCC publication", StandaloneRonUnresolvedAttachmentFailsAtomically),
        new("standalone NPC RON omits unresolved hair with a visible warning", StandaloneNpcRonUnresolvedHairDegrades),
        new("standalone player RON sex detection uses per-game LOD0 topology", StandalonePlayerRonSexDetection),
        new("standalone player RON import creates a detached non-committable workspace", StandalonePlayerRonImport),
        new("standalone NPC RON import uses a detached native donor workspace", StandaloneNpcRonImport),
        new("cancelled standalone NPC construction preserves the prior workspace and cleans PCC candidates", CancelledStandaloneNpcConstructionCleansCandidates),
        new("standalone LE1 and LE2 Asari RONs import directly in both directions", StandaloneNpcRonLe1ToLe2),
        new("standalone NPC RON rejects cross-game LE3 routing before publication", StandaloneNpcRonRejectsLe3CrossGame),
        new("standalone player PSK import proves topology and preserves its fixed bake", StandalonePlayerPskImport),
        new("standalone player glTF export-import preserves its exact fixed bake", StandalonePlayerGltfImport),
        new("standalone RON mesh exports preserve the baked pose on PSK and glTF round trips", StandaloneRonMeshRoundTripPreservesPose),
        new("installed player RON matrix preserves all games sexes LODs materials and readback", StandalonePlayerRonInstalledMatrix),
        new("LE1 player RON imports LE2 Scr10 from its installed package", StandaloneTextureImportTests.Le1RonImportsLe2Scar),
        new("installed human texture donors resolve local and seekfree paths across games", StandaloneTextureImportTests.HumanTextureDonorMatrix),
        new("legacy Gibbed ME2 and ME3 head morphs import through the context pipeline", GibbedHeadMorphsImport),
        new("standalone legacy imports reject a destination-game mismatch", StandaloneLegacyImportRejectsMismatch),
        new("installed LE2 and LE3 legacy imports create detached workspaces", StandaloneLegacyImportsInstalledPlayers),
        new("broken attachment materials fall back without blocking face authoring", BrokenAttachmentMaterialFallsBack),
        new("Hat attachment materials remain preview-only on package reload", HatMaterialIsPreviewOnly),
        new("corpus edge faces load through profile and material projection", CorpusEdgeFacesLoad),
        new("UModel staging package contains only baked mesh geometry", MeshExportStagingIsGeometryOnly),
        new("real baked mesh projects back into its profile target span", RealBakedMeshInverts)
    ];

    private static void ReviewedTexturePathsAreRegistryEligible()
    {
        TestAssert.True(
            CrossGameAssetReconciliationCatalog.IsReviewedTexturePath(
                "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_White"),
            "A corpus-reviewed texture outside the generic PROMorph filters would be omitted from the registry.");
    }

    private static void Le2BioDWorkspaceRetainsPostLoadResolution()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"BioD_MFE_PostLoad_{Guid.NewGuid():N}.pcc");
        MEPackageHandler.CreateAndSavePackage(sourcePath, MEGame.LE2);
        try
        {
            using var workspace = new MorphFacePackageWorkspace(sourcePath);
            TestAssert.True(
                EntryImporter.IsSafeToImportFrom(
                    "Startup_METR_Patch01_INT.pcc",
                    MEGame.LE2,
                    workspace.WorkingPath),
                "The temporary workspace filename prevented imports from LE2's patch startup package.");
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    private static void BrokenAttachmentMaterialFallsBack()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var packagePath = Path.Combine(Path.GetTempPath(), $"MFE-AttachmentFallback-{Guid.NewGuid():N}.pcc");
        MEPackageHandler.CreateAndSavePackage(packagePath, MEGame.LE2);
        try
        {
            using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
            var face = package.CreateExport("Face", "BioMorphFace", indexed: false);
            var attachment = package.CreateExport(
                "HMM_HAT_Broken_MAT", "MaterialInstanceConstant", indexed: false);
            var missingRoot = package.CreatePackageImport("MissingAttachmentMaster");
            var missingGroup = package.CreateImport("Package", "Human", missingRoot);
            var missingParent = package.CreateImport("Material", "MissingParent", missingGroup);
            attachment.WriteProperty(new ObjectProperty(missingParent, "Parent"));

            using var cache = new PackageCache();
            var materialReader = new MorphFaceMaterialReader(
                cache, new GamePackageReferenceResolver(cache));
            var result = materialReader.Read(face, [], [attachment]);
            var fallback = result.Materials.Find(MorphFacePackageReader.ToIdentity(attachment)!);

            TestAssert.True(fallback is not null,
                "The broken attachment material did not produce a blank fallback.");
            TestAssert.Equal(HeadMaterialFamily.Accessory, fallback!.Family);
            TestAssert.Equal(0, fallback.Scalars.Count);
            TestAssert.Equal(0, fallback.Vectors.Count);
            TestAssert.Equal(0, fallback.Textures.Count);
            TestAssert.Equal(1, result.Warnings.Count);
            TestAssert.True(result.Warnings[0].Contains("MissingParent", StringComparison.Ordinal),
                "The attachment fallback did not retain the material failure for diagnostics.");
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    private static void HumanAndAsariEyeCubesResolve()
    {
        foreach (var fileName in new[] { "LE1 GlobalMorphs.pcc", "LE2 GlobalMorphs.pcc", "LE3 GlobalMorphs.pcc" })
        {
            var path = FixturePath(fileName);
            var faces = ReadFaces(path)
                .Where(face => face.ProfileKey.Contains("human", StringComparison.OrdinalIgnoreCase) ||
                               face.ProfileKey.Contains("asari", StringComparison.OrdinalIgnoreCase))
                .GroupBy(face => face.ProfileKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            using var reader = new MorphFacePackageReader();
            foreach (var face in faces)
            {
                var loaded = reader.Load(path, face.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var eyes = loaded.Materials.Materials.Values
                    .Where(material => material.Family == HeadMaterialFamily.Eyes)
                    .ToArray();
                TestAssert.True(eyes.Length > 0,
                    $"{fileName} {face.ProfileKey} exposed no human-eye material.");
                TestAssert.True(eyes.All(material => material.FixedCubeTexture is not null),
                    $"{fileName} {face.ProfileKey} lost its primary fixed eye-reflection cube.");
                TestAssert.True(eyes.All(material => material.SecondaryFixedCubeTexture is not null),
                    $"{fileName} {face.ProfileKey} lost its secondary fixed eye-reflection cube.");
                var textures = loaded.Materials.Materials.Values
                    .SelectMany(material => material.Textures.Values)
                    .Select(binding => binding.Texture)
                    .ToArray();
                TestAssert.True(textures.All(texture =>
                        texture.Width == texture.SourceWidth && texture.Height == texture.SourceHeight),
                    $"{fileName} {face.ProfileKey} did not decode an authored top mip.");
                TestAssert.True(textures.All(texture => texture.Mips is { Count: > 0 } &&
                                                     texture.Mips[0].Width == texture.Width &&
                                                     texture.Mips[0].Height == texture.Height),
                    $"{fileName} {face.ProfileKey} did not retain its authored 2D mip chain.");
                TestAssert.True(eyes.All(material => material.FixedCubeTexture!.Mips is { Count: > 0 } &&
                                                       material.SecondaryFixedCubeTexture!.Mips is { Count: > 0 }),
                    $"{fileName} {face.ProfileKey} did not retain its authored cube mip chains.");
            }
        }
    }

    private static void CloneLe1Morph()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            using var workspace = new MorphFacePackageWorkspace(path);
            var service = new MorphFacePackageContextService();
            var sourceFingerprint = PackageFingerprint.Capture(path);
            var expectedWorkspaceRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LE BioMorphFace Editor",
                "Workspaces");
            TestAssert.True(Path.GetFullPath(workspace.WorkingPath).StartsWith(
                    Path.GetFullPath(expectedWorkspaceRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "The package workspace was not created under Local AppData.");
            var source = ReadFaces(workspace.WorkingPath).First();
            var sourceMorph = service.CaptureMorphData(workspace.WorkingPath, source.InstancedPath);
            var sourceMaterial = service.CaptureMaterialData(workspace.WorkingPath, source.InstancedPath);

            var result = service.CloneMorph(workspace.WorkingPath, source.InstancedPath, "MFE_ContextClone_Test");
            var clonedMorph = service.CaptureMorphData(workspace.WorkingPath, result.FaceInstancedPath);
            var clonedMaterial = service.CaptureMaterialData(workspace.WorkingPath, result.FaceInstancedPath);

            AssertMorphEqual(sourceMorph, clonedMorph);
            AssertMaterialEqual(sourceMaterial, clonedMaterial);
            TestAssert.True(result.FaceInstancedPath.EndsWith(".MFE_ContextClone_Test", StringComparison.Ordinal),
                "The cloned BioMorphFace did not receive the requested object name.");
            TestAssert.True(result.MaterialOverrideInstancedPath.StartsWith(result.FaceInstancedPath + ".", StringComparison.OrdinalIgnoreCase),
                "The cloned BioMaterialOverride was not owned by the cloned face.");
            TestAssert.Equal(sourceFingerprint, PackageFingerprint.Capture(path));
            TestAssert.True(ReadFaces(path).All(face => face.ObjectName != "MFE_ContextClone_Test"),
                "The context clone changed the source PCC before an explicit workspace commit.");

            workspace.Commit();

            TestAssert.True(ReadFaces(path).Any(face => face.ObjectName == "MFE_ContextClone_Test"),
                "The committed workspace did not install the cloned face in the source PCC.");
        });
    }

    private static void MorphDeleteReferences()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            var source = ReadFaces(path).First();
            var clone = service.CloneMorph(path, source.InstancedPath, "MFE_DeleteReference_Test");
            var beforeCheck = PackageFingerprint.Capture(path);
            TestAssert.True(!service.HasMorphReferences(path, clone.FaceInstancedPath),
                "An unreferenced face or its own children triggered the warning.");
            TestAssert.Equal(beforeCheck, PackageFingerprint.Capture(path));
            using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
            {
                package.FindExport(source.InstancedPath)!.WriteProperty(new ObjectProperty(
                    package.FindExport(clone.FaceInstancedPath)!.UIndex, "MFE_TestMorphReference"));
                package.Save(path);
            }
            beforeCheck = PackageFingerprint.Capture(path);
            TestAssert.True(service.HasMorphReferences(path, clone.FaceInstancedPath),
                "An incoming object reference did not trigger the warning.");
            TestAssert.Equal(beforeCheck, PackageFingerprint.Capture(path));
            service.DeleteMorph(path, clone.FaceInstancedPath);
            TestAssert.True(ReadFaces(path).All(face => face.InstancedPath != clone.FaceInstancedPath),
                "Referenced deletion was prohibited rather than left to the user's confirmation.");
        });
    }

    private static void DeleteLe1Morph()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            using var workspace = new MorphFacePackageWorkspace(path);
            var service = new MorphFacePackageContextService();
            var sourceFingerprint = PackageFingerprint.Capture(path);
            var source = ReadFaces(workspace.WorkingPath).First();
            var originalCount = ReadFaces(workspace.WorkingPath).Count;

            service.DeleteMorph(workspace.WorkingPath, source.InstancedPath);

            TestAssert.Equal(originalCount - 1, ReadFaces(workspace.WorkingPath).Count);
            TestAssert.True(ReadFaces(workspace.WorkingPath).All(face =>
                    !string.Equals(face.InstancedPath, source.InstancedPath, StringComparison.OrdinalIgnoreCase)),
                "The deleted BioMorphFace remained in the working package catalogue.");
            TestAssert.Equal(sourceFingerprint, PackageFingerprint.Capture(path));
            TestAssert.True(ReadFaces(path).Any(face =>
                    string.Equals(face.InstancedPath, source.InstancedPath, StringComparison.OrdinalIgnoreCase)),
                "Deleting from the workspace changed the source PCC before commit.");

            workspace.Commit();

            TestAssert.True(ReadFaces(path).All(face =>
                    !string.Equals(face.InstancedPath, source.InstancedPath, StringComparison.OrdinalIgnoreCase)),
                "The committed workspace retained the deleted BioMorphFace.");
        });
    }

    private static void ExportLe1MorphPackage()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var destination = Path.Combine(Path.GetTempPath(), $"MFE-MorphExport-{Guid.NewGuid():N}.pcc");
            try
            {
                var faces = ReadFaces(path);
                TestAssert.True(faces.Count >= 2, "The package fixture did not contain two morphs to export.");
                var writer = new MorphFacePackageWriter();
                using var reader = new MorphFacePackageReader();
                var first = reader.Load(path, faces[0].UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

                var firstResult = writer.SaveMorphToPackage(first.Document, path, destination, createNewPackage: true);

                TestAssert.Equal(first.Document.Source.InstancedPath, firstResult.FaceInstancedPath);
                var exportedFaces = ReadFaces(destination);
                TestAssert.Equal(1, exportedFaces.Count);
                TestAssert.Equal(first.Document.Source.InstancedPath, exportedFaces[0].InstancedPath);
                using (var sourcePackage = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
                using (var exportedPackage = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true))
                {
                    TestAssert.True(exportedPackage.ExportCount < sourcePackage.ExportCount,
                        "The standalone morph export copied the entire source package.");
                }

                var second = reader.Load(path, faces[1].UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var secondResult = writer.SaveMorphToPackage(second.Document, path, destination, createNewPackage: false);

                TestAssert.Equal(second.Document.Source.InstancedPath, secondResult.FaceInstancedPath);
                exportedFaces = ReadFaces(destination);
                TestAssert.Equal(2, exportedFaces.Count);
                TestAssert.True(exportedFaces.Any(face => string.Equals(
                        face.InstancedPath, first.Document.Source.InstancedPath, StringComparison.OrdinalIgnoreCase)),
                    "The existing destination lost its first exported morph.");
                TestAssert.True(exportedFaces.Any(face => string.Equals(
                        face.InstancedPath, second.Document.Source.InstancedPath, StringComparison.OrdinalIgnoreCase)),
                    "The existing destination did not receive the second morph.");
            }
            finally
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
        });
    }

    private static void ReexportMorphPair()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", sourcePath =>
        {
            var destinationPath = Path.Combine(Path.GetTempPath(), $"MFE-MorphReexport-{Guid.NewGuid():N}.pcc");
            try
            {
                using var reader = new MorphFacePackageReader();
                var facePath = ReadFaces(sourcePath).First().InstancedPath;
                var writer = new MorphFacePackageWriter();
                writer.SaveMorphToPackage(reader.Load(sourcePath, facePath).Document,
                    sourcePath, destinationPath, createNewPackage: true);

                int faceIndex;
                int overrideIndex;
                string overridePath;
                string texturePath;
                Dictionary<int, (string Path, string Class, byte[] Data)> preserved;
                using (var destination = MEPackageHandler.OpenMEPackage(destinationPath, forceLoadFromDisk: true))
                {
                    var face = destination.FindExport(facePath, "BioMorphFace")!;
                    var materialOverride = (ExportEntry)face.GetProperty<ObjectProperty>("m_oMaterialOverrides")!
                        .ResolveToEntry(destination)!;
                    // The replacement must follow the destination pair's link,
                    // even when its BMO identity differs from the incoming BMO.
                    materialOverride.ObjectName = new NameReference("MFE_DestinationOverride");
                    faceIndex = face.UIndex;
                    overrideIndex = materialOverride.UIndex;
                    overridePath = materialOverride.InstancedFullPath;
                    var existingDependency = destination.CreateExport("MFE_ExistingDependency", "Object", indexed: false);
                    existingDependency.WriteProperty(new IntProperty(99, "Marker"));
                    var consumer = destination.CreateExport("MFE_ExistingConsumer", "Object", indexed: false);
                    consumer.WriteProperty(new ObjectProperty(face, "FaceReference"));
                    consumer.WriteProperty(new ObjectProperty(materialOverride, "OverrideReference"));
                    destination.Save();
                    preserved = destination.Exports.Where(export => export != face && export != materialOverride)
                        .ToDictionary(export => export.UIndex,
                            export => (export.InstancedFullPath, export.ClassName, export.Data));
                }

                using (var source = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
                {
                    var face = source.FindExport(facePath, "BioMorphFace")!;
                    var materialOverride = (ExportEntry)face.GetProperty<ObjectProperty>("m_oMaterialOverrides")!
                        .ResolveToEntry(source)!;
                    var existingDependency = source.CreateExport("MFE_ExistingDependency", "Object", indexed: false);
                    existingDependency.WriteProperty(new IntProperty(1, "Marker"));
                    var leaf = source.CreateExport("MFE_NewSharedLeaf", "Object", indexed: false);
                    leaf.WriteProperty(new ObjectProperty(existingDependency, "ExistingReference"));
                    leaf.WriteProperty(new ObjectProperty(face, "FaceReference"));
                    leaf.WriteProperty(new ObjectProperty(materialOverride, "OverrideReference"));
                    var faceDependency = source.CreateExport("MFE_NewFaceDependency", "Object", face, indexed: false);
                    var overrideDependency = source.CreateExport("MFE_NewOverrideDependency", "Object", materialOverride, indexed: false);
                    faceDependency.WriteProperty(new ObjectProperty(leaf, "LeafReference"));
                    overrideDependency.WriteProperty(new ObjectProperty(leaf, "LeafReference"));
                    face.WriteProperty(new ObjectProperty(faceDependency, "ReplacementDependency"));
                    materialOverride.WriteProperty(new ObjectProperty(overrideDependency, "ReplacementDependency"));
                    var donorTexture = source.Exports.First(export => export.ClassName == "Texture2D" &&
                        new LecTexture2D(export).Mips.Any(mip => mip.storageType == StorageTypes.pccUnc));
                    var newTexture = EntryCloner.CloneEntry(donorTexture);
                    newTexture.ObjectName = new NameReference("MFE_ReexportTexture");
                    texturePath = newTexture.InstancedFullPath;
                    var textures = materialOverride.GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides")
                        ?? new ArrayProperty<StructProperty>([], "m_aTextureOverrides");
                    textures.Add(new StructProperty("TextureParameter",
                    [
                        new NameProperty("HED_ReexportTexture", "nName"),
                        new ObjectProperty(newTexture, "m_pTexture")
                    ]));
                    materialOverride.WriteProperty(textures);
                    source.Save();
                }

                using var updatedReader = new MorphFacePackageReader();
                var document = updatedReader.Load(sourcePath, facePath).Document;
                var lods = document.BakedLods.Select(lod => lod.ToArray()).ToArray();
                lods[0][0] += new Vector3(0.125f, 0, 0);
                var draft = document with
                {
                    BakedLods = lods,
                    MaterialOverrides = document.MaterialOverrides with
                    {
                        Scalars = [new ScalarMaterialOverride("HED_TestReplacement", 0.75f)]
                    }
                };
                var result = writer.SaveMorphToPackage(draft, sourcePath, destinationPath, createNewPackage: false);

                using var reopened = MEPackageHandler.OpenMEPackage(destinationPath, forceLoadFromDisk: true);
                TestAssert.Equal(overridePath, result.MaterialOverrideInstancedPath);
                TestAssert.Equal(facePath, reopened.GetUExport(faceIndex).InstancedFullPath);
                TestAssert.Equal(overridePath, reopened.GetUExport(overrideIndex).InstancedFullPath);
                TestAssert.Equal(lods[0][0], reopened.GetUExport(faceIndex).GetBinaryData<BioMorphFace>().LODs[0][0]);
                TestAssert.Equal(0.75f, reopened.GetUExport(overrideIndex)
                    .GetProperty<ArrayProperty<StructProperty>>("m_aScalarOverrides")!
                    .Single().GetProp<FloatProperty>("sValue")!.Value);
                using var savedSource = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
                var savedTexture = reopened.FindExport(texturePath, "Texture2D")
                    ?? throw new InvalidDataException("The replacement BMO's new texture was not imported.");
                PccTextureDependencyResolver.VerifyBulkDataPreserved(
                    savedSource.FindExport(texturePath, "Texture2D")!, savedTexture);
                TestAssert.Equal(savedTexture.UIndex, reopened.GetUExport(overrideIndex)
                    .GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides")!
                    .Single(item => item.GetProp<NameProperty>("nName")!.Value.Instanced == "HED_ReexportTexture")
                    .GetProp<ObjectProperty>("m_pTexture")!.Value);
                foreach (var (index, original) in preserved)
                {
                    var actual = reopened.GetUExport(index);
                    TestAssert.Equal(original.Path, actual.InstancedFullPath);
                    TestAssert.Equal(original.Class, actual.ClassName);
                    TestAssert.True(original.Data.SequenceEqual(actual.Data),
                        $"Re-export changed the existing dependency '{original.Path}'.");
                }
                var savedLeaf = reopened.FindExport("MFE_NewSharedLeaf", "Object")!;
                TestAssert.Equal(faceIndex, savedLeaf.GetProperty<ObjectProperty>("FaceReference")!.Value);
                TestAssert.Equal(overrideIndex, savedLeaf.GetProperty<ObjectProperty>("OverrideReference")!.Value);
                var savedExisting = (ExportEntry)savedLeaf.GetProperty<ObjectProperty>("ExistingReference")!
                    .ResolveToEntry(reopened)!;
                TestAssert.Equal(99, savedExisting.GetProperty<IntProperty>("Marker")!.Value);
                foreach (var (rootIndex, dependencyPath) in new[]
                         { (faceIndex, $"{facePath}.MFE_NewFaceDependency"),
                           (overrideIndex, $"{overridePath}.MFE_NewOverrideDependency") })
                {
                    var dependency = reopened.FindExport(dependencyPath, "Object")!;
                    TestAssert.Equal(dependency.UIndex, reopened.GetUExport(rootIndex)
                        .GetProperty<ObjectProperty>("ReplacementDependency")!.Value);
                    TestAssert.Equal(savedLeaf.UIndex, dependency.GetProperty<ObjectProperty>("LeafReference")!.Value);
                }
            }
            finally { File.Delete(destinationPath); }
        });
    }

    private static void ExternalAttachmentsPortOnMorphExport()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const string meshName = "HMM_HIR_PROShort02_MDL";
        WithPackageCopy("LE3 GlobalMorphs.pcc", sourcePath =>
        {
            string sourceFacePath;
            AssetIdentity meshIdentity;
            using (var sourcePackage = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
            {
                var mesh = sourcePackage.Exports.Single(export =>
                    export.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                    export.ObjectName.Instanced.Equals(meshName, StringComparison.OrdinalIgnoreCase));
                meshIdentity = MorphFacePackageReader.ToIdentity(mesh)!;
                sourceFacePath = sourcePackage.Exports.First(export =>
                {
                    if (!export.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    var baseHead = export.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(sourcePackage);
                    var hair = export.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(sourcePackage);
                    var others = export.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes");
                    return baseHead?.ObjectName.Instanced.StartsWith("HMM_", StringComparison.OrdinalIgnoreCase) == true &&
                           !string.Equals(hair?.InstancedFullPath, mesh.InstancedFullPath, StringComparison.OrdinalIgnoreCase) &&
                           (others is null || others.All(reference => !string.Equals(
                               reference.ResolveToEntry(sourcePackage)?.InstancedFullPath,
                               mesh.InstancedFullPath,
                               StringComparison.OrdinalIgnoreCase)));
                }).InstancedFullPath;
            }

            var clone = new MorphFacePackageContextService().CloneMorph(
                sourcePath,
                sourceFacePath,
                "MFE_AttachmentPort_Test");
            using var reader = new MorphFacePackageReader();
            var loaded = reader.Load(sourcePath, clone.FaceInstancedPath);
            var draft = loaded.Document with
            {
                HairMeshReference = meshIdentity,
                OtherMeshReferences = [meshIdentity]
            };
            var destinationPath = Path.Combine(
                Path.GetTempPath(),
                $"MFE-AttachmentExport-{Guid.NewGuid():N}.pcc");
            try
            {
                var result = new MorphFacePackageWriter().SaveMorphToPackage(
                    draft,
                    sourcePath,
                    destinationPath,
                    createNewPackage: true);

                using var sourcePackage = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
                using var destinationPackage = MEPackageHandler.OpenMEPackage(destinationPath, forceLoadFromDisk: true);
                var sourceMesh = sourcePackage.FindExport(meshIdentity.InstancedPath, "SkeletalMesh")
                                 ?? throw new InvalidDataException("The source attachment mesh disappeared.");
                var destinationMesh = destinationPackage.FindExport(meshIdentity.InstancedPath, "SkeletalMesh")
                                      ?? throw new InvalidDataException("The attachment mesh was not ported as an export.");
                var savedFace = destinationPackage.FindExport(result.FaceInstancedPath, "BioMorphFace")
                                ?? throw new InvalidDataException("The exported morph was not saved.");
                TestAssert.Equal(
                    destinationMesh.UIndex,
                    savedFace.GetProperty<ObjectProperty>("m_oHairMesh")?.Value ?? 0);
                var savedOthers = savedFace.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?.ToArray() ?? [];
                TestAssert.Equal(1, savedOthers.Length);
                TestAssert.Equal(destinationMesh.UIndex, savedOthers[0].Value);

                var sourceMaterials = sourceMesh.GetBinaryData<SkeletalMesh>().Materials ?? [];
                var destinationMaterials = destinationMesh.GetBinaryData<SkeletalMesh>().Materials ?? [];
                TestAssert.Equal(sourceMaterials.Length, destinationMaterials.Length);
                var micCount = 0;
                for (var slot = 0; slot < sourceMaterials.Length; slot++)
                {
                    var sourceMaterial = sourcePackage.GetEntry(sourceMaterials[slot])
                                         ?? throw new InvalidDataException($"Source material slot {slot} is invalid.");
                    var destinationMaterial = destinationPackage.GetEntry(destinationMaterials[slot])
                                              ?? throw new InvalidDataException($"Destination material slot {slot} is invalid.");
                    TestAssert.Equal(sourceMaterial.InstancedFullPath, destinationMaterial.InstancedFullPath);
                    TestAssert.Equal(sourceMaterial.ClassName, destinationMaterial.ClassName);
                    if (sourceMaterial is not ExportEntry sourceMic ||
                        !sourceMic.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    micCount++;
                    var sourceParent = sourceMic.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(sourcePackage)
                                       ?? throw new InvalidDataException($"Source MIC '{sourceMic.InstancedFullPath}' has no parent.");
                    var destinationMic = destinationMaterial as ExportEntry
                                         ?? throw new InvalidDataException($"MIC '{sourceMic.InstancedFullPath}' was not ported as an export.");
                    var destinationParent = destinationMic.GetProperty<ObjectProperty>("Parent")
                        ?.ResolveToEntry(destinationPackage);
                    TestAssert.True(destinationParent is not null,
                        $"Ported MIC '{destinationMic.InstancedFullPath}' lost its parent.");
                    TestAssert.Equal(sourceParent.InstancedFullPath, destinationParent!.InstancedFullPath);
                    TestAssert.Equal(sourceParent.ClassName, destinationParent.ClassName);
                }
                TestAssert.True(micCount > 0, $"{meshName} exposed no MIC material slots to verify.");
            }
            finally
            {
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }
            }
        });
    }

    private static void ExportMorphPackagesAcrossGames()
    {
        foreach (var fileName in new[] { "LE1 GlobalMorphs.pcc", "LE2 GlobalMorphs.pcc", "LE3 GlobalMorphs.pcc" })
        {
            WithPackageCopy(fileName, path =>
            {
                var destination = Path.Combine(Path.GetTempPath(), $"MFE-CrossGameExport-{Guid.NewGuid():N}.pcc");
                try
                {
                    var face = ReadFaces(path).First();
                    using var reader = new MorphFacePackageReader();
                    var loaded = reader.Load(path, face.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var result = new MorphFacePackageWriter().SaveMorphToPackage(
                        loaded.Document,
                        path,
                        destination,
                        createNewPackage: true);

                    TestAssert.Equal(loaded.Document.Source.InstancedPath, result.FaceInstancedPath);
                    using var verificationReader = new MorphFacePackageReader();
                    TestAssert.Equal(loaded.Game, verificationReader
                        .Load(destination, result.FaceInstancedPath).Game);
                }
                finally
                {
                    if (File.Exists(destination))
                    {
                        File.Delete(destination);
                    }
                }
            });
        }
    }

    private static void PasteLe2MorphData()
    {
        WithPackageCopy("LE2 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            var (source, target) = FindCompatiblePair(path, service);
            var sourceData = service.CaptureMorphData(path, source.InstancedPath);

            service.PasteMorphData(path, target.InstancedPath, sourceData);

            AssertMorphEqual(sourceData, service.CaptureMorphData(path, target.InstancedPath));
        });
    }

    private static void ConvertMorphsAcrossGameBoundary()
    {
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var targets = new MorphTargetCatalog();
        var writer = new MorphFacePackageWriter();
        var context = new MorphFacePackageContextService();
        var service = new MorphFaceConversionService(
            profiles, targets, context, TestFixtures.GetCorpusTextureCatalogService());

        WithPackageCopy("LE2 GlobalMorphs.pcc", le2Source =>
        {
            var sourceFingerprint = PackageFingerprint.Capture(le2Source);
            var source = ReadFaces(le2Source).First(face => face.ProfileKey == "le2-human-male");
            var sourceMorph = context.CaptureMorphData(le2Source, source.InstancedPath);
            var sourceMaterial = context.CaptureMaterialData(le2Source, source.InstancedPath);
            var destination = Path.Combine(Path.GetTempPath(), $"MFE-LE2-to-LE3-{Guid.NewGuid():N}.pcc");
            try
            {
                var result = service.Convert(new MorphFaceConversionRequest(
                    le2Source,
                    source.InstancedPath,
                    MorphFaceGame.LE3,
                    destination,
                    CreateNewPackage: true,
                    TemplatePackagePath: null));
                TestAssert.Equal(MorphFaceGame.LE3, new MorphFacePackageReader()
                    .Load(destination, result.SaveResult.FaceInstancedPath).Game);
                TestAssert.Equal(sourceFingerprint, PackageFingerprint.Capture(le2Source));
                var convertedMorph = context.CaptureMorphData(destination, result.SaveResult.FaceInstancedPath);
                var convertedMaterial = context.CaptureMaterialData(destination, result.SaveResult.FaceInstancedPath);
                var sourceFeatures = sourceMorph.MorphFeatures.ToDictionary(
                    value => value.Name, value => value.Offset, StringComparer.OrdinalIgnoreCase);
                var transferred = convertedMorph.MorphFeatures.Count(value =>
                    value.Offset != 0 && sourceFeatures.TryGetValue(value.Name, out var expected) &&
                    Math.Abs(expected - value.Offset) <= 0.000001f);
                TestAssert.Equal(result.TransferredFeatureCount, transferred);
                TestAssert.True(transferred > 0, "The real conversion transferred no non-zero sliders.");
                TestAssert.Equal(result.ScalarCount, convertedMaterial.Scalars.Count);
                TestAssert.Equal(result.VectorCount, convertedMaterial.Vectors.Count);
                TestAssert.Equal(result.TextureCount, convertedMaterial.Textures.Count);
                TestAssert.True(result.ScalarCount <= sourceMaterial.Scalars.Count &&
                                result.VectorCount <= sourceMaterial.Vectors.Count &&
                                result.TextureCount <= sourceMaterial.Textures.Count,
                    "The converted material introduced template-authored overrides instead of destination defaults.");
                AssertConvertedFaceIsEditable(destination, result.SaveResult.FaceInstancedPath, profiles, targets);
            }
            finally
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
        });

        WithPackageCopy("LE3 GlobalMorphs.pcc", le3Source =>
        WithPackageCopy("LE1 GlobalMorphs.pcc", le1Destination =>
        {
            var source = ReadFaces(le3Source).First(face => face.ProfileKey == "le3-human-female");
            var originalDestinationCount = ReadFaces(le1Destination).Count;
            var result = service.Convert(new MorphFaceConversionRequest(
                le3Source,
                source.InstancedPath,
                MorphFaceGame.LE1,
                le1Destination,
                CreateNewPackage: false,
                TemplatePackagePath: null));
            TestAssert.Equal(originalDestinationCount + 1, ReadFaces(le1Destination).Count);
            AssertConvertedFaceIsEditable(le1Destination, result.SaveResult.FaceInstancedPath, profiles, targets);
        }));
    }

    private static void MetadataOnlyFaceLoadsPreserveConversionEvidence()
    {
        var path = FixturePath("LE3 GlobalMorphs.pcc");
        var faces = ReadFaces(path);
        foreach (var profileKey in new[] { "le3-human-male", "le3-asari" })
        {
            var face = faces.First(value => value.ProfileKey == profileKey);
            var selector = face.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Separate readers keep a warm decoded-material cache from masking a
            // metadata-only path that accidentally materialises pixels.
            using var fullReader = new MorphFacePackageReader();
            var full = fullReader.Load(path, selector);
            using var metadataReader = new MorphFacePackageReader();
            var metadata = metadataReader.Load(path, selector, decodeTextures: false);

            TestAssert.Equal(full.Document.Source, metadata.Document.Source);
            TestAssert.Equal(full.Document.BaseHeadReference, metadata.Document.BaseHeadReference);
            TestAssert.Equal(full.Document.HairMeshReference, metadata.Document.HairMeshReference);
            TestAssert.True(full.Document.OtherMeshReferences.SequenceEqual(metadata.Document.OtherMeshReferences),
                $"{profileKey} changed its authored attachment identities in metadata-only mode.");
            TestAssert.Equal(full.Document.MaterialOverrides.Source, metadata.Document.MaterialOverrides.Source);
            TestAssert.True(full.Document.MaterialOverrides.Scalars.SequenceEqual(metadata.Document.MaterialOverrides.Scalars),
                $"{profileKey} changed authored scalar overrides in metadata-only mode.");
            TestAssert.True(full.Document.MaterialOverrides.Vectors.SequenceEqual(metadata.Document.MaterialOverrides.Vectors),
                $"{profileKey} changed authored vector overrides in metadata-only mode.");
            TestAssert.True(full.Document.MaterialOverrides.Textures.SequenceEqual(metadata.Document.MaterialOverrides.Textures),
                $"{profileKey} changed authored texture identities in metadata-only mode.");

            TestAssert.Equal(full.BaseHead.Source, metadata.BaseHead.Source);
            TestAssert.Equal(full.HairMesh?.Source, metadata.HairMesh?.Source);
            TestAssert.True(full.OtherMeshes.Select(mesh => mesh.Source)
                    .SequenceEqual(metadata.OtherMeshes.Select(mesh => mesh.Source)),
                $"{profileKey} changed resolved attachment mesh identities in metadata-only mode.");
            TestAssert.True(full.Materials.Materials.Keys.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(metadata.Materials.Materials.Keys.Order(StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase),
                $"{profileKey} changed its resolved material set in metadata-only mode.");

            foreach (var (key, fullMaterial) in full.Materials.Materials)
            {
                var metadataMaterial = metadata.Materials.Materials[key];
                TestAssert.Equal(fullMaterial.Source, metadataMaterial.Source);
                TestAssert.Equal(fullMaterial.Family, metadataMaterial.Family);
                TestAssert.True(fullMaterial.Scalars.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .SequenceEqual(metadataMaterial.Scalars.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)),
                    $"{profileKey} changed scalar material values for {key}.");
                TestAssert.True(fullMaterial.Vectors.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .SequenceEqual(metadataMaterial.Vectors.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)),
                    $"{profileKey} changed vector material values for {key}.");
                TestAssert.True(fullMaterial.SupportedScalars.SetEquals(metadataMaterial.SupportedScalars) &&
                                fullMaterial.SupportedVectors.SetEquals(metadataMaterial.SupportedVectors) &&
                                fullMaterial.SupportedTextures.SetEquals(metadataMaterial.SupportedTextures),
                    $"{profileKey} changed supported material parameters for {key}.");
                TestAssert.Equal(0, metadataMaterial.Textures.Count);
                TestAssert.Equal(0, metadataMaterial.DefaultTextures.Count);
                TestAssert.True(metadataMaterial.FixedCubeTexture is null &&
                                metadataMaterial.SecondaryFixedCubeTexture is null,
                    $"{profileKey} retained decoded reflection cube pixels for {key}.");
            }

            var fullEyes = full.Materials.Materials.Values
                .Where(material => material.Family == HeadMaterialFamily.Eyes)
                .ToArray();
            TestAssert.True(fullEyes.Length > 0 && fullEyes.All(material =>
                    material.FixedCubeTexture is not null && material.SecondaryFixedCubeTexture is not null),
                $"{profileKey} did not exercise decoded fixed eye-reflection cubes.");
            TestAssert.True(full.Materials.Materials.Values.Any(material =>
                    material.Textures.Count > 0 || material.DefaultTextures.Count > 0),
                $"{profileKey} did not exercise decoded 2D texture pixels.");
        }
    }

    private static void ApplyReviewedTextureTransferPolicy()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var targetGame in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
        {
            TestAssert.Equal(
                "BIOG_HMM_HED_PROMorph.Normal.HMM_HED_PROBase_Face_Norm_Stack",
                CrossGameAssetReconciliationCatalog.NormalizeTargetPath(
                    targetGame,
                    "BIOG_HMM_HED_PROMorph_R.Normal.HMM_HED_PROBase_Face_Norm_Stack"));
        }
        WithPackageCopy("LE3 GlobalMorphs.pcc", le3Destination =>
        {
            using var package = MEPackageHandler.OpenMEPackage(le3Destination, forceLoadFromDisk: true);
            var source = MaterialWithTextures(
                ("HAIR_Diff", "BIOG_HMF_HIR_PRO.Afro.HMF_HIR_Afr_Diff"),
                ("HAIR_Mask", "BIOG_HMF_HIR_PRO.Afro.HMF_HIR_Afr_Mask"),
                ("HAIR_Tang", "BIOG_HMF_HIR_PRO.Afro.HMF_HIR_Afr_Tang"),
                ("HED_Scalp_Diff", "BIOG_HMF_HED_PROMorph_R.PROAshley.HMF_HED_PROAshley_Scalp_Stack"),
                ("HED_Scalp_SpecShift", "BIOG_HMF_HIR_PRO.Human.HMF_HIR_PROAll_SpecShift"));

            var result = TransferTextures(
                package,
                source,
                MEGame.LE1,
                "le1-human-female",
                FixturePath("LE1 GlobalMorphs.pcc"),
                FixturePath("LE3 GlobalMorphs.pcc"));

            TestAssert.Equal(0, result.MaterialData.Textures.Count);
        });

        WithPackageCopy("LE3 GlobalMorphs.pcc", le3Destination =>
        {
            using var package = MEPackageHandler.OpenMEPackage(le3Destination, forceLoadFromDisk: true);
            var result = TransferTextures(
                package,
                MaterialWithTextures(
                    ("HED_Lash_Diff", "BIOG_HMF_HED_PROMorph_R.Average.HMF_HED_PROLash_Opac_M01"),
                    ("HED_Scalp_Spec", "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_White")),
                MEGame.LE1,
                "le1-human-female",
                FixturePath("LE1 GlobalMorphs.pcc"),
                FixturePath("LE3 GlobalMorphs.pcc"));

            TestAssert.Equal(
                "BIOG_Humanoid_MASTER_MTR_R.Human.HMF_HED_PROLash_Opac_M01",
                result.MaterialData.Textures.Single(value => value.Name == "HED_Lash_Diff")
                    .TextureReference?.InstancedPath);

            result = TransferTextures(
                package,
                MaterialWithTextures(("HED_Scalp_Spec", "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_White")),
                MEGame.LE2,
                "le2-human-female",
                FixturePath("LE2 GlobalMorphs.pcc"),
                FixturePath("LE3 GlobalMorphs.pcc"));
            TestAssert.Equal(
                "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black",
                result.MaterialData.Textures.Single().TextureReference?.InstancedPath);
        });

        WithPackageCopy("LE1 GlobalMorphs.pcc", le1Destination =>
        {
            using var package = MEPackageHandler.OpenMEPackage(le1Destination, forceLoadFromDisk: true);
            var result = TransferTextures(
                package,
                MaterialWithTextures(("Hair_Colour", "BIOG_HMF_HIR_PRO.Human.GBL_HMF_HIR_White")),
                MEGame.LE2,
                "le2-human-female",
                FixturePath("LE2 GlobalMorphs.pcc"),
                FixturePath("LE1 GlobalMorphs.pcc"));

            TestAssert.Equal(1, result.MaterialData.Textures.Count);
            TestAssert.Equal(
                "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_White",
                result.MaterialData.Textures[0].TextureReference?.InstancedPath);
        });

        WithPackageCopy("LE2 GlobalMorphs.pcc", le2Destination =>
        {
            using var package = MEPackageHandler.OpenMEPackage(le2Destination, forceLoadFromDisk: true);
            var result = TransferTextures(
                package,
                MaterialWithTextures(
                    ("HED_Diff", "BIOG_HMM_HED_PROMorph_R.Joker.HMM_HED_PROJoker_FaceSD_Diff"),
                    ("HED_Scalp_Diff", "BIOG_HMM_HED_PROMorph_R.Joker.HMM_HED_PROJoker_ScalpSD_Diff"),
                    ("HED_Scalp_Norm", "BIOG_HMF_HED_PROMorph_R.Normal.HMF_HED_PROBase_Scalp_Bald_Norm")),
                MEGame.LE3,
                "le3-human-male",
                FixturePath("LE3 GlobalMorphs.pcc"),
                FixturePath("LE2 GlobalMorphs.pcc"));

            TestAssert.Equal(2, result.MaterialData.Textures.Count);
            TestAssert.Equal(
                "BIOG_HMM_HED_PROMorph.Joker.HMM_HED_PROJoker_Face_Diff_Stack",
                result.MaterialData.Textures.Single(value => value.Name == "HED_Diff").TextureReference?.InstancedPath);
            TestAssert.Equal(
                "BIOG_HMM_HED_PROMorph.Joker.HMM_HED_PROJoker_Scalp_Diff_Stack",
                result.MaterialData.Textures.Single(value => value.Name == "HED_Scalp_Diff").TextureReference?.InstancedPath);
        });
    }

    private static void HatMaterialIsPreviewOnly()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var packagePath = Path.Combine(Path.GetTempPath(), $"MFE-HatPreview-{Guid.NewGuid():N}.pcc");
        MEPackageHandler.CreateAndSavePackage(packagePath, MEGame.LE2);
        try
        {
            int faceIndex;
            int hatIndex;
            using (var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true))
            {
                var face = package.CreateExport("Face", "BioMorphFace", indexed: false);
                var hat = package.CreateExport("HMM_HAT_Test_MAT", "MaterialInstanceConstant", indexed: false);
                faceIndex = face.UIndex;
                hatIndex = hat.UIndex;
                package.Save();
            }

            using var reopened = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
            var reopenedFace = reopened.GetUExport(faceIndex);
            var reopenedHat = reopened.GetUExport(hatIndex);
            using var cache = new PackageCache();
            var reader = new MorphFaceMaterialReader(cache, new GamePackageReferenceResolver(cache));
            var material = reader.Read(reopenedFace, [], [reopenedHat]).Materials.Find(
                MorphFacePackageReader.ToIdentity(reopenedHat)!);
            TestAssert.True(material?.IsPreviewOnlyAttachment == true,
                "The Hat material was not marked as preview-only during package reload.");
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    private static void MaterialCacheRetainsMetadataOnly()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var packagePath = FixturePath("LE3 GlobalMorphs.pcc");
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        using var cache = new PackageCache();
        var reader = new MorphFaceMaterialReader(cache, new GamePackageReferenceResolver(cache));
        ResolvedHeadMaterial? decoded = null;
        foreach (var material in package.Exports.Where(entry => entry.ClassName == "MaterialInstanceConstant"))
        {
            try
            {
                var candidate = reader.ReadMaterialEvidence(material);
                if (candidate.Textures.Count > 0)
                {
                    decoded = candidate;
                    break;
                }
            }
            catch (InvalidDataException)
            {
                // Some corpus materials are deliberately unsupported; find a normal graph.
            }
        }

        TestAssert.True(decoded is not null, "No fixture material produced decoded texture defaults.");
        TestAssert.True(decoded!.Textures.Count > 0, "Material rehydration did not restore its texture bindings.");
        var cacheField = typeof(MorphFaceMaterialReader).GetField("_materialCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var metadataCache = cacheField?.GetValue(reader) as System.Collections.IDictionary;
        TestAssert.True(metadataCache is { Count: > 0 }, "No material metadata was retained.");
        var cacheKey = MaterialIdentityKey.Create(decoded.Source);
        var cached = metadataCache!.Keys.Cast<object>()
            .Where(key => string.Equals(key as string, cacheKey, StringComparison.OrdinalIgnoreCase))
            .Select(key => metadataCache[key]!)
            .Single();
        var materialProperty = cached.GetType().GetProperty("Material");
        var template = materialProperty?.GetValue(cached) as ResolvedHeadMaterial;
        TestAssert.True(template is not null, "The cached material metadata has no detached material template.");
        TestAssert.Equal(0, template!.Textures.Count);
        TestAssert.Equal(0, template.DefaultTextures.Count);
        TestAssert.True(template.FixedCubeTexture is null && template.SecondaryFixedCubeTexture is null,
            "Decoded reflection cube faces were retained in material metadata.");
        var textureMetadata = cached.GetType().GetProperty("Textures")?.GetValue(cached) as System.Collections.IDictionary;
        TestAssert.True(textureMetadata is { Count: > 0 }, "Source identities were not retained for later texture rehydration.");
    }

    private static void CorpusEdgeFacesLoad()
    {
        var cases = new[]
        {
            (File: "LE1 GlobalMorphs.pcc", Fragment: "hench_krogan", Game: MorphFaceGame.LE1),
            (File: "LE2 GlobalMorphs.pcc", Fragment: "hench_krogan", Game: MorphFaceGame.LE2),
            (File: "LE3 GlobalMorphs.pcc", Fragment: "global_joker", Game: MorphFaceGame.LE3)
        };
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var targets = new MorphTargetCatalog();
        foreach (var item in cases)
        {
            var path = FixturePath(item.File);
            var face = ReadFaces(path).Single(value => value.InstancedPath.Contains(
                item.Fragment, StringComparison.OrdinalIgnoreCase));
            using var reader = new MorphFacePackageReader();
            var loaded = reader.Load(path, face.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var profile = profiles.Find(item.Game, loaded.Document.Source.InstancedPath,
                loaded.BaseHead.Source.InstancedPath);
            TestAssert.True(profile is not null, $"No profile resolved for {loaded.Document.Source.InstancedPath}.");

            var resolution = new MorphFeatureTargetResolver().Resolve(
                loaded.Document.MorphFeatures,
                targets.Load(profile!, item.Game),
                profile!.MetadataOnlyFeatures,
                profile.DisplayName,
                profile.FeatureAliases);
            foreach (var feature in resolution.Features)
            {
                _ = profile.UiProfile.Describe(feature, sessionCanEdit: true);
            }

            var materialSession = new MaterialEditingSession(loaded.Document.MaterialOverrides, loaded.Materials);
            _ = materialSession.CreateOverrides();
        }
    }

    private static void ConversionRequiresTextureDatabases()
    {
        var service = new MorphFaceConversionService(
            MorphFaceProfileRegistry.CreateDefault(),
            new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            TestFixtures.CreateMissingTextureCatalogService());
        var destination = Path.Combine(Path.GetTempPath(), $"MFE-missing-registry-{Guid.NewGuid():N}.pcc");
        try
        {
            service.Convert(new MorphFaceConversionRequest(
                FixturePath("LE1 GlobalMorphs.pcc"),
                "HMF.BIOA_PRC2_HMF_Guard01",
                MorphFaceGame.LE2,
                destination,
                CreateNewPackage: true,
                FixturePath("LE1 to LE2 GlobalMorphs.pcc")));
            throw new Exception("Cross-game conversion proceeded without a texture database.");
        }
        catch (InvalidOperationException exception)
        {
            TestAssert.True(
                exception.Message.Contains("texture database is unavailable", StringComparison.OrdinalIgnoreCase),
                $"The missing-database failure was unclear: {exception.Message}");
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static void PreserveCanonicalHairAcrossAllDirections()
    {
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var service = new MorphFaceConversionService(
            profiles,
            new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            TestFixtures.GetCorpusTextureCatalogService());
        var cases = new[]
        {
            ("LE1", MorphFaceGame.LE2, "HMF.BIOA_PRC2_HMF_Guard01", "BIOG_HMF_HIR_PRO.Cute.HMF_HIR_Cte_MDL"),
            ("LE1", MorphFaceGame.LE3, "HMF.BIOA_PRC2_HMF_Guard01", "biog_hmf_hir_pro.Hair_Cute.HMF_HIR_Cte_MDL"),
            ("LE2", MorphFaceGame.LE1, "HMF.arv_kenson", "BIOG_HMF_HIR_PRO.Mom.HMF_HIR_Mom_MDL"),
            ("LE2", MorphFaceGame.LE3, "HMF.arv_kenson", "biog_hmf_hir_pro.Hair_Mom.HMF_HIR_Mom_MDL"),
            ("LE3", MorphFaceGame.LE1, "HMF.cat004_cerb_scientist1_face", "BIOG_HMF_HIR_PRO.Mom.HMF_HIR_Mom_MDL"),
            ("LE3", MorphFaceGame.LE2, "HMF.cat004_cerb_scientist1_face", "BIOG_HMF_HIR_PRO.Mom.HMF_HIR_Mom_MDL")
        };
        foreach (var (sourceGame, targetGame, facePath, expectedHair) in cases)
        {
            var sourcePath = FixturePath($"{sourceGame} GlobalMorphs.pcc");
            var targetPath = FixturePath($"{targetGame} GlobalMorphs.pcc");
            var destination = Path.Combine(
                Path.GetTempPath(), $"MFE-{sourceGame}-to-{targetGame}-hair-{Guid.NewGuid():N}.pcc");
            try
            {
                var result = service.Convert(new MorphFaceConversionRequest(
                    sourcePath,
                    facePath,
                    targetGame,
                    destination,
                    CreateNewPackage: true,
                    targetPath));
                using var convertedPackage = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
                var convertedFace = convertedPackage.FindExport(result.SaveResult.FaceInstancedPath, "BioMorphFace")
                                    ?? throw new InvalidDataException("The converted face was not saved.");
                var hair = convertedFace.GetProperty<ObjectProperty>("m_oHairMesh")?
                    .ResolveToEntry(convertedPackage);
                TestAssert.Equal(expectedHair, hair?.InstancedFullPath);
            }
            finally
            {
                if (File.Exists(destination)) File.Delete(destination);
            }
        }
    }

    private static void Le2HumanAttachmentToLe1()
    {
        var service = new MorphFaceConversionService(
            MorphFaceProfileRegistry.CreateDefault(),
            new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            TestFixtures.GetCorpusTextureCatalogService());
        var destination = Path.Combine(Path.GetTempPath(), $"MFE-D1-LE2-human-{Guid.NewGuid():N}.pcc");
        try
        {
            var result = service.Convert(new MorphFaceConversionRequest(
                FixturePath("LE2 GlobalMorphs.pcc"),
                "HMM.BioFace_Security2",
                MorphFaceGame.LE1,
                destination,
                CreateNewPackage: true,
                FixturePath("LE1 GlobalMorphs.pcc")));
            using var reopened = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
            TestAssert.True(reopened.FindExport(result.SaveResult.FaceInstancedPath, "BioMorphFace") is not null,
                "Converted LE1 human face was not saved.");
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static void UnsupportedAttachmentMaterialIsOmitted()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-UnsupportedAttachment-{Guid.NewGuid():N}.pcc");
        var donorPackageName = Path.GetFileNameWithoutExtension(donorPath);
        var meshPath = $"{donorPackageName}.UnsupportedAttachment";
        try
        {
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage(donorPath, MEGame.LE3))
            {
                var material = donor.CreateExport("MFE_CustomAttachmentMaterial", "Material", indexed: false);
                var mesh = donor.CreateExport("UnsupportedAttachment", "SkeletalMesh", indexed: false);
                var binary = SkeletalMesh.Create();
                binary.Materials = [material.UIndex];
                mesh.WriteBinary(binary);
                donor.Save();
            }

            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("MFE-AttachmentTarget.pcc", MEGame.LE3);
            var result = MorphFaceAttachmentTransferEngine.Transfer(
                destination,
                null,
                [new AssetIdentity(donorPath, meshPath, 0, "SkeletalMesh")],
                MEGame.LE3,
                donorPath);

            TestAssert.True(result.OtherMeshes.Count == 0,
                "An attachment with a material outside the canonical material oracle was retained.");
            TestAssert.True(result.Warnings.Any(value =>
                    value.Contains("unsupported material", StringComparison.OrdinalIgnoreCase) &&
                    value.Contains("MFE_CustomAttachmentMaterial", StringComparison.OrdinalIgnoreCase)),
                "Omitting the unsupported attachment did not explain the unsupported material.");
            TestAssert.True(destination.FindExport(meshPath, "SkeletalMesh") is null,
                "The unsupported optional attachment was materialised before being rejected.");
        }
        finally
        {
            if (File.Exists(donorPath)) File.Delete(donorPath);
        }
    }

    private static void ReviewedHairProxiesAreDirectional()
    {
        TestAssert.True(CanonicalHairDonorPolicy.TryResolve(MEGame.LE2, MEGame.LE3,
            "BIOG_HMM_HIR_PRO_R.FauxHawk.HMM_HIR_Fhwk_01_MDL", out var proxy), "Reviewed proxy was not resolved.");
        TestAssert.Equal("BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_01_MDL", proxy);
        TestAssert.True(!CanonicalHairDonorPolicy.TryResolve(MEGame.LE2, MEGame.LE3,
            "WrongPackage.FauxHawk.HMM_HIR_Fhwk_01_MDL", out _), "A same-leaf lookalike was mapped.");
        TestAssert.True(CanonicalHairDonorPolicy.TryResolve(MEGame.LE3, MEGame.LE2,
            "BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_PROMiranda_MDL", out var miranda), "LE2 Miranda was not resolved.");
        TestAssert.Equal("BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_MIR_MDL", miranda);
        TestAssert.True(CanonicalHairDonorPolicy.TryResolve(MEGame.LE3, MEGame.LE1,
            "BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_PROMiranda_MDL", out var absent) && absent is null,
            "LE2 Miranda mapping was incorrectly reused for LE1.");
        TestAssert.True(CanonicalHairDonorPolicy.TryResolve(MEGame.LE3, MEGame.LE2,
            "BIOG_HMM_HIR_PRO_R.Hair_James.HMM_HIR_PROJames_MDL", out var james) && james is null,
            "LE3 James should be an explicit destination omission.");
    }

    private static void ReviewedHairGraphsSurviveConversion()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cases = new (MorphFaceGame Source, MorphFaceGame Target, string Face, string SourceMesh, string? TargetMesh)[]
        {
            (MorphFaceGame.LE2, MorphFaceGame.LE3, "HMM.BioFace_Archer",
                "BIOG_HMM_HIR_PRO_R.PROCustomBraids01.HMM_HIR_PROCustomBraids01_MDL",
                "BIOG_HMM_HIR_PRO_R.PROCustomBraids01.HMM_HIR_PROCustomBraids01_MDL"),
            (MorphFaceGame.LE3, MorphFaceGame.LE2, "HMF.Asari",
                "BIOG_HMF_HIR_PRO.Hair_PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL",
                "BIOG_HMF_HIR_PRO.PROCustomShortAfro.HMF_HIR_PROCustomShortAfro_MDL"),
            (MorphFaceGame.LE2, MorphFaceGame.LE3, "HMM.BioFace_Archer",
                "BIOG_HMM_HIR_PRO_R.Kaiden.HMM_HIR_Kai_MDL",
                "BIOG_HMM_HIR_PRO_R.Hair_Kaiden.HMM_HIR_Kai_MDL"),
            (MorphFaceGame.LE3, MorphFaceGame.LE2, "HMM.Archer_Face",
                "BIOG_HMM_HIR_PRO_R.Hair_Kaiden.HMM_HIR_Kai_MDL",
                "BIOG_HMM_HIR_PRO_R.Kaiden.HMM_HIR_Kai_MDL"),
            (MorphFaceGame.LE2, MorphFaceGame.LE3, "HMM.BioFace_Archer",
                "BIOG_HMM_HIR_PRO_R.FauxHawk.HMM_HIR_Fhwk_01_MDL",
                "BIOG_HMM_HIR_PRO_R.Hair_SargeSpike.HMM_HIR_Ssk_01_MDL"),
            (MorphFaceGame.LE3, MorphFaceGame.LE2, "HMM.Archer_Face",
                "BIOG_HMM_HIR_PRO_R.Hair_James.HMM_HIR_PROJames_MDL", null)
        };
        foreach (var item in cases)
        {
            var sourcePackage = FixturePath($"{item.Source} GlobalMorphs.pcc");
            var stagedPath = Path.Combine(Path.GetTempPath(), $"MFE-ReviewedHair-Source-{Guid.NewGuid():N}.pcc");
            var targetPath = Path.Combine(Path.GetTempPath(), $"MFE-ReviewedHair-Target-{Guid.NewGuid():N}.pcc");
            try
            {
                using var reader = new MorphFacePackageReader();
                var loaded = reader.Load(sourcePackage, item.Face, decodeTextures: false);
                using var cache = new PackageCache { CacheMaxSize = 8 };
                var donor = new InstalledGameAssetResolver().Resolve(ToMeGame(item.Source), item.SourceMesh, "SkeletalMesh", cache)!;
                var draft = loaded.Document with
                {
                    HairMeshReference = new AssetIdentity(donor.FileRef.FilePath, item.SourceMesh, donor.UIndex, "SkeletalMesh"),
                    OtherMeshReferences = []
                };
                var staged = new MorphFacePackageWriter().SaveMorphToPackage(draft, sourcePackage, stagedPath, createNewPackage: true);
                TestAssert.True(!staged.Warnings.Any(value => value.Contains("omitted", StringComparison.OrdinalIgnoreCase)),
                    "Reviewed source hair was omitted: " + string.Join(" | ", staged.Warnings));
                AssertReopenedHatIdentity(stagedPath, draft.Source.InstancedPath, "m_oHairMesh", item.SourceMesh, ToMeGame(item.Source));
                var service = new MorphFaceConversionService(MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog(),
                    new MorphFacePackageContextService(), TestFixtures.GetCorpusTextureCatalogService());
                var result = service.Convert(new MorphFaceConversionRequest(stagedPath, draft.Source.InstancedPath,
                    item.Target, targetPath, CreateNewPackage: true, FixturePath($"{item.Target} GlobalMorphs.pcc")));
                if (item.TargetMesh is not null)
                    AssertReopenedHatIdentity(targetPath, result.SaveResult.FaceInstancedPath, "m_oHairMesh", item.TargetMesh, ToMeGame(item.Target));
                else
                {
                    using var reopened = MEPackageHandler.OpenMEPackage(targetPath, forceLoadFromDisk: true);
                    var face = reopened.FindExport(result.SaveResult.FaceInstancedPath, "BioMorphFace")!;
                    TestAssert.True(face.GetProperty<ObjectProperty>("m_oHairMesh")?.Value is null or 0, "James destination did not skip.");
                    TestAssert.True(result.SaveResult.Warnings.Any(value => value.Contains(item.SourceMesh, StringComparison.OrdinalIgnoreCase) &&
                        value.Contains("unset", StringComparison.OrdinalIgnoreCase)), "James omission lacked its reviewed warning.");
                    var warning = result.SaveResult.UserWarnings.Single(value =>
                        value.LocalizationKey == "loc_Services_AttachmentTransfer_ReviewedDonorMissing");
                    TestAssert.True(warning.Arguments.Contains(item.SourceMesh) &&
                                    warning.DiagnosticText is not null &&
                                    result.SaveResult.Warnings.Contains(warning.DiagnosticText),
                        "Reviewed donor omission lost its source identity or raw diagnostic.");
                    PackageIntegrity.Verify(reopened);
                }
            }
            finally
            {
                if (File.Exists(stagedPath)) File.Delete(stagedPath);
                if (File.Exists(targetPath)) File.Delete(targetPath);
            }
        }
    }

    private static MEGame ToMeGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => MEGame.LE1,
        MorphFaceGame.LE2 => MEGame.LE2,
        MorphFaceGame.LE3 => MEGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game))
    };

    private static void ReviewedExcludedAttachmentsAreOmitted()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var sourcePackage = FixturePath("LE2 GlobalMorphs.pcc");
        using var reader = new MorphFacePackageReader();
        var loaded = reader.Load(sourcePackage, "HMF.arv_kenson", decodeTextures: false);
        var paths = new[]
        {
            "BIOG_HMF_HIR_PRO.Mira.HMF_HIR_MIR_LOD0",
            "BIOG_HMF_HIR_PRO.PROCustomBraids01.HMM_HIR_PROCustomBraids01_MDL_LOD0",
            "BIOG_HMF_HIR_PRO.Hair_PROCustomBraids01.HMM_HIR_PROCustomBraids01_MDL_LOD0",
            "BIOG_HMF_HIR_PRO.Hair_VI.HMF_HIR_VI_MDL"
        };
        foreach (var path in paths)
        {
            TestAssert.True(!AttachmentMeshNamePolicy.IsInstalledHeadAttachment(path), "Excluded native mesh remained selectable.");
            var target = Path.Combine(Path.GetTempPath(), $"MFE-ExcludedAttachment-{Guid.NewGuid():N}.pcc");
            try
            {
                var draft = loaded.Document with
                {
                    HairMeshReference = new AssetIdentity(sourcePackage, path, 0, "SkeletalMesh"), OtherMeshReferences = []
                };
                var result = new MorphFacePackageWriter().SaveMorphToPackage(draft, sourcePackage, target, createNewPackage: true);
                TestAssert.True(result.Warnings.Any(value => value.Contains(path, StringComparison.OrdinalIgnoreCase) &&
                    value.Contains("reviewed excluded", StringComparison.OrdinalIgnoreCase)), "Excluded source omission lacked its reason.");
                using var reopened = MEPackageHandler.OpenMEPackage(target, forceLoadFromDisk: true);
                var face = reopened.FindExport(draft.Source.InstancedPath, "BioMorphFace")!;
                TestAssert.True(face.GetProperty<ObjectProperty>("m_oHairMesh")?.Value is null or 0, "Excluded source mesh was retained.");
                TestAssert.True(reopened.FindExport(path, "SkeletalMesh") is null, "Excluded source mesh was materialised.");
                PackageIntegrity.Verify(reopened);
            }
            finally { if (File.Exists(target)) File.Delete(target); }
        }
        TestAssert.True(AttachmentMeshNamePolicy.IsInstalledHeadAttachment("BIOG_HMF_HIR_PRO.Hair_PROMiranda.HMF_HIR_MIR_MDL"),
            "Valid Miranda counterpart was excluded with the malformed mesh.");
    }

    private static void CanonicalHatMaterialsRemainSupported()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var cache = new PackageCache { CacheMaxSize = 8 };
        var installedAssets = new InstalledGameAssetResolver();
        var referenceResolver = new GamePackageReferenceResolver(cache, installedAssets);
        var examples = new (MEGame Game, string ClassName, string Path)[]
        {
            (MEGame.LE1, "MaterialInstanceConstant", "BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_MAT_1a"),
            (MEGame.LE2, "MaterialInstanceConstant", "BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_Mat_2b"),
            (MEGame.LE2, "MaterialInstanceConstant", "BIOG_HMM_HIR_PRO_R.Moto.HMM_HAT_Mtr_MAT_1a"),
            (MEGame.LE1, "MaterialInstanceConstant", "BIOG_HMM_HIR_PRO_R.MotoGlasses.HMM_HAT_Mtg_MAT_1b"),
            (MEGame.LE2, "MaterialInstanceConstant", "BIOG_HMM_HIR_PRO_R.MotoGlasses.HMM_HAT_Mtg_MAT_1b"),
            (MEGame.LE3, "MaterialInstanceConstant", "BIOG_HMM_HIR_PRO_R.Hat_Beret.HMM_HAT_Brt_MAT_1a"),
            (MEGame.LE3, "MaterialInstanceConstant", "BIOG_HMM_HIR_PRO_R.Hat_Officer.HMM_HAT_Off_MAT_1a"),
            (MEGame.LE2, "MaterialInstanceConstant", "BIOG_HMM_HIR_PRO_R.Beret.HMM_HAT_Brt_MAT_1a"),
            (MEGame.LE3, "Material", "BIOG_HMM_HIR_PRO_R.Hat_Moto.HMM_HAT_Mtr_MASTER"),
            (MEGame.LE1, "MaterialInstanceConstant", "BIOG_HMF_HIR_PRO.Hair_PROShepard.HMF_HIR_PROShepard_MAT_1a"),
            (MEGame.LE2, "MaterialInstanceConstant", "BIOG_HMF_HIR_PRO.Hair_PROShepard.HMF_HIR_PROShepard_MAT_1a"),
            (MEGame.LE3, "MaterialInstanceConstant", "BIOG_HMF_HIR_PRO.Hair_PROShepard.HMF_HIR_PROShepard_MAT_1a"),
            (MEGame.LE1, "MaterialInstanceConstant", "BIOG_HMF_HIR_PRO.PROCustomShort02.HMF_HIR_PROCustomShort02_MAT_1a"),
            (MEGame.LE2, "MaterialInstanceConstant", "BIOG_HMF_HIR_PRO.PROCustomShort02.HMF_HIR_PROCustomShort02_MAT_1a"),
            (MEGame.LE3, "MaterialInstanceConstant", "BIOG_HMF_HIR_FTL_R.Materials.LongHair.HMF_HIR_FTL_LongHair_Mat1a"),
            (MEGame.LE3, "MaterialInstanceConstant", "biog_hmf_hir_pro.Hair_PROJack.HMF_HIR_PROJack_Mat_1a")
        };
        foreach (var example in examples)
        {
            var material = installedAssets.Resolve(example.Game, example.Path, example.ClassName, cache)
                           ?? throw new InvalidDataException($"Official material '{example.Path}' is missing.");
            TestAssert.True(
                SupportedMaterialGraphPolicy.IsSupported(
                    example.Game, material, referenceResolver, out var failure),
                $"Canonical {example.Game} stock attachment material '{example.Path}' was rejected: {failure}");
        }
    }

    private static void CanonicalHatDonorsResolveExactPaths()
    {
        // Independent reviewed identities: LE1/LE2 share the first path; LE3 uses
        // the second path when it has a native equivalent. Null means absent.
        var reviewed = new (string? Le12, string? Le3)[]
        {
            ("BIOG_HMF_HIR_PRO.Beanie.HMF_HAT_Bn1_MDL", "BIOG_HMF_HIR_PRO.Hat_Beanie.HMF_HAT_Bn1_MDL"),
            ("BIOG_HMF_HIR_PRO.Beanie02.HMF_HAT_Bn2_MDL", "BIOG_HMF_HIR_PRO.Hat_Beanie02.HMF_HAT_Bn2_MDL"),
            ("BIOG_HMF_HIR_PRO.Beret.HMF_HAT_Brt_MDL", "BIOG_HMF_HIR_PRO.Hat_Beret.HMF_HAT_Brt_MDL"),
            ("BIOG_HMF_HIR_PRO.Cap.HMF_HAT_Cap_MDL", "BIOG_HMF_HIR_PRO.Hat_Cap.HMF_HAT_Cap_MDL"),
            ("BIOG_HMF_HIR_PRO.Cosmo.HMF_HAT_Cos_MDL", null),
            ("BIOG_HMF_HIR_PRO.Cuban.HMF_HAT_Cub_MDL", "BIOG_HMF_HIR_PRO.Hat_Cuban.HMF_HAT_Cub_MDL"),
            ("BIOG_HMF_HIR_PRO.Cyclops.HMF_HAT_CYC_MDL", null),
            ("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er1_MDL", null),
            ("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er2_MDL", null),
            ("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er3_MDL", null),
            ("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er4_MDL", null),
            ("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er5_MDL", null),
            ("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er6_MDL", null),
            ("BIOG_HMF_HIR_PRO.Earrings.HMF_HAT_Er7_MDL", null),
            ("BIOG_HMF_HIR_PRO.GlassesFull.HMF_HAT_GLA_MDL", null),
            ("BIOG_HMF_HIR_PRO.GlassesHalf.HMF_HAT_GLH_MDL", null),
            ("BIOG_HMF_HIR_PRO.HairClip.HMF_HAT_Hcp_MDL", "BIOG_HMF_HIR_PRO.Hat_HairClip.HMF_HAT_Hcp_MDL"),
            ("BIOG_HMF_HIR_PRO.Moto.HMF_HAT_MTR_MDL", "BIOG_HMF_HIR_PRO.Hat_Moto.HMF_HAT_MTR_MDL"),
            ("BIOG_HMF_HIR_PRO.MotoGlasses.HMF_HAT_MTG_MDL", null),
            ("BIOG_HMF_HIR_PRO.Officer.HMF_HAT_Off_MDL", "BIOG_HMF_HIR_PRO.Hat_Officer.HMF_HAT_Off_MDL"),
            ("BIOG_HMM_HIR_PRO_R.Beanie01.HMM_HAT_Bn1_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Beanie01.HMM_HAT_Bn1_MDL"),
            ("BIOG_HMM_HIR_PRO_R.Beanie02.HMM_HAT_Bn2_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Beanie02.HMM_HAT_Bn2_MDL"),
            ("BIOG_HMM_HIR_PRO_R.Beret.HMM_HAT_Brt_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Beret.HMM_HAT_Brt_MDL"),
            ("BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Cap.HMM_HAT_Cap_MDL"),
            ("BIOG_HMM_HIR_PRO_R.Cosmo.HMM_HAT_Cos_MDL", null),
            ("BIOG_HMM_HIR_PRO_R.Cuban.HMM_HAT_Cub_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Cuban.HMM_HAT_Cub_MDL"),
            ("BIOG_HMM_HIR_PRO_R.Cyclops.HMM_HAT_CYC_MDL", null),
            ("BIOG_HMM_HIR_PRO_R.GlassesFull.HMM_HAT_Glf_MDL", null),
            ("BIOG_HMM_HIR_PRO_R.GlassesHalf.HMM_HAT_GLH_MDL", null),
            ("BIOG_HMM_HIR_PRO_R.Moto.HMM_HAT_MTR_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Moto.HMM_HAT_MTR_MDL"),
            ("BIOG_HMM_HIR_PRO_R.MotoGlasses.HMM_HAT_MTG_MDL", null),
            ("BIOG_HMM_HIR_PRO_R.Officer.HMM_HAT_Off_MDL", "BIOG_HMM_HIR_PRO_R.Hat_Officer.HMM_HAT_Off_MDL"),
            ("BIOG_HMM_HIR_PRO_R.Visor.HMM_HAT_VSR_MDL", null),
            (null, "BIOG_HMM_HIR_PRO_R.Hat_Hackett.HMM_HAT_Hac_MDL")
        };

        var games = new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 };
        foreach (var (le12Path, le3Path) in reviewed)
        foreach (var sourceGame in games)
        foreach (var targetGame in games.Where(game => game != sourceGame))
        {
            var sourcePath = sourceGame == MEGame.LE3 ? le3Path : le12Path;
            if (sourcePath is null)
            {
                continue;
            }
            var expected = targetGame == MEGame.LE3 ? le3Path : le12Path;
            TestAssert.True(CanonicalHatDonorPolicy.TryResolve(
                    sourceGame, targetGame, sourcePath, out var actual),
                $"Reviewed hat donor was not recognized: {sourceGame}->{targetGame} {sourcePath}.");
            TestAssert.Equal(expected, actual);
        }

        TestAssert.True(!CanonicalHatDonorPolicy.TryResolve(
                MEGame.LE2, MEGame.LE3,
                "BIOG_HMF_HIR_PRO.Custom.HMF_HAT_Bn1_MDL", out var lookalikeTarget),
            "A lookalike mesh outside the reviewed full path was treated as a canonical hat.");
        TestAssert.Equal<string?>(null, lookalikeTarget);
    }

    private static void AttachmentLookupRejectsSameNameWrongPath()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var donorPath = Path.Combine(Path.GetTempPath(), $"MFE-WrongPathAttachment-{Guid.NewGuid():N}.pcc");
        var donorPackageName = Path.GetFileNameWithoutExtension(donorPath);
        var donorMeshPath = $"{donorPackageName}.Container.SameName_MDL";
        const string requestedPath = "BIOG_HMF_HIR_PRO.Beret.SameName_MDL";
        try
        {
            using (var donor = MEPackageHandler.CreateMemoryEmptyPackage(donorPath, MEGame.LE3))
            {
                var container = donor.CreatePackageExport("Container");
                var mesh = donor.CreateExport("SameName_MDL", "SkeletalMesh", container, indexed: false);
                mesh.WriteBinary(SkeletalMesh.Create());
                donor.Save();
            }

            using var destination = MEPackageHandler.CreateMemoryEmptyPackage("MFE-WrongPathTarget.pcc", MEGame.LE3);
            var result = MorphFaceAttachmentTransferEngine.Transfer(
                destination,
                new AssetIdentity(donorPath, requestedPath, 0, "SkeletalMesh"),
                [],
                MEGame.LE3,
                donorPath);

            TestAssert.Equal(0, result.OtherMeshes.Count);
            TestAssert.True(result.Hair is null,
                "An attachment with only a same-named, different-path template export was guessed.");
            TestAssert.True(result.Warnings.Any(value =>
                    value.Contains(requestedPath, StringComparison.OrdinalIgnoreCase) &&
                    value.Contains("no exact", StringComparison.OrdinalIgnoreCase)),
                "Rejecting the wrong-path candidate did not identify the missing exact donor path.");
            TestAssert.True(destination.FindExport(requestedPath, "SkeletalMesh") is null,
                "The same-named, different-path mesh was materialised under the requested path.");
        }
        finally
        {
            if (File.Exists(donorPath)) File.Delete(donorPath);
        }
    }

    private static void MotoGlassesConversionPreservesAuthoredMaterials()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const string meshPath = "BIOG_HMF_HIR_PRO.MotoGlasses.HMF_HAT_MTG_MDL";
        var destination = Path.Combine(Path.GetTempPath(), $"MFE-MotoGlasses-{Guid.NewGuid():N}.pcc");
        try
        {
            var service = new MorphFaceConversionService(
                MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog(),
                new MorphFacePackageContextService(), TestFixtures.GetCorpusTextureCatalogService());
            var result = service.Convert(new MorphFaceConversionRequest(
                FixturePath("LE2 GlobalMorphs.pcc"), "HMF.BioFace_RandomHumanFemale2",
                MorphFaceGame.LE1, destination, true, FixturePath("LE1 GlobalMorphs.pcc")));
            TestAssert.True(!result.SaveResult.Warnings.Any(value => value.Contains("unsupported material",
                    StringComparison.OrdinalIgnoreCase)),
                "MotoGlasses was rejected: " + string.Join(" | ", result.SaveResult.Warnings));

            using var reopened = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
            var face = reopened.FindExport(result.SaveResult.FaceInstancedPath, "BioMorphFace")!;
            var attachment = face.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
                .Select(value => value.ResolveToEntry(reopened)).OfType<ExportEntry>()
                .SingleOrDefault(value => value.InstancedFullPath.Equals(meshPath, StringComparison.OrdinalIgnoreCase));
            TestAssert.True(attachment is not null, "The authored MotoGlasses attachment was missing after reopen.");
            using var cache = new PackageCache { CacheMaxSize = 8 };
            var officialAssets = new InstalledGameAssetResolver();
            var donor = officialAssets.Resolve(MEGame.LE1, meshPath, "SkeletalMesh", cache)!;
            var donorSlots = donor.GetBinaryData<SkeletalMesh>().Materials;
            var savedSlots = attachment!.GetBinaryData<SkeletalMesh>().Materials;
            TestAssert.Equal(donorSlots.Length, savedSlots.Length);
            TestAssert.Equal(2, savedSlots.Length);
            var resolver = new GamePackageReferenceResolver(cache, officialAssets);
            for (var slot = 0; slot < donorSlots.Length; slot++)
            {
                var authored = resolver.Resolve(donor.FileRef.GetEntry(donorSlots[slot])!)!;
                var saved = reopened.GetEntry(savedSlots[slot]) as ExportEntry;
                TestAssert.True(saved is not null, $"MotoGlasses material slot {slot} was not materialised.");
                TestAssert.Equal(PccPackageWorkflow.CanonicalPath(authored), saved!.InstancedFullPath);
                TestAssert.Equal(authored.ClassName, saved.ClassName);
                TestAssert.True(authored.GetProperties().Equivalent(saved.GetProperties(), (left, right) =>
                {
                    if (left == 0 || right == 0) return left == right;
                    var sourceEntry = authored.FileRef.GetEntry(left);
                    var savedEntry = reopened.GetEntry(right);
                    return sourceEntry is not null && savedEntry is not null &&
                        sourceEntry.ClassName == savedEntry.ClassName &&
                        PccPackageWorkflow.CanonicalPath(sourceEntry).Equals(savedEntry.InstancedFullPath,
                            StringComparison.OrdinalIgnoreCase);
                }), $"MotoGlasses material slot {slot} lost authored properties or dependency identities.");
                TestAssert.True(SupportedMaterialGraphPolicy.IsSupported(MEGame.LE1, saved, resolver, out var failure),
                    $"Saved MotoGlasses parent graph could not be resolved: {failure}");
            }
            PackageIntegrity.Verify(reopened);
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static void StockHatAttachmentsSurviveReview()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var service = new MorphFaceConversionService(
            MorphFaceProfileRegistry.CreateDefault(),
            new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            TestFixtures.GetCorpusTextureCatalogService());
        var cases = new[]
        {
            (SourceGame: MorphFaceGame.LE1, TargetGame: MorphFaceGame.LE3, Family: "HMM",
                Face: "rp115_admiral", Kind: "Other", SourcePath: "BIOG_HMM_HIR_PRO_R.Officer.HMM_HAT_Off_MDL",
                TargetPath: "BIOG_HMM_HIR_PRO_R.Hat_Officer.HMM_HAT_Off_MDL",
                MaterialPath: "BIOG_HMM_HIR_PRO_R.Hat_Officer.HMM_HAT_Off_MAT_1a"),
            (SourceGame: MorphFaceGame.LE3, TargetGame: MorphFaceGame.LE2, Family: "HMF",
                Face: "nor_warroom_guard2", Kind: "Hair", SourcePath: "biog_hmf_hir_pro.Hat_Beret.HMF_HAT_Brt_MDL",
                TargetPath: "BIOG_HMF_HIR_PRO.Beret.HMF_HAT_Brt_MDL",
                MaterialPath: "BIOG_HMM_HIR_PRO_R.Beret.HMM_HAT_Brt_MAT_1a")
        };

        foreach (var item in cases)
        {
            var sourcePackage = FixturePath($"{item.SourceGame} GlobalMorphs.pcc");
            var destination = Path.Combine(Path.GetTempPath(), $"MFE-StockHat-{Guid.NewGuid():N}.pcc");
            try
            {
                var result = service.Convert(new MorphFaceConversionRequest(
                    sourcePackage,
                    $"{item.Family}.{item.Face}",
                    item.TargetGame,
                    destination,
                    CreateNewPackage: true,
                    FixturePath($"{item.TargetGame} GlobalMorphs.pcc")));

                TestAssert.True(!result.SaveResult.Warnings.Any(value =>
                        value.Contains("unsupported material", StringComparison.OrdinalIgnoreCase)),
                    $"{item.SourceGame}->{item.TargetGame} {item.Family}.{item.Face} still reports its native stock hat as unsupported: " +
                    string.Join(" | ", result.SaveResult.Warnings));

                using var package = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
                var face = package.FindExport(result.SaveResult.FaceInstancedPath, "BioMorphFace")
                           ?? throw new InvalidDataException($"Converted face '{result.SaveResult.FaceInstancedPath}' is missing.");
                var attachment = item.Kind == "Hair"
                    ? face.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package)
                    : face.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
                        .Select(value => value.ResolveToEntry(package))
                        .FirstOrDefault(value => value is not null);
                TestAssert.True(attachment is ExportEntry mesh,
                    $"The exact reviewed stock attachment '{item.TargetPath}' was omitted.");
                TestAssert.True(attachment!.InstancedFullPath.Equals(item.TargetPath,
                        StringComparison.OrdinalIgnoreCase),
                    $"Expected exact target attachment '{item.TargetPath}', got '{attachment.InstancedFullPath}'.");
                var materialIndices = ((ExportEntry)attachment).GetBinaryData<SkeletalMesh>().Materials;
                TestAssert.True(materialIndices.Length > 0, $"Stock hat '{item.TargetPath}' had no material slots.");
                var material = package.GetEntry(materialIndices[0]);
                TestAssert.True(material is not null && material.ClassName.Equals(
                        "MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) &&
                    material.InstancedFullPath.Equals(item.MaterialPath, StringComparison.OrdinalIgnoreCase),
                    $"Expected native material '{item.MaterialPath}', got '{material?.InstancedFullPath ?? "<unresolved>"}'.");
            }
            finally
            {
                if (File.Exists(destination)) File.Delete(destination);
            }
        }
    }

    private static void MaleBeanie01OtherConvertsToLe3()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const string sourcePath = "BIOG_HMM_HIR_PRO_R.Beanie01.HMM_HAT_Bn1_MDL";
        const string targetPath = "BIOG_HMM_HIR_PRO_R.Hat_Beanie01.HMM_HAT_Bn1_MDL";
        var sourcePackage = FixturePath("LE2 GlobalMorphs.pcc");
        var alteredSource = Path.Combine(Path.GetTempPath(), $"MFE-Beanie01-Other-Source-{Guid.NewGuid():N}.pcc");
        var destination = Path.Combine(Path.GetTempPath(), $"MFE-Beanie01-Other-LE3-{Guid.NewGuid():N}.pcc");
        try
        {
            using var reader = new MorphFacePackageReader();
            var loaded = reader.Load(sourcePackage, "HMM.BioFace_Archer");
            using var cache = new PackageCache { CacheMaxSize = 8 };
            var sourceDonor = new InstalledGameAssetResolver().Resolve(MEGame.LE2, sourcePath, "SkeletalMesh", cache)!;
            var movedToOther = loaded.Document with
            {
                HairMeshReference = null,
                OtherMeshReferences = [new AssetIdentity(
                    sourceDonor.FileRef.FilePath, sourcePath, sourceDonor.UIndex, "SkeletalMesh")]
            };
            new MorphFacePackageWriter().SaveMorphToPackage(
                movedToOther, sourcePackage, alteredSource, createNewPackage: true);

            var service = new MorphFaceConversionService(
                MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog(),
                new MorphFacePackageContextService(), TestFixtures.GetCorpusTextureCatalogService());
            var result = service.Convert(new MorphFaceConversionRequest(
                alteredSource, movedToOther.Source.InstancedPath, MorphFaceGame.LE3, destination,
                CreateNewPackage: true, FixturePath("LE3 GlobalMorphs.pcc")));
            AssertReopenedHatIdentity(destination, result.SaveResult.FaceInstancedPath,
                "m_oOtherMeshes", targetPath, MEGame.LE3);
        }
        finally
        {
            if (File.Exists(alteredSource)) File.Delete(alteredSource);
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static void FemaleCapHairConvertsToLe2()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        const string sourcePath = "BIOG_HMF_HIR_PRO.Hat_Cap.HMF_HAT_Cap_MDL";
        const string targetPath = "BIOG_HMF_HIR_PRO.Cap.HMF_HAT_Cap_MDL";
        var sourcePackage = FixturePath("LE3 GlobalMorphs.pcc");
        var alteredSource = Path.Combine(Path.GetTempPath(), $"MFE-HMF-Cap-Source-{Guid.NewGuid():N}.pcc");
        var destination = Path.Combine(Path.GetTempPath(), $"MFE-HMF-Cap-LE2-{Guid.NewGuid():N}.pcc");
        try
        {
            using var reader = new MorphFacePackageReader();
            var loaded = reader.Load(sourcePackage, "HMF.Asari");
            using var cache = new PackageCache { CacheMaxSize = 8 };
            var sourceDonor = new InstalledGameAssetResolver().Resolve(MEGame.LE3, sourcePath, "SkeletalMesh", cache)!;
            var withCap = loaded.Document with
            {
                HairMeshReference = new AssetIdentity(
                    sourceDonor.FileRef.FilePath, sourcePath, sourceDonor.UIndex, "SkeletalMesh"),
                OtherMeshReferences = []
            };
            new MorphFacePackageWriter().SaveMorphToPackage(
                withCap, sourcePackage, alteredSource, createNewPackage: true);
            var service = new MorphFaceConversionService(
                MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog(),
                new MorphFacePackageContextService(), TestFixtures.GetCorpusTextureCatalogService());
            var result = service.Convert(new MorphFaceConversionRequest(
                alteredSource, withCap.Source.InstancedPath, MorphFaceGame.LE2,
                destination, CreateNewPackage: true, FixturePath("LE2 GlobalMorphs.pcc")));
            AssertReopenedHatIdentity(destination, result.SaveResult.FaceInstancedPath,
                "m_oHairMesh", targetPath, MEGame.LE2);
        }
        finally
        {
            if (File.Exists(alteredSource)) File.Delete(alteredSource);
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static void AssertReopenedHatIdentity(
        string packagePath, string facePath, string propertyName, string expectedMeshPath, MEGame game)
    {
        using var reopened = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var face = reopened.FindExport(facePath, "BioMorphFace")
                   ?? throw new InvalidDataException($"Converted face '{facePath}' is missing after reopen.");
        var attachment = propertyName.Equals("m_oHairMesh", StringComparison.Ordinal)
            ? face.GetProperty<ObjectProperty>(propertyName)?.ResolveToEntry(reopened) as ExportEntry
            : face.GetProperty<ArrayProperty<ObjectProperty>>(propertyName)?
                .Select(value => value.ResolveToEntry(reopened)).OfType<ExportEntry>()
                .SingleOrDefault(value => value.InstancedFullPath.Equals(expectedMeshPath,
                    StringComparison.OrdinalIgnoreCase));
        TestAssert.True(attachment is not null, $"Converted {propertyName} hat was omitted after reopen.");
        TestAssert.True(expectedMeshPath.Equals(attachment!.InstancedFullPath, StringComparison.OrdinalIgnoreCase),
            $"Expected exact mesh '{expectedMeshPath}', got '{attachment.InstancedFullPath}'.");

        using var cache = new PackageCache { CacheMaxSize = 8 };
        var donor = new InstalledGameAssetResolver().Resolve(game, expectedMeshPath, "SkeletalMesh", cache)
                    ?? throw new InvalidDataException($"Official {game} hat donor '{expectedMeshPath}' was not found.");
        var expectedSlots = donor.GetBinaryData<SkeletalMesh>().Materials;
        var actualSlots = attachment.GetBinaryData<SkeletalMesh>().Materials;
        TestAssert.Equal(expectedSlots.Length, actualSlots.Length);
        var resolver = new GamePackageReferenceResolver(cache);
        for (var index = 0; index < expectedSlots.Length; index++)
        {
            var expected = donor.FileRef.GetEntry(expectedSlots[index])
                           ?? throw new InvalidDataException($"Native donor slot {index} is unresolved.");
            var actual = reopened.GetEntry(actualSlots[index]);
            TestAssert.True(actual is not null, $"Saved slot {index} for '{expectedMeshPath}' is unresolved.");
            TestAssert.Equal(expected.ClassName, actual!.ClassName);
            TestAssert.True(PccPackageWorkflow.CanonicalPath(expected).Equals(actual.InstancedFullPath,
                StringComparison.OrdinalIgnoreCase), "Saved material slot identity differs from its official donor.");
            var authoredMaterial = resolver.Resolve(expected)!;
            var savedMaterial = resolver.Resolve(actual)!;
            TestAssert.True(authoredMaterial.GetProperties().Equivalent(savedMaterial.GetProperties(), (left, right) =>
            {
                if (left == 0 || right == 0) return left == right;
                var authoredEntry = authoredMaterial.FileRef.GetEntry(left);
                var savedEntry = savedMaterial.FileRef.GetEntry(right);
                return authoredEntry is not null && savedEntry is not null &&
                    authoredEntry.ClassName == savedEntry.ClassName &&
                    PccPackageWorkflow.CanonicalPath(authoredEntry).Equals(
                        PccPackageWorkflow.CanonicalPath(savedEntry), StringComparison.OrdinalIgnoreCase);
            }), $"Saved hat slot {index} lost authored material properties or dependency identities.");
            TestAssert.True(SupportedMaterialGraphPolicy.IsSupported(game, savedMaterial, resolver, out var failure),
                $"Saved hat slot {index} has an unresolved parent graph: {failure}");
        }
        PackageIntegrity.Verify(reopened);
        var snapshot = PccPackageWorkflow.CaptureDependencyGraph(attachment);
        using var independentlyReopened = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        PccPackageWorkflow.VerifyDependencyGraph(independentlyReopened, snapshot, true);
    }

    private static void CustomHairMicUsesCanonicalParent()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MFE-ShepardHairMaterial.pcc", MEGame.LE3);
        var masterPackage = package.CreatePackageExport("BIOG_Humanoid_MASTER_MTR_R");
        var humanGroup = package.CreatePackageExport("Human", masterPackage);
        var hairMaster = package.CreateExport("HMN_HED_PRO_MASTER_HAIR_MAT", "Material", humanGroup, indexed: false);
        var customMic = package.CreateExport("MFE_CustomHairMic", "MaterialInstanceConstant", indexed: false);
        customMic.WriteProperty(new ObjectProperty(hairMaster, "Parent"));
        var customMesh = package.CreateExport("MFE_CustomHairMesh", "SkeletalMesh", indexed: false);
        var binary = SkeletalMesh.Create();
        binary.Materials = [customMic.UIndex];
        customMesh.WriteBinary(binary);

        TestAssert.True(
            MorphFaceAttachmentTransferEngine.HasSupportedMaterials(package, customMesh, out var failure),
            $"A custom MIC using the supported canonical hair master was rejected: {failure}");
    }

    private static void CustomAttachmentsUseCanonicalTurianParent()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MFE-TufAttachment.pcc", MEGame.LE3);
        var masterPackage = package.CreatePackageExport("BIOG_Humanoid_MASTER_MTR_R");
        var turianGroup = package.CreatePackageExport("Turian", masterPackage);
        var turianMaster = package.CreateExport("TUR_HED_PRO_MASTER_MAT", "Material", turianGroup, indexed: false);
        var tufMic = package.CreateExport("TUF_HED_PRO_Face_Mat", "MaterialInstanceConstant", indexed: false);
        tufMic.WriteProperty(new ObjectProperty(turianMaster, "Parent"));
        var tufMesh = package.CreateExport("TUF_HED_PROBase_MDL", "SkeletalMesh", indexed: false);
        var tufBinary = SkeletalMesh.Create();
        tufBinary.Materials = [tufMic.UIndex];
        tufMesh.WriteBinary(tufBinary);

        TestAssert.True(
            MorphFaceAttachmentTransferEngine.HasSupportedMaterials(package, tufMesh, out var tufFailure),
            $"The TUF MIC sharing the exact supported TUR master was rejected: {tufFailure}");

        var ordinaryMic = package.CreateExport("HMM_HED_Custom_Mat", "MaterialInstanceConstant", indexed: false);
        ordinaryMic.WriteProperty(new ObjectProperty(turianMaster, "Parent"));
        var ordinaryMesh = package.CreateExport("HMM_HED_Custom_MDL", "SkeletalMesh", indexed: false);
        var ordinaryBinary = SkeletalMesh.Create();
        ordinaryBinary.Materials = [ordinaryMic.UIndex];
        ordinaryMesh.WriteBinary(ordinaryBinary);
        TestAssert.True(
            MorphFaceAttachmentTransferEngine.HasSupportedMaterials(package, ordinaryMesh, out var failure),
            $"A custom MIC using the supported canonical TUR master was rejected: {failure}");
    }

    private static void InstalledLe1AsariTemplateSurvivesMod()
    {
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(MorphFaceGame.LE1).State != TextureRegistryState.Ready)
        {
            throw new InvalidOperationException("The v7 LE1 texture database is required for automatic Asari template selection.");
        }
        var installedTemplates = store.Read(MorphFaceGame.LE1).MorphFaceTemplates;
        TestAssert.True(installedTemplates.Any(candidate =>
                candidate.Origin == TextureCatalogOrigin.BaseGame &&
                candidate.FacePath.Equals(
                    "BIOA_STA_FAC.ASA.Ambient.sta60_amb_asari01", StringComparison.OrdinalIgnoreCase) &&
                candidate.BaseHeadPath?.Equals(
                    "BIOG_ASA_HED_PROMorph_R.PROBase.ASA_HED_PROBASE_MDL", StringComparison.OrdinalIgnoreCase) == true),
            "The registry omitted the shadowed physical LE1 Asari face template. Nearby candidates: " +
            string.Join("; ", installedTemplates
                .Where(candidate => candidate.FacePath.Contains("sta60_amb_asari01", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => $"{candidate.Origin} {candidate.PackagePath} {candidate.FacePath} {candidate.BaseHeadPath}")));
        var service = new MorphFaceConversionService(
            MorphFaceProfileRegistry.CreateDefault(),
            new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            new TextureCatalogService(store));
        var destination = Path.Combine(Path.GetTempPath(), $"MFE-D1-Asari-template-{Guid.NewGuid():N}.pcc");
        try
        {
            var result = service.Convert(new MorphFaceConversionRequest(
                FixturePath("LE2 GlobalMorphs.pcc"),
                "ASA.BioFace_Asari_Spectre_Head",
                MorphFaceGame.LE1,
                destination,
                CreateNewPackage: true,
                TemplatePackagePath: null));
            using var reopened = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
            TestAssert.True(reopened.FindExport(result.SaveResult.FaceInstancedPath, "BioMorphFace") is not null,
                "The automatically templated LE1 Asari conversion did not save a face.");
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static void InstalledConversionDependencies()
    {
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var service = new MorphFaceConversionService(profiles, new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault())));
        var cases = new[]
        {
            (MorphFaceGame.LE1, MorphFaceGame.LE2, "asari", "Texture2D", "BIOG_Humanoid_MASTER_MTR_R.Eye.HMF_EYE_MASTER_Diffuse"),
            (MorphFaceGame.LE3, MorphFaceGame.LE2, "asari", "Texture2D", "BIOG_Humanoid_MASTER_MTR_R.Eye.HMF_EYE_MASTER_Diffuse"),
            (MorphFaceGame.LE1, MorphFaceGame.LE3, "krogan", "Texture2D", "BioVFX_Z_TEXTURES.Distortion.fx_Distort"),
            (MorphFaceGame.LE2, MorphFaceGame.LE3, "krogan", "Texture2D", "BioVFX_Z_TEXTURES.Distortion.fx_Distort"),
            (MorphFaceGame.LE1, MorphFaceGame.LE2, "human-female", "RvrEffectsMaterialUser", "EffectsMaterials.Users.Human.HMN_HED_PRO_MASTER_HAIR_MAT_USER"),
            (MorphFaceGame.LE3, MorphFaceGame.LE2, "human-female", "RvrEffectsMaterialUser", "EffectsMaterials.Users.Human.HMN_HED_PRO_MASTER_HAIR_MAT_USER"),
            (MorphFaceGame.LE1, MorphFaceGame.LE3, "human-male", "Texture2D", "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black"),
            (MorphFaceGame.LE2, MorphFaceGame.LE3, "human-male", "Texture2D", "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black")
        };
        var failures = new List<string>();
        foreach (var (sourceGame, targetGame, species, dependencyClass, dependencyPath) in cases)
        {
            var sourcePath = FixturePath($"{sourceGame} GlobalMorphs.pcc");
            var family = species switch
            {
                "asari" => "ASA",
                "krogan" => "KRO",
                "human-female" => "HMF",
                "human-male" => "HMM",
                _ => throw new InvalidOperationException(species)
            };
            var sourceFace = MorphFaceReferenceInspector.Inspect(sourcePath).First(face =>
                face.FacePath.StartsWith($"{family}.", StringComparison.OrdinalIgnoreCase) &&
                (!species.StartsWith("human", StringComparison.Ordinal) || face.HairMeshPath is not null) &&
                profiles.Find(sourceGame, face.FacePath, face.BaseHeadPath)?.Key ==
                $"{sourceGame.ToString().ToLowerInvariant()}-{species}");
            var destination = Path.Combine(Path.GetTempPath(), $"MFE-installed-dependencies-{Guid.NewGuid():N}.pcc");
            var label = $"{sourceGame}->{targetGame} {species}";
            try
            {
                var result = service.Convert(new MorphFaceConversionRequest(sourcePath, sourceFace.FacePath,
                    targetGame, destination, CreateNewPackage: true, TemplatePackagePath: null));
                using var reopened = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
                var convertedFace = reopened.FindExport(result.SaveResult.FaceInstancedPath, "BioMorphFace");
                TestAssert.Equal("MorphFaceEditor", convertedFace?.Parent?.InstancedFullPath);
                TestAssert.True(reopened.FindExport(result.SaveResult.MaterialOverrideInstancedPath, "BioMaterialOverride") is not null,
                    $"{label}: the relocated face lost its material override.");
                var head = convertedFace!.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(reopened);
                TestAssert.True(head is ExportEntry && head.ClassName == "SkeletalMesh",
                    $"{label}: base head '{head?.InstancedFullPath}' was not saved as a SkeletalMesh export.");
                TestAssert.True(ObjectBinary.From<SkeletalMesh>((ExportEntry)head!) is { LODModels.Length: > 0 },
                    $"{label}: the exported base head has no readable mesh LODs.");
                TestAssert.True(!reopened.Imports.Any(entry => entry.ClassName == "SkeletalMesh" &&
                                    entry.InstancedFullPath.Equals(head!.InstancedFullPath, StringComparison.OrdinalIgnoreCase)),
                    $"{label}: the exported base head also survived as an import.");
                TestAssert.True(reopened.FindExport(dependencyPath, dependencyClass) is not null,
                    $"{label}: required {dependencyClass} '{dependencyPath}' was not saved as an export.");
                TestAssert.True(!reopened.Imports.Any(entry => entry.InstancedFullPath.Equals(
                    dependencyPath, StringComparison.OrdinalIgnoreCase) && entry.ClassName == dependencyClass),
                    $"{label}: the required dependency also survived as an import.");
                PackageIntegrity.Verify(reopened);
                Console.WriteLine($"Verified {label}: {result.SaveResult.FaceInstancedPath}");
            }
            catch (Exception exception)
            {
                failures.Add($"{label}: {exception.Message}");
            }
            finally
            {
                if (File.Exists(destination)) File.Delete(destination);
            }
        }
        TestAssert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void D1CorpusConversionMatrix()
    {
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var service = new MorphFaceConversionService(
            profiles,
            new MorphTargetCatalog(),
            new MorphFacePackageContextService(),
            TestFixtures.GetCorpusTextureCatalogService());
        var failures = new List<string>();
        var sources = new List<D1CorpusSource>();

        foreach (var candidate in D1CorpusCandidates())
        {
            var sourcePath = FixturePath($"{candidate.SourceGame} GlobalMorphs.pcc");
            var expectedFacePath = $"{candidate.Family}.{candidate.ObjectName}";
            BioMorphFaceListItem? face = null;
            MorphFaceMeshReferences? references = null;
            try
            {
                var matches = ReadFaces(sourcePath)
                    .Where(value => value.InstancedPath.Equals(
                        expectedFacePath, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length != 1)
                {
                    var available = ReadFaces(sourcePath)
                        .Where(value => value.InstancedPath.StartsWith(
                            $"{candidate.Family}.", StringComparison.OrdinalIgnoreCase))
                        .Select(value => value.InstancedPath)
                        .Take(12);
                    throw new InvalidDataException(
                        $"Exact face lookup found {matches.Length} matches for '{expectedFacePath}'. " +
                        $"Nearby {candidate.Family} paths: {string.Join(", ", available)}");
                }

                face = matches[0];
                references = MorphFaceReferenceInspector.Inspect(sourcePath)
                    .SingleOrDefault(value => value.FacePath.Equals(
                        expectedFacePath, StringComparison.OrdinalIgnoreCase));
                if (references is null)
                {
                    throw new InvalidDataException(
                        $"Exact reference lookup found no BioMorphFace graph for '{expectedFacePath}'.");
                }

                var sourceProfile = profiles.Find(
                    candidate.SourceGame,
                    references.FacePath,
                    references.BaseHeadPath);
                if (sourceProfile is null)
                {
                    throw new NotSupportedException(
                        $"'{expectedFacePath}' has no supported {candidate.SourceGame} profile " +
                        $"for base '{references.BaseHeadPath ?? "<unresolved>"}'.");
                }

                var expectedProfilePrefix = candidate.Family switch
                {
                    "HMM" => "human-male",
                    "HMF" => "human-female",
                    "ASA" => "asari",
                    "SAL" => "salarian",
                    "TUR" => "turian",
                    "KRO" => "krogan",
                    "BAT" => "batarian",
                    "ALN" => "vorcha",
                    _ => throw new InvalidDataException($"Unknown D1 family '{candidate.Family}'.")
                };
                if (!sourceProfile.Key.EndsWith(
                        $"-{expectedProfilePrefix}", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"'{expectedFacePath}' resolved to profile '{sourceProfile.Key}', " +
                        $"not the expected {expectedProfilePrefix} profile.");
                }

                sources.Add(new D1CorpusSource(candidate, sourcePath, face!, references, sourceProfile.Key));
            }
            catch (Exception exception)
            {
                failures.Add($"SOURCE {candidate.SourceGame}/{expectedFacePath}: {exception.Message}");
            }
        }

        AssertD1AttachmentCoverage(sources, failures);

        var conversionCount = 0;
        foreach (var source in sources)
        {
            foreach (var targetGame in D1TargetGames(source.Candidate))
            {
                conversionCount++;
                var label = $"{source.Candidate.SourceGame}->{targetGame} " +
                             $"{source.References.FacePath}";
                var destination = Path.Combine(
                    Path.GetTempPath(), $"MFE-D1-{Guid.NewGuid():N}.pcc");
                try
                {
                    var sourceInfo = new FileInfo(source.SourcePath);
                    var result = service.Convert(new MorphFaceConversionRequest(
                        source.SourcePath,
                        source.References.FacePath,
                        targetGame,
                        destination,
                        CreateNewPackage: true,
                        FixturePath($"{targetGame} GlobalMorphs.pcc")));
                    AssertD1ConversionReopened(
                        destination,
                        result,
                        targetGame,
                        source,
                        label);

                    var unchanged = new FileInfo(source.SourcePath);
                    TestAssert.Equal(sourceInfo.Length, unchanged.Length);
                    TestAssert.Equal(sourceInfo.LastWriteTimeUtc, unchanged.LastWriteTimeUtc);
                    var sourceAfter = MorphFaceReferenceInspector.Inspect(source.SourcePath)
                        .SingleOrDefault(value => value.FacePath.Equals(
                            source.References.FacePath, StringComparison.OrdinalIgnoreCase));
                    TestAssert.True(sourceAfter is not null,
                        $"{label}: source face disappeared after conversion.");
                    TestAssert.Equal(source.References.HairMeshPath, sourceAfter!.HairMeshPath);
                    TestAssert.True(
                        source.References.OtherMeshPaths.SequenceEqual(
                            sourceAfter.OtherMeshPaths, StringComparer.OrdinalIgnoreCase),
                        $"{label}: source m_oOtherMeshes changed after conversion.");
                }
                catch (Exception exception)
                {
                    failures.Add($"CASE {label}: {exception.Message}");
                }
                finally
                {
                    if (File.Exists(destination))
                    {
                        File.Delete(destination);
                    }
                }
            }
        }

        const int expectedConversionCount = MorphFaceEditor.TestData.ConversionMatrix.ExpectedCount;
        if (conversionCount != expectedConversionCount)
        {
            failures.Add(
                $"MATRIX expected {expectedConversionCount} conversions but constructed {conversionCount}; " +
                "one or more exact source cases were unavailable or invalid.");
        }

        if (failures.Count > 0)
        {
            throw new Exception(
                $"D1 corpus matrix failed with {failures.Count} issue(s):{Environment.NewLine}" +
                string.Join(Environment.NewLine, failures));
        }
    }

    private static void AssertD1AttachmentCoverage(
        IReadOnlyList<D1CorpusSource> sources,
        ICollection<string> failures)
    {
        foreach (var sourceGame in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            var human = sources
                .Where(source => source.Candidate.SourceGame == sourceGame &&
                                 (source.Candidate.Family is "HMM" or "HMF"))
                .ToArray();
            var masks = human.Select(source => GetD1AttachmentMask(source.References)).ToArray();
            var observed = human.Length == 0
                ? "<none>"
                : string.Join(", ", human.Select(source =>
                    $"{source.References.FacePath}={GetD1AttachmentMask(source.References)}"));
            if (!masks.Contains(D1AttachmentMask.None) ||
                !masks.Any(mask => mask.HasFlag(D1AttachmentMask.Hair)) ||
                !masks.Any(mask => mask.HasFlag(D1AttachmentMask.Other)))
            {
                failures.Add(
                    $"ATTACHMENT COVERAGE {sourceGame}: expected hair, other/hat, and none across " +
                    $"the three HMM and three HMF candidates; observed {observed}.");
            }
        }
    }

    private static void AssertD1ConversionReopened(
        string destination,
        MorphFaceConversionResult result,
        MorphFaceGame targetGame,
        D1CorpusSource source,
        string label)
    {
        TestAssert.True(File.Exists(destination), $"{label}: CreateNewPackage produced no PCC.");
        using var package = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
        TestAssert.Equal(targetGame, D1MorphFaceGame(package.Game));
        var face = package.FindExport(result.SaveResult.FaceInstancedPath, "BioMorphFace")
                   ?? throw new InvalidDataException(
                       $"{label}: saved face '{result.SaveResult.FaceInstancedPath}' was not found.");
        var references = MorphFaceReferenceInspector.Inspect(destination)
            .SingleOrDefault(value => value.FacePath.Equals(
                result.SaveResult.FaceInstancedPath, StringComparison.OrdinalIgnoreCase));
        TestAssert.True(references is not null,
            $"{label}: reopened package has no reference graph for saved face.");
        TestAssert.Equal(targetGame, references!.Game);
        TestAssert.True(!string.IsNullOrWhiteSpace(references.BaseHeadPath),
            $"{label}: target m_oBaseHead is missing from the reference graph.");

        var rawHair = face.GetProperty<ObjectProperty>("m_oHairMesh");
        var hair = rawHair?.ResolveToEntry(package);
        if (rawHair is not null && rawHair.Value != 0)
        {
            TestAssert.True(hair is not null,
                $"{label}: target m_oHairMesh is an unresolved reference.");
            TestAssert.Equal("SkeletalMesh", hair!.ClassName);
        }

        var rawOthers = face.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?.ToArray() ?? [];
        var others = rawOthers.Select((value, index) =>
        {
            var resolved = value.ResolveToEntry(package);
            TestAssert.True(resolved is not null,
                $"{label}: target m_oOtherMeshes[{index}] is an unresolved reference.");
            TestAssert.Equal("SkeletalMesh", resolved!.ClassName);
            return resolved;
        }).ToArray();
        TestAssert.True(
            references.OtherMeshPaths.Count == others.Length,
            $"{label}: reference graph and m_oOtherMeshes disagree ({references.OtherMeshPaths.Count} vs {others.Length}).");
        if (hair is not null)
        {
            TestAssert.True(
                !others.Any(other => other.InstancedFullPath.Equals(
                    hair.InstancedFullPath, StringComparison.OrdinalIgnoreCase)),
                $"{label}: target hair was duplicated into m_oOtherMeshes.");
        }

        var sourceMask = GetD1AttachmentMask(source.References);
        var targetMask = (hair is not null ? D1AttachmentMask.Hair : D1AttachmentMask.None) |
                         (others.Length > 0 ? D1AttachmentMask.Other : D1AttachmentMask.None);
        TestAssert.Equal(sourceMask, targetMask);
        var graphMask = (references.HairMeshPath is not null ? D1AttachmentMask.Hair : D1AttachmentMask.None) |
                        (references.OtherMeshPaths.Count > 0 ? D1AttachmentMask.Other : D1AttachmentMask.None);
        TestAssert.Equal(targetMask, graphMask);
    }

    private static IEnumerable<MorphFaceGame> D1TargetGames(D1CorpusCandidate candidate) =>
        MorphFaceEditor.TestData.ConversionMatrix.TargetGames(candidate.SourceGame, candidate.Family);

    private static IReadOnlyList<D1CorpusCandidate> D1CorpusCandidates() =>
        MorphFaceEditor.TestData.ConversionMatrix.Sources.Select(source =>
            new D1CorpusCandidate(source.SourceGame, source.Family, source.ObjectName)).ToArray();

    private static D1AttachmentMask GetD1AttachmentMask(MorphFaceMeshReferences references) =>
        (references.HairMeshPath is not null ? D1AttachmentMask.Hair : D1AttachmentMask.None) |
        (references.OtherMeshPaths.Count > 0 ? D1AttachmentMask.Other : D1AttachmentMask.None);

    private static MorphFaceGame D1MorphFaceGame(MEGame game) => game switch
    {
        MEGame.LE1 => MorphFaceGame.LE1,
        MEGame.LE2 => MorphFaceGame.LE2,
        MEGame.LE3 => MorphFaceGame.LE3,
        _ => MorphFaceGame.Unsupported
    };

    [Flags]
    private enum D1AttachmentMask
    {
        None = 0,
        Hair = 1,
        Other = 2
    }

    private sealed record D1CorpusCandidate(
        MorphFaceGame SourceGame,
        string Family,
        string ObjectName);

    private sealed record D1CorpusSource(
        D1CorpusCandidate Candidate,
        string SourcePath,
        BioMorphFaceListItem Face,
        MorphFaceMeshReferences References,
        string ProfileKey);

    private static void EmbedMissingAlienTextureIntoLe1()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        WithPackageCopy("LE1 GlobalMorphs.pcc", le1Destination =>
        {
            using (var package = MEPackageHandler.OpenMEPackage(le1Destination, forceLoadFromDisk: true))
            {
                var result = TransferTextures(
                    package,
                    MaterialWithTextures(
                        ("HED_Tattoo", "BIOG_ASA_HED_PROMorph_R.Masks.ASA_HED_PRO_Tat2"),
                        ("HED_Add_Batarian", "BIOG_BAT_HED_PROMorph_R.PROBase.BAT_HED_PROMorph_Add4"),
                        ("HED_Add_Turian", "BIOG_TUR_HED_PROMorph_R.Add.TUR_HED_PRO_Add2")),
                    MEGame.LE3,
                    "le3-asari",
                    FixturePath("LE3 GlobalMorphs.pcc"),
                    FixturePath("LE1 GlobalMorphs.pcc"));

                TestAssert.Equal(3, result.MaterialData.Textures.Count);
                var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "BIOG_ASA_HED_PROMorph_R.Masks.ASA_HED_PRO_Tat2",
                    "BIOG_BAT_HED_PROMorph_R.PROBase.BAT_HED_PROMorph_Add4",
                    "BIOG_TUR_HED_PROMorph_R.Add.TUR_HED_PRO_Add2"
                };
                TestAssert.True(result.MaterialData.Textures.All(value =>
                        value.TextureReference is { } reference && expectedPaths.Remove(reference.InstancedPath)) &&
                    expectedPaths.Count == 0,
                    "At least one missing LE1 alien texture was not embedded at its canonical UE3 path.");
                TestAssert.True(result.Warnings.Count(value =>
                        value.Contains("package-stored", StringComparison.OrdinalIgnoreCase) &&
                        value.Contains("mod's TFC", StringComparison.OrdinalIgnoreCase)) == 3,
                    "The conversion report did not include one package-storage/TFC advisory per embedded texture.");
                package.Save();
            }

            using var reopened = MEPackageHandler.OpenMEPackage(le1Destination, forceLoadFromDisk: true);
            var embedded = reopened.Exports.Where(entry =>
                new[] { "ASA_HED_PRO_Tat2", "BAT_HED_PROMorph_Add4", "TUR_HED_PRO_Add2" }
                    .Contains(entry.ObjectNameString, StringComparer.OrdinalIgnoreCase) &&
                string.Equals(entry.ClassName, "Texture2D", StringComparison.OrdinalIgnoreCase)).ToArray();
            TestAssert.Equal(3, embedded.Length);
            TestAssert.True(embedded.All(entry => new LecTexture2D(entry).GetTopMip().IsPackageStored),
                "At least one embedded alien texture was not serialized as package-stored.");
            TestAssert.True(embedded.All(entry => entry.Parent?.ClassName == "Package"),
                "At least one embedded alien texture lost its package parent.");
        });
    }

    private static TextureTransferResult TransferTextures(
        IMEPackage destination,
        MorphFaceMaterialData source,
        MEGame sourceGame,
        string sourceProfileKey,
        string sourcePackagePath,
        string targetTemplatePackagePath) => MorphFaceTextureTransferEngine.Transfer(
            destination,
            source,
            new HashSet<string>(source.Scalars.Select(value => value.Name), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(source.Vectors.Select(value => value.Name), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(source.Textures.Select(value => value.Name), StringComparer.OrdinalIgnoreCase),
            sourceGame,
            sourceProfileKey,
            sourcePackagePath,
            targetTemplatePackagePath,
            [],
            []);

    private static MorphFaceMaterialData MaterialWithTextures(params (string Parameter, string Path)[] textures) =>
        new([], [], textures.Select(value => new TextureMaterialOverride(
            value.Parameter,
            new AssetIdentity(string.Empty, value.Path, 0, "Texture2D"))).ToArray());

    private static string FixturePath(string fileName) =>
        Path.GetFullPath(Path.Combine("tests", "Global Morphs", fileName));

    private static void AssertConvertedFaceIsEditable(
        string packagePath,
        string facePath,
        MorphFaceProfileRegistry profiles,
        MorphTargetCatalog targets)
    {
        using var reader = new MorphFacePackageReader();
        var loaded = reader.Load(packagePath, facePath);
        var profile = profiles.Require(
            loaded.Game,
            loaded.Document.Source.InstancedPath,
            loaded.Document.BaseHeadReference?.InstancedPath);
        var session = new MorphFaceEditingSession(
            loaded.Document,
            loaded.BaseHead,
            targets.Load(profile, loaded.Game),
            profile.MetadataOnlyFeatures,
            profile.DisplayName,
            profile.FeatureAliases,
            profile.RecognizesBaseVariant,
            profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath));
        TestAssert.True(session.CanEditMorphFeatures, session.EditBlockReason ?? "Converted face was not editable.");
    }

    private static void PasteLe3MaterialData()
    {
        WithPackageCopy("LE3 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            var pair = ReadFaces(path)
                .GroupBy(face => face.ProfileKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() >= 2)
                .Select(group => group.Take(2).ToArray())
                .First();
            var sourceData = service.CaptureMaterialData(path, pair[0].InstancedPath);

            service.PasteMaterialData(path, pair[1].InstancedPath, sourceData);

            AssertMaterialEqual(sourceData, service.CaptureMaterialData(path, pair[1].InstancedPath));
        });
    }

    private static void RonRoundTripsMorph()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            var source = ReadFaces(path).First(face =>
                service.CaptureMorphData(path, face.InstancedPath).BakedLods.Count > 0);
            var expectedMorph = service.CaptureMorphData(path, source.InstancedPath);
            var expectedMaterial = service.CaptureMaterialData(path, source.InstancedPath);
            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-{Guid.NewGuid():N}.ron");
            try
            {
                service.ExportRon(path, source.InstancedPath, ronPath);
                var result = service.ImportRon(
                    path,
                    source.InstancedPath,
                    "MFE_RonRoundTrip_Test",
                    ronPath);
                AssertMorphEqual(expectedMorph, service.CaptureMorphData(path, result.FaceInstancedPath));
                AssertMaterialEqual(expectedMaterial, service.CaptureMaterialData(path, result.FaceInstancedPath));
                var text = File.ReadAllText(ronPath);
                TestAssert.True(text.Contains("morph_features", StringComparison.Ordinal) &&
                                text.Contains("lod0_vertices", StringComparison.Ordinal),
                    "The exported RON did not use Trilogy Save Editor's HeadMorph field names.");
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
            }
        });
    }

    private static void RonUnresolvedAssetsFailAtomically()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            var source = ReadFaces(path).First(face =>
                service.CaptureMaterialData(path, face.InstancedPath).Textures.Any(value => value.TextureReference is not null));
            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-{Guid.NewGuid():N}.ron");
            var sourceFingerprint = PackageFingerprint.Capture(path);
            var sourceMaterial = service.CaptureMaterialData(path, source.InstancedPath);
            var missingPath = "BIOG_MFE_Missing_Donor.MissingTexture";
            try
            {
                service.ExportRon(path, source.InstancedPath, ronPath);
                var original = File.ReadAllText(ronPath);
                var originalPath = sourceMaterial.Textures.First(value => value.TextureReference is not null)
                    .TextureReference!.InstancedPath;
                File.WriteAllText(ronPath, original.Replace(
                    $"\"{originalPath}\"", $"\"{missingPath}\"", StringComparison.Ordinal));

                var threw = false;
                try
                {
                    _ = service.ImportRon(path, source.InstancedPath, "MFE_UnresolvedRon", ronPath);
                }
                catch (InvalidDataException exception)
                {
                    threw = exception.Message.Contains(missingPath, StringComparison.Ordinal);
                }

                TestAssert.True(threw, "An unresolved RON texture did not fail with its exact missing path.");
                TestAssert.Equal(sourceFingerprint, PackageFingerprint.Capture(path));
                TestAssert.True(ReadFaces(path).All(face =>
                    !face.ObjectName.Equals("MFE_UnresolvedRon", StringComparison.OrdinalIgnoreCase)),
                    "An unresolved RON import left a partial BioMorphFace in the package.");
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
            }
        });
    }

    private static void StandaloneRonUnresolvedAttachmentFailsAtomically()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        WithPackageCopy("LE3 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            string facePath;
            string hairPath;
            int hairUIndex;
            using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
            {
                var face = package.Exports.First(candidate =>
                    candidate.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase) &&
                    candidate.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package) is not null);
                facePath = face.InstancedFullPath;
                var hair = face.GetProperty<ObjectProperty>("m_oHairMesh")!.ResolveToEntry(package)!;
                hairPath = hair.InstancedFullPath;
                hairUIndex = hair.UIndex;
            }

            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-RonMissingHair-{Guid.NewGuid():N}.ron");
            const string missingPath = "BIOG_MFE_Missing_Hair.Hair_Missing.HMF_HIR_Missing_MDL";
            try
            {
                // Reproduce a cloned-template import placeholder with an exact donor candidate.
                using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
                {
                    _ = MorphFaceAttachmentTransferEngine.EnsureImport(package, missingPath);
                    package.Save();
                }
                var sourceFingerprint = PackageFingerprint.Capture(path);
                service.ExportRon(path, facePath, ronPath);
                var original = File.ReadAllText(ronPath);
                File.WriteAllText(ronPath, original.Replace(
                    $"hair_mesh: \"{hairPath}\"",
                    $"hair_mesh: \"{missingPath}\"",
                    StringComparison.Ordinal));

                var threw = false;
                try
                {
                    _ = service.ImportStandalonePlayerRon(
                        path,
                        facePath,
                        "MFE_UnresolvedHair",
                        ronPath,
                        new StandalonePlayerAssetCatalog(
                            MorphFaceGame.LE3,
                            [],
                            [new AssetIdentity(path, missingPath, hairUIndex, "SkeletalMesh")]));
                }
                catch (InvalidDataException exception)
                {
                    threw = exception.Message.Contains(missingPath, StringComparison.Ordinal) &&
                        (exception.Message.Contains("import placeholder", StringComparison.OrdinalIgnoreCase) ||
                         exception.Message.Contains("cannot be exported safely", StringComparison.OrdinalIgnoreCase));
                }

                TestAssert.True(threw,
                    "An unresolved standalone RON hair mesh was accepted as a placeholder import.");
                TestAssert.Equal(sourceFingerprint, PackageFingerprint.Capture(path));
                TestAssert.True(ReadFaces(path).All(face =>
                    !face.ObjectName.Equals("MFE_UnresolvedHair", StringComparison.OrdinalIgnoreCase)),
                    "An unresolved standalone hair import left a partial BioMorphFace in the package.");
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
            }
        });
    }

    private static void StandaloneNpcRonUnresolvedHairDegrades()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        WithPackageCopy("LE3 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            string facePath;
            string hairPath;
            int hairUIndex;
            using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
            {
                var face = package.Exports.First(candidate =>
                    candidate.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase) &&
                    candidate.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package) is not null);
                facePath = face.InstancedFullPath;
                var hair = face.GetProperty<ObjectProperty>("m_oHairMesh")!.ResolveToEntry(package)!;
                hairPath = hair.InstancedFullPath;
                hairUIndex = hair.UIndex;
            }

            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-RonNpcMissingHair-{Guid.NewGuid():N}.ron");
            const string missingPath = "BIOG_MFE_Missing_Npc_Hair.Hair_Missing.HMF_HIR_Missing_MDL";
            try
            {
                // Leave the destination with a cloned-template import placeholder. The NPC
                // route may omit this unsafe hair reference, while Player remains strict.
                using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
                {
                    _ = MorphFaceAttachmentTransferEngine.EnsureImport(package, missingPath);
                    package.Save();
                }

                service.ExportRon(path, facePath, ronPath);
                var original = File.ReadAllText(ronPath);
                File.WriteAllText(ronPath, original.Replace(
                    $"hair_mesh: \"{hairPath}\"",
                    $"hair_mesh: \"{missingPath}\"",
                    StringComparison.Ordinal));
                var expectedMorph = service.CaptureMorphData(path, facePath);

                var result = service.ImportStandaloneNpcRon(
                    path,
                    facePath,
                    "MFE_Npc_UnresolvedHair",
                    ronPath,
                    MorphFaceGame.LE3,
                    MorphFaceGame.LE3,
                    "le3-hmm",
                    path,
                    [],
                    [],
                    new StandalonePlayerAssetCatalog(
                        MorphFaceGame.LE3,
                        [],
                        [new AssetIdentity(path, missingPath, hairUIndex, "SkeletalMesh")]));

                TestAssert.True(result.Warnings.Any(warning =>
                        warning.Contains(missingPath, StringComparison.Ordinal) &&
                        warning.Contains("omitted", StringComparison.OrdinalIgnoreCase) &&
                        warning.Contains("import placeholder", StringComparison.OrdinalIgnoreCase)),
                    "The NPC import did not report the omitted hair path and reason.");
                AssertMorphEqual(expectedMorph,
                    service.CaptureMorphData(path, result.FaceInstancedPath));

                using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
                {
                    var imported = package.FindEntry(result.FaceInstancedPath, "BioMorphFace") as ExportEntry;
                    TestAssert.True(imported is not null, "The NPC import did not publish its BioMorphFace.");
                    TestAssert.True(imported!.GetProperty<ObjectProperty>("m_oHairMesh") is null,
                        "The NPC import retained an unresolved m_oHairMesh reference.");
                }

                var exportedPath = Path.Combine(Path.GetTempPath(),
                    $"MFE-NpcMissingHairExport-{Guid.NewGuid():N}.pcc");
                try
                {
                    using var reader = new MorphFacePackageReader();
                    var loaded = reader.Load(path, result.FaceInstancedPath);
                    var saved = new MorphFacePackageWriter().SaveMorphToPackage(
                        loaded.Document,
                        path,
                        exportedPath,
                        createNewPackage: true);
                    AssertMorphEqual(expectedMorph,
                        service.CaptureMorphData(exportedPath, saved.FaceInstancedPath));
                }
                finally
                {
                    if (File.Exists(exportedPath)) File.Delete(exportedPath);
                }
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
            }
        });
    }

    private static void StandalonePlayerRonSexDetection()
    {
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2 })
        {
            TestAssert.Equal(StandalonePlayerSex.Male,
                StandalonePlayerMorphImportService.IdentifySex(game, 2294));
            TestAssert.Equal(StandalonePlayerSex.Female,
                StandalonePlayerMorphImportService.IdentifySex(game, 2232));
        }
        TestAssert.Equal(StandalonePlayerSex.Male,
            StandalonePlayerMorphImportService.IdentifySex(MorphFaceGame.LE3, 2392));
        TestAssert.Equal(StandalonePlayerSex.Female,
            StandalonePlayerMorphImportService.IdentifySex(MorphFaceGame.LE3, 2390));

        var rejected = false;
        try
        {
            _ = StandalonePlayerMorphImportService.IdentifySex(MorphFaceGame.LE3, 2294);
        }
        catch (InvalidDataException exception)
        {
            rejected = exception.Message.Contains("expected 2392", StringComparison.Ordinal) &&
                       exception.Message.Contains("2390", StringComparison.Ordinal);
        }
        TestAssert.True(rejected, "LE3 accepted an LE1/LE2 player topology or returned an unclear error.");
    }

    private static void StandalonePlayerRonImport()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cooked = LegendaryExplorerCoreRuntime.DefaultLe1CookedPath;
        var seedPath = cooked is null ? null : Path.Combine(cooked, "EntryMenu.pcc");
        if (seedPath is null || !File.Exists(seedPath))
        {
            // This is an installed-game integration check; source fixtures do
            // not contain the player packages needed for standalone import.
            return;
        }

        const string templatePath = "BIOG_MORPH_FACE.Player_Base_Male";
        var context = new MorphFacePackageContextService();
        var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-StandalonePlayer-{Guid.NewGuid():N}.ron");
        var before = PackageFingerprint.Capture(seedPath);
        try
        {
            context.ExportRon(seedPath, templatePath, ronPath);
            var seedRon = TseHeadMorphRon.Read(ronPath);
            const string customHairPath = "MFE_Custom_Hair.Hair.HMM_HIR_Custom_MDL";
            var customPackagePath = Path.Combine(Path.GetTempPath(), "MFE_Custom_Hair.pcc");
            var customOccurrence = new AttachmentMeshOccurrence(customPackagePath,
                "Hair.HMM_HIR_Custom_MDL", 17, 0, TextureCatalogOrigin.Manual, 42);
            TseHeadMorphRon.Write(ronPath, seedRon with { HairMesh = customHairPath });
            var customAssets = StandalonePlayerAssetCatalog.ForRon(MorphFaceGame.LE1, ronPath,
                [], attachmentMeshes: [new AttachmentMeshCandidate(customHairPath,
                    customOccurrence, [customOccurrence])]);
            TestAssert.Equal(customPackagePath, customAssets.SkeletalMeshes.Single(value =>
                value.InstancedPath == customHairPath).PackagePath);
            TseHeadMorphRon.Write(ronPath, seedRon);
            var expectedMorph = context.CaptureMorphData(seedPath, templatePath);
            var expectedMaterial = context.CaptureMaterialData(seedPath, templatePath);
            TestAssert.True(expectedMorph.BakedLods.Count > 1,
                "The installed player template cannot exercise standalone lower-LOD variance.");
            var importedMorph = expectedMorph with
            {
                BakedLods = [expectedMorph.BakedLods[0].ToArray()]
            };
            var ron = TseHeadMorphRon.Read(ronPath);
            TseHeadMorphRon.Write(ronPath, ron with { MorphData = importedMorph });

            var importService = new StandalonePlayerMorphImportService();
            using var imported = importService.ImportPlayerRon(
                MorphFaceGame.LE1,
                ronPath,
                "Ryan_Test_Morph");
            TestAssert.True(!imported.CanCommit && !imported.Workspace.CanCommit,
                "A standalone player import exposed a commit-capable workspace.");
            TestAssert.Equal(MorphFaceGame.LE1, imported.Game);
            TestAssert.Equal(StandalonePlayerSex.Male, imported.Sex);
            TestAssert.Equal("BIOG_MORPH_FACE.Ryan_Test_Morph", imported.ImportedFacePath);

            AssertMorphEqual(
                importedMorph,
                context.CaptureMorphData(imported.Workspace.WorkingPath, imported.ImportedFacePath));
            AssertMaterialEqual(
                expectedMaterial,
                context.CaptureMaterialData(imported.Workspace.WorkingPath, imported.ImportedFacePath));

            var appended = importService.ImportPlayerRonIntoWorkspace(
                MorphFaceGame.LE1,
                ronPath,
                "Ryan_Second_Morph",
                imported.Workspace);
            TestAssert.Equal("BIOG_MORPH_FACE.Ryan_Second_Morph", appended.FaceInstancedPath);
            AssertMorphEqual(
                importedMorph,
                context.CaptureMorphData(imported.Workspace.WorkingPath, imported.ImportedFacePath));
            AssertMorphEqual(
                importedMorph,
                context.CaptureMorphData(imported.Workspace.WorkingPath, appended.FaceInstancedPath));

            var sceneFactory = new HeadPreviewSceneFactory();
            var profiles = MorphFaceProfileRegistry.CreateDefault();
            using (var previewLoader = new MorphFacePreviewLoadService(
                       sceneFactory,
                       new MorphTargetCatalog(),
                       profiles,
                       new MorphFacePackageReader()))
            {
                var preview = previewLoader.LoadAsync(
                        imported.Workspace.WorkingPath,
                        imported.ImportedFacePath,
                        geometryMode: MorphFaceGeometryMode.FixedBake)
                    .GetAwaiter().GetResult();
                TestAssert.Equal(MorphFaceGeometryMode.FixedBake, preview.EditingSession.GeometryMode);
                TestAssert.True(!preview.EditingSession.CanEditMorphFeatures,
                    "A standalone fixed bake exposed morph sliders that could replace its imported geometry.");
                TestAssert.True(preview.EditingSession.CreateDraft(
                            preview.Loaded.Document.HairMeshReference,
                            preview.Loaded.Document.OtherMeshReferences,
                            preview.Loaded.Document.MaterialOverrides).BakedLods[0]
                        .SequenceEqual(importedMorph.BakedLods[0]),
                    "Loading the standalone preview replaced its imported LOD0 bake.");
            }

            var commitThrew = false;
            try
            {
                imported.Workspace.Commit();
            }
            catch (InvalidOperationException)
            {
                commitThrew = true;
            }
            TestAssert.True(commitThrew, "A detached standalone workspace accepted Commit().");
            TestAssert.Equal(before, PackageFingerprint.Capture(seedPath));
        }
        finally
        {
            if (File.Exists(ronPath))
            {
                File.Delete(ronPath);
            }
        }
    }

    private static void StandaloneNpcRonImport()
    {
        WithPackageCopy("LE2 GlobalMorphs.pcc", donorPath =>
        {
            var context = new MorphFacePackageContextService();
            var donor = ReadFaces(donorPath).First(face =>
                face.ProfileKey.Equals("le2-human-male", StringComparison.OrdinalIgnoreCase) &&
                face.InstancedPath.Equals("HMM.BioFace_Security2", StringComparison.OrdinalIgnoreCase));
            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-StandaloneNpc-{Guid.NewGuid():N}.ron");
            var donorFingerprint = PackageFingerprint.Capture(donorPath);
            try
            {
                AssertNpcEyeGraph(donorPath, donor.InstancedPath,
                    "The source must be a native HMM NPC donor, not a Player face classified under the shared human-male profile.");
                context.ExportRon(donorPath, donor.InstancedPath, ronPath);
                var expectedMorph = context.CaptureMorphData(donorPath, donor.InstancedPath);
                var expectedMaterial = context.CaptureMaterialData(donorPath, donor.InstancedPath);

                using var imported = new StandaloneNpcMorphImportService().ImportNpcRon(
                    new StandaloneNpcMorphImportRequest(
                        MorphFaceGame.LE2,
                        MorphFaceGame.LE2,
                        "HMM",
                        ronPath,
                        donorPath,
                        donor.InstancedPath,
                        "MFE_Npc_HMM",
                        IsVerifiedNativeNpcDonor: true));

                TestAssert.True(!imported.CanCommit && !imported.Workspace.CanCommit,
                    "An NPC import exposed a commit-capable workspace.");
                TestAssert.Equal(MorphFaceGame.LE2, imported.TargetGame);
                TestAssert.Equal("HMM", imported.Archetype);
                AssertNpcEyeGraph(imported.Workspace.WorkingPath, imported.ImportedFacePath,
                    "The detached NPC import did not preserve the source eye-material parent graph.");
                AssertMorphEqual(
                    expectedMorph,
                    context.CaptureMorphData(imported.Workspace.WorkingPath, imported.ImportedFacePath));
                AssertMaterialEqual(
                    expectedMaterial,
                    context.CaptureMaterialData(imported.Workspace.WorkingPath, imported.ImportedFacePath));
                TestAssert.Equal(donorFingerprint, PackageFingerprint.Capture(donorPath));

                var exportedPath = Path.Combine(Path.GetTempPath(),
                    $"MFE-NpcExport-{Guid.NewGuid():N}.pcc");
                try
                {
                    using var reader = new MorphFacePackageReader();
                    var loaded = reader.Load(imported.Workspace.WorkingPath, imported.ImportedFacePath);
                    var saved = new MorphFacePackageWriter().SaveMorphToPackage(
                        loaded.Document,
                        imported.Workspace.WorkingPath,
                        exportedPath,
                        createNewPackage: true);
                    AssertNpcEyeGraph(exportedPath, saved.FaceInstancedPath,
                        "The standalone NPC RON export did not preserve the source eye-material parent graph.");
                    AssertMorphEqual(expectedMorph,
                        context.CaptureMorphData(exportedPath, saved.FaceInstancedPath));
                }
                finally
                {
                    if (File.Exists(exportedPath)) File.Delete(exportedPath);
                }
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
            }
        });

        static void AssertNpcEyeGraph(string packagePath, string facePath, string failureContext)
        {
            using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
            var face = package.FindExport(facePath, "BioMorphFace");
            TestAssert.True(face is not null, $"{failureContext} Face export '{facePath}' was not found.");
            var baseHead = face!.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package) as ExportEntry;
            TestAssert.True(baseHead is not null, $"{failureContext} Base head for '{facePath}' was not an export.");
            var evidence = MorphFaceEditor.QA.QaNpcEyeGraphCheck.Inspect(package, baseHead!, "HMM");
            TestAssert.True(evidence.Passed,
                $"{failureContext} {string.Join("; ", evidence.Failures)}");
        }
    }

    private static void CancelledStandaloneNpcConstructionCleansCandidates()
    {
        WithPackageCopy("LE2 GlobalMorphs.pcc", donorPath =>
        {
            var context = new MorphFacePackageContextService();
            var donor = ReadFaces(donorPath).First(face =>
                face.ProfileKey.Equals("le2-human-male", StringComparison.OrdinalIgnoreCase));
            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-StandaloneNpc-Cancel-{Guid.NewGuid():N}.ron");
            using var priorWorkspace = new MorphFacePackageWorkspace(donorPath, canCommit: false);
            var priorBytes = File.ReadAllBytes(priorWorkspace.WorkingPath);
            var donorBytes = File.ReadAllBytes(donorPath);
            var workspaceDirectory = Path.GetDirectoryName(priorWorkspace.WorkingPath)!;
            var filesBefore = Directory.EnumerateFiles(workspaceDirectory)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var cancellation = new CancellationTokenSource();
            var interruptionReached = false;
            try
            {
                context.ExportRon(donorPath, donor.InstancedPath, ronPath);
                PccPackageWorkflow.BeforeAtomicReplaceForTesting = (temporaryPath, destination) =>
                {
                    TestAssert.True(temporaryPath.StartsWith(workspaceDirectory, StringComparison.OrdinalIgnoreCase),
                        "NPC cancellation did not interrupt an AppData candidate PCC.");
                    TestAssert.True(destination.StartsWith(workspaceDirectory, StringComparison.OrdinalIgnoreCase),
                        "NPC cancellation reached an installed PCC path.");
                    TestAssert.True(!string.Equals(destination, priorWorkspace.WorkingPath,
                        StringComparison.OrdinalIgnoreCase),
                        "NPC cancellation targeted the prior workspace instead of the off-screen candidate.");
                    interruptionReached = true;
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                };

                var cancelled = false;
                try
                {
                    _ = new StandaloneNpcMorphImportService().ImportNpcRon(
                        new StandaloneNpcMorphImportRequest(
                            MorphFaceGame.LE2,
                            MorphFaceGame.LE2,
                            "HMM",
                            ronPath,
                            donorPath,
                            donor.InstancedPath,
                            "MFE_Cancelled_Npc",
                            IsVerifiedNativeNpcDonor: true,
                            CancellationToken: cancellation.Token));
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                TestAssert.True(cancelled && cancellation.IsCancellationRequested,
                    "Cancelling during NPC candidate construction did not stop the import.");
                TestAssert.True(interruptionReached,
                    "NPC cancellation did not reach the pre-install candidate boundary.");
                TestAssert.True(donorBytes.SequenceEqual(File.ReadAllBytes(donorPath)),
                    "NPC cancellation changed the installed donor PCC.");
                TestAssert.True(priorBytes.SequenceEqual(File.ReadAllBytes(priorWorkspace.WorkingPath)),
                    "NPC cancellation changed the prior workspace.");
                var filesAfter = Directory.EnumerateFiles(workspaceDirectory)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                TestAssert.True(filesAfter.SetEquals(filesBefore),
                    "NPC cancellation left a candidate workspace or temporary PCC in AppData.");
            }
            finally
            {
                PccPackageWorkflow.BeforeAtomicReplaceForTesting = null;
                if (File.Exists(ronPath)) File.Delete(ronPath);
            }
        });
    }

    private static void StandaloneNpcRonLe1ToLe2()
    {
        VerifyDirectNpcRonImport(MorphFaceGame.LE1, MorphFaceGame.LE2);
        VerifyDirectNpcRonImport(MorphFaceGame.LE2, MorphFaceGame.LE1);
    }

    private static void VerifyDirectNpcRonImport(MorphFaceGame sourceGame, MorphFaceGame targetGame)
    {
        WithPackageCopy($"{sourceGame} GlobalMorphs.pcc", sourcePath =>
        WithPackageCopy($"{targetGame} GlobalMorphs.pcc", donorPath =>
        {
            var context = new MorphFacePackageContextService();
            var source = ReadFaces(sourcePath).First(face =>
                face.ProfileKey.Equals($"{sourceGame.ToString().ToLowerInvariant()}-asari", StringComparison.OrdinalIgnoreCase));
            var donor = ReadFaces(donorPath).First(face =>
                face.ProfileKey.Equals($"{targetGame.ToString().ToLowerInvariant()}-asari", StringComparison.OrdinalIgnoreCase));
            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-NpcCrossGame-{Guid.NewGuid():N}.ron");
            var sourceFingerprint = PackageFingerprint.Capture(sourcePath);
            var donorFingerprint = PackageFingerprint.Capture(donorPath);
            try
            {
                context.ExportRon(sourcePath, source.InstancedPath, ronPath);
                var expectedMorph = context.CaptureMorphData(sourcePath, source.InstancedPath);
                var expectedMaterial = context.CaptureMaterialData(sourcePath, source.InstancedPath);
                var registry = new TextureCatalogService(
                    new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));
                var sourceCatalog = registry.ReadAsync(sourceGame).GetAwaiter().GetResult();
                var targetCatalog = registry.ReadAsync(targetGame).GetAwaiter().GetResult();
                TestAssert.True(sourceCatalog.IsAvailable && targetCatalog.IsAvailable,
                    "LE1/LE2 texture databases are needed to verify direct NPC material transfer.");
                var assets = StandalonePlayerAssetCatalog.ForRon(
                    targetGame, ronPath, targetCatalog.Candidates);

                using var imported = new StandaloneNpcMorphImportService().ImportNpcRon(
                    new StandaloneNpcMorphImportRequest(
                        sourceGame,
                        targetGame,
                        "ASA",
                        ronPath,
                        donorPath,
                        donor.InstancedPath,
                        "MFE_Npc_CrossGame_ASA",
                        assets,
                        IsVerifiedNativeNpcDonor: true,
                        SourceTextureCatalog: sourceCatalog.Candidates,
                        TargetTextureCatalog: targetCatalog.Candidates));
                AssertMorphEqual(expectedMorph,
                    context.CaptureMorphData(imported.Workspace.WorkingPath, imported.ImportedFacePath));
                var importedMaterial = context.CaptureMaterialData(
                    imported.Workspace.WorkingPath, imported.ImportedFacePath);
                TestAssert.Equal(expectedMaterial.Scalars.Count, importedMaterial.Scalars.Count);
                TestAssert.Equal(expectedMaterial.Vectors.Count, importedMaterial.Vectors.Count);
                TestAssert.Equal(expectedMaterial.Textures.Count, importedMaterial.Textures.Count);
                TestAssert.Equal(sourceFingerprint, PackageFingerprint.Capture(sourcePath));
                TestAssert.Equal(donorFingerprint, PackageFingerprint.Capture(donorPath));
            }
            finally
            {
                if (File.Exists(ronPath)) File.Delete(ronPath);
            }
        }));
    }

    private static void StandaloneNpcRonRejectsLe3CrossGame()
    {
        WithPackageCopy("LE2 GlobalMorphs.pcc", donorPath =>
        {
            var context = new MorphFacePackageContextService();
            var donor = ReadFaces(donorPath).First(face =>
                face.ProfileKey.Equals("le2-human-male", StringComparison.OrdinalIgnoreCase));
            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-StandaloneNpc-Le3-{Guid.NewGuid():N}.ron");
            try
            {
                context.ExportRon(donorPath, donor.InstancedPath, ronPath);
                var rejected = false;
                try
                {
                    _ = new StandaloneNpcMorphImportService().ImportNpcRon(
                        new StandaloneNpcMorphImportRequest(
                            MorphFaceGame.LE3,
                            MorphFaceGame.LE2,
                            "HMM",
                            ronPath,
                            donorPath,
                            donor.InstancedPath,
                            "MFE_Invalid_Npc"));
                }
                catch (InvalidDataException exception)
                {
                    rejected = exception.Message.Contains("LE3", StringComparison.OrdinalIgnoreCase) &&
                               exception.Message.Contains("cross-game", StringComparison.OrdinalIgnoreCase);
                }

                TestAssert.True(rejected,
                    "The NPC service accepted an LE3 cross-game route instead of leaving it to explicit conversion.");
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
            }
        });
    }

    private static void StandalonePlayerPskImport()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cooked = LegendaryExplorerCoreRuntime.DefaultLe1CookedPath;
        var seedPath = cooked is null ? null : Path.Combine(cooked, "EntryMenu.pcc");
        if (seedPath is null || !File.Exists(seedPath))
        {
            return;
        }

        const string templatePath = "BIOG_MORPH_FACE.Player_Base_Male";
        var meshPath = Path.Combine(Path.GetTempPath(), $"MFE-StandalonePlayer-{Guid.NewGuid():N}.psk");
        var before = PackageFingerprint.Capture(seedPath);
        try
        {
            using var sourceReader = new MorphFacePackageReader();
            var source = sourceReader.Load(seedPath, templatePath);
            WriteRecognisablePsk(meshPath, source.BaseHead);
            var sculpt = PSK.FromFile(meshPath);
            sculpt.Points[0] += new Vector3(0.25f, 0, 0);
            sculpt.ToFile(meshPath);
            var expectedPositions = source.BaseHead.Positions.ToArray();
            expectedPositions[0] += new Vector3(0.25f, 0, 0);

            var service = new StandalonePlayerMeshImportService();
            using var imported = service.Import(
                MorphFaceGame.LE1,
                meshPath,
                "Ryan_PSK_Morph");
            TestAssert.True(!imported.CanCommit && !imported.Workspace.CanCommit,
                "A standalone player mesh exposed a commit-capable workspace.");
            TestAssert.Equal(StandalonePlayerSex.Male, imported.Recognition.Sex);
            TestAssert.Equal("BIOG_MORPH_FACE.Ryan_PSK_Morph", imported.ImportedFacePath);

            var context = new MorphFacePackageContextService();
            var authored = context.CaptureMorphData(
                imported.Workspace.WorkingPath,
                imported.ImportedFacePath);
            TestAssert.Equal(0, authored.MorphFeatures.Count);
            TestAssert.Equal(1, authored.BakedLods.Count);
            TestAssert.True(authored.BakedLods[0].SequenceEqual(expectedPositions),
                "The recognised off-target PSK sculpt did not survive as the authoritative LOD0 bake.");
            TestAssert.Equal(source.BaseHead.Topology.ReferenceSkeleton.Count, authored.FinalSkeleton.Count);

            var sceneFactory = new HeadPreviewSceneFactory();
            var profiles = MorphFaceProfileRegistry.CreateDefault();
            using var previewLoader = new MorphFacePreviewLoadService(
                sceneFactory,
                new MorphTargetCatalog(),
                profiles,
                new MorphFacePackageReader());
            var preview = previewLoader.LoadAsync(
                    imported.Workspace.WorkingPath,
                    imported.ImportedFacePath,
                    geometryMode: MorphFaceGeometryMode.FixedBake)
                .GetAwaiter().GetResult();
            TestAssert.True(!preview.EditingSession.CanEditMorphFeatures,
                "A recognised PSK enabled morph sliders without a reconstruction proof.");
            TestAssert.True(preview.EditingSession.CanEditBones,
                "The verified canonical rig did not enable fixed-bake bone editing.");
            TestAssert.True(preview.EditingSession.AvailableLodIndices.SequenceEqual([0]),
                "A LOD0-only mesh import exposed unauthored template lower LODs.");
            TestAssert.True(preview.EditingSession.CreateDraft(
                        preview.Loaded.Document.HairMeshReference,
                        preview.Loaded.Document.OtherMeshReferences,
                        preview.Loaded.Document.MaterialOverrides).BakedLods[0]
                    .SequenceEqual(expectedPositions),
                "Fixed-bake draft creation changed the imported pre-skin vertices.");
            TestAssert.Equal(before, PackageFingerprint.Capture(seedPath));
        }
        finally
        {
            if (File.Exists(meshPath)) File.Delete(meshPath);
        }
    }

    private static void WriteRecognisablePsk(string path, SkeletalMeshAsset mesh)
    {
        var lod = mesh.FindLod(0) ?? throw new Exception("Player base has no LOD0 render data.");
        var materialCount = Math.Max(
            lod.Topology.MaterialCount,
            lod.Topology.Sections.Select(section => section.MaterialIndex + 1).DefaultIfEmpty(0).Max());
        var vertexMaterials = new byte[lod.Positions.Length];
        foreach (var section in lod.Topology.Sections)
        {
            for (var index = section.BaseIndex;
                 index < section.BaseIndex + section.TriangleCount * 3;
                 index++)
            {
                vertexMaterials[lod.RenderData.Indices[index]] = checked((byte)section.MaterialIndex);
            }
        }

        var faces = new List<PSK.PSKTriangle>();
        foreach (var section in lod.Topology.Sections)
        {
            for (var triangle = 0; triangle < section.TriangleCount; triangle++)
            {
                var index = section.BaseIndex + triangle * 3;
                var a = lod.RenderData.Indices[index];
                var b = lod.RenderData.Indices[index + 1];
                var c = lod.RenderData.Indices[index + 2];
                faces.Add(new PSK.PSKTriangle
                {
                    // LEC's PSK writer swaps the first two corners; the detached
                    // decoder restores handedness and reverses the triangle once.
                    WedgeIdx0 = checked((ushort)b),
                    WedgeIdx1 = checked((ushort)a),
                    WedgeIdx2 = checked((ushort)c),
                    MatIndex = checked((byte)section.MaterialIndex)
                });
            }
        }

        new PSK
        {
            Points = lod.Positions.Select(value => new Vector3(value.X, -value.Y, value.Z)).ToList(),
            Wedges = lod.RenderData.TextureCoordinates.Select((uv, index) => new PSK.PSKWedge
            {
                PointIndex = checked((ushort)index),
                U = uv.X,
                V = uv.Y,
                MatIndex = vertexMaterials[index]
            }).ToList(),
            Faces = faces,
            Materials = Enumerable.Range(0, materialCount)
                .Select(index => new PSK.PSKMaterial { Name = $"Material_{index}" })
                .ToList(),
            Bones = [],
            Weights = [],
            VertexNormals = []
        }.ToFile(path);
    }

    private static void StandalonePlayerGltfImport()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cooked = LegendaryExplorerCoreRuntime.DefaultLe1CookedPath;
        var seedPath = cooked is null ? null : Path.Combine(cooked, "EntryMenu.pcc");
        var umodelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LegendaryExplorer", "staticexecutables", "umodel", "umodel.exe");
        if (seedPath is null || !File.Exists(seedPath) || !File.Exists(umodelPath))
        {
            return;
        }

        const string templatePath = "BIOG_MORPH_FACE.Player_Base_Male";
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"MFE-PlayerGltf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var before = PackageFingerprint.Capture(seedPath);
        try
        {
            var interchange = new MorphFaceInterchangeService();
            var export = interchange.ExportMeshAsync(
                    seedPath,
                    templatePath,
                    outputDirectory,
                    MorphMeshFormat.Gltf)
                .GetAwaiter().GetResult();
            var gltfPath = export.ProducedFiles.Single(path =>
                Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase));
            var expected = new MorphFacePackageContextService().CaptureMorphData(seedPath, templatePath);

            using var imported = new StandalonePlayerMeshImportService(interchange).Import(
                MorphFaceGame.LE1,
                gltfPath,
                "Ryan_GLTF_Morph");
            var authored = new MorphFacePackageContextService().CaptureMorphData(
                imported.Workspace.WorkingPath,
                imported.ImportedFacePath);
            TestAssert.Equal(StandalonePlayerSex.Male, imported.Recognition.Sex);
            TestAssert.Equal(1, authored.BakedLods.Count);
            var maximumError = authored.BakedLods[0]
                .Zip(expected.BakedLods[0], Vector3.Distance)
                .Max();
            TestAssert.True(maximumError <= 0.0001f,
                $"The app-exported glTF vertex-map sidecar missed its authoritative LOD0 by {maximumError:G9}.");
            TestAssert.True(imported.Recognition.CoordinateSystem.EndsWith(
                    "MFE vertex map", StringComparison.Ordinal),
                "The glTF import ignored its exact vertex-map sidecar.");
            TestAssert.Equal(before, PackageFingerprint.Capture(seedPath));
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static void StandaloneRonMeshRoundTripPreservesPose()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cooked = LegendaryExplorerCoreRuntime.DefaultLe1CookedPath;
        var seedPath = cooked is null ? null : Path.Combine(cooked, "EntryMenu.pcc");
        var umodelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LegendaryExplorer", "staticexecutables", "umodel", "umodel.exe");
        if (seedPath is null || !File.Exists(seedPath) || !File.Exists(umodelPath))
        {
            return;
        }

        const string templatePath = "BIOG_MORPH_FACE.Player_Base_Male";
        var context = new MorphFacePackageContextService();
        var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-StandalonePose-{Guid.NewGuid():N}.ron");
        var before = PackageFingerprint.Capture(seedPath);
        try
        {
            context.ExportRon(seedPath, templatePath, ronPath);
            var ron = TseHeadMorphRon.Read(ronPath);
            var baked = ron.MorphData.BakedLods.Select(lod => lod.ToArray()).ToArray();
            baked[0][0] += new Vector3(0.125f, -0.0625f, 0.25f);
            var bones = ron.MorphData.FinalSkeleton.ToArray();
            TestAssert.True(bones.Length > 0,
                "The installed player template did not expose a final skeleton for the pose round trip.");
            bones[0] = bones[0] with
            {
                Translation = bones[0].Translation + new Vector3(0.2f, 0.1f, -0.15f)
            };
            TseHeadMorphRon.Write(ronPath, ron with
            {
                MorphData = ron.MorphData with
                {
                    FinalSkeleton = bones,
                    BakedLods = baked
                }
            });

            using var source = new StandalonePlayerMorphImportService().ImportPlayerRon(
                MorphFaceGame.LE1, ronPath, "Ryan_Pose_Source");
            var expected = context.CaptureMorphData(
                source.Workspace.WorkingPath, source.ImportedFacePath);
            foreach (var format in new[] { MorphMeshFormat.Psk, MorphMeshFormat.Gltf })
            {
                var outputDirectory = Path.Combine(
                    Path.GetTempPath(), $"MFE-StandalonePose-{format}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(outputDirectory);
                try
                {
                    var interchange = new MorphFaceInterchangeService();
                    var export = interchange.ExportMeshAsync(
                            source.Workspace.WorkingPath,
                            source.ImportedFacePath,
                            outputDirectory,
                            format)
                        .GetAwaiter().GetResult();
                    var meshPath = export.ProducedFiles.Single(path =>
                        format == MorphMeshFormat.Psk
                            ? Path.GetExtension(path).Equals(".psk", StringComparison.OrdinalIgnoreCase) ||
                              Path.GetExtension(path).Equals(".pskx", StringComparison.OrdinalIgnoreCase)
                            : Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase));
                    using var roundTripped = new StandalonePlayerMeshImportService(interchange).Import(
                        MorphFaceGame.LE1, meshPath, $"Ryan_Pose_{format}");
                    var actual = context.CaptureMorphData(
                        roundTripped.Workspace.WorkingPath,
                        roundTripped.ImportedFacePath);
                    TestAssert.Equal(1, actual.BakedLods.Count);
                    var maximumError = actual.BakedLods[0]
                        .Zip(expected.BakedLods[0], Vector3.Distance)
                        .Max();
                    TestAssert.True(maximumError <= 0.0001f,
                        $"The {format} round trip changed the source RON's baked LOD0 by {maximumError:G9}.");
                    TestAssert.Equal(expected.FinalSkeleton.Count, actual.FinalSkeleton.Count);
                    for (var bone = 0; bone < expected.FinalSkeleton.Count; bone++)
                    {
                        TestAssert.Equal(expected.FinalSkeleton[bone].BoneName, actual.FinalSkeleton[bone].BoneName);
                        TestAssert.True(Vector3.Distance(
                                expected.FinalSkeleton[bone].Translation,
                                actual.FinalSkeleton[bone].Translation) <= 0.0001f,
                            $"The {format} round trip changed final skeleton bone {bone}.");
                    }
                }
                finally
                {
                    if (Directory.Exists(outputDirectory))
                    {
                        Directory.Delete(outputDirectory, recursive: true);
                    }
                }
            }
            TestAssert.Equal(before, PackageFingerprint.Capture(seedPath));
        }
        finally
        {
            if (File.Exists(ronPath))
            {
                File.Delete(ronPath);
            }
        }
    }

    private static void StandalonePlayerRonInstalledMatrix()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cases = new[]
        {
            (MorphFaceGame.LE1, StandalonePlayerSex.Male, "BIOG_MORPH_FACE.Player_Base_Male"),
            (MorphFaceGame.LE1, StandalonePlayerSex.Female, "BIOG_MORPH_FACE.Player_Base_Female"),
            (MorphFaceGame.LE2, StandalonePlayerSex.Male, "BIOG_MORPH_FACE.CharacterCreation_Base_Male"),
            (MorphFaceGame.LE2, StandalonePlayerSex.Female, "BIOG_MORPH_FACE.CharacterCreation_Base_Female"),
            (MorphFaceGame.LE3, StandalonePlayerSex.Male, "biog_morph_face.CharacterCreation_Base_Male"),
            (MorphFaceGame.LE3, StandalonePlayerSex.Female, "biog_morph_face.CharacterCreation_Base_Female")
        };
        var importService = new StandalonePlayerMorphImportService();
        var context = new MorphFacePackageContextService();
        var registry = new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));

        foreach (var (game, sex, templatePath) in cases)
        {
            var cooked = LegendaryExplorerCoreRuntime.GetCookedPath(game);
            var seedName = game == MorphFaceGame.LE1 ? "EntryMenu.pcc" : "BioP_Char.pcc";
            var seedPath = cooked is null ? null : Path.Combine(cooked, seedName);
            if (seedPath is null || !File.Exists(seedPath))
            {
                continue;
            }

            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-{game}-{sex}-{Guid.NewGuid():N}.ron");
            var readbackPath = Path.Combine(Path.GetTempPath(), $"MFE-{game}-{sex}-{Guid.NewGuid():N}-readback.ron");
            var before = PackageFingerprint.Capture(seedPath);
            try
            {
                context.ExportRon(seedPath, templatePath, ronPath);
                var expected = TseHeadMorphRon.Read(ronPath);
                TestAssert.True(expected.MorphData.BakedLods.Count > 1,
                    $"{game} {sex} player source did not contain lower LODs.");
                if (game == MorphFaceGame.LE2 && sex == StandalonePlayerSex.Male)
                {
                    TestAssert.Equal(2192, expected.MorphData.BakedLods[2].Length);
                }

                var textureCatalog = registry.ReadAsync(game).GetAwaiter().GetResult();
                var assetCatalog = StandalonePlayerAssetCatalog.ForRon(
                    game,
                    ronPath,
                    textureCatalog.Candidates);
                using var imported = importService.ImportPlayerRon(
                    game,
                    ronPath,
                    $"MFE_{game}_{sex}_Matrix",
                    assetCatalog);

                TestAssert.Equal(game, imported.Game);
                TestAssert.Equal(sex, imported.Sex);
                TestAssert.True(!imported.CanCommit && !imported.Workspace.CanCommit,
                    $"{game} {sex} matrix import exposed a commit-capable workspace.");
                AssertMorphEqual(
                    expected.MorphData,
                    context.CaptureMorphData(imported.Workspace.WorkingPath, imported.ImportedFacePath));
                AssertMaterialEqual(
                    expected.MaterialData,
                    context.CaptureMaterialData(imported.Workspace.WorkingPath, imported.ImportedFacePath));

                context.ExportRon(imported.Workspace.WorkingPath, imported.ImportedFacePath, readbackPath);
                var readback = TseHeadMorphRon.Read(readbackPath);
                AssertMorphEqual(expected.MorphData, readback.MorphData);
                AssertMaterialEqual(expected.MaterialData, readback.MaterialData);
                TestAssert.Equal(expected.HairMesh, readback.HairMesh);
                TestAssert.True(expected.AccessoryMeshes.SequenceEqual(
                        readback.AccessoryMeshes,
                        StringComparer.OrdinalIgnoreCase),
                    $"{game} {sex} accessory references changed during RON readback.");
                TestAssert.Equal(before, PackageFingerprint.Capture(seedPath));
            }
            finally
            {
                if (File.Exists(ronPath)) File.Delete(ronPath);
                if (File.Exists(readbackPath)) File.Delete(readbackPath);
            }
        }
    }

    private static void GibbedHeadMorphsImport()
    {
        WithPackageCopy("LE2 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            var source = ReadFaces(path).First(face =>
                face.ProfileKey.Equals("le2-human-female", StringComparison.OrdinalIgnoreCase));
            var expectedMorph = service.CaptureMorphData(path, source.InstancedPath);
            var expectedMaterial = service.CaptureMaterialData(path, source.InstancedPath);
            var legacyMorph = expectedMorph with { BakedLods = [expectedMorph.BakedLods[0]] };

            using var reader = new MorphFacePackageReader();
            var loaded = reader.Load(path, source.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var profile = MorphFaceProfileRegistry.CreateDefault().Require(
                loaded.Game,
                loaded.Document.Source.InstancedPath,
                loaded.Document.BaseHeadReference?.InstancedPath);

            foreach (var game in new[] { 2, 3 })
            {
                var extension = $".me{game}headmorph";
                var headMorphPath = Path.Combine(Path.GetTempPath(), $"MFE-{Guid.NewGuid():N}{extension}");
                try
                {
                    WriteGibbedHeadMorph(
                        headMorphPath,
                        game,
                        legacyMorph,
                        expectedMaterial with
                        {
                            Textures = expectedMaterial.Textures.Count == 0
                                ? expectedMaterial.Textures
                                : expectedMaterial.Textures
                                    .Select((value, index) => index == 0
                                        ? value with
                                        {
                                            TextureReference = new AssetIdentity(
                                                string.Empty,
                                                "BIOG_MFE_Missing_Donor.MissingTexture",
                                                0,
                                                "Texture2D")
                                        }
                                        : value)
                                    .ToArray()
                        },
                        formatVersion: game == 2 ? 1 : 0);
                    var legacy = service.ReadLegacyHeadMorph(headMorphPath);
                    TestAssert.Equal(1, legacy.MorphData.BakedLods.Count);

                    var session = new MorphFaceEditingSession(
                        loaded.Document,
                        loaded.BaseHead,
                        new MorphTargetCatalog().Load(profile, loaded.Game),
                        profile.MetadataOnlyFeatures,
                        profile.DisplayName,
                        profile.FeatureAliases,
                        profile.RecognizesBaseVariant,
                        profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath));
                    session.ApplyMorphData(legacy.MorphData);
                    var draft = session.CreateDraft(
                        loaded.Document.HairMeshReference,
                        loaded.Document.OtherMeshReferences,
                        loaded.Document.MaterialOverrides);
                    var converted = new MorphFaceMorphData(
                        draft.MorphFeatures,
                        draft.FinalSkeleton,
                        draft.BakedLods);
                    TestAssert.Equal(expectedMorph.BakedLods.Count, converted.BakedLods.Count);
                    TestAssert.True(converted.BakedLods.Select(value => value.Length)
                            .SequenceEqual(expectedMorph.BakedLods.Select(value => value.Length)),
                        "Legacy conversion did not rebuild the selected LE profile's LOD topology.");

                    var result = service.ImportConvertedLegacyHeadMorph(
                        path,
                        source.InstancedPath,
                        $"MFE_GibbedME{game}_Test",
                        headMorphPath,
                        converted);
                    TestAssert.True(result.Warnings.Any(value =>
                            value.Contains("MissingTexture", StringComparison.OrdinalIgnoreCase) &&
                            value.Contains("template", StringComparison.OrdinalIgnoreCase)),
                        "Legacy texture substitution did not produce an explicit warning.");
                    AssertMorphEqual(converted, service.CaptureMorphData(path, result.FaceInstancedPath));
                    AssertMaterialEqual(expectedMaterial, service.CaptureMaterialData(path, result.FaceInstancedPath));
                }
                finally
                {
                    if (File.Exists(headMorphPath))
                    {
                        File.Delete(headMorphPath);
                    }
                }
            }
        });
    }

    private static void StandaloneLegacyImportRejectsMismatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-Mismatch-{Guid.NewGuid():N}.me2headmorph");
        try
        {
            File.WriteAllBytes(path, [0]);
            var threw = false;
            try
            {
                _ = new StandaloneLegacyHeadMorphImportService().Import(
                    MorphFaceGame.LE3,
                    path,
                    "Mismatch");
            }
            catch (InvalidDataException exception)
            {
                threw = exception.Message.Contains("LE2", StringComparison.OrdinalIgnoreCase) &&
                        exception.Message.Contains("LE3", StringComparison.OrdinalIgnoreCase);
            }

            TestAssert.True(threw,
                "A .me2headmorph imported as LE3 without a clear destination-game mismatch error.");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void StandaloneLegacyImportsInstalledPlayers()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var (game, gameNumber, templatePath) in new[]
                 {
                     (MorphFaceGame.LE2, 2, "BIOG_MORPH_FACE.CharacterCreation_Base_Male"),
                     (MorphFaceGame.LE2, 2, "BIOG_MORPH_FACE.CharacterCreation_Base_Female"),
                     (MorphFaceGame.LE3, 3, "biog_morph_face.CharacterCreation_Base_Male"),
                     (MorphFaceGame.LE3, 3, "biog_morph_face.CharacterCreation_Base_Female")
                 })
        {
            var cooked = LegendaryExplorerCoreRuntime.GetCookedPath(game);
            var seedPath = cooked is null ? null : Path.Combine(cooked, "BioP_Char.pcc");
            if (seedPath is null || !File.Exists(seedPath))
            {
                continue;
            }

            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-LegacySource-{Guid.NewGuid():N}.ron");
            var legacyPath = Path.Combine(Path.GetTempPath(), $"MFE-LegacySource-{Guid.NewGuid():N}.me{gameNumber}headmorph");
            var before = PackageFingerprint.Capture(seedPath);
            try
            {
                var context = new MorphFacePackageContextService();
                context.ExportRon(seedPath, templatePath, ronPath);
                var ron = TseHeadMorphRon.Read(ronPath);
                var expectedMaterial = context.CaptureMaterialData(seedPath, templatePath);
                WriteGibbedHeadMorph(
                    legacyPath,
                    gameNumber,
                    ron.MorphData,
                    ron.MaterialData,
                    formatVersion: gameNumber == 2 ? 1 : 0);

                using var imported = new StandaloneLegacyHeadMorphImportService().Import(
                    game,
                    legacyPath,
                    $"Ryan_LE{gameNumber}_Legacy");
                TestAssert.True(!imported.CanCommit && !imported.Workspace.CanCommit,
                    $"LE{gameNumber} legacy import exposed a commit-capable workspace.");
                TestAssert.Equal(game, imported.Game);
                TestAssert.Equal(
                    templatePath.EndsWith("Female", StringComparison.OrdinalIgnoreCase)
                        ? StandalonePlayerSex.Female
                        : StandalonePlayerSex.Male,
                    imported.Sex);

                var converted = context.CaptureMorphData(
                    imported.Workspace.WorkingPath,
                    imported.ImportedFacePath);
                TestAssert.True(converted.BakedLods.Count > 1 &&
                                converted.BakedLods.All(lod => lod.Length > 0),
                    $"LE{gameNumber} legacy import did not rebuild native player LODs.");
                AssertMaterialEqual(
                    expectedMaterial,
                    context.CaptureMaterialData(imported.Workspace.WorkingPath, imported.ImportedFacePath));

                var failedAppend = false;
                try
                {
                    _ = new StandaloneLegacyHeadMorphImportService().ImportIntoWorkspace(
                        game == MorphFaceGame.LE2 ? MorphFaceGame.LE3 : MorphFaceGame.LE2,
                        legacyPath,
                        "WrongGameAppend",
                        imported.Workspace);
                }
                catch (InvalidDataException)
                {
                    failedAppend = true;
                }
                TestAssert.True(failedAppend && File.Exists(imported.Workspace.WorkingPath),
                    $"LE{gameNumber} failed legacy append disposed or lost the existing detached workspace.");
                TestAssert.Equal(before, PackageFingerprint.Capture(seedPath));
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
                if (File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                }
            }
        }
    }

    private static void WriteGibbedHeadMorph(
        string path,
        int game,
        MorphFaceMorphData morph,
        MorphFaceMaterialData material,
        int formatVersion = 0)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.Latin1);
        writer.Write(System.Text.Encoding.ASCII.GetBytes($"GIBBEDMASSEFFECT{game}HEADMORPH"));
        writer.Write((byte)formatVersion);
        if (formatVersion == 1)
        {
            writer.Write((byte)0); // little-endian marker used by the modified ME2 editor
        }
        writer.Write(0u);
        if (formatVersion == 1)
        {
            WriteUnrealString(writer, "743.EJM.A11.H8C.GGG.91W.A4P.786.NJ6.E92.9G6.576");
        }
        WriteUnrealString(writer, "None");
        writer.Write(0); // accessory meshes
        WriteMap(writer, morph.MorphFeatures, value => value.Name, value => writer.Write(value.Offset));
        WriteMap(writer, morph.FinalSkeleton, value => value.BoneName, value => WriteVector3(writer, value.Translation));
        for (var lod = 0; lod < 4; lod++)
        {
            var vertices = morph.BakedLods.ElementAtOrDefault(lod) ?? [];
            writer.Write(vertices.Count());
            foreach (var vertex in vertices)
            {
                WriteVector3(writer, vertex);
            }
        }
        WriteMap(writer, material.Scalars, value => value.Name, value => writer.Write(value.Value));
        WriteMap(writer, material.Vectors, value => value.Name, value =>
        {
            writer.Write(value.Value.X);
            writer.Write(value.Value.Y);
            writer.Write(value.Value.Z);
            writer.Write(value.Value.W);
        });
        WriteMap(writer, material.Textures, value => value.Name, value =>
            WriteUnrealString(writer, value.TextureReference?.InstancedPath ?? "None"));
    }

    private static void WriteMap<T>(
        BinaryWriter writer,
        IReadOnlyList<T> values,
        Func<T, string> key,
        Action<T> writeValue)
    {
        writer.Write(values.Count);
        foreach (var value in values)
        {
            WriteUnrealString(writer, key(value));
            writeValue(value);
        }
    }

    private static void WriteUnrealString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(value);
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static void RealBakedMeshInverts()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var profiles = MorphFaceProfileRegistry.CreateDefault();
            using var reader = new MorphFacePackageReader();
            foreach (var face in ReadFaces(path))
            {
                var loaded = reader.Load(path, face.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var profile = profiles.Require(
                    loaded.Game,
                    loaded.Document.Source.InstancedPath,
                    loaded.Document.BaseHeadReference?.InstancedPath);
                var session = new MorphFaceEditingSession(
                    loaded.Document,
                    loaded.BaseHead,
                    new MorphTargetCatalog().Load(profile, loaded.Game),
                    profile.MetadataOnlyFeatures,
                    profile.DisplayName,
                    profile.FeatureAliases,
                    profile.RecognizesBaseVariant,
                    profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath));
                if (!session.CanEditMorphFeatures)
                {
                    continue;
                }

                var fit = session.FitMeshPositions(
                [
                    new MorphMeshPositionCandidate(
                        "BioMorphFace baked LOD0",
                        loaded.Document.BakedLods[0])
                ]);
                TestAssert.True(fit.MaximumError < 0.001f,
                    $"Exact baked mesh inversion missed by {fit.MaximumError:G6} on {face.InstancedPath}.");
                return;
            }
            throw new Exception("No editable LE1 face was available for inverse-mesh verification.");
        });
    }

    private static void MeshExportStagingIsGeometryOnly()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            var face = ReadFaces(path).First(candidate =>
                service.CaptureMorphData(path, candidate.InstancedPath).BakedLods.Count > 0);
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(), $"MFE-MeshStage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var stagingPath = MorphFaceInterchangeService.CreateAppliedMeshPackage(
                    path,
                    face.InstancedPath,
                    temporaryDirectory,
                    out var meshName,
                    out var bakedPositions,
                    out _);
                var inventory = PackageAssetInspector.Inventory(stagingPath);
                using var stagingPackage = MEPackageHandler.OpenMEPackage(
                    stagingPath, forceLoadFromDisk: true);
                var meshExport = stagingPackage.Exports.Single();
                var mesh = ObjectBinary.From<SkeletalMesh>(meshExport)
                           ?? throw new Exception("The staged SkeletalMesh binary could not be read.");
                var lodInfo = meshExport.GetProperty<ArrayProperty<StructProperty>>("LODInfo")
                              ?? throw new Exception("The staged SkeletalMesh has no LODInfo metadata.");

                TestAssert.Equal(1, inventory.Entries.Count);
                TestAssert.Equal("SkeletalMesh", inventory.Entries[0].ClassName);
                TestAssert.Equal(meshName, inventory.Entries[0].ObjectName);
                TestAssert.True(mesh.Materials.All(index => index == 0),
                    "The UModel staging mesh retained a material dependency.");
                TestAssert.Equal(mesh.LODModels.Length, lodInfo.Count);
                TestAssert.Equal(bakedPositions.Length,
                    mesh.LODModels[0].VertexBufferGPUSkin.VertexData.Length);
                TestAssert.True(bakedPositions.Length > 0,
                    "The UModel staging mesh had no baked geometry.");
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        });
    }

    private static (BioMorphFaceListItem Source, BioMorphFaceListItem Target) FindCompatiblePair(
        string path,
        MorphFacePackageContextService service)
    {
        foreach (var group in ReadFaces(path).GroupBy(face => face.ProfileKey, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group.ToArray();
            for (var left = 0; left < candidates.Length; left++)
            for (var right = left + 1; right < candidates.Length; right++)
            {
                var leftData = service.CaptureMorphData(path, candidates[left].InstancedPath);
                var rightData = service.CaptureMorphData(path, candidates[right].InstancedPath);
                if (leftData.BakedLods.Count == rightData.BakedLods.Count &&
                    leftData.BakedLods.Select(lod => lod.Length).SequenceEqual(rightData.BakedLods.Select(lod => lod.Length)))
                {
                    return (candidates[left], candidates[right]);
                }
            }
        }
        throw new Exception("The package contains no topology-compatible pair in a matching profile.");
    }

    private static IReadOnlyList<BioMorphFaceListItem> ReadFaces(string path) =>
        new MorphFaceCatalogService().ReadAsync(path).GetAwaiter().GetResult().Faces;

    private static void AssertMorphEqual(MorphFaceMorphData expected, MorphFaceMorphData actual)
    {
        TestAssert.Equal(expected.MorphFeatures.Count, actual.MorphFeatures.Count);
        TestAssert.Equal(expected.FinalSkeleton.Count, actual.FinalSkeleton.Count);
        TestAssert.Equal(expected.BakedLods.Count, actual.BakedLods.Count);
        for (var index = 0; index < expected.MorphFeatures.Count; index++)
        {
            TestAssert.Equal(expected.MorphFeatures[index], actual.MorphFeatures[index]);
        }
        for (var index = 0; index < expected.FinalSkeleton.Count; index++)
        {
            TestAssert.Equal(expected.FinalSkeleton[index], actual.FinalSkeleton[index]);
        }
        for (var lod = 0; lod < expected.BakedLods.Count; lod++)
        {
            TestAssert.Equal(expected.BakedLods[lod].Length, actual.BakedLods[lod].Length);
            for (var vertex = 0; vertex < expected.BakedLods[lod].Length; vertex++)
            {
                TestAssert.Near(expected.BakedLods[lod][vertex], actual.BakedLods[lod][vertex], 0.000001f);
            }
        }
    }

    private static void AssertMaterialEqual(MorphFaceMaterialData expected, MorphFaceMaterialData actual)
    {
        TestAssert.Equal(expected.Scalars.Count, actual.Scalars.Count);
        TestAssert.Equal(expected.Vectors.Count, actual.Vectors.Count);
        TestAssert.Equal(expected.Textures.Count, actual.Textures.Count);
        for (var index = 0; index < expected.Scalars.Count; index++)
        {
            TestAssert.Equal(expected.Scalars[index], actual.Scalars[index]);
        }
        for (var index = 0; index < expected.Vectors.Count; index++)
        {
            TestAssert.Equal(expected.Vectors[index], actual.Vectors[index]);
        }
        for (var index = 0; index < expected.Textures.Count; index++)
        {
            TestAssert.Equal(expected.Textures[index].Name, actual.Textures[index].Name);
            TestAssert.Equal(
                expected.Textures[index].TextureReference?.InstancedPath,
                actual.Textures[index].TextureReference?.InstancedPath);
        }
    }

    private static void WithPackageCopy(string fileName, Action<string> action)
    {
        var source = FixturePath(fileName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Package fixture '{fileName}' was not found.", source);
        }
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"MFE-{Guid.NewGuid():N}-{fileName}");
        File.Copy(source, temporaryPath, overwrite: false);
        try
        {
            action(temporaryPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
