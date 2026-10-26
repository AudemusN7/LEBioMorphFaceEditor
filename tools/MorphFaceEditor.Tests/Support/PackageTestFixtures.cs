using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

internal static class PackageTestFixtures
{

    internal static string FixturePath(string fileName) =>
        Path.GetFullPath(Path.Combine("fixtures", "Global Morphs", fileName));

    internal static IReadOnlyList<BioMorphFaceListItem> ReadFaces(string path) =>
        new MorphFaceCatalogService().ReadAsync(path).GetAwaiter().GetResult().Faces;

    internal static void AssertMorphEqual(MorphFaceMorphData expected, MorphFaceMorphData actual)
    {
        TestAssert.Equal(expected.MorphFeatures.Count, actual.MorphFeatures.Count);
        TestAssert.Equal(expected.FinalSkeleton.Count, actual.FinalSkeleton.Count);
        TestAssert.Equal(expected.BakedLods.Count, actual.BakedLods.Count);
        for (var index = 0; index < expected.MorphFeatures.Count; index++)
        {
            TestAssert.Equal(expected.MorphFeatures[index], actual.MorphFeatures[index]);
        }
        for (var index = 0; index < expected.FinalSkeleton.Count; index++)
        {
            TestAssert.Equal(expected.FinalSkeleton[index], actual.FinalSkeleton[index]);
        }
        for (var lod = 0; lod < expected.BakedLods.Count; lod++)
        {
            TestAssert.Equal(expected.BakedLods[lod].Length, actual.BakedLods[lod].Length);
            for (var vertex = 0; vertex < expected.BakedLods[lod].Length; vertex++)
            {
                TestAssert.Near(expected.BakedLods[lod][vertex], actual.BakedLods[lod][vertex], 0.000001f);
            }
        }
    }

    internal static void AssertMaterialEqual(MorphFaceMaterialData expected, MorphFaceMaterialData actual)
    {
        TestAssert.Equal(expected.Scalars.Count, actual.Scalars.Count);
        TestAssert.Equal(expected.Vectors.Count, actual.Vectors.Count);
        TestAssert.Equal(expected.Textures.Count, actual.Textures.Count);
        for (var index = 0; index < expected.Scalars.Count; index++)
        {
            TestAssert.Equal(expected.Scalars[index], actual.Scalars[index]);
        }
        for (var index = 0; index < expected.Vectors.Count; index++)
        {
            TestAssert.Equal(expected.Vectors[index], actual.Vectors[index]);
        }
        for (var index = 0; index < expected.Textures.Count; index++)
        {
            TestAssert.Equal(expected.Textures[index].Name, actual.Textures[index].Name);
            TestAssert.Equal(
                expected.Textures[index].TextureReference?.InstancedPath,
                actual.Textures[index].TextureReference?.InstancedPath);
        }
    }

    internal static void WithPackageCopy(string fileName, Action<string> action)
    {
        var source = FixturePath(fileName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Package fixture '{fileName}' was not found.", source);
        }
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"MFE-{Guid.NewGuid():N}-{fileName}");
        File.Copy(source, temporaryPath, overwrite: false);
        try
        {
            action(temporaryPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
