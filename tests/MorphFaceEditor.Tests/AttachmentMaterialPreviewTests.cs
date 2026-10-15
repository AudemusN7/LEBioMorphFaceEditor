using MorphFaceEditor.Core.Domain;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class AttachmentMaterialPreviewTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("attachment preview roots respect game and exact master name", CanonicalRoots),
        new("hat preview retains MIC values without exposing controls or face overrides", ReaderPreservesAttachmentValues)
    ];

    private static readonly (MEGame Game, string Path, HumanMaterialPreviewModel Model)[] Roots =
    [
        (MEGame.LE1, "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HAT_MASTER_MAT", HumanMaterialPreviewModel.Hat),
        (MEGame.LE2, "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HAT_MASTER_MAT", HumanMaterialPreviewModel.Hat),
        (MEGame.LE3, "BIOG_Humanoid_MASTER_MTR_R.Human.HMN_HAT_MASTER_MAT", HumanMaterialPreviewModel.Hat),
        (MEGame.LE1, "BIOG_Humanoid_MASTER_MTR_R.Human.HMF_VSR_MASTER_MAT", HumanMaterialPreviewModel.Visor),
        (MEGame.LE2, "BIOG_Humanoid_MASTER_MTR_R.Human.HMF_VSR_MASTER_MAT", HumanMaterialPreviewModel.Visor),
        (MEGame.LE1, "BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_Decal_MASTER_MAT", HumanMaterialPreviewModel.CapDecal),
        (MEGame.LE2, "BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_Decal_MASTER_MAT", HumanMaterialPreviewModel.CapDecal),
        (MEGame.LE3, "BIOG_HMM_HIR_PRO_R.Hat_Cap.HMM_HAT_Cap_Decal_MASTER_MAT", HumanMaterialPreviewModel.CapDecal),
        (MEGame.LE2, "BIOG_HMM_HIR_PRO_R.Cap.HMM_HAT_Cap_MAT_2a", HumanMaterialPreviewModel.CapLe2),
        (MEGame.LE3, "BIOG_HMM_ARM_AND_R.Master.Andersonb_MASTER_MAT", HumanMaterialPreviewModel.AndersonHat),
        (MEGame.LE3, "BIOG_HMM_HIR_PRO_R.Hat_Moto.HMM_HAT_Mtr_MASTER", HumanMaterialPreviewModel.MotoHat)
    ];

    private static void CanonicalRoots()
    {
        foreach (var root in Roots)
        {
            var game = Enum.Parse<MorphFaceGame>(root.Game.ToString());
            var masterName = root.Path.Split('.').Last();
            TestAssert.Equal(root.Model, HeadMaterialCatalog.Find(game, masterName)!.PreviewModel);
            TestAssert.True(HeadMaterialCatalog.Find(game, masterName + "_Copy") is null,
                "A similar material name selected an attachment shader.");
        }
        TestAssert.True(HeadMaterialCatalog.Find(MorphFaceGame.LE3, "HMF_VSR_MASTER_MAT") is null, "Unused LE3 visor was enabled.");
        TestAssert.True(HeadMaterialCatalog.Find(MorphFaceGame.LE1, "HMM_HAT_Cap_MAT_2a") is null, "LE2-only cap was enabled in LE1.");
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2 })
            TestAssert.Equal(HumanMaterialPreviewModel.VisorLens,
                HeadMaterialCatalog.Find(game, "HMM_HAT_VSR_MAT_Visor")!.PreviewModel);
        TestAssert.True(HeadMaterialCatalog.Find(MorphFaceGame.LE3, "HMM_HAT_VSR_MAT_Visor") is null,
            "An unreviewed LE3 lens graph was enabled.");
    }
    private static void ReaderPreservesAttachmentValues()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        foreach (var root in Roots)
        {
            using var source = MEPackageHandler.CreateMemoryEmptyPackage(
                Path.Combine(Path.GetTempPath(), "MFE-HatPreview-" + Guid.NewGuid().ToString("N") + ".pcc"), root.Game);
            var split = root.Path.LastIndexOf('.');
            var group = source.CreatePackageExport("Relocated.ByAMod." + root.Path[..split]);
            var isEffectsUser = root.Model == HumanMaterialPreviewModel.AndersonHat;
            var master = source.CreateExport(isEffectsUser ? "Andersonb_MASTER_MAT" : root.Path[(split + 1)..],
                "Material", group, indexed: false);
            var scalar = source.CreateExport("Scalar", "MaterialExpressionScalarParameter", master, indexed: false);
            scalar.WriteProperty(new NameProperty("CubeMap_Intensity_Scalar", "ParameterName"));
            scalar.WriteProperty(new FloatProperty(0.25f, "DefaultValue"));
            var vector = source.CreateExport("Tint", "MaterialExpressionVectorParameter", master, indexed: false);
            vector.WriteProperty(new NameProperty("HMN_HAT_Tint1_Vector", "ParameterName"));
            vector.WriteProperty(new StructProperty("LinearColor", new PropertyCollection
            {
                new FloatProperty(0.2f, "R"), new FloatProperty(0.3f, "G"),
                new FloatProperty(0.4f, "B"), new FloatProperty(1, "A")
            }, "DefaultValue", isImmutable: true));
            master.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(scalar), new ObjectProperty(vector)], "Expressions"));
            var materialBinary = Material.Create();
            var mapId = Guid.NewGuid();
            materialBinary.SM3MaterialResource.ID = mapId;
            master.WriteBinary(materialBinary);
            var shaderCache = ShaderCache.Create();
            var staticParameters = (StaticParameterSet)mapId;
            shaderCache.MaterialShaderMaps.Add(staticParameters, new MaterialShaderMap
            {
                ID = mapId, FriendlyName = master.ObjectNameString, StaticParameters = staticParameters,
                Shaders = [], MeshShaderMaps = [],
                UniformPixelVectorExpressions = [], UniformPixelScalarExpressions = [],
                Uniform2DTextureExpressions = [], UniformCubeTextureExpressions = [],
                UniformVertexVectorExpressions = [], UniformVertexScalarExpressions = []
            });
            source.CreateExport("SeekFreeShaderCache", "ShaderCache", indexed: false).WriteBinary(shaderCache);
            var parent = master;
            if (isEffectsUser)
            {
                parent = source.CreateExport("Renamed_Effects_User", "RvrEffectsMaterialUser", group, indexed: false);
                parent.WriteProperty(new ObjectProperty(master, "m_pBaseMaterial"));
            }
            var mic = source.CreateExport("Custom_MIC", "MaterialInstanceConstant", group, indexed: false);
            mic.WriteProperty(new ObjectProperty(parent, "Parent"));
            mic.WriteProperty(new ArrayProperty<StructProperty>(
                [new StructProperty("ScalarParameterValue", new PropertyCollection
                {
                    new NameProperty("CubeMap_Intensity_Scalar", "ParameterName"), new FloatProperty(0.7f, "ParameterValue")
                })], "ScalarParameterValues"));
            var face = source.CreateExport("Face", "BioMorphFace", group, indexed: false);
            source.Save(source.FilePath);
            using var cache = new PackageCache();
            var resolver = new InstalledGameAssetResolver(_ => new Dictionary<string, string>(), _ => null);
            var reader = new MorphFaceMaterialReader(cache, new GamePackageReferenceResolver(cache, resolver));
            var detached = reader.ReadFaceAttachmentEvidence(mic);
            TestAssert.Equal(root.Model, detached.PreviewModel);
            TestAssert.True(detached.IsPreviewOnlyAttachment && !detached.ContributesFaceControls,
                "A renamed detached attachment bypassed its preview shader or exposed controls.");
            var overrides = new MorphFaceMaterialOverrides(null,
                [new ScalarMaterialOverride("CubeMap_Intensity_Scalar", 9)],
                [new VectorMaterialOverride("HMN_HAT_Tint1_Vector", Vector4.One)], []);
            var read = reader.Read(face, [], [mic], decodeTextures: false);
            TestAssert.True(read.Warnings.Count == 0, string.Join("; ", read.Warnings));
            var materialSet = read.Materials;
            var material = materialSet.Materials.Values.Single();
            TestAssert.Equal(root.Model, material.PreviewModel);
            TestAssert.Equal(HeadMaterialFamily.Accessory, material.Family);
            TestAssert.True(material.IsPreviewOnlyAttachment && !material.ContributesFaceControls, "Attachment became editable.");
            TestAssert.Near(0.7f, material.Scalars["CubeMap_Intensity_Scalar"], 0);
            TestAssert.Near(0.2f, material.Vectors["HMN_HAT_Tint1_Vector"].X, 0);
            var session = new MaterialEditingSession(overrides, materialSet);
            TestAssert.Equal(0, session.ScalarNames.Count);
            TestAssert.Equal(0, session.VectorNames.Count);
            TestAssert.Equal(0, session.TextureParameters.Count);
            TestAssert.Near(0.7f, session.Materials.Materials[material.Key].Scalars["CubeMap_Intensity_Scalar"], 0);
            File.Delete(source.FilePath);
        }
    }
}
