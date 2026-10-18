using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using LegendaryExplorerCore.Helpers;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Performs verified actor assignment against the editor workspace. Every mutation is made on a
/// sibling copy, reopened and checked, then atomically installed over the workspace.
/// </summary>
public sealed class ActorAssignmentService
{
    private readonly ActorAssignmentInventoryService _inventory;

    public ActorAssignmentService(ActorAssignmentInventoryService? inventory = null) =>
        _inventory = inventory ?? new ActorAssignmentInventoryService();

    public ActorAssignmentInventory ReadInventory(
        string packagePath,
        string selectedFaceSelector,
        string selectedProfileKey) =>
        _inventory.Read(packagePath, selectedFaceSelector, selectedProfileKey);

    public ActorMorphAssignmentResult AssignMorph(
        string packagePath,
        string selectedFaceSelector,
        string selectedProfileKey,
        int actorUIndex)
    {
        var inventory = _inventory.Read(packagePath, selectedFaceSelector, selectedProfileKey);
        var candidate = RequireCandidate(inventory, actorUIndex);
        if (!candidate.CanAssignMorph || candidate.MorphTarget is null)
        {
            throw new InvalidOperationException(candidate.MorphIneligibilityReason ??
                                                "The selected actor has no safe morph assignment target.");
        }

        var target = candidate.MorphTarget;
        using (var save = new FileSaveTransaction(packagePath, PackageFingerprint.Capture(packagePath), ".actor.tmp"))
        {
            var temporaryPath = save.CandidatePath;
            File.Copy(packagePath, temporaryPath, overwrite: false);
            LegendaryExplorerCoreRuntime.Initialize();
            using (var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true))
            {
                var face = ExportSelector.Find(package, inventory.SelectedFaceUIndex.ToString(), "BioMorphFace");
                var owner = RequireExport(package, target.OwnerUIndex, target.OwnerClass);
                var properties = owner.GetProperties();
                properties.AddOrReplaceProp(new ObjectProperty(face, target.PropertyName));
                owner.WriteProperties(properties);
                package.Save();
            }

            VerifyMorph(temporaryPath, inventory.SelectedFaceUIndex, target);
            save.Commit("The temporary workspace changed while actor assignment was being written. Nothing was replaced.");
            return new ActorMorphAssignmentResult(
                candidate.UIndex,
                candidate.InstancedPath,
                candidate.TargetKind,
                target.OwnerUIndex,
                target.OwnerPath,
                target.PropertyName,
                target.CurrentMorphUIndex,
                target.CurrentMorphPath,
                inventory.SelectedFaceUIndex,
                inventory.SelectedFacePath);
        }
    }

    public ActorMaterialAssignmentResult AssignMaterials(
        string packagePath,
        string selectedFaceSelector,
        string selectedProfileKey,
        int actorUIndex)
    {
        var inventory = _inventory.Read(packagePath, selectedFaceSelector, selectedProfileKey);
        var candidate = RequireCandidate(inventory, actorUIndex);
        if (!candidate.CanAssignMaterials)
        {
            throw UserFacingMessage.CreateError("loc_VM_actorAssignment_noMaterialOrAttachmentTargets",
                candidate.MaterialIneligibilityReason ?? "The selected actor has no material or attachment targets.");
        }

        var fingerprint = PackageFingerprint.Capture(packagePath);
        var plan = ReadMaterialPlan(packagePath, inventory, candidate);
        using (var save = new FileSaveTransaction(packagePath, fingerprint, ".actor.tmp"))
        {
            var temporaryPath = save.CandidatePath;
            File.Copy(packagePath, temporaryPath, overwrite: false);
            IReadOnlyDictionary<int, byte[]> attachmentData;
            using (var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true))
            {
                foreach (var target in plan.Targets.DistinctBy(value => value.UIndex))
                {
                    var mic = RequireExport(package, target.UIndex, "MaterialInstanceConstant");
                    var properties = mic.GetProperties();
                    if (target.ComponentRole == ActorComponentRole.Body)
                    {
                        UpdateBodySkinVectors(properties, plan.Vectors!);
                        mic.WriteProperties(properties);
                        plan.ExpectedBodyMicData.Add(target.UIndex, mic.Data.ToArray());
                        continue;
                    }
                    ApplyFaceParameters(properties, plan);
                    mic.WriteProperties(properties);
                }
                attachmentData = ApplyAttachments(package, Path.GetFullPath(packagePath), candidate.AttachmentTargets, plan);
                package.Save();
            }

            VerifyMaterials(temporaryPath, plan, attachmentData);
            save.Commit("The temporary workspace changed while actor assignment was being written. Nothing was replaced.");
            return new ActorMaterialAssignmentResult(
                candidate.UIndex,
                candidate.InstancedPath,
                plan.MaterialOverridePath,
                plan.Targets,
                candidate.SkippedMaterials,
                plan.Textures is not null,
                plan.Vectors is not null,
                plan.Scalars is not null)
            {
                ChangedAttachments = candidate.AttachmentTargets,
                UserWarnings = candidate.AttachmentWarnings
            };
        }
    }

    private static ActorAssignmentCandidate RequireCandidate(ActorAssignmentInventory inventory, int actorUIndex) =>
        inventory.Candidates.SingleOrDefault(value => value.UIndex == actorUIndex)
        ?? throw new InvalidOperationException($"Actor export #{actorUIndex} is no longer an assignment candidate.");

    private static ExportEntry RequireExport(IMEPackage package, int uIndex, string expectedClass)
    {
        if (!package.TryGetUExport(uIndex, out var export))
        {
            throw new InvalidDataException($"Required export #{uIndex} is missing.");
        }
        if (!export.IsA(expectedClass) &&
            !(expectedClass == "MaterialInstanceConstant" &&
              export.ClassName.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Export #{uIndex} is {export.ClassName}, not the expected {expectedClass} family.");
        }
        return export;
    }

    private static void VerifyMorph(
        string temporaryPath,
        int selectedFaceUIndex,
        ActorAssignmentMorphTarget target)
    {
        using var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true);
        var owner = RequireExport(package, target.OwnerUIndex, target.OwnerClass);
        var value = owner.GetProperty<ObjectProperty>(target.PropertyName)?.Value ?? 0;
        if (value != selectedFaceUIndex ||
            owner.GetProperty<ObjectProperty>(target.PropertyName)?.ResolveToEntry(package) is not ExportEntry face ||
            !face.IsA("BioMorphFace"))
        {
            throw new InvalidDataException(
                $"Verification failed: {target.OwnerPath}.{target.PropertyName} does not resolve to the selected BioMorphFace.");
        }
    }

    private static IReadOnlyDictionary<int, byte[]> ApplyAttachments(
        IMEPackage package, string originalPackagePath, IReadOnlyList<ActorAssignmentAttachmentTarget> targets, MaterialPlan plan)
    {
        using var cache = new PackageCache();
        var resolver = new GamePackageReferenceResolver(cache);
        var changed = new HashSet<ExportEntry>();
        foreach (var target in targets)
        {
            var component = RequireExport(package, target.ComponentUIndex, "SkeletalMeshComponent");
            var properties = component.GetProperties();
            properties.AddOrReplaceProp(new ObjectProperty(target.SourceMeshUIndex, "SkeletalMesh"));
            var slots = target.SourceMeshUIndex == 0 ? [] : target.MaterialSlots
                .Where(slot => target.ApplyMaterialParameters || slot.OverridePath is not null).ToArray();
            if (!target.MaterialSlotsKnown && slots.Length > 0)
                throw MissingAttachmentMaterials(target.SourceMeshPath!);
            ArrayProperty<ObjectProperty>? replacements = null;
            foreach (var slot in slots)
            {
                var parent = LocalMaterial(slot.SourceMaterial, target.SourceMeshPath!);
                var mic = slot.LocalMicUIndex != 0
                    ? RequireExport(package, slot.LocalMicUIndex, "MaterialInstanceConstant") : null;
                if (mic is null && parent is not null && target.ApplyMaterialParameters)
                {
                    mic = ExportCreator.CreateExport(package, "MaterialInstanceConstant", "MaterialInstanceConstant", component);
                    var values = mic.GetProperties();
                    ApplyFaceParameters(values, plan);
                    mic.WriteProperties(values);
                    plan.Targets.Add(new ActorAssignmentMaterialTarget(mic.UIndex, mic.ObjectNameString,
                        mic.InstancedFullPath, target.Role, target.ComponentPath, slot.SlotIndex,
                        MorphFaceEditor.Core.Materials.HeadMaterialFamily.Unknown, null, [], false));
                }
                if (mic is not null && parent is not null)
                {
                    plan.ParentUIndices[mic.UIndex] = parent.UIndex;
                    if (!ReferenceEquals(mic, parent))
                    {
                        mic.WriteProperty(new ObjectProperty(parent, "Parent"));
                        changed.Add(mic);
                    }
                    if (target.LocalMaterialOverrides is not { } existing ||
                        slot.SlotIndex >= existing.Count || existing[slot.SlotIndex] != mic.UIndex)
                    {
                        replacements ??= new ArrayProperty<ObjectProperty>(
                            target.LocalMaterialOverrides?.Select(value => new ObjectProperty(value)) ?? [], "Materials");
                        while (replacements.Count <= slot.SlotIndex) replacements.Add(new ObjectProperty(0));
                        replacements[slot.SlotIndex] = new ObjectProperty(mic);
                    }
                }
                else
                {
                    // Imported/non-instance overrides remain read-only. Use the authored
                    // mesh material on this component without creating a MIC.
                    replacements ??= new ArrayProperty<ObjectProperty>(
                        target.LocalMaterialOverrides is { } values
                            ? values.Select(value => new ObjectProperty(value))
                            : target.MaterialSlots.Select(value =>
                                new ObjectProperty(LocalMaterial(value.SourceMaterial, target.SourceMeshPath!))),
                        "Materials");
                    replacements[slot.SlotIndex] = new ObjectProperty(parent);
                }
            }
            if (replacements is not null) properties.AddOrReplaceProp(replacements);
            foreach (var reference in target.MaterialParentsToClear)
            {
                var mic = RequireExport(package, reference.UIndex, "MaterialInstanceConstant");
                var values = mic.GetProperties();
                values.AddOrReplaceProp(new ObjectProperty(0, "Parent"));
                if (target.SourceMeshUIndex == 0)
                {
                    values.AddOrReplaceProp(new ArrayProperty<StructProperty>("TextureParameterValues"));
                    values.AddOrReplaceProp(new ArrayProperty<StructProperty>("VectorParameterValues"));
                    values.AddOrReplaceProp(new ArrayProperty<StructProperty>("ScalarParameterValues"));
                    plan.ClearedMicUIndices.Add(mic.UIndex);
                }
                mic.WriteProperties(values);
                plan.ParentUIndices[mic.UIndex] = 0;
                changed.Add(mic);
            }
            component.WriteProperties(properties);
            changed.Add(component);
        }
        return changed.ToDictionary(export => export.UIndex, export => export.Data.ToArray());

        IEntry? LocalMaterial(AssetIdentity? material, string meshPath)
        {
            if (material is null) return null;
            if (material.PackagePath.Equals(originalPackagePath, StringComparison.OrdinalIgnoreCase))
                return package.GetEntry(material.UIndex);
            if (PackageIntegrity.FindExactEntry(package, material.InstancedPath, material.ClassName) is { } existing)
                return existing;
            var source = cache.GetCachedPackage(material.PackagePath)?.GetEntry(material.UIndex);
            if (source is null || !source.ClassName.Equals(material.ClassName, StringComparison.OrdinalIgnoreCase) ||
                !PccPackageWorkflow.CanonicalPath(source).Equals(material.InstancedPath, StringComparison.OrdinalIgnoreCase))
                throw MissingAttachmentMaterials(meshPath);
            return PccPackageWorkflow.ImportDependencyGraph(package,
                source as ExportEntry ?? resolver.Resolve(source) ?? throw MissingAttachmentMaterials(meshPath), null, null);
        }
    }
    private static InvalidDataException MissingAttachmentMaterials(string meshPath) =>
        UserFacingMessage.CreateError("loc_ActorAssignment_AttachmentMaterialsUnavailable",
            $"Could not read material data for actor attachment '{meshPath}'.", meshPath);

    private static void VerifyAttachments(IMEPackage package, IReadOnlyDictionary<int, byte[]> expectedData)
    {
        foreach (var (uIndex, expected) in expectedData)
            if (!package.GetUExport(uIndex).Data.SequenceEqual(expected))
                throw UserFacingMessage.CreateError("loc_PccSave_VerificationMismatch",
                    "The saved PCC did not retain the requested actor attachment data.");
    }

    private static MaterialPlan ReadMaterialPlan(
        string packagePath,
        ActorAssignmentInventory inventory,
        ActorAssignmentCandidate candidate)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var face = ExportSelector.Find(package, inventory.SelectedFaceUIndex.ToString(), "BioMorphFace");
        var overrideEntry = face.GetProperty<ObjectProperty>("m_oMaterialOverrides")?.ResolveToEntry(package)
                            as ExportEntry;
        if (overrideEntry is not null && !overrideEntry.IsA("BioMaterialOverride")) overrideEntry = null;

        var textures = overrideEntry?.GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides")?
            .Select(ReadTexture).ToArray();
        var vectors = overrideEntry?.GetProperty<ArrayProperty<StructProperty>>("m_aColorOverrides")?
            .Select(ReadVector).ToArray();
        var scalars = overrideEntry?.GetProperty<ArrayProperty<StructProperty>>("m_aScalarOverrides")?
            .Select(ReadScalar).ToArray();
        var hasParameters = textures is not null || vectors is not null || scalars is not null;
        if (!hasParameters && candidate.AttachmentTargets.Count == 0)
        {
            throw new InvalidOperationException("The selected face's BioMaterialOverride has no parameter categories to assign.");
        }

        var targets = hasParameters ? candidate.MaterialTargets : [];
        var targetIds = targets.Select(value => value.UIndex).ToHashSet();
        var protectedData = package.Exports
            .Where(export => ActorAssignmentInventoryService.IsWritableMic(export, package) && !targetIds.Contains(export.UIndex))
            .ToDictionary(export => export.UIndex, export => export.Data.ToArray());
        var parents = targetIds.ToDictionary(
            id => id,
            id => RequireExport(package, id, "MaterialInstanceConstant")
                .GetProperty<ObjectProperty>("Parent")?.Value ?? 0);
        return new MaterialPlan(
            inventory.SelectedFaceUIndex,
            face.Data.ToArray(),
            overrideEntry?.UIndex ?? 0,
            overrideEntry?.InstancedFullPath,
            overrideEntry?.Data.ToArray(),
            targets.ToList(),
            textures,
            vectors,
            scalars,
            protectedData,
            parents,
            new Dictionary<int, byte[]>());
    }

    private static TextureValue ReadTexture(StructProperty value) => new(
        RequiredName(value, "nName"),
        value.GetProp<ObjectProperty>("m_pTexture")?.Value
        ?? throw new InvalidDataException("A texture override is missing m_pTexture."));

    private static VectorValue ReadVector(StructProperty value)
    {
        var color = value.GetProp<StructProperty>("cValue")
                    ?? throw new InvalidDataException("A color override is missing cValue.");
        return new VectorValue(
            RequiredName(value, "nName"),
            RequiredFloat(color, "R"), RequiredFloat(color, "G"),
            RequiredFloat(color, "B"), RequiredFloat(color, "A"));
    }

    private static ScalarValue ReadScalar(StructProperty value) => new(
        RequiredName(value, "nName"), RequiredFloat(value, "sValue"));

    private static string RequiredName(StructProperty property, string name) =>
        property.GetProp<NameProperty>(name)?.Value.Name
        ?? throw new InvalidDataException($"A material override is missing {name}.");

    private static float RequiredFloat(StructProperty property, string name) =>
        property.GetProp<FloatProperty>(name)?.Value
        ?? throw new InvalidDataException($"A material override is missing {name}.");

    private static void ApplyFaceParameters(PropertyCollection properties, MaterialPlan plan)
    {
        if (plan.Textures is not null) properties.AddOrReplaceProp(BuildTextureValues(plan.Textures));
        if (plan.Vectors is not null) properties.AddOrReplaceProp(BuildVectorValues(plan.Vectors));
        if (plan.Scalars is not null) properties.AddOrReplaceProp(BuildScalarValues(plan.Scalars));
    }

    private static ArrayProperty<StructProperty> BuildTextureValues(IEnumerable<TextureValue> values) => new(
        values.Select(value => new StructProperty("TextureParameterValue", false,
            ExpressionGuid(),
            new NameProperty(value.Name, "ParameterName"),
            new ObjectProperty(value.TextureUIndex, "ParameterValue"))),
        "TextureParameterValues");

    private static ArrayProperty<StructProperty> BuildVectorValues(IEnumerable<VectorValue> values) => new(
        values.Select(value => new StructProperty("VectorParameterValue", false,
            ExpressionGuid(),
            new StructProperty("LinearColor", false,
                new FloatProperty(value.R, "R"), new FloatProperty(value.G, "G"),
                new FloatProperty(value.B, "B"), new FloatProperty(value.A, "A")) { Name = "ParameterValue", IsImmutable = true },
            new NameProperty(value.Name, "ParameterName"))),
        "VectorParameterValues");

    private static ArrayProperty<StructProperty> BuildScalarValues(IEnumerable<ScalarValue> values) => new(
        values.Select(value => new StructProperty("ScalarParameterValue", false,
            ExpressionGuid(),
            new NameProperty(value.Name, "ParameterName"),
            new FloatProperty(value.Value, "ParameterValue"))),
        "ScalarParameterValues");

    private static void UpdateBodySkinVectors(PropertyCollection properties, IReadOnlyList<VectorValue> source)
    {
        var vectors = properties.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues")
                      ?? new ArrayProperty<StructProperty>("VectorParameterValues");
        foreach (var value in source.Where(value => ActorAssignmentInventoryService.IsBodySkinParameter(value.Name)))
        {
            var existing = vectors.Where(parameter => string.Equals(
                parameter.GetProp<NameProperty>("ParameterName")?.Value.Name,
                value.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (existing.Length == 0)
            {
                vectors.Add(BuildVectorValues([value]).Single());
            }
            else
            {
                foreach (var parameter in existing)
                {
                    // Retain the authored name, ExpressionGUID and any extra struct fields.
                    var color = parameter.GetProp<StructProperty>("ParameterValue")
                                ?? throw new InvalidDataException("A body skin vector is missing ParameterValue.");
                    color.Properties.AddOrReplaceProp(new FloatProperty(value.R, "R"));
                    color.Properties.AddOrReplaceProp(new FloatProperty(value.G, "G"));
                    color.Properties.AddOrReplaceProp(new FloatProperty(value.B, "B"));
                    color.Properties.AddOrReplaceProp(new FloatProperty(value.A, "A"));
                }
            }
        }
        properties.AddOrReplaceProp(vectors);
    }

    private static StructProperty ExpressionGuid()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return new StructProperty("Guid", false,
            new IntProperty(BitConverter.ToInt32(bytes, 0), "A"),
            new IntProperty(BitConverter.ToInt32(bytes, 4), "B"),
            new IntProperty(BitConverter.ToInt32(bytes, 8), "C"),
            new IntProperty(BitConverter.ToInt32(bytes, 12), "D"))
        { Name = "ExpressionGUID", IsImmutable = true };
    }

    private static void VerifyMaterials(
        string temporaryPath,
        MaterialPlan plan,
        IReadOnlyDictionary<int, byte[]> attachmentData)
    {
        using var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true);
        if (!RequireExport(package, plan.FaceUIndex, "BioMorphFace").Data.SequenceEqual(plan.FaceData) ||
            plan.MaterialOverrideUIndex != 0 && !RequireExport(package, plan.MaterialOverrideUIndex, "BioMaterialOverride").Data
                .SequenceEqual(plan.MaterialOverrideData))
        {
            throw new InvalidDataException("Verification failed: the source BioMorphFace or BioMaterialOverride changed.");
        }
        foreach (var protectedExport in plan.ProtectedMicData)
        {
            if (!RequireExport(package, protectedExport.Key, "MaterialInstanceConstant").Data
                    .SequenceEqual(attachmentData.GetValueOrDefault(protectedExport.Key, protectedExport.Value)))
            {
                throw new InvalidDataException(
                    $"Verification failed: non-target MIC #{protectedExport.Key} changed.");
            }
        }
        foreach (var id in plan.ClearedMicUIndices)
        {
            var mic = RequireExport(package, id, "MaterialInstanceConstant");
            if ((mic.GetProperty<ObjectProperty>("Parent")?.Value ?? 0) != 0)
                throw new InvalidDataException($"Verification failed: cleared MIC '{mic.InstancedFullPath}' retains its Parent.");
            VerifyArray<TextureValue>(mic, "TextureParameterValues", [], ReadMicTexture);
            VerifyArray<VectorValue>(mic, "VectorParameterValues", [], ReadMicVector);
            VerifyArray<ScalarValue>(mic, "ScalarParameterValues", [], ReadMicScalar);
        }
        foreach (var target in plan.Targets.DistinctBy(value => value.UIndex))
        {
            if (plan.ClearedMicUIndices.Contains(target.UIndex)) continue;
            var mic = RequireExport(package, target.UIndex, "MaterialInstanceConstant");
            if ((mic.GetProperty<ObjectProperty>("Parent")?.Value ?? 0) != plan.ParentUIndices[target.UIndex])
            {
                throw new InvalidDataException($"Verification failed: parent of '{target.InstancedPath}' changed.");
            }
            if (target.ComponentRole == ActorComponentRole.Body)
            {
                if (!mic.Data.SequenceEqual(plan.ExpectedBodyMicData[target.UIndex]))
                {
                    throw new InvalidDataException(
                        $"Verification failed: body MIC '{target.InstancedPath}' does not match its surgical skin update.");
                }
                continue;
            }
            VerifyArray(mic, "TextureParameterValues", plan.Textures, ReadMicTexture);
            VerifyArray(mic, "VectorParameterValues", plan.Vectors, ReadMicVector);
            VerifyArray(mic, "ScalarParameterValues", plan.Scalars, ReadMicScalar);
        }
        VerifyAttachments(package, attachmentData);
    }

    private static void VerifyArray<T>(
        ExportEntry mic,
        string propertyName,
        IReadOnlyList<T>? expected,
        Func<StructProperty, T> reader)
    {
        if (expected is null) return;
        var actualProperty = mic.GetProperty<ArrayProperty<StructProperty>>(propertyName)
                             ?? throw new InvalidDataException(
                                 $"Verification failed: '{mic.InstancedFullPath}' is missing {propertyName}.");
        var actual = actualProperty.Select(value =>
        {
            var guid = value.GetProp<StructProperty>("ExpressionGUID");
            if (guid is null ||
                new[] { "A", "B", "C", "D" }.Any(name => guid.GetProp<IntProperty>(name) is null) ||
                new[] { "A", "B", "C", "D" }.All(name => guid.GetProp<IntProperty>(name)!.Value == 0))
            {
                throw new InvalidDataException(
                    $"Verification failed: '{mic.InstancedFullPath}' contains a parameter without a valid ExpressionGUID.");
            }
            return reader(value);
        }).ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException(
                $"Verification failed: '{mic.InstancedFullPath}' {propertyName} does not match the source override.");
        }
    }

    private static TextureValue ReadMicTexture(StructProperty value) => new(
        RequiredName(value, "ParameterName"), RequiredObject(value, "ParameterValue"));

    private static VectorValue ReadMicVector(StructProperty value)
    {
        var color = value.GetProp<StructProperty>("ParameterValue")
                    ?? throw new InvalidDataException("A MIC vector parameter is missing ParameterValue.");
        return new VectorValue(RequiredName(value, "ParameterName"),
            RequiredFloat(color, "R"), RequiredFloat(color, "G"),
            RequiredFloat(color, "B"), RequiredFloat(color, "A"));
    }

    private static ScalarValue ReadMicScalar(StructProperty value) => new(
        RequiredName(value, "ParameterName"), RequiredFloat(value, "ParameterValue"));

    private static int RequiredObject(StructProperty value, string name) =>
        value.GetProp<ObjectProperty>(name)?.Value
        ?? throw new InvalidDataException($"A MIC parameter is missing {name}.");

    private sealed record MaterialPlan(
        int FaceUIndex,
        byte[] FaceData,
        int MaterialOverrideUIndex,
        string? MaterialOverridePath,
        byte[]? MaterialOverrideData,
        List<ActorAssignmentMaterialTarget> Targets,
        IReadOnlyList<TextureValue>? Textures,
        IReadOnlyList<VectorValue>? Vectors,
        IReadOnlyList<ScalarValue>? Scalars,
        IReadOnlyDictionary<int, byte[]> ProtectedMicData,
        Dictionary<int, int> ParentUIndices,
        Dictionary<int, byte[]> ExpectedBodyMicData)
    {
        public HashSet<int> ClearedMicUIndices { get; } = [];
    }

    private sealed record TextureValue(string Name, int TextureUIndex);
    private sealed record VectorValue(string Name, float R, float G, float B, float A);
    private sealed record ScalarValue(string Name, float Value);
}
