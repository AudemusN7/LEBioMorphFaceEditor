using System.IO;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;

namespace MorphFaceEditor.Services;

public sealed record MorphFaceCatalog(string PackagePath, IReadOnlyList<BioMorphFaceListItem> Faces);

public sealed class MorphFaceCatalogService
{
    private readonly MorphFaceProfileRegistry _profiles;

    public MorphFaceCatalogService(MorphFaceProfileRegistry? profiles = null)
    {
        _profiles = profiles ?? MorphFaceProfileRegistry.CreateDefault();
    }

    public Task<MorphFaceCatalog> ReadAsync(string packagePath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(packagePath);
            var faces = MorphFaceReferenceInspector.Inspect(fullPath)
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

}
