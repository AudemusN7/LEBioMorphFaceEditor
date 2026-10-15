using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using static MorphFaceEditor.LegendaryExplorer.MorphFacePackageCodec;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed record MorphFaceSaveResult(
    string PackagePath,
    string FaceInstancedPath,
    string MaterialOverrideInstancedPath,
    int LodCount,
    int TextureReferenceCount)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<UserFacingMessage> UserWarnings { get; init; } = [];
}

/// <summary>
/// Writes BioMorphFace data through verified temporary package copies. Both
/// dependency-port exports and existing-export updates use atomic install.
/// </summary>
public sealed class MorphFacePackageWriter
{
    public MorphFaceSaveResult SaveMorphToPackage(
        MorphFaceDocument draft,
        string logicalSourcePackagePath,
        string destinationPath,
        bool createNewPackage,
        string? destinationObjectName = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalSourcePackagePath);
        draft = draft with { MaterialOverrides = HeadMorphMaterialParameterPolicy.ForHeadMorph(draft.MaterialOverrides) };
        LegendaryExplorerCoreRuntime.Initialize();
        var sourcePath = Path.GetFullPath(draft.Source.PackagePath);
        var logicalSourcePath = Path.GetFullPath(logicalSourcePackagePath);
        var destination = Path.GetFullPath(destinationPath);
        if (destinationObjectName is not null)
        {
            ValidateObjectName(destinationObjectName);
        }
        var destinationFacePath = destinationObjectName is null
            ? draft.Source.InstancedPath
            : ReplaceObjectName(draft.Source.InstancedPath, destinationObjectName);
        ValidateMorphPackageDestination(draft, sourcePath, logicalSourcePath, destination, createNewPackage);
        var destinationFingerprint = createNewPackage ? null : PackageFingerprint.Capture(destination);
        IReadOnlySet<string>? destinationIntegrityBaseline = null;
        if (!createNewPackage)
        {
            using var existingDestination = MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
            destinationIntegrityBaseline = PackageIntegrity.CaptureIssues(existingDestination);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var save = new FileSaveTransaction(destination, destinationFingerprint))
        {
            var temporaryPath = save.CandidatePath;
            string facePath;
            string overridePath;
            var warnings = new List<string>();
            PccDependencyGraphSnapshot dependencyGraph;
            var sourceBytes = File.ReadAllBytes(sourcePath);
            using (var sourceStream = new MemoryStream(sourceBytes, writable: false))
            using (var sourcePackage = MEPackageHandler.OpenMEPackageFromStream(sourceStream, logicalSourcePath))
            {
                EnsureSupportedGame(sourcePackage);
                var sourceFace = FindExport(sourcePackage, draft.Source.InstancedPath, "BioMorphFace");
                VerifyUnchangedNonFiniteValues(draft, sourceFace);
                draft = FilterSupportedAttachments(draft, sourcePackage.Game, warnings);
                if (draft.ScalpMaterialSelection is not null)
                {
                    // Materialize the selected graph on the source package's
                    // in-memory snapshot before importing the face. This keeps
                    // the original head graph out of a new PCC, where it would
                    // otherwise be imported first and then left orphaned after
                    // replacing the face's head reference.
                    draft = ApplyScalpMaterialSelection(sourcePackage, sourceFace, draft, warnings);
                    WriteFace(sourcePackage, sourceFace, draft, warnings);
                }
                using var explicitTextureCache = new PackageCache { CacheMaxSize = 8 };
                var requestedTextures = draft.MaterialOverrides.Textures;
                draft = draft with { MaterialOverrides = PccPackageWorkflow.OmitUnavailableSelectedTextures(
                    draft.MaterialOverrides, explicitTextureCache, warnings) };
                var omittedNames = requestedTextures.Except(draft.MaterialOverrides.Textures)
                    .Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (omittedNames.Count > 0)
                {
                    var sourceOverride = ResolveMaterialOverride(sourceFace);
                    var overrideProperties = sourceOverride.GetProperties();
                    var retained = overrideProperties.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides")?
                        .Where(value => !omittedNames.Contains(value.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty))
                        .ToArray() ?? [];
                    overrideProperties.AddOrReplaceProp(new ArrayProperty<StructProperty>(retained, "m_aTextureOverrides"));
                    sourceOverride.WriteProperties(overrideProperties);
                }
                // Keep the graph of retained attachments. Strip only outgoing
                // or unsupported references before the authored face is ported.
                var sourceProperties = sourceFace.GetProperties();
                var sourceHair = sourceProperties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(sourcePackage);
                if (draft.HairMeshReference is null || sourceHair is null ||
                    !sourceHair.InstancedFullPath.Equals(draft.HairMeshReference.InstancedPath, StringComparison.OrdinalIgnoreCase))
                    sourceProperties.RemoveNamedProperty("m_oHairMesh");
                var selectedOthers = draft.OtherMeshReferences.Where(identity => identity is not null)
                    .Select(identity => identity!.InstancedPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var retainedOthers = sourceProperties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
                    .Where(reference => reference.ResolveToEntry(sourcePackage) is { } entry &&
                        selectedOthers.Contains(entry.InstancedFullPath)).ToArray() ?? [];
                if (retainedOthers.Length == 0) sourceProperties.RemoveNamedProperty("m_oOtherMeshes");
                else sourceProperties.AddOrReplaceProp(new ArrayProperty<ObjectProperty>(retainedOthers, "m_oOtherMeshes"));
                sourceFace.WriteProperties(sourceProperties);
                using var destinationPackage = createNewPackage
                    ? MEPackageHandler.CreateMemoryEmptyPackage(destination, sourcePackage.Game)
                    : MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
                // The stage never reaches disk or AppData. Only its completed
                // root graph is cloned into the actual destination candidate.
                using var stagingStream = createNewPackage ? null : destinationPackage.SaveToStream(compress: false);
                if (stagingStream is not null) stagingStream.Position = 0;
                using var stagingPackage = stagingStream is null
                    ? MEPackageHandler.CreateMemoryEmptyPackage(destination, sourcePackage.Game)
                    : MEPackageHandler.OpenMEPackageFromStream(stagingStream, destination);
                EnsureSupportedGame(destinationPackage);
                if (destinationPackage.Game != sourcePackage.Game)
                {
                    throw new InvalidDataException(
                        $"The destination is {destinationPackage.Game}, but the selected morph is {sourcePackage.Game}.");
                }
                var occupyingEntry = destinationPackage.FindEntry(destinationFacePath);
                var existingFace = occupyingEntry as ExportEntry;
                if (occupyingEntry is not null &&
                    (existingFace is null || existingFace.ClassName != "BioMorphFace"))
                {
                    throw new InvalidOperationException(
                        $"The destination already contains an entry named '{destinationFacePath}'.");
                }

                var explicitTextureDonors = ResolveExplicitTextureDonors(draft, sourcePackage.Game, explicitTextureCache);
                var portedFace = PccPackageWorkflow.ImportRootPreservingStructure(
                    stagingPackage,
                    sourceFace,
                    warnings,
                    explicitTextureDonors: explicitTextureDonors,
                    replacementRoot: existingFace is null ? null
                        : stagingPackage.FindExport(existingFace.InstancedFullPath, "BioMorphFace"));
                if (destinationObjectName is not null)
                {
                    portedFace.ObjectName = new NameReference(destinationObjectName);
                }
                if (!string.Equals(portedFace.InstancedFullPath, destinationFacePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"The exported morph path changed from '{destinationFacePath}' to '{portedFace.InstancedFullPath}'.");
                }

                if (draft.ScalpMaterialSelection is not null)
                {
                    var importedHead = portedFace.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(stagingPackage)
                        ?? throw new InvalidDataException("The selected scalp graph did not import with the face.");
                    draft = draft with
                    {
                        BaseHeadReference = new AssetIdentity(
                            stagingPackage.FilePath,
                            importedHead is ExportEntry importedHeadExport
                                ? PccAssetPathPolicy.FromExport(importedHeadExport)
                                : importedHead.InstancedFullPath,
                            importedHead.UIndex,
                            importedHead.ClassName)
                    };
                }
                WriteFace(stagingPackage, portedFace, draft, warnings);
                var materialOverride = ResolveMaterialOverride(portedFace);
                WriteMaterialOverride(stagingPackage, materialOverride, draft.MaterialOverrides, warnings);
                portedFace = PccPackageWorkflow.CloneCompletedRoot(destinationPackage, portedFace, warnings, existingFace);
                materialOverride = ResolveMaterialOverride(portedFace);
                facePath = portedFace.InstancedFullPath;
                overridePath = materialOverride.InstancedFullPath;
                dependencyGraph = PccPackageWorkflow.CaptureDependencyGraph(portedFace);
                destinationPackage.Save(temporaryPath);
            }

            Verify(temporaryPath, facePath, draft, destinationIntegrityBaseline, dependencyGraph, requireMinimal: createNewPackage);
            FileSaveTransaction.VerifyUnchanged(sourcePath, draft.SourceFingerprint,
                "The source PCC changed while the morph package was being written. Nothing was replaced.");
            save.Commit("The destination changed while the morph package was being written. Nothing was replaced.");
            return new MorphFaceSaveResult(
                destination,
                facePath,
                overridePath,
                draft.BakedLods.Count,
                draft.MaterialOverrides.Textures.Count)
            {
                Warnings = warnings
            };
        }
    }

