using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

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
/// Loads the deliberately small Stage C1 assignment catalogue from exact installed
/// HMM/HMF material-bearing meshes. Package traversal and shader evidence remain in
/// the LegendaryExplorer adapter; this service only declares reviewed source assets.
/// </summary>
public sealed class CustomMaterialTemplateCatalogService(MorphFacePackageReader reader)
    : ICustomMaterialTemplateCatalog
{
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
                evidence = reader.LoadMeshMaterialEvidence(packagePath, source.MeshInstancedPath);
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
                    source.OptionLabel ?? $"{source.Label} - {FamilyLabel(material.Family)}",
                    source.AppearanceCompatibilityKey,
                    role,
                    material.Family,
                    material));
            }
        }

        var previewAttachments = LoadPreviewAttachments(cookedPath, warnings);
        return new CustomMaterialTemplateCatalogResult(
            options.DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            previewAttachments);
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
                             .Where(entry => !entry.IsDefaultObject && !IsDevelopmentLeftover(entry.InstancedPath)))
                {
                    try
                    {
                        reader.ValidateDetachedAttachment(inventory.PackagePath, entry.InstancedPath);
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
        HeadMaterialFamily.Skin => "Skin",
        HeadMaterialFamily.Eyes => "Eyes",
        HeadMaterialFamily.Lashes => "Lashes",
        HeadMaterialFamily.Scalp => "Scalp / Mouth",
        HeadMaterialFamily.Hair => "Hair",
        HeadMaterialFamily.MaskedHair => "Masked Hair",
        HeadMaterialFamily.Teeth => "Teeth",
        _ => family.ToString()
    };

    private sealed record TemplateSource(
        string Key,
        string Label,
        string AppearanceCompatibilityKey,
        string PackageFileName,
        string MeshInstancedPath,
        string? OptionLabel = null,
        IReadOnlyList<MorphFaceGame>? Games = null);
}
