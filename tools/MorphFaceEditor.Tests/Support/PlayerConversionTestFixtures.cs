using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

internal static class PlayerConversionTestFixtures
{
    internal static MorphFaceEditor.Core.Deformation.PlayerMorphConversionBaseline RequireBaseline(
        PlayerMorphBaselineCatalog catalog, MorphFaceProfile profile) =>
        catalog.ForProfile(profile).SingleOrDefault() ?? throw new Exception(
            $"Generated Player baseline was not accepted for {profile.Key}.");

    internal static MorphFaceEditor.Core.Deformation.PlayerMorphConversionBaseline RequireCounterpart(
        PlayerMorphBaselineCatalog catalog, string id, MorphFaceProfile profile) =>
        catalog.FindCounterpart(id, profile) ?? throw new Exception(
            $"Generated Player baseline counterpart {id} was not accepted for {profile.Key}.");

    internal static SkeletalMeshAsset MeshWithFaceBones()
    {
        var mesh = TestFixtures.CreateRenderableTwoLodMesh();
        ReferenceBone[] bones =
        [
            new("root", 0, Vector3.Zero, Quaternion.Identity),
            new("eye", 0, new Vector3(1, 2, 3), Quaternion.Identity),
            new("cheek", 0, new Vector3(-1, 2, 3), Quaternion.Identity)
        ];
        return mesh with { Lods = mesh.Lods.Select(l => l with { Topology = l.Topology with { ReferenceSkeleton = bones } }).ToArray() };
    }

    internal static void RequireSeeds(params MorphFaceGame[] games)
    {
        foreach (var game in games.Distinct())
        {
            try { _ = StandalonePlayerMorphImportService.ResolveInstalledSeed(game); }
            catch (FileNotFoundException) { throw new TestSkippedException($"Installed {game} Player seed required."); }
        }
    }

    internal static TextureCatalogService Prerequisites(params MorphFaceGame[] games)
    {
        RequireSeeds(games);
        var textures = new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));
        foreach (var game in games.Distinct())
            if (!textures.ReadAsync(game).GetAwaiter().GetResult().IsAvailable)
                throw new TestSkippedException($"Installed {game} texture registry required.");
        return textures;
    }

    internal static MorphFaceConversionService CreateService(params MorphFaceGame[] games) =>
        new(MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog(),
            new MorphFacePackageContextService(), Prerequisites(games));

    internal static LoadedMorphFace LoadSeed(MorphFacePackageReader reader, MorphFaceGame game, StandalonePlayerSex sex) =>
        reader.Load(StandalonePlayerMorphImportService.ResolveInstalledSeed(game),
            StandalonePlayerMorphImportService.ResolvePlayerTemplate(game, sex), decodeTextures: false);
}
