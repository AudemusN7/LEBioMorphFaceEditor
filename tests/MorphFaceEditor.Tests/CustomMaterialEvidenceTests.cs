using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

/// <summary>Focused package-adapter checks for Custom material evidence.</summary>
public static class CustomMaterialEvidenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("custom material evidence enumerates used mesh slots deterministically", EnumeratesUsedMeshSlotsDeterministically),
        new("custom material evidence retains duplicate source material refs", RetainsDuplicateSourceMaterialRefs),
        new("custom material catalogue exposes only proven installed families", CatalogueExposesProvenFamilies)
    ];

    private static void CatalogueExposesProvenFamilies()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            var cookedPath = LegendaryExplorerCoreRuntime.GetCookedPath(game);
            if (string.IsNullOrWhiteSpace(cookedPath) || !Directory.Exists(cookedPath)) continue;
            using var reader = new MorphFacePackageReader();
            var result = new CustomMaterialTemplateCatalogService(reader).Load(game);
            TestAssert.True(result.Options.Count > 0,
                $"The installed {game} catalogue produced no evidence-backed material options.");
            TestAssert.True(result.Options.All(option => option.Family != HeadMaterialFamily.Unknown),
                $"The installed {game} catalogue exposed an Unknown family.");
            TestAssert.True(result.Options.Any(option => option.Family == HeadMaterialFamily.Hair),
                $"The installed {game} catalogue did not recognise the reviewed Shepard hair master.");
            TestAssert.True(result.Options.Any(option => option.Label == "Human Hair"),
                $"The installed {game} catalogue omitted the standard human hair material.");
            var humanMaleOptions = result.Options.Where(option => option.Label.StartsWith("Human Male", StringComparison.Ordinal)).ToArray();
            var humanFemaleOptions = result.Options.Where(option => option.Label.StartsWith("Human Female", StringComparison.Ordinal)).ToArray();
            TestAssert.True(humanMaleOptions.All(option => option.AppearanceCompatibilityKey == "human-male"),
                $"The installed {game} human-male options did not retain their sex-specific compatibility key.");
            TestAssert.True(humanFemaleOptions.All(option => option.AppearanceCompatibilityKey == "human-female"),
                $"The installed {game} human-female options did not retain their sex-specific compatibility key.");
            var iconicHair = result.Options.SingleOrDefault(option =>
                option.Label == "Human Iconic FemShep - Hair");
            TestAssert.True((game is MorphFaceGame.LE1 or MorphFaceGame.LE2) == (iconicHair is not null),
                $"The installed {game} catalogue exposed iconic FemShep hair in the wrong game set.");
            if (iconicHair is not null)
            {
                TestAssert.True(iconicHair.Template.Textures.ContainsKey("HAIR_Diff") &&
                                iconicHair.Template.SupportedTextures.Contains("HAIR_Diff"),
                    $"The installed {game} iconic FemShep hair did not expose its semantic diffuse sampler.");
            }
            TestAssert.True(result.PreviewAttachments.Count > 0,
                $"The installed {game} catalogue exposed no preview-only hair/accessory meshes.");
            TestAssert.True(result.PreviewAttachments.All(attachment =>
                    !CustomMaterialTemplateCatalogService.IsDevelopmentLeftover(attachment.InstancedPath)),
                $"The installed {game} catalogue exposed a development leftover attachment.");
            foreach (var attachment in result.PreviewAttachments)
            {
                reader.ValidateDetachedAttachment(attachment.PackagePath, attachment.InstancedPath);
            }
            var previewAttachment = result.PreviewAttachments[0];
            var loadedAttachment = reader.LoadDetachedAttachment(
                previewAttachment.PackagePath, previewAttachment.InstancedPath);
            TestAssert.True(loadedAttachment.Mesh.Positions.Length > 0,
                $"The installed {game} preview attachment did not decode renderable geometry.");
        }
    }

    private static void EnumeratesUsedMeshSlotsDeterministically()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var cookedPath = LegendaryExplorerCoreRuntime.DefaultLe3CookedPath;
        var packagePath = string.IsNullOrWhiteSpace(cookedPath)
            ? null
            : Path.Combine(cookedPath, "BIOG_HMF_HIR_PRO.pcc");
        if (packagePath is null || !File.Exists(packagePath))
        {
            // The repository has no portable compiled shader-chain fixture for
            // this source. Installed-package validation remains an explicit
            // environment check rather than a fabricated graph assertion.
            return;
        }

        using var reader = new MorphFacePackageReader();
        var evidence = reader.LoadMeshMaterialEvidence(
            packagePath,
            "Hair_PROShepard.HMF_HIR_PROShepard_MDL");

        TestAssert.Equal(
            Path.GetFullPath(packagePath),
            Path.GetFullPath(evidence.Mesh.PackagePath));
        TestAssert.True(evidence.Slots.Count > 0,
            "The selected evidence mesh had no used material slots.");
        TestAssert.True(
            evidence.Slots.Select(slot => slot.SlotIndex).SequenceEqual(
                evidence.Slots.Select(slot => slot.SlotIndex).OrderBy(index => index)),
            "Material slots were not returned in stable source-slot order.");
        TestAssert.True(evidence.Slots.All(slot => slot.Sections.Count > 0),
            "A returned material slot was not backed by a source draw section.");
        TestAssert.True(evidence.Slots.All(slot =>
                slot.Sections.SequenceEqual(slot.Sections
                    .OrderBy(section => section.LodIndex)
                    .ThenBy(section => section.SectionIndex))),
            "Material sections were not returned in stable LOD/section order.");
        TestAssert.True(evidence.Materials.SequenceEqual(
                evidence.Slots.Select(slot => slot.Material)),
            "The detached material convenience view changed slot order.");
    }

    private static void RetainsDuplicateSourceMaterialRefs()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var packagePath = Path.GetFullPath(Path.Combine(
            "tests", "Global Morphs", "LE3 GlobalMorphs.pcc"));
        if (!File.Exists(packagePath))
        {
            return;
        }

        string? selector = null;
        int[]? expectedSlots = null;
        int[]? expectedMaterialUIndices = null;
        using (var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true))
        {
            foreach (var export in package.Exports
                         .Where(entry => entry.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(entry => entry.InstancedFullPath, StringComparer.OrdinalIgnoreCase))
            {
                SkeletalMesh mesh;
                try
                {
                    mesh = export.GetBinaryData<SkeletalMesh>();
                }
                catch
                {
                    continue;
                }

                var materialUIndices = mesh.Materials ?? [];
                if (mesh.LODModels is not { Length: > 0 } || materialUIndices.Length < 2)
                {
                    continue;
                }

                int[] used;
                try
                {
                    var maps = ReadMaterialMaps(export, mesh.LODModels.Length, materialUIndices.Length);
                    used = mesh.LODModels
                        .SelectMany((lod, lodIndex) => (lod.Sections ?? [])
                            .Select(section => LodMaterialMap.Resolve(
                                section.MaterialIndex,
                                maps[lodIndex],
                                materialUIndices.Length,
                                $"{export.InstancedFullPath} LOD {lodIndex} section")))
                        .Distinct()
                        .OrderBy(index => index)
                        .ToArray();
                }
                catch
                {
                    continue;
                }
                var hasMultipleSlots = used.Length > 1;
                var hasDuplicateReferences = used
                    .GroupBy(index => materialUIndices[index])
                    .Any(group => group.Count() > 1);
                if (!hasMultipleSlots && !hasDuplicateReferences)
                {
                    continue;
                }

                selector = export.InstancedFullPath;
                expectedSlots = used;
                expectedMaterialUIndices = materialUIndices;
                break;
            }
        }

        if (selector is null || expectedSlots is null || expectedMaterialUIndices is null)
        {
            // The checked-in corpus does not guarantee a multi-slot/duplicate
            // skeletal mesh in every game fixture. Do not invent one merely
            // to make this adapter test appear stronger than its evidence.
            return;
        }

        using var reader = new MorphFacePackageReader();
        var evidence = reader.LoadMeshMaterialEvidence(packagePath, selector);
        TestAssert.True(
            evidence.Slots.Select(slot => slot.SlotIndex).SequenceEqual(expectedSlots),
            "The adapter changed the source mesh's used-slot order or collapsed a slot.");
        var duplicateSourceRefs = expectedSlots
            .GroupBy(slot => expectedMaterialUIndices[slot])
            .Any(group => group.Count() > 1);
        if (duplicateSourceRefs)
        {
            TestAssert.True(
                evidence.Slots.GroupBy(slot => MaterialIdentityKey.Create(slot.Source))
                    .Any(group => group.Count() > 1),
                "Duplicate source material refs were collapsed in the detached evidence.");
        }
    }

    private static IReadOnlyList<int[]> ReadMaterialMaps(
        ExportEntry export,
        int lodCount,
        int materialCount)
    {
        var identity = Enumerable.Range(0, materialCount).ToArray();
        var result = Enumerable.Range(0, lodCount)
            .Select(_ => identity.ToArray())
            .ToArray();
        var lodInfo = export.GetProperty<ArrayProperty<StructProperty>>("LODInfo");
        if (lodInfo is null)
        {
            return result;
        }

        for (var lodIndex = 0; lodIndex < Math.Min(lodCount, lodInfo.Count); lodIndex++)
        {
            var authoredMap = lodInfo[lodIndex].GetProp<ArrayProperty<IntProperty>>("LODMaterialMap");
            if (authoredMap is not { Count: > 0 })
            {
                continue;
            }
            for (var localIndex = 0; localIndex < Math.Min(authoredMap.Count, materialCount); localIndex++)
            {
                result[lodIndex][localIndex] = authoredMap[localIndex].Value;
            }
        }
        return result;
    }
}
