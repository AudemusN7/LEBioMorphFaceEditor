using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed partial class ActorAssignmentInventoryService
{
    private static IReadOnlySet<int> ReadBodyMics(IMEPackage package,
        IReadOnlyList<ActorAssignmentComponent> components, GamePackageReferenceResolver resolver, PackageCache cache)
    {
        var bodyMics = new HashSet<int>();
        foreach (var body in components.Where(value => value.IsLocal && value.Role == ActorComponentRole.Body))
        {
            var materials = FindProperty<ArrayProperty<ObjectProperty>>(
                package.GetUExport(body.UIndex), "Materials", resolver, cache);
            if (materials is null) continue;
            foreach (var material in materials.Property.Select(value => value.ResolveToEntry(materials.Owner.FileRef)))
                if (IsWritableMic(material, package)) bodyMics.Add(material!.UIndex);
        }
        return bodyMics;
    }

    // Material preflight is intentionally per slot: one unsafe sibling must not hide a valid face MIC.
    private static void InventoryComponentMaterials(
        IMEPackage package,
        ActorAssignmentComponent component,
        string gamePrefix,
        string selectedProfileKey,
        string? headProfileKey,
        bool hasSkinVectors,
        ActorAssignmentAttachmentTarget? attachment,
        IReadOnlySet<int> bodyMics,
        IEnumerable<ExportEntry> ownedMics,
        GamePackageReferenceResolver resolver,
        PackageCache cache,
        ICollection<ActorAssignmentMaterialTarget> eligible,
        ICollection<ActorAssignmentSkippedMaterial> skipped,
        ICollection<string> warnings,
        ICollection<UserFacingMessage> userWarnings)
    {
        var componentPackage = component.IsLocal
            ? package
            : cache.GetCachedPackage(component.PackagePath);
        var componentExport = componentPackage?.GetEntry(component.UIndex) as ExportEntry;
        if (componentExport is null)
        {
            const string diagnosticTemplate = "Component '{0}' could not be reopened for material inspection.";
            var diagnostic = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                diagnosticTemplate, component.InstancedPath);
            warnings.Add(diagnostic);
            userWarnings.Add(UserFacingMessage.CreateWarning(
                "loc_VM_actorAssignment_componentCouldNotReopenForMaterialInspection",
                diagnostic, component.InstancedPath));
            return;
        }
        var materialsProperty = FindProperty<ArrayProperty<ObjectProperty>>(componentExport, "Materials", resolver, cache);
        var entries = materialsProperty?.Property.Select((value, slot) =>
                (Entry: (IEntry?)value.ResolveToEntry(materialsProperty.Owner.FileRef), Slot: slot)) ?? [];
        var children = ownedMics.Select(entry => (Entry: (IEntry?)entry, Slot: -1));
        foreach (var (rawEntry, slot) in entries.Concat(children)
                     .DistinctBy(value => (value.Entry?.FileRef.FilePath, value.Entry?.UIndex)))
        {
            if (rawEntry is null) continue;
            var resolved = resolver.Resolve(rawEntry);
            var chain = BuildMaterialChain(rawEntry, resolved, package, resolver, cache, out var master);
            // Eligibility follows the material this same transaction will assign.
            var planned = attachment?.MaterialSlots.FirstOrDefault(value => value.SlotIndex == slot &&
                value.LocalMicUIndex == rawEntry.UIndex)?.SourceMaterial;
            if (planned is not null)
            {
                var sourcePackage = planned.PackagePath.Equals(package.FilePath, StringComparison.OrdinalIgnoreCase)
                    ? package : cache.GetCachedPackage(planned.PackagePath);
                if (sourcePackage?.GetEntry(planned.UIndex) is { } source)
                    BuildMaterialChain(source, resolver.Resolve(source), package, resolver, cache, out master);
            }
            var definition = MorphFacePackageReader.DescribeMaterialMaster(package.Game, master);
            var family = definition?.Family ?? HeadMaterialFamily.Unknown;
            var rootSuffix = definition?.ProfileSuffix;
            var materialProfileKey = rootSuffix is not null
                ? HeadProfileIdentity.WithGame(gamePrefix, rootSuffix)
                : null;

            string? reason = null;
            UserFacingMessage? userReason = null;
            var localMic = IsWritableMic(rawEntry, package);
            var bodySharedAttachment = localMic && attachment is not null && bodyMics.Contains(rawEntry.UIndex);
            var unparented = localMic && (slot >= 0 || rawEntry.idxLink == componentExport.UIndex) &&
                (FindProperty<ObjectProperty>((ExportEntry)rawEntry, "Parent", resolver, cache)?.Property.Value ?? 0) == 0;
            if (bodySharedAttachment)
            {
                reason = "The attachment MIC is shared with Body.";
                userReason = UserFacingMessage.Create("loc_ActorAssignment_SharedBodyMaterial");
            }
            else if (!localMic)
            {
                reason = rawEntry is ImportEntry || resolved is not null && !ReferenceEquals(resolved.FileRef, package)
                    ? "Imported materials are read-only and are never assignment targets."
                    : $"{rawEntry.ClassName} is not a local MIC export.";
                userReason = rawEntry is ImportEntry || resolved is not null && !ReferenceEquals(resolved.FileRef, package)
                    ? UserFacingMessage.Create("loc_VM_actorAssignment_importedMaterialReadOnly")
                    : UserFacingMessage.Create("loc_VM_actorAssignment_nonLocalMicExport", rawEntry.ClassName);
            }
            else if (component.Role == ActorComponentRole.Body)
            {
                // Body slots are surgical targets regardless of their parent material family.
                if (!hasSkinVectors)
                {
                    reason = "The selected face has no SkinTone or SkinLightScattering value to assign to body MICs.";
                    userReason = UserFacingMessage.Create("loc_VM_actorAssignment_bodySkinParametersMissing");
                }
            }
            else if (HeadProfileIdentity.IsGeometryCompatible(selectedProfileKey, headProfileKey) &&
                     (unparented || attachment is not null || component.Role == ActorComponentRole.Other ||
                      component.Role == ActorComponentRole.Accessory && family == HeadMaterialFamily.Accessory))
            {
                // Proven attachment ownership applies to every child MIC, including
                // a retained child whose old Parent no longer describes this face.
            }
            else if (!HeadMaterialCatalog.IsAssignableHeadFamily(family))
            {
                reason = family == HeadMaterialFamily.Accessory
                    ? "The complete parent chain classifies as body, gear, headgear, or another accessory."
                    : "The complete parent chain is not a recognized safe head or hair material family.";
                userReason = family == HeadMaterialFamily.Accessory
                    ? UserFacingMessage.Create("loc_VM_actorAssignment_accessoryMaterialFamilyUnsafe")
                    : UserFacingMessage.Create("loc_VM_actorAssignment_unrecognizedHeadMaterialFamily");
            }
            else
            {
                var compatible = (definition?.IsSharedHeadRoot == true &&
                                  HeadProfileIdentity.IsGeometryCompatible(selectedProfileKey, headProfileKey)) ||
                                 HeadProfileIdentity.IsMaterialCompatible(
                                     selectedProfileKey, materialProfileKey, family);
                if (!compatible)
                {
                    reason = materialProfileKey is null
                        ? "The material family is recognizable, but its species/sex profile is not safe to infer."
                        : $"Material profile '{materialProfileKey}' is incompatible with selected face profile '{selectedProfileKey}'.";
                    userReason = materialProfileKey is null
                        ? UserFacingMessage.Create("loc_VM_actorAssignment_materialFamilyProfileUnknown")
                        : UserFacingMessage.Create("loc_VM_actorAssignment_incompatibleMaterialProfile",
                            materialProfileKey, selectedProfileKey);
                }
            }

            if (reason is null)
            {
                eligible.Add(new ActorAssignmentMaterialTarget(
                    rawEntry.UIndex, rawEntry.ObjectNameString, rawEntry.InstancedFullPath,
                    component.Role, component.InstancedPath, slot, family, materialProfileKey,
                    chain, slot >= 0 && materialsProperty?.Inherited == true));
            }
            else
            {
                skipped.Add(new ActorAssignmentSkippedMaterial(
                    rawEntry.UIndex, rawEntry.ObjectNameString, rawEntry.InstancedFullPath,
                    component.Role, component.InstancedPath, slot, family, materialProfileKey,
                    reason, chain, slot >= 0 && materialsProperty?.Inherited == true)
                {
                    UserReason = userReason
                });
            }
        }
    }

    internal static bool IsBodySkinParameter(string? name) =>
        string.Equals(name, "SkinTone", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "SkinLightScattering", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ActorMaterialChainEntry> BuildMaterialChain(
        IEntry start,
        ExportEntry? resolvedStart,
        IMEPackage workspacePackage,
        GamePackageReferenceResolver resolver,
        PackageCache cache,
        out ExportEntry? master)
    {
        master = null;
        var result = new List<ActorMaterialChainEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEntry? raw = start;
        while (raw is not null)
        {
            var key = $"{raw.FileRef.FilePath}|{raw.UIndex}|{raw.InstancedFullPath}";
            if (!visited.Add(key)) break;
            var resolved = ReferenceEquals(raw, start) ? resolvedStart : resolver.Resolve(raw);
            var identity = (IEntry?)resolved ?? raw;
            result.Add(new ActorMaterialChainEntry(
                raw.UIndex,
                raw.ClassName,
                raw.InstancedFullPath,
                raw is ExportEntry && ReferenceEquals(raw.FileRef, workspacePackage),
                resolved is not null));
            if (identity.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase))
            {
                master = resolved;
                break;
            }
            if (resolved is null) break;

            var parentName = resolved.IsA("RvrEffectsMaterialUser") ? "m_pBaseMaterial" : "Parent";
            var parent = FindProperty<ObjectProperty>(resolved, parentName, resolver, cache);
            raw = parent?.Property.ResolveToEntry(parent.Owner.FileRef);
        }
        return result;
    }

}
