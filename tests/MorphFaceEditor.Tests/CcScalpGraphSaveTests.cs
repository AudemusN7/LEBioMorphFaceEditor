using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class CcScalpGraphSaveTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE3 CC scalp graph is face-scoped and survives existing-save and export reopen", SaveExistingRoundTripsFaceScopedCcScalpGraph)
    ];

    internal static void SaveExistingRoundTripsFaceScopedCcScalpGraph()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var sourcePath = Path.GetFullPath("tests/Global Morphs/LE3 GlobalMorphs.pcc");
        var outputPath = Path.Combine(Path.GetTempPath(), $"MFE-CCScalp-{Guid.NewGuid():N}.pcc");
        var exportPath = Path.Combine(Path.GetTempPath(), $"MFE-CCScalp-Export-{Guid.NewGuid():N}.pcc");
        var backup = File.ReadAllBytes(sourcePath);
        try
        {
            File.Copy(sourcePath, outputPath);
            using var reader = new MorphFacePackageReader();
            var donorPath = InstalledGameAssetResolver.ResolvePackage(
                MorphFaceGame.LE3, "BIOG_HMM_HED_PROMorph.pcc");
            const string facePath = "HMM.Archer_Face";
            var loaded = reader.Load(sourcePath, facePath, decodeTextures: false);
            var originalSlots = loaded.BaseHead.RenderData?.MaterialSlots
                ?? throw new InvalidDataException("The LE3 HMM fixture head has no parsed material slots.");
            var target = loaded.Materials.Materials.Values.First(material =>
                material.Family == HeadMaterialFamily.Scalp && originalSlots.Any(slot =>
                    slot?.InstancedPath.Equals(material.Source.InstancedPath,
                        StringComparison.OrdinalIgnoreCase) == true) &&
                !material.Source.InstancedPath.Equals(Le3CcScalpMaterialPolicy.MaleDonorPath,
                    StringComparison.OrdinalIgnoreCase)).Source;
            var donorIdentity = ResolveDonor(donorPath, Le3CcScalpMaterialPolicy.MaleDonorPath);
            var outputHeadPath = loaded.Document.BaseHeadReference!.InstancedPath;
            var originalHeadBytes = ReadExactMeshBytes(loaded.BaseHead.Source);
            var targetSlot = originalSlots.Select((slot, index) => (slot, index))
                .Single(value => value.slot?.InstancedPath.Equals(target.InstancedPath,
                    StringComparison.OrdinalIgnoreCase) == true).index;

            var draft = loaded.Document with
            {
                Source = loaded.Document.Source with { PackagePath = outputPath },
                SourceFingerprint = PackageFingerprint.Capture(outputPath),
                ScalpMaterialSelection = new ScalpMaterialSelection(target, donorIdentity)
            };
            try { _ = new MorphFacePackageWriter().SaveExisting(draft); }
            catch (Exception exception) { throw new InvalidOperationException(exception.ToString()); }
            VerifySavedGraph(outputPath, facePath, outputHeadPath, loaded.BaseHead,
                targetSlot, Le3CcScalpMaterialPolicy.MaleDonorPath, originalHeadBytes, reader,
                preserveOriginalHead: true);

            var exportDraft = loaded.Document with
            {
                ScalpMaterialSelection = new ScalpMaterialSelection(target, donorIdentity)
            };
            var exported = new MorphFacePackageWriter().SaveMorphToPackage(
                exportDraft, sourcePath, exportPath, createNewPackage: true);
            VerifySavedGraph(exportPath, exported.FaceInstancedPath, outputHeadPath, loaded.BaseHead,
                targetSlot, Le3CcScalpMaterialPolicy.MaleDonorPath, originalHeadBytes, reader,
                preserveOriginalHead: false);

            using (var original = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true))
            {
                var face = original.FindExport(facePath, "BioMorphFace")!;
                var head = face.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(original) as ExportEntry;
                TestAssert.True(head is null || head.InstancedFullPath.Equals(outputHeadPath,
                    StringComparison.OrdinalIgnoreCase), "The source face was redirected to the output clone.");
            }
            TestAssert.True(backup.SequenceEqual(File.ReadAllBytes(sourcePath)),
                "Saving the scalp graph modified the source PCC.");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(exportPath)) File.Delete(exportPath);
        }
    }

    private static void VerifySavedGraph(
        string packagePath,
        string facePath,
        string originalHeadPath,
        SkeletalMeshAsset originalHead,
        int targetSlot,
        string donorPath,
        byte[] originalSharedHeadBytes,
        MorphFacePackageReader reader,
        bool preserveOriginalHead)
    {
        using var reopened = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        PackageIntegrity.Verify(reopened);
        var savedFace = reopened.FindExport(facePath, "BioMorphFace")
                        ?? throw new InvalidDataException("The saved face disappeared after reopening.");
        var savedHead = savedFace.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(reopened) as ExportEntry
                        ?? throw new InvalidDataException("The selected scalp graph did not save a face-owned base head.");
        TestAssert.True(savedHead.InstancedFullPath != originalHeadPath,
            "The selected graph changed the original shared head instead of creating a face-owned head clone.");
        TestAssert.True(savedHead.Parent is not null,
            "The cloned head lost its native package ancestry.");
        var sharedHead = PackageIntegrity.FindExactEntry(
            reopened, originalHead.Source.InstancedPath, "SkeletalMesh");
        if (!preserveOriginalHead)
        {
            TestAssert.True(sharedHead is null,
                "A new face export retained the replaced original base-head asset as an unused export/import.");
        }
        else if (sharedHead is ExportEntry sharedExport)
        {
            var savedBytes = sharedExport.Data.ToArray();
            var mismatch = Enumerable.Range(0, Math.Min(savedBytes.Length, originalSharedHeadBytes.Length))
                .FirstOrDefault(index => savedBytes[index] != originalSharedHeadBytes[index], -1);
            TestAssert.True(savedBytes.SequenceEqual(originalSharedHeadBytes),
                $"The shared native base-head export changed while applying a face-scoped scalp graph (length {originalSharedHeadBytes.Length}->{savedBytes.Length}, first mismatch {mismatch}: original={(mismatch >= 0 ? originalSharedHeadBytes[mismatch].ToString("X2") : "n/a")}, saved={(mismatch >= 0 ? savedBytes[mismatch].ToString("X2") : "n/a")}, output={packagePath}).");
        }
        else
            TestAssert.True(sharedHead is ImportEntry,
                "The original shared base-head identity disappeared after saving the selected face.");
        var donorSlots = savedHead.GetBinaryData<SkeletalMesh>().Materials
            .Select(reopened.GetEntry).Where(entry => entry is not null)
            .Count(entry => (entry is ExportEntry export ? PccAssetPathPolicy.FromExport(export) : entry!.InstancedFullPath)
                .Equals(donorPath, StringComparison.OrdinalIgnoreCase));
        TestAssert.Equal(1, donorSlots);
        var reopenedFace = reader.Load(packagePath, facePath, decodeTextures: false);
        TestAssert.True(reopenedFace.BaseHead.Source.InstancedPath != originalHead.Source.InstancedPath,
            "The saved face did not reopen through a private head clone.");
        using var sourcePackage = MEPackageHandler.OpenMEPackage(
            originalHead.Source.PackagePath, forceLoadFromDisk: true);
        VerifyUnchangedHeadData(originalHead, reopenedFace.BaseHead, targetSlot, donorPath,
            sourcePackage, reopened, allowUniqueMicCopy: !preserveOriginalHead);
    }

    private static void VerifyUnchangedHeadData(
        SkeletalMeshAsset original,
        SkeletalMeshAsset reopened,
        int changedMaterialSlot,
        string donorPath,
        IMEPackage sourcePackage,
        IMEPackage savedPackage,
        bool allowUniqueMicCopy)
    {
        var sourceLods = original.AvailableLods.OrderBy(lod => lod.LodIndex).ToArray();
        var savedLods = reopened.AvailableLods.OrderBy(lod => lod.LodIndex).ToArray();
        TestAssert.Equal(sourceLods.Length, savedLods.Length);
        for (var index = 0; index < sourceLods.Length; index++)
        {
            var source = sourceLods[index];
            var saved = savedLods[index];
            TestAssert.True(source.Positions.SequenceEqual(saved.Positions) &&
                            source.Normals.SequenceEqual(saved.Normals),
                $"Head LOD {source.LodIndex} vertex positions or normals changed.");
            TestAssert.True(source.RenderData.Indices.SequenceEqual(saved.RenderData.Indices) &&
                            source.RenderData.Tangents.SequenceEqual(saved.RenderData.Tangents) &&
                            source.RenderData.TextureCoordinates.SequenceEqual(saved.RenderData.TextureCoordinates) &&
                            source.RenderData.BoneIndices.SequenceEqual(saved.RenderData.BoneIndices) &&
                            source.RenderData.BoneWeights.SequenceEqual(saved.RenderData.BoneWeights),
                $"Head LOD {source.LodIndex} render buffers changed.");
            TestAssert.True(source.Topology.Sections.SequenceEqual(saved.Topology.Sections) &&
                            source.Topology.Chunks.Count == saved.Topology.Chunks.Count &&
                            source.Topology.Chunks.Zip(saved.Topology.Chunks).All(pair =>
                                pair.First.BaseVertexIndex == pair.Second.BaseVertexIndex &&
                                pair.First.RigidVertexCount == pair.Second.RigidVertexCount &&
                                pair.First.SoftVertexCount == pair.Second.SoftVertexCount &&
                                pair.First.MaxBoneInfluences == pair.Second.MaxBoneInfluences &&
                                pair.First.BoneMap.SequenceEqual(pair.Second.BoneMap)) &&
                            source.Topology.ReferenceSkeleton.SequenceEqual(saved.Topology.ReferenceSkeleton) &&
                            source.Topology.ActiveBones.SequenceEqual(saved.Topology.ActiveBones) &&
                            source.Topology.RequiredBones.SequenceEqual(saved.Topology.RequiredBones),
                $"Head LOD {source.LodIndex} topology or rig changed.");
        }

        var originalSlots = original.RenderData?.MaterialSlots
            ?? throw new InvalidDataException("The source head has no material slots.");
        var savedSlots = reopened.RenderData?.MaterialSlots
            ?? throw new InvalidDataException("The saved head has no material slots.");
        TestAssert.Equal(originalSlots.Count, savedSlots.Count);
        for (var index = 0; index < originalSlots.Count; index++)
        {
            var expected = index == changedMaterialSlot
                ? donorPath
                : originalSlots[index]?.InstancedPath;
            var actual = savedSlots[index]?.InstancedPath;
            if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) continue;
            if (index == changedMaterialSlot || !allowUniqueMicCopy || expected is null || actual is null ||
                !actual.StartsWith(expected + "_MFE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Head material slot {index} changed unexpectedly: expected '{expected}', got '{actual}'.");

            var sourceMic = sourcePackage.FindEntry(expected) as ExportEntry
                            ?? throw new InvalidDataException($"Source MIC '{expected}' is missing.");
            var savedMic = savedPackage.FindEntry(actual) as ExportEntry
                           ?? throw new InvalidDataException($"Uniquely named authored MIC '{actual}' is missing.");
            TestAssert.True(sourceMic.Parent is not null && savedMic.Parent is not null &&
                            sourceMic.Parent.InstancedFullPath.Equals(savedMic.Parent.InstancedFullPath,
                                StringComparison.OrdinalIgnoreCase),
                $"Uniquely named authored MIC '{actual}' did not retain source ancestry '{sourceMic.Parent?.InstancedFullPath}'.");
            TestAssert.True(StableMicProperties(sourceMic).Equivalent(StableMicProperties(savedMic), (left, right) =>
                ReferenceEquivalent(sourceMic.FileRef, left, savedMic.FileRef, right)),
                $"Uniquely named authored MIC '{actual}' did not preserve the source MIC's authored properties and dependencies.");
        }
    }

    private static PropertyCollection StableMicProperties(ExportEntry material)
    {
        PropertyCollection properties = [.. material.GetProperties().Where(property =>
            !property.Name.Name.Equals("LightingGuid", StringComparison.OrdinalIgnoreCase) &&
            !property.Name.Name.Equals("ParentLightingGuid", StringComparison.OrdinalIgnoreCase))];
        foreach (var name in new[] { "TextureParameterValues", "VectorParameterValues", "ScalarParameterValues" })
        {
            if (properties.GetProp<ArrayProperty<StructProperty>>(name) is not { } values) continue;
            var stableValues = values.DeepClone();
            foreach (var value in stableValues)
                value.Properties.RemoveAll(property =>
                    property.Name.Name.Equals("ExpressionGUID", StringComparison.OrdinalIgnoreCase));
            properties.AddOrReplaceProp(stableValues);
        }
        return properties;
    }

    private static bool ReferenceEquivalent(IMEPackage leftPackage, int leftIndex, IMEPackage rightPackage, int rightIndex)
    {
        if (leftIndex == 0 || rightIndex == 0) return leftIndex == rightIndex;
        var left = leftPackage.GetEntry(leftIndex);
        var right = rightPackage.GetEntry(rightIndex);
        if (left is null || right is null ||
            !left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase)) return false;
        var leftPath = left is ExportEntry leftExport ? PccAssetPathPolicy.FromExport(leftExport) : left.InstancedFullPath;
        var rightPath = right is ExportEntry rightExport ? PccAssetPathPolicy.FromExport(rightExport) : right.InstancedFullPath;
        return leftPath.Equals(rightPath, StringComparison.OrdinalIgnoreCase);
    }

    private static AssetIdentity ResolveDonor(string donorPackagePath, string donorPath)
    {
        using var package = MEPackageHandler.OpenMEPackage(donorPackagePath, forceLoadFromDisk: true);
        var donor = package.Exports.Single(export =>
            export.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) &&
            PccAssetPathPolicy.FromExport(export).Equals(donorPath, StringComparison.OrdinalIgnoreCase));
        return new AssetIdentity(donorPackagePath, donorPath, donor.UIndex, donor.ClassName);
    }

    private static byte[] ReadExactMeshBytes(AssetIdentity identity)
    {
        using var package = MEPackageHandler.OpenMEPackage(identity.PackagePath, forceLoadFromDisk: true);
        var export = identity.UIndex > 0 && package.IsUExport(identity.UIndex)
            ? package.GetUExport(identity.UIndex)
            : package.FindExport(identity.InstancedPath, "SkeletalMesh");
        if (export is null || !export.InstancedFullPath.Equals(identity.InstancedPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Could not read exact source head '{identity.InstancedPath}'.");
        return export.Data.ToArray();
    }
}
