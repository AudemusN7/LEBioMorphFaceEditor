using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

public sealed record MorphFaceProfile(
    string Key,
    string DisplayName,
    MorphFaceGame Game,
    string TargetPackageName,
    IReadOnlySet<string> MetadataOnlyFeatures,
    IReadOnlyDictionary<string, string> FeatureAliases,
    IHeadEditorUiProfile UiProfile,
    string ExportTag,
    string ExportTagColor,
    Func<DeformationComparisonReport, bool> RecognizesBaseVariant,
    Func<string, string?, bool> Matches)
{
    public string? TargetSetName { get; init; }
    public bool IgnoresAuthoredGeometry { get; init; }
    public Func<string?, string?> GeometryEditBlockReason { get; init; } = _ => null;
}

public sealed record MorphFaceProfileResolution(
    MorphFaceProfile Profile,
    bool UsesCustomMesh);

/// <summary>
/// Owns the supported head systems and keeps sex/species selection out of the
/// shared catalogue, editing, and rendering workflows.
/// </summary>
public sealed class MorphFaceProfileRegistry
{
    private readonly IReadOnlyList<MorphFaceProfile> _profiles;

    public MorphFaceProfileRegistry(IReadOnlyList<MorphFaceProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
        {
            throw new ArgumentException("At least one morph-face profile is required.", nameof(profiles));
        }
        _profiles = profiles;
    }

    public IReadOnlyList<MorphFaceProfile> Profiles => _profiles;

    public MorphFaceProfile? Find(MorphFaceGame game, string facePath, string? baseHeadPath) =>
        _profiles.FirstOrDefault(profile =>
            profile.Game == game && profile.Matches(facePath, baseHeadPath));

    public MorphFaceProfileResolution? Resolve(
        MorphFaceGame game,
        string facePath,
        string? baseHeadPath,
        Core.Materials.MorphFaceMaterialOverrides materialOverrides,
        Core.Materials.ResolvedHeadMaterialSet materials)
    {
        ArgumentNullException.ThrowIfNull(materialOverrides);
        ArgumentNullException.ThrowIfNull(materials);
        var recognised = Find(game, facePath, baseHeadPath);
        if (recognised is not null)
        {
            return new MorphFaceProfileResolution(recognised, false);
        }

        var profileSuffix = InferProfileSuffix(materialOverrides, materials);
        var customProfile = profileSuffix is null
            ? null
            : _profiles.FirstOrDefault(profile =>
                profile.Game == game &&
                profile.Key.EndsWith(profileSuffix, StringComparison.OrdinalIgnoreCase));
        return customProfile is null
            ? null
            : new MorphFaceProfileResolution(customProfile, true);
    }

    public MorphFaceProfile Require(MorphFaceGame game, string facePath, string? baseHeadPath) =>
        Find(game, facePath, baseHeadPath) ?? throw new NotSupportedException(
            $"{game} BioMorphFace '{facePath}' uses unsupported base head '{baseHeadPath ?? "<unresolved>"}'.");

