using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Models;

namespace MorphFaceEditor.Services;

/// <summary>
/// Player head morphs are loaded outside the package that supplied the editor
/// preview. Only an already-authored seek-free BIOG path is safe to persist;
/// package-relative exports must never become picker choices.
/// </summary>
public static class PlayerWorkspaceReferencePolicy
{
    private static readonly HashSet<string> PlayerGlobalTextureFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Black",
        "BIOG_Humanoid_MASTER_MTR_R.GBL_ARM_ALL_Norm"
    };
    private const string Le3HmmShortScalpPackage = "BIOG_HMM_HIR_PRO_R.pcc";
    private static readonly HashSet<string> Le3HmmShortScalpTextures = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hair_Short03.HMM_HIR_Short_Scalp_Diff",
        "Hair_Short03.HMM_HIR_Short_Scalp_Norm",
        "Hair_Short03.HMM_HIR_Short_Scalp_Mask"
    };

    public static bool IsSeekFreeQualified(string? instancedPath)
    {
        if (string.IsNullOrWhiteSpace(instancedPath))
        {
            return false;
        }

        var parts = instancedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0].StartsWith("BIOG_", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<PackageAssetListItem> SelectPackageAssets(
        IReadOnlyList<PackageAssetListItem> candidates) =>
        candidates.Where(candidate => IsSeekFreeQualified(candidate.Identity.InstancedPath)).ToArray();

    public static IReadOnlyList<TextureCatalogCandidate> SelectRegistryTextures(
        IReadOnlyList<TextureCatalogCandidate> candidates) =>
        candidates.Where(candidate => IsSeekFreeQualified(candidate.InstancedPath) ||
                                      PlayerGlobalTextureFallbacks.Contains(candidate.InstancedPath) ||
                                      IsLe3HmmShortScalpTexture(candidate)).ToArray();

    private static bool IsLe3HmmShortScalpTexture(TextureCatalogCandidate candidate) =>
        candidate.Game == TextureCatalogGame.LE3 &&
        Le3HmmShortScalpTextures.Contains(candidate.InstancedPath) &&
        candidate.Occurrences
            .DefaultIfEmpty(candidate.EffectiveOccurrence)
            .Any(occurrence => occurrence.Origin == TextureCatalogOrigin.BaseGame &&
                               occurrence.PackageName.Equals(Le3HmmShortScalpPackage,
                                   StringComparison.OrdinalIgnoreCase));
}
