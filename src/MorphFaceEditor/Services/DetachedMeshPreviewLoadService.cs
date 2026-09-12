using System.IO;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

public sealed record DetachedMeshPreviewLoadResult(
    DetachedMeshPreview Detached,
    MorphFacePreviewLoadResult Preview,
    CustomMaterialWorkspace? CustomMaterials,
    IReadOnlyList<CustomMaterialAssignmentOption> MaterialOptions,
    IReadOnlyList<AssetIdentity> PreviewAttachments);

/// <summary>Composes an unrecognised imported mesh into the existing editor/renderer boundary.</summary>
public sealed class DetachedMeshPreviewLoadService
{
    private readonly DetachedMeshPreviewService _detachedMeshes;
    private readonly HeadPreviewSceneFactory _sceneFactory;
    private readonly ICustomMaterialTemplateCatalog? _materialCatalog;

    public DetachedMeshPreviewLoadService(
        HeadPreviewSceneFactory sceneFactory,
        DetachedMeshPreviewService? detachedMeshes = null,
        ICustomMaterialTemplateCatalog? materialCatalog = null)
    {
        _sceneFactory = sceneFactory;
        _detachedMeshes = detachedMeshes ?? new DetachedMeshPreviewService();
        _materialCatalog = materialCatalog;
    }

    public DetachedMeshPreviewLoadResult Load(
        MorphFaceGame game,
        ImportedMeshAsset imported,
        DetachedMeshUpAxis upAxis = DetachedMeshUpAxis.Auto)
    {
        var detached = _detachedMeshes.Create(imported, upAxis);
        var warnings = detached.Warnings.ToList();
        CustomMaterialWorkspace? customMaterials = null;
        CustomMaterialTemplateCatalogResult materialCatalog = CustomMaterialTemplateCatalogResult.Empty;
        try
        {
            customMaterials = new CustomMaterialWorkspace(detached.Source);
            materialCatalog = _materialCatalog?.Load(game) ?? CustomMaterialTemplateCatalogResult.Empty;
            warnings.AddRange(materialCatalog.Warnings);
            detached = detached with { Mesh = BindPreviewMaterialSlots(detached.Mesh, customMaterials) };
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("at most", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(exception.Message + " The mesh remains previewable with its material slots unassigned.");
        }
        detached = detached with { Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray() };
        var loaded = new LoadedMorphFace(
            detached.Document,
            detached.Mesh,
            null,
            ResolvedHeadMaterialSet.Empty,
            detached.TopologyDiagnostics)
        {
            Game = game,
            UsesNativeAttachmentBindPose = true,
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
        if (customMaterials is not null)
        {
            customMaterials.ActiveMaterialsChanged += (_, args) =>
                materialSession.RebaseMaterialSurface(args.Materials);
        }
        var profile = CreateProfile(game, customMaterials);
        var scene = session.CanEditBones
            ? _sceneFactory.CreateEditable(loaded, session.Evaluation)
            : _sceneFactory.Create(loaded);
        return new DetachedMeshPreviewLoadResult(
            detached,
            new MorphFacePreviewLoadResult(loaded, scene, session, materialSession, profile),
            customMaterials,
            materialCatalog.Options,
            materialCatalog.PreviewAttachments);
    }

    private static SkeletalMeshAsset BindPreviewMaterialSlots(
        SkeletalMeshAsset mesh,
        CustomMaterialWorkspace workspace)
    {
        SkeletalMeshRenderData Bind(SkeletalMeshRenderData renderData)
        {
            var materialSlots = renderData.MaterialSlots.ToArray();
            foreach (var slot in workspace.UsedSlots)
            {
                if ((uint)slot.MaterialIndex < (uint)materialSlots.Length)
                {
                    materialSlots[slot.MaterialIndex] = workspace.GetPreviewMaterialIdentity(slot);
                }
            }
            return renderData with { MaterialSlots = materialSlots };
        }

        var lods = mesh.AvailableLods
            .Select(lod => lod with { RenderData = Bind(lod.RenderData) })
            .ToArray();
        return mesh with
        {
            RenderData = mesh.RenderData is null ? null : Bind(mesh.RenderData),
            Lods = lods
        };
    }

    private static MorphFaceProfile CreateProfile(
        MorphFaceGame game,
        CustomMaterialWorkspace? customMaterials) => new(
        $"{game.ToString().ToLowerInvariant()}-detached-mesh",
        "Detached Custom Mesh",
        game,
        string.Empty,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new DetachedMeshFeatureMetadataCatalog(customMaterials),
        "[MESH]",
        "#66717D",
        _ => false,
        (_, _) => false);

}