    public static MorphFaceProfileRegistry CreateDefault() => new(
    [
        new MorphFaceProfile(
            "le1-human-male",
            "LE1 Human Male",
            MorphFaceGame.LE1,
            "BIOG_HMM_HED_PROMorph.pcc",
            MorphFeatureTargetResolver.HumanMaleMetadataOnlyFeatures,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new HumanMaleFeatureMetadataCatalog(),
            "[HMM]",
            "#287CCB",
            _ => false,
            (facePath, baseHeadPath) => MatchesHuman(
                facePath, baseHeadPath, HumanMaleMorphMeshes, "HMM", "Human Male"))
        { TargetSetName = "HMM_BaseMorphSet" },
        new MorphFaceProfile(
            "le1-human-female",
            "LE1 Human Female",
            MorphFaceGame.LE1,
            "BIOG_HMF_HED_PROMorph_R.pcc",
            MorphFeatureTargetResolver.HumanFemaleMetadataOnlyFeatures,
            MorphFeatureTargetResolver.HumanFemaleFeatureAliases,
            new HumanFemaleFeatureMetadataCatalog(),
            "[HMF]",
            "#C02B9B",
            IsLe1HumanFemaleLegacyBaseVariant,
            (facePath, baseHeadPath) => MatchesHuman(
                facePath, baseHeadPath, HumanFemaleMorphMeshes, "HMF", "Human Female"))
        { TargetSetName = "HMF_BaseMorphSet" },
        CreateAsariProfile(MorphFaceGame.LE1, null),
        CreateSalarianProfile(MorphFaceGame.LE1, null),
        CreateTurianProfile(MorphFaceGame.LE1),
        CreateFemaleTurianProfile(MorphFaceGame.LE1),
        CreateBatarianProfile(MorphFaceGame.LE1),
        CreateKroganProfile(MorphFaceGame.LE1),
        new MorphFaceProfile(
            "le2-human-male",
            "LE2 Human Male",
            MorphFaceGame.LE2,
            "BIOG_HMM_HED_PROMorph.pcc",
            MorphFeatureTargetResolver.HumanMaleMetadataOnlyFeatures,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new HumanMaleFeatureMetadataCatalog(),
            "[HMM]",
            "#287CCB",
            _ => false,
            (facePath, baseHeadPath) => MatchesHuman(
                facePath, baseHeadPath, HumanMaleMorphMeshes, "HMM", "Human Male"))
        { TargetSetName = "HMM_BaseMorphSet" },
        new MorphFaceProfile(
            "le2-human-female",
            "LE2 Human Female",
            MorphFaceGame.LE2,
            "BIOG_HMF_HED_PROMorph_R.pcc",
            MorphFeatureTargetResolver.HumanFemaleMetadataOnlyFeatures,
            MorphFeatureTargetResolver.HumanFemaleFeatureAliases,
            new HumanFemaleFeatureMetadataCatalog(),
            "[HMF]",
            "#C02B9B",
            _ => false,
            (facePath, baseHeadPath) => MatchesHuman(
                facePath, baseHeadPath, HumanFemaleMorphMeshes, "HMF", "Human Female"))
        { TargetSetName = "HMF_BaseMorphSet" },
        CreateAsariProfile(MorphFaceGame.LE2, "BioP_TwrHub.pcc"),
        CreateSalarianProfile(MorphFaceGame.LE2, "BioP_TwrHub.pcc"),
        CreateTurianProfile(MorphFaceGame.LE2),
        CreateFemaleTurianProfile(MorphFaceGame.LE2),
        CreateBatarianProfile(MorphFaceGame.LE2),
        CreateKroganProfile(MorphFaceGame.LE2),
        CreateVorchaProfile(MorphFaceGame.LE2),
        new MorphFaceProfile(
            "le3-human-male",
            "LE3 Human Male",
            MorphFaceGame.LE3,
            "BIOG_HMM_HED_PROMorph.pcc",
            MorphFeatureTargetResolver.Le3HumanMaleMetadataOnlyFeatures,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Le3HumanMaleFeatureMetadataCatalog(),
            "[HMM]",
            "#287CCB",
            _ => false,
            (facePath, baseHeadPath) => MatchesHuman(
                facePath, baseHeadPath, HumanMaleMorphMeshes, "HMM", "Human Male"))
        { TargetSetName = "HMM_BaseMorphSet" },
        new MorphFaceProfile(
            "le3-human-female",
            "LE3 Human Female",
            MorphFaceGame.LE3,
            "BIOG_HMF_HED_PROMorph_R.pcc",
            MorphFeatureTargetResolver.Le3HumanFemaleMetadataOnlyFeatures,
            MorphFeatureTargetResolver.HumanFemaleFeatureAliases,
            new Le3HumanFemaleFeatureMetadataCatalog(),
            "[HMF]",
            "#C02B9B",
            _ => false,
            (facePath, baseHeadPath) => MatchesHuman(
                facePath, baseHeadPath, HumanFemaleMorphMeshes, "HMF", "Human Female"))
        { TargetSetName = "HMF_BaseMorphSet" },
        CreateAsariProfile(MorphFaceGame.LE3, "BIOG_ASA_HED_PROMorph_R.pcc"),
        CreateSalarianProfile(MorphFaceGame.LE3, "BIOG_SAL_HED_PROMorph_R.pcc"),
        CreateTurianProfile(MorphFaceGame.LE3),
        CreateFemaleTurianProfile(MorphFaceGame.LE3),
        CreateBatarianProfile(MorphFaceGame.LE3),
        CreateKroganProfile(MorphFaceGame.LE3),
        CreateVorchaProfile(MorphFaceGame.LE3)
    ]);

