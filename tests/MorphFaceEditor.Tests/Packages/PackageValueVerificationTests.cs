using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class PackageValueVerificationTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("saved value checks retain morph bone vertex and LOD order while ignoring name case", MorphOrder),
        new("saved value checks match unordered BMO names values duplicate counts and texture identities", MaterialParameters),
        new("saved value checks distinguish missing written values from explicit zero", ExplicitZeroFields)
    ];

    private static readonly float AuthoredNaN = BitConverter.Int32BitsToSingle(0x7fc12345);

    private static void MorphOrder()
    {
        var expected = new MorphFaceMorphData(
            [new("Unknown", AuthoredNaN), new("Zero", 0)],
            [new("BoneA", new Vector3(float.PositiveInfinity, 0, 1)), new("BoneB", Vector3.One)],
            [[Vector3.Zero, Vector3.One], [new Vector3(AuthoredNaN, 2, 3), Vector3.UnitZ]]);
        MorphFacePackageCodec.VerifyMorphData(expected with
        {
            MorphFeatures = expected.MorphFeatures.Select(value => value with { Name = value.Name.ToUpperInvariant() }).ToArray(),
            FinalSkeleton = expected.FinalSkeleton.Select(value => value with { BoneName = value.BoneName.ToUpperInvariant() }).ToArray()
        }, expected);
        Reject(() => MorphFacePackageCodec.VerifyMorphData(expected with
        {
            MorphFeatures = expected.MorphFeatures.Reverse().ToArray()
        }, expected));
        Reject(() => MorphFacePackageCodec.VerifyMorphData(expected with
        {
            FinalSkeleton = expected.FinalSkeleton.Reverse().ToArray()
        }, expected));
        Reject(() => MorphFacePackageCodec.VerifyMorphData(expected with
        {
            BakedLods = expected.BakedLods.Reverse().ToArray()
        }, expected));
        Reject(() => MorphFacePackageCodec.VerifyMorphData(expected with
        {
            BakedLods = [expected.BakedLods[0].Reverse().ToArray(), expected.BakedLods[1]]
        }, expected));
        foreach (var changed in new[] { 0f, float.NaN, float.PositiveInfinity })
            Reject(() => MorphFacePackageCodec.VerifyMorphData(expected with
            {
                MorphFeatures = [new("Unknown", changed), expected.MorphFeatures[1]]
            }, expected));
        Reject(() => MorphFacePackageCodec.VerifyMorphData(expected with
        {
            MorphFeatures = [expected.MorphFeatures[0], new("Zero", 0.0000001f)]
        }, expected));
    }

    private static void MaterialParameters()
    {
        var texture = new AssetIdentity("source.pcc", "Group.Texture", 3, "Texture2D", true);
        var expected = new MorphFaceMaterialData(
            [new("A", 0), new("A", 1), new("B", AuthoredNaN)],
            [new("ColourA", new Vector4(float.NegativeInfinity, 0, 1, AuthoredNaN)), new("ColourB", Vector4.One)],
            [new("TexA", texture), new("TexB", null)]);
        var reordered = expected with
        {
            Scalars = expected.Scalars.Reverse().Select(value => value with { Name = value.Name.ToLowerInvariant() }).ToArray(),
            Vectors = expected.Vectors.Reverse().ToArray(),
            Textures = [new("texb", null), new("texa", texture with { PackagePath = "other.pcc", UIndex = 97, IsImport = false })]
        };
        MorphFacePackageCodec.VerifyMaterialData(reordered, expected);
        Reject(() => MorphFacePackageCodec.VerifyMaterialData(expected with
        {
            Scalars = [new("A", 0), new("A", 0), expected.Scalars[2]]
        }, expected));
        Reject(() => MorphFacePackageCodec.VerifyMaterialData(expected with
        {
            Vectors = [expected.Vectors[0] with { Value = Vector4.One }, expected.Vectors[1]]
        }, expected));
        foreach (var wrong in new[] { texture with { ClassName = "TextureCube" }, texture with { InstancedPath = "Other.Texture" } })
            Reject(() => MorphFacePackageCodec.VerifyMaterialData(expected with
            {
                Textures = [new("TexA", wrong), expected.Textures[1]]
            }, expected));
    }

    private static void ExplicitZeroFields()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("WrittenValues.pcc", MEGame.LE3);
        var face = package.CreateExport("Face", "BioMorphFace", indexed: false);
        var material = package.CreateExport("Overrides", "BioMaterialOverride", face, indexed: false);
        foreach (var missingFeature in new[] { true, false })
        {
            var feature = new StructProperty("MorphFeature", false, new NameProperty("Zero", "sFeatureName"));
            var scalar = new StructProperty("ScalarParameter", false, new NameProperty("Zero", "nName"));
            if (!missingFeature) feature.Properties.Add(new FloatProperty(0, "Offset"));
            if (missingFeature) scalar.Properties.Add(new FloatProperty(0, "sValue"));
            face.WriteProperty(new ArrayProperty<StructProperty>([feature], "m_aMorphFeatures"));
            material.WriteProperty(new ArrayProperty<StructProperty>([scalar], "m_aScalarOverrides"));
            Reject(() => MorphFacePackageCodec.VerifyWrittenValueFields(face.GetProperties(), material.GetProperties()));
            if (missingFeature) feature.Properties.Add(new FloatProperty(0, "Offset"));
            else scalar.Properties.Add(new FloatProperty(0, "sValue"));
            face.WriteProperty(new ArrayProperty<StructProperty>([feature], "m_aMorphFeatures"));
            material.WriteProperty(new ArrayProperty<StructProperty>([scalar], "m_aScalarOverrides"));
            MorphFacePackageCodec.VerifyWrittenValueFields(face.GetProperties(), material.GetProperties());
        }
    }

    private static void Reject(Action verify)
    {
        try { verify(); }
        catch (InvalidDataException exception)
        {
            TestAssert.Equal("loc_PccSave_VerificationMismatch", UserFacingMessage.FromException(exception)?.LocalizationKey);
            return;
        }
        throw new Exception("Changed authored PCC values were accepted.");
    }
}
