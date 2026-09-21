using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
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

    public CustomMaterialTemplateCatalogService(
        MorphFacePackageReader reader,
        TextureRegistryStore? registry = null)
    {
        _reader = reader;
        _registry = registry ?? new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
    }

    private static readonly IReadOnlyList<TemplateSource> Sources =
    [
        new("human-male", "Human Male", "human-male", "BIOG_HMM_HED_PROMorph.pcc",
            "Custom.HMM_HED_PROCustom_MDL"),
        new("human-female", "Human Female", "human-female", "BIOG_HMF_HED_PROMorph_R.pcc",
            "Custom.HMF_HED_PROCustom_MDL"),
        new("human-hair", "Human Hair", "human", "BIOG_HMM_HIR_PRO_R.pcc",
            "Short02.HMM_HIR_PROShort02_MDL", "Human Hair", [MorphFaceGame.LE1, MorphFaceGame.LE2]),
        new("human-hair", "Human Hair", "human", "BIOG_HMM_HIR_PRO_R.pcc",
            "Hair_Short02.HMM_HIR_PROShort02_MDL_CC", "Human Hair", [MorphFaceGame.LE3]),
        new("human-iconic-femshep-hair", "Human Iconic FemShep", "human-female", "BIOG_HMF_HIR_PRO.pcc",
            "Hair_PROShepard.HMF_HIR_PROShepard_MDL", "Human Iconic FemShep - Hair",
            [MorphFaceGame.LE1, MorphFaceGame.LE2])
    ];

    private static readonly IReadOnlyList<IndexedTemplateSource> IndexedSources =
    [
        new("asari", "Asari", "asari", "ASA_HED_PROBASE_MDL", "ASA_HED"),
        new("salarian", "Salarian", "salarian", "SAL_HED_PROBASE_MDL", "SAL_HED"),
        new("turian", "Turian", "turian", "TUR_HED_PROBase_MDL", "TUR_HED"),
        new("krogan", "Krogan", "krogan", "KRO_HED_PROBase_MDL", "KRO_HED"),
        new("batarian", "Batarian", "batarian", "BAT_HED_PROBase_MDL", "BAT_HED"),
        new("vorcha", "Vorcha", "vorcha", "ALN_HED_PROBase_MDL", "ALN_HED",
            [MorphFaceGame.LE2, MorphFaceGame.LE3])
    ];

    public CustomMaterialTemplateCatalogResult Load(MorphFaceGame game)
    {
        if (game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3))
        {
            return CustomMaterialTemplateCatalogResult.Empty;
        }

        var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game);
        if (string.IsNullOrWhiteSpace(cookedPath) || !Directory.Exists(cookedPath))
        {
            return new CustomMaterialTemplateCatalogResult([], [
                $"{game} is not installed or its CookedPCConsole path is unavailable; Custom material assignment is disabled."
            ], []);
        }

        var options = new List<CustomMaterialAssignmentOption>();
        var warnings = new List<string>();
        foreach (var source in Sources)
        {
            if (source.Games is not null && !source.Games.Contains(game)) continue;
            var packagePath = Path.Combine(cookedPath, source.PackageFileName);
            if (!File.Exists(packagePath))
            {
                warnings.Add($"Custom material source '{source.PackageFileName}' is not installed for {game}.");
                continue;
            }

            MeshMaterialEvidence evidence;
            try
            {
                evidence = _reader.LoadMeshMaterialEvidence(packagePath, source.MeshInstancedPath);
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Custom material source '{source.MeshInstancedPath}' could not be read from " +
                    $"{source.PackageFileName}: {exception.Message}");
                continue;
            }

            foreach (var slot in evidence.Slots)
            {
                var material = slot.Material;
                if (material.Family == HeadMaterialFamily.Unknown ||
                    !HeadMaterialClassifier.IsAssignableHeadFamily(material.Family))
                {
                    warnings.Add(
                        $"Skipped '{material.Source.InstancedPath}' because its effective master does not prove " +
                        "an implemented head-material family.");
                    continue;
                }

                var role = Role(material.Family);
                options.Add(new CustomMaterialAssignmentOption(
                    $"{game}:{source.Key}:{material.Source.InstancedPath}",
                    source.OptionLabel ?? HumanLabel(source, material.Family),
                    source.AppearanceCompatibilityKey,
                    role,
                    material.Family,
                    material)
                {
                    RandomisationProfileKey = RandomisationProfileKey(game, source.Key),
                    ParameterScopeKey = "human",
                    ParameterScopeLabel = "Human"
                });
            }
        }

        LoadIndexedOptions(game, options, warnings);

        var previewAttachments = LoadPreviewAttachments(cookedPath, warnings);
        return new CustomMaterialTemplateCatalogResult(
            options.DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            previewAttachments);
    }

    private void LoadIndexedOptions(
        MorphFaceGame game,
        ICollection<CustomMaterialAssignmentOption> options,
        ICollection<string> warnings)
    {
        TextureRegistrySnapshot snapshot;
        try
        {
            snapshot = _registry.Read(game);
        }
        catch (Exception exception)
        {
            warnings.Add($"NPC Custom materials require the {game} texture database: {exception.Message}");
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
                try
                {
                    evidence = _reader.LoadMeshMaterialEvidence(candidate.PackagePath, candidate.MeshPath);
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
                    ? $"The {game} texture database contains no material-bearing {source.Label} head."
                    : $"No indexed {source.Label} head could provide Custom material evidence: {lastFailure?.Message}");
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
                    $"{source.Label} {FamilyLabel(material.Family)}",
                    source.AppearanceCompatibilityKey,
                    Role(material.Family),
                    material.Family,
                    material)
                {
                    RandomisationProfileKey = RandomisationProfileKey(game, source.Key),
                    ParameterScopeKey = source.Key,
                    ParameterScopeLabel = source.Label
                });
            }
        }
    }

    private static IEnumerable<IndexedMaterialCandidate> CandidatePackages(
        TextureRegistrySnapshot snapshot,
        IndexedTemplateSource source)
    {
        var templates = snapshot.MorphFaceTemplates
            .Where(candidate => candidate.BaseHeadPath is not null &&
                candidate.BaseHeadPath.Split('.').Last().Equals(
                    source.MeshObjectName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Origin)
            .ThenByDescending(candidate => candidate.MountPriority)
            .ThenBy(candidate => candidate.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new IndexedMaterialCandidate(candidate.PackagePath, candidate.BaseHeadPath!));
        var texturePackages = snapshot.Candidates
            .Where(candidate => candidate.InstancedPath.Contains(
                source.TextureMarker, StringComparison.OrdinalIgnoreCase))
            .SelectMany(candidate => candidate.Occurrences)
            .OrderBy(occurrence => occurrence.Origin)
            .ThenByDescending(occurrence => occurrence.MountPriority)
            .ThenBy(occurrence => occurrence.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(occurrence => new IndexedMaterialCandidate(
                occurrence.PackagePath, source.FullyQualifiedMeshPath));
        return templates.Concat(texturePackages)
            .Where(candidate => File.Exists(candidate.PackagePath))
            .DistinctBy(candidate => $"{candidate.PackagePath}|{candidate.MeshPath}",
                StringComparer.OrdinalIgnoreCase);
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
                            $"Preview attachment '{entry.InstancedPath}' in '{fileName}' was skipped because " +
                            $"its mesh/material data is not loadable: {exception.Message}");
                    }
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"Preview attachment catalogue '{fileName}' could not be read: {exception.Message}");
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
    /// Review meshes shipped in BIOG HIR packages are build/review variants and
    /// are not safe attachment choices for an in-game workspace.
    /// </summary>
    internal static bool IsUnsafeAttachment(string packagePath, string meshPath) =>
        IsDevelopmentLeftover(meshPath) ||
        (IsBiogHirPackage(packagePath) &&
         meshPath.Contains("Review", StringComparison.OrdinalIgnoreCase));

    private static bool IsBiogHirPackage(string packagePath)
    {
        var packageName = Path.GetFileNameWithoutExtension(packagePath);
        return packageName.StartsWith("BIOG_", StringComparison.OrdinalIgnoreCase) &&
               packageName.Contains("_HIR", StringComparison.OrdinalIgnoreCase);
    }

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
            HeadMaterialFamily.KroganSkin or HeadMaterialFamily.VorchaSkin => "Skin",
        HeadMaterialFamily.Eyes or HeadMaterialFamily.SalarianEyes or HeadMaterialFamily.TurianEyes or
            HeadMaterialFamily.KroganEyes or HeadMaterialFamily.VorchaEyes => "Eyes",
        HeadMaterialFamily.Lashes => "Lashes",
        HeadMaterialFamily.Scalp => "Scalp / Mouth",
        HeadMaterialFamily.Hair => "Hair",
        HeadMaterialFamily.MaskedHair => "Masked Hair",
        HeadMaterialFamily.Teeth => "Teeth",
        _ => family.ToString()
    };

    private static string HumanLabel(TemplateSource source, HeadMaterialFamily family) =>
        source.Key == "human-female" && family == HeadMaterialFamily.Eyes
            ? "Human Female/Asari Eyes"
            : source.Key == "human-female" && family == HeadMaterialFamily.Lashes
                ? "Human Female/Asari Lashes"
                : $"{source.Label} - {FamilyLabel(family)}";

    private static string RandomisationProfileKey(MorphFaceGame game, string archetype)
    {
        var profileArchetype = archetype switch
        {
            // Hair has no standalone donor corpus. Its parameters are part of the
            // audited human material profiles used by the corresponding head.
            "human-hair" => "human-male",
            "human-iconic-femshep-hair" => "human-female",
            _ => archetype
        };
        return $"{game.ToString().ToLowerInvariant()}-{profileArchetype}";
    }

    private sealed record TemplateSource(
        string Key,
        string Label,
        string AppearanceCompatibilityKey,
        string PackageFileName,
        string MeshInstancedPath,
        string? OptionLabel = null,
        IReadOnlyList<MorphFaceGame>? Games = null);

    private sealed record IndexedTemplateSource(
        string Key,
        string Label,
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

    private sealed record IndexedMaterialCandidate(string PackagePath, string MeshPath);
}
