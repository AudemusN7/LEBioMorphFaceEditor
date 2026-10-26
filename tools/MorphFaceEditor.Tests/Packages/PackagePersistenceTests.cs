using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LecTexture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;

using static MorphFaceEditor.Tests.PackageTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class PackagePersistenceTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE1 context clone and loaded save preserve authored fields and independent child ownership", CloneLe1Morph),
        new("safe-save rejects outside edits during loaded save clone and delete", SaveConflictsPreserveOutsideEdits),
        new("context clone preserves unrelated pre-existing package damage", ClonePreservesExistingDamage),
        new("safe-save rejects a competing new morph export and cleans its candidate", NewExportConflictPreservesDestination),
        new("safe-save workspace conflicts and locked destinations retain pending edits for retry", WorkspaceSaveFailureRetainsEdits),
        new("saved value checks preserve original nonfinite PCC fields and reject newly invalid edits", NonFiniteValuesRoundTrip),
        new("morph deletion detects incoming references without modifying the PCC", MorphDeleteReferences),
        new("morph re-export replaces only its face and override and imports missing dependencies", ReexportMorphPair),
    ];

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
            using (var package = MEPackageHandler.OpenMEPackage(workspace.WorkingPath, forceLoadFromDisk: true))
            {
                var face = package.FindExport(source.InstancedPath, "BioMorphFace")!;
                var features = face.GetProperty<ArrayProperty<StructProperty>>("m_aMorphFeatures")!;
                features.Add(new StructProperty("MorphFeature", false,
                    new NameProperty("MFE_UnknownZero", "sFeatureName"), new FloatProperty(0, "Offset")));
                face.WriteProperty(features);
                face.WriteProperty(new StrProperty("Unowned face data", "MFE_PreservedField"));
                var material = (ExportEntry)face.GetProperty<ObjectProperty>("m_oMaterialOverrides")!.ResolveToEntry(package);
                material.WriteProperty(new StrProperty("Unowned material data", "MFE_PreservedField"));
                package.Save(workspace.WorkingPath);
            }
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

            using (var reader = new MorphFacePackageReader())
            {
                var loaded = reader.Load(workspace.WorkingPath, result.FaceInstancedPath, decodeTextures: false);
                TestAssert.Equal(0f, loaded.Document.GetFeatureOffset("MFE_UnknownZero"));
                new MorphFacePackageWriter().SaveExisting(loaded.Document);
            }
            AssertMorphEqual(sourceMorph, service.CaptureMorphData(workspace.WorkingPath, result.FaceInstancedPath));
            using (var reopened = MEPackageHandler.OpenMEPackage(workspace.WorkingPath, forceLoadFromDisk: true))
            {
                TestAssert.Equal("Unowned face data", reopened.FindExport(result.FaceInstancedPath, "BioMorphFace")!
                    .GetProperty<StrProperty>("MFE_PreservedField")!.Value);
                TestAssert.Equal("Unowned material data", reopened.FindExport(result.MaterialOverrideInstancedPath, "BioMaterialOverride")!
                    .GetProperty<StrProperty>("MFE_PreservedField")!.Value);
            }
            TestAssert.Equal(sourceFingerprint, PackageFingerprint.Capture(path));
            TestAssert.True(ReadFaces(path).All(face => face.ObjectName != "MFE_ContextClone_Test"),
                "The context clone changed the source PCC before an explicit workspace commit.");

            workspace.Commit();

            TestAssert.True(ReadFaces(path).Any(face => face.ObjectName == "MFE_ContextClone_Test"),
                "The committed workspace did not install the cloned face in the source PCC.");
        });
    }

    private static void ClonePreservesExistingDamage()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var facePath = ReadFaces(path).First().InstancedPath;
            using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
            {
                var unrelated = package.CreateExport("MFE_PreExistingDamage", "Object", indexed: false);
                unrelated.WriteProperty(new ObjectProperty(999999, "BrokenReference"));
                package.Save();
            }
            IReadOnlySet<string> baseline;
            using (var original = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
                baseline = PackageIntegrity.CaptureIssues(original);
            TestAssert.True(baseline.Count > 0, "The source did not contain the intended existing damage.");

            var result = new MorphFacePackageContextService().CloneMorph(path, facePath, "MFE_BaselineClone");
            using var saved = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            PackageIntegrity.Verify(saved, baseline);
            TestAssert.True(saved.FindExport(result.FaceInstancedPath, "BioMorphFace") is not null,
                "Unrelated existing damage prevented the requested clone.");
            TestAssert.Equal(999999, saved.FindExport("MFE_PreExistingDamage", "Object")!
                .GetProperty<ObjectProperty>("BrokenReference")!.Value);
        });
    }

    private static void SaveConflictsPreserveOutsideEdits()
    {
        foreach (var operation in new[] { "save", "clone", "delete" })
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var facePath = ReadFaces(path).First().InstancedPath;
            using var reader = new MorphFacePackageReader();
            var draft = reader.Load(path, facePath, decodeTextures: false).Document;
            PackageFingerprint? outsideEdit = null;
            string? candidate = null;
            FileSaveTransaction.BeforeCommitForTesting = (temporaryPath, destination) =>
            {
                candidate = temporaryPath;
                using (var external = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true))
                {
                    external.FindExport(facePath, "BioMorphFace")!
                        .WriteProperty(new StrProperty("Outside edit", "MFE_ExternalEdit"));
                    external.Save();
                }
                outsideEdit = PackageFingerprint.Capture(destination);
            };
            try
            {
                ExpectSaveFailure(() =>
                {
                    if (operation == "save") new MorphFacePackageWriter().SaveExisting(draft);
                    else if (operation == "clone") new MorphFacePackageContextService().CloneMorph(path, facePath, "MFE_ConflictClone");
                    else new MorphFacePackageContextService().DeleteMorph(path, facePath);
                });
                TestAssert.True(outsideEdit is not null, "The operation failed before reaching publication.");
                TestAssert.Equal(outsideEdit, PackageFingerprint.Capture(path));
                TestAssert.True(candidate is not null && !File.Exists(candidate), "A rejected save left its candidate behind.");
                using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
                TestAssert.Equal("Outside edit", reopened.FindExport(facePath, "BioMorphFace")!
                    .GetProperty<StrProperty>("MFE_ExternalEdit")!.Value);
                TestAssert.True(reopened.Exports.All(value => value.ObjectNameString != "MFE_ConflictClone"),
                    "A rejected clone was published.");
            }
            finally { FileSaveTransaction.BeforeCommitForTesting = null; }
        });
    }

    private static void NewExportConflictPreservesDestination()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var destination = path + ".export.pcc";
            var original = PackageFingerprint.Capture(path);
            var facePath = ReadFaces(path).First().InstancedPath;
            using var reader = new MorphFacePackageReader();
            var draft = reader.Load(path, facePath, decodeTextures: false).Document;
            PackageFingerprint? competingFile = null;
            string? candidate = null;
            FileSaveTransaction.BeforeCommitForTesting = (temporaryPath, target) =>
            {
                candidate = temporaryPath;
                File.Copy(path, target);
                competingFile = PackageFingerprint.Capture(target);
            };
            try
            {
                ExpectSaveFailure(() => new MorphFacePackageWriter().SaveMorphToPackage(
                    draft, path, destination, createNewPackage: true));
                TestAssert.True(competingFile is not null, "The export failed before reaching publication.");
                TestAssert.Equal(competingFile, PackageFingerprint.Capture(destination));
                TestAssert.Equal(original, PackageFingerprint.Capture(path));
                TestAssert.True(candidate is not null && !File.Exists(candidate), "A rejected export left its candidate behind.");
            }
            finally
            {
                FileSaveTransaction.BeforeCommitForTesting = null;
                File.Delete(destination);
            }
        });
    }

    private static void WorkspaceSaveFailureRetainsEdits()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            using var workspace = new MorphFacePackageWorkspace(path);
            var context = new MorphFacePackageContextService();
            var facePath = ReadFaces(workspace.WorkingPath).First().InstancedPath;
            var clone = context.CloneMorph(workspace.WorkingPath, facePath, "MFE_PendingClone");
            var pending = PackageFingerprint.Capture(workspace.WorkingPath);
            var source = PackageFingerprint.Capture(path);
            var destination = path + ".save-as.pcc";
            string? candidate = null;
            FileSaveTransaction.BeforeCommitForTesting = (temporaryPath, target) =>
            {
                candidate = temporaryPath;
                File.Copy(path, target);
            };
            try
            {
                ExpectSaveFailure(() => workspace.Commit(destination));
                TestAssert.Equal(path, workspace.SourcePath);
                TestAssert.Equal(source, PackageFingerprint.Capture(path));
                TestAssert.Equal(pending, PackageFingerprint.Capture(workspace.WorkingPath));
                TestAssert.True(candidate is not null && !File.Exists(candidate), "A conflicted Save As left its candidate behind.");

                FileSaveTransaction.BeforeCommitForTesting = (temporaryPath, _) => candidate = temporaryPath;
                var competing = PackageFingerprint.Capture(destination);
                using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ExpectSaveFailure(() => workspace.Commit(destination));
                TestAssert.Equal(competing, PackageFingerprint.Capture(destination));
                TestAssert.Equal(path, workspace.SourcePath);
                TestAssert.Equal(pending, PackageFingerprint.Capture(workspace.WorkingPath));
                TestAssert.True(candidate is not null && !File.Exists(candidate), "A locked Save As left its candidate behind.");

                FileSaveTransaction.BeforeCommitForTesting = null;
                workspace.Commit(destination);
                workspace.Commit();
                TestAssert.Equal(destination, workspace.SourcePath);
                TestAssert.Equal(source, PackageFingerprint.Capture(path));
                using var saved = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
                TestAssert.True(saved.FindExport(clone.FaceInstancedPath, "BioMorphFace") is not null,
                    "Retrying Save As lost the pending clone.");
                TestAssert.True(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!,
                    Path.GetFileName(destination) + ".*.backup").Any(), "A completed Save As left a backup behind.");
            }
            finally
            {
                FileSaveTransaction.BeforeCommitForTesting = null;
                File.Delete(destination);
            }
        });
    }

    private static void ExpectSaveFailure(Action save)
    {
        try { save(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("The conflicting or blocked save unexpectedly succeeded.");
    }

    private static void NonFiniteValuesRoundTrip()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var facePath = ReadFaces(path).First().InstancedPath;
            MorphFaceDocument draft;
            using (var reader = new MorphFacePackageReader()) draft = reader.Load(path, facePath, decodeTextures: false).Document;
            var nan = BitConverter.Int32BitsToSingle(0x7fc12345);
            var features = draft.MorphFeatures.Append(new MorphFeatureValue("MFE_NaN", nan)).ToArray();
            var bones = draft.FinalSkeleton.Append(new BoneTranslation("MFE_InvalidBone",
                new Vector3(float.PositiveInfinity, 0, nan))).ToArray();
            var lods = draft.BakedLods.Select(lod => lod.ToArray()).ToArray();
            lods[^1][0] = new Vector3(nan, float.NegativeInfinity, 0);
            var materials = draft.MaterialOverrides with
            {
                Scalars = draft.MaterialOverrides.Scalars.Append(new ScalarMaterialOverride("MFE_NaN", nan)).ToArray(),
                Vectors = draft.MaterialOverrides.Vectors.Append(new VectorMaterialOverride("MFE_InvalidColour",
                    new Vector4(1, float.NegativeInfinity, nan, 0))).ToArray()
            };
            using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
            {
                var face = package.FindExport(facePath, "BioMorphFace")!;
                var storedFeatures = face.GetProperty<ArrayProperty<StructProperty>>("m_aMorphFeatures")
                    ?? new ArrayProperty<StructProperty>([], "m_aMorphFeatures");
                storedFeatures.Add(new StructProperty("MorphFeature", false,
                    new NameProperty("MFE_NaN", "sFeatureName"), new FloatProperty(nan, "Offset")));
                face.WriteProperty(storedFeatures);
                var storedBones = face.GetProperty<ArrayProperty<StructProperty>>("m_aFinalSkeleton")
                    ?? new ArrayProperty<StructProperty>([], "m_aFinalSkeleton");
                storedBones.Add(new StructProperty("OffsetBonePos", false,
                    new NameProperty("MFE_InvalidBone", "nName"),
                    new StructProperty("Vector", [new FloatProperty(float.PositiveInfinity, "X"),
                        new FloatProperty(0, "Y"), new FloatProperty(nan, "Z")], "vPos", true)));
                face.WriteProperty(storedBones);
                face.WriteBinary(new BioMorphFace { LODs = lods });
                var material = (ExportEntry)face.GetProperty<ObjectProperty>("m_oMaterialOverrides")!.ResolveToEntry(package);
                var scalars = material.GetProperty<ArrayProperty<StructProperty>>("m_aScalarOverrides")
                    ?? new ArrayProperty<StructProperty>([], "m_aScalarOverrides");
                scalars.Add(new StructProperty("ScalarParameter", false,
                    new NameProperty("MFE_NaN", "nName"), new FloatProperty(nan, "sValue")));
                material.WriteProperty(scalars);
                var vectors = material.GetProperty<ArrayProperty<StructProperty>>("m_aColorOverrides")
                    ?? new ArrayProperty<StructProperty>([], "m_aColorOverrides");
                vectors.Add(new StructProperty("ColorParameter", false,
                    new NameProperty("MFE_InvalidColour", "nName"),
                    new StructProperty("LinearColor", [new FloatProperty(1, "R"),
                        new FloatProperty(float.NegativeInfinity, "G"), new FloatProperty(nan, "B"),
                        new FloatProperty(0, "A")], "cValue", true)));
                material.WriteProperty(vectors);
                package.Save(path);
            }
            draft = draft with { SourceFingerprint = PackageFingerprint.Capture(path), MorphFeatures = features,
                FinalSkeleton = bones, BakedLods = lods, MaterialOverrides = materials };
            var writer = new MorphFacePackageWriter();
            writer.SaveExisting(draft);
            var service = new MorphFacePackageContextService();
            var clone = service.CloneMorph(path, facePath, "MFE_NonFiniteClone");
            AssertBits(path, clone.FaceInstancedPath);
            draft = draft with { SourceFingerprint = PackageFingerprint.Capture(path) };
            var destination = Path.Combine(Path.GetTempPath(), $"MFE-NonFinite-{Guid.NewGuid():N}.pcc");
            try
            {
                var exported = writer.SaveMorphToPackage(draft, path, destination, createNewPackage: true);
                AssertBits(destination, exported.FaceInstancedPath);
                var destinationFingerprint = PackageFingerprint.Capture(destination);
                try
                {
                    writer.SaveMorphToPackage(draft with
                    {
                        MorphFeatures = features.Append(new MorphFeatureValue("MFE_NewInvalid", nan)).ToArray()
                    }, path, destination, createNewPackage: false);
                    throw new Exception("A newly invalid re-export replaced the destination.");
                }
                catch (InvalidDataException exception)
                {
                    TestAssert.Equal("loc_PccSave_NewNonFiniteValue", UserFacingMessage.FromException(exception)?.LocalizationKey);
                }
                TestAssert.Equal(destinationFingerprint, PackageFingerprint.Capture(destination));
            }
            finally { File.Delete(destination); }

            var changedLods = lods.Select(lod => lod.ToArray()).ToArray();
            changedLods[0][0] = new Vector3(float.PositiveInfinity, 0, 0);
            foreach (var changed in new[]
                     {
                         draft with { MorphFeatures = features.Append(new("MFE_NewInvalid", nan)).ToArray() },
                         draft with { FinalSkeleton = bones.Select(value => value.BoneName == "MFE_InvalidBone"
                             ? value with { Translation = new Vector3(float.NegativeInfinity, 0, nan) } : value).ToArray() },
                         draft with { BakedLods = changedLods },
                         draft with { MaterialOverrides = materials with { Scalars = materials.Scalars.Select(value =>
                             value.Name == "MFE_NaN" ? value with { Value = float.NaN } : value).ToArray() } },
                         draft with { MaterialOverrides = materials with { Vectors = materials.Vectors.Append(
                             new VectorMaterialOverride("MFE_NewInvalid", new Vector4(nan))).ToArray() } }
                     })
            {
                try { writer.SaveExisting(changed); throw new Exception("A newly invalid edit was saved."); }
                catch (InvalidDataException exception)
                {
                    TestAssert.Equal("loc_PccSave_NewNonFiniteValue", UserFacingMessage.FromException(exception)?.LocalizationKey);
                }
                TestAssert.Equal(draft.SourceFingerprint, PackageFingerprint.Capture(path));
            }
            writer.SaveExisting(draft with
            {
                MorphFeatures = features.Select(value => value.Name == "MFE_NaN" ? value with { Offset = 0 } : value).ToArray()
            });
            TestAssert.Equal(0f, service.CaptureMorphData(path, facePath).MorphFeatures.Last().Offset);

            void AssertBits(string packagePath, string selectedFace)
            {
                var morph = service.CaptureMorphData(packagePath, selectedFace);
                var material = service.CaptureMaterialData(packagePath, selectedFace);
                TestAssert.Equal(0x7fc12345, BitConverter.SingleToInt32Bits(morph.MorphFeatures.Last().Offset));
                TestAssert.Equal(0x7fc12345, BitConverter.SingleToInt32Bits(morph.FinalSkeleton.Last().Translation.Z));
                TestAssert.Equal(float.PositiveInfinity, morph.FinalSkeleton.Last().Translation.X);
                TestAssert.Equal(0x7fc12345, BitConverter.SingleToInt32Bits(morph.BakedLods[^1][0].X));
                TestAssert.Equal(float.NegativeInfinity, morph.BakedLods[^1][0].Y);
                TestAssert.Equal(0x7fc12345, BitConverter.SingleToInt32Bits(material.Scalars.Last().Value));
                TestAssert.Equal(0x7fc12345, BitConverter.SingleToInt32Bits(material.Vectors.Last().Value.Z));
                TestAssert.Equal(float.NegativeInfinity, material.Vectors.Last().Value.Y);
            }
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

}
