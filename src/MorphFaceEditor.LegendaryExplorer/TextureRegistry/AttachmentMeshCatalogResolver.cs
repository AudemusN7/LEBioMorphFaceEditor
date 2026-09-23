using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Resolves a mesh path only when the merged database identifies one exact donor.</summary>
public static class AttachmentMeshCatalogResolver
{
    public static AssetIdentity? Resolve(
        IReadOnlyList<AttachmentMeshCandidate> candidates,
        string requestedPath)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);

        var candidate = candidates.FirstOrDefault(value =>
            value.CanonicalPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            var localMatches = candidates.Where(value => value.Occurrences.Any(occurrence =>
                    occurrence.InstancedPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase)))
                .Take(2).ToArray();
            candidate = localMatches.Length == 1 ? localMatches[0] : null;
        }
        if (candidate is null) return null;

        var source = candidate.EffectiveOccurrence;
        return new AssetIdentity(source.PackagePath, requestedPath, source.ExportUIndex,
            "SkeletalMesh");
    }
}
