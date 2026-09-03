using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.Tests;

public static class TestFixtures
{
    private static readonly Lazy<TextureCatalogService> CorpusTextureCatalog =
        new(BuildCorpusTextureCatalog);

    public static TextureCatalogService CreateMissingTextureCatalogService() => new(
        new TextureRegistryStore(new TextureRegistryPaths(Path.Combine(
            Path.GetTempPath(), "MFE-Tests-Missing-Texture-Registries"))));

    public static TextureCatalogService GetCorpusTextureCatalogService() => CorpusTextureCatalog.Value;

    public static SkeletalMeshAsset CreateMesh()
    {
        var topology = new SkeletalMeshTopology(
            0,
            3,
            3,
            1,
            [new MeshSectionTopology(0, 0, 0, 1)],
            [new MeshChunkTopology(0, 0, 3, 1, [0])],
            [new ReferenceBone("root", 0, Vector3.Zero, Quaternion.Identity)],
            [0],
            [0],
            new string('A', 64));
        return new SkeletalMeshAsset(
            CreateIdentity("BaseHead", "SkeletalMesh"),
            [Vector3.Zero, new Vector3(1, 2, 3), new Vector3(-1, -2, -3)],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            topology);
    }

    public static MorphTargetAsset CreateTarget(params MorphVertexDelta[] deltas) =>
        new(
            CreateIdentity("Target", "MorphTarget"),
            [new MorphTargetLod(0, 3, deltas)],
            []);

    public static SkeletalMeshAsset CreateRenderableTwoLodMesh(int lowerLodIndex = 1)
    {
        var skeleton = new[] { new ReferenceBone("root", 0, Vector3.Zero, Quaternion.Identity) };
        SkeletalMeshLod CreateLod(int index, Vector3[] positions)
        {
            var topology = new SkeletalMeshTopology(
                index, positions.Length, 3, 1,
                [new MeshSectionTopology(0, 0, 0, 1)],
                [new MeshChunkTopology(0, 0, positions.Length, 1, [0])],
                skeleton, [0], [0], new string((char)('A' + index), 64));
            var renderData = new SkeletalMeshRenderData(
                Enumerable.Repeat(new Vector4(1, 0, 0, 1), positions.Length).ToArray(),
                Enumerable.Repeat(Vector2.Zero, positions.Length).ToArray(),
                Enumerable.Repeat(new BoneIndex4(0, 0, 0, 0), positions.Length).ToArray(),
                Enumerable.Repeat(new Vector4(1, 0, 0, 0), positions.Length).ToArray(),
                [0, 1, 2],
                [null]);
            return new SkeletalMeshLod(
                index,
                positions,
                Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
                topology,
                renderData);
        }

        var lods = new[]
        {
            CreateLod(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY]),
            CreateLod(lowerLodIndex, [new Vector3(10, 0, 0), new Vector3(12, 0, 0), new Vector3(10, 2, 0)])
        };
        return new SkeletalMeshAsset(
            CreateIdentity("BaseHead", "SkeletalMesh"),
            lods[0].Positions,
            lods[0].Normals,
            lods[0].Topology,
            lods[0].RenderData,
            lods.Select(lod => lod.Positions).ToArray(),
            lods);
    }

    public static AssetIdentity CreateIdentity(string path, string className) =>
        new("fixture.pcc", path, 1, className);

    private static TextureCatalogService BuildCorpusTextureCatalog()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-Tests-Corpus-Registries-{Guid.NewGuid():N}");
        var store = new TextureRegistryStore(new TextureRegistryPaths(directory));
        var builder = new TextureRegistryBuilder(
            store,
            new LecTextureRegistryPackageScanner(),
            game => game switch
            {
                MorphFaceGame.LE1 => CorpusPackages("LE1", "LE2 to LE1", "LE3 to LE1"),
                MorphFaceGame.LE2 => CorpusPackages("LE2", "LE1 to LE2", "LE3 to LE2"),
                MorphFaceGame.LE3 => CorpusPackages("LE3", "LE1 to LE3", "LE2 to LE3"),
                _ => []
            });
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            var status = builder.RebuildAsync(game).GetAwaiter().GetResult();
            if (status.State != TextureRegistryState.Ready)
            {
                throw new InvalidDataException($"Test texture database build failed for {game}: {status.ErrorMessage}");
            }
        }
        return new TextureCatalogService(store);
    }

    private static string[] CorpusPackages(params string[] names) => names.Select(name =>
        Path.GetFullPath(Path.Combine("tests", "Global Morphs", $"{name} GlobalMorphs.pcc"))).ToArray();
}
