using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using BinaryMorphFace = LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Persisted BioMorphFace and BioMaterialOverride fields. Reads retain authored
/// rows; edit projection and texture-resolution policy belong to the caller.
/// Writes replace only owned fields in the existing property collection.
/// </summary>
internal static class MorphFacePackageCodec
{
    internal static MorphFaceMorphData ReadMorphData(ExportEntry face, PropertyCollection? properties = null)
    {
        properties ??= face.GetProperties();
        return new MorphFaceMorphData(
            ReadMorphFeatures(properties),
            properties.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton")?
                .Select(item => new BoneTranslation(
                    Name(item, "nName"), ReadVector(item.GetProp<StructProperty>("vPos"))))
                .ToArray() ?? [],
            face.GetBinaryData<BinaryMorphFace>().LODs?
                .Select(lod => lod?.ToArray() ?? []).ToArray() ?? []);
    }

    internal static IReadOnlyList<MorphFeatureValue> ReadMorphFeatures(PropertyCollection properties) =>
        properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")?
            .Select(item => new MorphFeatureValue(
                Name(item, "sFeatureName"), item.GetProp<FloatProperty>("Offset")?.Value ?? 0f))
            .ToArray() ?? [];

    internal static MorphFaceMaterialData ReadMaterialData(ExportEntry materialOverride)
    {
        var properties = materialOverride.GetProperties();
        return new MorphFaceMaterialData(
            ReadScalars(properties),
            ReadVectors(properties),
            properties.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides")?
                .Select(item => new TextureMaterialOverride(
                    Name(item, "nName"),
                    MorphFacePackageReader.ToIdentity(item.GetProp<ObjectProperty>("m_pTexture")?
                        .ResolveToEntry(materialOverride.FileRef))))
                .ToArray() ?? []);
    }

    internal static IReadOnlyList<ScalarMaterialOverride> ReadScalars(PropertyCollection properties) =>
        properties.GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides")?
            .Select(item => new ScalarMaterialOverride(
                Name(item, "nName"), item.GetProp<FloatProperty>("sValue")?.Value ?? 0f))
            .ToArray() ?? [];

    private static IReadOnlyList<VectorMaterialOverride> ReadVectors(PropertyCollection properties) =>
        properties.GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides")?
            .Select(item => new VectorMaterialOverride(
                Name(item, "nName"), ReadLinearColor(item.GetProp<StructProperty>("cValue"))))
            .ToArray() ?? [];

    internal static void WriteMorphData(
        ExportEntry face,
        MorphFaceMorphData data,
        PropertyCollection? properties = null)
    {
        properties ??= face.GetProperties();
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.MorphFeatures.Select(value => new StructProperty(
                "MorphFeature", false,
                new NameProperty(value.Name, "sFeatureName"),
                new FloatProperty(value.Offset, "Offset"))),
            "m_aMorphFeatures"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.FinalSkeleton.Select(value => new StructProperty(
                "OffsetBonePos", false,
                new NameProperty(value.BoneName, "nName"),
                VectorProperty(value.Translation, "vPos"))),
            "m_aFinalSkeleton"));
        face.WritePropertiesAndBinary(properties, new BinaryMorphFace
        {
            LODs = data.BakedLods.Select(lod => lod.ToArray()).ToArray()
        });
    }

    internal static void WriteMaterialData(
        ExportEntry materialOverride,
        MorphFaceMaterialData data,
        Func<AssetIdentity?, IEntry?> resolveTexture)
    {
        var properties = materialOverride.GetProperties();
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.Scalars.Select(value => new StructProperty(
                "ScalarParameter", false,
                new NameProperty(value.Name, "nName"),
                new FloatProperty(value.Value, "sValue"))),
            "m_aScalarOverrides"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.Vectors.Select(value => new StructProperty(
                "ColorParameter", false,
                new NameProperty(value.Name, "nName"),
                LinearColorProperty(value.Value, "cValue"))),
            "m_aColorOverrides"));
        properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(
            data.Textures.Select(value => new StructProperty(
                "TextureParameter",
                [
                    new NameProperty(value.Name, "nName"),
                    new ObjectProperty(resolveTexture(value.TextureReference), "m_pTexture")
                ])),
            "m_aTextureOverrides"));
        materialOverride.WriteProperties(properties);
    }

    private static StructProperty VectorProperty(Vector3 value, string name) => new(
        "Vector",
        [new FloatProperty(value.X, "X"), new FloatProperty(value.Y, "Y"), new FloatProperty(value.Z, "Z")],
        name, true);

    private static StructProperty LinearColorProperty(Vector4 value, string name) => new(
        "LinearColor",
        [new FloatProperty(value.X, "R"), new FloatProperty(value.Y, "G"),
            new FloatProperty(value.Z, "B"), new FloatProperty(value.W, "A")],
        name, true);

    internal static string Name(StructProperty property, string field) =>
        property.GetProp<NameProperty>(field)?.Value.Instanced ?? string.Empty;

    internal static Vector3 ReadVector(StructProperty? property) =>
        property is null ? Vector3.Zero : CommonStructs.GetVector3(property);

    internal static Vector4 ReadLinearColor(StructProperty? property) => property is null
        ? Vector4.Zero
        : new Vector4(
            property.GetProp<FloatProperty>("R")?.Value ?? 0,
            property.GetProp<FloatProperty>("G")?.Value ?? 0,
            property.GetProp<FloatProperty>("B")?.Value ?? 0,
            property.GetProp<FloatProperty>("A")?.Value ?? 0);
}
