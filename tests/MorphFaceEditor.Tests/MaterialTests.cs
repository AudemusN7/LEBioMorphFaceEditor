namespace MorphFaceEditor.Tests;

/// <summary>Aggregates focused material domains for the central suite runner.</summary>
public static class MaterialTests
{
    public static IReadOnlyList<TestCase> All { get; } =
        MaterialSchemaTests.All
            .Concat(MaterialEditingTests.All)
            .Concat(TextureRegistryStoreTests.All)
            .Concat(TextureCatalogTests.All)
            .Concat(MaterialRendererTests.All)
            .Concat(HumanSkinMaterialTests.All)
            .Concat(HairMaterialTests.All)
            .Concat(EyeMaterialTests.All)
            .Concat(AsariMaterialTests.All)
            .Concat(SalarianMaterialTests.All)
            .Concat(TurianMaterialTests.All)
            .Concat(BatarianMaterialTests.All)
            .Concat(KroganMaterialTests.All)
            .ToArray();
}

