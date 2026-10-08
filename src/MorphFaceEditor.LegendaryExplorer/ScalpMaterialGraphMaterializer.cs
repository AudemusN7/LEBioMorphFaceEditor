using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Applies the explicit LE3 CC scalp choice to a face-owned base-head clone.
/// The installed/native head and every other face keep their authored material slots.
/// </summary>
internal static class ScalpMaterialGraphMaterializer
{
    internal static ExportEntry Apply(
        IMEPackage package,
        ExportEntry face,
        AssetIdentity baseHeadIdentity,
        ScalpMaterialSelection selection,
        ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(baseHeadIdentity);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(warnings);
        if (package.Game != MEGame.LE3)
            throw new InvalidDataException("CC scalp material selection is supported only in LE3 packages.");

        using var cache = new PackageCache();
        var donor = InstalledGameAssetResolver.RequireExact(
            cache,
            selection.DonorMaterial.PackagePath,
            selection.DonorMaterial.InstancedPath,
            selection.DonorMaterial.ClassName,
            selection.DonorMaterial.UIndex);
        if (!donor.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) &&
            !donor.ClassName.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The selected CC scalp donor '{donor.InstancedFullPath}' is {donor.ClassName}, not a material instance.");
        if (donor.Game != package.Game)
            throw new InvalidDataException(
                $"The selected CC scalp donor '{donor.InstancedFullPath}' is from {donor.Game}, not {package.Game}.");
        var donorPath = PccAssetPathPolicy.FromExport(donor);
        if (!Le3CcScalpMaterialPolicy.IsCanonicalDonorPath(donorPath))
            throw new InvalidDataException(
                $"'{donorPath}' is not one of the reviewed LE3 Player CC scalp material instances.");
        EnsureCanonicalCcScalpGraph(donor, cache);

        var currentHead = face.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package);
        var baseHead = currentHead is ExportEntry localHead &&
                       localHead.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)
            ? localHead
            : ResolveBaseHeadDonor(baseHeadIdentity, package.Game, cache);

        if (!baseHead.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Base head '{baseHead.InstancedFullPath}' is not a SkeletalMesh.");
        var femaleHead = baseHeadIdentity.InstancedPath.Contains("HMF", StringComparison.OrdinalIgnoreCase);
        if (!Le3CcScalpMaterialPolicy.CanonicalDonorPath(femaleHead).Equals(
                donorPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The selected scalp donor '{donorPath}' does not match the sex of base head '{baseHeadIdentity.InstancedPath}'.");
        if (!PccAssetPathPolicy.FromExport(baseHead).Equals(baseHeadIdentity.InstancedPath,
                StringComparison.OrdinalIgnoreCase) &&
            !baseHead.InstancedFullPath.Equals(baseHeadIdentity.InstancedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The resolved base head '{baseHead.InstancedFullPath}' does not match '{baseHeadIdentity.InstancedPath}'.");

        var mesh = baseHead.GetBinaryData<SkeletalMesh>();
        var materials = mesh.Materials ?? [];
        var existingDonorSlots = materials.Select((uIndex, slot) =>
            (Slot: slot, Path: MaterialPath(baseHead.FileRef, uIndex)))
            .Where(value => value.Path?.Equals(donorPath, StringComparison.OrdinalIgnoreCase) == true)
            .Select(value => value.Slot).ToArray();
        if (existingDonorSlots.Length == 1)
        {
            // Repeated saves and stock heads that already use this exact graph
            // need no second private clone.
            return baseHead;
        }
        if (existingDonorSlots.Length > 1)
            throw new InvalidDataException(
                $"The base head already has {existingDonorSlots.Length} slots for selected scalp donor '{donorPath}'.");
        var targetSlots = materials.Select((uIndex, slot) =>
            (Slot: slot, Path: MaterialPath(baseHead.FileRef, uIndex)))
            .Where(value => value.Slot >= 0 &&
                value.Path?.Equals(selection.TargetMaterial.InstancedPath, StringComparison.OrdinalIgnoreCase) == true)
            .Select(value => value.Slot).ToArray();
        if (targetSlots.Length != 1)
            throw new InvalidDataException(
                $"The selected scalp material '{selection.TargetMaterial.InstancedPath}' matched {targetSlots.Length} base-head slots; exactly one is required.");

        var sourceFlags = baseHead.ExportFlags;
        var parent = baseHead.Parent ?? throw new InvalidDataException(
            $"Base head '{baseHead.InstancedFullPath}' has no native package ancestry to preserve.");
        var clonePath = UniqueClonePath(package, parent, baseHead.ObjectName.Name);
        var clone = currentHead is ExportEntry
            ? CloneLocalBaseHead(baseHead, parent, clonePath)
            : PccPackageWorkflow.ImportDependencyGraph(
                package,
                baseHead,
                destinationParent: null,
                textureCatalog: null,
                preferBiogTextures: false,
                warnings: warnings,
                applyCorpusMaterialPolicy: false,
                targetPathOverride: clonePath);
        if (clone.Parent is null || !clone.Parent.InstancedFullPath.Equals(parent.InstancedFullPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The face-owned scalp head clone did not retain the native parent ancestry.");
        if (currentHead is ExportEntry && clone.ExportFlags != sourceFlags)
            throw new InvalidDataException("The face-owned scalp head clone changed the authored export flags.");

        var targetPath = donorPath;
        var materializedDonor = PccPackageWorkflow.ImportDependencyGraph(
            package,
            donor,
            destinationParent: null,
            textureCatalog: null,
            preferBiogTextures: false,
            warnings: warnings,
            applyCorpusMaterialPolicy: false,
            targetPathOverride: targetPath);
        if (!materializedDonor.ClassName.Equals(selection.DonorMaterial.ClassName, StringComparison.OrdinalIgnoreCase) ||
            !PccAssetPathPolicy.FromExport(materializedDonor).Equals(targetPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The selected CC scalp donor changed identity while materialising: '{materializedDonor.InstancedFullPath}'.");

        var clonedMesh = clone.GetBinaryData<SkeletalMesh>();
        clonedMesh.Materials[targetSlots[0]] = materializedDonor.UIndex;
        clone.WriteBinary(clonedMesh);
        if (!PccAssetPathPolicy.FromExport(clone).Equals(clone.InstancedFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cloned base head does not have a stable PCC-facing identity.");
        return clone;
    }

    internal static void EnsureCanonicalCcScalpGraph(ExportEntry donor, PackageCache cache)
    {
        var resolver = new GamePackageReferenceResolver(cache);
        if (!SupportedMaterialGraphPolicy.IsSupported(donor.Game, donor, resolver, out var unsupported))
            throw new InvalidDataException(
                $"Selected CC scalp material '{donor.InstancedFullPath}' has an unsupported graph: {unsupported}.");

        var current = donor;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var key = $"{Path.GetFullPath(current.FileRef.FilePath)}|{current.UIndex}";
            if (!visited.Add(key))
                throw new InvalidDataException($"Selected CC scalp material '{donor.InstancedFullPath}' has a parent cycle.");
            if (current.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase))
            {
                if (!PccAssetPathPolicy.FromExport(current).Equals(
                        Le3CcScalpMaterialPolicy.MasterMaterialPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Selected CC scalp material '{donor.InstancedFullPath}' terminates at '{PccAssetPathPolicy.FromExport(current)}', not the reviewed CC scalp master.");
                return;
            }

            var propertyName = current.IsA("RvrEffectsMaterialUser") ? "m_pBaseMaterial" : "Parent";
            var parentReference = current.GetProperty<ObjectProperty>(propertyName)?.ResolveToEntry(current.FileRef)
                ?? throw new InvalidDataException(
                    $"Selected CC scalp material '{current.InstancedFullPath}' has no resolvable {propertyName}.");
            current = resolver.Resolve(parentReference)
                ?? throw new InvalidDataException(
                    $"Selected CC scalp material '{current.InstancedFullPath}' has an unresolved {propertyName}.");
        }
    }

    private static string? MaterialPath(IMEPackage package, int uIndex)
    {
        if (uIndex == 0 || package.GetEntry(uIndex) is not { } material) return null;
        return material is ExportEntry export
            ? PccAssetPathPolicy.FromExport(export)
            : material.InstancedFullPath;
    }

    private static ExportEntry CloneLocalBaseHead(ExportEntry source, IEntry parent, string targetPath)
    {
        var clone = EntryCloner.CloneTree(source);
        var separator = targetPath.LastIndexOf('.');
        clone.ObjectName = new NameReference(separator < 0 ? targetPath : targetPath[(separator + 1)..]);
        if (clone.Parent is null || !clone.Parent.InstancedFullPath.Equals(parent.InstancedFullPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cloned base head did not retain its source parent.");
        return clone;
    }

    private static ExportEntry ResolveBaseHeadDonor(
        AssetIdentity identity,
        MEGame game,
        PackageCache cache)
    {
        var installed = new InstalledGameAssetResolver().Resolve(game, identity, cache);
        if (installed is not null) return installed;
        if (identity.IsImport)
            throw new InvalidDataException(
                $"The imported base head '{identity.InstancedPath}' has no exact official donor to clone.");
        return InstalledGameAssetResolver.RequireExact(
            cache, identity.PackagePath, identity.InstancedPath, "SkeletalMesh", identity.UIndex);
    }

    private static string UniqueClonePath(IMEPackage package, IEntry parent, string sourceName)
    {
        var stem = $"{sourceName}_MFE_CcScalp";
        for (var suffix = 1; ; suffix++)
        {
            var candidate = suffix == 1 ? stem : $"{stem}_{suffix}";
            var path = $"{parent.InstancedFullPath}.{candidate}";
            if (package.FindEntry(path, "SkeletalMesh") is null) return path;
        }
    }
}
