using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Core.Randomisation;

/// <summary>
/// Selects the Player-only brow and facial-hair textures that are absent from the NPC
/// randomisation corpus. Candidate identities come from the installed texture registry.
/// </summary>
public static class PlayerFacialDetailTexturePolicy
{
    private const string HumanMaleAdditions = "HED_Addn";
    private const string HumanBrow = "HED_Brow";

    /// <summary>
    /// Independently chooses a Player-specific texture for each supported material
    /// parameter that is present on the assigned Player material. Empty results mean
    /// that the parameter is unsupported or no base-game candidate was found.
    /// </summary>
    public static IReadOnlyDictionary<string, PlayerFacialDetailTextureSelection> CreateProposal(
        string profileKey,
        IReadOnlyDictionary<string, string> currentTextureParameters,
        IEnumerable<TextureCatalogCandidate> installedCandidates,
        int randomSeed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        ArgumentNullException.ThrowIfNull(currentTextureParameters);
        ArgumentNullException.ThrowIfNull(installedCandidates);

        if (!TryResolveProfile(profileKey, out var game, out var isHumanMale))
        {
            return EmptyProposal;
        }

        var parameters = isHumanMale
            ? new[] { HumanMaleAdditions, HumanBrow }
            : new[] { HumanBrow };
        var candidates = installedCandidates
            .Where(candidate => candidate.Game == game)
            .ToArray();
        var result = new Dictionary<string, PlayerFacialDetailTextureSelection>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            if (!currentTextureParameters.ContainsKey(parameter)) continue;

            var category = parameter.Equals(HumanMaleAdditions, StringComparison.OrdinalIgnoreCase)
                ? "Beard"
                : "Brow";
            var expectedPrefix = isHumanMale ? "HMM_HED_PROCustom_" : "HMF_HED_PROCustom_";
            var eligible = candidates
                .SelectMany(candidate => BaseGameOccurrences(candidate)
                    .Select(occurrence => new
                    {
                        Candidate = candidate,
                        Occurrence = occurrence,
                        // Registry paths are the package-local instanced export paths.
                        // Keep that exact path for resolution; adding the package root
                        // here creates a seek-free PCC path that FindEntry cannot load.
                        Path = candidate.InstancedPath
                    }))
                .Where(value => HasCategory(value.Candidate.InstancedPath, category) &&
                                value.Candidate.ObjectName.StartsWith(expectedPrefix,
                                    StringComparison.OrdinalIgnoreCase) &&
                                value.Candidate.ObjectName.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                .GroupBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(value => value.Occurrence.PackagePath, StringComparer.OrdinalIgnoreCase)
                    .First())
                .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (eligible.Length == 0) continue;

            // Separate streams ensure adding/removing brow assets never changes the
            // beard draw, and vice versa.
            var stream = parameter.Equals(HumanMaleAdditions, StringComparison.OrdinalIgnoreCase)
                ? 0xA24BAED4963EE407UL
                : 0x9FB21C651E98DF25UL;
            var random = new MorphRandomiser.StableRandom(
                unchecked((ulong)(uint)randomSeed) ^ stream);
            var selected = eligible[random.NextIndex(eligible.Length)];
            var identity = new AssetIdentity(
                selected.Occurrence.PackagePath,
                selected.Path,
                selected.Occurrence.ExportUIndex,
                "Texture2D");
            var selectedCandidate = selected.Candidate with
            {
                EffectiveOccurrence = selected.Occurrence
            };
            result[parameter] = new PlayerFacialDetailTextureSelection(
                parameter, identity, selectedCandidate);
        }

        return result;
    }

    private static IEnumerable<TextureCatalogOccurrence> BaseGameOccurrences(
        TextureCatalogCandidate candidate) =>
        candidate.Occurrences
            .DefaultIfEmpty(candidate.EffectiveOccurrence)
            .Where(occurrence => occurrence.Origin == TextureCatalogOrigin.BaseGame);

    private static bool HasCategory(string instancedPath, string category) =>
        instancedPath.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals(category, StringComparison.OrdinalIgnoreCase));

    private static bool TryResolveProfile(
        string profileKey,
        out TextureCatalogGame game,
        out bool isHumanMale)
    {
        var parts = profileKey.Split('-', StringSplitOptions.RemoveEmptyEntries);
        game = parts.Length > 0
            ? parts[0].ToUpperInvariant() switch
            {
                "LE1" => TextureCatalogGame.LE1,
                "LE2" => TextureCatalogGame.LE2,
                "LE3" => TextureCatalogGame.LE3,
                _ => (TextureCatalogGame)(-1)
            }
            : (TextureCatalogGame)(-1);
        isHumanMale = parts.Length == 3 && parts[1].Equals("human", StringComparison.OrdinalIgnoreCase) &&
                      parts[2].Equals("male", StringComparison.OrdinalIgnoreCase);
        var isHumanFemale = parts.Length == 3 &&
                            parts[1].Equals("human", StringComparison.OrdinalIgnoreCase) &&
                            parts[2].Equals("female", StringComparison.OrdinalIgnoreCase);
        var supportedGame = game is TextureCatalogGame.LE1 or TextureCatalogGame.LE2 or TextureCatalogGame.LE3;
        return supportedGame && (isHumanMale || isHumanFemale);
    }

    private static IReadOnlyDictionary<string, PlayerFacialDetailTextureSelection> EmptyProposal { get; } =
        new Dictionary<string, PlayerFacialDetailTextureSelection>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Chosen Player texture with the exact installed package/export identity.</summary>
public sealed record PlayerFacialDetailTextureSelection(
    string ParameterName,
    AssetIdentity Asset,
    TextureCatalogCandidate Candidate);
