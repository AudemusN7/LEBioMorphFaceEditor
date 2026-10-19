using System.IO;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;

namespace MorphFaceEditor.Services;

public sealed record MorphFaceCatalog(string PackagePath, IReadOnlyList<BioMorphFaceListItem> Faces)
{
    public MorphFaceGame Game { get; init; }
}

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
        MorphFaceCatalogProjection projection = MorphFaceCatalogProjection.Default,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(packagePath);
            var references = MorphFaceReferenceInspector.Inspect(fullPath, out var game);
            return CreateCatalog(fullPath, game, references, projection);
        }, cancellationToken);

    public Task<(MorphFaceCatalog Faces, PackageReferenceCatalog References)> ReadWorkspaceAsync(
        string packagePath,
        PackageReferenceService referenceService,
        MorphFaceCatalogProjection projection = MorphFaceCatalogProjection.Default,
        CancellationToken cancellationToken = default) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inventory = PackageAssetInspector.InventoryWorkspace(packagePath);
            return (CreateCatalog(inventory.Assets.PackagePath, inventory.Game, inventory.Faces, projection),
                referenceService.CreateCatalog(inventory.Assets));
        }, cancellationToken);

    private MorphFaceCatalog CreateCatalog(string fullPath, MorphFaceGame game,
        IReadOnlyList<MorphFaceMeshReferences> references, MorphFaceCatalogProjection projection)
    {
        var faces = references
            .Where(reference => !IsHiddenForProjection(reference, projection))
            .Select(reference => (Reference: reference, Profile:
                _profiles.Find(reference.Game, reference.FacePath, reference.BaseHeadPath) ??
                _profiles.Find(reference.Game, reference.FacePath, null)))
            .Select(item => new BioMorphFaceListItem(
                item.Reference.FaceUIndex,
                item.Reference.FacePath,
                item.Reference.FacePath.Split('.').Last(),
                item.Profile?.ExportTag ?? "[CUSTOM]",
                item.Profile?.GetExportTagColor() ?? "#66717D",
                item.Profile?.Key ?? "custom")
            {
                SupportsMorphRonExport = RonNpcDonorResolver.SupportsMorphRonExport(item.Profile, item.Reference.BaseHeadPath)
            })
            .OrderBy(face => face.DisplayName, ExplorerNaturalStringComparer.Instance)
            .ThenBy(face => face.InstancedPath, ExplorerNaturalStringComparer.Instance)
            .ToArray();
        return new MorphFaceCatalog(fullPath, faces) { Game = game };
    }

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
}
