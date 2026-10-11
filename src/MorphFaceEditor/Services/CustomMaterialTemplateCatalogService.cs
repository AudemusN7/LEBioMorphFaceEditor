using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.Services;

public interface ICustomMaterialTemplateCatalog
{
    CustomMaterialTemplateCatalogResult Load(MorphFaceGame game);
}

public sealed record CustomMaterialTemplateCatalogResult(
    IReadOnlyList<CustomMaterialAssignmentOption> Options,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AssetIdentity> PreviewAttachments)
{
    public bool IsRegistryAvailable { get; init; }
    public static CustomMaterialTemplateCatalogResult Empty { get; } = new([], [], []);
}

/// <summary>
/// Loads Custom assignment templates from exact installed material-bearing meshes.
/// Human Player assets are fixed sources; NPC archetypes are selected from the
/// already-scanned texture registry rather than rescanning the game installation.
/// </summary>
public sealed class CustomMaterialTemplateCatalogService : ICustomMaterialTemplateCatalog
{
    private readonly MorphFacePackageReader _reader;
    private readonly TextureRegistryStore _registry;
    private readonly object _catalogLock = new();
    private readonly Dictionary<MorphFaceGame, CachedCatalog> _catalogs = [];
    private sealed record SourceStamp(string Path, long Length, DateTime LastWriteTimeUtc)
    {
        public static SourceStamp Read(string path)
        {
            var file = new FileInfo(path);
            return new SourceStamp(path, file.Exists ? file.Length : -1,
                file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue);
        }
    }
    private sealed record CachedCatalog(string CookedPath, TextureRegistrySnapshot? Registry,
        CustomMaterialTemplateCatalogResult Metadata, IReadOnlyList<SourceStamp> Sources);

    public CustomMaterialTemplateCatalogService(
        MorphFacePackageReader reader,
        TextureRegistryStore? registry = null)
    {
        _reader = reader;
        _registry = registry ?? new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
    }

    private static readonly IReadOnlyList<TemplateSource> Sources =
    [
        new("human-male", "loc_Services_CustomTemplate_HumanMale", "human-male", "BIOG_HMM_HED_PROMorph.pcc",
            "Custom.HMM_HED_PROCustom_MDL"),
        new("human-male-npc-face", "loc_Services_CustomTemplate_HumanMale", "human-male", "BIOG_HMM_HED_PROMorph.pcc",
            "Average.HMM_HED_PROAverage_MDL", NpcFace: true),
        new("human-male-player", "loc_Services_CustomTemplate_HumanMale", "human-male", "EntryMenu.pcc",
            "BIOG_HMM_HED_PROMorph.Custom.HMM_HED_PROCustom_MDL", Games: [MorphFaceGame.LE1], PlayerScalp: true),
        new("human-male-player", "loc_Services_CustomTemplate_HumanMale", "human-male", "BioP_Char.pcc",
            "BIOG_HMM_HED_PROMorph.Custom.HMM_HED_PROCustom_MDL", Games: [MorphFaceGame.LE2], PlayerScalp: true),
        new("human-male-player", "loc_Services_CustomTemplate_HumanMale", "human-male", "BioP_Char.pcc",
            "BIOG_HMM_HED_PROMorph.Custom.HMM_HED_PROCustom_MDL_CC", Games: [MorphFaceGame.LE3], PlayerScalp: true),
        new("human-female", "loc_Services_CustomTemplate_HumanFemale", "human-female", "BIOG_HMF_HED_PROMorph_R.pcc",
            "Custom.HMF_HED_PROCustom_MDL"),
        new("human-female-npc-face", "loc_Services_CustomTemplate_HumanFemale", "human-female", "BIOG_HMF_HED_PROMorph_R.pcc",
            "Average.HMF_Blank_World_LOD0", Games: [MorphFaceGame.LE1, MorphFaceGame.LE2], NpcFace: true),
        new("human-female-npc-face", "loc_Services_CustomTemplate_HumanFemale", "human-female", "BIOG_HMF_HED_PROMorph_R.pcc",
            "Average.HMF_HED_PROBase_MDL", Games: [MorphFaceGame.LE3], NpcFace: true),
        new("human-female-player", "loc_Services_CustomTemplate_HumanFemale", "human-female", "EntryMenu.pcc",
            "BIOG_HMF_HED_PROMorph_R.Custom.HMF_HED_PROCustom_MDL", Games: [MorphFaceGame.LE1], PlayerScalp: true),
        new("human-female-player", "loc_Services_CustomTemplate_HumanFemale", "human-female", "BioP_Char.pcc",
            "BIOG_HMF_HED_PROMorph_R.Custom.HMF_HED_PROCustom_MDL", Games: [MorphFaceGame.LE2], PlayerScalp: true),
        new("human-female-player", "loc_Services_CustomTemplate_HumanFemale", "human-female", "BioP_Char.pcc",
            "BIOG_HMF_HED_PROMorph_R.Custom.HMF_HED_PROCustom_MDL_CC", Games: [MorphFaceGame.LE3], PlayerScalp: true),
        new("human-hair", "loc_Services_CustomTemplate_HumanHair", "human", "BIOG_HMM_HIR_PRO_R.pcc",
            "Short02.HMM_HIR_PROShort02_MDL", "loc_Services_CustomTemplate_HumanHair", [MorphFaceGame.LE1, MorphFaceGame.LE2]),
        new("human-hair", "loc_Services_CustomTemplate_HumanHair", "human", "BIOG_HMM_HIR_PRO_R.pcc",
            "Hair_Short02.HMM_HIR_PROShort02_MDL_CC", "loc_Services_CustomTemplate_HumanHair", [MorphFaceGame.LE3]),
        new("human-iconic-femshep-hair", "loc_Services_CustomTemplate_IconicFemShepHair", "human-female", "BIOG_HMF_HIR_PRO.pcc",
            "Hair_PROShepard.HMF_HIR_PROShepard_MDL", "loc_Services_CustomTemplate_AdvancedHair",
            [MorphFaceGame.LE1, MorphFaceGame.LE2]),
        new("human-advanced-hair", "loc_Services_CustomTemplate_HumanHair", "human", "BIOG_HMM_HIR_PRO_R.pcc",
            "Hair_Short02.HMM_HIR_PROShort02_MDL", "loc_Services_CustomTemplate_AdvancedHair", [MorphFaceGame.LE3]),
        new("human-james-hair", "loc_Services_CustomTemplate_HumanHair", "human", "BIOG_HMM_HIR_PRO_R.pcc",
            "Hair_James.HMM_HIR_PROJames_MDL", "loc_Services_CustomTemplate_JamesHair", [MorphFaceGame.LE3])
    ];

