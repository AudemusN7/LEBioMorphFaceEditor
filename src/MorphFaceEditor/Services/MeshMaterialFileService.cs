using System.IO;
using System.Text;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

public sealed record PreparedMeshMaterialImport(
    IReadOnlyDictionary<int, CustomMaterialAssignmentOption>? Assignments,
    MorphFaceMaterialData Parameters,
    IReadOnlyDictionary<string, DecodedTextureAsset?> Textures,
    IReadOnlyList<string> Warnings,
    TsePreviewAttachmentImport? PreviewAttachments = null,
    bool IsTse = false);

public sealed record TsePreviewAttachmentImport(
    bool ApplyHair,
    AssetIdentity? Hair,
    bool ApplyAccessory,
    AssetIdentity? Accessory);

/// <summary>Owns MESH file policy, assignment resolution and texture preparation before any live edit.</summary>
public sealed class MeshMaterialFileService(PackageReferenceService references)
{
    public static MeshMaterialDocument Capture(CustomMaterialWorkspace workspace, MorphFaceGame game,
        string meshName, MorphFaceMaterialData data)
    {
        var assignments = workspace.Assignments.ToDictionary(value => value.Slot.MaterialIndex, value => value.Option);
        return new(game.ToString(), meshName, workspace.UsedSlots.Select(slot =>
        {
            assignments.TryGetValue(slot.MaterialIndex, out var option);
            return new MeshMaterialSlotData(slot.MaterialIndex, slot.MaterialName, option?.Id ?? "",
                option?.Label ?? "", option?.EffectiveParameterScopeKey ?? "", option?.Family ?? HeadMaterialFamily.Unknown);
        }).ToArray(), MeshMaterialInterchange.Split(QualifyTexturePaths(data)))
        {
            DefaultFace = InferDefaultFace(workspace, game)
        };
    }

    private static MaterialFaceTarget? InferDefaultFace(CustomMaterialWorkspace workspace, MorphFaceGame game)
    {
        var suffixes = workspace.Assignments.Select(value => HeadProfileIdentity.InferSuffix(
                value.Option.TemplateSource.InstancedPath, value.Option.Id, value.Option.Label))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (suffixes.Length != 1 || suffixes[0] == "human") return null;
        var profileKey = $"{game.ToString().ToLowerInvariant()}-{suffixes[0]}";
        if (!MorphFaceProfileRegistry.CreateDefault().Profiles.Any(profile => profile.Game == game && profile.Key == profileKey)) return null;
        return new(profileKey,
            suffixes[0] is "human-male" or "human-female");
    }

    public static string ExportTse(CustomMaterialWorkspace workspace, MorphFaceMaterialData data,
        AssetIdentity? hair, IReadOnlyList<AssetIdentity?> accessories, MorphFaceGame? game = null)
    {
        if (!MeshMaterialInterchange.CanExportTse(workspace))
            throw new InvalidOperationException(Loc.Text("loc_Services_MeshMaterial_TseExportRequiresHumanMaterial"));
        var human = MeshMaterialInterchange.Split(QualifyTexturePaths(data)).GetValueOrDefault("human")
            ?? new MorphFaceMaterialData([], [], []);
        return MaterialRonCodec.WriteTse(human, AttachmentPath(hair), accessories.OfType<AssetIdentity>()
            .Select(AttachmentPath).ToArray(), game?.ToString(), game is { } targetGame ? InferDefaultFace(workspace, targetGame) : null);
    }

