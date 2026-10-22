using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Materials;

/// <summary>
/// Owns the stable species/sex identity markers shared by editor profiles and actor assignment.
/// It deliberately reports unknown evidence instead of guessing from an actor class or body mesh.
/// </summary>
public static class HeadProfileIdentity
{
    public static string? InferSuffix(params string?[] evidence) => InferSuffix(evidence.AsEnumerable());

    public static string? InferSuffix(IEnumerable<string?> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var text = string.Join('|', evidence.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (ContainsAny(text, "TUF_", "Female Turian")) return "female-turian";
        if (ContainsAny(text, "BAF_", "BAF.", "Female Batarian")) return "female-batarian";
        if (ContainsAny(text, "ALN_", "Vorcha")) return "vorcha";
        if (ContainsAny(text, "BAT_", "Batarian")) return "batarian";
        if (ContainsAny(text, "KRO_", "Krogan")) return "krogan";
        if (ContainsAny(text, "SAL_", "Salarian")) return "salarian";
        if (ContainsAny(text, "ASA_", "Asari")) return "asari";
        if (ContainsAny(text, "TUR_", "Turian")) return "turian";
        if (ContainsAny(text, "HMF_", "Human Female")) return "human-female";
        if (ContainsAny(text, "HMM_", "Human Male")) return "human-male";
        if (ContainsAny(text, "HMN_")) return "human";
        return null;
    }

    public static string? SuffixFromProfileKey(string? profileKey)
    {
        if (string.IsNullOrWhiteSpace(profileKey)) return null;
        var separator = profileKey.IndexOf('-');
        return separator < 0 || separator == profileKey.Length - 1
            ? null
            : profileKey[(separator + 1)..];
    }

    public static bool IsGeometryCompatible(string selectedProfileKey, string? candidateProfileKey) =>
        !string.IsNullOrWhiteSpace(candidateProfileKey) &&
        string.Equals(selectedProfileKey, candidateProfileKey, StringComparison.OrdinalIgnoreCase);

    public static bool IsMaterialCompatible(
        string selectedProfileKey,
        string? materialProfileKey,
        HeadMaterialFamily family)
    {
        if (string.IsNullOrWhiteSpace(materialProfileKey)) return false;
        if (string.Equals(selectedProfileKey, materialProfileKey, StringComparison.OrdinalIgnoreCase)) return true;

        // Female Turians intentionally reuse the complete male Turian material schema.
        return (selectedProfileKey.EndsWith("-female-turian", StringComparison.OrdinalIgnoreCase) &&
                materialProfileKey.EndsWith("-turian", StringComparison.OrdinalIgnoreCase)) ||
               (selectedProfileKey.EndsWith("-female-batarian", StringComparison.OrdinalIgnoreCase) &&
                materialProfileKey.EndsWith("-batarian", StringComparison.OrdinalIgnoreCase)) ||
               (selectedProfileKey.Equals("le3-vorcha", StringComparison.OrdinalIgnoreCase) &&
                materialProfileKey.Equals("le3-turian", StringComparison.OrdinalIgnoreCase) &&
                family == HeadMaterialFamily.TurianEyes);
    }

    public static bool TryParseHumanProfile(
        string profileKey,
        out MorphFaceGame game,
        out bool isHumanMale)
    {
        var parts = profileKey.Split('-', StringSplitOptions.RemoveEmptyEntries);
        game = parts.Length > 0
            ? parts[0].ToUpperInvariant() switch
            {
                "LE1" => MorphFaceGame.LE1,
                "LE2" => MorphFaceGame.LE2,
                "LE3" => MorphFaceGame.LE3,
                _ => (MorphFaceGame)(-1)
            }
            : (MorphFaceGame)(-1);
        isHumanMale = parts.Length == 3 && parts[1].Equals("human", StringComparison.OrdinalIgnoreCase) &&
                      parts[2].Equals("male", StringComparison.OrdinalIgnoreCase);
        var isHumanFemale = parts.Length == 3 &&
                            parts[1].Equals("human", StringComparison.OrdinalIgnoreCase) &&
                            parts[2].Equals("female", StringComparison.OrdinalIgnoreCase);
        var supportedGame = game is MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3;
        return supportedGame && (isHumanMale || isHumanFemale);
    }

    public static string WithGame(string gamePrefix, string suffix) => $"{gamePrefix}-{suffix}";

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
