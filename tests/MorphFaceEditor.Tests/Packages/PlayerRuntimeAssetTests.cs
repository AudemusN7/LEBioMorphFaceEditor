using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class PlayerRuntimeAssetTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player runtime sources require exact mesh and texture identities and accept safe startup exports", ExactSources),
    ];

    private static void ExactSources()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-RuntimeSources-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var (game, meGame) in new[] { (MorphFaceGame.LE1, MEGame.LE1), (MorphFaceGame.LE2, MEGame.LE2), (MorphFaceGame.LE3, MEGame.LE3) })
            {
                var seekfree = Path.Combine(directory, "BIOG_MFE_HIR.pcc");
                using (var package = MEPackageHandler.CreateMemoryEmptyPackage(seekfree, meGame))
                {
                    package.CreateExport("Hair", "SkeletalMesh", indexed: false);
                    package.CreateExport("Accessory", "SkeletalMesh", indexed: false);
                    package.CreateExport("Face", "Texture2D", indexed: false);
                    package.Save(seekfree);
                }
                var startup = Path.Combine(directory, "Engine.pcc");
                using (var package = MEPackageHandler.CreateMemoryEmptyPackage(startup, meGame))
                {
                    var root = package.CreatePackageExport("BIOG_Global");
                    var texture = package.CreateExport("Black", "Texture2D", root, indexed: false);
                    TestAssert.True(texture.IsForcedExport, "The startup fixture is not a forced global export.");
                    package.Save(startup);
                }
                var factory = Path.Combine(directory, "BIOG_MORPH_FACE.pcc");
                using (var package = MEPackageHandler.CreateMemoryEmptyPackage(factory, meGame))
                {
                    var root = package.CreatePackageExport("BIOG_Global");
                    package.CreateExport("Eye", "Texture2D", root, indexed: false);
                    package.Save(factory);
                }
                var validator = new PlayerRuntimeAssetValidator(_ => [seekfree, startup, factory]);
                var materials = new MorphFaceMaterialData([], [], [new("HED_Diff", new("", "BIOG_MFE_HIR.Face", 0, "Texture2D")),
                    new("Global", new("", "BIOG_Global.Black", 0, "Texture2D")), new("EYE_Diff", new("", "BIOG_Global.Eye", 0, "Texture2D"))]);
                var head = new HeadMorphPayload("BIOG_MFE_HIR.Hair", ["BIOG_MFE_HIR.Accessory"], new([], [], []), materials);
                validator.Verify(game, head);
                var candidates = new[]
                {
                    new AssetIdentity("", "BIOG_MFE_HIR.Hair", 0, "SkeletalMesh"),
                    new AssetIdentity("", "BIOG_MFE_HIR.Face", 0, "Texture2D"),
                    new AssetIdentity("", "BIOG_Global.Black", 0, "Texture2D"),
                    new AssetIdentity("", "BIOG_Global.Eye", 0, "Texture2D"),
                    new AssetIdentity("", "BIOG_MFE_HIR.Face", 0, "SkeletalMesh"),
                    new AssetIdentity("", "BIOG_LevelOnly.Hair", 0, "SkeletalMesh")
                };
                var available = validator.FindAvailable(game, candidates);
                TestAssert.Equal(4, candidates.Count(available.Allows));

                new MorphFacePackageContextService(validator).VerifyPlayerRuntimeAssets(game,
                    new([], [], [new("HED_Diff", new(seekfree, "Face", 0, "Texture2D"))]));
                foreach (var invalid in new[]
                {
                    head with { HairMesh = "BIOG_MFE_HIR.Face" }, // matching path, wrong class
                    head with { AccessoryMeshes = ["BIOG_Other.Accessory"] }, // matching leaf, wrong runtime root
                    head with { MaterialData = materials with { Textures = [new("HED_Diff", new("", "BIOG_MFE_HIR.Hair", 0, "Texture2D"))] } }
                })
                {
                    try { validator.Verify(game, invalid); throw new Exception("An incompatible runtime reference was accepted."); }
                    catch (InvalidDataException exception)
                    { TestAssert.Equal("loc_PlayerAsset_SeekFreeSourceRequired", UserFacingMessage.FromException(exception)!.LocalizationKey); }
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
