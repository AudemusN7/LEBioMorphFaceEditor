using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

public enum ActorAssignmentTargetKind
{
    PlacedActor,
    SpawnTemplate
}

public enum ActorComponentRole
{
    Head,
    Hair
}

public enum ActorIdentityEvidenceKind
{
    LocalTag,
    LocalUniqueTag,
    InheritedTag,
    InheritedUniqueTag,
    BehaviorTag,
    ActorTypeTag,
    GameName,
    GameNameStringRef,
    ActorType,
    Archetype,
    Export
}

/// <summary>One searchable authored or technical identity attached to an actor candidate.</summary>
public sealed record ActorIdentityEvidence(
    ActorIdentityEvidenceKind Kind,
    string Label,
    string Value,
    string SourcePath);

/// <summary>A resolved actor component and the skeletal mesh which establishes its role.</summary>
public sealed record ActorAssignmentComponent(
    ActorComponentRole Role,
    int UIndex,
    string ObjectName,
    string InstancedPath,
    string PackagePath,
    bool IsLocal,
    string? SkeletalMeshPath,
    bool Inherited);

/// <summary>One node in the complete local-MIC-to-master parent chain used for classification.</summary>
public sealed record ActorMaterialChainEntry(
    int UIndex,
    string ClassName,
    string InstancedPath,
    bool IsLocal,
    bool Resolved);

/// <summary>A local MIC slot which passed family and selected-profile compatibility checks.</summary>
public sealed record ActorAssignmentMaterialTarget(
    int UIndex,
    string ObjectName,
    string InstancedPath,
    ActorComponentRole ComponentRole,
    string ComponentPath,
    int SlotIndex,
    HeadMaterialFamily Family,
    string? ProfileKey,
    IReadOnlyList<ActorMaterialChainEntry> ParentChain,
    bool InheritedSlot);

/// <summary>A material slot omitted by preflight, retaining enough evidence for the chooser to explain why.</summary>
public sealed record ActorAssignmentSkippedMaterial(
    int UIndex,
    string ObjectName,
    string InstancedPath,
    ActorComponentRole ComponentRole,
    string ComponentPath,
    int SlotIndex,
    HeadMaterialFamily Family,
    string? ProfileKey,
    string Reason,
    IReadOnlyList<ActorMaterialChainEntry> ParentChain,
    bool InheritedSlot);

/// <summary>The exact export/property which a later morph-assignment transaction may author.</summary>
public sealed record ActorAssignmentMorphTarget(
    int OwnerUIndex,
    string OwnerClass,
    string OwnerPath,
    string PropertyName,
    int CurrentMorphUIndex,
    string? CurrentMorphPath,
    string? CurrentMorphBaseHeadPath);

public sealed record ActorAssignmentReference(
    int UIndex,
    string ClassName,
    string InstancedPath,
    string PropertyPath);

/// <summary>
/// Detached preflight description for one supported actor or explicit LE1 spawn template.
/// No package objects escape the inventory boundary.
/// </summary>
public sealed record ActorAssignmentCandidate(
    int UIndex,
    string ClassName,
    string ObjectName,
    string InstancedPath,
    ActorAssignmentTargetKind TargetKind,
    string DisplayName,
    IReadOnlyList<ActorIdentityEvidence> IdentityEvidence,
    string SearchText,
    string SelectedProfileKey,
    string? HeadMeshPath,
    string? HeadMeshProfileKey,
    bool CanAssignMorph,
    string? MorphIneligibilityReason,
    ActorAssignmentMorphTarget? MorphTarget,
    bool CanAssignMaterials,
    string? MaterialIneligibilityReason,
    IReadOnlyList<ActorAssignmentComponent> Components,
    IReadOnlyList<ActorAssignmentMaterialTarget> MaterialTargets,
    IReadOnlyList<ActorAssignmentSkippedMaterial> SkippedMaterials,
    IReadOnlyList<ActorAssignmentReference> ReferencedBy,
    IReadOnlyList<string> Warnings)
{
    public int SafeMaterialCount => MaterialTargets.Select(target => target.UIndex).Distinct().Count();
}

/// <summary>Read-only inventory rooted at one selected local BioMorphFace.</summary>
public sealed record ActorAssignmentInventory(
    MorphFaceGame Game,
    int SelectedFaceUIndex,
    string SelectedFacePath,
    string SelectedProfileKey,
    IReadOnlyList<ActorAssignmentCandidate> Candidates);
