using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;

namespace MorphFaceEditor.Services;

/// <summary>Player choices use the same exact runtime-source proof as Player saving.</summary>
public static class PlayerWorkspaceReferencePolicy
{
    // Used by the TSE serializer for path syntax only; source safety is proven separately.
    public static bool IsSeekFreeQualified(string? path) => !string.IsNullOrWhiteSpace(path) &&
        path.Split('.', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 3 } parts &&
        parts[0].StartsWith("BIOG_", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<PackageAssetListItem> SelectPackageAssets(
        IReadOnlyList<PackageAssetListItem> candidates, PlayerRuntimeAssetAvailability available) =>
        candidates.Where(candidate => available.Allows(candidate.Identity)).ToArray();

    public static IReadOnlyList<TextureCatalogCandidate> SelectRegistryTextures(
        IReadOnlyList<TextureCatalogCandidate> candidates, PlayerRuntimeAssetAvailability available) =>
        candidates.Where(candidate => candidate.Game == available.Game).Select(candidate =>
        {
            var occurrences = candidate.Occurrences.Append(candidate.EffectiveOccurrence).Distinct()
                .Where(occurrence => available.Allows(new AssetIdentity(occurrence.PackagePath,
                    candidate.InstancedPath, occurrence.ExportUIndex, "Texture2D"))).ToArray();
            return occurrences.Length == 0 ? null : candidate with
            {
                EffectiveOccurrence = occurrences.Contains(candidate.EffectiveOccurrence)
                    ? candidate.EffectiveOccurrence : occurrences[0],
                Occurrences = occurrences
            };
        }).OfType<TextureCatalogCandidate>().ToArray();

    public static IReadOnlyList<AttachmentMeshCandidate> SelectRegistryMeshes(
        IReadOnlyList<AttachmentMeshCandidate> candidates, PlayerRuntimeAssetAvailability available) =>
        candidates.Where(candidate => available.Allows(new AssetIdentity(
            candidate.EffectiveOccurrence.PackagePath, candidate.CanonicalPath,
            candidate.EffectiveOccurrence.ExportUIndex, "SkeletalMesh"))).ToArray();
}
