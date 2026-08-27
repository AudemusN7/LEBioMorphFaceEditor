using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using static MorphFaceEditor.Tests.MaterialTestFixtures;

namespace MorphFaceEditor.Tests;

// Owns the material schema regression set.
public static class MaterialSchemaTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE1 human material schema contains male and female masters", SchemaContainsRecoveredFamilies)
    ];

    private static void SchemaContainsRecoveredFamilies()
    {
        foreach (var family in new[]
                 {
                     HeadMaterialFamily.Skin,
                     HeadMaterialFamily.Scalp,
                     HeadMaterialFamily.Eyes,
                     HeadMaterialFamily.Lashes,
                     HeadMaterialFamily.Hair,
                     HeadMaterialFamily.AsariSkin,
                     HeadMaterialFamily.SalarianSkin,
                     HeadMaterialFamily.SalarianEyes,
                     HeadMaterialFamily.TurianSkin,
                     HeadMaterialFamily.TurianEyes,
                     HeadMaterialFamily.BatarianSkin,
                     HeadMaterialFamily.KroganSkin,
                     HeadMaterialFamily.KroganEyes
                 })
        {
            TestAssert.True(HumanMaterialProfiles.Definitions.Any(value => value.Family == family), $"No schema entries exist for {family}.");
        }
        TestAssert.Equal(HeadMaterialFamily.Eyes, HumanMaterialProfiles.ClassifyMaster("HMM_EYE_MASTER_OVRD_MAT"));
        TestAssert.Equal(HeadMaterialFamily.Eyes, HumanMaterialProfiles.ClassifyMaster("HMF_EYE_MASTER_OVRD_MAT"));
        TestAssert.Equal(HeadMaterialFamily.Skin, HumanMaterialProfiles.ClassifyMaster("HMF_HED_PRO_MASTER_FACE_MAT"));
        TestAssert.Equal(HeadMaterialFamily.Hair, HumanMaterialProfiles.ClassifyMaster("HMN_HED_PRO_MASTER_ADDN_HAIR_MAT"));
        TestAssert.Equal(HeadMaterialFamily.SalarianSkin, HumanMaterialProfiles.ClassifyMaster("SAL_HED_PRO_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.SalarianEyes, HumanMaterialProfiles.ClassifyMaster("SAL_HED_EYE_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.BatarianSkin, HumanMaterialProfiles.ClassifyMaster("BAT_HED_PRO_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.KroganSkin, HumanMaterialProfiles.ClassifyMaster("KRO_HED_PRO_MASTER_MAT"));
        TestAssert.Equal(HeadMaterialFamily.KroganEyes, HumanMaterialProfiles.ClassifyMaster("KRO_HED_EYE_MASTER_MAT"));
        TestAssert.Equal(TextureRole.Other, HumanMaterialProfiles.Describe(
            "HED_Makeup_Mask", MaterialParameterKind.Texture).TextureRole);
        var additionalHair = HumanMaterialProfiles.Describe("HAIR_ADDN_Diff", MaterialParameterKind.Texture);
        TestAssert.Equal(TextureRole.Diffuse, additionalHair.TextureRole);
        TestAssert.Equal(TextureColorSpace.Srgb, additionalHair.ColorSpace);
        TestAssert.Equal(HeadMaterialBlendMode.Translucent, HumanMaterialProfiles.BlendMode(HeadMaterialFamily.Hair));
    }
}

