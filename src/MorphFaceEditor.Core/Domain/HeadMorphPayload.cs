namespace MorphFaceEditor.Core.Domain;

/// <summary>
/// Detached authored head fields shared by PCC, PCSAV and head-morph file adapters.
/// Vertices retain Unreal's native model-space basis and units (centimetres),
/// in authored LOD/vertex order; final-skeleton translations are parent-local
/// positions. These are neither preview-skinned vertices nor additive bone edits.
/// References use runtime object paths. A payload supplies no proof of topology
/// correspondence or rig validity.
/// </summary>
public sealed record HeadMorphPayload(
    string HairMesh,
    IReadOnlyList<string> AccessoryMeshes,
    MorphFaceMorphData MorphData,
    MorphFaceMaterialData MaterialData);
