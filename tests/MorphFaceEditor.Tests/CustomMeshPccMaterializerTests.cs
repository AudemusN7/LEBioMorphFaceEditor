using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class CustomMeshPccMaterializerTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("C4 PCC export rejects Unreal SelectionColor parameters", SelectionColorIsNotSerializable),
        new("C4 LE1 authored HMF lash preserves the stock import identity", Le1AuthoredHmfLashPreservesImport),
        new("C4 LE2 human PCC uses BIOG texture identities without VFX graph", Le2HumanPccUsesBiogTfcTextures),
        new("C4 LE2 human lash PCC avoids duplicate-name texture ambiguity", Le2HumanLashUsesExactTextureIdentity),
        new("C4 LE2 Salarian PCC follows GlobalMorphs dependency roles", Le2SalarianPccFollowsOracleRoles),
        new("C4 LE3 Batarian PCC excludes its gameplay physical-material graph", Le3BatarianPccExcludesPhysicalMaterial),
        new("C4 racial PCC export succeeds across LE1 LE2 and LE3", RacialPccExportSucceedsAcrossGames)
    ];

    private static void SelectionColorIsNotSerializable()
    {
        TestAssert.True(!CustomMeshPccMaterializer.IsSerializableMaterialParameter("SelectionColor"),
            "SelectionColor must never survive into an authored MIC parameter array.");
        TestAssert.True(!CustomMeshPccMaterializer.IsSerializableMaterialParameter(" selectioncolor "),
            "SelectionColor filtering must tolerate case and whitespace differences.");
        TestAssert.True(CustomMeshPccMaterializer.IsSerializableMaterialParameter("SkinTone"),
            "Authored material parameters must remain serializable.");
    }

    private static void Le1AuthoredHmfLashPreservesImport()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(MorphFaceGame.LE1).State != TextureRegistryState.Ready)
        {
            return;
        }
        const string lashPath = "BIOG_Humanoid_MASTER_MTR_R.Human.HMF_HED_PROLash_Opac_M01";
        var textureCatalog = store.Read(MorphFaceGame.LE1).Candidates;
        var candidate = textureCatalog.SingleOrDefault(value =>
            value.InstancedPath.Equals(lashPath, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            return;
        }
        var option = new CustomMaterialTemplateCatalogService(new MorphFacePackageReader(), store)
            .Load(MorphFaceGame.LE1)
            .Options
            .FirstOrDefault(value => value.Label == "Human Female/Asari Lashes");
        if (option is null)
        {
            return;
        }

        var occurrence = candidate.EffectiveOccurrence;
        var textureIdentity = new AssetIdentity(
            occurrence.PackagePath,
            lashPath,
            occurrence.ExportUIndex,
            "Texture2D");
        var materialData = new MorphFaceMaterialData(
            [],
            [],
            [new TextureMaterialOverride("HED_Lash_Diff", textureIdentity)]);
        var source = CreateTriangleMesh("c4-le1-hmf-lash.gltf");
        var workspace = new CustomMaterialWorkspace(source);
        workspace.Assign(0, option);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-C4-LE1-Lash-{Guid.NewGuid():N}.pcc");
        try
        {
            _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source,
                workspace,
                materialData,
                MorphFaceGame.LE1,
                output,
                "C4Le1LashFixture",
                new ImportedSkeletalMeshPackageWriter(),
                textureCatalog));

            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            TestAssert.True(
                PackageIntegrity.FindExactEntry(package, lashPath, "Texture2D") is ImportEntry,
                "The LE1 stock HMF lash identity must remain an import, matching GlobalMorphs.");
            TestAssert.True(
                package.FindExport(lashPath, "Texture2D") is null,
                "The LE1 stock HMF lash import was incorrectly duplicated as an export.");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static void Le2HumanPccUsesBiogTfcTextures()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(MorphFaceGame.LE2).State != TextureRegistryState.Ready)
        {
            return;
        }
        var textureCatalog = store.Read(MorphFaceGame.LE2).Candidates;

        var templates = new CustomMaterialTemplateCatalogService(
            new MorphFacePackageReader(),
            store).Load(MorphFaceGame.LE2);
        var option = templates.Options.FirstOrDefault(value =>
            value.AppearanceCompatibilityKey.Equals("human-female", StringComparison.OrdinalIgnoreCase) &&
            value.Family == HeadMaterialFamily.Skin);
        if (option is null)
        {
            return;
        }

        var source = new ImportedMeshAsset(
            "c4-human-fixture.gltf",
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(),
            Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), 3).ToArray(),
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [0, 1, 2],
            [new ImportedMeshSection(0, "Face", 0, 3)],
            [], null, null, [0, 1, 2]);
        var workspace = new CustomMaterialWorkspace(source);
        workspace.Assign(0, option);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-C4-LE2-Human-{Guid.NewGuid():N}.pcc");

        try
        {
            _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source,
                workspace,
                new MorphFaceMaterialData([], [], []),
                MorphFaceGame.LE2,
                output,
                "C4HumanFixture",
                new ImportedSkeletalMeshPackageWriter(),
                textureCatalog));

            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            var textures = package.Exports
                .Where(value => value.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            TestAssert.True(textures.Length > 0, "The human material graph exported no Texture2D dependencies.");
            TestAssert.True(textures.Any(value => new Texture2D(value).Mips.Any(mip =>
                    mip.storageType != StorageTypes.empty && !mip.IsPackageStored)),
                "Every texture mip was converted to package storage.");

            TestAssert.True(textures.All(value =>
                    value.InstancedFullPath.StartsWith("BIOG", StringComparison.OrdinalIgnoreCase)),
                "A human Texture2D was written at a character-creator/non-seekfree path instead of its BIOG identity.");
            TestAssert.True(package.Exports.All(value =>
                    !value.InstancedFullPath.Contains("CombinedEffect_", StringComparison.OrdinalIgnoreCase) &&
                    !value.ClassName.Equals("RvrMaterialMultiplexor", StringComparison.OrdinalIgnoreCase)),
                "The gameplay/VFX multiplexor graph was ported into a material-only PCC.");
            TestAssert.True(package.Exports.All(value =>
                    !value.ClassName.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase)),
                "Editor-only MaterialExpression exports were ported into a compiled material PCC.");

            const string stockMask = "Skin_HumanHED_SpecMulitplier_Mask";
            var mask = textures.SingleOrDefault(value =>
                value.ObjectNameString.Equals(stockMask, StringComparison.OrdinalIgnoreCase));
            TestAssert.True(mask is not null, "The LE2 compiled skin mask was not materialised.");
            TestAssert.True(new Texture2D(mask!).Mips.Any(mip =>
                    mip.storageType != StorageTypes.empty && mip.IsPackageStored),
                "The sole installed LE2PATCH skin-mask donor did not retain its package storage.");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static void Le2HumanLashUsesExactTextureIdentity()
    {
        WithInstalledMaterial(
            MorphFaceGame.LE2,
            option => option.Label == "Human Female/Asari Lashes",
            package =>
            {
                var textures = package.Exports
                    .Where(value => value.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                TestAssert.True(textures.Any(value =>
                        value.ObjectNameString.Contains("Lash", StringComparison.OrdinalIgnoreCase)),
                    "The authored HMF lash texture was not materialised.");
                TestAssert.True(textures.All(value =>
                        value.InstancedFullPath.StartsWith("BIOG", StringComparison.OrdinalIgnoreCase)),
                    "A lash texture was remapped to a non-BIOG identity.");
            });
    }

    private static void Le2SalarianPccFollowsOracleRoles()
    {
        WithInstalledMaterial(
            MorphFaceGame.LE2,
            option => option.Family == HeadMaterialFamily.SalarianSkin,
            package =>
            {
                TestAssert.True(package.Exports.Any(value =>
                        value.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase)),
                    "The Salarian render-chain RvrEffectsMaterialUser was not exported.");
                TestAssert.True(package.Exports.All(value =>
                        !value.InstancedFullPath.Contains("CombinedEffect_", StringComparison.OrdinalIgnoreCase) &&
                        !value.ClassName.Equals("RvrMaterialMultiplexor", StringComparison.OrdinalIgnoreCase)),
                    "The Salarian gameplay/VFX multiplexor graph was exported.");

                foreach (var export in package.Exports.Where(value =>
                             IsOracleDependencyClass(value.ClassName) &&
                             !value.InstancedFullPath.StartsWith("MorphFaceEditor.", StringComparison.OrdinalIgnoreCase)))
                {
                    TestAssert.Equal(
                        MaterialOracleEntryKind.Export,
                        MaterialDependencyOracle.Instance.GetKind(package.Game, export.ClassName, export.InstancedFullPath));
                }
                foreach (var import in package.Imports.Where(value => IsOracleDependencyClass(value.ClassName)))
                {
                    TestAssert.Equal(
                        MaterialOracleEntryKind.Import,
                        MaterialDependencyOracle.Instance.GetKind(package.Game, import.ClassName, import.InstancedFullPath));
                }
            });
    }

    private static void RacialPccExportSucceedsAcrossGames()
    {
        var cases = new[]
        {
            (MorphFaceGame.LE1, HeadMaterialFamily.TurianSkin),
            (MorphFaceGame.LE2, HeadMaterialFamily.SalarianEyes),
            (MorphFaceGame.LE3, HeadMaterialFamily.KroganSkin)
        };
        foreach (var (game, family) in cases)
        {
            WithInstalledMaterial(
                game,
                option => option.Family == family,
                package =>
                {
                    TestAssert.True(package.Exports.Any(value =>
                            value.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) ||
                            value.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase)),
                        $"{game} {family} exported no render-chain material.");
                    TestAssert.True(package.Exports.All(value =>
                            !value.InstancedFullPath.Contains("CombinedEffect_", StringComparison.OrdinalIgnoreCase)),
                        $"{game} {family} exported a gameplay/VFX CombinedEffect graph.");
                });
        }
    }

    private static void Le3BatarianPccExcludesPhysicalMaterial()
    {
        WithInstalledMaterial(
            MorphFaceGame.LE3,
            option => option.Family == HeadMaterialFamily.BatarianSkin,
            package =>
            {
                TestAssert.True(
                    package.Exports.All(value =>
                        !value.ClassName.Equals("PhysicalMaterial", StringComparison.OrdinalIgnoreCase) &&
                        !value.ClassName.StartsWith("SFXPhysicalMaterial", StringComparison.OrdinalIgnoreCase)),
                    "The LE3 Batarian material pulled its gameplay physical-material graph into the authoring PCC.");
                var baseMaterial = package.Exports.Single(value =>
                    value.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase) &&
                    value.InstancedFullPath.EndsWith("BAT_HED_PRO_MASTER_MAT", StringComparison.OrdinalIgnoreCase));
                TestAssert.True(
                    baseMaterial.GetProperty<ObjectProperty>("PhysMaterial") is null,
                    "The copied LE3 Batarian material retained its gameplay PhysMaterial reference.");
            });
    }

    private static void WithInstalledMaterial(
        MorphFaceGame game,
        Func<CustomMaterialAssignmentOption, bool> selector,
        Action<IMEPackage> assertion)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(game).State != TextureRegistryState.Ready)
        {
            return;
        }
        var textureCatalog = store.Read(game).Candidates;
        var option = new CustomMaterialTemplateCatalogService(
                new MorphFacePackageReader(),
                store)
            .Load(game)
            .Options
            .FirstOrDefault(selector);
        if (option is null)
        {
            return;
        }

        var source = CreateTriangleMesh("c4-material-oracle-fixture.gltf");
        var workspace = new CustomMaterialWorkspace(source);
        workspace.Assign(0, option);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-C4-Material-{Guid.NewGuid():N}.pcc");
        try
        {
            _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source,
                workspace,
                new MorphFaceMaterialData([], [], []),
                game,
                output,
                "C4MaterialFixture",
                new ImportedSkeletalMeshPackageWriter(),
                textureCatalog));
            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            assertion(package);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static ImportedMeshAsset CreateTriangleMesh(string sourcePath) => new(
        sourcePath,
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(),
        Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), 3).ToArray(),
        [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
        [0, 1, 2],
        [new ImportedMeshSection(0, "Material", 0, 3)],
        [], null, null, [0, 1, 2]);

    private static bool IsOracleDependencyClass(string className) =>
        className.Equals("Material", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("TextureCube", StringComparison.OrdinalIgnoreCase);

}
