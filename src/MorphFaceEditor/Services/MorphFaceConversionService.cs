using MorphFaceEditor.Core.Services;
using System.IO;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
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

public sealed record PlayerMorphRonConversionRequest(
    string SourcePackagePath,
    string SourceFacePath,
    MorphFaceGame TargetGame,
    string DestinationRonPath,
    bool ApproximateBakedShape = false);

public sealed record PlayerMorphRonConversionResult(
    string DestinationPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<UserFacingMessage> UserWarnings);

/// <summary>
/// Converts semantic face authoring state between game profiles while using a
/// real destination-game face as the package/dependency template.
/// </summary>
public sealed class MorphFaceConversionService(
    MorphFaceProfileRegistry profiles,
    MorphTargetCatalog targets,
    MorphFacePackageContextService packageContext,
    TextureCatalogService textureCatalogService,
    PlayerMorphBaselineCatalog? playerBaselines = null)
{
    private readonly TextureCatalogService _textureCatalogService =
        textureCatalogService ?? throw new ArgumentNullException(nameof(textureCatalogService));
    private readonly PlayerMorphSourceReconciler _playerSources = new();
    private readonly PlayerMorphBaselineCatalog _playerBaselines = playerBaselines ?? new();

    public static bool CanApproximateBakedShape(string sourceProfileKey, MorphFaceGame targetGame) =>
        sourceProfileKey.Equals("le1-human-female", StringComparison.OrdinalIgnoreCase) && targetGame == MorphFaceGame.LE3;

    public MorphFaceConversionResult Convert(MorphFaceConversionRequest request) => Convert(request, player: false);

    public PlayerMorphRonConversionResult ConvertPlayerRon(PlayerMorphRonConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var destination = Path.GetFullPath(request.DestinationRonPath);
        if (!Path.GetExtension(destination).Equals(".ron", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(destination) || Directory.Exists(destination) ||
            destination.Equals(Path.GetFullPath(request.SourcePackagePath), StringComparison.OrdinalIgnoreCase))
            throw UserFacingMessage.CreateError("loc_PlayerConversion_InvalidDestination", "Player conversion requires a new RON destination.");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"MFE-PlayerConversion-{Guid.NewGuid():N}");
        var candidate = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var converted = Convert(new MorphFaceConversionRequest(
                request.SourcePackagePath, request.SourceFacePath, request.TargetGame,
                Path.Combine(temporaryDirectory, "Player.pcc"), true, null), player: true, approximateBakedShape: request.ApproximateBakedShape);
            var face = MorphFaceReferenceInspector.Inspect(converted.SaveResult.PackagePath)
                .Single(reference => reference.FacePath == converted.SaveResult.FaceInstancedPath);
            var archetype = profiles.Require(request.TargetGame, face.FacePath, face.BaseHeadPath).ExportTag.Trim('[', ']');
            var provenance = new RonExportProvenance(RonExportProducer.MFE, request.TargetGame, archetype, PlayerMorph: true);
            // Validate runtime backing before writing even the candidate, then reopen the RON before publication.
            packageContext.ExportRon(converted.SaveResult.PackagePath, converted.SaveResult.FaceInstancedPath, candidate, provenance);
            var reopened = TseHeadMorphRon.ReadDocument(candidate);
            if (reopened.Provenance != provenance)
                throw UserFacingMessage.CreateError("loc_PlayerConversion_UnsafeOutput", "Converted Player RON provenance did not round-trip.");
            try { File.Move(candidate, destination); }
            catch (IOException exception) when (File.Exists(destination) || Directory.Exists(destination))
            {
                throw UserFacingMessage.CreateError("loc_PlayerConversion_InvalidDestination", exception.Message);
            }

            var warnings = converted.SaveResult.Warnings.ToList();
            var userWarnings = converted.SaveResult.UserWarnings.ToList();
            if (converted.DroppedFeatureCount > 0)
            {
                var diagnostic = $"Unsupported non-zero source sliders reset to neutral: {converted.DroppedFeatureCount}.";
                warnings.Add(diagnostic);
                userWarnings.Add(UserFacingMessage.CreateWarning("loc_Shell_Conversion_UnsupportedSlidersReset", diagnostic, converted.DroppedFeatureCount));
            }
            return new(destination, warnings, userWarnings);
        }
        finally
        {
            if (File.Exists(candidate)) File.Delete(candidate);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private MorphFaceConversionResult Convert(MorphFaceConversionRequest request, bool player, bool approximateBakedShape = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourcePath = RequirePackage(request.SourcePackagePath, "source");
        var destinationPath = Path.GetFullPath(request.DestinationPackagePath);
        if (request.CreateNewPackage && File.Exists(destinationPath))
        {
            throw new IOException("The new conversion destination already exists.");
        }

        using var reader = new MorphFacePackageReader();
        // Conversion ports authored texture references; it never uses preview pixels.
        var source = reader.Load(sourcePath, request.SourceFacePath, decodeTextures: false);
        var sourceProfile = profiles.Require(
            source.Game,
            source.Document.Source.InstancedPath,
            source.Document.BaseHeadReference?.InstancedPath);
        if (sourceProfile.TargetSetName is null && !sourceProfile.IgnoresAuthoredGeometry)
            throw new NotSupportedException();
        EnsureSupportedDirection(source.Game, request.TargetGame);
        StandalonePlayerSex? playerSex = null;
        var canonicalSourceHead = source.BaseHead;
        PlayerMorphBaselineFrame? sourceBaseline = null;
        MorphFaceDocument? bakedShapePrior = null;
        var transferBakedShape = false;
        if (player)
        {
            if (sourceProfile.Archetype is not ("human-male" or "human-female") || source.Document.BakedLods.Count == 0)
                throw UserFacingMessage.CreateError("loc_PlayerConversion_UnsupportedProfile", "Only male and female Player heads can use Player conversion.");
            playerSex = sourceProfile.Archetype == "human-male" ? StandalonePlayerSex.Male : StandalonePlayerSex.Female;
            if (source.UsesCustomBaseMesh || !reader.MatchesPlayerBaseHead(source, playerSex.Value))
                throw UserFacingMessage.CreateError("loc_PlayerConversion_UnsupportedGeometry", "The source does not use the canonical Player mesh and rig.");
            var (interpreted, baseline) = InterpretPlayerSource(source, sourceProfile);
            if (approximateBakedShape && CanApproximateBakedShape(sourceProfile.Key, request.TargetGame))
                bakedShapePrior = interpreted.BakedShapePrior;
            if (!interpreted.IsSupported && bakedShapePrior is null)
                throw UserFacingMessage.CreateError(interpreted.Reason == PlayerMorphConversionGeometryReason.InvalidSkeleton
                    ? "loc_PlayerConversion_UnsupportedBones" : "loc_PlayerConversion_UnsupportedGeometry",
                    $"The Player LOD0 has no verified native or recognised-baseline interpretation: {interpreted.Reason}.");
            // Recompute manual bone residuals using the same proven feature interpretation
            // as the vertices, while keeping the authored final skeleton unchanged.
            sourceBaseline = baseline;
            transferBakedShape = !interpreted.IsSupported;
            source = source with
            {
                Document = interpreted.Document ?? bakedShapePrior!,
                BaseHead = baseline?.EvaluationBase ?? canonicalSourceHead
            };
            AppLog.Verbose($"PLAYER:SOURCE-INTERPRETED {sourceProfile.Key} BASELINE:{baseline?.Baseline.Id ?? (transferBakedShape ? "baked" : "native")} RECOVERED:{interpreted.RecoveredWeights} RANK:{interpreted.BasisRank}");
        }
        var sourceSession = CreateSession(source, sourceProfile);
        // Earlier TUF corpora inherited male Turian bakes. Projecting their
        // sliders would silently replace that unverified authored geometry.
        if (sourceProfile.Archetype == "female-turian" && !sourceSession.CanEditMorphFeatures)
            throw Loc.Error(new InvalidOperationException(Loc.Text("loc_Editor_GeometryVerificationUnavailable")));
        var sourceCatalog = ReadTextureCatalog(source.Game);
        var targetCatalog = ReadTextureCatalog(request.TargetGame);
        var species = SpeciesForProfile(sourceProfile.Key);
        var targetProfile = profiles.Profiles.SingleOrDefault(profile =>
                                profile.Game == request.TargetGame &&
                                string.Equals(SpeciesForProfile(profile.Key), species, StringComparison.OrdinalIgnoreCase))
                            ?? throw new NotSupportedException(
                                $"No {request.TargetGame} conversion profile exists for {sourceProfile.GetDisplayName()}.");

        var templatePath = playerSex is not null
            ? StandalonePlayerMorphImportService.ResolveInstalledSeed(request.TargetGame)
            : request.CreateNewPackage
            ? string.IsNullOrWhiteSpace(request.TemplatePackagePath)
                ? FindAutomaticTemplatePath(targetProfile, targetCatalog.MorphFaceTemplates)
                : RequirePackage(request.TemplatePackagePath, "template")
            : RequirePackage(destinationPath, "destination");

        var (templateReference, template, templateReferences) = playerSex is not null
            ? FindPlayerTemplate(reader, templatePath, targetProfile, playerSex.Value)
            : FindEditableTemplate(reader, templatePath, targetProfile);
        PlayerMorphBaselineFrame? targetBaseline = null;
        if (sourceBaseline is not null)
        {
            // The local pair defines the two starting heads. Transfer only the edits.
            var counterpart = _playerBaselines.FindCounterpart(sourceBaseline.Baseline.Id, targetProfile);
            targetBaseline = counterpart is null ? null : PlayerMorphBaselineFrame.TryCreate(
                counterpart, template.Document, template.BaseHead, targetProfile, targets.Load(targetProfile));
            if (targetBaseline is null)
            {
                if (bakedShapePrior is null)
                    throw UserFacingMessage.CreateError("loc_PlayerConversion_MissingBaselineCounterpart",
                        $"No compatible local Player reference for {sourceBaseline.Baseline.Id} and {targetProfile.Key}.");
                source = source with { Document = bakedShapePrior, BaseHead = canonicalSourceHead };
                sourceSession = CreateSession(source, sourceProfile);
                transferBakedShape = true;
            }
            else
            {
                template = template with { Document = targetBaseline.Document, BaseHead = targetBaseline.EvaluationBase };
            }
        }
        var transfer = sourceSession.CaptureSemanticTransferData();
        if (targetBaseline is not null) transfer = sourceBaseline!.CaptureEdits(transfer);
        var targetSession = CreateSession(template, targetProfile);
        var resetSplitSide = targetProfile.Game == MorphFaceGame.LE3 && targetProfile.Archetype == "human-female";
        if (!targetProfile.IgnoresAuthoredGeometry)
        {
            targetSession.ApplySemanticTransferData(targetBaseline?.ApplyEdits(transfer) ?? transfer);
            // LE3's target has no LOD0 deltas; suppress its LOD1 deformation before baking.
            if (resetSplitSide) targetSession.SetFeature("HAIR_splitSide", 0f);
        }
        var convertedDraft = targetSession.CreateDraft(
            hairMeshReference: null,
            otherMeshReferences: [],
            materialOverrides: MorphFaceMaterialOverrides.Empty);
        if (player) RequirePlayerGeometry(convertedDraft, template.BaseHead, targetProfile);
        if (transferBakedShape)
        {
            var lods = convertedDraft.BakedLods.ToArray();
            try { lods[0] = PlayerBakedShapeTransfer.Transfer(source.BaseHead, source.Document, template.BaseHead, convertedDraft); }
            catch (InvalidDataException exception)
            {
                throw UserFacingMessage.CreateError("loc_PlayerConversion_UnsupportedGeometry", exception.Message);
            }
            convertedDraft = convertedDraft with { BakedLods = lods };
        }
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
            request.CreateNewPackage ? [] : templateReferences);

        MorphFaceSaveResult saveResult;
        MorphFaceMaterialData mappedMaterial;
        if (request.CreateNewPackage)
        {
            var transferred = packageContext.CreateConvertedMorphPackage(
                destinationPath,
                convertedDraft,
                objectName,
                templatePath,
                templateReference.FacePath,
                sourceMaterial,
                support.Scalars,
                support.Vectors,
                support.Textures,
                source.Game,
                sourceProfile.Key,
                sourcePath,
                source.Document.HairMeshReference,
                source.Document.OtherMeshReferences,
                sourceCatalog.Candidates,
                targetCatalog.Candidates);
            saveResult = transferred.SaveResult;
            mappedMaterial = transferred.MaterialData;
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

        if (transferBakedShape)
        {
            const string detail = "Transferred the posed Player LOD0 onto the target mesh using surface correspondence.";
            saveResult = saveResult with
            {
                Warnings = [.. saveResult.Warnings, detail],
                UserWarnings = [.. saveResult.UserWarnings, UserFacingMessage.CreateWarning("loc_PlayerConversion_ApproximateResult", detail)]
            };
        }
        var sourceNonZero = transfer.MorphFeatures
            .Where(value => value.Offset != 0)
            .Select(value => value.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetNames = targetSession.Features.Select(value => value.Name)
            .Where(name => !resetSplitSide || !name.Equals("HAIR_splitSide", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MorphFaceConversionResult(
            saveResult,
            sourceProfile.GetDisplayName(),
            targetProfile.GetDisplayName(),
            sourceNonZero.Count(targetNames.Contains),
            sourceNonZero.Count(name => !targetNames.Contains(name)),
            mappedMaterial.Scalars.Count,
            mappedMaterial.Vectors.Count,
            mappedMaterial.Textures.Count,
            sourceSession.CanEditMorphFeatures && !transferBakedShape);
    }

    private (PlayerMorphSourceReconciliation Interpretation, PlayerMorphBaselineFrame? Baseline) InterpretPlayerSource(
        LoadedMorphFace source, MorphFaceProfile profile)
    {
        var document = source.Document;
        var nativeTargets = targets.Load(profile);
        var native = _playerSources.Reconcile(document, source.BaseHead, profile, nativeTargets);
        if (native.IsSupported || native.Reason != PlayerMorphConversionGeometryReason.GeometryMismatch)
            return (native, null);

        (PlayerMorphSourceReconciliation Interpretation, PlayerMorphBaselineFrame? Baseline)? match = null;
        foreach (var baseline in _playerBaselines.ForProfile(profile))
        {
            var frame = PlayerMorphBaselineFrame.TryCreate(baseline, document, source.BaseHead, profile, nativeTargets);
            if (frame is null) continue;
            var interpreted = _playerSources.Reconcile(document, frame.EvaluationBase, profile, nativeTargets);
            if (!interpreted.IsSupported) continue;
            if (match is not null)
                throw UserFacingMessage.CreateError("loc_PlayerConversion_UnsupportedGeometry", "The Player LOD0 matches multiple baked baselines.");
            match = (interpreted with { BakedShapePrior = native.BakedShapePrior }, frame);
        }
        return match ?? (native, null);
    }

    private TextureCatalogReadResult ReadTextureCatalog(MorphFaceGame game)
    {
        var result = _textureCatalogService.ReadAsync(game).GetAwaiter().GetResult();
        if (!result.IsAvailable)
        {
            throw Loc.Error(new InvalidOperationException(
                Loc.Format("loc_Services_MorphFaceConversion_DatabaseUnavailable", game)));
        }
        return result;
    }

    private string FindAutomaticTemplatePath(
        MorphFaceProfile profile,
        IReadOnlyList<MorphFaceTemplateCandidate> candidates)
    {
        var match = candidates.FirstOrDefault(candidate =>
            (candidate.Origin != TextureCatalogOrigin.Mod || profile.Archetype == "female-turian") &&
            profiles.Find(profile.Game, candidate.FacePath, candidate.BaseHeadPath)?.Key == profile.Key &&
            (profile.IgnoresAuthoredGeometry || profile.GeometryEditBlockReason(candidate.BaseHeadPath) is null) &&
            File.Exists(candidate.PackagePath));
        if (match is null)
        {
            throw Loc.Error(new InvalidOperationException(
                Loc.Format("loc_Services_MorphFaceConversion_NoInstalledTemplate", profile.Game, profile.GetDisplayName())));
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

    private void RequirePlayerGeometry(MorphFaceDocument document, SkeletalMeshAsset canonicalBase, MorphFaceProfile profile)
    {
        var result = PlayerMorphConversionGeometry.Check(document, canonicalBase, targets.Load(profile),
            profile.MetadataOnlyFeatures, profile.FeatureAliases);
        if (!result.IsSupported)
            throw UserFacingMessage.CreateError("loc_PlayerConversion_UnsupportedGeometry",
                $"Player geometry cannot be recreated from its verified baseline: {result.Reason}, LOD {result.LodIndex}.");
    }

    private (MorphFaceMeshReferences Reference, LoadedMorphFace Loaded, IReadOnlyList<MorphFaceMeshReferences> References)
        FindPlayerTemplate(MorphFacePackageReader reader, string packagePath, MorphFaceProfile profile, StandalonePlayerSex sex)
    {
        var facePath = StandalonePlayerMorphImportService.ResolvePlayerTemplate(profile.Game, sex);
        var references = MorphFaceReferenceInspector.Inspect(packagePath);
        var reference = references.Single(value => value.FacePath.Equals(facePath, StringComparison.OrdinalIgnoreCase));
        var loaded = reader.Load(packagePath, facePath, decodeTextures: false);
        if (profiles.Require(loaded.Game, facePath, loaded.Document.BaseHeadReference?.InstancedPath).Key != profile.Key)
            throw UserFacingMessage.CreateError("loc_PlayerConversion_UnsupportedProfile", "The target Player template has a different head profile.");

        // Player seeds contain authored shapes. Conversion starts from the actual target bind mesh,
        // including every LOD and bone, so seed sliders and position corrections cannot leak into output.
        loaded = loaded with
        {
            Document = loaded.Document with
            {
                MorphFeatures = [],
                FinalSkeleton = loaded.BaseHead.Topology.ReferenceSkeleton.Select(bone => new BoneTranslation(bone.Name, bone.Position)).ToArray(),
                BakedLods = loaded.BaseHead.Lods.Select(lod => lod.Positions.ToArray()).ToArray()
            }
        };
        RequirePlayerGeometry(loaded.Document, loaded.BaseHead, profile);
        return (reference, loaded, references);
    }

    private (MorphFaceMeshReferences Reference, LoadedMorphFace Loaded,
        IReadOnlyList<MorphFaceMeshReferences> References) FindEditableTemplate(
        MorphFacePackageReader reader,
        string templatePath,
        MorphFaceProfile profile)
    {
        var references = MorphFaceReferenceInspector.Inspect(templatePath);
        var game = references.FirstOrDefault()?.Game ?? MorphFaceGame.Unsupported;
        if (game != profile.Game)
        {
            throw Loc.Error(new InvalidDataException(
                Loc.Format("loc_Services_MorphFaceConversion_DestinationGameMismatch", game, profile.Game)));
        }

        var candidates = references
            .Where(reference => profiles.Find(
                reference.Game,
                reference.FacePath,
                reference.BaseHeadPath)?.Key == profile.Key)
            .Where(reference => profile.IgnoresAuthoredGeometry ||
                                profile.GeometryEditBlockReason(reference.BaseHeadPath) is null)
            .OrderBy(reference => reference.FacePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var reference in candidates)
        {
            try
            {
                var loaded = reader.Load(templatePath, reference.FaceUIndex.ToString(
                    System.Globalization.CultureInfo.InvariantCulture), decodeTextures: false);
                var session = CreateSession(loaded, profile);
                if (session.CanEditMorphFeatures || profile.IgnoresAuthoredGeometry)
                {
                    return (reference, loaded, references);
                }
            }
            catch
            {
                // A package can contain stale or unresolved faces. Continue to
                // another same-profile candidate before reporting no template.
            }
        }
        throw Loc.Error(new InvalidOperationException(
            Loc.Format("loc_Services_MorphFaceConversion_NoEditableTemplate", profile.GetDisplayName())));
    }

    private MorphFaceEditingSession CreateSession(
        LoadedMorphFace loaded,
        MorphFaceProfile profile) => new(
        loaded.Document,
        loaded.BaseHead,
        profile.IgnoresAuthoredGeometry ? [] : targets.Load(profile),
        profile.MetadataOnlyFeatures,
        profile.GetDisplayName(),
        profile.FeatureAliases,
        profile.RecognizesBaseVariant,
        profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath),
        ignoreAuthoredGeometry: profile.IgnoresAuthoredGeometry);

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