    private static readonly IReadOnlyList<IndexedTemplateSource> IndexedSources =
    [
        new("asari", "loc_Services_CustomTemplate_Asari", "asari", "ASA_HED_PROBASE_MDL", "ASA_HED"),
        new("salarian", "loc_Services_CustomTemplate_Salarian", "salarian", "SAL_HED_PROBASE_MDL", "SAL_HED"),
        new("turian", "loc_Services_CustomTemplate_Turian", "turian", "TUR_HED_PROBase_MDL", "TUR_HED"),
        new("krogan", "loc_Services_CustomTemplate_Krogan", "krogan", "KRO_HED_PROBase_MDL", "KRO_HED"),
        new("batarian", "loc_Services_CustomTemplate_Batarian", "batarian", "BAT_HED_PROBase_MDL", "BAT_HED"),
        new("vorcha", "loc_Services_CustomTemplate_Vorcha", "vorcha", "ALN_HED_PROBase_MDL", "ALN_HED",
            [MorphFaceGame.LE2, MorphFaceGame.LE3])
    ];

    public CustomMaterialTemplateCatalogResult Load(MorphFaceGame game)
    {
        lock (_catalogLock)
        {
            if (game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3))
                return CustomMaterialTemplateCatalogResult.Empty;
            var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game) ?? string.Empty;
            TextureRegistrySnapshot? registry = null;
            try { registry = _registry.ReadForLookup(game); }
            catch (Exception) { /* BuildCatalog retains the existing missing/invalid-registry warning. */ }
            if (!_catalogs.TryGetValue(game, out var cached) ||
                registry is null ||
                !string.Equals(cached.CookedPath, cookedPath, StringComparison.OrdinalIgnoreCase) ||
                !ReferenceEquals(cached.Registry, registry) ||
                cached.Sources.Any(source => source != SourceStamp.Read(source.Path)))
            {
                var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var metadata = BuildCatalog(game, sourceFiles);
                var dependencies = metadata.Options.SelectMany(option =>
                        _reader.GetMaterialEvidenceDependencies(option.Template.Source))
                    .Concat(metadata.PreviewAttachments.Select(source => source.PackagePath))
                    .Concat(sourceFiles)
                    .Concat(new[] { "BIOG_HMF_HIR_PRO.pcc", "BIOG_HMM_HIR_PRO_R.pcc" }
                        .Select(file => Path.Combine(cookedPath, file)))
                    .Concat(Sources.Where(source => source.Games is null || source.Games.Contains(game))
                        .Select(source => Path.Combine(cookedPath, source.PackageFileName)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Select(SourceStamp.Read).ToArray();
                cached = new CachedCatalog(cookedPath, registry, metadata, dependencies);
                _catalogs[game] = cached;
            }
            // Cache metadata only. Each workspace owns its selected decoded templates;
            // unused catalogue choices never retain full mip chains.
            return cached.Metadata with
            {
                IsRegistryAvailable = cached.Registry is not null,
                Options = cached.Metadata.Options.Select(option =>
                {
                    var source = option.Template.Source;
                    return option.WithDeferredTemplate(() =>
                    {
                        lock (_catalogLock) return _reader.LoadMaterialEvidence(source);
                    });
                }).ToArray()
            };
        }
    }