    /// <summary>
    /// Updates the loaded BioMorphFace and its existing BioMaterialOverride in
    /// an adjacent verified copy, then atomically replaces the open PCC.
    /// </summary>
    public MorphFaceSaveResult SaveExisting(MorphFaceDocument draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft = draft with { MaterialOverrides = HeadMorphMaterialParameterPolicy.ForHeadMorph(draft.MaterialOverrides) };
        LegendaryExplorerCoreRuntime.Initialize();
        var packagePath = Path.GetFullPath(draft.Source.PackagePath);
        ValidateDraft(draft, packagePath);
        IReadOnlySet<string> integrityBaseline;
        using (var original = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true))
        {
            integrityBaseline = PackageIntegrity.CaptureIssues(original);
            VerifyUnchangedNonFiniteValues(draft, FindExport(original, draft.Source.InstancedPath, "BioMorphFace"));
        }
        using (var save = new FileSaveTransaction(packagePath, draft.SourceFingerprint))
        {
            var temporaryPath = save.CandidatePath;
            File.Copy(packagePath, temporaryPath, overwrite: false);
            string overridePath;
            var warnings = new List<string>();
            PccDependencyGraphSnapshot? dependencyGraph = null;
            using (var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true))
            {
                EnsureSupportedGame(package);
                var face = FindExport(package, draft.Source.InstancedPath, "BioMorphFace");
                draft = FilterSupportedAttachments(draft, package.Game, warnings);
                using var explicitTextureCache = new PackageCache { CacheMaxSize = 8 };
                draft = draft with { MaterialOverrides = PccPackageWorkflow.OmitUnavailableSelectedTextures(
                    draft.MaterialOverrides, explicitTextureCache, warnings) };
                var explicitDonors = ResolveExplicitTextureDonors(draft, package.Game, explicitTextureCache)
                    .Where(donor => !Path.GetFullPath(donor.FileRef.FilePath).Equals(packagePath, StringComparison.OrdinalIgnoreCase) ||
                        package.FindExport(PccAssetPathPolicy.FromExport(donor), "Texture2D")?.UIndex != donor.UIndex)
                    .ToArray();
                PccPackageWorkflow.StageExplicitTextureDonors(package, face, explicitDonors);
                draft = ApplyScalpMaterialSelection(package, face, draft, warnings);
                WriteFace(package, face, draft, warnings);
                var materialOverride = ResolveMaterialOverride(face);
                WriteMaterialOverride(package, materialOverride, draft.MaterialOverrides, warnings);
                overridePath = materialOverride.InstancedFullPath;
                if (draft.ScalpMaterialSelection is not null || explicitDonors.Length > 0)
                    dependencyGraph = PccPackageWorkflow.CaptureDependencyGraph(face);
                package.Save(temporaryPath);
            }

