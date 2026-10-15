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

    internal static MorphFaceMaterialData ReadMaterialData(ExportEntry materialOverride, PropertyCollection? properties = null)
    {
        properties ??= materialOverride.GetProperties();
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

    internal static void VerifyMorphData(MorphFaceMorphData actual, MorphFaceMorphData expected)
    {
        if (actual.MorphFeatures.Count != expected.MorphFeatures.Count ||
            actual.FinalSkeleton.Count != expected.FinalSkeleton.Count ||
            actual.BakedLods.Count != expected.BakedLods.Count)
            throw Mismatch("Morph data counts changed.");
        for (var index = 0; index < expected.MorphFeatures.Count; index++)
        {
            var left = actual.MorphFeatures[index];
            var right = expected.MorphFeatures[index];
            if (!SameName(left.Name, right.Name) || !SameNumber(left.Offset, right.Offset))
                throw Mismatch($"Morph feature {index} changed.");
        }
        for (var index = 0; index < expected.FinalSkeleton.Count; index++)
        {
            var left = actual.FinalSkeleton[index];
            var right = expected.FinalSkeleton[index];
            if (!SameName(left.BoneName, right.BoneName) || !SameVector(left.Translation, right.Translation))
                throw Mismatch($"Final skeleton bone {index} changed.");
        }
        for (var lod = 0; lod < expected.BakedLods.Count; lod++)
        {
            if (actual.BakedLods[lod].Length != expected.BakedLods[lod].Length)
                throw Mismatch($"Baked LOD {lod} changed size.");
            for (var vertex = 0; vertex < expected.BakedLods[lod].Length; vertex++)
                if (!SameVector(actual.BakedLods[lod][vertex], expected.BakedLods[lod][vertex]))
                    throw Mismatch($"Baked LOD {lod} vertex {vertex} changed.");
        }
    }

    internal static void VerifyMaterialData(MorphFaceMaterialData actual, MorphFaceMaterialData expected)
    {
        VerifyParameters(actual.Scalars, expected.Scalars, "m_aScalarOverrides",
            (left, right) => SameName(left.Name, right.Name) && SameNumber(left.Value, right.Value));
        VerifyParameters(actual.Vectors, expected.Vectors, "m_aColorOverrides",
            (left, right) => SameName(left.Name, right.Name) && SameVector(left.Value, right.Value));
        VerifyParameters(actual.Textures, expected.Textures, "m_aTextureOverrides",
            (left, right) => SameName(left.Name, right.Name) &&
                SameName(left.TextureReference?.InstancedPath, right.TextureReference?.InstancedPath) &&
                SameName(left.TextureReference?.ClassName, right.TextureReference?.ClassName));
    }

    internal static void VerifyWrittenValueFields(PropertyCollection face, PropertyCollection materialOverride)
    {
        // Raw capture may default absent fields to zero. A writer must actually
        // store the feature/scalar values it was asked to write, including zero.
        if (face.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")?
                .Any(row => row.GetProp<FloatProperty>("Offset") is null) == true ||
            materialOverride.GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides")?
                .Any(row => row.GetProp<FloatProperty>("sValue") is null) == true)
            throw Mismatch("A written feature or scalar value is missing.");
    }

    private static void VerifyParameters<T>(
        IReadOnlyList<T> actual,
        IReadOnlyList<T> expected,
        string field,
        Func<T, T, bool> same)
    {
        if (actual.Count != expected.Count) throw Mismatch($"{field} changed count.");
        // Parameter order has no meaning, but duplicate authored rows still count.
        var remaining = actual.ToList();
        foreach (var value in expected)
        {
            var index = remaining.FindIndex(candidate => same(candidate, value));
            if (index < 0) throw Mismatch($"{field} changed a parameter.");
            remaining.RemoveAt(index);
        }
    }

    internal static void VerifyUnchangedNonFiniteValues(MorphFaceDocument draft, ExportEntry sourceFace)
    {
        var requested = NonFiniteValues(
            new MorphFaceMorphData(draft.MorphFeatures, draft.FinalSkeleton, draft.BakedLods),
            new MorphFaceMaterialData(draft.MaterialOverrides.Scalars, draft.MaterialOverrides.Vectors,
                draft.MaterialOverrides.Textures)).ToArray();
        if (requested.Length == 0) return;

        var material = sourceFace.GetProperty<ObjectProperty>("m_oMaterialOverrides")?
            .ResolveToEntry(sourceFace.FileRef) as ExportEntry
            ?? throw new InvalidDataException($"BioMorphFace '{sourceFace.InstancedFullPath}' has no local BioMaterialOverride.");
        var original = NonFiniteValues(ReadMorphData(sourceFace), ReadMaterialData(material)).ToList();
        foreach (var value in requested)
        {
            var index = original.FindIndex(candidate => SameName(candidate.Field, value.Field) &&
                                                       candidate.Bits == value.Bits);
            if (index < 0)
                throw UserFacingMessage.CreateError("loc_PccSave_NewNonFiniteValue",
                    $"New or changed non-finite authored field: {value.Field}.", value.Field);
            original.RemoveAt(index);
        }
    }

    private static IEnumerable<(string Field, int Bits)> NonFiniteValues(
        MorphFaceMorphData morph,
        MorphFaceMaterialData material)
    {
        foreach (var feature in morph.MorphFeatures)
            if (!float.IsFinite(feature.Offset))
                yield return ($"m_aMorphFeatures.{feature.Name}.Offset", BitConverter.SingleToInt32Bits(feature.Offset));
        foreach (var bone in morph.FinalSkeleton)
            if (!IsFinite(bone.Translation))
                foreach (var value in NonFiniteComponents($"m_aFinalSkeleton.{bone.BoneName}.vPos",
                             bone.Translation.X, bone.Translation.Y, bone.Translation.Z))
                    yield return value;
        for (var lod = 0; lod < morph.BakedLods.Count; lod++)
            for (var vertex = 0; vertex < morph.BakedLods[lod].Length; vertex++)
            {
                var position = morph.BakedLods[lod][vertex];
                if (!IsFinite(position))
                    foreach (var value in NonFiniteComponents($"LODs[{lod}][{vertex}]", position.X, position.Y, position.Z))
                        yield return value;
            }
        foreach (var scalar in material.Scalars)
            if (!float.IsFinite(scalar.Value))
                yield return ($"m_aScalarOverrides.{scalar.Name}.sValue", BitConverter.SingleToInt32Bits(scalar.Value));
        foreach (var vector in material.Vectors)
            if (!IsFinite(new Vector3(vector.Value.X, vector.Value.Y, vector.Value.Z)) || !float.IsFinite(vector.Value.W))
                foreach (var value in NonFiniteComponents($"m_aColorOverrides.{vector.Name}.cValue",
                             vector.Value.X, vector.Value.Y, vector.Value.Z, vector.Value.W))
                    yield return value;
    }

    private static IEnumerable<(string Field, int Bits)> NonFiniteComponents(string field, params float[] components)
    {
        for (var index = 0; index < components.Length; index++)
            if (!float.IsFinite(components[index]))
                yield return ($"{field}[{index}]", BitConverter.SingleToInt32Bits(components[index]));
    }

    private static bool SameName(string? actual, string? expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    // These PCC fields are serialized directly as floats, without quantization.
    // NaN equality also needs its original sign/payload, not just float.Equals.
    private static bool SameNumber(float actual, float expected) =>
        float.IsFinite(actual) && float.IsFinite(expected)
            ? actual == expected
            : BitConverter.SingleToInt32Bits(actual) == BitConverter.SingleToInt32Bits(expected);

    private static bool SameVector(Vector3 actual, Vector3 expected) =>
        SameNumber(actual.X, expected.X) && SameNumber(actual.Y, expected.Y) && SameNumber(actual.Z, expected.Z);

    private static bool SameVector(Vector4 actual, Vector4 expected) =>
        SameNumber(actual.X, expected.X) && SameNumber(actual.Y, expected.Y) &&
        SameNumber(actual.Z, expected.Z) && SameNumber(actual.W, expected.W);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static InvalidDataException Mismatch(string diagnostic) =>
        UserFacingMessage.CreateError("loc_PccSave_VerificationMismatch", diagnostic);

    private static StructProperty VectorProperty(Vector3 value, string name) => new(
        "Vector",
        [new FloatProperty(value.X, "X"), new FloatProperty(value.Y, "Y"), new FloatProperty(value.Z, "Z")],
        name, true);

    private static StructProperty LinearColorProperty(Vector4 value, string name) => new(
        "LinearColor",
        [new FloatProperty(value.X, "R"), new FloatProperty(value.Y, "G"),
            new FloatProperty(value.Z, "B"), new FloatProperty(value.W, "A")],
        name, true);

    private static string Name(StructProperty property, string field) =>
        property.GetProp<NameProperty>(field)?.Value.Instanced ?? string.Empty;

    private static Vector3 ReadVector(StructProperty? property) =>
        property is null ? Vector3.Zero : CommonStructs.GetVector3(property);

    private static Vector4 ReadLinearColor(StructProperty? property) => property is null
        ? Vector4.Zero
        : new Vector4(
            property.GetProp<FloatProperty>("R")?.Value ?? 0,
            property.GetProp<FloatProperty>("G")?.Value ?? 0,
            property.GetProp<FloatProperty>("B")?.Value ?? 0,
            property.GetProp<FloatProperty>("A")?.Value ?? 0);
}
