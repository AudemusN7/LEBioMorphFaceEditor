using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Services;

public sealed record MorphFacePreviewLoadResult(
    LoadedMorphFace Loaded,
    HeadPreviewScene Scene,
    MorphFaceEditingSession EditingSession,
    MaterialEditingSession MaterialEditingSession,
    MorphFaceProfile Profile);

public sealed class MorphFacePreviewLoadService : IDisposable
{
    private readonly HeadPreviewSceneFactory _sceneFactory;
    private readonly MorphTargetCatalog _targetCatalog;
    private readonly MorphFaceProfileRegistry _profiles;
    private readonly MorphFacePackageReader _reader;

    public MorphFacePreviewLoadService(
        HeadPreviewSceneFactory sceneFactory,
        MorphTargetCatalog targetCatalog,
        MorphFaceProfileRegistry profiles,
        MorphFacePackageReader reader)
    {
        _sceneFactory = sceneFactory;
        _targetCatalog = targetCatalog;
        _profiles = profiles;
        _reader = reader;
    }

    public Task<MorphFacePreviewLoadResult> LoadAsync(
        string packagePath,
        string exportSelector,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = _reader.Load(packagePath, exportSelector);
        cancellationToken.ThrowIfCancellationRequested();
        var profileResolution = _profiles.Resolve(
            loaded.Game,
            loaded.Document.Source.InstancedPath,
            loaded.Document.BaseHeadReference?.InstancedPath,
            loaded.Document.MaterialOverrides,
            loaded.Materials) ?? throw new NotSupportedException(
                $"{loaded.Game} BioMorphFace '{loaded.Document.Source.InstancedPath}' uses unsupported base head " +
                $"'{loaded.Document.BaseHeadReference?.InstancedPath ?? "<unresolved>"}'.");
        var profile = profileResolution.Profile;
        loaded = loaded with { UsesCustomBaseMesh = profileResolution.UsesCustomMesh };
        var geometryEditBlockReason = profileResolution.UsesCustomMesh
            ? "This BioMorphFace uses a custom base mesh; morph and bone controls are disabled. Material editing remains available."
            : profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath);
        var session = new MorphFaceEditingSession(
            loaded.Document,
            loaded.BaseHead,
            profileResolution.UsesCustomMesh
                ? []
                : _targetCatalog.Load(profile, loaded.Game, packagePath),
            profile.MetadataOnlyFeatures,
            profile.DisplayName,
            profile.FeatureAliases,
            profile.RecognizesBaseVariant,
            geometryEditBlockReason);
        var baseMaterialKeys = loaded.BaseHead.RenderData?.MaterialSlots
            .Where(identity => identity is not null)
            .Select(identity => MaterialIdentityKey.Create(identity!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var materialSession = new MaterialEditingSession(
            loaded.Document.MaterialOverrides,
            loaded.Materials,
            baseMaterialKeys);
        var previewLoaded = loaded with { Materials = materialSession.Materials };
        var scene = session.CanEdit
            ? _sceneFactory.CreateEditable(previewLoaded, session.Evaluation)
            : _sceneFactory.Create(previewLoaded);
        return new MorphFacePreviewLoadResult(loaded, scene, session, materialSession, profile);
    }, cancellationToken);

    public void Dispose() => _reader.Dispose();
}
