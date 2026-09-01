using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

public sealed partial class ActorAssignmentInventoryService
{
    // Material preflight is intentionally per slot: one unsafe sibling must not hide a valid face MIC.
    private static void InventoryComponentMaterials(
        IMEPackage package,
        ActorAssignmentComponent component,
        string gamePrefix,
        string selectedProfileKey,
        string? headProfileKey,
        GamePackageReferenceResolver resolver,
        PackageCache cache,
        ICollection<ActorAssignmentMaterialTarget> eligible,
        ICollection<ActorAssignmentSkippedMaterial> skipped,
        ICollection<string> warnings)
    {
        var componentPackage = component.IsLocal
            ? package
            : cache.GetCachedPackage(component.PackagePath);
        var componentExport = componentPackage?.Exports.FirstOrDefault(export =>
            export.InstancedFullPath.Equals(component.InstancedPath, StringComparison.OrdinalIgnoreCase));
        if (componentExport is null)
        {
            warnings.Add($"Component '{component.InstancedPath}' could not be reopened for material inspection.");
            return;
        }
        var materialsProperty = FindProperty<ArrayProperty<ObjectProperty>>(
            componentExport, "Materials", resolver, cache);
        if (materialsProperty is null) return;

        for (var slot = 0; slot < materialsProperty.Property.Count; slot++)
        {
            var rawEntry = materialsProperty.Property[slot].ResolveToEntry(materialsProperty.Owner.FileRef);
            if (rawEntry is null) continue;
            var resolved = resolver.Resolve(rawEntry);
            var chain = BuildMaterialChain(rawEntry, package, resolver, cache);
            var chainIdentities = chain.Select(node => node.InstancedPath).ToArray();
            var family = HeadMaterialClassifier.ClassifyChain(
                chainIdentities, component.Role == ActorComponentRole.Hair);
            var rootIdentity = HeadMaterialClassifier.EffectiveRootIdentity(chainIdentities);
            var rootSuffix = HeadProfileIdentity.InferSuffix(rootIdentity);
            var materialProfileKey = rootSuffix is not null && rootSuffix != "human"
                ? HeadProfileIdentity.WithGame(gamePrefix, rootSuffix)
                : null;

            string? reason = null;
            var localMic = rawEntry is ExportEntry local && ReferenceEquals(local.FileRef, package) &&
                           (local.IsA("MaterialInstanceConstant") ||
                            local.ClassName.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase));
            if (!localMic)
            {
                reason = rawEntry is ImportEntry || resolved is not null && !ReferenceEquals(resolved.FileRef, package)
                    ? "Imported materials are read-only and are never assignment targets."
                    : $"{rawEntry.ClassName} is not a local MIC export.";
            }
            else if (!HeadMaterialClassifier.IsAssignableHeadFamily(family))
            {
                reason = family == HeadMaterialFamily.Accessory
                    ? "The complete parent chain classifies as body, gear, headgear, or another accessory."
                    : "The complete parent chain is not a recognized safe head or hair material family.";
            }
            else
            {
                var compatible = (IsApprovedSharedRoot(family, rootIdentity) &&
                                  HeadProfileIdentity.IsGeometryCompatible(selectedProfileKey, headProfileKey)) ||
                                 HeadProfileIdentity.IsMaterialCompatible(
                                     selectedProfileKey, materialProfileKey, family);
                if (!compatible)
                {
                    reason = materialProfileKey is null
                        ? "The material family is recognizable, but its species/sex profile is not safe to infer."
                        : $"Material profile '{materialProfileKey}' is incompatible with selected face profile '{selectedProfileKey}'.";
                }
            }

            if (reason is null)
            {
                eligible.Add(new ActorAssignmentMaterialTarget(
                    rawEntry.UIndex, rawEntry.ObjectNameString, rawEntry.InstancedFullPath,
                    component.Role, component.InstancedPath, slot, family, materialProfileKey,
                    chain, materialsProperty.Inherited));
            }
            else
            {
                skipped.Add(new ActorAssignmentSkippedMaterial(
                    rawEntry.UIndex, rawEntry.ObjectNameString, rawEntry.InstancedFullPath,
                    component.Role, component.InstancedPath, slot, family, materialProfileKey,
                    reason, chain, materialsProperty.Inherited));
            }
        }
    }

    private static IReadOnlyList<ActorMaterialChainEntry> BuildMaterialChain(
        IEntry start,
        IMEPackage workspacePackage,
        GamePackageReferenceResolver resolver,
        PackageCache cache)
    {
        var result = new List<ActorMaterialChainEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEntry? raw = start;
        while (raw is not null)
        {
            var key = $"{raw.FileRef.FilePath}|{raw.UIndex}|{raw.InstancedFullPath}";
            if (!visited.Add(key)) break;
            var resolved = resolver.Resolve(raw);
            var identity = (IEntry?)resolved ?? raw;
            result.Add(new ActorMaterialChainEntry(
                raw.UIndex,
                raw.ClassName,
                raw.InstancedFullPath,
                raw is ExportEntry && ReferenceEquals(raw.FileRef, workspacePackage),
                resolved is not null));
            if (identity.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase)) break;
            if (resolved is null) break;

            var parent = FindProperty<ObjectProperty>(resolved, "Parent", resolver, cache)
                         ?? FindProperty<ObjectProperty>(resolved, "m_pBaseMaterial", resolver, cache);
            raw = parent?.Property.ResolveToEntry(parent.Owner.FileRef);
        }
        return result;
    }

    private static bool IsApprovedSharedRoot(HeadMaterialFamily family, string? rootIdentity)
    {
        if (family is not (HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes or
            HeadMaterialFamily.Scalp or HeadMaterialFamily.Teeth or
            HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair) ||
            string.IsNullOrWhiteSpace(rootIdentity))
        {
            return false;
        }
        return new[]
        {
            "HMM_", "HMF_", "HMN_", "ASA_", "BIOG_HAIR", "BIO_EYE", "PROShort", "HED_Scalp"
        }.Any(marker => rootIdentity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
