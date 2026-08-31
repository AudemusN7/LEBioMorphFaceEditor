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
            var chainFamilies = chainIdentities
                .Select(identity => HeadMaterialClassifier.Classify(
                    identity, component.Role == ActorComponentRole.Hair))
                .Where(value => value != HeadMaterialFamily.Unknown)
                .ToArray();
            var approvedLe3VorchaEyeChain = selectedProfileKey.Equals(
                                                "le3-vorcha", StringComparison.OrdinalIgnoreCase) &&
                                            chainFamilies.Length > 0 &&
                                            chainFamilies.All(value => value is HeadMaterialFamily.VorchaEyes or
                                                HeadMaterialFamily.TurianEyes or HeadMaterialFamily.Eyes) &&
                                            chainFamilies.Any(value => value is HeadMaterialFamily.VorchaEyes or
                                                HeadMaterialFamily.TurianEyes);
            if (approvedLe3VorchaEyeChain)
            {
                family = HeadMaterialFamily.TurianEyes;
            }
            var suffixes = chainIdentities
                .Select(identity => HeadProfileIdentity.InferSuffix(identity))
                .Where(suffix => suffix is not null && suffix != "human")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var sharedTurianSchema =
                (selectedProfileKey.EndsWith("-female-turian", StringComparison.OrdinalIgnoreCase) &&
                 suffixes.All(suffix => suffix is "female-turian" or "turian")) ||
                (approvedLe3VorchaEyeChain &&
                 suffixes.All(suffix => suffix is "vorcha" or "turian"));
            var normalizedSuffixes = sharedTurianSchema
                ? suffixes.Select(_ => "turian").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : suffixes;
            var conflictingProfiles = normalizedSuffixes.Length > 1;
            var materialProfileKey = normalizedSuffixes.Length == 1
                ? HeadProfileIdentity.WithGame(gamePrefix, normalizedSuffixes[0]!)
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
            else if (conflictingProfiles)
            {
                reason = $"The parent chain contains conflicting profile identities: {string.Join(", ", suffixes)}.";
            }
            else if (!HeadMaterialClassifier.IsAssignableHeadFamily(family))
            {
                reason = family == HeadMaterialFamily.Accessory
                    ? "The complete parent chain classifies as body, gear, headgear, or another accessory."
                    : "The complete parent chain is not a recognized safe head or hair material family.";
            }
            else
            {
                var compatible = HeadProfileIdentity.IsMaterialCompatible(selectedProfileKey, materialProfileKey, family);
                if (!compatible && suffixes.Length == 0 &&
                    IsKnownSharedSchema(family, chainIdentities) &&
                    HeadProfileIdentity.IsGeometryCompatible(selectedProfileKey, headProfileKey))
                {
                    compatible = true;
                    materialProfileKey = headProfileKey;
                }
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

    private static bool IsKnownSharedSchema(HeadMaterialFamily family, IReadOnlyList<string> chain)
    {
        if (family is not (HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes or HeadMaterialFamily.Scalp or
            HeadMaterialFamily.Teeth or HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair))
        {
            return false;
        }
        var text = string.Join('|', chain);
        return new[]
        {
            "HMM_", "HMF_", "HMN_", "BIOG_HAIR", "BIO_EYE", "PROShort", "HED_Scalp"
        }.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
