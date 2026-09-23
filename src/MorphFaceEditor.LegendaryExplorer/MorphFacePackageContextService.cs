using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using BinaryMorphFace = LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed record TransferredMaterialSaveResult(
    MorphFaceSaveResult SaveResult,
    MorphFaceMaterialData MaterialData);

/// <summary>
/// Performs the export-browser operations that do not require a live editing
/// session: exact data capture, transactional paste, and in-package cloning.
/// </summary>
public sealed class MorphFacePackageContextService
{
    private const float FloatTolerance = 0.000001f;

    private sealed record NpcRonMaterialTransfer(
        MorphFaceGame SourceGame,
        string SourceProfileKey,
        string TargetTemplatePackagePath,
        IReadOnlyList<TextureCatalogCandidate> SourceTextureCatalog,
        IReadOnlyList<TextureCatalogCandidate> TargetTextureCatalog);

    public MorphFaceMorphData CaptureMorphData(string packagePath, string facePath)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = OpenPackage(packagePath);
        return ReadMorphData(FindFace(package, facePath));
    }

    public MorphFaceMaterialData CaptureMaterialData(string packagePath, string facePath)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = OpenPackage(packagePath);
        return ReadMaterialData(ResolveMaterialOverride(FindFace(package, facePath)));
    }

    public TransferredMaterialSaveResult CreateConvertedMorphPackage(
        string destinationPath,
        MorphFaceDocument draft,
        string objectName,
        string targetTemplatePackagePath,
        string targetTemplateFacePath,
        MorphFaceMaterialData sourceMaterialData,
        IReadOnlySet<string> supportedScalars,
        IReadOnlySet<string> supportedVectors,
        IReadOnlySet<string> supportedTextures,
        MorphFaceGame sourceGame,
        string sourceProfileKey,
        string sourcePackagePath,
        AssetIdentity? sourceHair,
        IReadOnlyList<AssetIdentity?> sourceOtherMeshes,
        IReadOnlyList<TextureCatalogCandidate> sourceTextureCatalog,
        IReadOnlyList<TextureCatalogCandidate> targetTextureCatalog)
    {
        ValidateObjectName(objectName);
        ValidateMaterialData(sourceMaterialData);
        var destination = Path.GetFullPath(destinationPath);
        var templatePath = Path.GetFullPath(targetTemplatePackagePath);
        if (File.Exists(destination))
        {
            throw new IOException("The new conversion destination already exists.");
        }
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("The target template PCC was not found.", templatePath);
        }
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException("The conversion destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.conversion.tmp.pcc");
        try
        {
            TextureTransferResult transfer;
            AttachmentTransferResult attachments;
            IReadOnlyList<string> stagingWarnings;
            string facePath;
            string materialPath;
            PccDependencyGraphSnapshot dependencyGraph;
            using (var template = OpenPackage(templatePath))
            using (var package = MEPackageHandler.CreateMemoryEmptyPackage(temporaryPath, template.Game))
            {
                var templateFace = FindFace(template, targetTemplateFacePath);
                NormalizeInvalidHmmMorphPackageAliases(template);
                var stagingMessages = new List<string>();
                var face = PccPackageWorkflow.ImportRootPreservingStructure(
                    package,
                    templateFace,
                    stagingMessages,
                    checkImportsWhenExportingToPackage: false);
                stagingWarnings = stagingMessages;
                EnsureNoExportParentsAreImports(package, "target-template staging");
                face.ObjectName = new NameReference(objectName);
                var materialOverride = ResolveMaterialOverride(face);

                // Stage the authored material and attachment assets first. The base-head
                // dependency walk can then bind to those exports instead of pre-creating
                // fragile imports for the same canonical paths.
                transfer = MorphFaceTextureTransferEngine.Transfer(
                    package,
                    sourceMaterialData,
                    supportedScalars,
                    supportedVectors,
                    supportedTextures,
                    ToMeGame(sourceGame),
                    sourceProfileKey,
                    sourcePackagePath,
                    templatePath,
                    sourceTextureCatalog,
                    targetTextureCatalog);
                EnsureNoExportParentsAreImports(package, "texture staging");
                WriteMaterialData(package, materialOverride, transfer.MaterialData);
                attachments = MorphFaceAttachmentTransferEngine.Transfer(
                    package,
                    sourceHair,
                    sourceOtherMeshes,
                    ToMeGame(sourceGame),
                    templatePath);
                EnsureNoExportParentsAreImports(package, "attachment staging");

                var baseHead = draft.BaseHeadReference
                               ?? throw new InvalidDataException("The converted face has no target-game base head.");
                var stagedBaseHead = package.FindEntry(baseHead.InstancedPath, "SkeletalMesh")
                                     ?? ExternalSkeletalMeshMaterializer.Materialize(
                                         package,
                                         baseHead,
                                         warnings: null);
                EnsureNoExportParentsAreImports(package, "base-head staging");
                var properties = face.GetProperties();
                properties.AddOrReplaceProp(new ObjectProperty(stagedBaseHead, "m_oBaseHead"));
                properties.AddOrReplaceProp(new ObjectProperty(materialOverride, "m_oMaterialOverrides"));
                face.WriteProperties(properties);
                WriteMorphData(face, new MorphFaceMorphData(
                    draft.MorphFeatures, draft.FinalSkeleton, draft.BakedLods));
                WriteAttachments(face, attachments);
                facePath = face.InstancedFullPath;
                materialPath = materialOverride.InstancedFullPath;
                dependencyGraph = PccPackageWorkflow.CaptureDependencyGraph(face);
                package.Save(temporaryPath);
            }

            VerifyMinimalConvertedPackage(temporaryPath, facePath, materialPath, dependencyGraph);
            if (File.Exists(destination))
            {
                throw new IOException("The new conversion destination was created by another process.");
            }
            PccPackageWorkflow.AtomicReplace(temporaryPath, destination);
            return new TransferredMaterialSaveResult(
                new MorphFaceSaveResult(
                    destination,
                    facePath,
                    materialPath,
                    draft.BakedLods.Count,
                    transfer.MaterialData.Textures.Count)
                {
                    Warnings = stagingWarnings
                        .Concat(transfer.Warnings)
                        .Concat(attachments.Warnings)
                        .ToArray()
                },
                transfer.MaterialData);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public MorphFaceSaveResult CloneMorph(string packagePath, string facePath, string objectName)
    {
        ValidateObjectName(objectName);
        return Mutate(packagePath, package =>
        {
            var source = FindFace(package, facePath);
            EnsureNameAvailable(package, source.Parent, objectName);
            var sourceMorph = ReadMorphData(source);
            var sourceMaterial = ReadMaterialData(ResolveMaterialOverride(source));

            package.FindNameOrAdd(objectName);
            var clone = EntryCloner.CloneTree(source);
            clone.ObjectName = new NameReference(objectName);
            var materialOverride = EnsureIndependentMaterialOverride(clone);
            materialOverride.ObjectName = new NameReference(
                materialOverride.ObjectName.Name,
                materialOverride.ObjectName.Number + 1);

            return new PendingResult(
                clone.InstancedFullPath,
                materialOverride.InstancedFullPath,
                sourceMorph,
                sourceMaterial);
        });
    }

    public void DeleteMorph(string packagePath, string facePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(facePath);
        LegendaryExplorerCoreRuntime.Initialize();
        var path = Path.GetFullPath(packagePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The open PCC no longer exists.", path);
        }

        var originalFingerprint = PackageFingerprint.Capture(path);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(path, temporaryPath, overwrite: false);
            using (var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true))
            {
                EnsureSupportedGame(package);
                EntryPruner.TrashEntryAndDescendants(FindFace(package, facePath));
                package.Save(temporaryPath);
            }

            using (var verificationPackage = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true))
            {
                EnsureSupportedGame(verificationPackage);
                if (verificationPackage.FindExport(facePath, "BioMorphFace") is not null)
                {
                    throw new InvalidDataException($"BioMorphFace '{facePath}' was still present after it was trashed.");
                }
            }

            if (PackageFingerprint.Capture(path) != originalFingerprint)
            {
                throw new IOException("The open PCC changed while the morph was being deleted. Nothing was replaced.");
            }
            PccPackageWorkflow.AtomicReplace(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public MorphFaceSaveResult CloneMorphWithData(
        string packagePath,
        string facePath,
        string objectName,
        MorphFaceMorphData data,
        bool requireMatchingAllLods = true,
        bool clearAttachmentReferences = false)
    {
        ValidateObjectName(objectName);
        ValidateMorphData(data);
        return Mutate(packagePath, package =>
        {
            var source = FindFace(package, facePath);
            EnsureNameAvailable(package, source.Parent, objectName);
            if (requireMatchingAllLods)
            {
                EnsureCompatibleLods(ReadMorphData(source), data);
            }
            else
            {
                EnsureCompatibleLod0(ReadMorphData(source), data);
            }
            package.FindNameOrAdd(objectName);
            var clone = EntryCloner.CloneTree(source);
            clone.ObjectName = new NameReference(objectName);
            var materialOverride = EnsureIndependentMaterialOverride(clone);
            materialOverride.ObjectName = new NameReference(
                materialOverride.ObjectName.Name,
                materialOverride.ObjectName.Number + 1);
            WriteMorphData(clone, data);
            if (clearAttachmentReferences)
            {
                var properties = clone.GetProperties();
                properties.RemoveNamedProperty("m_oHairMesh");
                properties.RemoveNamedProperty("m_oOtherMeshes");
                clone.WriteProperties(properties);
            }
            return new PendingResult(
                clone.InstancedFullPath,
                materialOverride.InstancedFullPath,
                data,
                ReadMaterialData(materialOverride));
        });
    }

    public TransferredMaterialSaveResult CloneMorphWithTransferredData(
        string packagePath,
        string facePath,
        string objectName,
        MorphFaceMorphData morphData,
        MorphFaceMaterialData sourceMaterialData,
        IReadOnlySet<string> supportedScalars,
        IReadOnlySet<string> supportedVectors,
        IReadOnlySet<string> supportedTextures,
        MorphFaceGame sourceGame,
        string sourceProfileKey,
        string sourcePackagePath,
        string? targetTemplatePackagePath,
        AssetIdentity? sourceHair,
        IReadOnlyList<AssetIdentity?> sourceOtherMeshes,
        IReadOnlyList<TextureCatalogCandidate>? sourceTextureCatalog = null,
        IReadOnlyList<TextureCatalogCandidate>? targetTextureCatalog = null)
    {
        ValidateObjectName(objectName);
        ValidateMorphData(morphData);
        ValidateMaterialData(sourceMaterialData);
        TextureTransferResult? transfer = null;
        var saveResult = Mutate(packagePath, package =>
        {
            transfer = MorphFaceTextureTransferEngine.Transfer(
                package,
                sourceMaterialData,
                supportedScalars,
                supportedVectors,
                supportedTextures,
                ToMeGame(sourceGame),
                sourceProfileKey,
                sourcePackagePath,
                targetTemplatePackagePath,
                sourceTextureCatalog ?? [],
                targetTextureCatalog ?? []);
            var source = FindFace(package, facePath);
            EnsureNameAvailable(package, source.Parent, objectName);
            EnsureCompatibleLods(ReadMorphData(source), morphData);
            package.FindNameOrAdd(objectName);
            var clone = EntryCloner.CloneTree(source);
            clone.ObjectName = new NameReference(objectName);
            var materialOverride = EnsureIndependentMaterialOverride(clone);
            materialOverride.ObjectName = new NameReference(
                materialOverride.ObjectName.Name,
                materialOverride.ObjectName.Number + 1);
            WriteMorphData(clone, morphData);
            WriteMaterialData(package, materialOverride, transfer.MaterialData);
            var attachments = MorphFaceAttachmentTransferEngine.Transfer(
                package,
                sourceHair,
                sourceOtherMeshes,
                ToMeGame(sourceGame),
                targetTemplatePackagePath);
            WriteAttachments(clone, attachments);
            return new PendingResult(
                clone.InstancedFullPath,
                materialOverride.InstancedFullPath,
                morphData,
                transfer.MaterialData,
                transfer.Warnings.Concat(attachments.Warnings).ToArray());
        });
        return new TransferredMaterialSaveResult(saveResult, transfer!.MaterialData);
    }

    public MorphFaceSaveResult CloneMorphWithData(
        string packagePath,
        string facePath,
        string objectName,
        MorphFaceMorphData morphData,
        MorphFaceMaterialData materialData,
        bool clearAttachmentReferences)
    {
        ValidateObjectName(objectName);
        ValidateMorphData(morphData);
        ValidateMaterialData(materialData);
        return Mutate(packagePath, package =>
        {
            var source = FindFace(package, facePath);
            EnsureNameAvailable(package, source.Parent, objectName);
            EnsureCompatibleLods(ReadMorphData(source), morphData);
            package.FindNameOrAdd(objectName);
            var clone = EntryCloner.CloneTree(source);
            clone.ObjectName = new NameReference(objectName);
            var materialOverride = EnsureIndependentMaterialOverride(clone);
            materialOverride.ObjectName = new NameReference(
                materialOverride.ObjectName.Name,
                materialOverride.ObjectName.Number + 1);
            WriteMorphData(clone, morphData);
            WriteMaterialData(package, materialOverride, materialData);
            if (clearAttachmentReferences)
            {
                var properties = clone.GetProperties();
                properties.RemoveNamedProperty("m_oHairMesh");
                properties.RemoveNamedProperty("m_oOtherMeshes");
                clone.WriteProperties(properties);
            }
            return new PendingResult(
                clone.InstancedFullPath,
                materialOverride.InstancedFullPath,
                morphData,
                materialData);
        });
    }

    /// <summary>
    /// Retains only material parameters supported by the destination profile.
    /// Texture identities are rebound to equivalent entries already present in
    /// that package; unresolved cross-game assets deliberately fall back.
    /// </summary>
    public MorphFaceMaterialData MapCompatibleMaterialData(
        string packagePath,
        MorphFaceMaterialData source,
        IReadOnlySet<string> supportedScalars,
        IReadOnlySet<string> supportedVectors,
        IReadOnlySet<string> supportedTextures)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(supportedScalars);
        ArgumentNullException.ThrowIfNull(supportedVectors);
        ArgumentNullException.ThrowIfNull(supportedTextures);
        ValidateMaterialData(source);
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = OpenPackage(packagePath);
        var textures = source.Textures
            .Where(value => value.TextureReference is not null && supportedTextures.Contains(value.Name))
            .Select(value => (Value: value, Entry: FindCompatibleEntry(
                package,
                value.TextureReference!.InstancedPath,
                "Texture2D")))
            .Where(value => value.Entry is not null)
            .Select(value => value.Value with { TextureReference = ToIdentity(value.Entry) })
            .ToArray();
        return new MorphFaceMaterialData(
            source.Scalars.Where(value => supportedScalars.Contains(value.Name)).ToArray(),
            source.Vectors.Where(value => supportedVectors.Contains(value.Name)).ToArray(),
            textures);
    }

    public void ExportRon(
        string packagePath,
        string facePath,
        string destinationPath,
        RonExportProvenance? provenance = null)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = OpenPackage(packagePath);
        var face = FindFace(package, facePath);
        var properties = face.GetProperties();
        var hair = properties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package)?.InstancedFullPath
                   ?? "None";
        var accessories = properties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
            .Select(value => value.ResolveToEntry(package)?.InstancedFullPath)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray() ?? [];
        TseHeadMorphRon.Write(destinationPath, new TseHeadMorph(
            hair,
            accessories,
            ReadMorphData(face),
            ReadMaterialData(ResolveMaterialOverride(face))), provenance);
    }

    public MorphFaceSaveResult ImportRon(
        string packagePath,
        string templateFacePath,
        string objectName,
        string sourcePath) => ImportHeadMorph(packagePath, templateFacePath, objectName, sourcePath);

    /// <summary>
    /// Imports a player RON after standalone identification has already matched
    /// its LOD0 to the selected game's HMM/HMF topology. Player lower-LOD
    /// coverage is preserved as authored instead of being forced to match the
    /// seed BioMorphFace's stored arrays.
    /// </summary>
    public MorphFaceSaveResult ImportStandalonePlayerRon(
        string packagePath,
        string templateFacePath,
        string objectName,
        string sourcePath,
        StandalonePlayerAssetCatalog? assetCatalog = null)
    {
        ValidateObjectName(objectName);
        var ron = TseHeadMorphRon.Read(sourcePath);
        return ImportHeadMorph(
            packagePath,
            templateFacePath,
            objectName,
            ron,
            ron.MorphData,
            mergeWithTemplate: false,
            requireMatchingAllLods: false,
            assetCatalog: assetCatalog,
            strictAssetResolution: true,
            allowMissingHair: false);
    }

    /// <summary>Imports a proven NPC RON while applying the shared LE1/LE2 material transfer policy.</summary>
    public MorphFaceSaveResult ImportStandaloneNpcRon(
        string packagePath,
        string templateFacePath,
        string objectName,
        string sourcePath,
        MorphFaceGame sourceGame,
        MorphFaceGame targetGame,
        string sourceProfileKey,
        string targetTemplatePackagePath,
        IReadOnlyList<TextureCatalogCandidate> sourceTextureCatalog,
        IReadOnlyList<TextureCatalogCandidate> targetTextureCatalog,
        StandalonePlayerAssetCatalog? assetCatalog = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateObjectName(objectName);
        var ron = TseHeadMorphRon.Read(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        var transfer = sourceGame == targetGame ? null : new NpcRonMaterialTransfer(
            sourceGame,
            sourceProfileKey,
            targetTemplatePackagePath,
            sourceTextureCatalog,
            targetTextureCatalog);
        return ImportHeadMorph(
            packagePath,
            templateFacePath,
            objectName,
            ron,
            ron.MorphData,
            mergeWithTemplate: false,
            requireMatchingAllLods: false,
            assetCatalog: assetCatalog,
            strictAssetResolution: true,
            npcMaterialTransfer: transfer,
            allowMissingHair: true,
            cancellationToken: cancellationToken);
    }

    public MorphFaceSaveResult ImportHeadMorph(
        string packagePath,
        string templateFacePath,
        string objectName,
        string sourcePath)
    {
        ValidateObjectName(objectName);
        var ron = Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".ron" => TseHeadMorphRon.Read(sourcePath),
            ".me2headmorph" or ".me3headmorph" => throw new InvalidOperationException(
                "Legacy Gibbed morphs must be converted against an LE face profile before package import."),
            _ => throw new InvalidDataException("The file is not a supported serialized head morph.")
        };
        return ImportHeadMorph(packagePath, templateFacePath, objectName, ron, ron.MorphData, mergeWithTemplate: false);
    }

    public LegacyHeadMorphImport ReadLegacyHeadMorph(string sourcePath)
    {
        var morph = GibbedHeadMorph.Read(sourcePath);
        ValidateMorphData(morph.MorphData);
        ValidateMaterialData(morph.MaterialData);
        return new LegacyHeadMorphImport(
            morph.HairMesh,
            morph.AccessoryMeshes,
            morph.MorphData,
            morph.MaterialData);
    }

    public MorphFaceSaveResult ImportConvertedLegacyHeadMorph(
        string packagePath,
        string templateFacePath,
        string objectName,
        string sourcePath,
        MorphFaceMorphData convertedMorphData)
    {
        ValidateObjectName(objectName);
        var legacy = GibbedHeadMorph.Read(sourcePath);
        ValidateMorphData(convertedMorphData);
        ValidateMaterialData(legacy.MaterialData);
        return ImportHeadMorph(
            packagePath,
            templateFacePath,
            objectName,
            legacy,
            convertedMorphData,
            mergeWithTemplate: true);
    }

    private MorphFaceSaveResult ImportHeadMorph(
        string packagePath,
        string templateFacePath,
        string objectName,
        TseHeadMorph ron,
        MorphFaceMorphData morphData,
        bool mergeWithTemplate,
        bool requireMatchingAllLods = true,
        StandalonePlayerAssetCatalog? assetCatalog = null,
        bool strictAssetResolution = false,
        NpcRonMaterialTransfer? npcMaterialTransfer = null,
        bool allowMissingHair = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMorphData(ron.MorphData);
        ValidateMaterialData(ron.MaterialData);
        return Mutate(packagePath, package =>
        {
            var source = FindFace(package, templateFacePath);
            EnsureNameAvailable(package, source.Parent, objectName);
            var templateMorph = ReadMorphData(source);
            if (requireMatchingAllLods)
            {
                EnsureCompatibleLods(templateMorph, morphData);
            }
            else
            {
                EnsureCompatibleLod0(templateMorph, morphData);
            }
            package.FindNameOrAdd(objectName);
            var clone = EntryCloner.CloneTree(source);
            clone.ObjectName = new NameReference(objectName);
            var materialOverride = EnsureIndependentMaterialOverride(clone);
            materialOverride.ObjectName = new NameReference(
                materialOverride.ObjectName.Name,
                materialOverride.ObjectName.Number + 1);

            var templateMaterial = ReadMaterialData(ResolveMaterialOverride(source));
            var warnings = new List<string>();
            var resolvedMaterial = mergeWithTemplate
                ? MergeLegacyMaterials(package, templateMaterial, ron.MaterialData, warnings)
                : npcMaterialTransfer is null
                    ? ResolveRonMaterials(package, ron.MaterialData, assetCatalog, strictAssetResolution, warnings)
                    : TransferNpcRonMaterials(package, ron.MaterialData, npcMaterialTransfer,
                        assetCatalog, warnings);
            WriteMorphData(clone, morphData);
            WriteMaterialData(package, materialOverride, resolvedMaterial);
            if (mergeWithTemplate)
            {
                WriteLegacyMeshReferences(package, clone, ron, warnings);
            }
            else
            {
                WriteRonMeshReferences(
                    package,
                    clone,
                    ron,
                    assetCatalog,
                    strictAssetResolution,
                    warnings,
                    allowMissingHair);
            }
            return new PendingResult(
                clone.InstancedFullPath,
                materialOverride.InstancedFullPath,
                morphData,
                resolvedMaterial,
                warnings);
        }, cancellationToken);
    }

    public MorphFaceSaveResult PasteMorphData(
        string packagePath,
        string facePath,
        MorphFaceMorphData data)
    {
        ValidateMorphData(data);
        return Mutate(packagePath, package =>
        {
            var face = FindFace(package, facePath);
            var current = ReadMorphData(face);
            EnsureCompatibleLods(current, data);
            WriteMorphData(face, data);
            var materialOverride = ResolveMaterialOverride(face);
            return new PendingResult(
                face.InstancedFullPath,
                materialOverride.InstancedFullPath,
                data,
                ReadMaterialData(materialOverride));
        });
    }

    public MorphFaceSaveResult PasteMaterialData(
        string packagePath,
        string facePath,
        MorphFaceMaterialData data)
    {
        ValidateMaterialData(data);
        return Mutate(packagePath, package =>
        {
            var face = FindFace(package, facePath);
            var materialOverride = ResolveMaterialOverride(face);
            WriteMaterialData(package, materialOverride, data);
            return new PendingResult(
                face.InstancedFullPath,
                materialOverride.InstancedFullPath,
                ReadMorphData(face),
                data);
        });
    }

    public TransferredMaterialSaveResult PasteTransferredMaterialData(
        string packagePath,
        string facePath,
        MorphFaceMaterialData source,
        IReadOnlySet<string> supportedScalars,
        IReadOnlySet<string> supportedVectors,
        IReadOnlySet<string> supportedTextures,
        MorphFaceGame sourceGame,
        string sourceProfileKey,
        string sourcePackagePath,
        string? targetTemplatePackagePath,
        AssetIdentity? sourceHair,
        IReadOnlyList<AssetIdentity?> sourceOtherMeshes,
        IReadOnlyList<TextureCatalogCandidate>? sourceTextureCatalog = null,
        IReadOnlyList<TextureCatalogCandidate>? targetTextureCatalog = null)
    {
        ValidateMaterialData(source);
        TextureTransferResult? transfer = null;
        var saveResult = Mutate(packagePath, package =>
        {
            transfer = MorphFaceTextureTransferEngine.Transfer(
                package,
                source,
                supportedScalars,
                supportedVectors,
                supportedTextures,
                ToMeGame(sourceGame),
                sourceProfileKey,
                sourcePackagePath,
                targetTemplatePackagePath,
                sourceTextureCatalog ?? [],
                targetTextureCatalog ?? []);
            var face = FindFace(package, facePath);
            var materialOverride = ResolveMaterialOverride(face);
            WriteMaterialData(package, materialOverride, transfer.MaterialData);
            var attachments = MorphFaceAttachmentTransferEngine.Transfer(
                package,
                sourceHair,
                sourceOtherMeshes,
                ToMeGame(sourceGame),
                targetTemplatePackagePath);
            WriteAttachments(face, attachments);
            return new PendingResult(
                face.InstancedFullPath,
                materialOverride.InstancedFullPath,
                ReadMorphData(face),
                transfer.MaterialData,
                transfer.Warnings.Concat(attachments.Warnings).ToArray());
        });
        return new TransferredMaterialSaveResult(saveResult, transfer!.MaterialData);
    }

    private static void WriteAttachments(ExportEntry face, AttachmentTransferResult attachments)
    {
        var properties = face.GetProperties();
        if (attachments.Hair is null)
        {
            properties.RemoveNamedProperty("m_oHairMesh");
        }
        else
        {
            properties.AddOrReplaceProp(new ObjectProperty(attachments.Hair, "m_oHairMesh"));
        }
        if (attachments.OtherMeshes.Count == 0)
        {
            properties.RemoveNamedProperty("m_oOtherMeshes");
        }
        else
        {
            properties.AddOrReplaceProp(new ArrayProperty<ObjectProperty>(
                attachments.OtherMeshes.Select(value => new ObjectProperty(value)),
                "m_oOtherMeshes"));
        }
        face.WriteProperties(properties);
    }

    private static ExportEntry? EnsureExportPackagePath(IMEPackage package, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        ExportEntry? parent = null;
        var currentPath = string.Empty;
        foreach (var segment in path.Split('.'))
        {
            currentPath = currentPath.Length == 0 ? segment : $"{currentPath}.{segment}";
            parent = package.FindExport(currentPath, "Package")
                     ?? package.CreatePackageExport(NameReference.FromInstancedString(segment), parent);
        }
        return parent;
    }

    private static void VerifyMinimalConvertedPackage(
        string packagePath,
        string facePath,
        string materialPath,
        PccDependencyGraphSnapshot dependencyGraph)
    {
        using var package = OpenPackage(packagePath);
        PackageIntegrity.Verify(package);
        _ = FindFace(package, facePath);
        _ = package.FindExport(materialPath, "BioMaterialOverride")
            ?? throw new InvalidDataException("The converted material override was not saved.");
        PccPackageWorkflow.VerifyDependencyGraph(package, dependencyGraph, verifyCorpusRoles: false);
        var malformed = package.Exports
            .Where(export => export.GetProperties().Any(ContainsUnknownProperty))
            .Select(export => export.InstancedFullPath)
            .ToArray();
        if (malformed.Length > 0)
        {
            throw new InvalidDataException(
                $"Converted package contains UnknownProperty data in: {string.Join(", ", malformed)}.");
        }
        EnsureNoInvalidHmmMorphPackageAliases(package, "final verification");
    }

    private static void EnsureNoExportParentsAreImports(IMEPackage package, string stage)
    {
        PackageIntegrity.Verify(package);
        EnsureNoInvalidHmmMorphPackageAliases(package, stage);
    }

    private static void NormalizeInvalidHmmMorphPackageAliases(IMEPackage package)
    {
        const string invalidName = "BIOG_HMM_HED_PROMorph_R";
        const string canonicalName = "BIOG_HMM_HED_PROMorph";
        foreach (var entry in package.Exports.Cast<IEntry>().Concat(package.Imports).Where(entry =>
                     entry.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase) &&
                     entry.ObjectName.Instanced.Equals(invalidName, StringComparison.OrdinalIgnoreCase)))
        {
            entry.ObjectName = NameReference.FromInstancedString(canonicalName);
        }
    }

    private static void EnsureNoInvalidHmmMorphPackageAliases(IMEPackage package, string stage)
    {
        var invalid = package.Exports.Cast<IEntry>().Concat(package.Imports).FirstOrDefault(entry =>
            entry.InstancedFullPath.Equals("BIOG_HMM_HED_PROMorph_R", StringComparison.OrdinalIgnoreCase) ||
            entry.InstancedFullPath.StartsWith("BIOG_HMM_HED_PROMorph_R.", StringComparison.OrdinalIgnoreCase));
        if (invalid is not null)
        {
            throw new InvalidDataException(
                $"{stage} retained invalid HMM morph package path '{invalid.InstancedFullPath}'.");
        }
    }

    private static bool ContainsUnknownProperty(Property property) => property switch
    {
        UnknownProperty => true,
        StructProperty structure => structure.Properties.Any(ContainsUnknownProperty),
        ArrayProperty<StructProperty> array => array.Any(value =>
            value.Properties.Any(ContainsUnknownProperty)),
        _ => false
    };

    private static MorphFaceSaveResult Mutate(
        string packagePath,
        Func<IMEPackage, PendingResult> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(mutation);
        var path = Path.GetFullPath(packagePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The open PCC no longer exists.", path);
        }

        var originalFingerprint = PackageFingerprint.Capture(path);
        IReadOnlySet<string> integrityBaseline;
        using (var original = OpenPackage(path))
            integrityBaseline = PackageIntegrity.CaptureIssues(original);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(path, temporaryPath, overwrite: false);
            cancellationToken.ThrowIfCancellationRequested();
            PendingResult pending;
            using (var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true))
            {
                EnsureSupportedGame(package);
                pending = mutation(package);
                cancellationToken.ThrowIfCancellationRequested();
                package.Save(temporaryPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Verify(temporaryPath, pending, integrityBaseline);
            cancellationToken.ThrowIfCancellationRequested();
            if (PackageFingerprint.Capture(path) != originalFingerprint)
            {
                throw new IOException("The open PCC changed while the operation was being written. Nothing was replaced.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            PccPackageWorkflow.AtomicReplace(temporaryPath, path);
            return new MorphFaceSaveResult(
                path,
                pending.FacePath,
                pending.MaterialOverridePath,
                pending.MorphData.BakedLods.Count,
                pending.MaterialData.Textures.Count)
            {
                Warnings = pending.Warnings ?? []
            };
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static MorphFaceMorphData ReadMorphData(ExportEntry face)
    {
        var properties = face.GetProperties();
        var binary = face.GetBinaryData<BinaryMorphFace>();
        return new MorphFaceMorphData(
            properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")?
                .Select(item => new MorphFeatureValue(
                    Name(item, "sFeatureName"),
                    item.GetProp<FloatProperty>("Offset")?.Value ?? 0f))
                .ToArray() ?? [],
            properties.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton")?
                .Select(item => new BoneTranslation(
                    Name(item, "nName"),
                    ReadVector(item.GetProp<StructProperty>("vPos"))))
                .ToArray() ?? [],
            binary.LODs?.Select(lod => lod?.ToArray() ?? []).ToArray() ?? []);
    }

    private static MorphFaceMaterialData ReadMaterialData(ExportEntry materialOverride)
    {
        var package = materialOverride.FileRef;
        var properties = materialOverride.GetProperties();
        return new MorphFaceMaterialData(
            properties.GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides")?
                .Select(item => new ScalarMaterialOverride(
                    Name(item, "nName"),
                    item.GetProp<FloatProperty>("sValue")?.Value ?? 0f))
                .ToArray() ?? [],
            properties.GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides")?
                .Select(item => new VectorMaterialOverride(
                    Name(item, "nName"),
                    ReadLinearColor(item.GetProp<StructProperty>("cValue"))))
                .ToArray() ?? [],
            properties.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides")?
                .Select(item => new TextureMaterialOverride(
                    Name(item, "nName"),
                    ToIdentity(item.GetProp<ObjectProperty>("m_pTexture")?.ResolveToEntry(package))))
                .ToArray() ?? []);
    }

    private static void WriteMorphData(ExportEntry face, MorphFaceMorphData data)
    {
        var properties = face.GetProperties();
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.MorphFeatures.Select(value => new StructProperty(
                "MorphFeature",
                false,
                new NameProperty(value.Name, "sFeatureName"),
                new FloatProperty(value.Offset, "Offset"))),
            "m_aMorphFeatures"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.FinalSkeleton.Select(value => new StructProperty(
                "OffsetBonePos",
                false,
                new NameProperty(value.BoneName, "nName"),
                VectorProperty(value.Translation, "vPos"))),
            "m_aFinalSkeleton"));
        face.WritePropertiesAndBinary(properties, new BinaryMorphFace
        {
            LODs = data.BakedLods.Select(lod => lod.ToArray()).ToArray()
        });
    }

    private static void WriteMaterialData(
        IMEPackage package,
        ExportEntry materialOverride,
        MorphFaceMaterialData data)
    {
        var properties = materialOverride.GetProperties();
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.Scalars.Select(value => new StructProperty(
                "ScalarParameter",
                false,
                new NameProperty(value.Name, "nName"),
                new FloatProperty(value.Value, "sValue"))),
            "m_aScalarOverrides"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.Vectors.Select(value => new StructProperty(
                "ColorParameter",
                false,
                new NameProperty(value.Name, "nName"),
                LinearColorProperty(value.Value, "cValue"))),
            "m_aColorOverrides"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.Textures.Select(value => new StructProperty(
                "TextureParameter",
                [
                    new NameProperty(value.Name, "nName"),
                    new ObjectProperty(ResolveTexture(package, value.TextureReference), "m_pTexture")
                ])),
            "m_aTextureOverrides"));
        materialOverride.WriteProperties(properties);
    }

    private static MorphFaceMaterialData TransferNpcRonMaterials(
        IMEPackage package,
        MorphFaceMaterialData source,
        NpcRonMaterialTransfer context,
        StandalonePlayerAssetCatalog? assetCatalog,
        ICollection<string> warnings)
    {
        var scalars = source.Scalars.Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vectors = source.Vectors.Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textures = source.Textures.Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapped = MorphFaceTextureTransferEngine.Transfer(
            package,
            source,
            scalars,
            vectors,
            textures,
            ToMeGame(context.SourceGame),
            context.SourceProfileKey,
            string.Empty,
            context.TargetTemplatePackagePath,
            context.SourceTextureCatalog,
            context.TargetTextureCatalog);
        foreach (var warning in mapped.Warnings)
        {
            warnings.Add(warning);
        }

        // Keep authored overrides that the reviewed cross-game policy cannot
        // map. The ordinary RON resolver retains their exact path as an import
        // with a visible warning rather than silently discarding source data.
        var mappedNames = mapped.MaterialData.Textures.Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untransferred = source.Textures.Where(value => !mappedNames.Contains(value.Name)).ToArray();
        var retained = ResolveRonMaterials(
            package,
            new MorphFaceMaterialData([], [], untransferred),
            assetCatalog,
            strictAssetResolution: true,
            warnings);
        return new MorphFaceMaterialData(
            source.Scalars,
            source.Vectors,
            mapped.MaterialData.Textures.Concat(retained.Textures).ToArray());
    }

    private static MorphFaceMaterialData ResolveRonMaterials(
        IMEPackage package,
        MorphFaceMaterialData materialData,
        StandalonePlayerAssetCatalog? assetCatalog,
        bool strictAssetResolution,
        ICollection<string> warnings)
    {
        var textures = materialData.Textures.Select(value =>
        {
            if (value.TextureReference is null)
            {
                return value;
            }
            var existing = PackageIntegrity.FindExactEntry(package, value.TextureReference.InstancedPath, "Texture2D");
            IEntry? entry = existing as ExportEntry ?? (!strictAssetResolution ? existing : null);
            if (entry is null)
            {
                try
                {
                    entry = ResolveTextureCandidate(
                        package,
                        value.TextureReference.InstancedPath,
                        assetCatalog,
                        existing as ImportEntry);
                }
                catch (Exception exception) when (strictAssetResolution &&
                                                  exception is InvalidDataException or FileNotFoundException)
                {
                    entry = EnsureImport(package, value.TextureReference.InstancedPath, "Texture2D");
                    warnings.Add(
                        $"RON texture '{value.TextureReference.InstancedPath}' is unavailable in the selected game's installed assets. " +
                        "Its exact authored reference was retained for RON roundtrip; preview will use inherited or placeholder material data.");
                }
            }
            return value with { TextureReference = ToIdentity(entry) };
        }).ToArray();
        return materialData with { Textures = textures };
    }

    private static MorphFaceMaterialData MergeLegacyMaterials(
        IMEPackage package,
        MorphFaceMaterialData template,
        MorphFaceMaterialData legacy,
        ICollection<string> warnings)
    {
        var scalars = MergeByName(template.Scalars, legacy.Scalars, value => value.Name);
        var vectors = MergeByName(template.Vectors, legacy.Vectors, value => value.Name);
        var templateTextures = template.Textures.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var convertedTextures = new List<TextureMaterialOverride>();
        foreach (var value in legacy.Textures)
        {
            if (value.TextureReference is null)
            {
                convertedTextures.Add(value);
                continue;
            }
            var exact = package.FindEntry(value.TextureReference.InstancedPath, "Texture2D");
            var resolved = exact ?? FindCompatibleEntry(package, value.TextureReference.InstancedPath, "Texture2D");
            if (resolved is not null)
            {
                convertedTextures.Add(value with { TextureReference = ToIdentity(resolved) });
                if (exact is null)
                {
                    warnings.Add(
                        $"Legacy texture '{value.Name}' at '{value.TextureReference.InstancedPath}' was not present exactly; " +
                        $"retained the unique object-name match '{resolved.InstancedFullPath}'.");
                }
            }
            else if (templateTextures.TryGetValue(value.Name, out var fallback))
            {
                convertedTextures.Add(fallback);
                warnings.Add(
                    $"Legacy texture '{value.Name}' at '{value.TextureReference.InstancedPath}' was unresolved; " +
                    $"retained the selected player template texture '{fallback.TextureReference?.InstancedPath ?? "None"}'.");
            }
            else
            {
                warnings.Add(
                    $"Legacy texture '{value.Name}' at '{value.TextureReference.InstancedPath}' was unresolved and omitted.");
            }
        }
        var textures = MergeByName(template.Textures, convertedTextures, value => value.Name);
        return new MorphFaceMaterialData(scalars, vectors, textures);
    }

    private static IReadOnlyList<T> MergeByName<T>(
        IReadOnlyList<T> template,
        IReadOnlyList<T> imported,
        Func<T, string> name)
    {
        var replacements = imported.ToDictionary(name, StringComparer.OrdinalIgnoreCase);
        var result = template
            .Select(value => replacements.Remove(name(value), out var replacement) ? replacement : value)
            .ToList();
        result.AddRange(imported.Where(value => replacements.ContainsKey(name(value))));
        return result;
    }

    private static void WriteLegacyMeshReferences(
        IMEPackage package,
        ExportEntry clone,
        TseHeadMorph legacy,
        ICollection<string> warnings)
    {
        var properties = clone.GetProperties();
        if (string.IsNullOrWhiteSpace(legacy.HairMesh) ||
            string.Equals(legacy.HairMesh, "None", StringComparison.OrdinalIgnoreCase))
        {
            properties.RemoveNamedProperty("m_oHairMesh");
        }
        else if (FindCompatibleEntry(package, legacy.HairMesh, "SkeletalMesh") is { } hair)
        {
            properties.AddOrReplaceProp(new ObjectProperty(hair, "m_oHairMesh"));
            if (package.FindEntry(legacy.HairMesh, "SkeletalMesh") is null)
            {
                warnings.Add(
                    $"Legacy hair mesh '{legacy.HairMesh}' was not present exactly; " +
                    $"retained the unique object-name match '{hair.InstancedFullPath}'.");
            }
        }
        else
        {
            warnings.Add($"Legacy hair mesh '{legacy.HairMesh}' was unresolved and omitted.");
            properties.RemoveNamedProperty("m_oHairMesh");
        }

        var accessories = legacy.AccessoryMeshes
            .Select(path =>
            {
                var exact = package.FindEntry(path, "SkeletalMesh");
                return (Path: path, Entry: FindCompatibleEntry(package, path, "SkeletalMesh"), Exact: exact is not null);
            })
            .ToArray();
        foreach (var accessory in accessories.Where(value => value.Entry is null))
        {
            warnings.Add($"Legacy accessory mesh '{accessory.Path}' was unresolved and omitted.");
        }
        foreach (var accessory in accessories.Where(value => value.Entry is not null && !value.Exact))
        {
            warnings.Add(
                $"Legacy accessory mesh '{accessory.Path}' was not present exactly; " +
                $"retained the unique object-name match '{accessory.Entry!.InstancedFullPath}'.");
        }
        var resolvedAccessories = accessories
            .Where(value => value.Entry is not null)
            .Select(value => new ObjectProperty(value.Entry!))
            .ToArray();
        if (legacy.AccessoryMeshes.Count == 0)
        {
            properties.RemoveNamedProperty("m_oOtherMeshes");
        }
        else if (resolvedAccessories.Length > 0)
        {
            properties.AddOrReplaceProp(new ArrayProperty<ObjectProperty>(resolvedAccessories, "m_oOtherMeshes"));
        }
        else
        {
            properties.RemoveNamedProperty("m_oOtherMeshes");
        }
        clone.WriteProperties(properties);
    }

    private static IEntry? FindCompatibleEntry(IMEPackage package, string legacyPath, string className)
    {
        if (package.FindEntry(legacyPath, className) is { } exact)
        {
            return exact;
        }
        var objectName = legacyPath.Split('.').LastOrDefault();
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return null;
        }
        var matches = package.Exports.Cast<IEntry>()
            .Concat(package.Imports)
            .Where(entry =>
                string.Equals(entry.ClassName, className, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.ObjectNameString, objectName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static IEntry ResolveTextureCandidate(
        IMEPackage package,
        string instancedPath,
        StandalonePlayerAssetCatalog? assetCatalog,
        ImportEntry? existingImport)
    {
        var candidate = assetCatalog?.Textures
            .SingleOrDefault(value => value.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                                      value.InstancedPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            throw new InvalidDataException(
                $"RON Texture2D '{instancedPath}' is not present in the standalone seed and has no exact installed-game catalogue candidate.");
        }

        if (!File.Exists(candidate.PackagePath))
        {
            throw new FileNotFoundException(
                $"The installed-game texture donor package is missing: {candidate.PackagePath}",
                candidate.PackagePath);
        }
        if (existingImport is not null)
        {
            return existingImport;
        }
        return ExternalTextureMaterializer.Materialize(package, candidate);
    }

    private static IEntry ResolveMeshCandidate(
        IMEPackage package,
        string instancedPath,
        StandalonePlayerAssetCatalog? assetCatalog,
        ImportEntry? existingImport)
    {
        var candidates = assetCatalog?.SkeletalMeshes
            .Where(value => value.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
                            value.InstancedPath.Equals(instancedPath, StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(value.PackagePath))
            .ToArray() ?? [];
        if (candidates.Length != 1)
        {
            throw new UnresolvedRonAttachmentException(
                $"RON SkeletalMesh '{instancedPath}' is not present in the standalone seed and does not have exactly one exact installed-game donor candidate.");
        }
        if (existingImport is not null)
        {
            throw new UnresolvedRonAttachmentException(
                $"RON SkeletalMesh '{instancedPath}' has an import placeholder in the destination; " +
                "its exact donor cannot be materialised without replacing an import ancestry.");
        }
        return ExternalSkeletalMeshMaterializer.Materialize(package, candidates[0]);
    }

    private static void WriteRonMeshReferences(
        IMEPackage package,
        ExportEntry face,
        TseHeadMorph ron,
        StandalonePlayerAssetCatalog? assetCatalog,
        bool strictAssetResolution,
        ICollection<string> warnings,
        bool allowMissingHair)
    {
        var properties = face.GetProperties();
        if (string.IsNullOrWhiteSpace(ron.HairMesh) ||
            string.Equals(ron.HairMesh, "None", StringComparison.OrdinalIgnoreCase))
        {
            properties.RemoveNamedProperty("m_oHairMesh");
        }
        else
        {
            var existing = package.FindEntry(ron.HairMesh, "SkeletalMesh");
            IEntry? hair = existing as ExportEntry ?? (!strictAssetResolution ? existing : null);
            if (hair is null)
            {
                try
                {
                    hair = ResolveMeshCandidate(package, ron.HairMesh, assetCatalog, existing as ImportEntry);
                }
                catch (UnresolvedRonAttachmentException exception) when (strictAssetResolution)
                {
                    if (allowMissingHair)
                    {
                        properties.RemoveNamedProperty("m_oHairMesh");
                        warnings.Add(
                            $"RON hair mesh '{ron.HairMesh}' was omitted because it is unavailable in the selected game's installed assets: " +
                            exception.Message);
                        hair = null;
                    }
                    else
                    {
                        throw new InvalidDataException(
                            $"RON attachment '{ron.HairMesh}' is unavailable in the selected game's installed assets; " +
                            "the import was rejected because an unresolved SkeletalMesh cannot be exported safely.",
                            exception);
                    }
                }
            }
            if (hair is not null)
            {
                properties.AddOrReplaceProp(new ObjectProperty(hair, "m_oHairMesh"));
            }
        }

        var accessories = ron.AccessoryMeshes.Select(path =>
        {
            var existing = package.FindEntry(path, "SkeletalMesh");
            IEntry? entry = existing as ExportEntry ?? (!strictAssetResolution ? existing : null);
            if (entry is null)
            {
                try
                {
                    entry = ResolveMeshCandidate(package, path, assetCatalog, existing as ImportEntry);
                }
                catch (UnresolvedRonAttachmentException exception) when (strictAssetResolution)
                {
                    throw new InvalidDataException(
                        $"RON attachment '{path}' is unavailable in the selected game's installed assets; " +
                        "the import was rejected because an unresolved SkeletalMesh cannot be exported safely.",
                        exception);
                }
            }
            return new ObjectProperty(entry);
        }).ToArray();
        if (accessories.Length == 0)
        {
            properties.RemoveNamedProperty("m_oOtherMeshes");
        }
        else
        {
            properties.AddOrReplaceProp(new ArrayProperty<ObjectProperty>(accessories, "m_oOtherMeshes"));
        }
        face.WriteProperties(properties);
    }

    private sealed class UnresolvedRonAttachmentException(string message) : IOException(message);

    private static IEntry EnsureImport(IMEPackage destination, string instancedPath, string className)
    {
        if (destination.FindEntry(instancedPath, className) is { } existing)
        {
            return existing;
        }
        var segments = instancedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            throw new InvalidDataException($"Cannot preserve malformed {className} path '{instancedPath}'.");
        }
        IEntry? parent = null;
        var currentPath = string.Empty;
        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = index == 0 ? segments[index] : $"{currentPath}.{segments[index]}";
            var segmentClass = index == segments.Length - 1 ? className : "Package";
            parent = destination.FindEntry(currentPath, segmentClass) ?? (index == 0
                ? destination.CreatePackageImport(NameReference.FromInstancedString(segments[index]))
                : destination.CreateImport(
                    segmentClass,
                    NameReference.FromInstancedString(segments[index]),
                    parent));
        }
        return parent!;
    }

    private static ExportEntry EnsureIndependentMaterialOverride(ExportEntry clone)
    {
        var properties = clone.GetProperties();
        var existing = properties.GetProp<ObjectProperty>("m_oMaterialOverrides")?
            .ResolveToEntry(clone.FileRef) as ExportEntry
            ?? throw new InvalidDataException(
                $"BioMorphFace '{clone.InstancedFullPath}' has no resolvable BioMaterialOverride.");
        if (existing.Parent == clone)
        {
            return existing;
        }

        var created = ExportCreator.CreateExport(
            clone.FileRef,
            new NameReference(existing.ObjectName.Name, existing.ObjectName.Number + 1),
            "BioMaterialOverride",
            clone,
            indexed: false);
        created.WriteProperties(existing.GetProperties());
        properties.AddOrReplaceProp(new ObjectProperty(created, "m_oMaterialOverrides"));
        clone.WriteProperties(properties);
        return created;
    }

    private static void Verify(
        string packagePath,
        PendingResult expected,
        IReadOnlySet<string> allowedExistingIntegrityIssues)
    {
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        PackageIntegrity.Verify(package, allowedExistingIntegrityIssues);
        var face = FindFace(package, expected.FacePath);
        var materialOverride = ResolveMaterialOverride(face);
        CompareMorphData(ReadMorphData(face), expected.MorphData);
        CompareMaterialData(ReadMaterialData(materialOverride), expected.MaterialData);
        if (!string.Equals(materialOverride.InstancedFullPath, expected.MaterialOverridePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The BioMaterialOverride path changed during package verification.");
        }
    }

    private static void CompareMorphData(MorphFaceMorphData actual, MorphFaceMorphData expected)
    {
        if (actual.MorphFeatures.Count != expected.MorphFeatures.Count ||
            actual.FinalSkeleton.Count != expected.FinalSkeleton.Count ||
            actual.BakedLods.Count != expected.BakedLods.Count)
        {
            throw new InvalidDataException("Morph data counts changed during package verification.");
        }
        for (var index = 0; index < expected.MorphFeatures.Count; index++)
        {
            var left = actual.MorphFeatures[index];
            var right = expected.MorphFeatures[index];
            if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(left.Offset - right.Offset) > FloatTolerance)
            {
                throw new InvalidDataException($"Morph feature {index} changed during package verification.");
            }
        }
        for (var index = 0; index < expected.FinalSkeleton.Count; index++)
        {
            var left = actual.FinalSkeleton[index];
            var right = expected.FinalSkeleton[index];
            if (!string.Equals(left.BoneName, right.BoneName, StringComparison.OrdinalIgnoreCase) ||
                Vector3.Distance(left.Translation, right.Translation) > FloatTolerance)
            {
                throw new InvalidDataException($"Final skeleton bone {index} changed during package verification.");
            }
        }
        for (var lod = 0; lod < expected.BakedLods.Count; lod++)
        {
            if (actual.BakedLods[lod].Length != expected.BakedLods[lod].Length)
            {
                throw new InvalidDataException($"Baked LOD {lod} changed size during package verification.");
            }
            for (var vertex = 0; vertex < expected.BakedLods[lod].Length; vertex++)
            {
                if (Vector3.Distance(actual.BakedLods[lod][vertex], expected.BakedLods[lod][vertex]) > FloatTolerance)
                {
                    throw new InvalidDataException($"Baked LOD {lod} vertex {vertex} changed during package verification.");
                }
            }
        }
    }

    private static void CompareMaterialData(MorphFaceMaterialData actual, MorphFaceMaterialData expected)
    {
        if (actual.Scalars.Count != expected.Scalars.Count ||
            actual.Vectors.Count != expected.Vectors.Count ||
            actual.Textures.Count != expected.Textures.Count)
        {
            throw new InvalidDataException("Material data counts changed during package verification.");
        }
        for (var index = 0; index < expected.Scalars.Count; index++)
        {
            var left = actual.Scalars[index];
            var right = expected.Scalars[index];
            if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(left.Value - right.Value) > FloatTolerance)
            {
                throw new InvalidDataException($"Material scalar {index} changed during package verification.");
            }
        }
        for (var index = 0; index < expected.Vectors.Count; index++)
        {
            var left = actual.Vectors[index];
            var right = expected.Vectors[index];
            if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
                Vector4.Distance(left.Value, right.Value) > FloatTolerance)
            {
                throw new InvalidDataException($"Material vector {index} changed during package verification.");
            }
        }
        for (var index = 0; index < expected.Textures.Count; index++)
        {
            var left = actual.Textures[index];
            var right = expected.Textures[index];
            if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(left.TextureReference?.InstancedPath, right.TextureReference?.InstancedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Material texture {index} changed during package verification.");
            }
        }
    }

    private static void ValidateMorphData(MorphFaceMorphData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.MorphFeatures.Any(value => string.IsNullOrWhiteSpace(value.Name) || !float.IsFinite(value.Offset)) ||
            data.MorphFeatures.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.MorphFeatures.Count)
        {
            throw new InvalidDataException("Clipboard morph features must have unique names and finite values.");
        }
        if (data.FinalSkeleton.Any(value => string.IsNullOrWhiteSpace(value.BoneName) || !IsFinite(value.Translation)) ||
            data.FinalSkeleton.Select(value => value.BoneName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.FinalSkeleton.Count)
        {
            throw new InvalidDataException("Clipboard final-skeleton entries must have unique names and finite values.");
        }
        if (data.BakedLods.Count == 0 || data.BakedLods.Any(lod => lod.Length == 0 || lod.Any(value => !IsFinite(value))))
        {
            throw new InvalidDataException("Clipboard baked LODs must be non-empty and finite.");
        }
    }

    private static void ValidateMaterialData(MorphFaceMaterialData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (HasDuplicateNames(data.Scalars.Select(value => value.Name)) ||
            HasDuplicateNames(data.Vectors.Select(value => value.Name)) ||
            HasDuplicateNames(data.Textures.Select(value => value.Name)) ||
            data.Scalars.Any(value => !float.IsFinite(value.Value)) ||
            data.Vectors.Any(value => !IsFinite(value.Value)))
        {
            throw new InvalidDataException("Clipboard material parameters must have unique names and finite values.");
        }
    }

    private static void EnsureCompatibleLods(MorphFaceMorphData target, MorphFaceMorphData source)
    {
        if (target.BakedLods.Count != source.BakedLods.Count ||
            target.BakedLods.Where((lod, index) => lod.Length != source.BakedLods[index].Length).Any())
        {
            throw new InvalidOperationException(
                "The source and destination morphs do not have matching baked-LOD topology.");
        }
    }

    private static void EnsureCompatibleLod0(MorphFaceMorphData target, MorphFaceMorphData source)
    {
        if (target.BakedLods.Count == 0 || source.BakedLods.Count == 0 ||
            target.BakedLods[0].Length != source.BakedLods[0].Length)
        {
            throw new InvalidOperationException(
                "The source player morph does not match the selected game's HMM or HMF LOD0 topology.");
        }
    }

    private static void ValidateObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName) ||
            !(char.IsLetter(objectName[0]) || objectName[0] == '_') ||
            objectName.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                "The new export name may contain letters, digits, and underscores, and cannot begin with a digit.",
                nameof(objectName));
        }
    }

    private static void EnsureNameAvailable(IMEPackage package, IEntry? parent, string objectName)
    {
        var path = parent is null ? objectName : $"{parent.InstancedFullPath}.{objectName}";
        if (package.FindEntry(path) is not null)
        {
            throw new InvalidOperationException($"An entry named '{path}' already exists.");
        }
    }

    private static IEntry ResolveTexture(IMEPackage package, AssetIdentity? identity)
    {
        if (identity is null)
        {
            return null!;
        }
        return package.FindEntry(identity.InstancedPath, "Texture2D")
            ?? throw new InvalidDataException(
                $"Texture2D '{identity.InstancedPath}' is not present in the destination PCC.");
    }

    private static ExportEntry ResolveMaterialOverride(ExportEntry face) =>
        face.GetProperty<ObjectProperty>("m_oMaterialOverrides")?.ResolveToEntry(face.FileRef) as ExportEntry
        ?? throw new InvalidDataException($"BioMorphFace '{face.InstancedFullPath}' has no local BioMaterialOverride.");

    private static ExportEntry FindFace(IMEPackage package, string facePath) =>
        package.FindExport(facePath, "BioMorphFace")
        ?? throw new InvalidDataException($"BioMorphFace '{facePath}' was not found in the package.");

    private static IMEPackage OpenPackage(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var path = Path.GetFullPath(packagePath);
        return File.Exists(path)
            ? MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true)
            : throw new FileNotFoundException("The PCC does not exist.", path);
    }

    private static void EnsureSupportedGame(IMEPackage package)
    {
        if (package.Game is not (MEGame.LE1 or MEGame.LE2 or MEGame.LE3))
        {
            throw new InvalidDataException($"BioMorphFace operations do not support {package.Game} packages.");
        }
    }

    private static MEGame ToMeGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => MEGame.LE1,
        MorphFaceGame.LE2 => MEGame.LE2,
        MorphFaceGame.LE3 => MEGame.LE3,
        _ => throw new InvalidDataException($"Unsupported source game '{game}'.")
    };

    private static AssetIdentity? ToIdentity(IEntry? entry) => entry is null
        ? null
        : new AssetIdentity(
            Path.GetFullPath(entry.FileRef.FilePath),
            entry.InstancedFullPath,
            entry.UIndex,
            entry.ClassName,
            entry is ImportEntry);

    private static StructProperty VectorProperty(Vector3 value, string name) => new(
        "Vector",
        [
            new FloatProperty(value.X, "X"),
            new FloatProperty(value.Y, "Y"),
            new FloatProperty(value.Z, "Z")
        ],
        name,
        true);

    private static StructProperty LinearColorProperty(Vector4 value, string name) => new(
        "LinearColor",
        [
            new FloatProperty(value.X, "R"),
            new FloatProperty(value.Y, "G"),
            new FloatProperty(value.Z, "B"),
            new FloatProperty(value.W, "A")
        ],
        name,
        true);

    private static string Name(StructProperty property, string field) =>
        property.GetProp<NameProperty>(field)?.Value.Instanced ?? string.Empty;

    private static Vector3 ReadVector(StructProperty? property) =>
        property is null ? Vector3.Zero : CommonStructs.GetVector3(property);

    private static Vector4 ReadLinearColor(StructProperty? property) => property is null
        ? Vector4.Zero
        : new Vector4(
            property.GetProp<FloatProperty>("R")?.Value ?? 0,
            property.GetProp<FloatProperty>("G")?.Value ?? 0,
            property.GetProp<FloatProperty>("B")?.Value ?? 0,
            property.GetProp<FloatProperty>("A")?.Value ?? 0);

    private static bool HasDuplicateNames(IEnumerable<string> names)
    {
        var values = names.ToArray();
        return values.Any(string.IsNullOrWhiteSpace) ||
               values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private sealed record PendingResult(
        string FacePath,
        string MaterialOverridePath,
        MorphFaceMorphData MorphData,
        MorphFaceMaterialData MaterialData,
        IReadOnlyList<string>? Warnings = null);
}