    private CustomMaterialTemplateCatalogResult BuildCatalog(MorphFaceGame game, HashSet<string> sourceFiles)
    {
        if (game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3))
        {
            return CustomMaterialTemplateCatalogResult.Empty;
        }

        var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game);
        if (string.IsNullOrWhiteSpace(cookedPath) || !Directory.Exists(cookedPath))
        {
            return new CustomMaterialTemplateCatalogResult([], [
                Loc.Format("loc_Services_CustomTemplate_GameInstallUnavailable", game)
            ], []);
        }

        var options = new List<CustomMaterialAssignmentOption>();
        var warnings = new List<string>();
        var officialPackages = InstalledGameAssetResolver.GetOfficialPackages(game);
        foreach (var source in Sources)
        {
            if (source.Games is not null && !source.Games.Contains(game)) continue;
            var packagePath = officialPackages.GetValueOrDefault(source.PackageFileName)
                ?? Path.Combine(cookedPath, source.PackageFileName);
            sourceFiles.Add(packagePath);
            if (!File.Exists(packagePath))
            {
                warnings.Add(Loc.Format("loc_Services_CustomTemplate_SourceNotInstalled", source.PackageFileName, game));
                continue;
            }

            MeshMaterialEvidence evidence;
            try
            {
                evidence = _reader.LoadMeshMaterialEvidence(packagePath, source.MeshInstancedPath, decodeTextures: false);
            }
            catch (Exception exception)
            {
                warnings.Add(
                    Loc.Format("loc_Services_CustomTemplate_SourceCouldNotBeRead", source.MeshInstancedPath,
                        source.PackageFileName, exception.Message));
                continue;
            }

            foreach (var slot in evidence.Slots)
            {
                var material = slot.Material;
                // The original Custom head supplies the Player face, NPC scalp
                // and existing eye/lash choices. Average supplies the NPC face;
                // the native Player seed supplies the Player scalp occurrence.
                if (source.PlayerScalp && material.Family != HeadMaterialFamily.Scalp ||
                    source.NpcFace && material.Family != HeadMaterialFamily.Skin)
                    continue;
                if (material.Family == HeadMaterialFamily.Unknown ||
                    !HeadMaterialClassifier.IsAssignableHeadFamily(material.Family))
                {
                    warnings.Add(
                        Loc.Format("loc_Services_CustomTemplate_SkippedUnprovenFamily", material.Source.InstancedPath));
                    continue;
                }

                var role = Role(material.Family);
                options.Add(new CustomMaterialAssignmentOption(
                    $"{game}:{source.Key}:{material.Source.InstancedPath}",
                    source.OptionLabelKey is null ? HumanLabel(source, material.Family) : Loc.Text(source.OptionLabelKey),
                    source.AppearanceCompatibilityKey,
                    role,
                    material.Family,
                    material)
                {
                    RandomisationProfileKey = RandomisationProfileKey(game, source.PlayerScalp || source.NpcFace
                        ? source.AppearanceCompatibilityKey : source.Key),
                    ParameterScopeKey = "human",
                    ParameterScopeLabel = Loc.Text("loc_Services_CustomTemplate_HumanScope")
                });
            }
        }

        LoadIndexedOptions(game, options, warnings, sourceFiles);

        var previewAttachments = LoadPreviewAttachments(cookedPath, warnings);
        return new CustomMaterialTemplateCatalogResult(
            options.DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(HumanChoiceOrder).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            previewAttachments);
    }

    private void LoadIndexedOptions(
        MorphFaceGame game,
        ICollection<CustomMaterialAssignmentOption> options,
        ICollection<string> warnings,
        HashSet<string> sourceFiles)
    {
        TextureRegistrySnapshot snapshot;
        try
        {
            snapshot = _registry.ReadForLookup(game);
        }
        catch (Exception exception)
        {
            warnings.Add(Loc.Format("loc_Services_CustomTemplate_TextureDatabaseRequired", game, exception.Message));
            return;
        }

        foreach (var source in IndexedSources)
        {
            if (source.Games is not null && !source.Games.Contains(game)) continue;
            var candidates = CandidatePackages(snapshot, source).ToArray();
            MeshMaterialEvidence? evidence = null;
            Exception? lastFailure = null;
            foreach (var candidate in candidates)
            {
                sourceFiles.Add(candidate.PackagePath);
                try
                {
                    evidence = _reader.LoadMeshMaterialEvidence(candidate.PackagePath, candidate.MeshPath, decodeTextures: false);
                    break;
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                }
            }
            if (evidence is null)
            {
                warnings.Add(candidates.Length == 0
                    ? Loc.Format("loc_Services_CustomTemplate_NoMaterialBearingHead", game, SourceLabel(source))
                    : Loc.Format("loc_Services_CustomTemplate_NoIndexedHeadEvidence", SourceLabel(source), lastFailure?.Message));
                continue;
            }

            foreach (var slot in evidence.Slots)
            {
                var material = slot.Material;
                if (source.Key == "asari" &&
                    material.Family is HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes)
                {
                    continue;
                }
                if (material.Family == HeadMaterialFamily.Unknown ||
                    !HeadMaterialClassifier.IsAssignableHeadFamily(material.Family))
                {
                    continue;
                }
                options.Add(new CustomMaterialAssignmentOption(
                    $"{game}:{source.Key}:{material.Source.InstancedPath}",
                    Loc.Format("loc_Services_CustomTemplate_IndexedOptionLabel", SourceLabel(source), FamilyLabel(material.Family)),
                    source.AppearanceCompatibilityKey,
                    Role(material.Family),
                    material.Family,
                    material)
                {
                    RandomisationProfileKey = RandomisationProfileKey(game, source.Key),
                    ParameterScopeKey = source.Key,
                    ParameterScopeLabel = SourceLabel(source)
                });
            }
        }
    }

    private static IEnumerable<IndexedMaterialCandidate> CandidatePackages(
        TextureRegistrySnapshot snapshot,
        IndexedTemplateSource source) => CandidatePackages(snapshot, source.MeshObjectName,
        source.TextureMarker, source.FullyQualifiedMeshPath);

    internal static IReadOnlyList<IndexedMaterialCandidate> CandidatePackages(
        TextureRegistrySnapshot snapshot,
        string meshObjectName,
        string textureMarker,
        string fullyQualifiedMeshPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var officialPackages = snapshot.OfficialPackagePaths.Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var templates = snapshot.MorphFaceTemplates
            .Where(candidate => candidate.BaseHeadPath is not null &&
                candidate.BaseHeadPath.Split('.').Last().Equals(
                    meshObjectName, StringComparison.OrdinalIgnoreCase) &&
                officialPackages.Contains(Path.GetFullPath(candidate.PackagePath)))
            .Select(candidate => new IndexedMaterialCandidate(candidate.PackagePath, candidate.BaseHeadPath!,
                candidate.MountPriority, candidate.Origin));
        var texturePackages = snapshot.Candidates
            .Where(candidate => candidate.InstancedPath.Contains(textureMarker,
                StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.EffectiveOccurrence)
            .Where(occurrence =>
                occurrence.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc &&
                officialPackages.Contains(Path.GetFullPath(occurrence.PackagePath)))
            .Select(occurrence => new IndexedMaterialCandidate(occurrence.PackagePath,
                fullyQualifiedMeshPath, occurrence.MountPriority, occurrence.Origin));
        return templates.Concat(texturePackages)
            .Where(candidate => File.Exists(candidate.PackagePath))
            .DistinctBy(candidate => $"{candidate.PackagePath}|{candidate.MeshPath}",
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(candidate => candidate.MountPriority)
            .ThenBy(candidate => candidate.Origin)
            .ThenBy(candidate => candidate.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.MeshPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<AssetIdentity> LoadPreviewAttachments(
        string cookedPath,
        ICollection<string> warnings)
    {
        var results = new List<AssetIdentity>();
        foreach (var fileName in new[] { "BIOG_HMF_HIR_PRO.pcc", "BIOG_HMM_HIR_PRO_R.pcc" })
        {
            var packagePath = Path.Combine(cookedPath, fileName);
            if (!File.Exists(packagePath)) continue;
            try
            {
                var inventory = PackageAssetInspector.Inventory(packagePath, ["SkeletalMesh"]);
                foreach (var entry in inventory.Entries
                             .Where(entry => !entry.IsDefaultObject &&
                                             !IsUnsafeAttachment(inventory.PackagePath, entry.InstancedPath)))
                {
                    try
                    {
                        _reader.ValidateDetachedAttachment(inventory.PackagePath, entry.InstancedPath);
                        results.Add(new AssetIdentity(
                            inventory.PackagePath, entry.InstancedPath, entry.UIndex, entry.ClassName));
                    }
                    catch (Exception exception)
                    {
                        warnings.Add(
                            Loc.Format("loc_Services_CustomTemplate_PreviewAttachmentSkipped", entry.InstancedPath,
                                fileName, exception.Message));
                    }
                }
            }
            catch (Exception exception)
            {
                warnings.Add(Loc.Format("loc_Services_CustomTemplate_PreviewAttachmentCatalogueUnreadable", fileName, exception.Message));
            }
        }
        return results
            .DistinctBy(value => $"{value.PackagePath}|{value.UIndex}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Stock cooked packages retain review/build variants beside the playable
    /// attachment meshes. They are not valid user-facing preview choices.
    /// </summary>
    internal static bool IsDevelopmentLeftover(string path) =>
        path.Contains("_Remaster", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("_Old", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Excludes reviewed native leftovers and audited literal object-name suffixes.
    /// </summary>
    internal static bool IsUnsafeAttachment(string packagePath, string meshPath) =>
        AttachmentMeshNamePolicy.HasExcludedSuffix(meshPath) ||
        AttachmentMeshNamePolicy.IsExcludedNativeAttachment(meshPath);

    private static string Role(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Eyes or HeadMaterialFamily.SalarianEyes or HeadMaterialFamily.TurianEyes or
            HeadMaterialFamily.KroganEyes or HeadMaterialFamily.VorchaEyes => "eyes",
        HeadMaterialFamily.Lashes => "lashes",
        HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair => "hair",
        HeadMaterialFamily.Scalp => "scalp",
        HeadMaterialFamily.Teeth => "teeth",
        _ => "head"
    };

    private static string FamilyLabel(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Skin or HeadMaterialFamily.AsariSkin or HeadMaterialFamily.SalarianSkin or
            HeadMaterialFamily.TurianSkin or HeadMaterialFamily.BatarianSkin or
            HeadMaterialFamily.KroganSkin or HeadMaterialFamily.VorchaSkin => Loc.Text("loc_Services_CustomTemplate_FamilySkin"),
        HeadMaterialFamily.Eyes or HeadMaterialFamily.SalarianEyes or HeadMaterialFamily.TurianEyes or
            HeadMaterialFamily.KroganEyes or HeadMaterialFamily.VorchaEyes => Loc.Text("loc_Services_CustomTemplate_FamilyEyes"),
        HeadMaterialFamily.Lashes => Loc.Text("loc_Services_CustomTemplate_FamilyLashes"),
        HeadMaterialFamily.Scalp => Loc.Text("loc_Services_CustomTemplate_FamilyScalpMouth"),
        HeadMaterialFamily.Hair => Loc.Text("loc_Services_CustomTemplate_FamilyHair"),
        HeadMaterialFamily.MaskedHair => Loc.Text("loc_Services_CustomTemplate_FamilyMaskedHair"),
        HeadMaterialFamily.Teeth => Loc.Text("loc_Services_CustomTemplate_FamilyTeeth"),
        HeadMaterialFamily.Unknown => Loc.Text("loc_Services_CustomTemplate_FamilyUnknown"),
        _ => family.ToString()
    };

    private static string HumanLabel(TemplateSource source, HeadMaterialFamily family) =>
        family is HeadMaterialFamily.Skin or HeadMaterialFamily.Scalp
            ? Loc.Format("loc_Services_CustomTemplate_HumanOptionLabel", Loc.Text(source.LabelKey),
                Loc.Text((family, source.PlayerScalp) switch
                {
                    (HeadMaterialFamily.Skin, _) => source.NpcFace
                        ? "loc_Services_CustomTemplate_FaceNpc" : "loc_Services_CustomTemplate_FacePlayer",
                    (HeadMaterialFamily.Scalp, false) => "loc_Services_CustomTemplate_ScalpNpc",
                    _ => "loc_Services_CustomTemplate_ScalpPlayer"
                }))
        : source.Key == "human-female" && family == HeadMaterialFamily.Eyes
            ? Loc.Text("loc_Services_CustomTemplate_HumanFemaleAsariEyes")
            : source.Key == "human-female" && family == HeadMaterialFamily.Lashes
                ? Loc.Text("loc_Services_CustomTemplate_HumanFemaleAsariLashes")
                : Loc.Format("loc_Services_CustomTemplate_HumanOptionLabel", Loc.Text(source.LabelKey), FamilyLabel(family));

    private static string SourceLabel(IndexedTemplateSource source) => Loc.Text(source.LabelKey);

    private static int HumanChoiceOrder(CustomMaterialAssignmentOption option)
    {
        if (option.AppearanceCompatibilityKey is "human-male" or "human-female" &&
            option.Family is HeadMaterialFamily.Skin or HeadMaterialFamily.Scalp or HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes)
        {
            var female = option.AppearanceCompatibilityKey == "human-female";
            var offset = female ? 6 : 0;
            if (option.Family == HeadMaterialFamily.Eyes) return offset + 4;
            if (option.Family == HeadMaterialFamily.Lashes) return offset + 5;
            var scalp = option.Family == HeadMaterialFamily.Scalp;
            var player = scalp ? option.Id.Contains("-player:", StringComparison.OrdinalIgnoreCase)
                : !option.Id.Contains("-npc-face:", StringComparison.OrdinalIgnoreCase);
            return offset + (scalp ? 2 : 0) + (player ? 1 : 0);
        }
        if (option.Family == HeadMaterialFamily.Hair)
            return option.Id.Contains(":human-hair:", StringComparison.OrdinalIgnoreCase) ? 12
                : option.Id.Contains(":human-james-hair:", StringComparison.OrdinalIgnoreCase) ? 14 : 13;
        return option.EffectiveParameterScopeKey == "human" ? 15 : 16;
    }

    private static string RandomisationProfileKey(MorphFaceGame game, string archetype)
    {
        var profileArchetype = archetype switch
        {
            // Hair has no standalone donor corpus. Its parameters are part of the
            // audited human material profiles used by the corresponding head.
            "human-hair" => "human-male",
            "human-advanced-hair" or "human-james-hair" => "human-male",
            "human-iconic-femshep-hair" => "human-female",
            _ => archetype
        };
        return $"{game.ToString().ToLowerInvariant()}-{profileArchetype}";
    }

    private sealed record TemplateSource(
        string Key,
        string LabelKey,
        string AppearanceCompatibilityKey,
        string PackageFileName,
        string MeshInstancedPath,
        string? OptionLabelKey = null,
        IReadOnlyList<MorphFaceGame>? Games = null,
        bool PlayerScalp = false,
        bool NpcFace = false);

    private sealed record IndexedTemplateSource(
        string Key,
        string LabelKey,
        string AppearanceCompatibilityKey,
        string MeshObjectName,
        string TextureMarker,
        IReadOnlyList<MorphFaceGame>? Games = null)
    {
        public string FullyQualifiedMeshPath => Key switch
        {
            "asari" => $"BIOG_ASA_HED_PROMorph_R.PROBase.{MeshObjectName}",
            "salarian" => $"BIOG_SAL_HED_PROMorph_R.{MeshObjectName}",
            "turian" => $"BIOG_TUR_HED_PROMorph_R.PROBase.{MeshObjectName}",
            "krogan" => $"BIOG_KRO_HED_PROMorph.{MeshObjectName}",
            "batarian" => $"BIOG_BAT_HED_PROMorph_R.PROBase.{MeshObjectName}",
            "vorcha" => "BIOG_ALN_HED_PROMorph_R.ALN_HED_PROBase_MDL",
            _ => MeshObjectName
        };
    }

    internal sealed record IndexedMaterialCandidate(
        string PackagePath,
        string MeshPath,
        int MountPriority,
        TextureCatalogOrigin Origin);
}
