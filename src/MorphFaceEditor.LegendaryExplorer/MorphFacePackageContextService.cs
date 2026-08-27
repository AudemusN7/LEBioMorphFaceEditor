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
            AtomicReplace(temporaryPath, path);
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
        MorphFaceMorphData data)
    {
        ValidateObjectName(objectName);
        ValidateMorphData(data);
        return Mutate(packagePath, package =>
        {
            var source = FindFace(package, facePath);
            EnsureNameAvailable(package, source.Parent, objectName);
            EnsureCompatibleLods(ReadMorphData(source), data);
            package.FindNameOrAdd(objectName);
            var clone = EntryCloner.CloneTree(source);
            clone.ObjectName = new NameReference(objectName);
            var materialOverride = EnsureIndependentMaterialOverride(clone);
            materialOverride.ObjectName = new NameReference(
                materialOverride.ObjectName.Name,
                materialOverride.ObjectName.Number + 1);
            WriteMorphData(clone, data);
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
        bool clearAttachmentReferences)
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
                targetTemplatePackagePath);
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
                transfer.MaterialData,
                transfer.Warnings);
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

    public void ExportRon(string packagePath, string facePath, string destinationPath)
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
            ReadMaterialData(ResolveMaterialOverride(face))));
    }

    public MorphFaceSaveResult ImportRon(
        string packagePath,
        string templateFacePath,
        string objectName,
        string sourcePath) => ImportHeadMorph(packagePath, templateFacePath, objectName, sourcePath);

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
        bool mergeWithTemplate)
    {
        ValidateMorphData(ron.MorphData);
        ValidateMaterialData(ron.MaterialData);
        return Mutate(packagePath, package =>
        {
            var source = FindFace(package, templateFacePath);
            EnsureNameAvailable(package, source.Parent, objectName);
            EnsureCompatibleLods(ReadMorphData(source), morphData);
            package.FindNameOrAdd(objectName);
            var clone = EntryCloner.CloneTree(source);
            clone.ObjectName = new NameReference(objectName);
            var materialOverride = EnsureIndependentMaterialOverride(clone);
            materialOverride.ObjectName = new NameReference(
                materialOverride.ObjectName.Name,
                materialOverride.ObjectName.Number + 1);

            var templateMaterial = ReadMaterialData(ResolveMaterialOverride(source));
            var resolvedMaterial = mergeWithTemplate
                ? MergeLegacyMaterials(package, templateMaterial, ron.MaterialData)
                : ResolveRonMaterials(package, ron.MaterialData);
            WriteMorphData(clone, morphData);
            WriteMaterialData(package, materialOverride, resolvedMaterial);
            if (mergeWithTemplate)
            {
                WriteLegacyMeshReferences(package, clone, ron);
            }
            else
            {
                WriteRonMeshReferences(package, clone, ron);
            }
            return new PendingResult(
                clone.InstancedFullPath,
                materialOverride.InstancedFullPath,
                morphData,
                resolvedMaterial);
        });
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
        string? targetTemplatePackagePath)
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
                targetTemplatePackagePath);
            var face = FindFace(package, facePath);
            var materialOverride = ResolveMaterialOverride(face);
            WriteMaterialData(package, materialOverride, transfer.MaterialData);
            return new PendingResult(
                face.InstancedFullPath,
                materialOverride.InstancedFullPath,
                ReadMorphData(face),
                transfer.MaterialData,
                transfer.Warnings);
        });
        return new TransferredMaterialSaveResult(saveResult, transfer!.MaterialData);
    }

    private static MorphFaceSaveResult Mutate(
        string packagePath,
        Func<IMEPackage, PendingResult> mutation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(mutation);
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
            PendingResult pending;
            using (var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true))
            {
                EnsureSupportedGame(package);
                pending = mutation(package);
                package.Save(temporaryPath);
            }

            Verify(temporaryPath, pending);
            if (PackageFingerprint.Capture(path) != originalFingerprint)
            {
                throw new IOException("The open PCC changed while the operation was being written. Nothing was replaced.");
            }

            AtomicReplace(temporaryPath, path);
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

    private static MorphFaceMaterialData ResolveRonMaterials(
        IMEPackage package,
        MorphFaceMaterialData materialData)
    {
        var textures = materialData.Textures.Select(value =>
        {
            if (value.TextureReference is null)
            {
                return value;
            }
            var entry = package.FindEntry(value.TextureReference.InstancedPath, "Texture2D")
                        ?? throw new InvalidDataException(
                            $"RON Texture2D '{value.TextureReference.InstancedPath}' is not present in the open PCC.");
            return value with { TextureReference = ToIdentity(entry) };
        }).ToArray();
        return materialData with { Textures = textures };
    }

    private static MorphFaceMaterialData MergeLegacyMaterials(
        IMEPackage package,
        MorphFaceMaterialData template,
        MorphFaceMaterialData legacy)
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
            var resolved = FindCompatibleEntry(package, value.TextureReference.InstancedPath, "Texture2D");
            if (resolved is not null)
            {
                convertedTextures.Add(value with { TextureReference = ToIdentity(resolved) });
            }
            else if (templateTextures.TryGetValue(value.Name, out var fallback))
            {
                convertedTextures.Add(fallback);
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
        TseHeadMorph legacy)
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
        }

        var accessories = legacy.AccessoryMeshes
            .Select(path => FindCompatibleEntry(package, path, "SkeletalMesh"))
            .Where(entry => entry is not null)
            .Cast<IEntry>()
            .Select(entry => new ObjectProperty(entry))
            .ToArray();
        if (legacy.AccessoryMeshes.Count == 0)
        {
            properties.RemoveNamedProperty("m_oOtherMeshes");
        }
        else if (accessories.Length > 0)
        {
            properties.AddOrReplaceProp(new ArrayProperty<ObjectProperty>(accessories, "m_oOtherMeshes"));
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

    private static void WriteRonMeshReferences(IMEPackage package, ExportEntry face, TseHeadMorph ron)
    {
        var properties = face.GetProperties();
        if (string.IsNullOrWhiteSpace(ron.HairMesh) ||
            string.Equals(ron.HairMesh, "None", StringComparison.OrdinalIgnoreCase))
        {
            properties.RemoveNamedProperty("m_oHairMesh");
        }
        else
        {
            var hair = package.FindEntry(ron.HairMesh, "SkeletalMesh")
                       ?? throw new InvalidDataException(
                           $"RON hair mesh '{ron.HairMesh}' is not present in the open PCC.");
            properties.AddOrReplaceProp(new ObjectProperty(hair, "m_oHairMesh"));
        }

        var accessories = ron.AccessoryMeshes.Select(path =>
            new ObjectProperty(package.FindEntry(path, "SkeletalMesh")
                ?? throw new InvalidDataException(
                    $"RON accessory mesh '{path}' is not present in the open PCC."))).ToArray();
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

    private static void Verify(string packagePath, PendingResult expected)
    {
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
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

    private static void AtomicReplace(string temporaryPath, string destination)
    {
        var backup = $"{destination}.{Guid.NewGuid():N}.backup";
        try
        {
            File.Replace(temporaryPath, destination, backup, ignoreMetadataErrors: true);
            File.Delete(backup);
        }
        catch
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, destination, overwrite: true);
                File.Delete(backup);
            }
            throw;
        }
    }

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
