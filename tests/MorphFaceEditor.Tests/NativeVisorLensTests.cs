using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

/// <summary>Native mesh-route coverage for the additive HMM visor lens material.</summary>
internal static class NativeVisorLensTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("native detached visor lenses retain their game-specific compiled texture bindings", DetachedVisorLensesRetainCompiledBindings)
    ];

    private static void DetachedVisorLensesRetainCompiledBindings()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var le1Source = Source(MorphFaceGame.LE1);
        var le2Source = Source(MorphFaceGame.LE2);
        if (le1Source is null || le2Source is null)
            throw new TestSkippedException("Native LE1 and LE2 HMM visor packages are required.");

        using var reader = new MorphFacePackageReader();
        var le1 = reader.LoadDetachedAttachment(le1Source, "Visor.HMM_HAT_VSR_MDL");
        var le2 = reader.LoadDetachedAttachment(le2Source, "Visor.HMM_HAT_VSR_MDL");

        AssertMesh(le1, "LE1");
        AssertMesh(le2, "LE2");
        var le1Lens = AssertLens(le1, "LE1");
        var le2Lens = AssertLens(le2, "LE2");

        AssertLiteral(le1Lens, "__LensScanline", "BIOG_V_Z_TEXTURES_A_Z.Textures.fx_scanline01");
        AssertLiteral(le1Lens, "__LensEmission", "BIOG_HMM_HIR_PRO_R.Visor.HMM_HLT_LGT_Emis");
        AssertLiteral(le1Lens, "__LensDistortion", "BIOG_V_Z_TEXTURES_A_Z.Textures.fx_Distort_02");
        AssertLiteral(le1Lens, "__LensFractal", "BIOG_V_Z_TEXTURES_A_Z.Textures.fx_Fractal_05");
        TestAssert.True(le1Lens.FixedCubeTexture is not null, "LE1 visor lens did not retain its compiled reflection cube.");
        TestAssert.True(Canonical(le1Lens.FixedCubeTexture!.Source).EndsWith("HMM_HGR_HVYa_CUBE", StringComparison.OrdinalIgnoreCase),
            $"LE1 visor lens resolved unexpected cube '{Canonical(le1Lens.FixedCubeTexture.Source)}'.");

        AssertLiteral(le2Lens, "__LensScanline", "BIOG_V_Z_TEXTURES_A_Z.To_Be_Deleted.fx_scanline01");
        AssertLiteral(le2Lens, "__LensEmission", "BIOG_HMM_HIR_PRO_R.Visor.HMM_HLT_LGT_Emis");
        AssertLiteral(le2Lens, "__LensDistortion", "BioVFX_Z_TEXTURES.Distortion.fx_Distort_02");
        AssertLiteral(le2Lens, "__LensFractal", "BioVFX_Z_TEXTURES.Fractals.fx_Fractal_05");
        TestAssert.True(le2Lens.FixedCubeTexture is null, "LE2 visor lens unexpectedly retained an LE1 reflection cube.");
    }

    private static void AssertMesh(LoadedAttachment attachment, string game)
    {
        TestAssert.True(attachment.Mesh.Positions.Length > 0, $"{game} visor mesh had no decoded geometry.");
        TestAssert.True(attachment.Mesh.RenderData!.MaterialSlots.Count >= 2, $"{game} visor mesh did not retain both material slots.");
        TestAssert.True(attachment.Materials.Materials.Count >= 2, $"{game} visor mesh did not load both native slot materials.");
    }

    private static ResolvedHeadMaterial AssertLens(LoadedAttachment attachment, string game)
    {
        var slots = attachment.Mesh.RenderData!.MaterialSlots;
        var identity = slots[1] ?? throw new InvalidDataException($"{game} visor lens slot was null.");
        var material = attachment.Materials.Find(identity)
            ?? throw new InvalidDataException($"{game} visor lens slot did not resolve material evidence.");

        TestAssert.True(material.Source.InstancedPath.EndsWith("Visor.HMM_HAT_VSR_MAT_Visor", StringComparison.OrdinalIgnoreCase),
            $"{game} visor slot 1 resolved unexpected material '{material.Source.InstancedPath}'.");
        TestAssert.Equal(HumanMaterialPreviewModel.VisorLens, material.PreviewModel);
        TestAssert.Equal(HeadMaterialFamily.Accessory, material.Family);
        TestAssert.Equal(HeadMaterialBlendMode.Additive, material.BlendMode);
        TestAssert.True(!material.TwoSided, $"{game} visor lens was unexpectedly two-sided.");
        TestAssert.True(material.IsPreviewOnlyAttachment && !material.ContributesFaceControls,
            $"{game} visor lens exposed editable face controls.");
        TestAssert.True(material.HasCompiledParameterSupport, $"{game} visor lens has no compiled parameter evidence.");
        return material;
    }

    private static void AssertLiteral(ResolvedHeadMaterial material, string bindingName, string expectedPath)
    {
        TestAssert.True(material.DefaultTextures.TryGetValue(bindingName, out var binding),
            $"'{material.Source.InstancedPath}' did not bind compiled visor texture '{bindingName}'.");
        var actualPath = Canonical(binding!.Texture.Source);
        TestAssert.True(actualPath.EndsWith(expectedPath, StringComparison.OrdinalIgnoreCase),
            $"'{bindingName}' resolved '{actualPath}', expected a path ending in '{expectedPath}'.");
    }

    private static string Canonical(AssetIdentity source) =>
        PccAssetPathPolicy.FromDonorOccurrence(source.InstancedPath, source.PackagePath);

    private static string? Source(MorphFaceGame game)
    {
        try
        {
            var path = InstalledGameAssetResolver.ResolvePackage(game, "BIOG_HMM_HIR_PRO_R.pcc");
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }
}
