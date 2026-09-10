using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

public sealed record DetachedMeshPreviewLoadResult(
    DetachedMeshPreview Detached,
    MorphFacePreviewLoadResult Preview);

/// <summary>Composes an unrecognised imported mesh into the existing editor/renderer boundary.</summary>
public sealed class DetachedMeshPreviewLoadService
{
    private readonly DetachedMeshPreviewService _detachedMeshes;
    private readonly HeadPreviewSceneFactory _sceneFactory;

    public DetachedMeshPreviewLoadService(
        HeadPreviewSceneFactory sceneFactory,
        DetachedMeshPreviewService? detachedMeshes = null)
    {
        _sceneFactory = sceneFactory;
        _detachedMeshes = detachedMeshes ?? new DetachedMeshPreviewService();
    }

    public DetachedMeshPreviewLoadResult Load(
        MorphFaceGame game,
        ImportedMeshAsset imported,
        DetachedMeshUpAxis upAxis = DetachedMeshUpAxis.Auto)
    {
        var detached = _detachedMeshes.Create(imported, upAxis);
        var loaded = new LoadedMorphFace(
            detached.Document,
            detached.Mesh,
            null,
            ResolvedHeadMaterialSet.Empty,
            detached.TopologyDiagnostics)
        {
            Game = game,
            Warnings = detached.Warnings
        };
        var blockReason = detached.Editing.Rig.IsValid
            ? null
            : detached.Source.Bones.Count == 0
                ? "This imported mesh has no rig; morph and bone controls are disabled."
                : "The imported rig could not be verified; morph and bone controls are disabled. " +
                  detached.Editing.Rig.BlockReason;
        var session = new MorphFaceEditingSession(
            detached.Document,
            detached.Mesh,
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "Detached Custom Mesh",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            _ => false,
            blockReason,
            geometryMode: MorphFaceGeometryMode.FixedBake);
        var materialSession = new MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            ResolvedHeadMaterialSet.Empty);
        var profile = CreateProfile(game);
        var scene = session.CanEditBones
            ? _sceneFactory.CreateEditable(loaded, session.Evaluation)
            : _sceneFactory.Create(loaded);
        return new DetachedMeshPreviewLoadResult(
            detached,
            new MorphFacePreviewLoadResult(loaded, scene, session, materialSession, profile));
    }

    private static MorphFaceProfile CreateProfile(MorphFaceGame game) => new(
        $"{game.ToString().ToLowerInvariant()}-detached-mesh",
        "Detached Custom Mesh",
        game,
        string.Empty,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        DetachedMeshUiProfile.Instance,
        "[MESH]",
        "#66717D",
        _ => false,
        (_, _) => false);

    private sealed class DetachedMeshUiProfile : IHeadEditorUiProfile
    {
        public static DetachedMeshUiProfile Instance { get; } = new();
        public IReadOnlyList<EditorCategoryDefinition> Categories { get; } = [];

        public MorphFeatureMetadata Describe(ResolvedMorphFeature feature, bool sessionCanEdit) =>
            new(feature.Feature.Name, feature.Feature.Name, string.Empty, string.Empty, false, 0, 0, 0, 0, false,
                "Morph controls are unavailable for detached custom meshes.");

        public MaterialParameterDefinition DescribeMaterial(MaterialParameterDefinition definition) => definition;
        public bool IsMaterialVisible(string parameterName, MaterialParameterKind kind) => false;
        public string GetMaterialCategory(string parameterName, MaterialParameterKind kind) => string.Empty;
        public string GetMaterialSubcategory(string parameterName, MaterialParameterKind kind) => string.Empty;
        public int GetMaterialSortOrder(string parameterName, MaterialParameterKind kind) => 0;
    }
}
