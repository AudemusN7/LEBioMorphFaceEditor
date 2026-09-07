using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
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
        new("LE1 morph export creates and extends a dependency-only PCC without changing paths", ExportLe1MorphPackage),
        new("external hair and attachment meshes port their material chains on morph export", ExternalAttachmentsPortOnMorphExport),
        new("morph package export preserves paths across LE1, LE2, and LE3", ExportMorphPackagesAcrossGames),
        new("cross-game conversion rebakes LE2 to LE3 and LE3 to LE1 packages", ConvertMorphsAcrossGameBoundary),
        new("cross-game conversion requires current texture databases", ConversionRequiresTextureDatabases),
        new("all six cross-game directions preserve canonical hair donors", PreserveCanonicalHairAcrossAllDirections),
        new("reviewed cross-game texture policy aliases donors and omits inert overrides", ApplyReviewedTextureTransferPolicy),
        new("reviewed cross-game texture paths are admitted to the local registry", ReviewedTexturePathsAreRegistryEligible),
        new("later-game alien textures embed package-stored when LE1 has no stock equivalent", EmbedMissingAlienTextureIntoLe1),
        new("LE2 context morph paste round-trips matching-profile topology", PasteLe2MorphData),
        new("LE3 context material paste round-trips every override kind", PasteLe3MaterialData),
        new("bundled human and Asari eyes recover both fixed reflection cubes", HumanAndAsariEyeCubesResolve),
        new("TSE RON export and import round-trip a real BioMorphFace", RonRoundTripsMorph),
        new("legacy Gibbed ME2 and ME3 head morphs import through the context pipeline", GibbedHeadMorphsImport),
        new("broken attachment materials fall back without blocking face authoring", BrokenAttachmentMaterialFallsBack),
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
            ("LE3", MorphFaceGame.LE1, "HMF.cat004_cerb_scientist1_face", "BIOG_HMF_HIR_PRO.PonyTail.Mom.HMF_HIR_Mom_MDL"),
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
            TestAssert.True(embedded.All(entry => entry.Parent is ExportEntry),
                "At least one embedded alien texture was parented beneath an import.");
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
            targets.Load(profile, loaded.Game, packagePath),
            profile.MetadataOnlyFeatures,
            profile.DisplayName,
            profile.FeatureAliases,
            profile.RecognizesBaseVariant,
            profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath));
        TestAssert.True(session.CanEdit, session.EditBlockReason ?? "Converted face was not editable.");
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
                        expectedMaterial,
                        formatVersion: game == 2 ? 1 : 0);
                    var legacy = service.ReadLegacyHeadMorph(headMorphPath);
                    TestAssert.Equal(1, legacy.MorphData.BakedLods.Count);

                    var session = new MorphFaceEditingSession(
                        loaded.Document,
                        loaded.BaseHead,
                        new MorphTargetCatalog().Load(profile, loaded.Game, path),
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
                    new MorphTargetCatalog().Load(profile, loaded.Game, path),
                    profile.MetadataOnlyFeatures,
                    profile.DisplayName,
                    profile.FeatureAliases,
                    profile.RecognizesBaseVariant,
                    profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath));
                if (!session.CanEdit)
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