    public async Task<PreparedMeshMaterialImport> PrepareAsync(string text, bool tse,
        CustomMaterialWorkspace workspace, IReadOnlyList<CustomMaterialAssignmentOption> options, MorphFaceGame game)
    {
        IReadOnlyDictionary<int, CustomMaterialAssignmentOption>? assignments = null;
        MorphFaceMaterialData parameters;
        MaterialRonCodec.TseMaterialDocument? tseDocument = null;
        var target = workspace;
        if (tse)
        {
            if (!MeshMaterialInterchange.CanImportTse(workspace))
                throw new InvalidOperationException(Loc.Text("loc_Services_MeshMaterial_AssignHumanBeforeTseImport"));
            tseDocument = MaterialRonCodec.ReadTseDocument(text);
            parameters = MeshMaterialInterchange.Scope(tseDocument.Parameters, "human");
        }
        else
        {
            MeshMaterialDocument? document = null;
            try
            {
                document = MaterialRonCodec.ReadMfe(text);
            }
            catch (InvalidDataException mfeError)
            {
                try { tseDocument = MaterialRonCodec.ReadTseDocument(text); }
                catch (InvalidDataException) { throw mfeError; }
            }
            if (tseDocument is not null)
            {
                if (!MeshMaterialInterchange.CanImportTse(workspace))
                    throw new InvalidOperationException(Loc.Text("loc_Services_MeshMaterial_TseRonNeedsHumanMaterial"));
                parameters = MeshMaterialInterchange.Scope(tseDocument.Parameters, "human");
            }
            else
            {
                if (document!.Game != game.ToString())
                    throw new InvalidDataException(Loc.Format("loc_Services_MeshMaterial_GameMismatch", document.Game, game));
                assignments = ResolveAssignments(document, workspace, options);
                target = new CustomMaterialWorkspace(workspace.Source);
                target.ReplaceAssignments(assignments);
                parameters = MeshMaterialInterchange.Flatten(document.Parameters);
            }
        }
        var catalogue = await references.ReadTextureCatalogAsync(game);
        var prepared = await PrepareTexturesAsync(assignments, parameters, target, catalogue.Candidates, references);
        if (tseDocument is null) return prepared;
        var warnings = prepared.Warnings.ToList();
        var attachments = await PreparePreviewAttachmentsAsync(tseDocument, game, warnings);
        return prepared with { Warnings = warnings, PreviewAttachments = attachments, IsTse = true };
    }

