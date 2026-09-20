using System.IO;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

/// <summary>Chooses a game-local installed NPC face for a tagged standalone RON.</summary>
public sealed class RonNpcDonorResolver(MorphFaceProfileRegistry profiles)
{
    private readonly MorphFaceProfileRegistry _profiles = profiles;

    public IReadOnlyList<RonNpcArchetypeOption> Archetypes(MorphFaceGame game) =>
        _profiles.Profiles
            .Where(profile => profile.Game == game && IsNativeNpcProfile(profile))
            .Select(profile => new RonNpcArchetypeOption(
                profile.ExportTag.Trim('[', ']'), profile.DisplayName))
            .DistinctBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public MorphFaceTemplateCandidate Resolve(
        MorphFaceGame game,
        string archetype,
        IReadOnlyList<MorphFaceTemplateCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archetype);
        ArgumentNullException.ThrowIfNull(candidates);
        var profile = _profiles.Profiles.SingleOrDefault(value =>
            value.Game == game &&
            value.ExportTag.Trim('[', ']').Equals(archetype, StringComparison.OrdinalIgnoreCase) &&
            IsNativeNpcProfile(value)) ?? throw new InvalidDataException(
            $"The selected {game} game has no supported native NPC '{archetype}' archetype.");

        return candidates
            .Where(candidate => IsNativeNpc(candidate) &&
                                _profiles.Find(game, candidate.FacePath, candidate.BaseHeadPath)?.Key == profile.Key &&
                                profile.GeometryEditBlockReason(candidate.BaseHeadPath) is null &&
                                File.Exists(candidate.PackagePath))
            .OrderBy(candidate => candidate.Origin)
            .ThenBy(candidate => candidate.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.FacePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? throw new InvalidDataException(
                $"The installed {game} texture database has no native NPC '{archetype}' face. Rebuild the database and try again.");
    }

    // NPC RON import can use a static native base.  In particular, ALN/Vorcha
    // faces are valid donors even when the profile does not expose editable
    // authored morph controls.  Keep geometry-ignored profiles (such as TUF)
    // out of the donor list because their native payload is intentionally not
    // safe to use as an NPC morph base.
    private static bool IsNativeNpcProfile(MorphFaceProfile profile) =>
        !profile.IgnoresAuthoredGeometry &&
        profile.ExportTag.Trim('[', ']').Length > 0;

    private static bool IsNativeNpc(MorphFaceTemplateCandidate candidate) =>
        (candidate.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc ||
         candidate.Origin == TextureCatalogOrigin.Mod &&
         candidate.PackagePath.Contains("CommunityPatch", StringComparison.OrdinalIgnoreCase)) &&
        !IsPlayerOrCreator(candidate.FacePath) &&
        !IsPlayerOrCreator(candidate.BaseHeadPath);

    private static bool IsPlayerOrCreator(string? path) =>
        path?.Contains("Player_Base_", StringComparison.OrdinalIgnoreCase) == true ||
        path?.Contains("Player_Iconic_", StringComparison.OrdinalIgnoreCase) == true ||
        path?.Contains("CharacterCreation_Base_", StringComparison.OrdinalIgnoreCase) == true;
}
