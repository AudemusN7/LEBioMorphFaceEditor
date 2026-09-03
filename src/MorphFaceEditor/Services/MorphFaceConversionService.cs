using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

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
    MorphFacePackageContextService packageContext,
    TextureCatalogService textureCatalogService)
{
    private readonly TextureCatalogService _textureCatalogService =
        textureCatalogService ?? throw new ArgumentNullException(nameof(textureCatalogService));

    public MorphFaceConversionResult Convert(MorphFaceConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourcePath = RequirePackage(request.SourcePackagePath, "source");
        var destinationPath = Path.GetFullPath(request.DestinationPackagePath);
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
        var sourceCatalog = ReadTextureCatalog(source.Game);
        var targetCatalog = ReadTextureCatalog(request.TargetGame);
        var species = SpeciesForProfile(sourceProfile.Key);
        var targetProfile = profiles.Profiles.SingleOrDefault(profile =>
                                profile.Game == request.TargetGame &&
                                string.Equals(SpeciesForProfile(profile.Key), species, StringComparison.OrdinalIgnoreCase))
                            ?? throw new NotSupportedException(
                                $"No {request.TargetGame} conversion profile exists for {sourceProfile.DisplayName}.");

        var templatePath = request.CreateNewPackage
            ? string.IsNullOrWhiteSpace(request.TemplatePackagePath)
                ? FindAutomaticTemplatePath(targetProfile, targetCatalog.MorphFaceTemplates)
                : RequirePackage(request.TemplatePackagePath, "template")
            : RequirePackage(destinationPath, "destination");

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
        sourceMaterial = ApplyFaceSpecificMaterialPolicy(
            source.Game,
            request.TargetGame,
            source.Document.Source.InstancedPath,
            sourceMaterial);
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
                templateReference.FacePath,
                destinationPath,
                objectName,
                sourceMaterial,
                support,
                source.Game,
                sourceProfile.Key,
                sourcePath,
                source.Document.HairMeshReference,
                source.Document.OtherMeshReferences,
                sourceCatalog.Candidates,
                targetCatalog.Candidates);
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
                source.Document.HairMeshReference,
                source.Document.OtherMeshReferences,
                sourceCatalog.Candidates,
                targetCatalog.Candidates);
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
        string templateFacePath,
        string destinationPath,
        string objectName,
        MorphFaceMaterialData sourceMaterial,
        MaterialSupport support,
        MorphFaceGame sourceGame,
        string sourceProfileKey,
        string sourcePackagePath,
        AssetIdentity? sourceHair,
        IReadOnlyList<AssetIdentity?> sourceOtherMeshes,
        IReadOnlyList<TextureCatalogCandidate> sourceTextureCatalog,
        IReadOnlyList<TextureCatalogCandidate> targetTextureCatalog)
    {
        var transferred = packageContext.CreateConvertedMorphPackage(
            destinationPath,
            draft,
            objectName,
            templatePath,
            templateFacePath,
            sourceMaterial,
            support.Scalars,
            support.Vectors,
            support.Textures,
            sourceGame,
            sourceProfileKey,
            sourcePackagePath,
            sourceHair,
            sourceOtherMeshes,
            sourceTextureCatalog,
            targetTextureCatalog);
        return (transferred.SaveResult, transferred.MaterialData);
    }

    private TextureCatalogReadResult ReadTextureCatalog(MorphFaceGame game)
    {
        var result = _textureCatalogService.ReadAsync(game).GetAwaiter().GetResult();
        if (!result.IsAvailable)
        {
            throw new InvalidOperationException(
                $"The {game} texture database is unavailable. Build it in Texture Databases before converting across games.");
        }
        return result;
    }

    private string FindAutomaticTemplatePath(
        MorphFaceProfile profile,
        IReadOnlyList<MorphFaceTemplateCandidate> candidates)
    {
        var match = candidates.FirstOrDefault(candidate =>
            candidate.Origin != TextureCatalogOrigin.Mod &&
            profiles.Find(profile.Game, candidate.FacePath, candidate.BaseHeadPath)?.Key == profile.Key &&
            profile.GeometryEditBlockReason(candidate.BaseHeadPath) is null &&
            File.Exists(candidate.PackagePath));
        if (match is null)
        {
            throw new InvalidOperationException(
                $"The {profile.Game} texture database contains no installed {profile.DisplayName} face to use as a conversion template. Rebuild that database and try again.");
        }
        return Path.GetFullPath(match.PackagePath);
    }

    private static MorphFaceMaterialData ApplyFaceSpecificMaterialPolicy(
        MorphFaceGame sourceGame,
        MorphFaceGame targetGame,
        string sourceFacePath,
        MorphFaceMaterialData source)
    {
        if (sourceGame != MorphFaceGame.LE2 || targetGame != MorphFaceGame.LE3 ||
            !sourceFacePath.Equals("HMF.BioFace_HF1", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        const string canonicalLash =
            "BIOG_HMF_HED_PROMorph_R.Average.HMF_HED_PROLash_Opac_M01";
        var textures = source.Textures
            .Where(value => !value.Name.Equals("HED_Scalp_Spec", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Name.Equals("HED_Lash_Diff", StringComparison.OrdinalIgnoreCase)
                ? value with
                {
                    TextureReference = new AssetIdentity(string.Empty, canonicalLash, 0, "Texture2D")
                }
                : value)
            .ToArray();
        return new MorphFaceMaterialData(source.Scalars, source.Vectors, textures);
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
        var supported = source is MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3 &&
                        target is MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3 &&
                        source != target;
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
