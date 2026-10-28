using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Domain;

public sealed record MorphFaceProfile(
    string Key,
    MorphFaceGame Game,
    IReadOnlySet<string> MetadataOnlyFeatures,
    IReadOnlyDictionary<string, string> FeatureAliases,
    string ExportTag,
    Func<DeformationComparisonReport, bool> RecognizesBaseVariant,
    Func<string, string?, bool> Matches)
{
    public string Archetype => HeadProfileIdentity.SuffixFromProfileKey(Key)!;
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
        Core.Materials.ResolvedHeadMaterialSet materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var recognised = Find(game, facePath, baseHeadPath);
        if (recognised is not null)
        {
            return new MorphFaceProfileResolution(recognised, false);
        }

        var profileSuffix = InferProfileSuffix(game, materials);
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
        new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 }.SelectMany(game =>
        {
            var profiles = new List<MorphFaceProfile>
            {
                CreateHumanMaleProfile(game),
                CreateHumanFemaleProfile(game),
                CreateAsariProfile(game),
                CreateSalarianProfile(game),
                CreateTurianProfile(game),
                CreateFemaleTurianProfile(game),
                CreateBatarianProfile(game),
                CreateFemaleBatarianProfile(game),
                CreateKroganProfile(game)
            };
            if (game != MorphFaceGame.LE1) profiles.Add(CreateVorchaProfile(game));
            return profiles;
        }).ToArray());

    private static MorphFaceProfile CreateHumanMaleProfile(MorphFaceGame game) => new(
        $"{game.ToString().ToLowerInvariant()}-human-male",
        game,
        game == MorphFaceGame.LE3
            ? MorphFaceProfileFeatures.Le3HumanMaleMetadataOnlyFeatures
            : MorphFaceProfileFeatures.HumanMaleMetadataOnlyFeatures,
        game == MorphFaceGame.LE3
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : MorphFaceProfileFeatures.Le12HumanMaleFeatureAliases,
        "[HMM]",

        _ => false,
        (facePath, baseHeadPath) => MatchesHuman(
            facePath, baseHeadPath, HumanMaleMorphMeshes, "HMM", "Human Male",
            allowCharacterCreationVariant: game == MorphFaceGame.LE3))
    { TargetSetName = "HMM_BaseMorphSet" };

    private static MorphFaceProfile CreateHumanFemaleProfile(MorphFaceGame game) => new(
        $"{game.ToString().ToLowerInvariant()}-human-female",
        game,
        game == MorphFaceGame.LE3
            ? MorphFaceProfileFeatures.Le3HumanFemaleMetadataOnlyFeatures
            : MorphFaceProfileFeatures.HumanFemaleMetadataOnlyFeatures,
        MorphFaceProfileFeatures.NoAliases,
        "[HMF]",

        game == MorphFaceGame.LE1 ? IsLe1HumanFemaleLegacyBaseVariant : _ => false,
        (facePath, baseHeadPath) => MatchesHuman(
            facePath, baseHeadPath, HumanFemaleMorphMeshes, "HMF", "Human Female",
            allowCharacterCreationVariant: game == MorphFaceGame.LE3))
    { TargetSetName = "HMF_BaseMorphSet" };

    private static string? InferProfileSuffix(
        MorphFaceGame game,
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

        return materials.Materials.Values
            .Select(material => HeadMaterialCatalog.Find(game, material.MasterMaterialName)?.ProfileSuffix)
            .FirstOrDefault(suffix => suffix is not null) is { } suffix ? "-" + suffix : null;
    }
    private static MorphFaceProfile CreateAsariProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-asari",
            game,
            MorphFaceProfileFeatures.AsariMetadataOnlyFeatures,
            MorphFaceProfileFeatures.NoAliases,
            "[ASA]",

            _ => false,
            MatchesAsari)
        {
            TargetSetName = "ASA_BaseMorphSet"
        };

    private static bool MatchesAsari(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return HeadProfileIdentity.InferSuffix(baseHeadPath) == "asari" &&
                   baseHeadPath.Contains("ASA_HED_PROBASE_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Asari.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateSalarianProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-salarian",
            game,
            MorphFaceProfileFeatures.SalarianMetadataOnlyFeatures,
            MorphFaceProfileFeatures.NoAliases,
            "[SAL]",

            _ => false,
            MatchesSalarian)
        {
            TargetSetName = "SAL_BaseMorphSet"
        };

    private static bool MatchesSalarian(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return HeadProfileIdentity.InferSuffix(baseHeadPath) == "salarian" &&
                   baseHeadPath.Contains("SAL_HED_PROBASE_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Salarian.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateTurianProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-turian",
            game,
            MorphFaceProfileFeatures.TurianMetadataOnlyFeatures,
            MorphFaceProfileFeatures.NoAliases,
            "[TUR]",

            _ => false,
            MatchesTurian)
        {
            TargetSetName = "TUR_BaseMorphSet"
        };

    private static bool MatchesTurian(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return HeadProfileIdentity.InferSuffix(baseHeadPath) == "turian" &&
                   baseHeadPath.Contains("TUR_HED_PROBASE_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Turian.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateFemaleTurianProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-female-turian",
            game,
            MorphFaceProfileFeatures.FemaleTurianMetadataOnlyFeatures,
            MorphFaceProfileFeatures.NoAliases,
            "[TUF]",

            _ => false,
            MatchesFemaleTurian)
        {
            TargetSetName = "TUF_BaseMorphSet"
        };

    private static bool MatchesFemaleTurian(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return HeadProfileIdentity.InferSuffix(baseHeadPath) == "female-turian" &&
                   baseHeadPath.Contains("TUF_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Female Turian.", StringComparison.OrdinalIgnoreCase) ||
               facePath.Contains(".TUF_", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateKroganProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-krogan",
            game,
            MorphFaceProfileFeatures.KroganMetadataOnlyFeatures,
            MorphFaceProfileFeatures.NoAliases,
            "[KRO]",

            _ => false,
            MatchesKrogan)
        {
            TargetSetName = "KRO_baseMorphSet"
        };

    private static bool MatchesKrogan(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return HeadProfileIdentity.InferSuffix(baseHeadPath) == "krogan" &&
                   baseHeadPath.Contains("KRO_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Krogan.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateBatarianProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-batarian",
            game,
            MorphFaceProfileFeatures.BatarianMetadataOnlyFeatures,
            MorphFaceProfileFeatures.NoAliases,
            "[BAT]",

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

    private static MorphFaceProfile CreateFemaleBatarianProfile(MorphFaceGame game) =>
        CreateBatarianProfile(game) with
        {
            Key = $"{game.ToString().ToLowerInvariant()}-female-batarian",
            ExportTag = "[BAF]",
            Matches = MatchesFemaleBatarian,
            IgnoresAuthoredGeometry = true,
            GeometryEditBlockReason = _ =>
                "Female Batarian morph, bone, and baked LOD data contains inherited male Batarian payloads and is intentionally ignored. Material editing remains available."
        };

    private static bool MatchesFemaleBatarian(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return HeadProfileIdentity.InferSuffix(baseHeadPath) == "female-batarian" &&
                   baseHeadPath.Contains("BAF_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("BAF.", StringComparison.OrdinalIgnoreCase) ||
               facePath.StartsWith("Female Batarian.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesBatarian(string facePath, string? baseHeadPath)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            return HeadProfileIdentity.InferSuffix(baseHeadPath) == "batarian" &&
                   baseHeadPath.Contains("BAT_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase);
        }
        return facePath.StartsWith("Batarian.", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphFaceProfile CreateVorchaProfile(MorphFaceGame game) =>
        new(
            $"{game.ToString().ToLowerInvariant()}-vorcha",
            game,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            MorphFaceProfileFeatures.NoAliases,
            "[ALN]",

            _ => false,
            MatchesVorcha);

    private static bool MatchesVorcha(string facePath, string? baseHeadPath) =>
        !string.IsNullOrWhiteSpace(baseHeadPath)
            ? HeadProfileIdentity.InferSuffix(baseHeadPath) == "vorcha" &&
              baseHeadPath.Contains("ALN_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase)
            : facePath.StartsWith("Vorcha.", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesHuman(
        string facePath,
        string? baseHeadPath,
        IReadOnlyList<string> morphMeshNames,
        string pathSegment,
        string displayPrefix,
        bool allowCharacterCreationVariant = false)
    {
        if (!string.IsNullOrWhiteSpace(baseHeadPath))
        {
            var expectedSuffix = pathSegment.Equals("HMF", StringComparison.OrdinalIgnoreCase)
                ? "human-female"
                : "human-male";
            return HeadProfileIdentity.InferSuffix(baseHeadPath) == expectedSuffix &&
                   (allowCharacterCreationVariant ||
                    !baseHeadPath.Contains("PROCustom_MDL_CC", StringComparison.OrdinalIgnoreCase)) &&
                   morphMeshNames.Any(name =>
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
        "HMM_HED_PROBase_MDL",
        "HMM_HED_PROCustom_MDL",
        "HMM_HED_PROCustom_MDL_CC"
    ];

    private static readonly string[] HumanFemaleMorphMeshes =
    [
        "HMF_Blank_World_LOD0",
        "HMF_HED_PROBase_MDL",
        "HMF_HED_PROCustom_MDL",
        "HMF_HED_PROCustom_MDL_CC"
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
