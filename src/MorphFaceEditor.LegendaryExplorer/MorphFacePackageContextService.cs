using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using static MorphFaceEditor.LegendaryExplorer.MorphFacePackageCodec;

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
    private readonly PlayerRuntimeAssetValidator _playerAssets;

    public MorphFacePackageContextService() : this(new PlayerRuntimeAssetValidator()) { }
    internal MorphFacePackageContextService(PlayerRuntimeAssetValidator playerAssets) => _playerAssets = playerAssets;

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
        using (var save = new FileSaveTransaction(destination, null, ".conversion.tmp.pcc"))
        {
            var temporaryPath = save.CandidatePath;
            TextureTransferResult transfer;
            AttachmentTransferResult attachments;
            IReadOnlyList<string> stagingWarnings;
            string facePath;
            string materialPath;
            PccDependencyGraphSnapshot dependencyGraph;
            using (var template = OpenPackage(templatePath))
            using (var package = MEPackageHandler.CreateMemoryEmptyPackage(temporaryPath, template.Game))
            {
                PccPackageWorkflow.ConfigureNewFacePackage(package);
                var templateFace = FindFace(template, targetTemplateFacePath);
                NormalizeInvalidHmmMorphPackageAliases(template);
                var stagingMessages = new List<string>();
                var templateBaseHead = templateFace.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(template)
                                       ?? throw new InvalidDataException("The conversion template has no base head.");
                // Plan the template graph before importing it. The recursive LEC
                // exporter can otherwise create imports for identities which the
                // destination game's native material graph requires as exports.
                // Only the authored face container is relocated; dependencies
                // continue to use the shared workflow's canonical native paths.
                var face = PccPackageWorkflow.ImportDependencyGraph(
                    package,
                    templateFace,
                    package.CreatePackageExport("MorphFaceEditor"),
                    targetTextureCatalog,
                    warnings: stagingMessages,
                    requiredExportReferences: [templateBaseHead]);
                stagingWarnings = stagingMessages;
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
                WriteMaterialData(package, materialOverride, transfer.MaterialData);
                attachments = MorphFaceAttachmentTransferEngine.Transfer(
                    package,
                    sourceHair,
                    sourceOtherMeshes,
                    ToMeGame(sourceGame),
                    templatePath);

                var baseHead = draft.BaseHeadReference
                               ?? throw new InvalidDataException("The converted face has no target-game base head.");
                var stagedBaseHead = package.FindExport(baseHead.InstancedPath, "SkeletalMesh")
                                     ?? ExternalSkeletalMeshMaterializer.Materialize(
                                         package,
                                         baseHead,
                                         warnings: null);
                var properties = face.GetProperties();
                properties.AddOrReplaceProp(new ObjectProperty(stagedBaseHead, "m_oBaseHead"));
                properties.AddOrReplaceProp(new ObjectProperty(materialOverride, "m_oMaterialOverrides"));
                face.WriteProperties(properties);
                WriteMorphData(face, new MorphFaceMorphData(
                    draft.MorphFeatures, draft.FinalSkeleton, draft.BakedLods));
                WriteAttachments(face, attachments);
                PackageIntegrity.Verify(package);
                EnsureNoInvalidHmmMorphPackageAliases(package, "final verification");
                using var completedPackage = MEPackageHandler.CreateMemoryEmptyPackage(destination, package.Game);
                face = PccPackageWorkflow.CloneCompletedRoot(completedPackage, face, stagingMessages);
                materialOverride = ResolveMaterialOverride(face);
                facePath = face.InstancedFullPath;
                materialPath = materialOverride.InstancedFullPath;
                dependencyGraph = PccPackageWorkflow.CaptureDependencyGraph(face);
                completedPackage.Save(temporaryPath);
            }

            VerifyMinimalConvertedPackage(temporaryPath, facePath, materialPath, dependencyGraph);
            save.Commit("The new conversion destination was created by another process.");
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
                        .ToArray(),
                    UserWarnings = attachments.UserWarnings
                },
                transfer.MaterialData);
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

    public bool HasMorphReferences(string packagePath, string facePath)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        EnsureSupportedGame(package);
        var face = FindFace(package, facePath);
        return face.GetEntriesThatReferenceThisOne().Keys.Any(entry => !entry.IsDescendantOf(face));
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

        using (var save = new FileSaveTransaction(path, PackageFingerprint.Capture(path)))
        {
            var temporaryPath = save.CandidatePath;
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

            save.Commit("The open PCC changed while the morph was being deleted. Nothing was replaced.");
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
                transfer.Warnings.Concat(attachments.Warnings).ToArray(),
                attachments.UserWarnings);
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

    public PlayerRuntimeAssetAvailability FindAvailablePlayerAssets(MorphFaceGame game,
        IEnumerable<AssetIdentity> references, CancellationToken cancellationToken = default) =>
        _playerAssets.FindAvailable(game, references, cancellationToken);

    public void VerifyPlayerRuntimeAssets(MorphFaceGame game, MorphFaceMaterialData materials,
        string hairMesh = "None", IReadOnlyList<string>? accessoryMeshes = null) =>
        _playerAssets.Verify(game, new(hairMesh, accessoryMeshes ?? [], new([], [], []), materials with
        {
            Textures = materials.Textures.Select(value => value.TextureReference is { } texture
                ? value with { TextureReference = texture with { InstancedPath = TextureCatalogPicker.CanonicalPath(texture.InstancedPath, texture.PackagePath) } }
                : value).ToArray()
        }));

    public MorphFaceMaterialData CapturePlayerDefaultMaterials(MorphFaceGame game, StandalonePlayerSex sex) =>
        PreparePlayerDefaultMaterials(game, sex, CaptureMaterialData(
            StandalonePlayerMorphImportService.ResolveInstalledSeed(game),
            StandalonePlayerMorphImportService.ResolvePlayerTemplate(game, sex)));

    public void ExportPlayerDefaultsRon(MorphFaceGame game, StandalonePlayerSex sex, string destinationPath,
        RonExportProvenance? provenance = null)
        => TseHeadMorphRon.Write(destinationPath, CapturePlayerDefaultHead(game, sex), provenance);

    public HeadMorphPayload CapturePlayerDefaultHead(MorphFaceGame game, StandalonePlayerSex sex)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = OpenPackage(StandalonePlayerMorphImportService.ResolveInstalledSeed(game));
        var head = ReadHead(FindFace(package, StandalonePlayerMorphImportService.ResolvePlayerTemplate(game, sex)), false);
        head = head with { MaterialData = PreparePlayerDefaultMaterials(game, sex, head.MaterialData) };
        _playerAssets.Verify(game, head);
        return head;
    }

    private static MorphFaceMaterialData PreparePlayerDefaultMaterials(MorphFaceGame game, StandalonePlayerSex sex,
        MorphFaceMaterialData materials) => game == MorphFaceGame.LE2 && sex == StandalonePlayerSex.Female
        ? materials with { Textures = materials.Textures.Select(value =>
            value.Name.Equals("HED_Lash_Diff", StringComparison.OrdinalIgnoreCase) &&
            value.TextureReference?.InstancedPath.Equals("BIOG_Humanoid_MASTER_MTR_R.Human.HMF_HED_PROLash_Opac_M01",
                StringComparison.OrdinalIgnoreCase) == true
                ? value with { TextureReference = new AssetIdentity("", "BIOG_HMF_HED_PROMorph_R.Average.HMF_HED_PROLash_Opac_M01", 0, "Texture2D") }
                : value).ToArray() }
        : materials;

    public void ExportRon(
        string packagePath,
        string facePath,
        string destinationPath,
        RonExportProvenance? provenance = null,
        bool morphFeaturesOnly = false)
    {
        var head = CaptureHead(packagePath, facePath, morphFeaturesOnly);
        if (provenance is { PlayerMorph: true } && !morphFeaturesOnly)
            _playerAssets.Verify(provenance.Game, head);
        TseHeadMorphRon.Write(destinationPath, head, provenance, morphFeaturesOnly);
    }

    public HeadMorphPayload CaptureHead(string packagePath, string facePath, bool morphFeaturesOnly = false)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = OpenPackage(packagePath);
        return ReadHead(FindFace(package, facePath), morphFeaturesOnly);
    }

    private static HeadMorphPayload ReadHead(ExportEntry face, bool morphFeaturesOnly)
    {
        var package = face.FileRef;
        var properties = face.GetProperties();
        var hair = properties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package)?.InstancedFullPath
                   ?? "None";
        var accessories = properties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
            .Select(value => value.ResolveToEntry(package)?.InstancedFullPath)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray() ?? [];
        var data = ReadMorphData(face);
        var materials = morphFeaturesOnly ? new MorphFaceMaterialData([], [], []) : ReadMaterialData(ResolveMaterialOverride(face));
        materials = materials with { Textures = materials.Textures.Where(value =>
            !HeadMorphMaterialParameterPolicy.IsAttachmentOnlyTexture(value.Name)).ToArray() };
        return new HeadMorphPayload(
            morphFeaturesOnly ? "None" : hair,
            morphFeaturesOnly ? [] : accessories,
            morphFeaturesOnly ? new(data.MorphFeatures, [], []) : data,
            materials);
    }

    /// <summary>Replace a face in the staged PCC through the same verified mutation path as other edits.</summary>
    public MorphFaceSaveResult ReplaceHead(string packagePath, string facePath, HeadMorphPayload ron,
        bool player, StandalonePlayerAssetCatalog? assets = null)
    {
        ValidateMorphData(ron.MorphData);
        ValidateMaterialData(ron.MaterialData);
        return Mutate(packagePath, package =>
        {
            var face = FindFace(package, facePath);
            var previous = ReadMorphData(face);
            if (player) EnsureCompatibleLod0(previous, ron.MorphData);
            else EnsureCompatibleLods(previous, ron.MorphData);
            var material = EnsureIndependentMaterialOverride(face);
            var warnings = new List<string>();
            var userWarnings = new List<UserFacingMessage>();
            var resolved = ResolveRonMaterials(package, ron.MaterialData, assets, true, warnings, userWarnings);
            if (player)
                _playerAssets.Verify(package.Game switch
                {
                    MEGame.LE1 => MorphFaceGame.LE1,
                    MEGame.LE2 => MorphFaceGame.LE2,
                    MEGame.LE3 => MorphFaceGame.LE3,
                    _ => MorphFaceGame.Unsupported
                }, ron with { MaterialData = resolved });
            WriteMorphData(face, ron.MorphData);
            WriteMaterialData(package, material, resolved);
            WriteRonMeshReferences(package, face, ron, assets, true, warnings, !player, userWarnings);
            return new PendingResult(face.InstancedFullPath, material.InstancedFullPath, ron.MorphData, resolved, warnings, userWarnings);
        });
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
    public MorphFaceSaveResult ImportStandalonePlayerHead(
        string packagePath,
        string templateFacePath,
        string objectName,
        HeadMorphPayload head,
        StandalonePlayerAssetCatalog? assetCatalog = null)
    {
        ValidateObjectName(objectName);
        return ImportHeadMorph(
            packagePath,
            templateFacePath,
            objectName,
            head,
            head.MorphData,
            mergeWithTemplate: false,
            requireMatchingAllLods: false,
            assetCatalog: assetCatalog,
            strictAssetResolution: true,
            allowMissingAttachments: false);
    }

    /// <summary>Imports a proven NPC RON while applying the shared LE1/LE2 material transfer policy.</summary>
    public MorphFaceSaveResult ImportStandaloneNpcHead(
        string packagePath,
        string templateFacePath,
        string objectName,
        HeadMorphPayload ron,
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
            allowMissingAttachments: true,
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
        HeadMorphPayload ron,
        MorphFaceMorphData morphData,
        bool mergeWithTemplate,
        bool requireMatchingAllLods = true,
        StandalonePlayerAssetCatalog? assetCatalog = null,
        bool strictAssetResolution = false,
        NpcRonMaterialTransfer? npcMaterialTransfer = null,
        bool allowMissingAttachments = false,
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
            var userWarnings = new List<UserFacingMessage>();
            var resolvedMaterial = mergeWithTemplate
                ? MergeLegacyMaterials(package, templateMaterial, ron.MaterialData, warnings)
                : npcMaterialTransfer is null
                    ? ResolveRonMaterials(package, ron.MaterialData, assetCatalog, strictAssetResolution, warnings, userWarnings)
                    : TransferNpcRonMaterials(package, ron.MaterialData, npcMaterialTransfer,
                        assetCatalog, warnings, userWarnings);
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
                    allowMissingAttachments, userWarnings);
            }
            return new PendingResult(
                clone.InstancedFullPath,
                materialOverride.InstancedFullPath,
                morphData,
                resolvedMaterial,
                warnings, userWarnings);
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
                transfer.Warnings.Concat(attachments.Warnings).ToArray(),
                attachments.UserWarnings);
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
        var face = FindFace(package, facePath);
        if (face.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package) is not ExportEntry
            { ClassName: "SkeletalMesh" })
        {
            throw new InvalidDataException("The converted base head was not saved as a SkeletalMesh export.");
        }
        _ = package.FindExport(materialPath, "BioMaterialOverride")
            ?? throw new InvalidDataException("The converted material override was not saved.");
        PccPackageWorkflow.VerifyDependencyGraph(package, dependencyGraph, verifyCorpusRoles: false);
        PccPackageWorkflow.VerifyNoUnreachableEntries(face);
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
        using (var save = new FileSaveTransaction(path, originalFingerprint))
        {
            var temporaryPath = save.CandidatePath;
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
            save.Commit("The open PCC changed while the operation was being written. Nothing was replaced.",
                cancellationToken);
            return new MorphFaceSaveResult(
                path,
                pending.FacePath,
                pending.MaterialOverridePath,
                pending.MorphData.BakedLods.Count,
                pending.MaterialData.Textures.Count)
            {
                Warnings = pending.Warnings ?? [],
                UserWarnings = pending.UserWarnings ?? []
            };
        }
    }

    private static void WriteMaterialData(
        IMEPackage package,
        ExportEntry materialOverride,
        MorphFaceMaterialData data) =>
        MorphFacePackageCodec.WriteMaterialData(materialOverride, data, identity => ResolveTexture(package, identity));

    private static MorphFaceMaterialData TransferNpcRonMaterials(
        IMEPackage package,
        MorphFaceMaterialData source,
        NpcRonMaterialTransfer context,
        StandalonePlayerAssetCatalog? assetCatalog,
        ICollection<string> warnings,
        ICollection<UserFacingMessage> userWarnings)
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

        // Resolve untransferred overrides by exact identity. Missing optional
        // textures are omitted so the material can inherit its default.
        var mappedNames = mapped.MaterialData.Textures.Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untransferred = source.Textures.Where(value => !mappedNames.Contains(value.Name)).ToArray();
        var retained = ResolveRonMaterials(
            package,
            new MorphFaceMaterialData([], [], untransferred),
            assetCatalog,
            strictAssetResolution: true,
            warnings, userWarnings);
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
        ICollection<string> warnings,
        ICollection<UserFacingMessage> userWarnings)
    {
        var textures = new List<TextureMaterialOverride>();
        foreach (var value in materialData.Textures)
        {
            if (value.TextureReference is null)
            {
                textures.Add(value);
                continue;
            }
            var path = value.TextureReference.InstancedPath;
            var existing = PackageIntegrity.FindExactEntry(package, path, "Texture2D");
            IEntry? entry = existing as ExportEntry ?? (!strictAssetResolution ? existing : null);
            if (entry is null)
            {
                try
                {
                    entry = ResolveTextureCandidate(package, path, assetCatalog, existing as ImportEntry);
                }
                catch (UnresolvedRonTextureException exception)
                {
                    if (!strictAssetResolution) throw new InvalidDataException(exception.Message, exception);
                    AddSkippedRonAssetWarning(warnings, userWarnings, "loc_RonImport_TextureSkipped",
                        $"RON texture '{path}' for '{value.Name}' was omitted because it is unavailable; the material will use its inherited texture.",
                        value.Name, path);
                    continue;
                }
            }
            textures.Add(value with { TextureReference = ToIdentity(entry) });
        }
        return materialData with { Textures = textures.ToArray() };
    }

    private static void AddSkippedRonAssetWarning(ICollection<string> warnings,
        ICollection<UserFacingMessage> userWarnings, string key, string diagnostic, params object?[] arguments)
    {
        warnings.Add(diagnostic);
        userWarnings.Add(UserFacingMessage.CreateWarning(key, diagnostic, arguments));
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
        HeadMorphPayload legacy,
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
            throw new UnresolvedRonTextureException(
                $"RON Texture2D '{instancedPath}' is not present in the standalone seed and has no exact installed-game catalogue candidate.");
        }

        if (!File.Exists(candidate.PackagePath))
        {
            throw new UnresolvedRonTextureException(
                $"The installed-game texture donor package is missing: {candidate.PackagePath}");
        }
        if (existingImport is not null)
        {
            return existingImport;
        }
        return ExternalTextureMaterializer.Materialize(package, candidate);
    }

    private static IEntry? ResolveMeshCandidate(
        IMEPackage package,
        string instancedPath,
        StandalonePlayerAssetCatalog? assetCatalog,
        ImportEntry? existingImport,
        ICollection<string> warnings)
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
        using (var cache = new PackageCache { CacheMaxSize = 8 })
        {
            var donor = cache.GetCachedPackage(candidates[0].PackagePath);
            var source = donor?.GetEntry(candidates[0].UIndex) ?? donor?.FindEntry(instancedPath, "SkeletalMesh");
            var mesh = new GamePackageReferenceResolver(cache).Resolve(source);
            if (mesh is not null && !MorphFaceAttachmentTransferEngine.HasSupportedMaterials(mesh.FileRef, mesh, out var unsupported))
            {
                warnings.Add($"RON attachment '{instancedPath}' was omitted because {unsupported} is outside MFE's supported canonical materials.");
                return null;
            }
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
        HeadMorphPayload ron,
        StandalonePlayerAssetCatalog? assetCatalog,
        bool strictAssetResolution,
        ICollection<string> warnings,
        bool allowMissingAttachments,
        ICollection<UserFacingMessage> userWarnings)
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
                    hair = ResolveMeshCandidate(package, ron.HairMesh, assetCatalog, existing as ImportEntry, warnings);
                }
                catch (UnresolvedRonAttachmentException exception) when (strictAssetResolution)
                {
                    if (allowMissingAttachments)
                    {
                        properties.RemoveNamedProperty("m_oHairMesh");
                        AddSkippedRonAssetWarning(warnings, userWarnings, "loc_RonImport_HairSkipped",
                            $"RON hair mesh '{ron.HairMesh}' was omitted because it is unavailable in the selected game's installed assets: " +
                            exception.Message, ron.HairMesh);
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
            hair = RetainSupportedRonAttachment(hair, warnings);
            if (hair is not null)
            {
                properties.AddOrReplaceProp(new ObjectProperty(hair, "m_oHairMesh"));
            }
            else properties.RemoveNamedProperty("m_oHairMesh");
        }

        var accessories = ron.AccessoryMeshes.Select(path =>
        {
            var existing = package.FindEntry(path, "SkeletalMesh");
            IEntry? entry = existing as ExportEntry ?? (!strictAssetResolution ? existing : null);
            if (entry is null)
            {
                try
                {
                    entry = ResolveMeshCandidate(package, path, assetCatalog, existing as ImportEntry, warnings);
                }
                catch (UnresolvedRonAttachmentException exception) when (strictAssetResolution)
                {
                    if (allowMissingAttachments)
                    {
                        AddSkippedRonAssetWarning(warnings, userWarnings, "loc_RonImport_AccessorySkipped",
                            $"RON accessory mesh '{path}' was omitted because it is unavailable in the selected game's installed assets: " +
                            exception.Message, path);
                        return null;
                    }
                    throw new InvalidDataException(
                        $"RON attachment '{path}' is unavailable in the selected game's installed assets; " +
                        "the import was rejected because an unresolved SkeletalMesh cannot be exported safely.",
                        exception);
                }
            }
            entry = RetainSupportedRonAttachment(entry, warnings);
            return entry is null ? null : new ObjectProperty(entry);
        }).Where(property => property is not null).Cast<ObjectProperty>().ToArray();
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

    private static IEntry? RetainSupportedRonAttachment(IEntry? entry, ICollection<string> warnings)
    {
        if (entry is ExportEntry mesh &&
            !MorphFaceAttachmentTransferEngine.HasSupportedMaterials(mesh.FileRef, mesh, out var unsupported))
        {
            warnings.Add($"RON attachment '{entry.InstancedFullPath}' was omitted because {unsupported} is outside MFE's supported canonical materials.");
            return null;
        }
        return entry;
    }

    private sealed class UnresolvedRonTextureException(string message) : IOException(message);

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
        VerifyMorphData(ReadMorphData(face), expected.MorphData);
        VerifyMaterialData(ReadMaterialData(materialOverride), expected.MaterialData);
        if (!string.Equals(materialOverride.InstancedFullPath, expected.MaterialOverridePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The BioMaterialOverride path changed during package verification.");
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
        IReadOnlyList<string>? Warnings = null,
        IReadOnlyList<UserFacingMessage>? UserWarnings = null);
}
