using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using BinaryMorphFace = LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed record MorphFaceSaveResult(
    string PackagePath,
    string FaceInstancedPath,
    string MaterialOverrideInstancedPath,
    int LodCount,
    int TextureReferenceCount)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Writes BioMorphFace data through verified temporary package copies. Both
/// dependency-port exports and existing-export updates use atomic install.
/// </summary>
public sealed class MorphFacePackageWriter
{
    private const float FloatTolerance = 0.000001f;

    public MorphFaceSaveResult SaveMorphToPackage(
        MorphFaceDocument draft,
        string logicalSourcePackagePath,
        string destinationPath,
        bool createNewPackage,
        string? destinationObjectName = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalSourcePackagePath);
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

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        try
        {
            string facePath;
            string overridePath;
            List<string> warnings;
            var sourceBytes = File.ReadAllBytes(sourcePath);
            using (var sourceStream = new MemoryStream(sourceBytes, writable: false))
            using (var sourcePackage = MEPackageHandler.OpenMEPackageFromStream(sourceStream, logicalSourcePath))
            {
                EnsureSupportedGame(sourcePackage);
                var sourceFace = FindExport(sourcePackage, draft.Source.InstancedPath, "BioMorphFace");
                using var destinationPackage = createNewPackage
                    ? MEPackageHandler.CreateMemoryEmptyPackage(destination, sourcePackage.Game)
                    : MEPackageHandler.OpenMEPackage(destination, forceLoadFromDisk: true);
                EnsureSupportedGame(destinationPackage);
                if (destinationPackage.Game != sourcePackage.Game)
                {
                    throw new InvalidDataException(
                        $"The destination is {destinationPackage.Game}, but the selected morph is {sourcePackage.Game}.");
                }
                if (destinationPackage.FindEntry(destinationFacePath) is not null)
                {
                    throw new InvalidOperationException(
                        $"The destination already contains an entry named '{destinationFacePath}'.");
                }

                var issues = EntryExporter.ExportExportToPackage(sourceFace, destinationPackage, out var portedEntry);
                warnings = issues.Select(issue => issue.Message).ToList();
                if (portedEntry is not ExportEntry portedFace ||
                    !string.Equals(portedFace.ClassName, "BioMorphFace", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("LEC did not produce a BioMorphFace export in the destination.");
                }
                if (destinationObjectName is not null)
                {
                    portedFace.ObjectName = new NameReference(destinationObjectName);
                }
                if (!string.Equals(portedFace.InstancedFullPath, destinationFacePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"The exported morph path changed from '{destinationFacePath}' to '{portedFace.InstancedFullPath}'.");
                }

                WriteFace(destinationPackage, portedFace, draft);
                var materialOverride = ResolveMaterialOverride(portedFace);
                WriteMaterialOverride(destinationPackage, materialOverride, draft.MaterialOverrides, warnings);
                facePath = portedFace.InstancedFullPath;
                overridePath = materialOverride.InstancedFullPath;
                destinationPackage.Save(temporaryPath);
            }

            Verify(temporaryPath, facePath, draft);
            if (PackageFingerprint.Capture(sourcePath) != draft.SourceFingerprint)
            {
                throw new IOException("The source PCC changed while the morph package was being written. Nothing was replaced.");
            }
            if (destinationFingerprint is null && File.Exists(destination) ||
                destinationFingerprint is not null &&
                (!File.Exists(destination) || PackageFingerprint.Capture(destination) != destinationFingerprint))
            {
                throw new IOException("The destination changed while the morph package was being written. Nothing was replaced.");
            }

            AtomicReplace(temporaryPath, destination);
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
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Updates the loaded BioMorphFace and its existing BioMaterialOverride in
    /// an adjacent verified copy, then atomically replaces the open PCC.
    /// </summary>
    public MorphFaceSaveResult SaveExisting(MorphFaceDocument draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        LegendaryExplorerCoreRuntime.Initialize();
        var packagePath = Path.GetFullPath(draft.Source.PackagePath);
        ValidateDraft(draft, packagePath);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(packagePath)!,
            $".{Path.GetFileName(packagePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.Copy(packagePath, temporaryPath, overwrite: false);
            string overridePath;
            var warnings = new List<string>();
            using (var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true))
            {
                EnsureSupportedGame(package);
                var face = FindExport(package, draft.Source.InstancedPath, "BioMorphFace");
                WriteFace(package, face, draft);
                var materialOverride = ResolveMaterialOverride(face);
                WriteMaterialOverride(package, materialOverride, draft.MaterialOverrides, warnings);
                overridePath = materialOverride.InstancedFullPath;
                package.Save(temporaryPath);
            }

            Verify(temporaryPath, draft.Source.InstancedPath, draft);
            if (PackageFingerprint.Capture(packagePath) != draft.SourceFingerprint)
            {
                throw new IOException("The open PCC changed while the face was being written. Nothing was replaced.");
            }

            AtomicReplace(temporaryPath, packagePath);
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
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteFace(IMEPackage package, ExportEntry face, MorphFaceDocument draft)
    {
        var properties = face.GetProperties();
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            draft.MorphFeatures.Select(value => new StructProperty(
                "MorphFeature",
                false,
                new NameProperty(value.Name, "sFeatureName"),
                new FloatProperty(value.Offset, "Offset"))),
            "m_aMorphFeatures"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            draft.FinalSkeleton.Select(value => new StructProperty(
                "OffsetBonePos",
                false,
                new NameProperty(value.BoneName, "nName"),
                VectorProperty(value.Translation, "vPos"))),
            "m_aFinalSkeleton"));

        properties.AddOrReplaceProp(new ObjectProperty(
            ResolveDraftEntry(package, draft.BaseHeadReference, "SkeletalMesh", required: true),
            "m_oBaseHead"));
        WriteAttachmentReferences(package, properties, draft);

        var binary = new BinaryMorphFace
        {
            LODs = draft.BakedLods.Select(lod => lod.ToArray()).ToArray()
        };
        face.WritePropertiesAndBinary(properties, binary);
    }