    private static string? InferProfileSuffix(
        Core.Materials.MorphFaceMaterialOverrides materialOverrides,
        Core.Materials.ResolvedHeadMaterialSet materials)
    {
        var families = materials.Materials.Values
            .Select(material => material.Family)
            .ToHashSet();
        if (families.Contains(Core.Materials.HeadMaterialFamily.VorchaSkin) ||
            families.Contains(Core.Materials.HeadMaterialFamily.VorchaEyes)) return "-vorcha";
        if (families.Contains(Core.Materials.HeadMaterialFamily.BatarianSkin)) return "-batarian";
        if (families.Contains(Core.Materials.HeadMaterialFamily.KroganSkin) ||
            families.Contains(Core.Materials.HeadMaterialFamily.KroganEyes)) return "-krogan";
        if (families.Contains(Core.Materials.HeadMaterialFamily.TurianSkin) ||
            families.Contains(Core.Materials.HeadMaterialFamily.TurianEyes)) return "-turian";
        if (families.Contains(Core.Materials.HeadMaterialFamily.SalarianSkin) ||
            families.Contains(Core.Materials.HeadMaterialFamily.SalarianEyes)) return "-salarian";
        if (families.Contains(Core.Materials.HeadMaterialFamily.AsariSkin)) return "-asari";

        var evidence = string.Join('|', materials.Materials.Values.SelectMany(material => new[]
            {
                material.Source.InstancedPath,
                material.MasterMaterialName
            })
            .Concat(materialOverrides.Source is null ? [] : [materialOverrides.Source.InstancedPath])
            .Concat(materialOverrides.Scalars.Select(value => value.Name))
            .Concat(materialOverrides.Vectors.Select(value => value.Name))
            .Concat(materialOverrides.Textures.Select(value => value.Name)));
        if (ContainsMarker(evidence, "ALN_", "Vorcha")) return "-vorcha";
        if (ContainsMarker(evidence, "BAT_", "Batarian")) return "-batarian";
        if (ContainsMarker(evidence, "KRO_", "Krogan")) return "-krogan";
        if (ContainsMarker(evidence, "TUR_", "Turian")) return "-turian";
        if (ContainsMarker(evidence, "SAL_", "Salarian")) return "-salarian";
        if (ContainsMarker(evidence, "ASA_", "Asari")) return "-asari";
        if (ContainsMarker(evidence, "HMF_", "Human Female")) return "-human-female";
        if (ContainsMarker(evidence, "HMM_", "Human Male")) return "-human-male";
        return null;
    }

    private static bool ContainsMarker(string evidence, params string[] markers) =>
        markers.Any(marker => evidence.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static MorphFaceProfile CreateAsariProfile(MorphFaceGame game, string? targetPackageName) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-asari",
            $"{game} Asari",
            game,
            targetPackageName ?? string.Empty,
            AsariFeatureMetadataCatalog.MetadataOnlyFeatures,
            AsariFeatureMetadataCatalog.FeatureAliases,
            new AsariFeatureMetadataCatalog(game == MorphFaceGame.LE3),
            "[ASA]",
            "#6635BA",
            _ => false,
            MatchesAsari)
        {
            TargetSetName = "ASA_BaseMorphSet"
        };

