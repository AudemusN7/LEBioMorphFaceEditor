using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Performs verified actor assignment against the editor workspace. Every mutation is made on a
/// sibling copy, reopened and checked, then atomically installed over the workspace.
/// </summary>
public sealed class ActorAssignmentService
{
    private static readonly string[] MicArrayNames =
        ["TextureParameterValues", "VectorParameterValues", "ScalarParameterValues"];
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
        var fingerprint = PackageFingerprint.Capture(packagePath);
        var temporaryPath = TemporarySibling(packagePath);
        try
        {
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
            EnsureUnchanged(packagePath, fingerprint);
            AtomicReplace(temporaryPath, packagePath);
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
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
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
        if (!candidate.CanAssignMaterials || candidate.MaterialTargets.Count == 0)
        {
            throw new InvalidOperationException(candidate.MaterialIneligibilityReason ??
                                                "The selected actor has no safe local MIC targets.");
        }

        var fingerprint = PackageFingerprint.Capture(packagePath);
        var plan = ReadMaterialPlan(packagePath, inventory, candidate);
        var temporaryPath = TemporarySibling(packagePath);
        try
        {
            File.Copy(packagePath, temporaryPath, overwrite: false);
            using (var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true))
            {
                foreach (var target in candidate.MaterialTargets.DistinctBy(value => value.UIndex))
                {
                    var mic = RequireExport(package, target.UIndex, "MaterialInstanceConstant");
                    var properties = mic.GetProperties();
                    if (plan.Textures is not null)
                    {
                        properties.AddOrReplaceProp(BuildTextureValues(plan.Textures));
                    }
                    if (plan.Vectors is not null)
                    {
                        properties.AddOrReplaceProp(BuildVectorValues(plan.Vectors));
                    }
                    if (plan.Scalars is not null)
                    {
                        properties.AddOrReplaceProp(BuildScalarValues(plan.Scalars));
                    }
                    mic.WriteProperties(properties);
                }
                package.Save();
            }

            VerifyMaterials(temporaryPath, plan, candidate);
            EnsureUnchanged(packagePath, fingerprint);
            AtomicReplace(temporaryPath, packagePath);
            return new ActorMaterialAssignmentResult(
                candidate.UIndex,
                candidate.InstancedPath,
                plan.MaterialOverridePath,
                candidate.MaterialTargets,
                candidate.SkippedMaterials,
                plan.Textures is not null,
                plan.Vectors is not null,
                plan.Scalars is not null);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
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
        if (overrideEntry is null || !overrideEntry.IsA("BioMaterialOverride") ||
            !ReferenceEquals(overrideEntry.FileRef, package))
        {
            throw new InvalidOperationException(
                $"Selected face '{inventory.SelectedFacePath}' has no local BioMaterialOverride.");
        }

        var textures = overrideEntry.GetProperty<ArrayProperty<StructProperty>>("m_aTextureOverrides")?
            .Select(ReadTexture).ToArray();
        var vectors = overrideEntry.GetProperty<ArrayProperty<StructProperty>>("m_aColorOverrides")?
            .Select(ReadVector).ToArray();
        var scalars = overrideEntry.GetProperty<ArrayProperty<StructProperty>>("m_aScalarOverrides")?
            .Select(ReadScalar).ToArray();
        if (textures is null && vectors is null && scalars is null)
        {
            throw new InvalidOperationException("The selected face's BioMaterialOverride has no parameter categories to assign.");
        }

        var targetIds = candidate.MaterialTargets.Select(value => value.UIndex).ToHashSet();
        var protectedData = package.Exports
            .Where(export => export.IsA("MaterialInstanceConstant") && !targetIds.Contains(export.UIndex))
            .ToDictionary(export => export.UIndex, export => export.Data.ToArray());
        var parents = targetIds.ToDictionary(
            id => id,
            id => RequireExport(package, id, "MaterialInstanceConstant")
                .GetProperty<ObjectProperty>("Parent")?.Value ?? 0);
        return new MaterialPlan(
            inventory.SelectedFaceUIndex,
            face.Data.ToArray(),
            overrideEntry.UIndex,
            overrideEntry.InstancedFullPath,
            overrideEntry.Data.ToArray(),
            textures,
            vectors,
            scalars,
            protectedData,
            parents);
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
        ActorAssignmentCandidate candidate)
    {
        using var package = MEPackageHandler.OpenMEPackage(temporaryPath, forceLoadFromDisk: true);
        if (!RequireExport(package, plan.FaceUIndex, "BioMorphFace").Data.SequenceEqual(plan.FaceData) ||
            !RequireExport(package, plan.MaterialOverrideUIndex, "BioMaterialOverride").Data
                .SequenceEqual(plan.MaterialOverrideData))
        {
            throw new InvalidDataException("Verification failed: the source BioMorphFace or BioMaterialOverride changed.");
        }
        foreach (var protectedExport in plan.ProtectedMicData)
        {
            if (!RequireExport(package, protectedExport.Key, "MaterialInstanceConstant").Data
                    .SequenceEqual(protectedExport.Value))
            {
                throw new InvalidDataException(
                    $"Verification failed: non-target MIC #{protectedExport.Key} changed.");
            }
        }
        foreach (var target in candidate.MaterialTargets.DistinctBy(value => value.UIndex))
        {
            var mic = RequireExport(package, target.UIndex, "MaterialInstanceConstant");
            if ((mic.GetProperty<ObjectProperty>("Parent")?.Value ?? 0) != plan.ParentUIndices[target.UIndex])
            {
                throw new InvalidDataException($"Verification failed: parent of '{target.InstancedPath}' changed.");
            }
            VerifyArray(mic, "TextureParameterValues", plan.Textures, ReadMicTexture);
            VerifyArray(mic, "VectorParameterValues", plan.Vectors, ReadMicVector);
            VerifyArray(mic, "ScalarParameterValues", plan.Scalars, ReadMicScalar);
        }
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

    private static string TemporarySibling(string path) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(path))!,
        $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.actor.tmp");

    private static void EnsureUnchanged(string path, PackageFingerprint expected)
    {
        if (PackageFingerprint.Capture(path) != expected)
        {
            throw new IOException("The temporary workspace changed while actor assignment was being written. Nothing was replaced.");
        }
    }

    private static void AtomicReplace(string temporaryPath, string destination)
    {
        var backup = $"{destination}.{Guid.NewGuid():N}.backup";
        try
        {
            File.Replace(temporaryPath, destination, backup, ignoreMetadataErrors: true);
            File.Delete(backup);
        }
        catch
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, destination, overwrite: true);
                File.Delete(backup);
            }
            throw;
        }
    }

    private sealed record MaterialPlan(
        int FaceUIndex,
        byte[] FaceData,
        int MaterialOverrideUIndex,
        string MaterialOverridePath,
        byte[] MaterialOverrideData,
        IReadOnlyList<TextureValue>? Textures,
        IReadOnlyList<VectorValue>? Vectors,
        IReadOnlyList<ScalarValue>? Scalars,
        IReadOnlyDictionary<int, byte[]> ProtectedMicData,
        IReadOnlyDictionary<int, int> ParentUIndices);

    private sealed record TextureValue(string Name, int TextureUIndex);
    private sealed record VectorValue(string Name, float R, float G, float B, float A);
    private sealed record ScalarValue(string Name, float Value);
}
