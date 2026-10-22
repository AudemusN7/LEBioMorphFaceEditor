using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed partial class ActorAssignmentInventoryService
{
    private static (IReadOnlyList<ActorAssignmentAttachmentTarget> Targets, IReadOnlyList<UserFacingMessage> Warnings)
        ReadAttachmentTargets(IMEPackage package, ExportEntry actor, ExportEntry face,
            IReadOnlyList<ActorAssignmentComponent> components, IReadOnlySet<int> bodyMics, ActorAssignmentMorphTarget? morphTarget,
            IReadOnlyList<IEntry> knownHairMeshes, IReadOnlyList<IEntry> knownOtherMeshes,
            GamePackageReferenceResolver resolver, PackageCache cache,
            Lazy<ILookup<int, ActorAssignmentReference>> referrers,
            Lazy<ILookup<int, ExportEntry>> ownedMics)
    {
        var current = morphTarget is null ? null : FindProperty<ObjectProperty>(
            package.GetUExport(morphTarget.OwnerUIndex), morphTarget.PropertyName, resolver, cache);
        var oldFace = resolver.Resolve(current?.Property.ResolveToEntry(current.Owner.FileRef));
        var hair = components.Where(value => value.IsLocal && value.Role == ActorComponentRole.Hair)
            .Select(value => package.GetUExport(value.UIndex)).ToList();
        var array = components.Where(value => value.IsLocal && value.Role == ActorComponentRole.Accessory)
            .Select(value => package.GetUExport(value.UIndex)).ToArray();
        var oldHair = oldFace?.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(oldFace.FileRef);
        var hairMatch = FindMatchingComponent(array, oldHair is null ? [] : [oldHair], knownHairMeshes, resolver, cache);
        if (hairMatch is { Component: null, Ambiguous: false })
            hairMatch = FindComponentByMaterials(array, ActorComponentRole.Hair, resolver, cache);
        if (hairMatch.Component is { } arrayHair) hair.Add(arrayHair);
        var assignments = hair.Select(component => (Role: ActorComponentRole.Hair, Component: component,
                Mesh: face.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package))).ToList();
        var warnings = new List<UserFacingMessage>();
        var others = face.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes");
        var gearReference = package.Game == MEGame.LE3
            ? FindProperty<ObjectProperty>(actor, "m_oHeadGearMesh", resolver, cache)
              ?? FindProperty<ObjectProperty>(actor, "HeadGearMesh", resolver, cache) : null;
        var gear = gearReference?.Property.ResolveToEntry(gearReference.Owner.FileRef) as ExportEntry;
        if (gear is not null && (!ReferenceEquals(gear.FileRef, package) || !gear.IsA("SkeletalMeshComponent"))) gear = null;
        var otherAmbiguous = false;
        if ((others?.Count ?? 0) <= 1)
        {
            var otherMatch = FindMorphOtherComponent(gear, hair.Concat(array).Distinct().ToArray(),
                oldFace, knownOtherMeshes, resolver, cache);
            if (otherMatch is { Component: null, Ambiguous: false })
                otherMatch = FindComponentByMaterials(array, ActorComponentRole.Accessory, resolver, cache);
            otherAmbiguous = otherMatch.Ambiguous;
            var other = otherMatch.Component;
            if (other is not null)
            {
                var source = others?.SingleOrDefault()?.ResolveToEntry(package);
                var existing = assignments.FindIndex(value => value.Component == other);
                if (existing < 0) assignments.Add((ActorComponentRole.Accessory, other, source));
                else if (assignments[existing].Mesh is null)
                    assignments[existing] = (assignments[existing].Role, other, source);
                else if (source is not null && !SameReference(assignments[existing].Mesh, source))
                    warnings.Add(UserFacingMessage.CreateWarning("loc_ActorAssignment_SecondHairSkipped",
                        $"The second mesh '{source.InstancedFullPath}' could not be applied to '{other.InstancedFullPath}'.",
                        source.InstancedFullPath, other.InstancedFullPath));
            }
        }

        var authored = new[] { face.GetProperty<ObjectProperty>("m_oHairMesh") }
            .Concat(others?.AsEnumerable() ?? []).Where(value => value is { Value: not 0 }).ToArray();
        if (authored is [var only] && only!.ResolveToEntry(package) is { } soleSource &&
            !assignments.Any(value => SameReference(value.Mesh, soleSource)))
        {
            var role = face.GetProperty<ObjectProperty>("m_oHairMesh")?.Value is not (null or 0)
                ? ActorComponentRole.Hair : ActorComponentRole.Accessory;
            var empty = role == ActorComponentRole.Accessory && gear is not null && IsEmpty(gear)
                ? [gear] : array.Where(IsEmpty).Take(2).ToArray();
            if (!(role == ActorComponentRole.Hair ? hairMatch.Ambiguous : otherAmbiguous) && empty is [var component])
                assignments.Add((role, component, soleSource));
        }

        var clearing = assignments.Where(value => value.Mesh is null).Select(value => value.Component.UIndex).ToHashSet();
        return (assignments.Select(value => DescribeAttachment(
            package, actor, value.Role, value.Component, value.Mesh, resolver, cache, clearing, bodyMics,
            referrers, ownedMics.Value)).ToArray(), warnings);

        bool IsEmpty(ExportEntry component)
        {
            var overrides = FindProperty<ArrayProperty<ObjectProperty>>(component, "Materials", resolver, cache);
            return (FindProperty<ObjectProperty>(component, "SkeletalMesh", resolver, cache)?.Property.Value ?? 0) == 0 &&
                (overrides?.Property.All(value => value.Value == 0 || value.ResolveToEntry(overrides.Owner.FileRef) is ExportEntry mic &&
                    IsWritableMic(mic, package) &&
                    (FindProperty<ObjectProperty>(mic, "Parent", resolver, cache)?.Property.Value ?? 0) == 0) ?? true);
        }
    }

    private static ActorAssignmentAttachmentTarget DescribeAttachment(IMEPackage package, ExportEntry actor, ActorComponentRole role,
        ExportEntry component, IEntry? source, GamePackageReferenceResolver resolver, PackageCache cache,
        IReadOnlySet<int> clearing, IReadOnlySet<int> bodyMics, Lazy<ILookup<int, ActorAssignmentReference>> referrers,
        ILookup<int, ExportEntry> ownedMics)
    {
        var current = FindProperty<ObjectProperty>(component, "SkeletalMesh", resolver, cache);
        var mesh = source as ExportEntry ?? resolver.Resolve(source);
        var overrides = FindProperty<ArrayProperty<ObjectProperty>>(component, "Materials", resolver, cache);
        var materials = mesh?.GetBinaryData<SkeletalMesh>().Materials;
        var slotCount = source is null ? overrides?.Property.Count ?? 0 : materials?.Length ?? overrides?.Property.Count ?? 0;
        var slots = Enumerable.Range(0, slotCount).Select(slot =>
        {
            var material = materials is null ? null : mesh!.FileRef.GetEntry(materials[slot]);
            var raw = overrides is not null && slot < overrides.Property.Count
                ? overrides.Property[slot].ResolveToEntry(overrides.Owner.FileRef) : null;
            // Reparenting an attachment must never change a MIC also used by Body.
            var mic = IsWritableMic(raw, package) && !bodyMics.Contains(raw!.UIndex) ? (ExportEntry)raw : null;
            var parent = mic is null ? null : FindProperty<ObjectProperty>(mic, "Parent", resolver, cache);
            return new ActorAssignmentAttachmentSlot(slot, raw?.InstancedFullPath, mic?.UIndex ?? 0,
                parent?.Property.ResolveToEntry(parent.Owner.FileRef)?.InstancedFullPath,
                material is null ? null : new AssetIdentity(material.FileRef.FilePath,
                    PccPackageWorkflow.CanonicalPath(material), material.UIndex, material.ClassName));
        }).ToArray();
        return new ActorAssignmentAttachmentTarget(role, component.UIndex, component.InstancedFullPath,
            current?.Property.ResolveToEntry(current.Owner.FileRef)?.InstancedFullPath,
            source?.UIndex ?? 0, source?.InstancedFullPath, source is null || mesh is not null,
            overrides is not null && ReferenceEquals(overrides.Owner.FileRef, package)
                ? overrides.Property.Select(value => value.Value).ToArray() : null, slots)
        {
            MaterialParentsToClear = ownedMics[component.UIndex]
                .Concat(slots.Where(slot => slot.LocalMicUIndex != 0).Select(slot => package.GetUExport(slot.LocalMicUIndex))
                    .Where(entry => entry.idxLink == actor.UIndex)).DistinctBy(entry => entry.UIndex)
                .Where(entry => referrers.Value[entry.UIndex].All(reference => clearing.Contains(reference.UIndex)))
                    .Select(entry => new ActorAssignmentReference(entry.UIndex, entry.ClassName, entry.InstancedFullPath, "Parent"))
                    .ToArray()
        };
    }

    private static (ExportEntry? Component, bool Ambiguous) FindMorphOtherComponent(ExportEntry? gear,
        IReadOnlyList<ExportEntry> components, ExportEntry? oldFace,
        IReadOnlyList<IEntry> knownOtherMeshes, GamePackageReferenceResolver resolver, PackageCache cache)
    {
        var oldOthers = oldFace?.GetProperty<ArrayProperty<ObjectProperty>>("m_oOtherMeshes");
        var oldMesh = oldOthers is { Count: 1 } ? oldOthers[0].ResolveToEntry(oldFace!.FileRef) : null;
        return FindMatchingComponent(gear is null ? components : components.Append(gear).Distinct().ToArray(),
            oldMesh is null ? [] : [oldMesh], knownOtherMeshes, resolver, cache, gear);
    }

    private static (ExportEntry? Component, bool Ambiguous) FindMatchingComponent(IReadOnlyList<ExportEntry> components,
        IReadOnlyList<IEntry> preferredMeshes, IReadOnlyList<IEntry> knownMeshes,
        GamePackageReferenceResolver resolver, PackageCache cache, ExportEntry? materialMatchComponent = null)
    {
        // Morph may already have replaced the face pointer. Workspace faces can
        // still prove a component's exact Hair/Other identity, without array-order guesses.
        var matches = Matches(preferredMeshes);
        if (matches.Length == 0) matches = Matches(knownMeshes);
        return (matches is [var match] ? match : null, matches.Length > 1);

        ExportEntry[] Matches(IEnumerable<IEntry> meshes)
        {
            return meshes.SelectMany(mesh => components.Where(component =>
                MatchesMorphOther(component, mesh, resolver, cache,
                    allowMaterialMatch: component == materialMatchComponent))).Distinct().Take(2).ToArray();
        }
    }

    private static (ExportEntry? Component, bool Ambiguous) FindComponentByMaterials(IReadOnlyList<ExportEntry> components,
        ActorComponentRole role, GamePackageReferenceResolver resolver, PackageCache cache)
    {
        // Editing the current face in place can remove its old mesh identity.
        // An accessory-array entry can still be proven by its actual material roots.
        var matches = components.Where(component => Classify(component) == role).Take(2).ToArray();
        return (matches is [var match] ? match : null, matches.Length > 1);

        ActorComponentRole? Classify(ExportEntry component)
        {
            var reference = FindProperty<ObjectProperty>(component, "SkeletalMesh", resolver, cache);
            var mesh = resolver.Resolve(reference?.Property.ResolveToEntry(reference.Owner.FileRef));
            var overrides = FindProperty<ArrayProperty<ObjectProperty>>(component, "Materials", resolver, cache);
            var materials = mesh is not null
                ? mesh.GetBinaryData<SkeletalMesh>().Materials.Select(mesh.FileRef.GetEntry)
                : overrides?.Property.Select(value => value.ResolveToEntry(overrides.Owner.FileRef)) ?? [];
            var families = materials.Select(entry => resolver.Resolve(entry) is { } material
                    ? MorphFacePackageReader.DescribeMaterialMaster(component.Game,
                        SupportedMaterialGraphPolicy.ResolveSupportedMaster(component.Game, material, resolver, out _))?.Family
                    : null).Distinct().ToArray();
            return families.Length == 0 ? null
                : families.All(family => family is HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair)
                    ? ActorComponentRole.Hair
                    : families.All(family => family == HeadMaterialFamily.Accessory) ? ActorComponentRole.Accessory : null;
        }
    }

    private static bool MatchesMorphOther(ExportEntry component, IEntry oldMesh,
        GamePackageReferenceResolver resolver, PackageCache cache, bool allowMaterialMatch)
    {
        var meshProperty = FindProperty<ObjectProperty>(component, "SkeletalMesh", resolver, cache);
        var currentMesh = meshProperty?.Property.ResolveToEntry(meshProperty.Owner.FileRef);
        if (currentMesh is not null) return SameReference(currentMesh, oldMesh);
        if (!allowMaterialMatch) return false;
        var mesh = oldMesh as ExportEntry ?? resolver.Resolve(oldMesh);
        if (mesh is null) return false;
        var materials = mesh.GetBinaryData<SkeletalMesh>().Materials;
        var overrides = FindProperty<ArrayProperty<ObjectProperty>>(component, "Materials", resolver, cache);
        if (materials.Length == 0 || overrides?.Property.Count != materials.Length) return false;
        for (var slot = 0; slot < materials.Length; slot++)
        {
            var raw = overrides.Property[slot].ResolveToEntry(overrides.Owner.FileRef);
            if (!IsWritableMic(raw, component.FileRef)) return false;
            var parent = FindProperty<ObjectProperty>((ExportEntry)raw!, "Parent", resolver, cache);
            if (!SameReference(parent?.Property.ResolveToEntry(parent.Owner.FileRef), mesh.FileRef.GetEntry(materials[slot]))) return false;
        }
        return true;
    }

    internal static bool IsWritableMic(IEntry? entry, IMEPackage package) =>
        entry is ExportEntry mic && ReferenceEquals(mic.FileRef, package) &&
        (mic.IsA("MaterialInstanceConstant") || mic.ClassName.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase));

    private static bool SameReference(IEntry? left, IEntry? right) => left is not null && right is not null &&
        left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase) &&
        PccPackageWorkflow.CanonicalPath(left).Equals(PccPackageWorkflow.CanonicalPath(right), StringComparison.OrdinalIgnoreCase);
}
