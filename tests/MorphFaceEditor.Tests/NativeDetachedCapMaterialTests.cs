using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

/// <summary>End-to-end native package coverage for attachment material classification on detached previews.</summary>
internal static class NativeDetachedCapMaterialTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("native detached cap meshes retain per-slot attachment material models", DetachedCapMeshesRetainMaterialModels)
    ];

    private static void DetachedCapMeshesRetainMaterialModels()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var sources = new[]
        {
            Source(MorphFaceGame.LE1, "BIOG_HMF_HIR_PRO.pcc"),
            Source(MorphFaceGame.LE1, "BIOG_HMM_HIR_PRO_R.pcc"),
            Source(MorphFaceGame.LE2, "BIOG_HMF_HIR_PRO.pcc"),
            Source(MorphFaceGame.LE3, "BIOG_HMM_HIR_PRO_R.pcc")
        };
        if (sources.Any(source => source is null))
            throw new TestSkippedException("Required native LE cap attachment packages are not installed.");

        using var reader = new MorphFacePackageReader();
        var le1Bn2 = Load(reader, MorphFaceGame.LE1, "BIOG_HMF_HIR_PRO.pcc", "Beanie02.HMF_HAT_Bn2_MDL");
        TestAssert.True(le1Bn2.Mesh.Positions.Length > 0, "LE1 HMF Bn2 did not decode native mesh geometry.");
        TestAssert.True(le1Bn2.Mesh.RenderData!.MaterialSlots.Count > 0,
            "LE1 HMF Bn2 did not retain its native material slot.");
        TestAssert.True(le1Bn2.Materials.Materials.Count > 0,
            "LE1 HMF Bn2 did not load its native hat material.");
        var bn2Identity = le1Bn2.Mesh.RenderData.MaterialSlots[0]
            ?? throw new InvalidDataException("LE1 HMF Bn2 hat material slot was null.");
        var bn2Material = le1Bn2.Materials.Find(bn2Identity)
            ?? throw new InvalidDataException("LE1 HMF Bn2 hat material did not resolve preview evidence.");
        TestAssert.True(bn2Material.Source.InstancedPath.EndsWith("HMM_HAT_Bn2_MAT_1a", StringComparison.OrdinalIgnoreCase),
            $"LE1 HMF Bn2 resolved unexpected material '{bn2Material.Source.InstancedPath}'.");
        TestAssert.Equal(HumanMaterialPreviewModel.Hat, bn2Material.PreviewModel);
        TestAssert.Equal(HeadMaterialFamily.Accessory, bn2Material.Family);
        TestAssert.Equal(HeadMaterialBlendMode.Opaque, bn2Material.BlendMode);
        TestAssert.True(bn2Material.IsPreviewOnlyAttachment && !bn2Material.ContributesFaceControls,
            "LE1 HMF Bn2 hat material was not preview-only.");
        TestAssert.True(bn2Material.DefaultTextures.ContainsKey("HMN_HAT_Diff"),
            "LE1 HMF Bn2 hat material did not retain its native diffuse sampler.");

        var le1 = Load(reader, MorphFaceGame.LE1, "BIOG_HMM_HIR_PRO_R.pcc", "Cap.HMM_HAT_Cap_MDL");
        AssertMesh(le1, "LE1 male cap");
        AssertSlot(le1, 0, "Cap.HMM_HAT_Cap_MAT_1a", HumanMaterialPreviewModel.Hat, HeadMaterialBlendMode.Opaque);
        AssertSlot(le1, 1, "Cap.HMM_HAT_Cap_Mat_1b", HumanMaterialPreviewModel.CapDecal, HeadMaterialBlendMode.Masked);

        // The HMF cap routes through the detached attachment reader and uses both slots.
        // Slot 0 is the LE2-only native material; slot 1 is the shared cap decal chain.
        var le2 = Load(reader, MorphFaceGame.LE2, "BIOG_HMF_HIR_PRO.pcc", "Cap.HMF_HAT_Cap_MDL");
        AssertMesh(le2, "LE2 female cap");
        AssertSlot(le2, 0, "Cap.HMM_HAT_Cap_MAT_2a", HumanMaterialPreviewModel.CapLe2, HeadMaterialBlendMode.Opaque);
        AssertSlot(le2, 1, "Cap.HMM_HAT_Cap_Mat_2b", HumanMaterialPreviewModel.CapDecal, HeadMaterialBlendMode.Masked);

        // LE3 moved this mesh/material chain under Hat_Cap; its slot 0 is a native cap material.
        var le3 = Load(reader, MorphFaceGame.LE3, "BIOG_HMM_HIR_PRO_R.pcc", "Hat_Cap.HMM_HAT_Cap_MDL");
        AssertMesh(le3, "LE3 male cap");
        AssertSlot(le3, 0, "Hat_Cap.HMM_HAT_Cap_MAT_1a",
            HumanMaterialPreviewModel.AndersonHat, HeadMaterialBlendMode.Opaque);
        AssertSlot(le3, 1, "Hat_Cap.HMM_HAT_Cap_Mat_1b", HumanMaterialPreviewModel.CapDecal, HeadMaterialBlendMode.Masked);
    }

    private static LoadedAttachment Load(
        MorphFacePackageReader reader,
        MorphFaceGame game,
        string fileName,
        string meshPath)
    {
        var source = Source(game, fileName)
            ?? throw new TestSkippedException($"Native package '{fileName}' for {game} is not installed.");
        return reader.LoadDetachedAttachment(source, meshPath);
    }

    private static void AssertMesh(LoadedAttachment attachment, string label)
    {
        TestAssert.True(attachment.Mesh.Positions.Length > 0, $"{label} did not decode native mesh geometry.");
        TestAssert.True(attachment.Mesh.RenderData!.MaterialSlots.Count >= 2, $"{label} did not retain its native material slots.");
        TestAssert.True(attachment.Materials.Materials.Count >= 2, $"{label} did not load both used slot materials.");
    }

    private static void AssertSlot(
        LoadedAttachment attachment,
        int slotIndex,
        string expectedPath,
        HumanMaterialPreviewModel expectedModel,
        HeadMaterialBlendMode expectedBlend)
    {
        var slots = attachment.Mesh.RenderData!.MaterialSlots;
        TestAssert.True(slotIndex < slots.Count, $"Material slot {slotIndex} was missing.");
        var identity = slots[slotIndex]
            ?? throw new InvalidDataException($"Native material slot {slotIndex} was null.");
        var material = attachment.Materials.Find(identity)
            ?? throw new InvalidDataException($"Material slot {slotIndex} did not resolve preview evidence.");

        TestAssert.True(material.Source.InstancedPath.EndsWith(expectedPath, StringComparison.OrdinalIgnoreCase),
            $"Slot {slotIndex} resolved '{material.Source.InstancedPath}', expected a path ending in '{expectedPath}'.");
        TestAssert.Equal(expectedModel, material.PreviewModel);
        TestAssert.Equal(HeadMaterialFamily.Accessory, material.Family);
        TestAssert.Equal(expectedBlend, material.BlendMode);
        TestAssert.True(material.IsPreviewOnlyAttachment, $"{expectedPath} was not preview-only.");
        TestAssert.True(!material.ContributesFaceControls, $"{expectedPath} contributed face controls.");
        TestAssert.True(material.HasCompiledParameterSupport, $"{expectedPath} had no compiled parameter evidence.");
    }

    private static string? Source(MorphFaceGame game, string fileName)
    {
        try
        {
            var path = InstalledGameAssetResolver.ResolvePackage(game, fileName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }
}
