using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

public sealed record MorphFaceConversionRequest(
    string SourcePackagePath,
    string SourceFacePath,
    MorphFaceGame TargetGame,
    string DestinationPackagePath,
    bool CreateNewPackage,
    string? TemplatePackagePath);

public sealed record MorphFaceConversionResult(
    MorphFaceSaveResult SaveResult,
    string SourceProfile,
    string TargetProfile,
    int TransferredFeatureCount,
    int DroppedFeatureCount,
    int ScalarCount,
    int VectorCount,
    int TextureCount,
    bool SourceGeometryValidated);

/// <summary>
/// Converts semantic face authoring state between game profiles while using a
/// real destination-game face as the package/dependency template.
/// </summary>
public sealed class MorphFaceConversionService(
    MorphFaceProfileRegistry profiles,
    MorphTargetCatalog targets,
    MorphFacePackageWriter writer,
    MorphFacePackageContextService packageContext)
{
    public MorphFaceConversionResult Convert(MorphFaceConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourcePath = RequirePackage(request.SourcePackagePath, "source");
        var destinationPath = Path.GetFullPath(request.DestinationPackagePath);
        var templatePath = request.CreateNewPackage
            ? RequirePackage(request.TemplatePackagePath, "template")
            : RequirePackage(destinationPath, "destination");
        if (request.CreateNewPackage && File.Exists(destinationPath))
        {
            throw new IOException("The new conversion destination already exists.");
        }

        using var reader = new MorphFacePackageReader();
        var source = reader.Load(sourcePath, request.SourceFacePath);
        var sourceProfile = profiles.Require(
            source.Game,
            source.Document.Source.InstancedPath,
            source.Document.BaseHeadReference?.InstancedPath);
        EnsureSupportedDirection(source.Game, request.TargetGame);
        var species = SpeciesForProfile(sourceProfile.Key);
        var targetProfile = profiles.Profiles.SingleOrDefault(profile =>
                                profile.Game == request.TargetGame &&
                                string.Equals(SpeciesForProfile(profile.Key), species, StringComparison.OrdinalIgnoreCase))
                            ?? throw new NotSupportedException(
                                $"No {request.TargetGame} conversion profile exists for {sourceProfile.DisplayName}.");

        var (templateReference, template) = FindEditableTemplate(reader, templatePath, targetProfile);
        var sourceSession = CreateSession(source, sourceProfile, sourcePath);
        var transfer = sourceSession.CaptureSemanticTransferData();
        var targetSession = CreateSession(template, targetProfile, templatePath);
        targetSession.ApplySemanticTransferData(transfer);
        var convertedDraft = targetSession.CreateDraft(
            hairMeshReference: null,
            otherMeshReferences: [],
            materialOverrides: MorphFaceMaterialOverrides.Empty);
        var convertedMorph = new MorphFaceMorphData(
            convertedDraft.MorphFeatures,
            convertedDraft.FinalSkeleton,
            convertedDraft.BakedLods);
        var sourceMaterial = new MorphFaceMaterialData(
            source.Document.MaterialOverrides.Scalars,
            source.Document.MaterialOverrides.Vectors,
            source.Document.MaterialOverrides.Textures);
        var support = GetBaseMaterialSupport(template);
        var objectName = ChooseObjectName(
            source.Document.Source.InstancedPath.Split('.').Last(),
            request.TargetGame,
            templateReference.FacePath,
            request.CreateNewPackage ? [] : MorphFaceReferenceInspector.Inspect(destinationPath));

        MorphFaceSaveResult saveResult;
        MorphFaceMaterialData mappedMaterial;
        if (request.CreateNewPackage)
        {
            (saveResult, mappedMaterial) = CreateNewPackage(
                convertedDraft,
                templatePath,
                destinationPath,
                objectName,
                sourceMaterial,
                support,
                source.Game,
                sourceProfile.Key,
                sourcePath);
        }
        else
        {
            var transferred = packageContext.CloneMorphWithTransferredData(
                destinationPath,
                templateReference.FacePath,
                objectName,
                convertedMorph,
                sourceMaterial,
                support.Scalars,
                support.Vectors,
                support.Textures,
                source.Game,
                sourceProfile.Key,
                sourcePath,
                templatePath,
                clearAttachmentReferences: true);
            saveResult = transferred.SaveResult;
            mappedMaterial = transferred.MaterialData;
        }

        var sourceNonZero = transfer.MorphFeatures
            .Where(value => value.Offset != 0)
            .Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetNames = targetSession.Features.Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MorphFaceConversionResult(
            saveResult,
            sourceProfile.DisplayName,
            targetProfile.DisplayName,
            sourceNonZero.Count(targetNames.Contains),
            sourceNonZero.Count(name => !targetNames.Contains(name)),
            mappedMaterial.Scalars.Count,
            mappedMaterial.Vectors.Count,
            mappedMaterial.Textures.Count,
            sourceSession.CanEdit);
    }

    private (MorphFaceSaveResult SaveResult, MorphFaceMaterialData Material) CreateNewPackage(
        MorphFaceDocument draft,
        string templatePath,
        string destinationPath,
        string objectName,
        MorphFaceMaterialData sourceMaterial,
        MaterialSupport support,
        MorphFaceGame sourceGame,
        string sourceProfileKey,
        string sourcePackagePath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
                                   ?? throw new InvalidOperationException("The destination has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.conversion.tmp.pcc");
        try
        {
            var exported = writer.SaveMorphToPackage(
                draft,
                templatePath,
                temporaryPath,
                createNewPackage: true,
                destinationObjectName: objectName);
            var transferred = packageContext.PasteTransferredMaterialData(
                temporaryPath,
                exported.FaceInstancedPath,
                sourceMaterial,
                support.Scalars,
                support.Vectors,
                support.Textures,
                sourceGame,
                sourceProfileKey,
                sourcePackagePath,
                templatePath);
            if (File.Exists(destinationPath))
            {
                throw new IOException("The new conversion destination was created by another process.");
            }
            File.Move(temporaryPath, destinationPath);
            return (transferred.SaveResult with
            {
                PackagePath = destinationPath,
                Warnings = exported.Warnings.Concat(transferred.SaveResult.Warnings).ToArray()
            }, transferred.MaterialData);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private (MorphFaceMeshReferences Reference, LoadedMorphFace Loaded) FindEditableTemplate(
        MorphFacePackageReader reader,
        string templatePath,
        MorphFaceProfile profile)
    {
        var references = MorphFaceReferenceInspector.Inspect(templatePath);
        var game = references.FirstOrDefault()?.Game ?? ReadPackageGame(templatePath);
        if (game != profile.Game)
        {
            throw new InvalidDataException(
                $"The template/destination PCC is {game}, but this conversion requires {profile.Game}.");
        }

        var candidates = references
            .Where(reference => profiles.Find(
                reference.Game,
                reference.FacePath,
                reference.BaseHeadPath)?.Key == profile.Key)
            .Where(reference => profile.GeometryEditBlockReason(reference.BaseHeadPath) is null)
            .OrderBy(reference => reference.FacePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var reference in candidates)
        {
            try
            {
                var loaded = reader.Load(templatePath, reference.FaceUIndex.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                var session = CreateSession(loaded, profile, templatePath);
                if (session.CanEdit)
                {
                    return (reference, loaded);
                }
            }
            catch
            {
                // A package can contain stale or unresolved faces. Continue to
                // another same-profile candidate before reporting no template.
            }
        }
        throw new InvalidOperationException(
            $"'{templatePath}' contains no editable {profile.DisplayName} BioMorphFace to use as a conversion template.");
    }

    private MorphFaceEditingSession CreateSession(
        LoadedMorphFace loaded,
        MorphFaceProfile profile,
        string packagePath) => new(
        loaded.Document,
        loaded.BaseHead,
        targets.Load(profile, loaded.Game, packagePath),
        profile.MetadataOnlyFeatures,
        profile.DisplayName,
        profile.FeatureAliases,
        profile.RecognizesBaseVariant,
        profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath));

    private static MaterialSupport GetBaseMaterialSupport(LoadedMorphFace loaded)
    {
        var baseKeys = loaded.BaseHead.RenderData?.MaterialSlots
            .Where(identity => identity is not null)
            .Select(identity => MaterialIdentityKey.Create(identity!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var materials = loaded.Materials.Materials
            .Where(pair => baseKeys.Count == 0 || baseKeys.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
        return new MaterialSupport(
            materials.SelectMany(material => material.SupportedScalars.Count == 0
                    ? material.Scalars.Keys
                    : material.SupportedScalars)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            materials.SelectMany(material => material.SupportedVectors.Count == 0
                    ? material.Vectors.Keys
                    : material.SupportedVectors)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            materials.SelectMany(material => material.SupportedTextures.Count == 0
                    ? material.Textures.Keys
                    : material.SupportedTextures)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static string ChooseObjectName(
        string sourceName,
        MorphFaceGame targetGame,
        string templateFacePath,
        IReadOnlyList<MorphFaceMeshReferences> existing)
    {
        var sanitized = SanitizeObjectName(sourceName);
        var separator = templateFacePath.LastIndexOf('.');
        var parent = separator < 0 ? string.Empty : templateFacePath[..separator];
        var names = existing
            .Where(reference => string.Equals(
                reference.FacePath.Contains('.') ? reference.FacePath[..reference.FacePath.LastIndexOf('.')] : string.Empty,
                parent,
                StringComparison.OrdinalIgnoreCase))
            .Select(reference => reference.FacePath.Split('.').Last())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(sanitized))
        {
            return sanitized;
        }
        var stem = $"{sanitized}_{targetGame}";
        if (!names.Contains(stem))
        {
            return stem;
        }
        for (var number = 2; ; number++)
        {
            var candidate = $"{stem}_{number}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeObjectName(string value)
    {
        var sanitized = new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "ConvertedMorph";
        }
        return char.IsDigit(sanitized[0]) ? $"Morph_{sanitized}" : sanitized;
    }

    private static string RequirePackage(string? path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"A {purpose} PCC is required.");
        }
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException($"The {purpose} PCC was not found.", fullPath);
    }

    private static MorphFaceGame ReadPackageGame(string path) =>
        MorphFaceReferenceInspector.Inspect(path).FirstOrDefault()?.Game ?? MorphFaceGame.Unsupported;

    private static void EnsureSupportedDirection(MorphFaceGame source, MorphFaceGame target)
    {
        var supported = source == MorphFaceGame.LE3
            ? target is MorphFaceGame.LE1 or MorphFaceGame.LE2
            : source is MorphFaceGame.LE1 or MorphFaceGame.LE2 && target == MorphFaceGame.LE3;
        if (!supported)
        {
            throw new InvalidOperationException($"Conversion from {source} to {target} is not supported.");
        }
    }

    private static string SpeciesForProfile(string profileKey)
    {
        var separator = profileKey.IndexOf('-');
        return separator > 0 ? profileKey[(separator + 1)..] : profileKey;
    }

    private sealed record MaterialSupport(
        IReadOnlySet<string> Scalars,
        IReadOnlySet<string> Vectors,
        IReadOnlySet<string> Textures);
}