    private async Task<TsePreviewAttachmentImport?> PreparePreviewAttachmentsAsync(
        MaterialRonCodec.TseMaterialDocument document,
        MorphFaceGame game,
        ICollection<string> warnings)
    {
        if (document.HairMesh is null && document.AccessoryMeshes is null) return null;
        var requested = (document.HairMesh is null ? [] : new[] { document.HairMesh })
            .Concat(document.AccessoryMeshes ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && !path.Equals("None", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        IReadOnlyDictionary<string, AssetIdentity> resolved;
        try
        {
            resolved = requested.Length == 0
                ? new Dictionary<string, AssetIdentity>(StringComparer.OrdinalIgnoreCase)
                : await references.ResolveInstalledSkeletalMeshesAsync(game, requested);
        }
        catch (Exception exception)
        {
            warnings.Add(Loc.Format("loc_Services_MeshMaterial_AttachmentsCouldNotResolve", game, exception.Message));
            resolved = new Dictionary<string, AssetIdentity>(StringComparer.OrdinalIgnoreCase);
        }
        var validated = new Dictionary<string, AssetIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in resolved)
        {
            try
            {
                await references.ValidateDetachedAttachmentAsync(value.Value.PackagePath, value.Value.InstancedPath);
                validated[value.Key] = value.Value;
            }
            catch (Exception exception)
            {
                warnings.Add(Loc.Format("loc_Services_MeshMaterial_AttachmentCouldNotLoad", value.Key, exception.Message));
            }
        }

        (bool Apply, AssetIdentity? Identity) Resolve(string? path, string label)
        {
            if (path is null) return (false, null);
            if (string.IsNullOrWhiteSpace(path) || path.Equals("None", StringComparison.OrdinalIgnoreCase)) return (true, null);
            if (validated.TryGetValue(path, out var identity)) return (true, identity);
            if (resolved.ContainsKey(path)) return (false, null); // Validation already supplied the specific warning.
            warnings.Add(Loc.Format("loc_Services_MeshMaterial_AttachmentPathNotFound", label, path, game));
            return (false, null);
        }

        var hair = Resolve(document.HairMesh, "hair mesh");
        var accessoryPath = document.AccessoryMeshes?.FirstOrDefault();
        var accessory = document.AccessoryMeshes is null
            ? (Apply: false, Identity: (AssetIdentity?)null)
            : document.AccessoryMeshes.Count == 0
                ? (Apply: true, Identity: (AssetIdentity?)null)
                : Resolve(accessoryPath, "accessory mesh");
        if (document.AccessoryMeshes is { Count: > 1 })
            warnings.Add(Loc.Format("loc_Services_MeshMaterial_ExtraAccessoriesIgnored", document.AccessoryMeshes.Count));
        return new(hair.Apply, hair.Identity, accessory.Apply, accessory.Identity);
    }

    public static IReadOnlyDictionary<int, CustomMaterialAssignmentOption> ResolveAssignments(
        MeshMaterialDocument document, CustomMaterialWorkspace workspace, IReadOnlyList<CustomMaterialAssignmentOption> options)
    {
        if (!document.Slots.Select(value => value.Index).Order().SequenceEqual(workspace.UsedSlots.Select(value => value.MaterialIndex).Order()))
            throw new InvalidDataException(Loc.Text("loc_Services_MeshMaterial_SlotIndicesMismatch"));
        var result = new Dictionary<int, CustomMaterialAssignmentOption>();
        foreach (var slot in document.Slots.Where(value => value.MaterialId.Length > 0))
        {
            var option = options.SingleOrDefault(value => value.Id.Equals(slot.MaterialId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(Loc.Format("loc_Services_MeshMaterial_OptionUnavailable", slot.MaterialName));
            if (option.Family != slot.Family || !option.EffectiveParameterScopeKey.Equals(slot.Scope, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.Format("loc_Services_MeshMaterial_OptionMetadataInconsistent", slot.MaterialName));
            result.Add(slot.Index, option);
        }
        // Incompatible assignments must fail before any live assignments or history change.
        var validation = new CustomMaterialWorkspace(workspace.Source);
        validation.ReplaceAssignments(result);
        return result;
    }

    internal static async Task<PreparedMeshMaterialImport> PrepareTexturesAsync(
        IReadOnlyDictionary<int, CustomMaterialAssignmentOption>? assignments, MorphFaceMaterialData data,
        CustomMaterialWorkspace target, IReadOnlyList<TextureCatalogCandidate> candidates, ITextureReferenceLoader loader)
    {
        var decoded = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var catalog = new DetachedMeshFeatureMetadataCatalog(target);
        IEnumerable<ResolvedHeadMaterial> Supporting(string controlName, MaterialParameterKind kind) =>
            target.Assignments.Where(assignment => assignment.Option.EffectiveParameterScopeKey.Equals(
                    MaterialParameterControlKey.ScopeKey(controlName), StringComparison.OrdinalIgnoreCase) &&
                    assignment.Option.Template.Supports(MaterialParameterControlKey.ParameterName(controlName), kind))
                .Select(assignment => assignment.Option.Template);
        void WarnUnsupported(string controlName) => warnings.Add(
            Loc.Format("loc_Services_MeshMaterial_ParameterUnsupported", MaterialParameterControlKey.ScopeKey(controlName),
                MaterialParameterControlKey.ParameterName(controlName)));
        foreach (var value in data.Scalars)
            if (!Supporting(value.Name, MaterialParameterKind.Scalar).Any()) WarnUnsupported(value.Name);
        foreach (var value in data.Vectors)
            if (!Supporting(value.Name, MaterialParameterKind.Vector).Any()) WarnUnsupported(value.Name);
        foreach (var value in data.Textures)
        {
            var name = MaterialParameterControlKey.ParameterName(value.Name);
            var supporting = Supporting(value.Name, MaterialParameterKind.Texture).ToArray();
            if (supporting.Length == 0) WarnUnsupported(value.Name);
            if (value.TextureReference is not { } reference) { decoded[value.Name] = null; continue; }
            if (supporting.Length == 0)
            {
                continue;
            }
            var candidate = PreferredTextureSource(reference.InstancedPath, candidates);
            var inherited = supporting.SelectMany(material => material.Textures.Values.Concat(material.DefaultTextures.Values))
                .FirstOrDefault(binding => QualifyPath(binding.Texture.Source).Equals(reference.InstancedPath, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (candidate is not null)
                {
                    var definition = catalog.DescribeMaterial(HumanMaterialProfiles.Describe(name, MaterialParameterKind.Texture)
                        with { Name = value.Name }) with { Name = name };
                    decoded[value.Name] = await loader.LoadTextureAsync(candidate.Value.Occurrence.PackagePath,
                        candidate.Value.Candidate.InstancedPath, definition);
                }
                else if (inherited is not null) decoded[value.Name] = inherited.Texture;
                else warnings.Add(Loc.Format("loc_Services_MeshMaterial_TextureNotFound", reference.InstancedPath));
            }
            catch (Exception exception)
            {
                warnings.Add(Loc.Format("loc_Services_MeshMaterial_TexturePreviewFailed", reference.InstancedPath, exception.Message));
            }
        }
        return new(assignments, data, decoded, warnings);
    }

    internal static (TextureCatalogCandidate Candidate, TextureCatalogOccurrence Occurrence)? PreferredTextureSource(
        string requestedPath,
        IReadOnlyList<TextureCatalogCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);

        var matches = TextureCatalogPicker.Select(candidates)
            .Where(candidate => candidate.InstancedPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase) ||
                TextureCatalogPicker.CanonicalPath(candidate.InstancedPath,
                    candidate.EffectiveOccurrence.PackagePath).Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var official = matches
            .Where(candidate => candidate.IsEligibleOfficialDefault &&
                candidate.EffectiveOccurrence.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
            .OrderByDescending(candidate => candidate.EffectiveOccurrence.MountPriority)
            .ThenBy(candidate => candidate.EffectiveOccurrence.PackagePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (official is not null) return (official, official.EffectiveOccurrence);

        // An explicitly registered custom package remains usable when the path
        // has no eligible installed official default. Mod occurrences are kept
        // visible for explicit picker selection, not inferred from a path-only import.
        var manual = matches
            .Where(candidate => candidate.EffectiveOccurrence.Origin == TextureCatalogOrigin.Manual)
            .OrderBy(candidate => candidate.EffectiveOccurrence.PackagePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return manual is null ? null : (manual, manual.EffectiveOccurrence);
    }

    public static async Task WriteAsync(string destination, string text, string sourceMeshPath)
    {
        var path = Path.GetFullPath(destination);
        if (path.Equals(Path.GetFullPath(sourceMeshPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A material export cannot replace its source mesh.");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static MorphFaceMaterialData QualifyTexturePaths(MorphFaceMaterialData data) => data with
    {
        Textures = data.Textures.Select(value => value with
        {
            TextureReference = value.TextureReference is { } texture ? texture with { InstancedPath = QualifyPath(texture) } : null
        }).ToArray()
    };

    internal static string QualifyPath(AssetIdentity identity)
    {
        var package = Path.GetFileNameWithoutExtension(identity.PackagePath);
        return package.StartsWith("BIOG_", StringComparison.OrdinalIgnoreCase) &&
               !identity.InstancedPath.StartsWith(package + ".", StringComparison.OrdinalIgnoreCase) &&
               !identity.InstancedPath.StartsWith("BIOG_", StringComparison.OrdinalIgnoreCase)
            ? package + "." + identity.InstancedPath : identity.InstancedPath;
    }

    private static string AttachmentPath(AssetIdentity? identity)
    {
        if (identity is null) return "None";
        var path = QualifyPath(identity);
        if (!PlayerWorkspaceReferencePolicy.IsSeekFreeQualified(path))
            throw new InvalidDataException($"Attachment '{identity.InstancedPath}' has no full seek-free path for TSE export.");
        return path;
    }
}
