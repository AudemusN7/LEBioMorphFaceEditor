using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;

namespace MorphFaceEditor.Services;

public sealed record MorphFaceCatalog(string PackagePath, IReadOnlyList<BioMorphFaceListItem> Faces);

/// <summary>Controls route-specific visibility applied after package face discovery.</summary>
public enum MorphFaceCatalogProjection
{
    Default,
    Le1EntryMenuPlayerStaging
}

public static class MorphFaceCatalogProjectionPolicy
{
    /// <summary>Resolves the projection for the detached LE1 Player seed route.</summary>
    public static MorphFaceCatalogProjection ForPlayerWorkspace(
        MorphFaceGame? game,
        string? sourcePackagePath,
        bool isNpcWorkspace)
    {
        return !isNpcWorkspace &&
               game == MorphFaceGame.LE1 &&
               !string.IsNullOrWhiteSpace(sourcePackagePath) &&
               Path.GetFileName(sourcePackagePath).Equals(
                   "EntryMenu.pcc", StringComparison.OrdinalIgnoreCase)
            ? MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging
            : MorphFaceCatalogProjection.Default;
    }
}

public sealed class MorphFaceCatalogService
{
    private static readonly HashSet<string> Le1EntryMenuPlayerStagingHiddenFaces = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "BIOG_MORPH_FACE.Broke",
        "BIOG_MORPH_FACE.Asari",
        "BIOG_MORPH_FACE.Krogan"
    };

    private readonly MorphFaceProfileRegistry _profiles;

    public MorphFaceCatalogService(MorphFaceProfileRegistry? profiles = null)
    {
        _profiles = profiles ?? MorphFaceProfileRegistry.CreateDefault();
    }

    public Task<MorphFaceCatalog> ReadAsync(
        string packagePath,
        CancellationToken cancellationToken) =>
        ReadAsync(packagePath, MorphFaceCatalogProjection.Default, cancellationToken);

    public Task<MorphFaceCatalog> ReadAsync(
        string packagePath,
        MorphFaceCatalogProjection projection = MorphFaceCatalogProjection.Default,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(packagePath);
            var faces = MorphFaceReferenceInspector.Inspect(fullPath)
                .Where(reference => !ShouldHide(reference, projection))
                .Select(reference => (Reference: reference, Profile:
                    _profiles.Find(reference.Game, reference.FacePath, reference.BaseHeadPath) ??
                    _profiles.Find(reference.Game, reference.FacePath, null)))
                .Select(item => new BioMorphFaceListItem(
                    item.Reference.FaceUIndex,
                    item.Reference.FacePath,
                    item.Reference.FacePath.Split('.').Last(),
                    item.Profile?.ExportTag ?? "[CUSTOM]",
                    item.Profile?.ExportTagColor ?? "#66717D",
                    item.Profile?.Key ?? "custom"))
                .OrderBy(face => face.DisplayName, ExplorerNaturalStringComparer.Instance)
                .ThenBy(face => face.InstancedPath, ExplorerNaturalStringComparer.Instance)
                .ToArray();
            return new MorphFaceCatalog(fullPath, faces);
        }, cancellationToken);

    /// <summary>
    /// Returns whether a discovered face belongs to the narrow LE1 Player
    /// staging projection. Package contents and default face discovery are not
    /// changed; callers opt into this projection only for that route.
    /// </summary>
    public static bool IsHiddenForProjection(
        MorphFaceMeshReferences reference,
        MorphFaceCatalogProjection projection) =>
        projection == MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging &&
        reference.Game == MorphFaceGame.LE1 &&
        Le1EntryMenuPlayerStagingHiddenFaces.Contains(reference.FacePath);

    private static bool ShouldHide(
        MorphFaceMeshReferences reference,
        MorphFaceCatalogProjection projection) =>
        IsHiddenForProjection(reference, projection);

}
