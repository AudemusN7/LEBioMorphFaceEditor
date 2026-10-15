using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class NativeHairMaterialSupportTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("Kaidan preview control capability is LE3-only and stays out of Custom evidence", KaidanControlCapability),
        new("CC scalp hides its unused alpha control while preserving authored data after reopen", CcScalpUnusedAlpha)
    ];

    private static void CcScalpUnusedAlpha()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "MFE-CcScalpCapability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "scalp.pcc");
            using (var source = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE3))
            {
                var group = source.CreatePackageExport("BIOG_Humanoid_MASTER_MTR_R.Human");
                var master = source.CreateExport("HMM_HED_PRO_MASTER_SCALP_MAT_CC", "Material", group, indexed: false);
                var expressions = new ArrayProperty<ObjectProperty>("Expressions");
                foreach (var (name, value) in new[]
                         { ("HAIR_Mask_Alpha_Scalar", 0.7f), ("HED_Scalp_Buzzcut_Alpha_Scalar", 0.4f) })
                {
                    var expression = source.CreateExport(name, "MaterialExpressionScalarParameter", master, indexed: false);
                    expression.WriteProperty(new NameProperty(name, "ParameterName"));
                    expression.WriteProperty(new FloatProperty(value, "DefaultValue"));
                    expressions.Add(new ObjectProperty(expression));
                }
                master.WriteProperty(expressions);
                var mic = source.CreateExport("Scalp_MIC", "MaterialInstanceConstant", group, indexed: false);
                mic.WriteProperty(new ObjectProperty(master, "Parent"));
                source.CreateExport("Face", "BioMorphFace", group, indexed: false);
                source.Save(path);
            }
            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            var savedAlpha = reopened.Exports.Single(export => export.ObjectNameString == "HAIR_Mask_Alpha_Scalar")
                .GetProperty<FloatProperty>("DefaultValue").Value;
            TestAssert.True(Math.Abs(savedAlpha - 0.7f) < 0.00001f, $"Saved expression alpha was {savedAlpha}.");
            using var cache = new PackageCache();
            var resolver = new InstalledGameAssetResolver(_ => new Dictionary<string, string>(), _ => null);
            var reader = new MorphFaceMaterialReader(cache, new GamePackageReferenceResolver(cache, resolver));
            var face = reopened.Exports.Single(export => export.ObjectNameString == "Face");
            var micEntry = reopened.Exports.Single(export => export.ObjectNameString == "Scalp_MIC");
            var materials = reader.Read(face, [], [micEntry], decodeTextures: false).Materials;
            var scalp = materials.Materials.Values.Single();
            TestAssert.Equal(HumanMaterialPreviewModel.CcScalp, scalp.PreviewModel);
            TestAssert.True(Math.Abs(scalp.Scalars["HAIR_Mask_Alpha_Scalar"] - 0.7f) < 0.00001f,
                $"Resolved expression alpha was {scalp.Scalars["HAIR_Mask_Alpha_Scalar"]}.");
            TestAssert.True(!scalp.SupportedScalars.Contains("HAIR_Mask_Alpha_Scalar"),
                "The unused CC alpha slot is still advertised as a working control.");
            var overrides = new MorphFaceMaterialOverrides(null, [new ScalarMaterialOverride("HAIR_Mask_Alpha_Scalar", 0.9f)], [], []);
            var session = new MaterialEditingSession(overrides, materials);
            TestAssert.True(!session.ScalarNames.Contains("HAIR_Mask_Alpha_Scalar"), "The unused control survived UI projection.");
            TestAssert.True(session.ScalarNames.Contains("HED_Scalp_Buzzcut_Alpha_Scalar"), "The working CC buzzcut control was hidden.");
            TestAssert.Equal(overrides.Scalars.Single(), session.CreateOverrides().Scalars.Single());
            TestAssert.Near(0.9f, session.CaptureInterchangeData().Scalars.Single(value => value.Name == "HAIR_Mask_Alpha_Scalar").Value, 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void KaidanControlCapability()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "MFE-KaidanCapability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var game in new[] { MEGame.LE1, MEGame.LE2, MEGame.LE3 })
            {
                var path = Path.Combine(directory, game + ".pcc");
                using var source = MEPackageHandler.CreateMemoryEmptyPackage(path, game);
                var group = source.CreatePackageExport("BIOG_HMM_HED_PROMorph.Kaiden.Master");
                var master = source.CreateExport("BIOG_HMM_HED_Kaiden_MASTER", "Material", group, indexed: false);
                var expression = source.CreateExport("SpecParameter", "MaterialExpressionScalarParameter", master, indexed: false);
                expression.WriteProperty(new NameProperty("HED_Spec_Scalar", "ParameterName"));
                expression.WriteProperty(new FloatProperty(1, "DefaultValue"));
                master.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(expression)], "Expressions"));
                var mic = source.CreateExport("Scalp_MIC", "MaterialInstanceConstant", group, indexed: false);
                mic.WriteProperty(new ObjectProperty(master, "Parent"));
                var face = source.CreateExport("Face", "BioMorphFace", group, indexed: false);
                source.Save(path);

                using var cache = new PackageCache();
                var resolver = new InstalledGameAssetResolver(_ => new Dictionary<string, string>(), _ => null);
                var reader = new MorphFaceMaterialReader(cache, new GamePackageReferenceResolver(cache, resolver));
                var native = reader.Read(face, [], [mic], decodeTextures: false).Materials.Materials.Values.Single();
                TestAssert.Equal(game == MEGame.LE3 ? HumanMaterialPreviewModel.Kaiden : HumanMaterialPreviewModel.Default,
                    native.PreviewModel);
                TestAssert.True(!native.ContributesFaceControls,
                    "A preview-only or unsupported-game master granted native face controls.");
                TestAssert.Near(1, native.Scalars["HED_Spec_Scalar"], 0);
                // Native control policy must not contaminate the shared metadata cache.
                var custom = reader.ReadMaterialEvidence(mic, decodeTextures: false);
                TestAssert.Equal(game == MEGame.LE3, custom.ContributesFaceControls);
                TestAssert.Equal(native.PreviewModel, custom.PreviewModel);
                var reloaded = reader.Read(face, [], [mic], decodeTextures: false).Materials.Materials.Values.Single();
                TestAssert.Equal(native.ContributesFaceControls, reloaded.ContributesFaceControls);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