    private static bool MatchesAsari(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return baseHeadPath.Contains("ASA_HED_PROBASE_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Asari.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateSalarianProfile(MorphFaceGame game, string? targetPackageName) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-salarian",
            $"{game} Salarian",
            game,
            targetPackageName ?? string.Empty,
            SalarianFeatureMetadataCatalog.MetadataOnlyFeatures,
            SalarianFeatureMetadataCatalog.FeatureAliases,
            new SalarianFeatureMetadataCatalog(),
            "[SAL]",
            "#1F9139",
            _ => false,
            MatchesSalarian)
        {
            TargetSetName = "SAL_BaseMorphSet"
        };

    private static bool MatchesSalarian(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return baseHeadPath.Contains("SAL_HED_PROBASE_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Salarian.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateTurianProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-turian",
            $"{game} Turian",
            game,
            string.Empty,
            TurianFeatureMetadataCatalog.MetadataOnlyFeatures,
            TurianFeatureMetadataCatalog.FeatureAliases,
            new TurianFeatureMetadataCatalog(),
            "[TUR]",
            "#008C83",
            _ => false,
            MatchesTurian)
        {
            TargetSetName = "TUR_BaseMorphSet"
        };

    private static bool MatchesTurian(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return baseHeadPath.Contains("TUR_HED_PROBASE_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Turian.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateFemaleTurianProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-female-turian",
            $"{game} Female Turian",
            game,
            string.Empty,
            FemaleTurianFeatureMetadataCatalog.MetadataOnlyFeatures,
            TurianFeatureMetadataCatalog.FeatureAliases,
            new FemaleTurianFeatureMetadataCatalog(),
            "[TUF]",
            "#99597D",
            _ => false,
            MatchesFemaleTurian)
        {
            IgnoresAuthoredGeometry = true,
            GeometryEditBlockReason = _ =>
                "Female Turian morph, bone, and baked LOD data currently contains inherited male Turian payloads and is intentionally ignored. Material editing remains available."
        };

    private static bool MatchesFemaleTurian(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return baseHeadPath.Contains("TUF_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Female Turian.", StringComparison.OrdinalIgnoreCase) ||
               facePath.Contains(".TUF_", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateKroganProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-krogan",
            $"{game} Krogan",
            game,
            string.Empty,
            KroganFeatureMetadataCatalog.MetadataOnlyFeatures,
            KroganFeatureMetadataCatalog.FeatureAliases,
            new KroganFeatureMetadataCatalog(),
            "[KRO]",
            "#781400",
            _ => false,
            MatchesKrogan)
        {
            TargetSetName = "KRO_baseMorphSet"
        };

    private static bool MatchesKrogan(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return baseHeadPath.Contains("KRO_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Krogan.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateBatarianProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-batarian",
            $"{game} Batarian",
            game,
            string.Empty,
            BatarianFeatureMetadataCatalog.MetadataOnlyFeatures,
            BatarianFeatureMetadataCatalog.FeatureAliases,
            new BatarianFeatureMetadataCatalog(),
            "[BAT]",
            "#A16F10",
            _ => false,
            MatchesBatarian)
        {
            TargetSetName = "BAT_BaseMorphSet",
            GeometryEditBlockReason = game == MorphFaceGame.LE3
                ? baseHeadPath => baseHeadPath?.Contains(
                        "BIOG_BAT_OMEGA_HED", StringComparison.OrdinalIgnoreCase) == true
                    ? "This LE3 Batarian uses the independently cooked Omega base mesh; morph and bone controls are disabled because the canonical BAT target indices are incompatible. Material editing remains available."
                    : null
                : _ => null
        };

    private static bool MatchesBatarian(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return baseHeadPath.Contains("BAT_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Batarian.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateVorchaProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-vorcha",
            $"{game} Vorcha",
            game,
            "BIOG_ALN_HED_PROMorph_R.pcc",
            VorchaFeatureMetadataCatalog.MetadataOnlyFeatures,
            VorchaFeatureMetadataCatalog.FeatureAliases,
            new VorchaFeatureMetadataCatalog(),
            "[ALN]",
            "#909110",
            _ => false,
            MatchesVorcha)
        {
            TargetSetName = "ALN_ReconstructedMorphSet"
        };

    private static bool MatchesVorcha(string facePath, string? baseHeadPath) =>
        !string.IsNullOrWhiteSpace(baseHeadPath)
            ? baseHeadPath.Contains("ALN_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase)
            : facePath.StartsWith("Vorcha.", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesHuman(
        string facePath,
        string? baseHeadPath,
        IReadOnlyList<string> morphMeshNames,
        string pathSegment,
        string displayPrefix)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return morphMeshNames.Any(name =>
                       baseHeadPath.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
                   !baseHeadPath.Contains("TUR_", StringComparison.OrdinalIgnoreCase);
        }

        return facePath.StartsWith(pathSegment + ".", StringComparison.OrdinalIgnoreCase) ||
               facePath.Contains("." + pathSegment + ".", StringComparison.OrdinalIgnoreCase) ||
               facePath.StartsWith(displayPrefix + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] HumanMaleMorphMeshes =
    [
        "HMM_HED_PROAverage_MDL",
        "HMM_HED_PROBase_MDL"
    ];

    private static readonly string[] HumanFemaleMorphMeshes =
    [
        "HMF_Blank_World_LOD0",
        "HMF_HED_PROBase_MDL"
    ];

    private static bool IsLe1HumanFemaleLegacyBaseVariant(DeformationComparisonReport report)
    {
        // Most shipped HMF faces were baked against the original LE1 eye-card
        // base while the remaster target package exposes the revised base.
        // The delta is a stable 113-vertex signature across the 95-face corpus.
        return report.ComparedVertexCount == 2232 &&
               report.VerticesAboveTolerance is >= 110 and <= 116 &&
               report.MaximumError is >= 0.02911f and <= 0.02913f &&
               report.RootMeanSquareError is >= 0.00487f and <= 0.00490f &&
               report.LargestErrors.Any(error => error.SourceIndex is >= 78 and <= 88);
    }
}
