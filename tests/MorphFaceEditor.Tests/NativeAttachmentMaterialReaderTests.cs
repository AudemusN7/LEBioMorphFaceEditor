using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

/// <summary>Native installed-package coverage for preview-only hat and visor material reads.</summary>
public static class NativeAttachmentMaterialReaderTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("native attachment materials retain game-specific preview evidence", NativeAttachmentMaterialsRetainPreviewEvidence)
    ];

    private static void NativeAttachmentMaterialsRetainPreviewEvidence()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var sources = new[]
        {
            Source(MorphFaceGame.LE1, "BIOG_HMM_HIR_PRO_R.pcc"),
            Source(MorphFaceGame.LE2, "BIOG_HMM_HIR_PRO_R.pcc"),
            Source(MorphFaceGame.LE3, "BIOG_HMM_HIR_PRO_R.pcc"),
            Source(MorphFaceGame.LE1, "BIOA_CRD00_00_DSG.pcc"),
            Source(MorphFaceGame.LE2, "BioA_N7Spdr2_100.pcc")
        };
        if (sources.Any(value => value is null)) throw new TestSkippedException("Required native LE attachment packages are not installed.");

        using var cache = new PackageCache { CacheMaxSize = 16 };
        var resolver = new GamePackageReferenceResolver(cache);
        var reader = new MorphFaceMaterialReader(cache, resolver);

        ResolvedHeadMaterial Read(MorphFaceGame game, string file, string path, string className)
        {
            var packagePath = sources.Single(value => value!.Game == game && value.FileName.Equals(file, StringComparison.OrdinalIgnoreCase))!.Path;
            var package = PackageSourceCache.GetPackage(cache, packagePath)
                ?? throw new InvalidDataException($"Could not load installed package '{packagePath}'.");
            var export = package.FindExport(path, className)
                ?? throw new InvalidDataException($"Native test export '{path}' ({className}) was not found in '{packagePath}'.");
            var result = reader.Read(export, [], [export], applyFaceOverrides: false, decodeTextures: true);
            TestAssert.Equal(1, result.Materials.Materials.Count);
            return result.Materials.Materials.Values.Single();
        }

        static void AssertAttachment(ResolvedHeadMaterial material, HumanMaterialPreviewModel model, HeadMaterialBlendMode blend)
        {
            TestAssert.Equal(model, material.PreviewModel);
            TestAssert.Equal(HeadMaterialFamily.Accessory, material.Family);
            TestAssert.Equal(blend, material.BlendMode);
            TestAssert.True(!material.TwoSided, $"{material.Source.InstancedPath} unexpectedly became two-sided.");
            TestAssert.True(material.IsPreviewOnlyAttachment, $"{material.Source.InstancedPath} was not marked preview-only.");
            TestAssert.True(!material.ContributesFaceControls, $"{material.Source.InstancedPath} contributes face controls.");
        }

        static void AssertColorSpace(ResolvedHeadMaterial material, string name, TextureColorSpace colorSpace)
        {
            TestAssert.True(material.DefaultTextures.TryGetValue(name, out var binding),
                $"'{material.Source.InstancedPath}' did not retain preview texture '{name}'.");
            TestAssert.Equal(colorSpace, binding!.Texture.ColorSpace);
        }

        // Each game's shader is read from the MIC or material used by a native hat slot.
        var le1Hat = Read(MorphFaceGame.LE1, "BIOG_HMM_HIR_PRO_R.pcc", "Cap.HMM_HAT_Cap_MAT_1a", "MaterialInstanceConstant");
        AssertAttachment(le1Hat, HumanMaterialPreviewModel.Hat, HeadMaterialBlendMode.Opaque);
        AssertColorSpace(le1Hat, "HMN_HAT_Diff", TextureColorSpace.Linear);

        var le2Hat = Read(MorphFaceGame.LE2, "BIOG_HMM_HIR_PRO_R.pcc", "Visor.HMM_HAT_Vsr_MAT_1a_test", "MaterialInstanceConstant");
        AssertAttachment(le2Hat, HumanMaterialPreviewModel.Hat, HeadMaterialBlendMode.Opaque);
        AssertColorSpace(le2Hat, "HMN_HAT_Diff", TextureColorSpace.Linear);

        var le3Hat = Read(MorphFaceGame.LE3, "BIOG_HMM_HIR_PRO_R.pcc", "Hat_Beanie01.HMM_HAT_Bn1_MAT_1a", "MaterialInstanceConstant");
        AssertAttachment(le3Hat, HumanMaterialPreviewModel.Hat, HeadMaterialBlendMode.Opaque);
        AssertColorSpace(le3Hat, "HMN_HAT_Diff", TextureColorSpace.Linear);
        TestAssert.True(le1Hat.FixedCubeTexture is not null && le2Hat.FixedCubeTexture is not null && le3Hat.FixedCubeTexture is null,
            "Native HMN hat cube presence disagrees with the game-specific shader maps.");

        var le1CapDecal = Read(MorphFaceGame.LE1, "BIOG_HMM_HIR_PRO_R.pcc", "Cap.HMM_HAT_Cap_Mat_1b", "MaterialInstanceConstant");
        AssertAttachment(le1CapDecal, HumanMaterialPreviewModel.CapDecal, HeadMaterialBlendMode.Masked);
        AssertColorSpace(le1CapDecal, "Cap_Diff", TextureColorSpace.Linear);

        var le2CapDecal = Read(MorphFaceGame.LE2, "BIOG_HMM_HIR_PRO_R.pcc", "Cap.HMM_HAT_Cap_Mat_1b", "MaterialInstanceConstant");
        AssertAttachment(le2CapDecal, HumanMaterialPreviewModel.CapDecal, HeadMaterialBlendMode.Masked);
        AssertColorSpace(le2CapDecal, "Cap_Diff", TextureColorSpace.Linear);

        var le2CapMaterial = Read(MorphFaceGame.LE2, "BIOG_HMM_HIR_PRO_R.pcc", "Cap.HMM_HAT_Cap_MAT_2a", "Material");
        AssertAttachment(le2CapMaterial, HumanMaterialPreviewModel.CapLe2, HeadMaterialBlendMode.Opaque);
        AssertColorSpace(le2CapMaterial, "__CapCrewDiffuse", TextureColorSpace.Srgb);
        AssertColorSpace(le2CapMaterial, "HMN_HAT_Diff", TextureColorSpace.Linear);
        TestAssert.Equal("BIOG_HMM_HIR_PRO_R.Cap.HMM_HGR_CAP_CrewCerberus_Diff",
            Canonical(le2CapMaterial.DefaultTextures["__CapCrewDiffuse"].Texture.Source));

        var le3CapDecal = Read(MorphFaceGame.LE3, "BIOG_HMM_HIR_PRO_R.pcc", "Hat_Cap.HMM_HAT_Cap_Mat_1b", "MaterialInstanceConstant");
        AssertAttachment(le3CapDecal, HumanMaterialPreviewModel.CapDecal, HeadMaterialBlendMode.Masked);
        TestAssert.Near(0.745919f, le3CapDecal.Vectors["None"].X, 0.00001f);
        TestAssert.Near(0.5041125f, le3CapDecal.Vectors["None"].Y, 0.00001f);
        TestAssert.Near(0.004267827f, le3CapDecal.Vectors["None"].Z, 0.000001f);
        TestAssert.Near(5f, le3CapDecal.Scalars["Phong_Spwr"], 0.00001f);
        TestAssert.Near(0.464848101f, le3CapDecal.Vectors["Phong_Spec_Color"].X, 0.00001f);
        TestAssert.Near(0.232066512f, le3CapDecal.Vectors["Phong_Spec_Color"].Y, 0.00001f);

        var le3Anderson = Read(MorphFaceGame.LE3, "BIOG_HMM_HIR_PRO_R.pcc",
            "BIOG_HMM_ARM_AND_R.Master.Andersonb_MASTER_MAT_USER", "RvrEffectsMaterialUser");
        AssertAttachment(le3Anderson, HumanMaterialPreviewModel.AndersonHat, HeadMaterialBlendMode.Opaque);
        AssertColorSpace(le3Anderson, "Diffuse_Map", TextureColorSpace.Srgb);

        var le3Moto = Read(MorphFaceGame.LE3, "BIOG_HMM_HIR_PRO_R.pcc",
            "Hat_Moto.HMM_HAT_Mtr_MASTER", "Material");
        AssertAttachment(le3Moto, HumanMaterialPreviewModel.MotoHat, HeadMaterialBlendMode.Opaque);
        AssertColorSpace(le3Moto, "Diffuse_Map", TextureColorSpace.Srgb);
        TestAssert.True(le3Moto.FixedCubeTexture is not null, "Moto's compiled Chrome cube was not materialized.");
        TestAssert.Equal("BIOG_Humanoid_MASTER_MTR_R.CubeMap.Cube_Chrome",
            Canonical(le3Moto.FixedCubeTexture!.Source));

        var le1Visor = Read(MorphFaceGame.LE1, "BIOA_CRD00_00_DSG.pcc",
            "BIOG_HMF_HGR_HVY_R.HVYa.HMF_VSR_HVYa_MAT_1a", "MaterialInstanceConstant");
        AssertAttachment(le1Visor, HumanMaterialPreviewModel.Visor, HeadMaterialBlendMode.Translucent);
        AssertColorSpace(le1Visor, "HMM_VSR_Norm", TextureColorSpace.Linear);
        TestAssert.True(le1Visor.FixedCubeTexture is not null, "LE1 VSR's named cube override was not materialized.");
        TestAssert.Equal("BIOG_HMM_HGR_HVY_R.HMM_HGR_HVYa_CUBE", Canonical(le1Visor.FixedCubeTexture!.Source));

        var le2Visor = Read(MorphFaceGame.LE2, "BioA_N7Spdr2_100.pcc",
            "BIOG_HMM_HGR_HVY_R.HVYa.HMM_VSR_HVYa_MAT_1a", "MaterialInstanceConstant");
        AssertAttachment(le2Visor, HumanMaterialPreviewModel.Visor, HeadMaterialBlendMode.Translucent);
        AssertColorSpace(le2Visor, "HMM_VSR_Norm", TextureColorSpace.Linear);
        TestAssert.True(le2Visor.FixedCubeTexture is not null, "LE2 VSR's named cube override was not materialized.");
        TestAssert.Equal("BIOG_HMM_HGR_HVY_R.HMM_HGR_HVYa_CUBE", Canonical(le2Visor.FixedCubeTexture!.Source));
    }

    private static string Canonical(AssetIdentity source) =>
        PccAssetPathPolicy.FromDonorOccurrence(source.InstancedPath, source.PackagePath);

    private sealed record PackageSource(MorphFaceGame Game, string FileName, string Path);

    private static PackageSource? Source(MorphFaceGame game, string fileName)
    {
        var path = ResolvePackage(game, fileName);
        return path is null ? null : new PackageSource(game, fileName, path);
    }

    private static string? ResolvePackage(MorphFaceGame game, string fileName)
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
