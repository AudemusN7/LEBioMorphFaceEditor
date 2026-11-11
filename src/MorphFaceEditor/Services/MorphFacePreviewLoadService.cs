using System.IO;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Infrastructure;
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
    private readonly PlayerMorphSourceReconciler _baselineSources = new();
    private readonly PlayerMorphBaselineCatalog _playerBaselines;

    public MorphFacePreviewLoadService(
        HeadPreviewSceneFactory sceneFactory,
        MorphTargetCatalog targetCatalog,
        MorphFaceProfileRegistry profiles,
        MorphFacePackageReader reader,
        PlayerMorphBaselineCatalog? playerBaselines = null)
    {
        _sceneFactory = sceneFactory;
        _targetCatalog = targetCatalog;
        _profiles = profiles;
        _reader = reader;
        _playerBaselines = playerBaselines ?? new();
    }

    public Task<MorphFacePreviewLoadResult> LoadAsync(
        string packagePath,
        string exportSelector,
        CancellationToken cancellationToken = default,
        MorphFaceGeometryMode? geometryMode = null) => Task.Run(() =>
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = _reader.Load(packagePath, exportSelector);
        cancellationToken.ThrowIfCancellationRequested();
        var profileResolution = _profiles.Resolve(
            loaded.Game,
            loaded.Document.Source.InstancedPath,
            loaded.Document.BaseHeadReference?.InstancedPath,
            loaded.Materials) ?? throw Loc.Error(new NotSupportedException(Loc.Format(
                "loc_Services_MorphFacePreviewLoad_UnsupportedBaseHead", loaded.Game,
                loaded.Document.BaseHeadReference?.InstancedPath ?? "<unresolved>")));
        var profile = profileResolution.Profile;
        // Earlier TUF material corpora carry TUR-sized bakes. Keep those
        // payloads intact and preview the female base until explicitly rebaked.
        if (profile.Archetype == "female-turian" && loaded.Document.BakedLods
                .Where((lod, index) => index >= loaded.BaseHead.Lods.Count ||
                    lod.Length != loaded.BaseHead.Lods[index].Positions.Length).Any())
            profile = profile with { IgnoresAuthoredGeometry = true };
        loaded = loaded with
        {
            UsesCustomBaseMesh = profileResolution.UsesCustomMesh,
            IgnoresAuthoredGeometry = profile.IgnoresAuthoredGeometry,
            HairMesh = profile.IgnoresAuthoredGeometry ? null : loaded.HairMesh,
            OtherMeshes = profile.IgnoresAuthoredGeometry ? [] : loaded.OtherMeshes
        };
        var geometryEditBlockReason = profileResolution.UsesCustomMesh
            ? "This BioMorphFace uses a custom base mesh; morph and bone controls are disabled. Material editing remains available."
            : profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath);
        var resolvedGeometryMode = profileResolution.UsesCustomMesh || profile.IgnoresAuthoredGeometry
            ? MorphFaceGeometryMode.BaseMeshOnly
            : profile.TargetSetName is null
                ? loaded.Document.BakedLods.FirstOrDefault()?.Length == loaded.BaseHead.Topology.VertexCount
                    ? MorphFaceGeometryMode.FixedBake
                    : MorphFaceGeometryMode.MorphEvaluated
                : geometryMode ?? MorphFaceGeometryMode.MorphEvaluated;
        IReadOnlyList<MorphTargetAsset> profileTargets = profileResolution.UsesCustomMesh || profile.IgnoresAuthoredGeometry
            ? [] : _targetCatalog.Load(profile);
        MorphFaceEditingSession CreateSession(MorphFaceGeometryMode mode) => new(
            loaded.Document,
            loaded.BaseHead,
            profileTargets,
            profile.MetadataOnlyFeatures,
            profile.GetDisplayName(),
            profile.FeatureAliases,
            profile.RecognizesBaseVariant,
            geometryEditBlockReason,
            profile.IgnoresAuthoredGeometry,
            mode,
            topologyDiagnostics: loaded.TopologyDiagnostics);
        var session = CreateSession(resolvedGeometryMode);
        if (geometryMode is null && session.CanFixMorph &&
            IsKnownPlayerBaseline(loaded, profile, profileTargets))
        {
            // A recognised authored bake is not a broken standard-head bake.
            // Keep every source field and apply future edits relative to it.
            session = CreateSession(MorphFaceGeometryMode.RelativeBake);
        }
        var baseMaterialKeys = loaded.BaseHead.RenderData?.MaterialSlots
            .Where(identity => identity is not null)
            .Select(identity => MaterialIdentityKey.Create(identity!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var materialSession = new MaterialEditingSession(
            loaded.Document.MaterialOverrides,
            loaded.Materials,
            baseMaterialKeys);
        var previewLoaded = loaded with { Materials = materialSession.Materials };
        var scene = session.UsesLiveDeformationPreview || session.CanEditBones
            ? _sceneFactory.CreateEditable(previewLoaded, session.Evaluation)
            : _sceneFactory.Create(previewLoaded);
        var cache = _reader.PackageCacheStatistics;
        AppLog.Information($"PERF:FACE-LOAD PROFILE:{profile.Key} MS:{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} " +
            $"PCCS:{cache.Packages} PCCBYTES:{cache.EstimatedBytes} PCCHITS:{cache.Hits} PCCMISSES:{cache.Misses}");
        return new MorphFacePreviewLoadResult(loaded, scene, session, materialSession, profile);
    }, cancellationToken);

    private bool IsKnownPlayerBaseline(LoadedMorphFace loaded, MorphFaceProfile profile,
        IReadOnlyList<MorphTargetAsset> targets)
    {
        var baselines = _playerBaselines.ForProfile(profile);
        if (baselines.Count == 0) return false;

        // A matching shape alone does not establish that the PCC uses the Player rig.
        try
        {
            var sex = profile.Archetype == "human-male" ? StandalonePlayerSex.Male : StandalonePlayerSex.Female;
            if (!_reader.MatchesPlayerBaseHead(loaded, sex)) return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }

        var matches = 0;
        foreach (var baseline in baselines)
        {
            var frame = PlayerMorphBaselineFrame.TryCreate(baseline, loaded.Document, loaded.BaseHead, profile, targets);
            if (frame is not null && _baselineSources.Reconcile(loaded.Document, frame.EvaluationBase, profile, targets).IsSupported)
                matches++;
        }
        return matches == 1;
    }

    public void Dispose() => _reader.Dispose();
}
