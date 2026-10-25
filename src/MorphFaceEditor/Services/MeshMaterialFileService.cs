using System.IO;
using System.Text;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

/// <summary>Prepares compatible material parameters and textures against the mesh's current assignments.</summary>
public sealed class MeshMaterialFileService(PackageReferenceService references)
{
    public static MeshMaterialDocument WithAttachments(MeshMaterialDocument document,
        AssetIdentity? hair, IReadOnlyList<AssetIdentity?> accessories) => document with
    {
        HairMesh = hair is null ? "None" : QualifyPath(hair),
        AccessoryMeshes = accessories.OfType<AssetIdentity>().Select(QualifyPath).ToArray()
    };

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
            throw Loc.Error(new InvalidOperationException(Loc.Text("loc_Services_MeshMaterial_TseExportRequiresHumanMaterial")));
        var human = MeshMaterialInterchange.Split(QualifyTexturePaths(data)).GetValueOrDefault("human")
            ?? new MorphFaceMaterialData([], [], []);
        return MaterialRonCodec.WriteTse(human, AttachmentPath(hair), accessories.OfType<AssetIdentity>()
            .Select(AttachmentPath).ToArray(), game?.ToString(), game is { } targetGame ? InferDefaultFace(workspace, targetGame) : null);
    }

    public Task<PreparedMaterialImport> PrepareAsync(string text, bool tse,
        CustomMaterialWorkspace workspace, MorphFaceGame game) =>
        PrepareAsync(RonInputSnapshot.Parse(text), tse, workspace, game);

    public async Task<PreparedMaterialImport> PrepareAsync(RonInputSnapshot input, bool tse,
        CustomMaterialWorkspace workspace, MorphFaceGame game)
    {
        MorphFaceMaterialData parameters;
        var isTse = tse || MfeRonMetadata.TryRead(input)?.Format != MfeRonMetadata.Materials;
        string? hair;
        IReadOnlyList<string>? accessories;
        if (isTse)
        {
            if (!MeshMaterialInterchange.CanImportTse(workspace))
                throw Loc.Error(new InvalidOperationException(Loc.Text(tse
                    ? "loc_Services_MeshMaterial_AssignHumanBeforeTseImport"
                    : "loc_Services_MeshMaterial_TseRonNeedsHumanMaterial")));
            var document = MaterialRonCodec.ReadTseDocument(input);
            parameters = MeshMaterialInterchange.Scope(document.Parameters, "human");
            hair = document.HairMesh;
            accessories = document.AccessoryMeshes;
        }
        else
        {
            var document = MaterialRonCodec.ReadMfe(input);
            if (document.Game != game.ToString())
                throw Loc.Error(new InvalidDataException(Loc.Format("loc_Services_MeshMaterial_GameMismatch", document.Game, game)));
            parameters = MeshMaterialInterchange.Flatten(document.Parameters);
            hair = document.HairMesh;
            accessories = document.AccessoryMeshes;
        }
        var catalogue = await references.ReadTextureCatalogAsync(game);
        var prepared = await PrepareTexturesAsync(parameters, workspace, catalogue.Candidates, references);
        var warnings = prepared.Warnings.ToList();
        var attachments = await PreparePreviewAttachmentsAsync(hair, accessories, game, warnings);
        return prepared with { Warnings = warnings, PreviewAttachments = attachments, IsTse = isTse };
    }

    internal async Task<TsePreviewAttachmentImport?> PreparePreviewAttachmentsAsync(
        string? hairMesh,
        IReadOnlyList<string>? accessoryMeshes,
        MorphFaceGame game,
        ICollection<string> warnings)
    {
        if (hairMesh is null && accessoryMeshes is null) return null;
        var requested = (hairMesh is null ? [] : new[] { hairMesh })
            .Concat(accessoryMeshes ?? [])
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
            warnings.Add(Loc.Format("loc_Services_MeshMaterial_AttachmentsCouldNotResolve", game,
                UserFacingMessageFormatter.Format(exception)));
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
                warnings.Add(Loc.Format("loc_Services_MeshMaterial_AttachmentCouldNotLoad", value.Key,
                    UserFacingMessageFormatter.Format(exception)));
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

        var hair = Resolve(hairMesh, Loc.Text("loc_Services_MeshMaterial_HairMeshKind"));
        var accessoryPath = accessoryMeshes?.FirstOrDefault();
        var accessory = accessoryMeshes is null
            ? (Apply: false, Identity: (AssetIdentity?)null)
            : accessoryMeshes.Count == 0
                ? (Apply: true, Identity: (AssetIdentity?)null)
            : Resolve(accessoryPath, Loc.Text("loc_Services_MeshMaterial_AccessoryMeshKind"));
        if (accessoryMeshes is { Count: > 1 })
            warnings.Add(Loc.Format("loc_Services_MeshMaterial_ExtraAccessoriesIgnored", accessoryMeshes.Count));
        return new(hair.Apply, hair.Identity, accessory.Apply, accessory.Identity);
    }

    private static MorphFaceMaterialData CompatibleParameters(MorphFaceMaterialData data, CustomMaterialWorkspace target)
    {
        var assignments = target.Assignments;
        T[] Match<T>(IEnumerable<T> values, MaterialParameterKind kind, Func<T, string> name, Func<T, string, T> rename)
        {
            var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                var scope = MaterialParameterControlKey.ScopeKey(name(value));
                var parameter = MaterialParameterControlKey.ParameterName(name(value));
                // PCC face exports have a neutral namespace; explicit mixed-mesh scopes remain distinct.
                var neutral = string.Equals(scope, "face", StringComparison.OrdinalIgnoreCase);
                var scopes = assignments.Where(assignment => (neutral || string.Equals(scope,
                        assignment.Option.EffectiveParameterScopeKey, StringComparison.OrdinalIgnoreCase)) &&
                        assignment.Option.Template.Supports(parameter, kind))
                    .Select(assignment => assignment.Option.EffectiveParameterScopeKey).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var destination in scopes)
                {
                    var key = MaterialParameterControlKey.Create(destination, parameter);
                    if (neutral) result.TryAdd(key, rename(value, key));
                    else result[key] = rename(value, key);
                }
            }
            return result.Values.ToArray();
        }
        return new(Match(data.Scalars, MaterialParameterKind.Scalar, value => value.Name, (value, key) => value with { Name = key }),
            Match(data.Vectors, MaterialParameterKind.Vector, value => value.Name, (value, key) => value with { Name = key }),
            Match(data.Textures, MaterialParameterKind.Texture, value => value.Name, (value, key) => value with { Name = key }));
    }

    internal static async Task<PreparedMaterialImport> PrepareTexturesAsync(
        MorphFaceMaterialData data,
        CustomMaterialWorkspace target, IReadOnlyList<TextureCatalogCandidate> candidates, ITextureReferenceLoader loader)
    {
        data = CompatibleParameters(data, target);
        var decoded = new Dictionary<string, DecodedTextureAsset?>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var catalog = new DetachedMeshFeatureMetadataCatalog(target);
        IEnumerable<ResolvedHeadMaterial> Supporting(string controlName, MaterialParameterKind kind) =>
            target.Assignments.Where(assignment => assignment.Option.EffectiveParameterScopeKey.Equals(
                    MaterialParameterControlKey.ScopeKey(controlName), StringComparison.OrdinalIgnoreCase) &&
                    assignment.Option.Template.Supports(MaterialParameterControlKey.ParameterName(controlName), kind))
                .Select(assignment => assignment.Option.Template);
        foreach (var value in data.Textures)
        {
            var name = MaterialParameterControlKey.ParameterName(value.Name);
            var supporting = Supporting(value.Name, MaterialParameterKind.Texture).ToArray();
            if (value.TextureReference is not { } reference) { decoded[value.Name] = null; continue; }
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
                warnings.Add(Loc.Format("loc_Services_MeshMaterial_TexturePreviewFailed", reference.InstancedPath,
                    UserFacingMessageFormatter.Format(exception)));
            }
        }
        return new(data, decoded, warnings);
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
            throw Loc.Error(new InvalidOperationException(Loc.Text(
                "loc_Services_MeshMaterial_SourceMeshDestination")));
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
            throw Loc.Error(new InvalidDataException(Loc.Format(
                "loc_Services_MeshMaterial_UnqualifiedAttachmentPath", identity.InstancedPath)));
        return path;
    }
}