    private static void WriteMaterialOverride(
        IMEPackage package,
        ExportEntry materialOverride,
        MorphFaceMaterialOverrides overrides,
        ICollection<string> warnings)
    {
        var properties = materialOverride.GetProperties();
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            overrides.Scalars.Select(value => new StructProperty(
                "ScalarParameter",
                false,
                new NameProperty(value.Name, "nName"),
                new FloatProperty(value.Value, "sValue"))),
            "m_aScalarOverrides"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            overrides.Vectors.Select(value => new StructProperty(
                "ColorParameter",
                false,
                new NameProperty(value.Name, "nName"),
                LinearColorProperty(value.Value, "cValue"))),
            "m_aColorOverrides"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            overrides.Textures.Select(value => new StructProperty(
                "TextureParameter",
                [
                    new NameProperty(value.Name, "nName"),
                    new ObjectProperty(
                        ResolveTextureDraftEntry(package, value.TextureReference, warnings),
                        "m_pTexture")
                ])),
            "m_aTextureOverrides"));
        materialOverride.WriteProperties(properties);
    }

    private static void Verify(string packagePath, string facePath, MorphFaceDocument expected)
    {
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var face = FindExport(package, facePath, "BioMorphFace");
        var properties = face.GetProperties();
        var binary = face.GetBinaryData<BinaryMorphFace>();
        IReadOnlyCollection<StructProperty> features = properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")?.ToArray() ?? [];
        IReadOnlyCollection<StructProperty> bones = properties.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton")?.ToArray() ?? [];
        VerifyFeatures(features, expected.MorphFeatures);
        VerifyBones(bones, expected.FinalSkeleton);
        VerifyLods(binary.LODs ?? [], expected.BakedLods);

        var baseHead = properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package);
        VerifyReference(baseHead, expected.BaseHeadReference, "base head");
        VerifyHairReference(package, properties, expected);

        var materialOverride = ResolveMaterialOverride(face);
        VerifyMaterialOverrides(package, materialOverride, expected.MaterialOverrides);
    }

    private static void VerifyMaterialOverrides(
        IMEPackage package,
        ExportEntry export,
        MorphFaceMaterialOverrides expected)
    {
        var properties = export.GetProperties();
        IReadOnlyCollection<StructProperty> scalars = properties.GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides")?.ToArray() ?? [];
        IReadOnlyCollection<StructProperty> vectors = properties.GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides")?.ToArray() ?? [];
        IReadOnlyCollection<StructProperty> textures = properties.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides")?.ToArray() ?? [];
        if (scalars.Count != expected.Scalars.Count || vectors.Count != expected.Vectors.Count || textures.Count != expected.Textures.Count)
        {
            throw new InvalidDataException("Material override counts changed during package round-trip verification.");
        }

        foreach (var value in expected.Scalars)
        {
            var actual = scalars.Single(item => Name(item, "nName") == value.Name)
                .GetProp<FloatProperty>("sValue")?.Value ?? float.NaN;
            EnsureClose(actual, value.Value, $"scalar {value.Name}");
        }
        foreach (var value in expected.Vectors)
        {
            var actual = ReadLinearColor(vectors.Single(item => Name(item, "nName") == value.Name)
                .GetProp<StructProperty>("cValue"));
            EnsureClose(actual.X, value.Value.X, $"colour {value.Name}.R");
            EnsureClose(actual.Y, value.Value.Y, $"colour {value.Name}.G");
            EnsureClose(actual.Z, value.Value.Z, $"colour {value.Name}.B");
            EnsureClose(actual.W, value.Value.W, $"colour {value.Name}.A");
        }
        foreach (var value in expected.Textures)
        {
            var actual = textures.Single(item => Name(item, "nName") == value.Name)
                .GetProp<ObjectProperty>("m_pTexture")?.ResolveToEntry(package);
            VerifyReference(actual, value.TextureReference, $"texture {value.Name}");
        }
    }

    private static void WriteAttachmentReferences(
        IMEPackage package,
        PropertyCollection properties,
        MorphFaceDocument draft)
    {
        if (draft.HairMeshReference is null)
        {
            properties.RemoveNamedProperty("m_oHairMesh");
        }
        else
        {
            properties.AddOrReplaceProp(new ObjectProperty(
                ResolveDraftEntry(package, draft.HairMeshReference, "SkeletalMesh", required: true),
                "m_oHairMesh"));
        }

        var otherMeshes = draft.OtherMeshReferences
            .Where(reference => reference is not null)
            .Select(reference => new ObjectProperty(
                ResolveDraftEntry(package, reference, "SkeletalMesh", required: true)))
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

    private static void VerifyFeatures(
        IReadOnlyCollection<StructProperty> actual,
        IReadOnlyList<MorphFeatureValue> expected)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidDataException("Morph feature count changed during package round-trip verification.");
        }
        foreach (var value in expected)
        {
            var stored = actual.Single(item => Name(item, "sFeatureName") == value.Name)
                .GetProp<FloatProperty>("Offset")?.Value ?? float.NaN;
            EnsureClose(stored, value.Offset, $"feature {value.Name}");
        }
    }

    private static void VerifyBones(
        IReadOnlyCollection<StructProperty> actual,
        IReadOnlyList<BoneTranslation> expected)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidDataException("Final skeleton count changed during package round-trip verification.");
        }
        foreach (var value in expected)
        {
            var stored = ReadVector(actual.Single(item => Name(item, "nName") == value.BoneName)
                .GetProp<StructProperty>("vPos"));
            EnsureClose(stored.X, value.Translation.X, $"bone {value.BoneName}.X");
            EnsureClose(stored.Y, value.Translation.Y, $"bone {value.BoneName}.Y");
            EnsureClose(stored.Z, value.Translation.Z, $"bone {value.BoneName}.Z");
        }
    }

    private static void VerifyLods(Vector3[][] actual, IReadOnlyList<Vector3[]> expected)
    {
        if (actual.Length != expected.Count)
        {
            throw new InvalidDataException("Baked LOD count changed during package round-trip verification.");
        }
        for (var lod = 0; lod < actual.Length; lod++)
        {
            if (actual[lod].Length != expected[lod].Length)
            {
                throw new InvalidDataException($"Baked LOD {lod} vertex count changed during package round-trip verification.");
            }
            for (var vertex = 0; vertex < actual[lod].Length; vertex++)
            {
                var left = actual[lod][vertex];
                var right = expected[lod][vertex];
                if (Vector3.Distance(left, right) > FloatTolerance)
                {
                    throw new InvalidDataException($"Baked LOD {lod} vertex {vertex} changed during package round-trip verification.");
                }
            }
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
        if (identity.IsImport &&
            package.FindEntry(identity.InstancedPath, "Texture2D") is { } preservedImport)
        {
            return preservedImport;
        }
        return package.FindExport(identity.InstancedPath, "Texture2D")
               ?? ExternalTextureMaterializer.Materialize(package, identity, warnings);
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
        if (draft.MorphFeatures.Any(value => string.IsNullOrWhiteSpace(value.Name) || !float.IsFinite(value.Offset)) ||
            draft.MorphFeatures.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != draft.MorphFeatures.Count)
        {
            throw new InvalidDataException("Morph features must have unique names and finite values.");
        }
        if (draft.FinalSkeleton.Any(value => string.IsNullOrWhiteSpace(value.BoneName) || !IsFinite(value.Translation)) ||
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

    private static void AtomicReplace(string temporaryPath, string destination)
    {
        if (File.Exists(destination))
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
        else
        {
            File.Move(temporaryPath, destination);
        }
    }

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

    private static void EnsureClose(float actual, float expected, string label)
    {
        if (!float.IsFinite(actual) || Math.Abs(actual - expected) > FloatTolerance)
        {
            throw new InvalidDataException($"{label} changed during package round-trip verification.");
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
