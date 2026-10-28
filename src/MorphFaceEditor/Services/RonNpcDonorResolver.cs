using MorphFaceEditor.Core.Domain;
using System.IO;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
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
                profile.ExportTag.Trim('[', ']'), profile.GetDisplayName()))
            .DistinctBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public MorphFaceTemplateCandidate Resolve(
        MorphFaceGame game,
        string archetype,
        IReadOnlyList<MorphFaceTemplateCandidate> candidates,
        IReadOnlyCollection<string>? eligibleOfficialPackagePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archetype);
        ArgumentNullException.ThrowIfNull(candidates);
        var eligiblePackages = eligibleOfficialPackagePaths?.Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profile = _profiles.Profiles.SingleOrDefault(value =>
            value.Game == game &&
            value.ExportTag.Trim('[', ']').Equals(archetype, StringComparison.OrdinalIgnoreCase) &&
            IsNativeNpcProfile(value)) ?? throw new InvalidDataException(
            $"The selected {game} game has no supported native NPC '{archetype}' archetype.");

        return candidates
            .Where(candidate => IsNativeNpc(candidate, profile.Archetype == "female-turian") &&
                                (eligiblePackages is null || eligiblePackages.Contains(Path.GetFullPath(candidate.PackagePath)) ||
                                 profile.Archetype == "female-turian" && candidate.Origin == TextureCatalogOrigin.Mod) &&
                                _profiles.Find(game, candidate.FacePath, candidate.BaseHeadPath)?.Key == profile.Key &&
                                profile.GeometryEditBlockReason(candidate.BaseHeadPath) is null &&
                                File.Exists(candidate.PackagePath))
            .OrderByDescending(candidate => candidate.MountPriority)
            .ThenBy(candidate => candidate.Origin)
            .ThenBy(candidate => candidate.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.FacePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? throw Loc.Error(new InvalidDataException(
                Loc.Format("loc_Services_RonNpcDonorResolver_NoInstalledNativeNpcFace", game, archetype)));
    }

    // NPC RON import can use a static native base.  In particular, ALN/Vorcha
    // faces are valid donors even when the profile does not expose editable
    // authored morph controls. Keep geometry-ignored profiles
    // out of the donor list because their native payload is intentionally not
    // safe to use as an NPC morph base.
    internal static bool SupportsMorphRonExport(MorphFaceProfile? profile, string? baseHeadPath) =>
        profile is not null && IsNativeNpcProfile(profile) && profile.GeometryEditBlockReason(baseHeadPath) is null;

    internal MorphFaceTemplateCandidate? ResolveMaterialDefault(
        MorphFaceGame game, string profileKey, IReadOnlyList<MorphFaceTemplateCandidate> candidates,
        IReadOnlyCollection<string> eligibleOfficialPackagePaths)
    {
        var profile = _profiles.Profiles.SingleOrDefault(p => p.Game == game && p.Key == profileKey);
        var eligible = eligibleOfficialPackagePaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.Where(candidate => IsNativeNpc(candidate, profile?.Archetype == "female-turian") &&
                (eligible.Contains(Path.GetFullPath(candidate.PackagePath)) ||
                 profile?.Archetype == "female-turian" && candidate.Origin == TextureCatalogOrigin.Mod) && File.Exists(candidate.PackagePath) &&
                (_profiles.Find(game, candidate.FacePath, candidate.BaseHeadPath) ??
                 _profiles.Find(game, candidate.FacePath, null))?.Key == profileKey)
            .OrderByDescending(candidate => candidate.MountPriority)
            .ThenBy(candidate => candidate.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.FacePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static bool IsNativeNpcProfile(MorphFaceProfile profile) =>
        !profile.IgnoresAuthoredGeometry &&
        profile.ExportTag.Trim('[', ']').Length > 0;

    private static bool IsNativeNpc(MorphFaceTemplateCandidate candidate, bool allowInstalledMod) =>
        (candidate.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc ||
         allowInstalledMod && candidate.Origin == TextureCatalogOrigin.Mod) &&
        !IsPlayerOrCreator(candidate.FacePath) &&
        !IsPlayerOrCreator(candidate.BaseHeadPath);

    internal static bool IsPlayerOrCreator(string? path) =>
        path?.Contains("Player_Base_", StringComparison.OrdinalIgnoreCase) == true ||
        path?.Contains("Player_Iconic_", StringComparison.OrdinalIgnoreCase) == true ||
        path?.Contains("CharacterCreation_Base_", StringComparison.OrdinalIgnoreCase) == true;
}