            Verify(temporaryPath, draft.Source.InstancedPath, draft, integrityBaseline, dependencyGraph);
            save.Commit("The open PCC changed while the face was being written. Nothing was replaced.");
            return new MorphFaceSaveResult(
                packagePath,
                draft.Source.InstancedPath,
                overridePath,
                draft.BakedLods.Count,
                draft.MaterialOverrides.Textures.Count)
            {
                Warnings = warnings
            };
        }
    }

    private static void WriteFace(
        IMEPackage package,
        ExportEntry face,
        MorphFaceDocument draft,
        ICollection<string> warnings)
    {
        var properties = face.GetProperties();
        properties.AddOrReplaceProp(new ObjectProperty(
            ResolveDraftEntry(package, draft.BaseHeadReference, "SkeletalMesh", required: true),
            "m_oBaseHead"));
        WriteAttachmentReferences(package, properties, draft, warnings);

        WriteMorphData(face, new MorphFaceMorphData(
            draft.MorphFeatures, draft.FinalSkeleton, draft.BakedLods), properties);
    }

    private static MorphFaceDocument ApplyScalpMaterialSelection(
        IMEPackage package,
        ExportEntry face,
        MorphFaceDocument draft,
        ICollection<string> warnings)
    {
        if (draft.ScalpMaterialSelection is not { } selection) return draft;
        if (package.Game != MEGame.LE3)
            throw new InvalidDataException(
                "The selected LE3 CC scalp graph cannot be saved into a different game's package. Select that game's scalp material instead.");

        var clone = ScalpMaterialGraphMaterializer.Apply(
            package, face, draft.BaseHeadReference ?? throw new InvalidDataException(
                "A base-head reference is required to save the selected CC scalp material graph."),
            selection, warnings);
        return draft with
        {
            BaseHeadReference = new AssetIdentity(
                package.FilePath,
                PccAssetPathPolicy.FromExport(clone),
                clone.UIndex,
                clone.ClassName)
        };
    }

    private static void WriteMaterialOverride(
        IMEPackage package,
        ExportEntry materialOverride,
        MorphFaceMaterialOverrides overrides,
        ICollection<string> warnings) =>
        WriteMaterialData(materialOverride,
            new MorphFaceMaterialData(overrides.Scalars, overrides.Vectors, overrides.Textures),
            identity => ResolveTextureDraftEntry(package, identity, warnings));

    private static void Verify(
        string packagePath,
        string facePath,
        MorphFaceDocument expected,
        IReadOnlySet<string>? allowedExistingIntegrityIssues = null,
        PccDependencyGraphSnapshot? dependencyGraph = null,
        bool requireMinimal = false)
    {
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        PackageIntegrity.Verify(package, allowedExistingIntegrityIssues);
        if (dependencyGraph is not null)
        {
            PccPackageWorkflow.VerifyDependencyGraph(package, dependencyGraph, verifyCorpusRoles: false);
        }
        var face = FindExport(package, facePath, "BioMorphFace");
        if (requireMinimal) PccPackageWorkflow.VerifyNoUnreachableEntries(face);
        var properties = face.GetProperties();
        VerifyMorphData(ReadMorphData(face, properties), new MorphFaceMorphData(
            expected.MorphFeatures, expected.FinalSkeleton, expected.BakedLods));

        var baseHead = properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package);
        VerifyReference(baseHead, expected.BaseHeadReference, "base head");
        VerifyScalpMaterialSelection(baseHead, expected.ScalpMaterialSelection);
        VerifyHairReference(package, properties, expected);

        var materialOverride = ResolveMaterialOverride(face);
        var materialProperties = materialOverride.GetProperties();
        VerifyWrittenValueFields(properties, materialProperties);
        VerifyMaterialData(ReadMaterialData(materialOverride, materialProperties), new MorphFaceMaterialData(
            expected.MaterialOverrides.Scalars, expected.MaterialOverrides.Vectors, expected.MaterialOverrides.Textures));
    }

    private static void VerifyScalpMaterialSelection(
        IEntry? baseHeadEntry,
        ScalpMaterialSelection? selection)
    {
        if (selection is null) return;
        if (baseHeadEntry is not ExportEntry baseHead ||
            !baseHead.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected CC scalp graph did not reopen through a local head-mesh export.");
        if (baseHead.Parent is null)
            throw new InvalidDataException("The saved CC scalp head lost its native ancestry.");

        var expectedPath = selection.DonorMaterial.InstancedPath;
        var matches = (baseHead.GetBinaryData<LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh>().Materials ?? [])
            .Select(index => baseHead.FileRef.GetEntry(index))
            .Where(entry => entry is not null)
            .Count(entry => (entry is ExportEntry export
                    ? PccAssetPathPolicy.FromExport(export)
                    : entry!.InstancedFullPath)
                .Equals(expectedPath, StringComparison.OrdinalIgnoreCase));
        if (matches != 1)
            throw new InvalidDataException(
                $"The saved CC scalp head contains {matches} slots for exact donor '{expectedPath}'; exactly one is required.");
        using var cache = new PackageCache();
        var resolver = new GamePackageReferenceResolver(cache);
        var donorEntry = PackageIntegrity.FindExactEntry(
            baseHead.FileRef, expectedPath, selection.DonorMaterial.ClassName)
            ?? throw new InvalidDataException($"The saved CC scalp graph is missing exact donor '{expectedPath}'.");
        var donor = resolver.Resolve(donorEntry)
            ?? throw new InvalidDataException($"The saved CC scalp donor '{expectedPath}' cannot be reopened.");
        ScalpMaterialGraphMaterializer.EnsureCanonicalCcScalpGraph(donor, cache);
    }

    private static IReadOnlyList<ExportEntry> ResolveExplicitTextureDonors(
        MorphFaceDocument draft, MEGame game, PackageCache cache) =>
        PccPackageWorkflow.ResolveExplicitTextureDonors(
            draft.MaterialOverrides.Textures.Select(value => value.TextureReference), game, cache);

    private static MorphFaceDocument FilterSupportedAttachments(
        MorphFaceDocument draft, MEGame game, ICollection<string> warnings)
    {
        using var cache = new PackageCache { CacheMaxSize = 8 };
        var resolver = new GamePackageReferenceResolver(cache);
        AssetIdentity? Filter(AssetIdentity? identity)
        {
            if (identity is null) return null;
            if (AttachmentMeshNamePolicy.IsExcludedNativeAttachment(identity.InstancedPath))
            {
                warnings.Add($"Optional attachment '{identity.InstancedPath}' was omitted because it is a reviewed excluded native mesh.");
                return null;
            }
            if (!File.Exists(identity.PackagePath))
            {
                warnings.Add($"Optional attachment '{identity.InstancedPath}' was omitted because '{identity.PackagePath}' is unavailable.");
                return null;
            }
            // Native seekfree packages can omit the BIOG package prefix from
            // disk paths. Use the same canonical identity lookup as the
            // materialiser before deciding that an attachment is missing.
            IEntry? entry = InstalledGameAssetResolver.TryExact(cache, identity.PackagePath,
                identity.InstancedPath, "SkeletalMesh", identity.UIndex);
            if (entry is null && identity.IsImport)
                entry = cache.GetCachedPackage(identity.PackagePath)?.FindEntry(identity.InstancedPath, "SkeletalMesh");
            var mesh = resolver.Resolve(entry);
            if (mesh is null)
            {
                warnings.Add($"Optional attachment '{identity.InstancedPath}' was omitted because its mesh could not be found.");
                return null;
            }
            if (MorphFaceAttachmentTransferEngine.HasSupportedMaterials(mesh.FileRef, mesh, out var unsupported))
                return identity;
            warnings.Add($"Optional attachment '{identity.InstancedPath}' was omitted because {unsupported} is outside MFE's supported canonical materials.");
            return null;
        }
        return draft with
        {
            HairMeshReference = Filter(draft.HairMeshReference),
            OtherMeshReferences = draft.OtherMeshReferences.Select(Filter).Where(identity => identity is not null).ToArray()
        };
    }

    private static void WriteAttachmentReferences(
        IMEPackage package,
        PropertyCollection properties,
        MorphFaceDocument draft,
        ICollection<string> warnings)
    {
        if (draft.HairMeshReference is null)
        {
            properties.RemoveNamedProperty("m_oHairMesh");
        }
        else
        {
            properties.AddOrReplaceProp(new ObjectProperty(
                ResolveAttachmentDraftEntry(package, draft.HairMeshReference, warnings),
                "m_oHairMesh"));
        }

        var otherMeshes = draft.OtherMeshReferences
            .Where(reference => reference is not null)
            .Select(reference => new ObjectProperty(
                ResolveAttachmentDraftEntry(package, reference, warnings)))
            .ToArray();
        if (otherMeshes.Length == 0)
        {
            properties.RemoveNamedProperty("m_oOtherMeshes");
        }
        else
        {
            properties.AddOrReplaceProp(new ArrayProperty<ObjectProperty>(otherMeshes, "m_oOtherMeshes"));
        }
    }

    private static void VerifyHairReference(
        IMEPackage package,
        PropertyCollection properties,
        MorphFaceDocument expected)
    {
        var hair = properties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package);
        VerifyReference(hair, expected.HairMeshReference, "m_oHairMesh");
        var otherMeshes = properties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?.ToArray() ?? [];
        var expectedOthers = expected.OtherMeshReferences.Where(reference => reference is not null).ToArray();
        if (otherMeshes.Length != expectedOthers.Length)
        {
            throw new InvalidDataException(
                $"m_oOtherMeshes count is {otherMeshes.Length}; expected {expectedOthers.Length}.");
        }
        for (var index = 0; index < expectedOthers.Length; index++)
        {
            VerifyReference(
                otherMeshes[index].ResolveToEntry(package),
                expectedOthers[index],
                $"m_oOtherMeshes[{index}]");
        }
    }

    private static ExportEntry ResolveMaterialOverride(ExportEntry face) =>
        face.GetProperty<ObjectProperty>("m_oMaterialOverrides")?.ResolveToEntry(face.FileRef) as ExportEntry
        ?? throw new InvalidDataException($"BioMorphFace '{face.InstancedFullPath}' has no local BioMaterialOverride.");

    private static IEntry ResolveDraftEntry(
        IMEPackage package,
        AssetIdentity? identity,
        string expectedClass,
        bool required)
    {
        if (identity is null)
        {
            return required
                ? throw new InvalidDataException($"A {expectedClass} reference is required.")
                : null!;
        }
        var entry = package.FindEntry(identity.InstancedPath, expectedClass);
        return entry ?? throw new InvalidDataException(
            $"Referenced {expectedClass} '{identity.InstancedPath}' is not inside the destination PCC.");
    }

    private static IEntry ResolveTextureDraftEntry(
        IMEPackage package,
        AssetIdentity? identity,
        ICollection<string> warnings)
    {
        if (identity is null)
        {
            return null!;
        }
        // A texture reference can lose its provenance bit while crossing the
        // RON/material preview boundary. The destination package is still the
        // authority: if the exact canonical path is already an import, retain
        // that authored dependency regardless of identity.IsImport. Attempting
        // to materialise an export beside it creates a duplicate Texture2D
        // identity and PackageIntegrity correctly rejects the graph.
        if (PackageIntegrity.FindExactEntry(package, identity.InstancedPath, "Texture2D") is ImportEntry preservedImport)
        {
            return preservedImport;
        }
        return PackageIntegrity.FindExactEntry(package, identity.InstancedPath, "Texture2D") as ExportEntry
               ?? PccPackageWorkflow.ResolveTextureReference(
                   package,
                   identity,
                   textureCatalog: null,
                   preferBiogTextures: false,
                   warnings: warnings);
    }

    private static IEntry ResolveAttachmentDraftEntry(
        IMEPackage package,
        AssetIdentity? identity,
        ICollection<string> warnings)
    {
        if (identity is null)
        {
            throw new InvalidDataException("A SkeletalMesh reference is required.");
        }
        if (package.FindEntry(identity.InstancedPath, "SkeletalMesh") is ImportEntry)
        {
            throw new InvalidDataException(
                $"Referenced imported SkeletalMesh '{identity.InstancedPath}' has no materialised mesh dependency graph; " +
                "the PCC export was rejected to avoid writing an unresolved attachment.");
        }
        if (package.FindEntry(identity.InstancedPath, "SkeletalMesh") is { } existing)
        {
            return existing;
        }
        if (identity.IsImport)
        {
            throw new InvalidDataException(
                $"Referenced imported SkeletalMesh '{identity.InstancedPath}' was not preserved in the destination PCC.");
        }
        return ExternalSkeletalMeshMaterializer.Materialize(package, identity, warnings);
    }

    private static ExportEntry FindExport(IMEPackage package, string path, string expectedClass)
    {
        var export = package.FindExport(path, expectedClass);
        return export ?? throw new InvalidDataException($"{expectedClass} '{path}' was not found in the package.");
    }

    private static void ValidateMorphPackageDestination(
        MorphFaceDocument draft,
        string sourcePath,
        string logicalSourcePath,
        string destination,
        bool createNewPackage)
    {
        ValidateDraft(draft, sourcePath);
        if (string.Equals(sourcePath, destination, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(logicalSourcePath, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The open source PCC cannot also be the morph export destination.");
        }
        if (createNewPackage && File.Exists(destination))
        {
            throw new IOException("The new morph package destination already exists.");
        }
        if (!createNewPackage && !File.Exists(destination))
        {
            throw new FileNotFoundException("The existing destination PCC no longer exists.", destination);
        }
    }

    private static void ValidateDraft(MorphFaceDocument draft, string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source PCC no longer exists.", sourcePath);
        }
        if (PackageFingerprint.Capture(sourcePath) != draft.SourceFingerprint)
        {
            throw new IOException("The source PCC has changed since this face was loaded. Reload it before saving.");
        }
        if (draft.BakedLods.Count == 0 || draft.BakedLods.Any(lod => lod.Length == 0))
        {
            throw new InvalidDataException("At least one non-empty baked LOD is required.");
        }
        if (draft.MorphFeatures.Any(value => string.IsNullOrWhiteSpace(value.Name)) ||
            draft.MorphFeatures.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != draft.MorphFeatures.Count)
        {
            throw new InvalidDataException("Morph features must have unique names and finite values.");
        }
        if (draft.FinalSkeleton.Any(value => string.IsNullOrWhiteSpace(value.BoneName)) ||
            draft.FinalSkeleton.Select(value => value.BoneName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != draft.FinalSkeleton.Count)
        {
            throw new InvalidDataException("Final skeleton bones must have unique names and finite translations.");
        }
    }

    private static void EnsureSupportedGame(IMEPackage package)
    {
        if (package.Game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3))
        {
            throw new InvalidDataException($"BioMorphFace writing supports LE1, LE2, and LE3 packages, not {package.Game}.");
        }
    }

    private static void ValidateObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName) ||
            !(char.IsLetter(objectName[0]) || objectName[0] == '_') ||
            objectName.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                "The destination export name may contain letters, digits, and underscores, and cannot begin with a digit.",
                nameof(objectName));
        }
    }

    private static string ReplaceObjectName(string instancedPath, string objectName)
    {
        var separator = instancedPath.LastIndexOf('.');
        return separator < 0 ? objectName : $"{instancedPath[..separator]}.{objectName}";
    }

    private static void VerifyReference(IEntry? actual, AssetIdentity? expected, string label)
    {
        if (actual is null && expected is null)
        {
            return;
        }
        if (actual is null || expected is null ||
            !string.Equals(actual.InstancedFullPath, expected.InstancedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {label} reference changed during package round-trip verification.");
        }
    }
}
