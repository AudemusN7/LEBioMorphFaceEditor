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
        candidates.Where(candidate => IsSeekFreeQualified(candidate.InstancedPath)).ToArray();
}
