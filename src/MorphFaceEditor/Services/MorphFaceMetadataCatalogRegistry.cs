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
            [MorphFaceGame.LE1] = "LE1",
            [MorphFaceGame.LE2] = "LE2",
            [MorphFaceGame.LE3] = "LE3"
        };

    private static readonly IReadOnlyDictionary<string, string> ArchetypeTerms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [HumanMale] = "Human Male",
            [HumanFemale] = "Human Female",
            [Asari] = "Asari",
            [Salarian] = "Salarian",
            [Turian] = "Turian",
            [FemaleTurian] = "Female Turian",
            [Batarian] = "Batarian",
            [Krogan] = "Krogan",
            [Vorcha] = "Vorcha"
        };

    public const string HumanMale = "human-male";
    public const string HumanFemale = "human-female";
    public const string Asari = "asari";
    public const string Salarian = "salarian";
    public const string Turian = "turian";
    public const string FemaleTurian = "female-turian";
    public const string Batarian = "batarian";
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
        $"{GameTerms[game]} {ArchetypeTerms[archetypeKey]}";
}
