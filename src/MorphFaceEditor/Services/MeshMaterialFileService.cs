using System.IO;
using System.Text;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
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
        }).ToArray(), MeshMaterialInterchange.Split(QualifyTexturePaths(data)));
    }

    public static string ExportTse(CustomMaterialWorkspace workspace, MorphFaceMaterialData data,
        AssetIdentity? hair, IReadOnlyList<AssetIdentity?> accessories)
    {
        if (!MeshMaterialInterchange.CanExportTse(workspace))
            throw new InvalidOperationException("TSE export requires at least one assigned material and only Human materials.");
        var human = MeshMaterialInterchange.Split(QualifyTexturePaths(data)).GetValueOrDefault("human")
            ?? new MorphFaceMaterialData([], [], []);
        return MaterialRonCodec.WriteTse(human, AttachmentPath(hair), accessories.OfType<AssetIdentity>()
            .Select(AttachmentPath).ToArray());
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
                throw new InvalidOperationException("Assign a Human material before importing TSE material settings.");
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
                    throw new InvalidOperationException("This is a TSE RON. Assign a Human material before importing its settings.");
                parameters = MeshMaterialInterchange.Scope(tseDocument.Parameters, "human");
            }
            else
            {
                if (document!.Game != game.ToString())
                    throw new InvalidDataException($"These materials are for {document.Game}; the current mesh workspace is {game}.");
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
            warnings.Add($"TSE preview attachments could not be resolved from the installed {game} files; " +
                         $"the current preview attachments were retained. {exception.Message}");
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
                warnings.Add($"TSE preview attachment '{value.Key}' resolved but could not be loaded; " +
                             $"the current preview attachment was retained. {exception.Message}");
            }
        }

        (bool Apply, AssetIdentity? Identity) Resolve(string? path, string label)
        {
            if (path is null) return (false, null);
            if (string.IsNullOrWhiteSpace(path) || path.Equals("None", StringComparison.OrdinalIgnoreCase)) return (true, null);
            if (validated.TryGetValue(path, out var identity)) return (true, identity);
            if (resolved.ContainsKey(path)) return (false, null); // Validation already supplied the specific warning.
            warnings.Add($"TSE {label} '{path}' was not found at its full path in the installed {game} files; the current preview attachment was retained.");
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
            warnings.Add($"The TSE RON contains {document.AccessoryMeshes.Count} accessory meshes; MESH preview currently uses the first slot only.");
        return new(hair.Apply, hair.Identity, accessory.Apply, accessory.Identity);
    }

    public static IReadOnlyDictionary<int, CustomMaterialAssignmentOption> ResolveAssignments(
        MeshMaterialDocument document, CustomMaterialWorkspace workspace, IReadOnlyList<CustomMaterialAssignmentOption> options)
    {
        if (!document.Slots.Select(value => value.Index).Order().SequenceEqual(workspace.UsedSlots.Select(value => value.MaterialIndex).Order()))
            throw new InvalidDataException("The material file's slot indices do not match this mesh. Import it onto a mesh with the same used slots.");
        var result = new Dictionary<int, CustomMaterialAssignmentOption>();
        foreach (var slot in document.Slots.Where(value => value.MaterialId.Length > 0))
        {
            var option = options.SingleOrDefault(value => value.Id.Equals(slot.MaterialId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Material '{slot.MaterialName}' is unavailable in this game's material catalogue.");
            if (option.Family != slot.Family || !option.EffectiveParameterScopeKey.Equals(slot.Scope, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Material '{slot.MaterialName}' has inconsistent family or scope metadata.");
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
            $"'{MaterialParameterControlKey.ScopeKey(controlName)} / {MaterialParameterControlKey.ParameterName(controlName)}' " +
            "is not supported by an assigned material; its value was retained for export.");
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
                else warnings.Add($"Texture '{reference.InstancedPath}' was not found in the texture database; its path was retained.");
            }
            catch (Exception exception)
            {
                warnings.Add($"Texture '{reference.InstancedPath}' could not be previewed; its path was retained. {exception.Message}");
            }
        }
        return new(assignments, data, decoded, warnings);
    }

    internal static (TextureCatalogCandidate Candidate, TextureCatalogOccurrence Occurrence)? PreferredTextureSource(
        string requestedPath,
        IReadOnlyList<TextureCatalogCandidate> candidates)
    {
        var requestedRoot = requestedPath.Split('.')[0];
        return candidates.SelectMany(candidate => candidate.Occurrences.Select((occurrence, index) =>
            {
                var packageName = Path.GetFileNameWithoutExtension(occurrence.PackagePath);
                var canonical = candidate.InstancedPath.StartsWith("BIO", StringComparison.OrdinalIgnoreCase)
                    ? candidate.InstancedPath
                    : $"{packageName}.{candidate.InstancedPath}";
                var priority = packageName.Equals(requestedRoot, StringComparison.OrdinalIgnoreCase) ? 0 :
                    packageName.Equals("EntryMenu", StringComparison.OrdinalIgnoreCase) ||
                    packageName.Equals("BioP_Char", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                return (candidate, occurrence, index, canonical, priority);
            }))
            .Where(value => value.canonical.Equals(requestedPath, StringComparison.OrdinalIgnoreCase) ||
                            value.candidate.InstancedPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.priority)
            .ThenBy(value => value.index)
            .Select(value => ((TextureCatalogCandidate Candidate, TextureCatalogOccurrence Occurrence)?)
                (value.candidate, value.occurrence))
            .FirstOrDefault();
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
