using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

/// <summary>
/// Holds the shared archetype metadata parents used by game profiles and detached material workspaces.
/// Game-specific catalogues are explicit child variants; ordinary game profiles reuse their archetype parent.
/// </summary>
public static class MorphFaceMetadataCatalogRegistry
{
    private static readonly IReadOnlyDictionary<MorphFaceGame, string> GameTerms =
        new Dictionary<MorphFaceGame, string>
        {
            [MorphFaceGame.LE1] = Loc.Text("loc_Services_ProfileGame_LE1"),
            [MorphFaceGame.LE2] = Loc.Text("loc_Services_ProfileGame_LE2"),
            [MorphFaceGame.LE3] = Loc.Text("loc_Shell_Game_LE3")
        };

    private static readonly IReadOnlyDictionary<string, string> ArchetypeTerms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [HumanMale] = Loc.Text("loc_Services_ProfileArchetype_HumanMale"),
            [HumanFemale] = Loc.Text("loc_Services_ProfileArchetype_HumanFemale"),
            [Asari] = Loc.Text("loc_Services_ProfileArchetype_Asari"),
            [Salarian] = Loc.Text("loc_Services_ProfileArchetype_Salarian"),
            [Turian] = Loc.Text("loc_Services_ProfileArchetype_Turian"),
            [FemaleTurian] = Loc.Text("loc_Services_ProfileArchetype_FemaleTurian"),
            [Batarian] = Loc.Text("loc_Services_ProfileArchetype_Batarian"),
            [FemaleBatarian] = Loc.Text("loc_Services_ProfileArchetype_FemaleBatarian"),
            [Krogan] = Loc.Text("loc_Services_ProfileArchetype_Krogan"),
            [Vorcha] = Loc.Text("loc_Services_ProfileArchetype_Vorcha")
        };

    public const string HumanMale = "human-male";
    public const string HumanFemale = "human-female";
    public const string Asari = "asari";
    public const string Salarian = "salarian";
    public const string Turian = "turian";
    public const string FemaleTurian = "female-turian";
    public const string Batarian = "batarian";
    public const string FemaleBatarian = "female-batarian";
    public const string Krogan = "krogan";
    public const string Vorcha = "vorcha";

    public static HumanMaleFeatureMetadataCatalog HumanMaleParent { get; } = new();
    public static HumanFemaleFeatureMetadataCatalog HumanFemaleParent { get; } = new();
    public static Le3HumanMaleFeatureMetadataCatalog Le3HumanMale { get; } = new();
    public static Le3HumanFemaleFeatureMetadataCatalog Le3HumanFemale { get; } = new();
    public static AsariFeatureMetadataCatalog AsariParent { get; } = new();
    public static AsariFeatureMetadataCatalog Le3Asari { get; } = new(MorphFaceGame.LE3);
    public static SalarianFeatureMetadataCatalog SalarianParent { get; } = new();
    public static TurianFeatureMetadataCatalog TurianParent { get; } = new();
    public static FemaleTurianFeatureMetadataCatalog FemaleTurianParent { get; } = new(TurianParent);
    public static BatarianFeatureMetadataCatalog BatarianParent { get; } = new();
    public static KroganFeatureMetadataCatalog KroganParent { get; } = new();
    public static VorchaFeatureMetadataCatalog VorchaParent { get; } = new();

    public static HumanMaleFeatureMetadataCatalog HumanMaleFor(MorphFaceGame game) =>
        game == MorphFaceGame.LE3 ? Le3HumanMale : HumanMaleParent;

    public static HumanFemaleFeatureMetadataCatalog HumanFemaleFor(MorphFaceGame game) =>
        game == MorphFaceGame.LE3 ? Le3HumanFemale : HumanFemaleParent;

    public static AsariFeatureMetadataCatalog AsariFor(MorphFaceGame game) =>
        game == MorphFaceGame.LE3 ? Le3Asari : AsariParent;

    public static string ProfileDisplayName(MorphFaceGame game, string archetypeKey) =>
        Loc.Format("loc_Services_ProfileDisplayName", GameTerms[game], ArchetypeTerms[archetypeKey]);

    public static string GetDisplayName(this MorphFaceProfile profile) =>
        profile.Archetype == "detached-mesh"
            ? Loc.Text("loc_Services_DetachedMeshPreviewLoad_ProfileName")
            : ProfileDisplayName(profile.Game, profile.Archetype);

    public static IHeadEditorUiProfile GetUiProfile(this MorphFaceProfile profile, CustomMaterialWorkspace? workspace = null) =>
        profile.Archetype switch
        {
            HumanMale => HumanMaleFor(profile.Game),
            HumanFemale => HumanFemaleFor(profile.Game),
            Asari => AsariFor(profile.Game),
            Salarian => SalarianParent,
            Turian => TurianParent,
            FemaleTurian => FemaleTurianParent,
            Batarian or FemaleBatarian => BatarianParent,
            Krogan => KroganParent,
            Vorcha => VorchaParent,
            "detached-mesh" => new DetachedMeshFeatureMetadataCatalog(workspace),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Key, "Unknown editor profile.")
        };

    public static string GetExportTagColor(this MorphFaceProfile profile) => profile.ExportTag switch
    {
        "[HMM]" => "#287CCB",
        "[HMF]" => "#C02B9B",
        "[ASA]" => "#6635BA",
        "[SAL]" => "#1F9139",
        "[TUR]" => "#008C83",
        "[TUF]" => "#99597D",
        "[BAT]" => "#A16F10",
        "[BAF]" => "#A85B21FF",
        "[KRO]" => "#781400",
        "[ALN]" => "#909110",
        _ => "#66717D"
    };
}
